using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Service;

public sealed class ProductServerPropertiesConflictException()
    : InvalidOperationException("server.properties changed after it was loaded; reload before saving.");

public sealed class ProductServerPropertiesConsistencyException(Exception updateError, Exception rollbackError)
    : IOException(
        "server.properties could not be restored after its related launch configuration update failed.",
        new AggregateException(updateError, rollbackError));

/// <summary>
/// Reads and writes only the server.properties file resolved from a Service registry identity.
/// Caller paths never cross this boundary, and the returned contract deliberately contains no
/// path or encoding metadata that could later be projected to a remote client.
/// </summary>
public sealed class ProductServerPropertiesManager
{
    private readonly ProductDataLayout _layout;
    private readonly ProductServerRegistry _registry;
    private readonly ServerPropertiesPortService _documents;
    private readonly ServerProcessManager _processes;
    private readonly Func<Guid, int, CoreType, bool, CancellationToken, Task<ProductServerRegistration>>
        _updateLaunchConfigurationAsync;

    public ProductServerPropertiesManager(
        ProductDataLayout layout,
        ProductServerRegistry registry,
        ServerPropertiesPortService documents,
        ServerProcessManager processes)
        : this(
            layout,
            registry,
            documents,
            processes,
            registry.UpdateLaunchConfigurationAsync)
    {
    }

    internal ProductServerPropertiesManager(
        ProductDataLayout layout,
        ProductServerRegistry registry,
        ServerPropertiesPortService documents,
        ServerProcessManager processes,
        Func<Guid, int, CoreType, bool, CancellationToken, Task<ProductServerRegistration>>
            updateLaunchConfigurationAsync)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _updateLaunchConfigurationAsync = updateLaunchConfigurationAsync ??
            throw new ArgumentNullException(nameof(updateLaunchConfigurationAsync));
    }

    public async Task<ProductServerPropertiesDocument> ReadAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
        => await _processes.ExecuteSerializedAsync(
                serverId,
                token => ReadSerializedAsync(serverId, token),
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<ProductServerPropertiesDocument> ReadSerializedAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        var target = ResolveTarget(serverId);
        using var ownershipLease = SafePath.AcquireNoReparseDirectoryChainLease(
            _layout.Servers,
            target.ServerRoot);
        return await ReadUnderLeaseAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProductServerPropertiesDocument> SaveAsync(
        Guid serverId,
        ProductServerPropertiesUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ProductServerPropertiesContract.IsValidText(request.Text) ||
            !ProductServerPropertiesContract.IsValidRevision(request.ExpectedRevisionSha256))
        {
            throw new ArgumentException("The bounded server.properties update is invalid.", nameof(request));
        }

        // Use the process manager's exact per-server lifecycle gate. This waits out an in-flight
        // start preparation and then rejects an active session, so the save cannot race the
        // PrepareStart port rewrite that consumes the same file.
        return await _processes.ExecuteWhileInactiveAsync(
                serverId,
                token => SaveWhileInactiveAsync(serverId, request, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ProductServerPropertiesDocument> SaveWhileInactiveAsync(
        Guid serverId,
        ProductServerPropertiesUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var target = ResolveTarget(serverId);
        using var ownershipLease = SafePath.AcquireNoReparseDirectoryChainLease(
            _layout.Servers,
            target.ServerRoot);
        var current = await ReadUnderLeaseAsync(target, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                current.RevisionSha256,
                request.ExpectedRevisionSha256,
                StringComparison.Ordinal))
        {
            throw new ProductServerPropertiesConflictException();
        }

        var currentDocument = current.Exists
            ? await _documents.ReadBoundedDocumentAsync(
                    target.PropertiesPath,
                    ProductServerPropertiesContract.MaximumSourceFileBytes,
                    cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (current.Exists &&
            (currentDocument is null ||
             !ProductServerPropertiesContract.IsValidText(currentDocument.Text) ||
             !string.Equals(
                 current.RevisionSha256,
                 ProductServerPropertiesContract.CalculateRevision(currentDocument.Text),
                 StringComparison.Ordinal)) ||
            !current.Exists && (currentDocument is not null || File.Exists(target.PropertiesPath)))
        {
            throw new ProductServerPropertiesConflictException();
        }

        var committedUpdate = await _documents.SaveBoundedDocumentAsync(
                target.PropertiesPath,
                request.Text,
                ProductServerPropertiesContract.MaximumSourceFileBytes,
                currentDocument?.FormatToken,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            _ = SafePath.EnsureNoReparsePointsUnderRoot(_layout.Servers, target.PropertiesPath);
            // Re-read the committed bytes. The response revision must describe exactly what is
            // durable on disk, including the preserved source encoding, never merely echo input.
            var saved = await ReadUnderLeaseAsync(target, cancellationToken).ConfigureAwait(false);
            if (!saved.Exists)
            {
                throw new IOException("The saved server.properties file could not be verified.");
            }

            if (target.CoreType != CoreType.Velocity &&
                ServerPropertiesPortEditor.TryReadServerPort(saved.Text, out var configuredPort) &&
                configuredPort != target.Registration.Port)
            {
                await _updateLaunchConfigurationAsync(
                        serverId,
                        configuredPort,
                        target.CoreType,
                        false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return saved;
        }
        catch (Exception updateError) when (updateError is not OutOfMemoryException)
        {
            try
            {
                await RestoreOriginalDocumentAsync(target, current, committedUpdate)
                    .ConfigureAwait(false);
            }
            catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException)
            {
                throw new ProductServerPropertiesConsistencyException(updateError, rollbackError);
            }

            throw;
        }
    }

    private async Task RestoreOriginalDocumentAsync(
        PropertiesTarget target,
        ProductServerPropertiesDocument original,
        ServerPropertiesDocumentUpdateResult committedUpdate)
    {
        if (!original.Exists)
        {
            if (Directory.Exists(target.PropertiesPath))
            {
                throw new InvalidDataException("The managed server.properties rollback target is not a regular file.");
            }
            if (File.Exists(target.PropertiesPath))
            {
                _ = SafePath.EnsureNoReparsePointsUnderRoot(_layout.Servers, target.PropertiesPath);
                File.Delete(target.PropertiesPath);
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(committedUpdate.BackupPath))
            {
                throw new InvalidOperationException("The original server.properties backup is unavailable.");
            }

            var rollbackSource = SafePath.EnsureWithinRoot(
                target.ServerRoot,
                committedUpdate.BackupPath,
                allowRoot: false);
            if (!string.Equals(
                    Path.GetDirectoryName(rollbackSource),
                    target.ServerRoot,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal) ||
                !Path.GetFileName(rollbackSource).StartsWith(
                    Path.GetFileName(target.PropertiesPath) + ".bak",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The server.properties rollback source is invalid.");
            }

            _ = SafePath.EnsureNoReparsePointsUnderRoot(_layout.Servers, rollbackSource);
            _ = SafePath.EnsureNoReparsePointsUnderRoot(_layout.Servers, target.PropertiesPath);
            File.Move(rollbackSource, target.PropertiesPath, overwrite: true);
        }

        var restored = await ReadUnderLeaseAsync(target, CancellationToken.None).ConfigureAwait(false);
        if (restored.Exists != original.Exists ||
            !string.Equals(restored.RevisionSha256, original.RevisionSha256, StringComparison.Ordinal))
        {
            throw new IOException("The original server.properties file could not be restored.");
        }
    }

    private async Task<ProductServerPropertiesDocument> ReadUnderLeaseAsync(
        PropertiesTarget target,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(target.PropertiesPath))
        {
            throw new InvalidDataException("The managed server.properties target is not a regular file.");
        }

        if (!File.Exists(target.PropertiesPath))
        {
            return new ProductServerPropertiesDocument(
                target.Registration.Id,
                false,
                string.Empty,
                ProductServerPropertiesContract.MissingRevision);
        }

        _ = SafePath.EnsureNoReparsePointsUnderRoot(_layout.Servers, target.PropertiesPath);
        var document = await _documents.ReadBoundedDocumentAsync(
                target.PropertiesPath,
                ProductServerPropertiesContract.MaximumSourceFileBytes,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ProductServerPropertiesConflictException();
        if (!ProductServerPropertiesContract.IsValidText(document.Text))
        {
            throw new InvalidDataException("The managed server.properties content is invalid or too large.");
        }

        return new ProductServerPropertiesDocument(
            target.Registration.Id,
            true,
            document.Text,
            ProductServerPropertiesContract.CalculateRevision(document.Text));
    }

    private PropertiesTarget ResolveTarget(Guid serverId)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("Server id must not be empty.", nameof(serverId));
        }
        if (!_registry.TryGet(serverId, out var registration))
        {
            throw new KeyNotFoundException("The selected server is not registered.");
        }
        if (!Enum.TryParse<CoreType>(registration.CoreType, ignoreCase: true, out var coreType) ||
            !Enum.IsDefined(coreType))
        {
            throw new InvalidDataException("The registered server core type is invalid.");
        }

        var serverRoot = ProductServerRegistrationValidator.ResolveOwnedPath(
            _layout.Servers,
            registration.ServerDirectory,
            allowRoot: false);
        if (!Directory.Exists(serverRoot))
        {
            throw new DirectoryNotFoundException("The Service-owned server directory was not found.");
        }
        serverRoot = SafePath.EnsureNoReparsePointsUnderRoot(_layout.Servers, serverRoot);
        var propertiesPath = SafePath.EnsureWithinRoot(
            serverRoot,
            "server.properties",
            allowRoot: false);
        return new PropertiesTarget(registration, coreType, serverRoot, propertiesPath);
    }

    private sealed record PropertiesTarget(
        ProductServerRegistration Registration,
        CoreType CoreType,
        string ServerRoot,
        string PropertiesPath);
}

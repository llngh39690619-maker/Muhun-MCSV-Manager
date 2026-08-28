using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>Atomic, per-user registry for client instances. Authentication tokens never enter it.</summary>
public sealed class MinecraftClientRegistry : IDisposable
{
    private readonly JsonSettingsStore<MinecraftClientRegistryDocument> _store;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private bool _disposed;

    public MinecraftClientRegistry(string registryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryPath);
        _store = new JsonSettingsStore<MinecraftClientRegistryDocument>(registryPath);
    }

    public async Task<MinecraftClientRegistryDocument> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task SaveAsync(
        MinecraftClientRegistryDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Performs one in-process read/modify/validate/atomic-save transaction. The callback is
    /// synchronous so user code cannot hold the registry lock across arbitrary asynchronous work.
    /// </summary>
    public async Task<TResult> UpdateAsync<TResult>(
        Func<MinecraftClientRegistryDocument, TResult> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var result = update(document);
            cancellationToken.ThrowIfCancellationRequested();
            await SaveCoreAsync(document, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.Dispose();
        _mutationGate.Dispose();
    }

    private async Task<MinecraftClientRegistryDocument> LoadCoreAsync(
        CancellationToken cancellationToken)
    {
        var document = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? new MinecraftClientRegistryDocument();
        Validate(document);
        return document;
    }

    private Task SaveCoreAsync(
        MinecraftClientRegistryDocument document,
        CancellationToken cancellationToken)
    {
        Validate(document);
        document.SchemaVersion = MinecraftClientRegistryDocument.CurrentSchemaVersion;
        return _store.SaveAsync(document, cancellationToken);
    }

    internal static void Validate(MinecraftClientRegistryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion is < 1 or > MinecraftClientRegistryDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Minecraft client registry schema {document.SchemaVersion}.");
        }

        if (document.Instances.Count > 1_024)
        {
            throw new InvalidDataException("Minecraft client registry contains too many instances.");
        }

        var ids = new HashSet<Guid>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeProcesses = new HashSet<(int ProcessId, long StartedAtUtcTicks)>();
        foreach (var instance in document.Instances)
        {
            if (instance.Id == Guid.Empty || !ids.Add(instance.Id))
            {
                throw new InvalidDataException("Minecraft client registry contains an invalid or duplicate id.");
            }

            if (string.IsNullOrWhiteSpace(instance.Name) || instance.Name.Length > 128)
            {
                throw new InvalidDataException("Minecraft client instance name is invalid.");
            }

            if (string.IsNullOrWhiteSpace(instance.DirectoryPath) ||
                !Path.IsPathFullyQualified(instance.DirectoryPath))
            {
                throw new InvalidDataException("Minecraft client instance directory is invalid.");
            }

            var fullDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(instance.DirectoryPath));
            if (!directories.Add(fullDirectory))
            {
                throw new InvalidDataException("Minecraft client registry contains duplicate directories.");
            }

            if (instance.Edition == MinecraftClientEdition.Java &&
                (string.IsNullOrWhiteSpace(instance.GameVersion) ||
                 string.IsNullOrWhiteSpace(instance.InstalledVersionId)))
            {
                throw new InvalidDataException("Java client instance is missing its release or installed profile id.");
            }

            if (instance.MinimumMemoryMb is < 512 or > 262_144 ||
                instance.MaximumMemoryMb < instance.MinimumMemoryMb ||
                instance.MaximumMemoryMb > 262_144 ||
                instance.JavaMajorVersion is < 8 or > 99)
            {
                throw new InvalidDataException("Minecraft client memory range or Java major is invalid.");
            }

            if (instance.WindowWidth is < 640 or > 16_384 ||
                instance.WindowHeight is < 360 or > 16_384)
            {
                throw new InvalidDataException("Minecraft client resolution is invalid.");
            }

            if (instance.AccountId?.Length > 256 || instance.JvmArguments.Count > 256 ||
                instance.EnvironmentVariables.Count > 128 || instance.CatalogProvider?.Length > 32 ||
                instance.CatalogProjectId?.Length > 64 || instance.CatalogVersionId?.Length > 64)
            {
                throw new InvalidDataException("Minecraft client instance settings exceed safe limits.");
            }

            if (instance.CatalogProvider is not null &&
                !instance.CatalogProvider.Equals("modrinth", StringComparison.Ordinal) ||
                instance.CatalogIconUri is not null &&
                !ModrinthClientModpackCatalog.IsOfficialCdnUri(instance.CatalogIconUri) ||
                instance.CatalogPreviewUri is not null &&
                !ModrinthClientModpackCatalog.IsOfficialCdnUri(instance.CatalogPreviewUri))
            {
                throw new InvalidDataException("Minecraft client catalog provenance is invalid.");
            }

            if (instance.CatalogProvider?.Equals("modrinth", StringComparison.Ordinal) == true &&
                (!IsCatalogIdentifier(instance.CatalogProjectId) ||
                 !IsCatalogIdentifier(instance.CatalogVersionId)))
            {
                throw new InvalidDataException("Modrinth client catalog provenance is incomplete.");
            }

            if (instance.CatalogProvider is null &&
                (instance.CatalogProjectId is not null || instance.CatalogVersionId is not null ||
                 instance.CatalogIconUri is not null || instance.CatalogPreviewUri is not null))
            {
                throw new InvalidDataException("Minecraft client catalog metadata has no provider.");
            }

            var activeMarkerFieldCount =
                (instance.ActiveProcessId is null ? 0 : 1) +
                (instance.ActiveProcessStartedAtUtc is null ? 0 : 1) +
                (instance.ActiveProcessExecutablePath is null ? 0 : 1);
            if (activeMarkerFieldCount != 0)
            {
                if (activeMarkerFieldCount != 3 ||
                    !MinecraftClientProcessRecoveryService.TryGetPersistedIdentity(
                        instance,
                        out var activeIdentity) ||
                    !activeProcesses.Add((activeIdentity.ProcessId, activeIdentity.StartedAtUtc.UtcTicks)))
                {
                    throw new InvalidDataException(
                        "Minecraft client active-process identity is incomplete, unsafe or duplicated.");
                }

                if (!string.IsNullOrWhiteSpace(instance.JavaExecutablePath) &&
                    !PathsEqual(instance.JavaExecutablePath, activeIdentity.ExecutablePath))
                {
                    throw new InvalidDataException(
                        "Minecraft client active-process executable does not match its selected Java runtime.");
                }
            }

            foreach (var pair in instance.EnvironmentVariables)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 ||
                    pair.Key.Contains('=') || pair.Value.Length > 4_096)
                {
                    throw new InvalidDataException("Minecraft client environment variable is invalid.");
                }
            }
        }

        static bool IsCatalogIdentifier(string? value)
            => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
               value.All(char.IsAsciiLetterOrDigit);

        static bool PathsEqual(string first, string second)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(first),
                    Path.GetFullPath(second),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (
                error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }
    }
}

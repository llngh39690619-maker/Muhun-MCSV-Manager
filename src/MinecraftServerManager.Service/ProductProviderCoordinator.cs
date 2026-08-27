using System.Text.Json;
using MinecraftServerManager.Contracts.Plugins;
using MinecraftServerManager.Data;
using MinecraftServerManager.ProviderHost;

namespace MinecraftServerManager.Service;

/// <summary>
/// Single Service-owned entry point for provider trust, installation, lifecycle, and execution.
/// GUI/Web clients never launch a provider or supply an arbitrary host path.
/// </summary>
public sealed class ProductProviderCoordinator(
    ProductDataLayout productLayout,
    ProviderHostLayout providerLayout,
    ProviderRegistry registry,
    ProductProviderPublisherTrustStore trustStore,
    ProviderPackageInstaller installer,
    ProviderPackageUninstaller uninstaller,
    ProviderInvocationHost invocationHost,
    ProductBuiltinProviderBootstrapper builtinProviderBootstrapper,
    ProductSecurityAuditStore auditStore,
    TimeProvider timeProvider,
    ILogger<ProductProviderCoordinator> logger)
{
    public const string InboxDirectoryName = "inbox";
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public string InboxDirectory => Path.Combine(productLayout.Plugins, InboxDirectoryName);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        providerLayout.EnsureCreated();
        Directory.CreateDirectory(InboxDirectory);
        EnsurePathHasNoReparsePoints(productLayout.Plugins, InboxDirectory);
        await trustStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        await registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        await builtinProviderBootstrapper.EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<ProductProviderSummary> List()
        => registry.GetAll().Select(ToSummary).ToArray();

    public IReadOnlyList<ProductTrustedProviderPublisherSummary> ListTrustedPublishers()
        => trustStore.List();

    public async Task<ProductTrustedProviderPublisherSummary> PinPublisherAsync(
        ProductPinProviderPublisherRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var correlationId = Guid.NewGuid();
        try
        {
            RequireAcceptedAudit("provider.publisher.pin", "publisher_pin_requested", correlationId);
            var result = await trustStore.PinAsync(
                    request.PublisherId,
                    request.PublicKeyPem,
                    cancellationToken)
                .ConfigureAwait(false);
            TryOutcomeAudit("provider.publisher.pin", "succeeded", "publisher_pinned", correlationId);
            return result;
        }
        catch
        {
            TryOutcomeAudit("provider.publisher.pin", "failed", "publisher_pin_failed", correlationId);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<bool> RemovePublisherAsync(
        string publisherId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var correlationId = Guid.NewGuid();
        try
        {
            if (registry.GetAll().Any(provider =>
                    provider.PublisherId.Equals(publisherId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "A publisher key cannot be removed while its providers are installed.");
            }

            RequireAcceptedAudit("provider.publisher.remove", "publisher_remove_requested", correlationId);
            var removed = await trustStore.RemoveAsync(publisherId, cancellationToken).ConfigureAwait(false);
            TryOutcomeAudit(
                "provider.publisher.remove",
                removed ? "succeeded" : "skipped",
                removed ? "publisher_removed" : "publisher_not_found",
                correlationId);
            return removed;
        }
        catch
        {
            TryOutcomeAudit("provider.publisher.remove", "failed", "publisher_remove_failed", correlationId);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ProductProviderSummary> InstallFromInboxAsync(
        ProductProviderInstallFromInboxRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateInstallContract(request);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var correlationId = Guid.NewGuid();
        string? privateStagingPath = null;
        try
        {
            RequireAcceptedAudit("provider.install", "provider_install_requested", correlationId);
            privateStagingPath = await CopyInboxPackageToPrivateStagingAsync(
                    request.InboxFileName,
                    cancellationToken)
                .ConfigureAwait(false);
            var result = await installer.InstallAsync(
                    new ProviderPackageInstallRequest(
                        privateStagingPath,
                        request.ExpectedSha256,
                        request.ExpectedProviderId,
                        request.ExpectedVersion,
                        request.ExpectedPublisherId,
                        new ProviderPackageSignature(
                            request.Signature.PublisherId,
                            request.Signature.Algorithm,
                            request.Signature.SignatureBase64,
                            request.Signature.FormatVersion),
                        request.AllowDowngrade),
                    cancellationToken)
                .ConfigureAwait(false);

            TryDeleteInboxSource(request.InboxFileName);
            TryOutcomeAudit("provider.install", "succeeded", "provider_installed", correlationId);
            return ToSummary(result.Registration);
        }
        catch
        {
            TryOutcomeAudit("provider.install", "failed", "provider_install_failed", correlationId);
            throw;
        }
        finally
        {
            if (privateStagingPath is not null)
            {
                TryDeleteFile(privateStagingPath);
            }

            _operationGate.Release();
        }
    }

    public async Task<ProductProviderSummary> SetEnabledAsync(
        string providerId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var correlationId = Guid.NewGuid();
        try
        {
            RequireAcceptedAudit("provider.enable", "provider_enable_requested", correlationId);
            await registry.SetEnabledAsync(providerId, enabled, cancellationToken).ConfigureAwait(false);
            if (!registry.TryGet(providerId, out var current))
            {
                throw new KeyNotFoundException("Provider is not registered.");
            }

            TryOutcomeAudit(
                "provider.enable",
                "succeeded",
                enabled ? "provider_enabled" : "provider_disabled",
                correlationId);
            return ToSummary(current);
        }
        catch
        {
            TryOutcomeAudit("provider.enable", "failed", "provider_enable_failed", correlationId);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<bool> UninstallAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var correlationId = Guid.NewGuid();
        try
        {
            RequireAcceptedAudit("provider.uninstall", "provider_uninstall_requested", correlationId);
            var removed = await uninstaller.UninstallAsync(providerId, cancellationToken).ConfigureAwait(false);
            TryOutcomeAudit(
                "provider.uninstall",
                removed ? "succeeded" : "skipped",
                removed ? "provider_uninstalled" : "provider_not_found",
                correlationId);
            return removed;
        }
        catch
        {
            TryOutcomeAudit("provider.uninstall", "failed", "provider_uninstall_failed", correlationId);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ProductProviderRpcResponse> CheckHealthAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var correlationId = Guid.NewGuid();
        try
        {
            RequireAcceptedAudit("provider.health", "provider_health_requested", correlationId);
            using var payload = JsonDocument.Parse("{}");
            var response = await invocationHost.InvokeAsync(
                    providerId,
                    new ProviderInvocationRequest(
                        ProductProviderOperations.HealthGet,
                        payload.RootElement.Clone()),
                    TimeSpan.FromSeconds(10),
                    cancellationToken)
                .ConfigureAwait(false);
            TryOutcomeAudit("provider.health", "succeeded", "provider_health_succeeded", correlationId);
            return response;
        }
        catch
        {
            TryOutcomeAudit("provider.health", "failed", "provider_health_failed", correlationId);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task<ProductProviderRpcResponse> InvokeAsync(
        string providerId,
        ProviderInvocationRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => invocationHost.InvokeAsync(providerId, request, timeout, cancellationToken);

    private async Task<string> CopyInboxPackageToPrivateStagingAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        providerLayout.EnsureCreated();
        Directory.CreateDirectory(InboxDirectory);
        EnsurePathHasNoReparsePoints(productLayout.Plugins, InboxDirectory);
        var sourcePath = ResolveInboxPath(fileName);
        var sourceInfo = new FileInfo(sourcePath);
        if (!sourceInfo.Exists || sourceInfo.Length is < 1 or > ProviderPackageInstaller.MaximumPackageBytes ||
            sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Provider inbox package is unavailable or unsafe.");
        }

        var stagingPath = Path.Combine(
            providerLayout.State,
            $".provider-upload-{Guid.NewGuid():N}.mcsvp");
        try
        {
            await using var input = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length is < 1 or > ProviderPackageInstaller.MaximumPackageBytes)
            {
                throw new InvalidDataException("Provider inbox package size is invalid.");
            }

            await using var output = new FileStream(
                stagingPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            return stagingPath;
        }
        catch
        {
            TryDeleteFile(stagingPath);
            throw;
        }
    }

    private string ResolveInboxPath(string fileName)
    {
        if (fileName is null || fileName.Length is < 1 or > 180 ||
            !fileName.EndsWith(".mcsvp", StringComparison.OrdinalIgnoreCase) ||
            fileName.Any(character =>
                char.IsControl(character) || char.IsSurrogate(character) ||
                character is '/' or '\\' or ':') ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Provider inbox file name is invalid.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(InboxDirectory));
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Provider inbox path leaves its managed root.");
        }

        return path;
    }

    private static void ValidateInstallContract(ProductProviderInstallFromInboxRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Signature);
        if (!string.Equals(
                request.Signature.PublisherId,
                request.ExpectedPublisherId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Provider signature publisher does not match the request.");
        }

        if (request.AllowDowngrade)
        {
            // Rollback authorization is intentionally a distinct local API decision and remains
            // visible in the security audit. It is never inferred from a lower package version.
        }
    }

    private void TryDeleteInboxSource(string fileName)
    {
        try
        {
            TryDeleteFile(ResolveInboxPath(fileName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning(
                "Installed provider inbox source could not be removed ({FailureType}).",
                error.GetType().Name);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void EnsurePathHasNoReparsePoints(string rootDirectory, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var candidate = Path.GetFullPath(candidatePath);
        if (candidate != root &&
            !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Provider inbox path leaves its managed root.");
        }

        var current = root;
        RejectReparse(current);
        if (candidate == root)
        {
            return;
        }

        foreach (var segment in Path.GetRelativePath(root, candidate)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectReparse(current);
        }
    }

    private static void RejectReparse(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Provider inbox paths cannot contain reparse points.");
        }
    }

    private void RequireAcceptedAudit(string action, string reason, Guid correlationId)
    {
        if (!TryOutcomeAudit(action, "accepted", reason, correlationId))
        {
            throw new InvalidOperationException(
                "Provider management was rejected because its security audit could not be recorded.");
        }
    }

    private bool TryOutcomeAudit(string action, string outcome, string reason, Guid correlationId)
        => auditStore.TryAppend(new ProductSecurityAuditEntry(
            Guid.NewGuid(),
            timeProvider.GetUtcNow().ToUniversalTime(),
            action,
            outcome,
            Username: null,
            PermissionCode: "provider.manage",
            ServerId: null,
            reason,
            correlationId));

    private static ProductProviderSummary ToSummary(ProviderRegistration registration)
        => new(
            registration.Manifest.Id,
            registration.Manifest.DisplayName,
            registration.Manifest.Version,
            registration.PublisherId,
            registration.IsEnabled,
            registration.Health switch
            {
                ProviderHealthStatus.Disabled => ProductProviderHealthState.Disabled,
                ProviderHealthStatus.Stopped => ProductProviderHealthState.Stopped,
                ProviderHealthStatus.Starting => ProductProviderHealthState.Starting,
                ProviderHealthStatus.Healthy => ProductProviderHealthState.Healthy,
                ProviderHealthStatus.Degraded => ProductProviderHealthState.Degraded,
                ProviderHealthStatus.Failed => ProductProviderHealthState.Failed,
                _ => throw new InvalidDataException("Provider registry contains unknown health state."),
            },
            registration.Manifest.Capabilities.ToArray(),
            registration.Manifest.Permissions.ToArray(),
            registration.InstalledAtUtc,
            registration.LastHealthTransitionUtc,
            registration.ConsecutiveFailures,
            registration.LastError);
}

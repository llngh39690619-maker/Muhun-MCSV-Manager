using System.Diagnostics;
using System.Security.Cryptography;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Updater;

namespace MinecraftServerManager.Service;

public interface IProductUpdateCoordinator
{
    ProductUpdateStatus GetStatus(ProductUpdateChannel channel);

    Task<ProductUpdateOperationResult> CheckAsync(ProductUpdateChannel channel, CancellationToken cancellationToken);

    Task<ProductUpdateOperationResult> DownloadAsync(ProductUpdateChannel channel, CancellationToken cancellationToken);

    Task<ProductUpdateOperationResult> ScheduleAsync(
        ProductUpdateChannel channel,
        DateTimeOffset? notBeforeUtc,
        CancellationToken cancellationToken);

    Task<bool> LaunchDueActivationAsync(CancellationToken cancellationToken);
}

public interface IProductUpdateActivationLauncher
{
    Task LaunchAsync(string updaterExecutablePath, string requestPath, CancellationToken cancellationToken);
}

public sealed record ProductUpdateStatusChangedEventArgs(
    ProductUpdateStatus Previous,
    ProductUpdateStatus Current,
    DateTimeOffset OccurredAtUtc);

public sealed class ProductUpdateActivationLauncher : IProductUpdateActivationLauncher
{
    public Task LaunchAsync(
        string updaterExecutablePath,
        string requestPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(updaterExecutablePath) ||
            !string.Equals(
                Path.GetFileName(updaterExecutablePath),
                "Muhun MCSV Updater.exe",
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(updaterExecutablePath) ||
            (File.GetAttributes(updaterExecutablePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The fixed signed updater executable is unavailable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(updaterExecutablePath),
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(updaterExecutablePath))!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--activation-request");
        startInfo.ArgumentList.Add(Path.GetFullPath(requestPath));
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Signed updater process could not be started.");
        // The updater owns the Windows Service stop/config/start transition after it has verified
        // the authenticated request, signature and package. Stopping this Service immediately
        // after Process.Start would turn any updater preflight failure into an outage.
        return Task.CompletedTask;
    }
}

public sealed class ProductUpdateCoordinator : IProductUpdateCoordinator, IDisposable
{
    private static readonly TimeSpan ActivationRelaunchInterval = TimeSpan.FromSeconds(15);

    private readonly ProductUpdateOptions _options;
    private readonly ProductServiceOptions _serviceOptions;
    private readonly ProductDataLayout _layout;
    private readonly ProductInstallationIdentityStore _identityStore;
    private readonly IProductUpdateTransport? _transport;
    private readonly IProductUpdateActivationLauncher _launcher;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _updaterPathResolver;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<ProductUpdateChannel, ProductUpdateStatus> _statuses = [];
    private readonly Dictionary<ProductUpdateChannel, VerifiedCandidate> _candidates = [];
    private int _disposed;

    public event EventHandler<ProductUpdateStatusChangedEventArgs>? StatusChanged;

    public ProductUpdateCoordinator(
        ProductUpdateOptions options,
        ProductServiceOptions serviceOptions,
        ProductDataLayout layout,
        ProductInstallationIdentityStore identityStore,
        IProductUpdateActivationLauncher launcher,
        TimeProvider timeProvider,
        IProductUpdateTransport? transport = null,
        Func<string>? updaterPathResolver = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _serviceOptions = serviceOptions ?? throw new ArgumentNullException(nameof(serviceOptions));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _identityStore = identityStore ?? throw new ArgumentNullException(nameof(identityStore));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _updaterPathResolver = updaterPathResolver ?? (() => ResolveSiblingProductExecutable(
            "updater-win-x64",
            "Muhun MCSV Updater.exe"));
        var validation = ProductUpdateOptionsValidator.Validate(options);
        if (validation.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", validation));
        }

        _transport = transport;
        foreach (var channel in Enum.GetValues<ProductUpdateChannel>())
        {
            _statuses[channel] = CreateInitialStatus(channel);
        }

        RestorePendingSchedule();
    }

    public ProductUpdateStatus GetStatus(ProductUpdateChannel channel)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ValidateChannel(channel);
        lock (_stateGate)
        {
            return _statuses[channel];
        }
    }

    public Task<ProductUpdateOperationResult> CheckAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken)
        => ExecuteAsync(channel, () => CheckCoreAsync(channel, cancellationToken), cancellationToken);

    public Task<ProductUpdateOperationResult> DownloadAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken)
        => ExecuteAsync(channel, () => DownloadCoreAsync(channel, cancellationToken), cancellationToken);

    public Task<ProductUpdateOperationResult> ScheduleAsync(
        ProductUpdateChannel channel,
        DateTimeOffset? notBeforeUtc,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            channel,
            () => ScheduleCoreAsync(channel, notBeforeUtc, cancellationToken),
            cancellationToken);

    internal async Task<ProductUpdateRetentionResult> RunArtifactRetentionAsync(
        string installRoot,
        string executingVersion,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string[] candidateVersions;
            lock (_stateGate)
            {
                candidateVersions = _candidates.Values
                    .Select(candidate => candidate.Manifest.Version)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            return new ProductUpdateRetentionManager(
                    installRoot,
                    _layout.Updates,
                    executingVersion)
                .Run(candidateVersions, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<bool> LaunchDueActivationAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = ReadPendingSchedule();
            if (pending is null)
            {
                return false;
            }

            ValidateChannel(pending.Channel);
            var receipt = ProductUpdateActivationReceiptProtocol.Read(
                _layout.Updates,
                pending.OperationId);
            if (receipt is not null)
            {
                ValidateReceiptBinding(pending, receipt);
                if (receipt.Outcome == ProductUpdateActivationReceiptOutcome.Rejected)
                {
                    SetFailure(pending.Channel, receipt.FailureCode ?? "update.activation_rejected");
                    return false;
                }

                CompletePendingActivation(pending, receipt);
                return true;
            }

            var now = _timeProvider.GetUtcNow();
            if (pending.NotBeforeUtc > now)
            {
                return false;
            }

            if (pending.LastLaunchAtUtc is { } lastLaunch)
            {
                if (lastLaunch > now.AddMinutes(1))
                {
                    throw new InvalidDataException("Pending activation launch time is in the future.");
                }

                if (now - lastLaunch < ActivationRelaunchInterval)
                {
                    return false;
                }
            }

            var candidate = LoadVerifiedCandidate(pending.Channel, pending.Version);
            ValidatePendingBinding(pending, candidate);
            var requestPath = ProductUpdateActivationRequestProtocol.GetRequestPath(
                _layout.Updates,
                pending.OperationId);
            if (!File.Exists(requestPath))
            {
                var activationKey = GetOrCreateActivationKey();
                try
                {
                    requestPath = ProductUpdateActivationRequestProtocol.Create(
                        _layout.Updates,
                        _identityStore.GetOrCreate(),
                        candidate.Manifest.Version,
                        candidate.Manifest.Channel,
                        Convert.ToHexString(SHA256.HashData(candidate.ManifestBytes)),
                        candidate.Manifest.Package.Sha256,
                        _serviceOptions.Port,
                        candidate.KeyDocument.KeyId,
                        candidate.KeyDocument.SubjectPublicKeyInfoSha256,
                        _options.AllowedFeedHosts,
                        activationKey,
                        _timeProvider,
                        operationId: pending.OperationId);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(activationKey);
                }
            }

            var verifiedRequest = ProductUpdateActivationRequestProtocol.VerifyForActivation(
                requestPath,
                _timeProvider);
            ProductUpdatePendingActivationProtocol.ValidateRequestBinding(
                pending,
                verifiedRequest.Request);
            var dispatched = pending with
            {
                SchemaVersion = ProductUpdatePendingActivationProtocol.CurrentSchemaVersion,
                LastLaunchAtUtc = now,
                LaunchAttempts = checked(pending.LaunchAttempts + 1),
            };
            WritePendingSchedule(dispatched);

            SetStatus(pending.Channel, GetStatus(pending.Channel) with
            {
                Phase = ProductUpdatePhase.Applying,
                Message = "Signed updater activation is starting.",
                ErrorCode = null,
            });
            var updaterPath = _updaterPathResolver();
            await _launcher.LaunchAsync(updaterPath, requestPath, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
        {
            try
            {
                var pending = ReadPendingSchedule();
                if (pending is not null)
                {
                    SetFailure(pending.Channel, "update.activation_rejected");
                }
            }
            catch (Exception stateException) when (IsRecoverable(stateException, cancellationToken))
            {
                // A malformed durable record remains untouched for protected-log diagnosis.
            }

            return false;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _operationGate.Dispose();
            if (_transport is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private async Task<ProductUpdateOperationResult> ExecuteAsync(
        ProductUpdateChannel channel,
        Func<Task<ProductUpdateOperationResult>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ValidateChannel(channel);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
        {
            var status = SetFailure(channel, "update.operation_failed");
            return new ProductUpdateOperationResult(false, status);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<ProductUpdateOperationResult> CheckCoreAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken)
    {
        var feed = GetFeed(channel);
        if (feed is null || _transport is null)
        {
            var disabled = SetStatus(channel, CreateInitialStatus(channel));
            return new ProductUpdateOperationResult(false, disabled);
        }

        SetStatus(channel, GetStatus(channel) with
        {
            Phase = ProductUpdatePhase.Checking,
            ErrorCode = null,
            Message = "Checking the signed update feed.",
        });
        var signatureUri = new Uri(feed.AbsoluteUri + ".sig", UriKind.Absolute);
        var manifestBytes = await _transport.GetBytesAsync(
                feed,
                ProductUpdateManifestParser.MaximumManifestBytes,
                cancellationToken)
            .ConfigureAwait(false);
        var signature = await _transport.GetBytesAsync(signatureUri, 1024, cancellationToken)
            .ConfigureAwait(false);
        var publicKeyBytes = ReadPublicKeyDocument();
        using var key = ProductUpdatePublicKeyLoader.Load(publicKeyBytes, out var keyDocument);
        var now = _timeProvider.GetUtcNow();
        if (now < keyDocument.NotBeforeUtc || now > keyDocument.NotAfterUtc)
        {
            throw new CryptographicException("Update signing key is outside its validity period.");
        }

        var verifier = new SignedProductUpdateManifestVerifier(
            new Dictionary<string, RSA>(StringComparer.Ordinal) { [keyDocument.KeyId] = key },
            new HashSet<string>(_options.AllowedFeedHosts, StringComparer.OrdinalIgnoreCase),
            _timeProvider);
        var manifest = verifier.Verify(manifestBytes, signature);
        ProductFormalUpdateManifestValidator.Validate(manifest);
        var expectedChannel = channel == ProductUpdateChannel.Stable ? "stable" : "beta";
        if (!string.Equals(manifest.Channel, expectedChannel, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Signed update manifest channel does not match the selected feed.");
        }

        PersistVerifiedTrust(manifest, manifestBytes, signature, publicKeyBytes);
        var candidate = new VerifiedCandidate(manifest, manifestBytes, signature, keyDocument);
        lock (_stateGate)
        {
            _candidates[channel] = candidate;
        }

        var comparison = CompareSemanticVersions(manifest.Version, ProductServiceApplication.ProductVersion);
        var phase = comparison > 0 ? ProductUpdatePhase.Available : ProductUpdatePhase.Idle;
        var status = SetStatus(channel, GetStatus(channel) with
        {
            Phase = phase,
            AvailableVersion = comparison > 0 ? manifest.Version : null,
            PackageSizeBytes = comparison > 0 ? manifest.Package.SizeBytes : null,
            DownloadedBytes = 0,
            LastCheckedAtUtc = now,
            ErrorCode = null,
            Message = comparison > 0 ? "A signed update is available." : "This installation is up to date.",
        });
        return new ProductUpdateOperationResult(true, status);
    }

    private async Task<ProductUpdateOperationResult> DownloadCoreAsync(
        ProductUpdateChannel channel,
        CancellationToken cancellationToken)
    {
        VerifiedCandidate? candidate;
        lock (_stateGate)
        {
            _candidates.TryGetValue(channel, out candidate);
        }

        if (candidate is null)
        {
            var check = await CheckCoreAsync(channel, cancellationToken).ConfigureAwait(false);
            if (!check.Accepted)
            {
                return check;
            }

            lock (_stateGate)
            {
                _candidates.TryGetValue(channel, out candidate);
            }
        }

        if (candidate is null ||
            CompareSemanticVersions(candidate.Manifest.Version, ProductServiceApplication.ProductVersion) <= 0)
        {
            return new ProductUpdateOperationResult(false, GetStatus(channel));
        }

        Directory.CreateDirectory(PackagesRoot);
        RejectExistingReparsePoints(PackagesRoot);
        var finalPath = GetPackagePath(candidate.Manifest.Version);
        var temporaryPath = Path.Combine(PackagesRoot, $".{candidate.Manifest.Version}.{Guid.NewGuid():N}.tmp");
        SetStatus(channel, GetStatus(channel) with
        {
            Phase = ProductUpdatePhase.Downloading,
            DownloadedBytes = 0,
            ErrorCode = null,
            Message = "Downloading the signed update package.",
        });
        try
        {
            if (!File.Exists(finalPath))
            {
                await _transport!.DownloadAsync(
                        new Uri(candidate.Manifest.Package.Url, UriKind.Absolute),
                        temporaryPath,
                        candidate.Manifest.Package.SizeBytes,
                        bytes => SetDownloadedBytes(channel, bytes),
                        cancellationToken)
                    .ConfigureAwait(false);
                await VerifyPackageAsync(temporaryPath, candidate.Manifest, cancellationToken)
                    .ConfigureAwait(false);
                File.Move(temporaryPath, finalPath, overwrite: false);
            }
            else
            {
                await VerifyPackageAsync(finalPath, candidate.Manifest, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            TryDelete(temporaryPath);
        }

        var status = SetStatus(channel, GetStatus(channel) with
        {
            Phase = ProductUpdatePhase.Ready,
            DownloadedBytes = candidate.Manifest.Package.SizeBytes,
            ErrorCode = null,
            Message = "Update package was fully verified and is ready.",
        });
        return new ProductUpdateOperationResult(true, status);
    }

    private Task<ProductUpdateOperationResult> ScheduleCoreAsync(
        ProductUpdateChannel channel,
        DateTimeOffset? notBeforeUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = GetStatus(channel);
        VerifiedCandidate? candidate;
        lock (_stateGate)
        {
            _candidates.TryGetValue(channel, out candidate);
        }

        if (candidate is null || !File.Exists(GetPackagePath(candidate.Manifest.Version)) ||
            status.Phase is not (ProductUpdatePhase.Ready or ProductUpdatePhase.Scheduled))
        {
            return Task.FromResult(new ProductUpdateOperationResult(false, status));
        }

        var now = _timeProvider.GetUtcNow();
        var scheduled = notBeforeUtc ?? now;
        if (scheduled.Offset != TimeSpan.Zero || scheduled < now.AddMinutes(-1) || scheduled > now.AddDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(notBeforeUtc));
        }

        WritePendingSchedule(new ProductUpdatePendingActivation(
            ProductUpdatePendingActivationProtocol.CurrentSchemaVersion,
            channel,
            candidate.Manifest.Version,
            scheduled,
            Guid.NewGuid(),
            Convert.ToHexString(SHA256.HashData(candidate.ManifestBytes)),
            candidate.Manifest.Package.Sha256,
            candidate.KeyDocument.KeyId,
            candidate.KeyDocument.SubjectPublicKeyInfoSha256,
            ProductUpdatePendingActivationProtocol.HashAllowedHosts(_options.AllowedFeedHosts)));
        var updated = SetStatus(channel, status with
        {
            Phase = ProductUpdatePhase.Scheduled,
            ScheduledForUtc = scheduled,
            ErrorCode = null,
            Message = "Verified update activation is scheduled.",
        });
        return Task.FromResult(new ProductUpdateOperationResult(
            true,
            updated,
            ReadPendingSchedule()!.OperationId.ToString("D")));
    }

    private VerifiedCandidate LoadVerifiedCandidate(ProductUpdateChannel channel, string version)
    {
        ProductUpdateManifestParser.ValidateVersion(version);
        var verifiedRoot = Path.Combine(_layout.Updates, ProductUpdateActivationRequestProtocol.VerifiedDirectoryName, version);
        var manifestBytes = ReadBounded(
            Path.Combine(verifiedRoot, ProductUpdateActivationRequestProtocol.ManifestFileName),
            ProductUpdateManifestParser.MaximumManifestBytes);
        var signature = ReadBounded(
            Path.Combine(verifiedRoot, ProductUpdateActivationRequestProtocol.ManifestSignatureFileName),
            1024);
        var publicKeyBytes = ReadBounded(
            Path.Combine(
                _layout.Updates,
                ProductUpdateActivationRequestProtocol.TrustDirectoryName,
                ProductUpdateActivationRequestProtocol.PublicKeyDocumentFileName),
            ProductUpdatePublicKeyLoader.MaximumDocumentBytes);
        using var key = ProductUpdatePublicKeyLoader.Load(publicKeyBytes, out var keyDocument);
        var verifier = new SignedProductUpdateManifestVerifier(
            new Dictionary<string, RSA>(StringComparer.Ordinal) { [keyDocument.KeyId] = key },
            new HashSet<string>(_options.AllowedFeedHosts, StringComparer.OrdinalIgnoreCase),
            _timeProvider);
        var manifest = verifier.Verify(manifestBytes, signature);
        ProductFormalUpdateManifestValidator.Validate(manifest);
        var expectedChannel = channel == ProductUpdateChannel.Stable ? "stable" : "beta";
        var packagePath = GetPackagePath(version);
        if (!string.Equals(manifest.Version, version, StringComparison.Ordinal) ||
            !string.Equals(manifest.Channel, expectedChannel, StringComparison.Ordinal) ||
            !File.Exists(packagePath) ||
            (File.GetAttributes(packagePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Scheduled update artifacts are incomplete or inconsistent.");
        }

        return new VerifiedCandidate(manifest, manifestBytes, signature, keyDocument);
    }

    private async Task VerifyPackageAsync(
        string packagePath,
        ProductUpdateManifest manifest,
        CancellationToken cancellationToken)
    {
        var verificationRoot = Path.Combine(_layout.Updates, "verification", Guid.NewGuid().ToString("N"));
        try
        {
            await using var package = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await new SafeProductPackageExtractor()
                .ExtractAndVerifyAsync(package, verificationRoot, manifest, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(verificationRoot))
            {
                Directory.Delete(verificationRoot, recursive: true);
            }
        }
    }

    private void PersistVerifiedTrust(
        ProductUpdateManifest manifest,
        byte[] manifestBytes,
        byte[] signature,
        byte[] publicKeyBytes)
    {
        var verifiedRoot = Path.Combine(
            _layout.Updates,
            ProductUpdateActivationRequestProtocol.VerifiedDirectoryName,
            manifest.Version);
        var trustRoot = Path.Combine(_layout.Updates, ProductUpdateActivationRequestProtocol.TrustDirectoryName);
        Directory.CreateDirectory(verifiedRoot);
        Directory.CreateDirectory(trustRoot);
        RejectExistingReparsePoints(verifiedRoot);
        RejectExistingReparsePoints(trustRoot);
        WriteAtomic(
            Path.Combine(verifiedRoot, ProductUpdateActivationRequestProtocol.ManifestFileName),
            manifestBytes);
        WriteAtomic(
            Path.Combine(verifiedRoot, ProductUpdateActivationRequestProtocol.ManifestSignatureFileName),
            signature);
        WriteAtomic(
            Path.Combine(trustRoot, ProductUpdateActivationRequestProtocol.PublicKeyDocumentFileName),
            publicKeyBytes);
    }

    private byte[] ReadPublicKeyDocument()
    {
        var path = string.IsNullOrWhiteSpace(_options.PublicKeyDocumentPath)
            ? Path.Combine(AppContext.BaseDirectory, ProductUpdateActivationRequestProtocol.PublicKeyDocumentFileName)
            : Path.GetFullPath(_options.PublicKeyDocumentPath);
        return ReadBounded(path, ProductUpdatePublicKeyLoader.MaximumDocumentBytes);
    }

    private byte[] GetOrCreateActivationKey()
    {
        Directory.CreateDirectory(_layout.Updates);
        var keyPath = Path.Combine(
            _layout.Updates,
            ProductUpdateActivationRequestProtocol.AuthenticationKeyFileName);
        if (File.Exists(keyPath))
        {
            return ReadBoundedExact(
                keyPath,
                ProductUpdateActivationRequestProtocol.AuthenticationKeyBytes);
        }

        var key = RandomNumberGenerator.GetBytes(ProductUpdateActivationRequestProtocol.AuthenticationKeyBytes);
        try
        {
            using var stream = new FileStream(
                keyPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4_096,
                FileOptions.WriteThrough);
            stream.Write(key);
            stream.Flush(flushToDisk: true);
            return key.ToArray();
        }
        catch (IOException) when (File.Exists(keyPath))
        {
            return ReadBoundedExact(
                keyPath,
                ProductUpdateActivationRequestProtocol.AuthenticationKeyBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private ProductUpdateStatus CreateInitialStatus(ProductUpdateChannel channel)
    {
        var configured = GetFeed(channel) is not null && _options.AllowedFeedHosts.Count > 0;
        var serviceVersion = ProductServiceApplication.ProductVersion;
        var guiVersion = GetGuiVersion();
        return new ProductUpdateStatus(
            channel,
            configured ? ProductUpdatePhase.Idle : ProductUpdatePhase.Disabled,
            serviceVersion,
            guiVersion,
            string.Equals(serviceVersion, guiVersion, StringComparison.Ordinal),
            configured,
            null,
            null,
            0,
            null,
            null,
            null,
            configured ? "Update feed is ready." : "No exact allowlisted HTTPS feed is configured.");
    }

    private Uri? GetFeed(ProductUpdateChannel channel)
    {
        ValidateChannel(channel);
        var value = channel == ProductUpdateChannel.Stable
            ? _options.StableManifestUrl
            : _options.BetaManifestUrl;
        return string.IsNullOrWhiteSpace(value) ? null : new Uri(value, UriKind.Absolute);
    }

    private string GetGuiVersion()
    {
        string path;
        try
        {
            path = ResolveSiblingProductExecutable(
                "gui-win-x64",
                "Muhun MCSV Manager.exe");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return "unavailable";
        }

        return FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+', 2)[0] ?? "unknown";
    }

    internal static string ResolveSiblingProductExecutable(string payloadDirectory, string fileName)
        => ResolveSiblingProductExecutableFromBaseDirectory(
            AppContext.BaseDirectory,
            payloadDirectory,
            fileName);

    internal static string ResolveSiblingProductExecutableFromBaseDirectory(
        string serviceBaseDirectory,
        string payloadDirectory,
        string fileName)
    {
        if (!((payloadDirectory == "gui-win-x64" && fileName == "Muhun MCSV Manager.exe") ||
              (payloadDirectory == "service-win-x64" && fileName == "Muhun MCSV Service.exe") ||
              (payloadDirectory == "updater-win-x64" && fileName == "Muhun MCSV Updater.exe")))
        {
            throw new InvalidDataException("Product payload identity is invalid.");
        }

        if (!Path.IsPathFullyQualified(serviceBaseDirectory))
        {
            throw new InvalidDataException("Service base directory must be absolute.");
        }

        var currentPayloadRoot = new DirectoryInfo(Path.GetFullPath(serviceBaseDirectory).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        if (!string.Equals(currentPayloadRoot.Name, "service-win-x64", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Service is not running from the formal service-win-x64 directory.");
        }

        var versionRoot = currentPayloadRoot.Parent
            ?? throw new InvalidDataException("Service version root is missing.");
        ProductUpdateManifestParser.ValidateVersion(versionRoot.Name);
        RejectExistingReparsePoints(versionRoot.FullName);
        var path = Path.GetFullPath(Path.Combine(versionRoot.FullName, payloadDirectory, fileName));
        RejectExistingReparsePoints(path);
        if (!File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            !string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Required sibling product executable is unavailable.");
        }

        var productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+', 2)[0];
        if (!string.Equals(productVersion, versionRoot.Name, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Sibling product executable version does not match its version root.");
        }

        return path;
    }

    private void RestorePendingSchedule()
    {
        try
        {
            var pending = ReadPendingSchedule();
            if (pending is null)
            {
                return;
            }

            var status = _statuses[pending.Channel];
            _statuses[pending.Channel] = status with
            {
                Phase = pending.LastLaunchAtUtc is null
                    ? ProductUpdatePhase.Scheduled
                    : ProductUpdatePhase.Applying,
                AvailableVersion = pending.Version,
                ScheduledForUtc = pending.NotBeforeUtc,
                Message = pending.LastLaunchAtUtc is null
                    ? "Verified update activation is scheduled."
                    : "Update activation is awaiting a durable updater result.",
            };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            // A malformed pending record never triggers activation. It remains for diagnostics.
        }
    }

    private ProductUpdatePendingActivation? ReadPendingSchedule()
        => ProductUpdatePendingActivationProtocol.Read(_layout.Updates);

    private void WritePendingSchedule(ProductUpdatePendingActivation pending)
        => ProductUpdatePendingActivationProtocol.Write(_layout.Updates, pending);

    private ProductUpdateStatus SetFailure(ProductUpdateChannel channel, string errorCode)
        => SetStatus(channel, GetStatus(channel) with
        {
            Phase = ProductUpdatePhase.Failed,
            ErrorCode = errorCode,
            Message = "Update operation failed closed. Review the protected Service log.",
        });

    private ProductUpdateStatus SetStatus(ProductUpdateChannel channel, ProductUpdateStatus status)
    {
        ProductUpdateStatus previous;
        lock (_stateGate)
        {
            previous = _statuses[channel];
            _statuses[channel] = status;
        }

        if (previous != status)
        {
            StatusChanged?.Invoke(
                this,
                new ProductUpdateStatusChangedEventArgs(
                    previous,
                    status,
                    _timeProvider.GetUtcNow()));
        }

        return status;
    }

    private void SetDownloadedBytes(ProductUpdateChannel channel, long bytes)
    {
        lock (_stateGate)
        {
            var current = _statuses[channel];
            _statuses[channel] = current with { DownloadedBytes = bytes };
        }
    }

    private static void ValidateChannel(ProductUpdateChannel channel)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }
    }

    private static bool IsRecoverable(Exception exception, CancellationToken cancellationToken)
        => exception is not OutOfMemoryException and not StackOverflowException &&
           !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);

    private static int CompareSemanticVersions(string left, string right)
    {
        var leftVersion = SemanticVersion.Parse(left);
        var rightVersion = SemanticVersion.Parse(right);
        return leftVersion.CompareTo(rightVersion);
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4_096);
        if (stream.Length is < 1 || stream.Length > maximumBytes ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Update artifact has an invalid size or file type.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] ReadBoundedExact(string path, int expectedBytes)
    {
        var bytes = ReadBounded(path, expectedBytes);
        if (bytes.Length != expectedBytes)
        {
            throw new InvalidDataException("Update secret has an invalid size.");
        }

        return bytes;
    }

    private void ValidatePendingBinding(
        ProductUpdatePendingActivation pending,
        VerifiedCandidate candidate)
    {
        var manifestHash = Convert.ToHexString(SHA256.HashData(candidate.ManifestBytes));
        if (!string.Equals(pending.ManifestSha256, manifestHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                pending.PackageSha256,
                candidate.Manifest.Package.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pending.SigningKeyId, candidate.KeyDocument.KeyId, StringComparison.Ordinal) ||
            !string.Equals(
                pending.SigningPublicKeySha256,
                candidate.KeyDocument.SubjectPublicKeyInfoSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                pending.AllowedHostsSha256,
                ProductUpdatePendingActivationProtocol.HashAllowedHosts(_options.AllowedFeedHosts),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("Scheduled update trust binding changed before activation.");
        }
    }

    private static void ValidateReceiptBinding(
        ProductUpdatePendingActivation pending,
        ProductUpdateActivationReceipt receipt)
    {
        if (receipt.OperationId != pending.OperationId ||
            receipt.RequestId != pending.OperationId ||
            !string.Equals(receipt.TargetVersion, pending.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Activation receipt does not match the pending operation.");
        }
    }

    private void CompletePendingActivation(
        ProductUpdatePendingActivation pending,
        ProductUpdateActivationReceipt receipt)
    {
        // Receipt validation above is the durable ACK boundary. Delete pending first so a crash
        // during best-effort request cleanup cannot relaunch a terminal operation.
        File.Delete(PendingPath);
        try
        {
            ProductUpdateActivationRequestProtocol.DeleteCompletedArtifacts(
                _layout.Updates,
                receipt.RequestId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The durable receipt remains available for diagnosis/retention cleanup. Failure to
            // remove obsolete handoff files must not turn a completed activation back into Applying.
        }

        if (receipt.Outcome == ProductUpdateActivationReceiptOutcome.Committed)
        {
            SetStatus(pending.Channel, GetStatus(pending.Channel) with
            {
                Phase = ProductUpdatePhase.Idle,
                AvailableVersion = null,
                ScheduledForUtc = null,
                ErrorCode = null,
                Message = "Signed update activation completed successfully.",
            });
            return;
        }

        SetStatus(pending.Channel, GetStatus(pending.Channel) with
        {
            Phase = ProductUpdatePhase.Failed,
            ScheduledForUtc = null,
            ErrorCode = "update.activation_rolled_back",
            Message = "Update activation rolled back to the previous healthy version.",
        });
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        if (Directory.Exists(path) ||
            (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
        {
            throw new IOException("Update state path is not a regular file.");
        }

        var temporary = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4_096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static void RejectExistingReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Update paths must not traverse a reparse point.");
            }
        }
    }

    private string PackagesRoot => Path.Combine(_layout.Updates, "packages");

    private string GetPackagePath(string version) => Path.Combine(PackagesRoot, version + ".zip");

    private string PendingPath => Path.Combine(
        _layout.Updates,
        ProductUpdatePendingActivationProtocol.FileName);

    private sealed record VerifiedCandidate(
        ProductUpdateManifest Manifest,
        byte[] ManifestBytes,
        byte[] Signature,
        ProductUpdatePublicKeyDocument KeyDocument);

    private readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? Prerelease)
        : IComparable<SemanticVersion>
    {
        public static SemanticVersion Parse(string value)
        {
            ProductUpdateManifestParser.ValidateVersion(value);
            var components = value.Split('-', 2);
            var numbers = components[0].Split('.');
            return new SemanticVersion(
                int.Parse(numbers[0], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(numbers[1], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(numbers[2], System.Globalization.CultureInfo.InvariantCulture),
                components.Length == 2 ? components[1] : null);
        }

        public int CompareTo(SemanticVersion other)
        {
            var comparison = Major.CompareTo(other.Major);
            if (comparison == 0) comparison = Minor.CompareTo(other.Minor);
            if (comparison == 0) comparison = Patch.CompareTo(other.Patch);
            if (comparison != 0) return comparison;
            if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
            if (other.Prerelease is null) return -1;
            return StringComparer.Ordinal.Compare(Prerelease, other.Prerelease);
        }
    }
}

public sealed class ProductUpdateSchedulerHostedService(
    IProductUpdateCoordinator coordinator,
    ILogger<ProductUpdateSchedulerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ = await coordinator.LaunchDueActivationAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                logger.LogError(exception, "Scheduled product update activation failed closed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }
    }
}

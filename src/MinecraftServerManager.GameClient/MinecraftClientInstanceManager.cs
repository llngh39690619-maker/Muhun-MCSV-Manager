using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>Coordinates release validation, staging installation, atomic promotion and registry commit.</summary>
public sealed class MinecraftClientInstanceManager
{
    internal const int MaximumStagingCleanupCandidates = 256;
    internal static readonly TimeSpan StaleStagingAge = TimeSpan.FromHours(24);
    private readonly string _instancesDirectory;
    private readonly string _stagingDirectory;
    private readonly MinecraftClientRegistry _registry;
    private readonly IMinecraftReleaseCatalog _releaseCatalog;
    private readonly IMinecraftClientPayloadInstaller _payloadInstaller;
    private readonly MinecraftClientProcessRecoveryService _processRecoveryService = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public MinecraftClientInstanceManager(
        string instancesDirectory,
        string stagingDirectory,
        MinecraftClientRegistry registry,
        IMinecraftReleaseCatalog releaseCatalog,
        IMinecraftClientPayloadInstaller payloadInstaller)
    {
        _instancesDirectory = NormalizeRoot(instancesDirectory, nameof(instancesDirectory));
        _stagingDirectory = NormalizeRoot(stagingDirectory, nameof(stagingDirectory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _releaseCatalog = releaseCatalog ?? throw new ArgumentNullException(nameof(releaseCatalog));
        _payloadInstaller = payloadInstaller ?? throw new ArgumentNullException(nameof(payloadInstaller));
        Directory.CreateDirectory(_instancesDirectory);
        Directory.CreateDirectory(_stagingDirectory);
        RejectReparsePoint(_instancesDirectory);
        RejectReparsePoint(_stagingDirectory);
        ScavengeStaleStagingDirectories();
    }

    public async Task<MinecraftClientInstallResult> InstallAsync(
        MinecraftClientInstallRequest request,
        string? javaExecutablePath,
        IProgress<MinecraftClientInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var catalog = await _releaseCatalog.GetStableReleasesAsync(cancellationToken).ConfigureAwait(false);
        if (!catalog.Releases.Any(release => string.Equals(release.Id, request.GameVersion, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Minecraft {request.GameVersion} is not in the official stable release catalog.");
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var finalDirectory = Path.Combine(_instancesDirectory, request.InstanceId.ToString("N"));
        var stagingDirectory = Path.Combine(_stagingDirectory, request.InstanceId.ToString("N"));
        try
        {
            RejectExistingPath(finalDirectory);
            RejectExistingPath(stagingDirectory);
            Directory.CreateDirectory(stagingDirectory);
            progress?.Report(new MinecraftClientInstallProgress("prepare", "正在建立隔離的客戶端安裝區…", 0d));

            string installedVersionId;
            try
            {
                installedVersionId = await _payloadInstaller.InstallAsync(
                        request,
                        stagingDirectory,
                        javaExecutablePath,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                RejectTreeReparsePoints(stagingDirectory);
                Directory.Move(stagingDirectory, finalDirectory);
            }
            catch
            {
                TryDeleteOwnedDirectory(_stagingDirectory, stagingDirectory);
                throw;
            }

            var instance = CreateInstance(request, finalDirectory, installedVersionId, javaExecutablePath);
            try
            {
                await _registry.UpdateAsync(
                        document =>
                        {
                            if (document.Instances.Any(item => item.Id == instance.Id))
                            {
                                throw new InvalidOperationException(
                                    "A client instance with the same id already exists.");
                            }

                            document.Instances.Add(instance);
                            return true;
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                TryDeleteOwnedDirectory(_instancesDirectory, finalDirectory);
                throw;
            }

            progress?.Report(new MinecraftClientInstallProgress("complete", "客戶端已建立並加入 X MCSV。", 1d));
            return new MinecraftClientInstallResult(instance, installedVersionId);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Adds a managed loader to an existing, stopped instance without modifying the live tree
    /// until a complete replacement has been prepared and verified. The original instance is
    /// retained as a same-volume rollback tombstone until the registry transaction succeeds.
    /// </summary>
    public async Task<MinecraftClientInstallResult> SwitchLoaderAsync(
        Guid instanceId,
        MinecraftClientLoader loader,
        string loaderVersion,
        string? javaExecutablePath,
        IProgress<MinecraftClientInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (instanceId == Guid.Empty)
        {
            throw new ArgumentException("The client instance id is invalid.", nameof(instanceId));
        }

        if (loader is not (MinecraftClientLoader.Forge or MinecraftClientLoader.Fabric or
            MinecraftClientLoader.Quilt or MinecraftClientLoader.NeoForge))
        {
            throw new NotSupportedException(
                $"{loader} cannot be selected by the managed mod-loader switcher.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(loaderVersion);
        try
        {
            OfficialCatalogValidation.ValidateVersionToken(
                loaderVersion,
                nameof(loaderVersion),
                maximumLength: 128);
        }
        catch (InvalidDataException error)
        {
            throw new ArgumentException("The loader version is invalid.", nameof(loaderVersion), error);
        }

        if (!string.IsNullOrWhiteSpace(javaExecutablePath) &&
            (!Path.IsPathFullyQualified(javaExecutablePath) || !File.Exists(javaExecutablePath)))
        {
            throw new FileNotFoundException(
                "The selected Java executable does not exist.",
                javaExecutablePath);
        }

        var catalog = await _releaseCatalog.GetStableReleasesAsync(cancellationToken)
            .ConfigureAwait(false);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? preparedDirectory = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(_instancesDirectory);
            RejectReparsePoint(_stagingDirectory);

            var expectedDirectory = GetManagedInstanceDirectory(instanceId);
            var snapshot = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
            var original = snapshot.Instances.FirstOrDefault(item => item.Id == instanceId)
                           ?? throw new InvalidDataException(
                               "The Minecraft client instance is missing from the registry.");
            ValidateManagedInstancePath(original, expectedDirectory);
            RejectMissingOrRedirectedInstanceDirectory(expectedDirectory);
            RejectRunningInstance(original);
            if (original.Edition != MinecraftClientEdition.Java)
            {
                throw new NotSupportedException(
                    "Managed loader switching only supports Minecraft Java Edition instances.");
            }

            if (!catalog.Releases.Any(release =>
                    string.Equals(release.Id, original.GameVersion, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Minecraft {original.GameVersion} is not in the official stable release catalog.");
            }

            if (original.Loader == loader &&
                original.LoaderInstallKind == MinecraftClientLoaderInstallKind.Managed &&
                !string.IsNullOrWhiteSpace(original.LoaderVersion))
            {
                return new MinecraftClientInstallResult(original, original.InstalledVersionId);
            }

            if (original.CatalogProvider?.Equals("ftb", StringComparison.Ordinal) == true)
            {
                throw new NotSupportedException(
                    "FTB modpack instances must keep the Minecraft and mod-loader versions declared by their official pack manifest. Use the mod's official project download for this instance or create a separate client instance.");
            }

            EnsureSameVolume(expectedDirectory, _stagingDirectory);
            preparedDirectory = CreateLoaderSwitchPath("work");
            RejectExistingPath(preparedDirectory);
            Directory.CreateDirectory(preparedDirectory);
            progress?.Report(new MinecraftClientInstallProgress(
                "loader-prepare",
                "Preparing the compatible mod loader in isolation…",
                0d));

            var request = CreateLoaderSwitchRequest(original, loader, loaderVersion);
            var installedVersionId = await _payloadInstaller.InstallAsync(
                    request,
                    preparedDirectory,
                    javaExecutablePath,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(installedVersionId) || installedVersionId.Length > 192)
            {
                throw new InvalidDataException(
                    "The client loader installer returned an invalid launch profile id.");
            }

            // Overlay non-conflicting files only after the fresh loader profile has been verified.
            // This preserves saves/configuration while preventing stale or tampered managed files
            // from replacing the newly installed profile and libraries.
            progress?.Report(new MinecraftClientInstallProgress(
                "loader-copy",
                "Preserving the existing client data…",
                0.9d));
            MergeInstanceTree(expectedDirectory, preparedDirectory, cancellationToken);
            RejectTreeReparsePoints(preparedDirectory);
            cancellationToken.ThrowIfCancellationRequested();

            // Re-read immediately before promotion. A concurrent launch or settings mutation must
            // fail closed rather than replacing a now-active or changed instance.
            var currentDocument = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
            var current = currentDocument.Instances.FirstOrDefault(item => item.Id == instanceId)
                          ?? throw new InvalidDataException(
                              "The Minecraft client instance is missing from the registry.");
            ValidateManagedInstancePath(current, expectedDirectory);
            RejectRunningInstance(current);
            EnsureLoaderSwitchSourceUnchanged(original, current);

            var rollbackDirectory = CreateLoaderSwitchPath("rollback");
            RejectExistingPath(rollbackDirectory);
            progress?.Report(new MinecraftClientInstallProgress(
                "loader-commit",
                "Safely activating the compatible mod loader…",
                0.95d));
            var updated = await CommitStagedLoaderSwitchAsync(
                    _instancesDirectory,
                    expectedDirectory,
                    _stagingDirectory,
                    preparedDirectory,
                    rollbackDirectory,
                    async () => await _registry.UpdateAsync(
                            document =>
                            {
                                var stored = document.Instances.FirstOrDefault(
                                                 item => item.Id == instanceId)
                                             ?? throw new InvalidDataException(
                                                 "The Minecraft client instance is missing from the registry.");
                                ValidateManagedInstancePath(stored, expectedDirectory);
                                RejectRunningInstance(stored);
                                EnsureLoaderSwitchSourceUnchanged(original, stored);
                                stored.Loader = loader;
                                stored.LoaderVersion = loaderVersion;
                                stored.LoaderInstallKind = MinecraftClientLoaderInstallKind.Managed;
                                stored.InstalledVersionId = installedVersionId;
                                if (!string.IsNullOrWhiteSpace(javaExecutablePath))
                                {
                                    stored.JavaExecutablePath = Path.GetFullPath(javaExecutablePath);
                                }

                                return stored;
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false))
                .ConfigureAwait(false);
            preparedDirectory = null;
            progress?.Report(new MinecraftClientInstallProgress(
                "loader-complete",
                $"{loader} is ready for Minecraft {updated.GameVersion}.",
                1d));
            return new MinecraftClientInstallResult(updated, installedVersionId);
        }
        finally
        {
            if (preparedDirectory is not null)
            {
                TryDeleteOwnedDirectory(_stagingDirectory, preparedDirectory);
            }

            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Removes one managed, stopped client instance. The directory is first renamed to a unique
    /// tombstone on the same volume, then the registry transaction is committed. A failed registry
    /// commit restores the original directory; committed tombstones are cleaned without following
    /// reparse points.
    /// </summary>
    public async Task<MinecraftClientInstance> DeleteAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        if (instanceId == Guid.Empty)
        {
            throw new ArgumentException("The client instance id is invalid.", nameof(instanceId));
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(_instancesDirectory);
            RejectReparsePoint(_stagingDirectory);

            var expectedDirectory = GetManagedInstanceDirectory(instanceId);
            var snapshot = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
            var instance = snapshot.Instances.FirstOrDefault(item => item.Id == instanceId)
                           ?? throw new InvalidDataException(
                               "The Minecraft client instance is missing from the registry.");
            ValidateManagedInstancePath(instance, expectedDirectory);
            RejectMissingOrRedirectedInstanceDirectory(expectedDirectory);
            RejectRunningInstance(instance);
            EnsureSameVolume(expectedDirectory, _stagingDirectory);

            cancellationToken.ThrowIfCancellationRequested();
            var tombstoneDirectory = CreateDeletionTombstonePath();
            return await CommitStagedDeletionAsync(
                    _instancesDirectory,
                    expectedDirectory,
                    _stagingDirectory,
                    tombstoneDirectory,
                    async () => await _registry.UpdateAsync(
                            document =>
                            {
                                var stored = document.Instances.FirstOrDefault(
                                                 item => item.Id == instanceId)
                                             ?? throw new InvalidDataException(
                                                 "The Minecraft client instance is missing from the registry.");
                                ValidateManagedInstancePath(stored, expectedDirectory);
                                RejectRunningInstance(stored);
                                document.Instances.Remove(stored);
                                return stored;
                            },
                            CancellationToken.None)
                        .ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    internal static async Task<TResult> CommitStagedDeletionAsync<TResult>(
        string instancesDirectory,
        string instanceDirectory,
        string stagingDirectory,
        string tombstoneDirectory,
        Func<Task<TResult>> commitRegistryAsync)
    {
        ArgumentNullException.ThrowIfNull(commitRegistryAsync);
        var normalizedInstances = NormalizeRoot(instancesDirectory, nameof(instancesDirectory));
        var normalizedStaging = NormalizeRoot(stagingDirectory, nameof(stagingDirectory));
        var normalizedInstance = SafePath.EnsureWithinRoot(
            normalizedInstances,
            instanceDirectory,
            allowRoot: false);
        var normalizedTombstone = SafePath.EnsureWithinRoot(
            normalizedStaging,
            tombstoneDirectory,
            allowRoot: false);
        RejectReparsePoint(normalizedInstances);
        RejectReparsePoint(normalizedStaging);
        RejectMissingOrRedirectedInstanceDirectory(normalizedInstance);
        RejectExistingPath(normalizedTombstone);
        EnsureSameVolume(normalizedInstance, normalizedStaging);

        Directory.Move(normalizedInstance, normalizedTombstone);
        try
        {
            TResult result;
            try
            {
                result = await commitRegistryAsync().ConfigureAwait(false);
            }
            catch (Exception commitError)
            {
                try
                {
                    if (Directory.Exists(normalizedInstance) || File.Exists(normalizedInstance))
                    {
                        throw new IOException(
                            $"Cannot roll back client deletion because the managed path was recreated: {normalizedInstance}");
                    }

                    RejectReparsePoint(normalizedStaging);
                    RejectReparsePoint(normalizedTombstone);
                    Directory.Move(normalizedTombstone, normalizedInstance);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        "The client registry commit failed and the instance directory could not be restored.",
                        commitError,
                        rollbackError);
                }

                throw;
            }

            TryDeleteOwnedDirectory(normalizedStaging, normalizedTombstone);
            return result;
        }
        catch
        {
            // A successfully restored directory no longer has a tombstone. Any rollback failure
            // deliberately leaves the tombstone for forensic recovery instead of deleting data.
            throw;
        }
    }

    internal static async Task<TResult> CommitStagedLoaderSwitchAsync<TResult>(
        string instancesDirectory,
        string instanceDirectory,
        string stagingDirectory,
        string preparedDirectory,
        string rollbackDirectory,
        Func<Task<TResult>> commitRegistryAsync)
    {
        ArgumentNullException.ThrowIfNull(commitRegistryAsync);
        var normalizedInstances = NormalizeRoot(instancesDirectory, nameof(instancesDirectory));
        var normalizedStaging = NormalizeRoot(stagingDirectory, nameof(stagingDirectory));
        var normalizedInstance = SafePath.EnsureWithinRoot(
            normalizedInstances,
            instanceDirectory,
            allowRoot: false);
        var normalizedPrepared = SafePath.EnsureWithinRoot(
            normalizedStaging,
            preparedDirectory,
            allowRoot: false);
        var normalizedRollback = SafePath.EnsureWithinRoot(
            normalizedStaging,
            rollbackDirectory,
            allowRoot: false);
        RejectReparsePoint(normalizedInstances);
        RejectReparsePoint(normalizedStaging);
        RejectMissingOrRedirectedInstanceDirectory(normalizedInstance);
        RejectMissingOrRedirectedInstanceDirectory(normalizedPrepared);
        RejectExistingPath(normalizedRollback);
        RejectTreeReparsePoints(normalizedInstance);
        RejectTreeReparsePoints(normalizedPrepared);
        EnsureSameVolume(normalizedInstance, normalizedStaging);

        Directory.Move(normalizedInstance, normalizedRollback);
        try
        {
            try
            {
                Directory.Move(normalizedPrepared, normalizedInstance);
            }
            catch (Exception promotionError)
            {
                try
                {
                    Directory.Move(normalizedRollback, normalizedInstance);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        "The prepared loader could not be activated and the original instance could not be restored.",
                        promotionError,
                        rollbackError);
                }

                throw;
            }

            TResult result;
            try
            {
                result = await commitRegistryAsync().ConfigureAwait(false);
            }
            catch (Exception commitError)
            {
                try
                {
                    if (Directory.Exists(normalizedPrepared) || File.Exists(normalizedPrepared))
                    {
                        throw new IOException(
                            $"Cannot roll back the loader switch because its staging path was recreated: {normalizedPrepared}");
                    }

                    Directory.Move(normalizedInstance, normalizedPrepared);
                    Directory.Move(normalizedRollback, normalizedInstance);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        "The loader registry commit failed and the original instance could not be restored.",
                        commitError,
                        rollbackError);
                }

                throw;
            }

            TryDeleteOwnedDirectory(normalizedStaging, normalizedRollback);
            return result;
        }
        catch
        {
            // Prepared output and rollback tombstones are deliberately retained if restoration is
            // incomplete. The caller only cleans the prepared copy after the original path exists.
            throw;
        }
    }

    private static MinecraftClientInstance CreateInstance(
        MinecraftClientInstallRequest request,
        string finalDirectory,
        string installedVersionId,
        string? javaExecutablePath) =>
        new()
        {
            Id = request.InstanceId,
            Name = request.Name.Trim(),
            Edition = request.Edition,
            DirectoryPath = finalDirectory,
            GameVersion = request.GameVersion,
            InstalledVersionId = installedVersionId,
            Loader = request.Loader,
            LoaderVersion = request.LoaderVersion,
            LoaderInstallKind = MinecraftClientLoaderInstallKind.Managed,
            JavaExecutablePath = javaExecutablePath,
            JavaMajorVersion = request.JavaMajorVersion,
            MemoryMode = request.MemoryMode,
            MinimumMemoryMb = request.MinimumMemoryMb,
            MaximumMemoryMb = request.MaximumMemoryMb,
            WindowWidth = request.WindowWidth,
            WindowHeight = request.WindowHeight,
            FullScreen = request.FullScreen,
            EnableQuickLaunch = request.EnableQuickLaunch,
            HideLauncherAfterGameStarts = request.HideLauncherAfterGameStarts,
            ShowGameLog = request.ShowGameLog,
            EnableDedicatedGpu = request.EnableDedicatedGpu,
            EnableDiscordPresence = request.EnableDiscordPresence,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static MinecraftClientInstallRequest CreateLoaderSwitchRequest(
        MinecraftClientInstance source,
        MinecraftClientLoader loader,
        string loaderVersion) =>
        new(
            source.Id,
            source.Name,
            source.Edition,
            source.GameVersion,
            loader,
            loaderVersion,
            source.MemoryMode,
            source.MinimumMemoryMb,
            source.MaximumMemoryMb,
            source.WindowWidth,
            source.WindowHeight,
            source.FullScreen,
            source.EnableQuickLaunch,
            source.HideLauncherAfterGameStarts,
            source.ShowGameLog,
            source.EnableDedicatedGpu,
            source.EnableDiscordPresence,
            source.JavaMajorVersion);

    private static void EnsureLoaderSwitchSourceUnchanged(
        MinecraftClientInstance expected,
        MinecraftClientInstance actual)
    {
        if (expected.Id != actual.Id ||
            expected.Edition != actual.Edition ||
            !string.Equals(expected.GameVersion, actual.GameVersion, StringComparison.Ordinal) ||
            expected.Loader != actual.Loader ||
            !string.Equals(expected.LoaderVersion, actual.LoaderVersion, StringComparison.Ordinal) ||
            expected.LoaderInstallKind != actual.LoaderInstallKind ||
            !string.Equals(
                expected.InstalledVersionId,
                actual.InstalledVersionId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Minecraft client instance changed while its loader was being prepared.");
        }
    }

    private static void ValidateRequest(MinecraftClientInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InstanceId == Guid.Empty || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128)
        {
            throw new ArgumentException("The client instance id or name is invalid.", nameof(request));
        }

        if (request.Edition != MinecraftClientEdition.Java)
        {
            throw new NotSupportedException("Managed creation currently supports Minecraft Java Edition only.");
        }

        if (string.IsNullOrWhiteSpace(request.GameVersion) || request.GameVersion.Length > 64 ||
            request.GameVersion.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException("The Minecraft release id is invalid.", nameof(request));
        }

        if (request.Loader is MinecraftClientLoader.OptiFine or MinecraftClientLoader.LabyMod)
        {
            throw new NotSupportedException(
                $"{request.Loader} is an external client extension and cannot use managed creation.");
        }

        if (request.MinimumMemoryMb is < 512 or > 262_144 ||
            request.MaximumMemoryMb < request.MinimumMemoryMb || request.MaximumMemoryMb > 262_144 ||
            request.WindowWidth is < 640 or > 16_384 || request.WindowHeight is < 360 or > 16_384)
        {
            throw new ArgumentException("The client memory or resolution is outside the safe range.", nameof(request));
        }

        if (request.JavaMajorVersion is < 8 or > 99)
        {
            throw new ArgumentException("The Java major version is outside the safe range.", nameof(request));
        }
    }

    private static string NormalizeRoot(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private string GetManagedInstanceDirectory(Guid instanceId) =>
        Path.Combine(_instancesDirectory, instanceId.ToString("N"));

    private string CreateDeletionTombstonePath()
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Path.Combine(
                _stagingDirectory,
                $"delete-{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Unable to allocate a unique client deletion tombstone.");
    }

    private string CreateLoaderSwitchPath(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Path.Combine(
                _stagingDirectory,
                $"loader-switch-{role}-{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Unable to allocate a unique loader-switch staging directory.");
    }

    private static void MergeInstanceTree(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        RejectMissingOrRedirectedInstanceDirectory(sourceRoot);
        RejectTreeReparsePoints(sourceRoot);
        RejectMissingOrRedirectedInstanceDirectory(destinationRoot);
        RejectTreeReparsePoints(destinationRoot);

        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((sourceRoot, destinationRoot));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (source, destination) = pending.Pop();
            RejectReparsePoint(source);
            RejectReparsePoint(destination);

            foreach (var directory in Directory.EnumerateDirectories(
                         source,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectReparsePoint(directory);
                var target = SafePath.CombineUnderRoot(
                    destinationRoot,
                    Path.GetRelativePath(sourceRoot, directory));
                if (File.Exists(target))
                {
                    throw new IOException(
                        $"Cannot preserve a client directory because a verified loader file uses the same path: {target}");
                }

                if (!Directory.Exists(target))
                {
                    Directory.CreateDirectory(target);
                }

                RejectReparsePoint(target);
                pending.Push((directory, target));
            }

            foreach (var file in Directory.EnumerateFiles(
                         source,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectReparsePoint(file);
                var target = SafePath.CombineUnderRoot(
                    destinationRoot,
                    Path.GetRelativePath(sourceRoot, file));
                if (Directory.Exists(target))
                {
                    throw new IOException(
                        $"Cannot preserve a client file because a verified loader directory uses the same path: {target}");
                }

                if (File.Exists(target))
                {
                    RejectReparsePoint(target);
                    continue;
                }

                File.Copy(file, target, overwrite: false);
            }
        }
    }

    private static void ValidateManagedInstancePath(
        MinecraftClientInstance instance,
        string expectedDirectory)
    {
        string actualDirectory;
        try
        {
            actualDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(instance.DirectoryPath));
        }
        catch (Exception error) when (
            error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(
                "The client registry contains an invalid instance directory.",
                error);
        }

        if (!string.Equals(
                actualDirectory,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedDirectory)),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "Only the exact managed instances/<idN> directory may be deleted.");
        }
    }

    private void RejectRunningInstance(MinecraftClientInstance instance)
    {
        if (_processRecoveryService.IsMatchingProcessActive(instance))
        {
            throw new InvalidOperationException(
                "A running Minecraft client instance cannot be deleted.");
        }
    }

    private static void RejectMissingOrRedirectedInstanceDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(
                $"The managed client instance directory does not exist: {path}");
        }

        RejectReparsePoint(path);
    }

    private static void EnsureSameVolume(string firstPath, string secondPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var firstVolume = SafePath.GetExistingObjectIdentity(firstPath).VolumeSerialNumber;
            var secondVolume = SafePath.GetExistingObjectIdentity(secondPath).VolumeSerialNumber;
            if (firstVolume != secondVolume)
            {
                throw new IOException(
                    "Client deletion staging must be on the same volume as the managed instance.");
            }

            return;
        }

        if (!string.Equals(
                Path.GetPathRoot(Path.GetFullPath(firstPath)),
                Path.GetPathRoot(Path.GetFullPath(secondPath)),
                StringComparison.Ordinal))
        {
            throw new IOException(
                "Client deletion staging must be on the same volume as the managed instance.");
        }
    }

    private static void RejectExistingPath(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new IOException($"Client installation path already exists: {path}");
        }
    }

    private static void RejectTreeReparsePoints(string root)
    {
        RejectReparsePoint(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            RejectReparsePoint(path);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Client installation paths must not contain links or reparse points.");
        }
    }

    private void ScavengeStaleStagingDirectories()
    {
        var cutoff = DateTime.UtcNow - StaleStagingAge;
        var inspected = 0;
        try
        {
            foreach (var candidate in new DirectoryInfo(_stagingDirectory)
                         .EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
            {
                if (inspected++ >= MaximumStagingCleanupCandidates)
                {
                    break;
                }

                var isInstallStaging = Guid.TryParseExact(candidate.Name, "N", out _);
                var isDeletionTombstone = TryParseDeletionTombstoneCreatedAtUtc(
                    candidate.Name,
                    out var tombstoneCreatedAtUtc);
                if ((!isInstallStaging && !isDeletionTombstone) ||
                    candidate.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    (isDeletionTombstone ? tombstoneCreatedAtUtc : candidate.LastWriteTimeUtc) >= cutoff)
                {
                    continue;
                }

                TryDeleteOwnedDirectory(_stagingDirectory, candidate.FullName);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Staging cleanup is best effort and must never prevent the client workspace opening.
        }
    }

    private static bool TryParseDeletionTombstoneCreatedAtUtc(
        string name,
        out DateTime createdAtUtc)
    {
        const string prefix = "delete-";
        const int ticksLength = 19;
        createdAtUtc = default;
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            name.Length != prefix.Length + ticksLength + 1 + 32 ||
            name[prefix.Length + ticksLength] != '-' ||
            !long.TryParse(
                name.AsSpan(prefix.Length, ticksLength),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var ticks) ||
            !Guid.TryParseExact(name[(prefix.Length + ticksLength + 1)..], "N", out _))
        {
            return false;
        }

        try
        {
            createdAtUtc = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static void TryDeleteOwnedDirectory(string trustedParent, string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                SafePath.DeleteTreeWithoutFollowingReparsePoints(trustedParent, path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}

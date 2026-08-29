using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Installs one official public stable FTB client pack into an isolated staging tree, verifies
/// every declared file, promotes the complete payload atomically, then commits the registry last.
/// </summary>
public sealed class FtbMinecraftClientPackInstaller
{
    // Must stay aligned with the public catalogue parser: featured FTB releases currently exceed
    // 11,000 entries, while 20,000 remains a bounded defence against oversized manifests.
    private const int MaximumManifestFiles = 20_000;
    private const int MaximumDownloadConcurrency = 16;
    private const long MaximumManifestFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumManifestTotalBytes = 16L * 1024 * 1024 * 1024;
    private const long MaximumCatalogArtworkBytes = 5L * 1024 * 1024;
    private const int MaximumPendingPromotionReceipts = 1_024;
    private const long MaximumPromotionReceiptBytes = 16L * 1024;
    private const int PromotionReceiptSchemaVersion = 1;
    private const string PromotionReceiptPrefix = ".ftb-client-promotion-";
    private const string PromotionReceiptSuffix = ".json";
    private static readonly OfficialFtbClientDownloadUriPolicy DownloadUriPolicy = new();
    private static readonly JsonSerializerOptions PromotionReceiptJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly string _instancesRoot;
    private readonly string _stagingRoot;
    private readonly MinecraftClientRegistry _registry;
    private readonly IMinecraftReleaseCatalog _releaseCatalog;
    private readonly IMinecraftClientPayloadInstaller _payloadInstaller;
    private readonly IFtbClientPackCatalog _catalog;
    private readonly ModrinthModpackArtifactDownloader _downloader;
    private readonly Func<
        string,
        string,
        SafePathObjectIdentity,
        CancellationToken,
        Task> _deleteOwnedTreeAsync;
    private readonly Action<string>? _afterPromotionBeforeLeaseForTesting;
    private readonly Action<string>? _duringCommittedFinalizationForTesting;
    private readonly Action<string>? _duringRegisteredRecoveryForTesting;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public FtbMinecraftClientPackInstaller(
        string instancesDirectory,
        string stagingDirectory,
        MinecraftClientRegistry registry,
        IMinecraftReleaseCatalog releaseCatalog,
        IMinecraftClientPayloadInstaller payloadInstaller,
        IFtbClientPackCatalog catalog,
        HttpClient artifactHttpClient)
        : this(
            instancesDirectory,
            stagingDirectory,
            registry,
            releaseCatalog,
            payloadInstaller,
            catalog,
            artifactHttpClient,
            DeleteOwnedTreeWithRetryAsync,
            afterPromotionBeforeLeaseForTesting: null,
            duringCommittedFinalizationForTesting: null,
            duringRegisteredRecoveryForTesting: null)
    {
    }

    internal FtbMinecraftClientPackInstaller(
        string instancesDirectory,
        string stagingDirectory,
        MinecraftClientRegistry registry,
        IMinecraftReleaseCatalog releaseCatalog,
        IMinecraftClientPayloadInstaller payloadInstaller,
        IFtbClientPackCatalog catalog,
        HttpClient artifactHttpClient,
        Func<string, string, SafePathObjectIdentity, CancellationToken, Task> deleteOwnedTreeAsync,
        Action<string>? afterPromotionBeforeLeaseForTesting = null,
        Action<string>? duringCommittedFinalizationForTesting = null,
        Action<string>? duringRegisteredRecoveryForTesting = null)
    {
        _instancesRoot = NormalizeRoot(instancesDirectory, nameof(instancesDirectory));
        _stagingRoot = NormalizeRoot(stagingDirectory, nameof(stagingDirectory));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _releaseCatalog = releaseCatalog ?? throw new ArgumentNullException(nameof(releaseCatalog));
        _payloadInstaller = payloadInstaller ?? throw new ArgumentNullException(nameof(payloadInstaller));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentNullException.ThrowIfNull(artifactHttpClient);
        _deleteOwnedTreeAsync = deleteOwnedTreeAsync ??
                                throw new ArgumentNullException(nameof(deleteOwnedTreeAsync));
        _afterPromotionBeforeLeaseForTesting = afterPromotionBeforeLeaseForTesting;
        _duringCommittedFinalizationForTesting = duringCommittedFinalizationForTesting;
        _duringRegisteredRecoveryForTesting = duringRegisteredRecoveryForTesting;
        _downloader = new ModrinthModpackArtifactDownloader(
            new HttpClientModrinthModpackHttpTransport(artifactHttpClient),
            DownloadUriPolicy,
            maxRedirects: 1);

        Directory.CreateDirectory(_instancesRoot);
        Directory.CreateDirectory(_stagingRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(_instancesRoot, _instancesRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(_stagingRoot, _stagingRoot);
    }

    /// <summary>
    /// Reconciles durable promotion receipts left by process loss or an incomplete rollback. A
    /// final tree is removed only when the registry does not own its id/path and its stable Windows
    /// filesystem identity still matches the receipt captured before promotion.
    /// </summary>
    public async Task RecoverPendingPromotionsAsync(
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecoverPendingPromotionsCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<FtbClientPackInstallResult> InstallAsync(
        FtbClientPackInstallRequest request,
        string? javaExecutablePath,
        IProgress<FtbClientPackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var packTask = _catalog.GetPackAsync(request.PackId, cancellationToken);
        var manifestTask = _catalog.GetVersionManifestAsync(
            request.PackId,
            request.VersionId,
            cancellationToken);
        await Task.WhenAll(packTask, manifestTask).ConfigureAwait(false);
        var pack = await packTask.ConfigureAwait(false);
        var manifest = await manifestTask.ConfigureAwait(false);
        var selection = ValidateCatalogSelection(request, pack, manifest);
        var effectiveMinimumMemoryMb = request.MemoryMode == MinecraftClientMemoryMode.Manual
            ? request.MinimumMemoryMb
            : Math.Max(request.MinimumMemoryMb, manifest.Memory.MinimumMb);
        var effectiveMaximumMemoryMb = request.MemoryMode == MinecraftClientMemoryMode.Manual
            ? request.MaximumMemoryMb
            : Math.Max(request.MaximumMemoryMb, manifest.Memory.RecommendedMb);

        var releases = await _releaseCatalog.GetStableReleasesAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!releases.Releases.Any(release =>
                string.Equals(release.Id, selection.GameVersion, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Minecraft {selection.GameVersion} is not an official stable release.");
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var operationRoot = SafePath.CombineUnderRoot(_stagingRoot, request.InstanceId.ToString("N"));
        var payloadRoot = SafePath.CombineUnderRoot(operationRoot, "payload");
        var finalRoot = SafePath.CombineUnderRoot(_instancesRoot, request.InstanceId.ToString("N"));
        var operationCreated = false;
        var registryCommitted = false;
        var rollbackPermitted = true;
        SafePathObjectIdentity? operationIdentity = null;
        PromotionReceipt? promotionReceipt = null;
        try
        {
            await RecoverPendingPromotionsCoreAsync(cancellationToken).ConfigureAwait(false);
            RejectExistingPath(operationRoot);
            RejectExistingPath(finalRoot);
            Directory.CreateDirectory(operationRoot);
            operationCreated = true;
            operationIdentity = SafePath.GetExistingObjectIdentity(operationRoot);
            Directory.CreateDirectory(payloadRoot);
            SafePath.EnsureNoReparsePointsUnderRoot(_stagingRoot, payloadRoot);

            var effectiveJavaMajor = selection.JavaMajorVersion ?? request.JavaMajorVersion;
            var clientRequest = new MinecraftClientInstallRequest(
                request.InstanceId,
                request.Name.Trim(),
                MinecraftClientEdition.Java,
                selection.GameVersion,
                selection.Loader,
                selection.LoaderVersion,
                request.MemoryMode,
                effectiveMinimumMemoryMb,
                effectiveMaximumMemoryMb,
                request.WindowWidth,
                request.WindowHeight,
                request.FullScreen,
                request.EnableQuickLaunch,
                request.HideLauncherAfterGameStarts,
                request.ShowGameLog,
                request.EnableDedicatedGpu,
                request.EnableDiscordPresence,
                effectiveJavaMajor);
            var gameProgress = new InlineProgress<MinecraftClientInstallProgress>(value =>
                progress?.Report(new FtbClientPackInstallProgress(
                    "install-game",
                    value.Message,
                    Fraction: value.Fraction)));
            progress?.Report(new FtbClientPackInstallProgress(
                "install-game",
                $"正在建立 Minecraft {selection.GameVersion} 與相符模組載入器…"));
            var installedVersionId = await _payloadInstaller.InstallAsync(
                    clientRequest,
                    payloadRoot,
                    javaExecutablePath,
                    gameProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateInstalledVersionId(installedVersionId);

            var selectedFiles = selection.ManifestFiles
                .Where(static file => !file.ServerOnly)
                .ToArray();
            var installedPaths = await DownloadPackFilesAsync(
                    selectedFiles,
                    payloadRoot,
                    request.MaximumConcurrentDownloads,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            var catalogIconRelativePath = CopyArtworkIntoOwnedPayload(
                request.CatalogIconImagePath,
                payloadRoot,
                "catalog-icon",
                cancellationToken);
            var catalogPreviewRelativePath = CopyArtworkIntoOwnedPayload(
                request.CatalogPreviewImagePath,
                payloadRoot,
                "catalog-preview",
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            SafePath.EnsureTreeContainsNoReparsePoints(payloadRoot);
            var payloadIdentity = SafePath.GetExistingObjectIdentity(payloadRoot);
            promotionReceipt = CreatePromotionReceipt(
                request.InstanceId,
                finalRoot,
                operationRoot,
                payloadIdentity,
                operationIdentity.Value);
            await WritePromotionReceiptAsync(promotionReceipt, CancellationToken.None)
                .ConfigureAwait(false);
            Directory.Move(payloadRoot, finalRoot);
            _afterPromotionBeforeLeaseForTesting?.Invoke(finalRoot);
            using (SafePath.AcquireNoReparseDirectoryChainLease(_instancesRoot, finalRoot))
            {
                ValidatePromotedFinalTree(finalRoot, payloadIdentity, "promoted FTB final tree");

                var instance = CreateInstance(
                    request,
                    pack,
                    manifest,
                    selection,
                    effectiveMinimumMemoryMb,
                    effectiveMaximumMemoryMb,
                    finalRoot,
                    installedVersionId,
                    javaExecutablePath,
                    ResolveOwnedArtworkPath(finalRoot, catalogIconRelativePath),
                    ResolveOwnedArtworkPath(finalRoot, catalogPreviewRelativePath));

                // Scan again immediately before the durable registry mutation. The retained
                // no-follow chain lease prevents the verified final root from being renamed or
                // replaced for the entire commit boundary.
                ValidatePromotedFinalTree(finalRoot, payloadIdentity, "pre-commit FTB final tree");
                try
                {
                    await CommitRegistryInstanceAsync(instance).ConfigureAwait(false);
                    registryCommitted = true;
                }
                catch (Exception commitError)
                {
                    try
                    {
                        var commitState = await ReadRegistryCommitStateAsync(instance)
                            .ConfigureAwait(false);
                        if (commitState == RegistryCommitState.Committed)
                        {
                            registryCommitted = true;
                            Debug.WriteLine(
                                $"FTB registry commit completed despite a caller-visible error: {commitError}");
                        }
                        else
                        {
                            ExceptionDispatchInfo.Capture(commitError).Throw();
                        }
                    }
                    catch (Exception verificationError) when (!ReferenceEquals(
                               verificationError,
                               commitError))
                    {
                        rollbackPermitted = false;
                        throw new AggregateException(
                            "The FTB registry commit outcome could not be proven; the durable promotion receipt was retained for recovery.",
                            commitError,
                            verificationError);
                    }
                }

                try
                {
                    // A child junction can be inserted without replacing the leased root. Verify
                    // once more after the atomic registry write and revoke the exact entry if the
                    // tree changed during that narrow interval.
                    ValidatePromotedFinalTree(
                        finalRoot,
                        payloadIdentity,
                        "post-commit FTB final tree");
                }
                catch (Exception integrityError)
                {
                    try
                    {
                        await RemoveExactRegistryInstanceAsync(instance).ConfigureAwait(false);
                        registryCommitted = false;
                    }
                    catch (Exception revocationError)
                    {
                        rollbackPermitted = false;
                        throw new AggregateException(
                            "The committed FTB tree failed its final security validation and registry revocation could not be proven; recovery is required.",
                            integrityError,
                            revocationError);
                    }

                    throw new InvalidDataException(
                        "The committed FTB tree changed during registry commit and was revoked.",
                        integrityError);
                }

                // Invoke the caller-controlled completion observer before the final security
                // invariant, but never let an observer exception skip finalization. A synchronous
                // observer can both mutate filesystem state and throw; the tree must still be
                // validated/revoked before this method can return or propagate an installation
                // failure. Observer failures alone are diagnostic and do not undo a safe commit.
                try
                {
                    progress?.Report(new FtbClientPackInstallProgress(
                        "complete",
                        "FTB 客戶端模組包已安全安裝並加入 X MCSV。",
                        1,
                        1,
                        Fraction: 1d));
                }
                catch (Exception)
                {
                    // Observer failures cannot participate in the durable commit decision. Avoid
                    // even formatting the caller-provided exception before finalization because a
                    // custom Exception.ToString implementation can itself throw.
                }

                try
                {
                    await TryFinalizeCommittedPromotionAsync(promotionReceipt).ConfigureAwait(false);
                }
                catch (Exception integrityError)
                {
                    try
                    {
                        await RemoveExactRegistryInstanceAsync(instance).ConfigureAwait(false);
                        registryCommitted = false;
                    }
                    catch (Exception revocationError)
                    {
                        rollbackPermitted = false;
                        throw new AggregateException(
                            "The committed FTB tree failed its finalization invariant and registry revocation could not be proven; its ownership receipt was retained for recovery.",
                            integrityError,
                            revocationError);
                    }

                    throw new InvalidDataException(
                        "The committed FTB tree changed during finalization and was revoked.",
                        integrityError);
                }

                return new FtbClientPackInstallResult(
                    instance,
                    pack.Id,
                    manifest.VersionId,
                    pack.Name,
                    manifest.Name,
                    selectedFiles.Length,
                    selection.ManifestFiles.Count(static file => file.ServerOnly),
                    selection.ManifestFiles.Count(file => !file.ServerOnly && file.Optional) -
                    selectedFiles.Count(static file => file.Optional),
                    installedPaths);
            }
        }
        catch (Exception installError)
        {
            if (registryCommitted)
            {
                ExceptionDispatchInfo.Capture(installError).Throw();
            }

            if (!rollbackPermitted)
            {
                ExceptionDispatchInfo.Capture(installError).Throw();
            }

            var rollbackErrors = new List<Exception>();
            if (promotionReceipt is not null)
            {
                await RollBackPendingPromotionAsync(promotionReceipt, rollbackErrors)
                    .ConfigureAwait(false);
            }
            else if (operationCreated && operationIdentity is { } identity)
            {
                try
                {
                    await _deleteOwnedTreeAsync(
                            _stagingRoot,
                            operationRoot,
                            identity,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    rollbackErrors.Add(cleanupError);
                }
            }

            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException(
                    "The FTB client installation failed and durable rollback is incomplete; its receipt was retained for recovery.",
                    new[] { installError }.Concat(rollbackErrors));
            }

            ExceptionDispatchInfo.Capture(installError).Throw();
            throw;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task RecoverPendingPromotionsCoreAsync(CancellationToken cancellationToken)
    {
        SafePath.EnsureNoReparsePointsUnderRoot(_instancesRoot, _instancesRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(_stagingRoot, _stagingRoot);
        var receiptPaths = Directory.EnumerateFiles(
                _stagingRoot,
                $"{PromotionReceiptPrefix}*{PromotionReceiptSuffix}",
                SearchOption.TopDirectoryOnly)
            .Take(MaximumPendingPromotionReceipts + 1)
            .ToArray();
        if (receiptPaths.Length > MaximumPendingPromotionReceipts)
        {
            throw new InvalidDataException("Too many pending FTB client promotion receipts were found.");
        }

        if (receiptPaths.Length == 0)
        {
            return;
        }

        var registry = await _registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        var failures = new List<Exception>();
        foreach (var receiptPath in receiptPaths.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var receipt = await ReadPromotionReceiptAsync(receiptPath, cancellationToken)
                    .ConfigureAwait(false);
                var registeredById = registry.Instances.FirstOrDefault(
                    instance => instance.Id == receipt.InstanceId);
                var registeredByPath = registry.Instances.FirstOrDefault(instance =>
                    PathsEqual(instance.DirectoryPath, receipt.FinalDirectoryPath));
                if (registeredById is not null)
                {
                    if (!PathsEqual(registeredById.DirectoryPath, receipt.FinalDirectoryPath) ||
                        registeredByPath?.Id != registeredById.Id)
                    {
                        throw new InvalidDataException(
                            "A pending FTB receipt conflicts with registered instance ownership.");
                    }

                    using (SafePath.AcquireNoReparseDirectoryChainLease(
                               _instancesRoot,
                               receipt.FinalDirectoryPath))
                    {
                        ValidatePromotedFinalTree(
                            receipt.FinalDirectoryPath,
                            receipt.FinalIdentity,
                            "registered FTB instance before recovery cleanup");
                        _duringRegisteredRecoveryForTesting?.Invoke(
                            receipt.FinalDirectoryPath);
                        await DeleteReceiptOperationRootIfPresentAsync(receipt, cancellationToken)
                            .ConfigureAwait(false);
                        ValidatePromotedFinalTree(
                            receipt.FinalDirectoryPath,
                            receipt.FinalIdentity,
                            "registered FTB instance after recovery cleanup");
                    }

                    // Keep this committed ownership receipt for the full lifetime of the registry
                    // entry. Windows permits some rename forms despite retained directory handles;
                    // deleting the receipt here would remove the last independent recovery proof.
                    continue;
                }

                if (registeredByPath is not null)
                {
                    throw new InvalidDataException(
                        "A pending FTB receipt final path is owned by another registered instance.");
                }

                if (Directory.Exists(receipt.FinalDirectoryPath))
                {
                    EnsureDirectoryIdentity(
                        receipt.FinalDirectoryPath,
                        receipt.FinalIdentity,
                        "unregistered FTB final tree");
                    await _deleteOwnedTreeAsync(
                            _instancesRoot,
                            receipt.FinalDirectoryPath,
                            receipt.FinalIdentity,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (File.Exists(receipt.FinalDirectoryPath))
                {
                    throw new InvalidDataException(
                        "A pending FTB receipt points to a final path that became a file.");
                }

                await DeleteReceiptOperationRootIfPresentAsync(receipt, cancellationToken)
                    .ConfigureAwait(false);
                DeletePromotionReceipt(receiptPath);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failures.Add(new IOException(
                    $"Could not reconcile pending FTB promotion receipt '{receiptPath}'.",
                    error));
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "One or more pending FTB client promotions require recovery before new installation can continue.",
                failures);
        }
    }

    private Task CommitRegistryInstanceAsync(MinecraftClientInstance instance) =>
        _registry.UpdateAsync(
            document =>
            {
                if (document.Instances.Any(item => item.Id == instance.Id))
                {
                    throw new InvalidOperationException(
                        "A client instance with the same id already exists.");
                }

                if (document.Instances.Any(item => PathsEqual(
                        item.DirectoryPath,
                        instance.DirectoryPath)))
                {
                    throw new InvalidOperationException(
                        "A client instance already owns the selected directory.");
                }

                document.Instances.Add(instance);
                return true;
            },
            // Once the verified tree is promoted, caller cancellation must not interrupt the
            // commit boundary and create an unregistered final directory.
            CancellationToken.None);

    private async Task<RegistryCommitState> ReadRegistryCommitStateAsync(
        MinecraftClientInstance expected)
    {
        if (Directory.Exists(_registry.RegistryPath) && !File.Exists(_registry.RegistryPath))
        {
            // A directory cannot be the atomically committed JSON registry file. This also keeps
            // a deterministic pre-commit configuration failure eligible for normal rollback.
            return RegistryCommitState.NotCommitted;
        }

        using var verifier = new MinecraftClientRegistry(_registry.RegistryPath);
        var document = await verifier.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        var byId = document.Instances.FirstOrDefault(item => item.Id == expected.Id);
        var byPath = document.Instances.FirstOrDefault(item =>
            PathsEqual(item.DirectoryPath, expected.DirectoryPath));
        if (byId is null && byPath is null)
        {
            return RegistryCommitState.NotCommitted;
        }

        if (byId is not null && byPath?.Id == byId.Id &&
            RegistryEntriesEqual(byId, expected))
        {
            return RegistryCommitState.Committed;
        }

        throw new InvalidDataException(
            "The registry contains conflicting ownership after an uncertain FTB commit.");
    }

    private async Task RemoveExactRegistryInstanceAsync(MinecraftClientInstance expected)
    {
        Exception? mutationError = null;
        try
        {
            // Use a fresh registry object because shutdown may already be disposing the workspace
            // registry. The atomic settings store still provides a durable mutation boundary.
            using var registry = new MinecraftClientRegistry(_registry.RegistryPath);
            await registry.UpdateAsync(
                    document =>
                    {
                        var byId = document.Instances.FirstOrDefault(item => item.Id == expected.Id);
                        var byPath = document.Instances.FirstOrDefault(item =>
                            PathsEqual(item.DirectoryPath, expected.DirectoryPath));
                        if (byId is null && byPath is null)
                        {
                            return false;
                        }

                        if (byId is null || byPath?.Id != byId.Id ||
                            !RegistryEntriesEqual(byId, expected))
                        {
                            throw new InvalidDataException(
                                "Refused to revoke a conflicting Minecraft client registry entry.");
                        }

                        document.Instances.Remove(byId);
                        return true;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            mutationError = error;
        }

        RegistryCommitState state;
        try
        {
            state = await ReadRegistryCommitStateAsync(expected).ConfigureAwait(false);
        }
        catch (Exception verificationError)
        {
            throw mutationError is null
                ? verificationError
                : new AggregateException(
                    "The FTB registry revocation outcome could not be verified.",
                    mutationError,
                    verificationError);
        }

        if (state == RegistryCommitState.Committed)
        {
            throw mutationError ?? new IOException(
                "The unsafe FTB registry entry remained committed after revocation.");
        }
    }

    private static void ValidatePromotedFinalTree(
        string finalDirectoryPath,
        SafePathObjectIdentity expectedIdentity,
        string description)
    {
        EnsureDirectoryIdentity(finalDirectoryPath, expectedIdentity, description);
        SafePath.EnsureTreeContainsNoReparsePoints(finalDirectoryPath);
        EnsureDirectoryIdentity(finalDirectoryPath, expectedIdentity, description);
    }

    private static bool RegistryEntriesEqual(
        MinecraftClientInstance first,
        MinecraftClientInstance second) =>
        JsonSerializer.SerializeToUtf8Bytes(first).AsSpan().SequenceEqual(
            JsonSerializer.SerializeToUtf8Bytes(second));

    private async Task DeleteReceiptOperationRootIfPresentAsync(
        PromotionReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(receipt.OperationDirectoryPath))
        {
            EnsureDirectoryIdentity(
                receipt.OperationDirectoryPath,
                receipt.OperationIdentity,
                "FTB staging operation");
            await _deleteOwnedTreeAsync(
                    _stagingRoot,
                    receipt.OperationDirectoryPath,
                    receipt.OperationIdentity,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (File.Exists(receipt.OperationDirectoryPath))
        {
            throw new InvalidDataException(
                "A pending FTB receipt operation path became a file.");
        }
    }

    private async Task RollBackPendingPromotionAsync(
        PromotionReceipt receipt,
        ICollection<Exception> rollbackErrors)
    {
        if (Directory.Exists(receipt.FinalDirectoryPath))
        {
            try
            {
                EnsureDirectoryIdentity(
                    receipt.FinalDirectoryPath,
                    receipt.FinalIdentity,
                    "unregistered FTB final tree");
                await _deleteOwnedTreeAsync(
                        _instancesRoot,
                        receipt.FinalDirectoryPath,
                        receipt.FinalIdentity,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                rollbackErrors.Add(cleanupError);
            }
        }
        else if (File.Exists(receipt.FinalDirectoryPath))
        {
            rollbackErrors.Add(new InvalidDataException(
                "The promoted FTB final directory became a file during rollback."));
        }

        try
        {
            await DeleteReceiptOperationRootIfPresentAsync(receipt, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception cleanupError)
        {
            rollbackErrors.Add(cleanupError);
        }

        if (rollbackErrors.Count == 0)
        {
            try
            {
                DeletePromotionReceipt(GetPromotionReceiptPath(receipt.InstanceId));
            }
            catch (Exception cleanupError)
            {
                rollbackErrors.Add(cleanupError);
            }
        }
    }

    private async Task TryFinalizeCommittedPromotionAsync(PromotionReceipt receipt)
    {
        ValidatePromotedFinalTree(
            receipt.FinalDirectoryPath,
            receipt.FinalIdentity,
            "committed FTB final tree before cleanup");
        _duringCommittedFinalizationForTesting?.Invoke(receipt.FinalDirectoryPath);
        try
        {
            await DeleteReceiptOperationRootIfPresentAsync(receipt, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException)
        {
            // The registry now durably owns the final tree. Its receipt is deliberately retained
            // for lifetime recovery, so operation cleanup can safely be retried at startup.
            Debug.WriteLine($"Committed FTB promotion receipt cleanup deferred: {cleanupError}");
        }

        ValidatePromotedFinalTree(
            receipt.FinalDirectoryPath,
            receipt.FinalIdentity,
            "committed FTB final tree after cleanup");
    }

    private PromotionReceipt CreatePromotionReceipt(
        Guid instanceId,
        string finalDirectoryPath,
        string operationDirectoryPath,
        SafePathObjectIdentity finalIdentity,
        SafePathObjectIdentity operationIdentity)
    {
        var receipt = new PromotionReceipt(
            PromotionReceiptSchemaVersion,
            instanceId,
            "ftb",
            finalDirectoryPath,
            operationDirectoryPath,
            finalIdentity.VolumeSerialNumber,
            finalIdentity.FileId,
            operationIdentity.VolumeSerialNumber,
            operationIdentity.FileId,
            DateTimeOffset.UtcNow);
        ValidatePromotionReceipt(receipt);
        return receipt;
    }

    private async Task WritePromotionReceiptAsync(
        PromotionReceipt receipt,
        CancellationToken cancellationToken)
    {
        ValidatePromotionReceipt(receipt);
        var receiptPath = GetPromotionReceiptPath(receipt.InstanceId);
        RejectExistingPath(receiptPath);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, PromotionReceiptJsonOptions);
        if (bytes.LongLength is <= 0 or > MaximumPromotionReceiptBytes)
        {
            throw new InvalidDataException("The FTB promotion receipt exceeds its safe size limit.");
        }

        var temporaryPath = SafePath.CombineUnderRoot(
            _stagingRoot,
            $"{PromotionReceiptPrefix}{receipt.InstanceId:N}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, receiptPath, overwrite: false);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private async Task<PromotionReceipt> ReadPromotionReceiptAsync(
        string receiptPath,
        CancellationToken cancellationToken)
    {
        var safePath = SafePath.EnsureNoReparsePointsUnderRoot(_stagingRoot, receiptPath);
        var info = new FileInfo(safePath);
        if (!info.Exists || info.Length is <= 0 or > MaximumPromotionReceiptBytes)
        {
            throw new InvalidDataException("The pending FTB promotion receipt has an unsafe size.");
        }

        await using var input = new FileStream(
            safePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var receipt = await JsonSerializer.DeserializeAsync<PromotionReceipt>(
                input,
                PromotionReceiptJsonOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The pending FTB promotion receipt is empty.");
        ValidatePromotionReceipt(receipt);
        if (!PathsEqual(safePath, GetPromotionReceiptPath(receipt.InstanceId)))
        {
            throw new InvalidDataException("The pending FTB promotion receipt file name is invalid.");
        }

        return receipt;
    }

    private void ValidatePromotionReceipt(PromotionReceipt receipt)
    {
        if (receipt.SchemaVersion != PromotionReceiptSchemaVersion ||
            receipt.InstanceId == Guid.Empty ||
            !receipt.Provider.Equals("ftb", StringComparison.Ordinal) ||
            receipt.FinalFileId == Guid.Empty ||
            receipt.OperationFileId == Guid.Empty ||
            receipt.CreatedAtUtc.Offset != TimeSpan.Zero ||
            receipt.CreatedAtUtc < DateTimeOffset.Parse("2020-01-01T00:00:00Z") ||
            receipt.CreatedAtUtc > DateTimeOffset.UtcNow.AddDays(1))
        {
            throw new InvalidDataException("The pending FTB promotion receipt is invalid.");
        }

        var expectedFinal = SafePath.CombineUnderRoot(
            _instancesRoot,
            receipt.InstanceId.ToString("N"));
        var expectedOperation = SafePath.CombineUnderRoot(
            _stagingRoot,
            receipt.InstanceId.ToString("N"));
        if (!PathsEqual(receipt.FinalDirectoryPath, expectedFinal) ||
            !PathsEqual(receipt.OperationDirectoryPath, expectedOperation))
        {
            throw new InvalidDataException(
                "The pending FTB promotion receipt paths do not match their managed roots.");
        }
    }

    private string GetPromotionReceiptPath(Guid instanceId) => SafePath.CombineUnderRoot(
        _stagingRoot,
        $"{PromotionReceiptPrefix}{instanceId:N}{PromotionReceiptSuffix}");

    private void DeletePromotionReceipt(string receiptPath)
    {
        var safePath = SafePath.EnsureNoReparsePointsUnderRoot(_stagingRoot, receiptPath);
        if (Directory.Exists(safePath))
        {
            throw new InvalidDataException("The FTB promotion receipt path became a directory.");
        }

        File.Delete(safePath);
    }

    private static void EnsureDirectoryIdentity(
        string path,
        SafePathObjectIdentity expected,
        string description)
    {
        if (!Directory.Exists(path) || SafePath.GetExistingObjectIdentity(path) != expected)
        {
            throw new UnauthorizedAccessException(
                $"The {description} identity changed; automatic cleanup was refused.");
        }
    }

    private static bool PathsEqual(string first, string second) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    private static Task DeleteOwnedTreeWithRetryAsync(
        string trustedRoot,
        string ownedPath,
        SafePathObjectIdentity expectedIdentity,
        CancellationToken cancellationToken) =>
        SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
            trustedRoot,
            ownedPath,
            expectedIdentity,
            protectedObjectIdentities: null,
            cancellationToken);

    private enum RegistryCommitState
    {
        NotCommitted,
        Committed,
    }

    private async Task<IReadOnlyList<string>> DownloadPackFilesAsync(
        IReadOnlyList<FtbPackFile> files,
        string payloadRoot,
        int maximumConcurrency,
        IProgress<FtbClientPackInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var installed = new ConcurrentBag<string>();
        var completedFiles = 0;
        long completedBytes = 0;
        Exception? firstFailure = null;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        progress?.Report(new FtbClientPackInstallProgress(
            "download-content",
            "正在下載並驗證 FTB 模組包內容…",
            TotalItems: files.Count));
        try
        {
            await Parallel.ForEachAsync(
                files,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(maximumConcurrency, Math.Max(1, files.Count)),
                    CancellationToken = linkedCancellation.Token,
                },
                async (file, token) =>
                {
                    try
                    {
                        var destination = PreparePackDestination(payloadRoot, file.Path);
                        long previousBytes = 0;
                        var byteProgress = new InlineProgress<long>(current =>
                        {
                            var delta = Math.Max(
                                0,
                                current - Interlocked.Exchange(ref previousBytes, current));
                            var aggregate = Interlocked.Add(ref completedBytes, delta);
                            progress?.Report(new FtbClientPackInstallProgress(
                                "download-content",
                                file.Path,
                                Volatile.Read(ref completedFiles),
                                files.Count,
                                aggregate));
                        });
                        await _downloader.DownloadAsync(
                                file.PreferredDownloadUris,
                                destination,
                                file.Size,
                                file.Hashes.Sha512,
                                file.Hashes.Sha1,
                                byteProgress,
                                token)
                            .ConfigureAwait(false);
                        await VerifySha256Async(destination, file.Hashes.Sha256, token)
                            .ConfigureAwait(false);
                        installed.Add(file.Path);
                        var count = Interlocked.Increment(ref completedFiles);
                        progress?.Report(new FtbClientPackInstallProgress(
                            "download-content",
                            file.Path,
                            count,
                            files.Count,
                            Volatile.Read(ref completedBytes)));
                    }
                    catch (Exception exception)
                    {
                        Interlocked.CompareExchange(ref firstFailure, exception, null);
                        await linkedCancellation.CancelAsync().ConfigureAwait(false);
                        throw;
                    }
                }).ConfigureAwait(false);
        }
        catch
        {
            await linkedCancellation.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (firstFailure is not null)
            {
                ExceptionDispatchInfo.Capture(firstFailure).Throw();
            }

            throw;
        }

        return installed.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Selection ValidateCatalogSelection(
        FtbClientPackInstallRequest request,
        FtbPack pack,
        FtbPackVersionManifest manifest)
    {
        if (pack.Id != request.PackId || manifest.PackId != request.PackId ||
            manifest.VersionId != request.VersionId || pack.IsPrivate || manifest.IsPrivate ||
            !manifest.Type.Equals("release", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Only the selected official public stable FTB pack release can be installed.");
        }

        var listedVersion = pack.Versions.SingleOrDefault(version => version.Id == request.VersionId)
            ?? throw new InvalidDataException("The selected FTB version is not listed by its pack.");
        if (listedVersion.IsPrivate ||
            !listedVersion.Type.Equals("release", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The selected FTB version is not a public stable release.");
        }

        var gameVersion = RequireSafeVersion(manifest.MinecraftVersion, "Minecraft");
        if (!string.Equals(listedVersion.MinecraftVersion, gameVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The FTB manifest Minecraft target does not match pack metadata.");
        }

        var loader = MapLoader(manifest.ModLoaderName);
        var loaderVersion = loader == MinecraftClientLoader.Vanilla
            ? null
            : RequireSafeVersion(manifest.ModLoaderVersion, "mod loader");
        if (MapLoader(listedVersion.ModLoaderName) != loader ||
            !string.Equals(listedVersion.ModLoaderVersion, loaderVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The FTB manifest loader target does not match pack metadata.");
        }

        var javaMajor = ParseJavaMajor(manifest.JavaVersion);
        var listedJavaMajor = ParseJavaMajor(listedVersion.JavaVersion);
        if (javaMajor is not null && listedJavaMajor is not null && javaMajor != listedJavaMajor)
        {
            throw new InvalidDataException("The FTB manifest Java target does not match pack metadata.");
        }

        if (request.JavaMajorVersion is not null && javaMajor is not null &&
            request.JavaMajorVersion != javaMajor)
        {
            throw new InvalidDataException(
                $"This FTB release requires Java {javaMajor}; the selected Java major does not match.");
        }

        if (request.MemoryMode == MinecraftClientMemoryMode.Manual &&
            request.MaximumMemoryMb < manifest.Memory.MinimumMb)
        {
            throw new InvalidOperationException(
                $"This FTB release requires at least {manifest.Memory.MinimumMb} MB of memory.");
        }

        var manifestFiles = ValidateManifestFiles(manifest.Files);
        return new Selection(gameVersion, loader, loaderVersion, javaMajor, manifestFiles);
    }

    private static IReadOnlyList<FtbPackFile> ValidateManifestFiles(
        IReadOnlyList<FtbPackFile> files)
    {
        if (files.Count > MaximumManifestFiles)
        {
            throw new InvalidDataException("The FTB manifest contains too many files.");
        }

        long totalBytes = 0;
        var paths = new Dictionary<string, FtbPackFile>(StringComparer.OrdinalIgnoreCase);
        var uniqueFiles = new List<FtbPackFile>(files.Count);
        foreach (var file in files)
        {
            if (file.Size is < 0 or > MaximumManifestFileBytes ||
                file.PreferredDownloadUris.Count == 0)
            {
                throw new InvalidDataException("The FTB manifest contains an unsafe file entry.");
            }

            _ = ParseHash(file.Hashes.Sha1, 20, "SHA-1");
            _ = ParseHash(file.Hashes.Sha256, 32, "SHA-256");
            _ = ParseHash(file.Hashes.Sha512, 64, "SHA-512");
            foreach (var uri in file.PreferredDownloadUris)
            {
                DownloadUriPolicy.EnsureAllowed(uri, isRedirect: false);
            }

            _ = SafeModpackArchive.ResolveDestination(
                Path.Combine(Path.GetTempPath(), "x-mcsv-ftb-path-validation"),
                file.Path);
            ValidateProtectedPath(file.Path);

            if (paths.TryGetValue(file.Path, out var existing))
            {
                if (!IsSafeCaseOnlyAlias(existing, file))
                {
                    throw new InvalidDataException(
                        "The FTB manifest contains conflicting file destinations.");
                }

                continue;
            }

            totalBytes = checked(totalBytes + file.Size);
            if (totalBytes > MaximumManifestTotalBytes)
            {
                throw new InvalidDataException("The FTB manifest exceeds the safe total download limit.");
            }

            paths.Add(file.Path, file);
            uniqueFiles.Add(file);
        }

        return uniqueFiles;
    }

    private static bool IsSafeCaseOnlyAlias(FtbPackFile existing, FtbPackFile candidate) =>
        !existing.Path.Equals(candidate.Path, StringComparison.Ordinal) &&
        existing.Path.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase) &&
        existing.Size == candidate.Size &&
        existing.ClientOnly == candidate.ClientOnly &&
        existing.ServerOnly == candidate.ServerOnly &&
        existing.Optional == candidate.Optional &&
        existing.Type.Equals(candidate.Type, StringComparison.OrdinalIgnoreCase) &&
        existing.Hashes.Sha1.Equals(candidate.Hashes.Sha1, StringComparison.OrdinalIgnoreCase) &&
        existing.Hashes.Sha256.Equals(candidate.Hashes.Sha256, StringComparison.OrdinalIgnoreCase) &&
        existing.Hashes.Sha512.Equals(candidate.Hashes.Sha512, StringComparison.OrdinalIgnoreCase);

    private static MinecraftClientLoader MapLoader(string? name)
    {
        var normalized = string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : new string(name.Where(char.IsAsciiLetterOrDigit)
                .Select(char.ToLowerInvariant).ToArray());
        return normalized switch
        {
            "" or "vanilla" or "minecraft" => MinecraftClientLoader.Vanilla,
            "fabric" => MinecraftClientLoader.Fabric,
            "forge" => MinecraftClientLoader.Forge,
            "neoforge" or "neoforged" => MinecraftClientLoader.NeoForge,
            "quilt" => MinecraftClientLoader.Quilt,
            _ => throw new InvalidDataException($"The FTB loader '{name}' is unsupported."),
        };
    }

    private static int? ParseJavaMajor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var first = value.Trim().Split('.', '-', '+')[0];
        if (!int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            major is < 8 or > 99)
        {
            throw new InvalidDataException("The FTB Java target is invalid.");
        }

        return major;
    }

    private static string RequireSafeVersion(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new InvalidDataException($"The FTB {field} target is missing or unsafe.");
        }

        return value;
    }

    private static string PreparePackDestination(string payloadRoot, string relativePath)
    {
        ValidateProtectedPath(relativePath);
        var destination = SafeModpackArchive.ResolveDestination(payloadRoot, relativePath);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("FTB file destination has no parent directory.");
        var relativeParent = Path.GetRelativePath(payloadRoot, parent);
        var current = payloadRoot;
        foreach (var segment in relativeParent.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = SafePath.CombineUnderRoot(current, segment);
            if (File.Exists(current))
            {
                throw new IOException($"FTB destination parent is a file: '{current}'.");
            }

            Directory.CreateDirectory(current);
            SafePath.EnsureNoReparsePointsUnderRoot(payloadRoot, current);
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"FTB destination already exists: '{relativePath}'.");
        }

        return destination;
    }

    private static void ValidateProtectedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2_048 || path.Contains('\\'))
        {
            throw new InvalidDataException("The FTB manifest path is invalid.");
        }

        var first = path.Split('/', 2)[0];
        if (first.Equals("versions", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("libraries", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("assets", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("runtime", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("jre", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("natives", StringComparison.OrdinalIgnoreCase) ||
            first.Equals(".x-mcsv-content", StringComparison.OrdinalIgnoreCase) ||
            first.Equals(".x-mcsv", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("installation.id", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("launcher_accounts.json", StringComparison.OrdinalIgnoreCase) ||
            first.Equals("launcher_profiles.json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The FTB pack attempts to replace protected launcher content: '{path}'.");
        }
    }

    private static async Task VerifySha256Async(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        var expectedBytes = ParseHash(expected, 32, "SHA-256");
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actual))
        {
            throw new InvalidDataException("Downloaded FTB file failed SHA-256 validation.");
        }
    }

    private static byte[] ParseHash(string value, int expectedBytes, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        try
        {
            var bytes = Convert.FromHexString(value.Trim());
            if (bytes.Length != expectedBytes)
            {
                throw new FormatException();
            }

            return bytes;
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"FTB file {name} hash is invalid.", exception);
        }
    }

    private static MinecraftClientInstance CreateInstance(
        FtbClientPackInstallRequest request,
        FtbPack pack,
        FtbPackVersionManifest manifest,
        Selection selection,
        int effectiveMinimumMemoryMb,
        int effectiveMaximumMemoryMb,
        string finalRoot,
        string installedVersionId,
        string? javaExecutablePath,
        string? catalogIconImagePath,
        string? catalogPreviewImagePath) => new()
    {
        Id = request.InstanceId,
        Name = request.Name.Trim(),
        Edition = MinecraftClientEdition.Java,
        DirectoryPath = finalRoot,
        GameVersion = selection.GameVersion,
        InstalledVersionId = installedVersionId,
        Loader = selection.Loader,
        LoaderVersion = selection.LoaderVersion,
        LoaderInstallKind = MinecraftClientLoaderInstallKind.Managed,
        JavaExecutablePath = javaExecutablePath,
        JavaMajorVersion = selection.JavaMajorVersion ?? request.JavaMajorVersion,
        MemoryMode = request.MemoryMode,
        MinimumMemoryMb = effectiveMinimumMemoryMb,
        MaximumMemoryMb = effectiveMaximumMemoryMb,
        WindowWidth = request.WindowWidth,
        WindowHeight = request.WindowHeight,
        FullScreen = request.FullScreen,
        EnableQuickLaunch = request.EnableQuickLaunch,
        HideLauncherAfterGameStarts = request.HideLauncherAfterGameStarts,
        ShowGameLog = request.ShowGameLog,
        EnableDedicatedGpu = request.EnableDedicatedGpu,
        EnableDiscordPresence = request.EnableDiscordPresence,
        CatalogProvider = "ftb",
        CatalogProjectId = pack.Id.ToString(CultureInfo.InvariantCulture),
        CatalogVersionId = manifest.VersionId.ToString(CultureInfo.InvariantCulture),
        CatalogIconUri = IsOfficialFtbArtworkUri(pack.IconUri) ? pack.IconUri : null,
        CatalogPreviewUri = IsOfficialFtbArtworkUri(pack.PreviewImageUri) ? pack.PreviewImageUri : null,
        CatalogIconImagePath = catalogIconImagePath,
        CatalogPreviewImagePath = catalogPreviewImagePath,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    internal static bool IsOfficialFtbArtworkUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        uri.IdnHost.TrimEnd('.').Equals(
            "cdn.feed-the-beast.com",
            StringComparison.OrdinalIgnoreCase);

    private static void ValidateRequest(FtbClientPackInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InstanceId == Guid.Empty || string.IsNullOrWhiteSpace(request.Name) ||
            request.Name.Length > 128 || request.PackId <= 0 || request.VersionId <= 0)
        {
            throw new ArgumentException("The FTB client pack install request is invalid.", nameof(request));
        }

        if (request.MinimumMemoryMb is < 512 or > 262_144 ||
            request.MaximumMemoryMb < request.MinimumMemoryMb || request.MaximumMemoryMb > 262_144 ||
            request.WindowWidth is < 640 or > 16_384 || request.WindowHeight is < 360 or > 16_384 ||
            request.MaximumConcurrentDownloads is < 1 or > MaximumDownloadConcurrency ||
            request.JavaMajorVersion is < 8 or > 99)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The client memory, resolution, Java, or concurrency is outside the safe range.");
        }
    }

    private static string? ValidateCatalogArtworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Cached FTB artwork path must be absolute.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Cached FTB artwork no longer exists.", fullPath);
        }

        var file = new FileInfo(fullPath);
        var extension = file.Extension;
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            file.Length is <= 0 or > MaximumCatalogArtworkBytes ||
            !IsSupportedArtworkExtension(extension) ||
            !HasMatchingArtworkSignature(fullPath, extension))
        {
            throw new InvalidDataException("Cached FTB artwork is not a bounded regular image file.");
        }

        return fullPath;
    }

    private static string? CopyArtworkIntoOwnedPayload(
        string? sourcePath,
        string payloadRoot,
        string fileStem,
        CancellationToken cancellationToken)
    {
        var source = ValidateCatalogArtworkPath(sourcePath);
        if (source is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(source).ToLowerInvariant();
        var assetsDirectory = SafePath.CombineUnderRoot(payloadRoot, ".x-mcsv", "assets");
        Directory.CreateDirectory(assetsDirectory);
        SafePath.EnsureNoReparsePointsUnderRoot(payloadRoot, assetsDirectory);
        var destination = SafePath.CombineUnderRoot(assetsDirectory, fileStem + extension);
        var temporary = SafePath.CombineUnderRoot(
            assetsDirectory,
            $".{fileStem}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var input = new FileStream(
                       source,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       64 * 1024,
                       FileOptions.SequentialScan))
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var copied = new FileInfo(temporary);
            if (copied.Length is <= 0 or > MaximumCatalogArtworkBytes ||
                !HasMatchingArtworkSignature(temporary, extension))
            {
                throw new InvalidDataException("Copied FTB artwork failed validation.");
            }

            File.Move(temporary, destination);
            return Path.GetRelativePath(payloadRoot, destination);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static bool IsSupportedArtworkExtension(string extension) =>
        extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);

    private static bool HasMatchingArtworkSignature(string path, string extension)
    {
        Span<byte> header = stackalloc byte[12];
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = input.Read(header);
        var bytes = header[..read];
        return extension.ToLowerInvariant() switch
        {
            ".png" => bytes.StartsWith(
                new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            ".jpg" => bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff }),
            ".webp" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) &&
                       bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            ".gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
            _ => false,
        };
    }

    private static string? ResolveOwnedArtworkPath(string instanceRoot, string? relativePath) =>
        relativePath is null ? null : SafePath.CombineUnderRoot(instanceRoot, relativePath);

    private static void ValidateInstalledVersionId(string installedVersionId)
    {
        if (string.IsNullOrWhiteSpace(installedVersionId) || installedVersionId.Length > 192 ||
            installedVersionId.Any(char.IsControl))
        {
            throw new InvalidDataException("The client installer returned an invalid launch profile id.");
        }
    }

    private static string NormalizeRoot(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void RejectExistingPath(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"Managed client path already exists: '{path}'.");
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

    private sealed record Selection(
        string GameVersion,
        MinecraftClientLoader Loader,
        string? LoaderVersion,
        int? JavaMajorVersion,
        IReadOnlyList<FtbPackFile> ManifestFiles);

    private sealed record PromotionReceipt(
        int SchemaVersion,
        Guid InstanceId,
        string Provider,
        string FinalDirectoryPath,
        string OperationDirectoryPath,
        ulong FinalVolumeSerialNumber,
        Guid FinalFileId,
        ulong OperationVolumeSerialNumber,
        Guid OperationFileId,
        DateTimeOffset CreatedAtUtc)
    {
        [JsonIgnore]
        public SafePathObjectIdentity FinalIdentity =>
            new(FinalVolumeSerialNumber, FinalFileId);

        [JsonIgnore]
        public SafePathObjectIdentity OperationIdentity =>
            new(OperationVolumeSerialNumber, OperationFileId);
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}

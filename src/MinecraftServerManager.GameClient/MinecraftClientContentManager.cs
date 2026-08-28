using System.Diagnostics;
using System.Text.Json;
using MinecraftServerManager.Core.Services;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Root-confined content management for one isolated Minecraft client instance. The manager never
/// downloads content. Imports are copied into an instance-local staging area and committed by
/// same-volume moves only after validation succeeds.
/// </summary>
public sealed class MinecraftClientContentManager : IDisposable
{
    private const string ManagementDirectoryName = ".x-mcsv-content";
    private const string ManifestFileName = "entry.json";
    private const string PayloadName = "payload";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly StringComparer NameComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly string _instanceRoot;
    private readonly string _managementRoot;
    private readonly string _disabledRoot;
    private readonly string _recycleRoot;
    private readonly string _stagingRoot;
    private readonly MinecraftClientContentLimits _limits;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private bool _disposed;

    public MinecraftClientContentManager(
        string instanceDirectory,
        MinecraftClientContentLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceDirectory);
        if (!Path.IsPathFullyQualified(instanceDirectory))
        {
            throw new ArgumentException("Minecraft client instance directory must be absolute.", nameof(instanceDirectory));
        }

        _instanceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instanceDirectory));
        if (!Directory.Exists(_instanceRoot))
        {
            throw new DirectoryNotFoundException(
                $"Minecraft client instance directory does not exist: '{_instanceRoot}'.");
        }

        RejectReparsePoint(_instanceRoot, "Minecraft client instance directory");
        _limits = limits ?? new MinecraftClientContentLimits();
        ValidateLimits(_limits);

        _managementRoot = SafePath.CombineUnderRoot(_instanceRoot, ManagementDirectoryName);
        _disabledRoot = SafePath.CombineUnderRoot(_managementRoot, "disabled");
        _recycleRoot = SafePath.CombineUnderRoot(_managementRoot, "recycle");
        _stagingRoot = SafePath.CombineUnderRoot(_managementRoot, "staging");
        CreateAndValidateManagedDirectory(_managementRoot);
        CreateAndValidateManagedDirectory(_disabledRoot);
        CreateAndValidateManagedDirectory(_recycleRoot);
        CreateAndValidateManagedDirectory(_stagingRoot);
    }

    public string InstanceDirectory => _instanceRoot;

    public async Task<MinecraftClientContentSnapshot> ListAsync(
        MinecraftClientContentKind kind,
        bool includeDisabled = true,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateKind(kind);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = new List<MinecraftClientContentEntry>();
            var inspectionBudget = new InspectionBudget(_limits);
            var limitReached = EnumerateActiveRoot(
                kind,
                MinecraftClientContentState.Enabled,
                GetEnabledRoot(kind),
                entries,
                inspectionBudget,
                cancellationToken);
            if (includeDisabled &&
                entries.Count < _limits.MaximumItemsPerCategory &&
                !inspectionBudget.IsExhausted)
            {
                limitReached |= EnumerateActiveRoot(
                    kind,
                    MinecraftClientContentState.Disabled,
                    GetDisabledRoot(kind),
                    entries,
                    inspectionBudget,
                    cancellationToken);
            }

            limitReached |= inspectionBudget.IsExhausted;

            entries.Sort(static (left, right) =>
            {
                var state = left.Key.State.CompareTo(right.Key.State);
                return state != 0
                    ? state
                    : StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
            });
            return new MinecraftClientContentSnapshot(
                kind,
                DateTimeOffset.UtcNow,
                entries,
                limitReached);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<IReadOnlyList<MinecraftClientContentEntry>> ListRecycleBinAsync(
        MinecraftClientContentKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (kind is { } selectedKind)
        {
            ValidateKind(selectedKind);
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateManagedRoot(_recycleRoot);
            var entries = new List<MinecraftClientContentEntry>();
            var inspectionBudget = new InspectionBudget(_limits);
            var candidates = new List<DirectoryInfo>(
                Math.Min(_limits.MaximumRecycleCandidates, _limits.MaximumItemsPerCategory));
            foreach (var slot in new DirectoryInfo(_recycleRoot)
                         .EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidates.Count >= _limits.MaximumRecycleCandidates)
                {
                    break;
                }

                candidates.Add(slot);
            }

            candidates.Sort(static (left, right) =>
                right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc));
            foreach (var slot in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count >= _limits.MaximumItemsPerCategory ||
                    !inspectionBudget.TryConsumeEntry())
                {
                    break;
                }

                if (!Guid.TryParseExact(slot.Name, "N", out var recycleId) ||
                    slot.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                RecycleManifest? manifest;
                try
                {
                    manifest = await TryReadManifestAsync(slot.FullName, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (manifest is null || (kind is { } filter && manifest.Kind != filter))
                {
                    continue;
                }

                if (manifest.RecycleId != recycleId)
                {
                    continue;
                }

                var payload = SafePath.CombineUnderRoot(slot.FullName, PayloadName);
                if (!PathExists(payload))
                {
                    continue;
                }

                var key = new MinecraftClientContentItemKey(
                    manifest.Kind,
                    MinecraftClientContentState.Recycled,
                    manifest.StorageName,
                    recycleId);
                entries.Add(InspectEntry(
                    key,
                    payload,
                    $"{ManagementDirectoryName}/recycle/{slot.Name}/{PayloadName}",
                    cancellationToken,
                    inspectionBudget));

                if (inspectionBudget.IsExhausted)
                {
                    break;
                }
            }

            return entries;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<MinecraftClientContentImportResult> ImportAsync(
        MinecraftClientContentImportRequest request,
        IProgress<MinecraftClientContentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateImportRequest(request);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var operationRoot = SafePath.CombineUnderRoot(_stagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            ValidateManagedRoot(_stagingRoot);
            Directory.CreateDirectory(operationRoot);
            SafePath.EnsureNoReparsePointsUnderRoot(_stagingRoot, operationRoot);

            var sources = ResolveImportSources(request);
            EnsureNoDestinationConflicts(request.Kind, sources.Select(source => source.Name));
            var budget = new CopyBudget(_limits);
            var staged = new List<StagedImport>(sources.Count);
            for (var index = 0; index < sources.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sources[index];
                var stagedPath = SafePath.CombineUnderRoot(operationRoot, source.Name);
                progress?.Report(new MinecraftClientContentProgress(
                    "copy",
                    $"正在安全匯入 {source.Name}…",
                    index,
                    sources.Count,
                    budget.CopiedBytes));
                await CopySourceAsync(
                        request.Kind,
                        source.Path,
                        stagedPath,
                        budget,
                        cancellationToken)
                    .ConfigureAwait(false);
                staged.Add(new StagedImport(source.Name, stagedPath));
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoDestinationConflicts(request.Kind, sources.Select(source => source.Name));
            var enabledRoot = GetEnabledRoot(request.Kind);
            var promoted = new List<(string Source, string Destination)>(staged.Count);
            try
            {
                // Commit is deliberately non-cancellable: either every staged item is promoted,
                // or completed moves are rolled back into staging before an error is returned.
                foreach (var item in staged)
                {
                    var destination = SafePath.CombineUnderRoot(enabledRoot, item.Name);
                    Move(item.StagedPath, destination);
                    promoted.Add((destination, item.StagedPath));
                }
            }
            catch (Exception commitError)
            {
                Exception? rollbackError = null;
                for (var index = promoted.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        Move(promoted[index].Source, promoted[index].Destination);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        rollbackError ??= exception;
                    }
                }

                if (rollbackError is not null)
                {
                    throw new AggregateException(
                        "Content import failed and its atomic rollback was incomplete.",
                        commitError,
                        rollbackError);
                }

                throw;
            }

            var imported = new List<MinecraftClientContentEntry>(promoted.Count);
            foreach (var (destination, _) in promoted)
            {
                var name = Path.GetFileName(destination);
                var key = new MinecraftClientContentItemKey(
                    request.Kind,
                    MinecraftClientContentState.Enabled,
                    name);
                imported.Add(InspectEntry(
                    key,
                    destination,
                    GetRelativeDisplayPath(request.Kind, MinecraftClientContentState.Enabled, name),
                    CancellationToken.None));
            }

            progress?.Report(new MinecraftClientContentProgress(
                "complete",
                $"已匯入 {imported.Count} 個項目。",
                imported.Count,
                imported.Count,
                budget.CopiedBytes));
            return new MinecraftClientContentImportResult(request.Kind, imported);
        }
        finally
        {
            TryDeleteOwnedDirectory(_stagingRoot, operationRoot);
            _mutationGate.Release();
        }
    }

    public async Task<MinecraftClientContentEntry> SetEnabledAsync(
        MinecraftClientContentItemKey key,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateActiveKey(key);
        var destinationState = enabled
            ? MinecraftClientContentState.Enabled
            : MinecraftClientContentState.Disabled;
        if (key.State == destinationState)
        {
            return await GetEntryAsync(key, cancellationToken).ConfigureAwait(false);
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ResolveActiveItem(key, requireExists: true);
            var destinationRoot = destinationState == MinecraftClientContentState.Enabled
                ? GetEnabledRoot(key.Kind)
                : GetDisabledRoot(key.Kind);
            if (destinationState == MinecraftClientContentState.Enabled && Directory.Exists(source))
            {
                SafePath.EnsureTreeContainsNoReparsePoints(source);
            }

            var destination = SafePath.CombineUnderRoot(destinationRoot, key.StorageName);
            RejectExistingPath(destination);
            Move(source, destination);
            var newKey = key with { State = destinationState };
            return InspectEntry(
                newKey,
                destination,
                GetRelativeDisplayPath(key.Kind, destinationState, key.StorageName),
                CancellationToken.None);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Removes an active item. The default is recoverable instance-local recycling; permanent
    /// deletion happens only when the caller explicitly sets <paramref name="permanently"/>.
    /// </summary>
    public async Task<MinecraftClientContentEntry?> RemoveAsync(
        MinecraftClientContentItemKey key,
        bool permanently = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (key.State == MinecraftClientContentState.Recycled)
        {
            if (!permanently)
            {
                throw new InvalidOperationException("A recycled item can be restored or permanently removed.");
            }

            await PermanentlyDeleteRecycledAsync(key, cancellationToken).ConfigureAwait(false);
            return null;
        }

        ValidateActiveKey(key);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = ResolveActiveItem(key, requireExists: true);
            if (permanently)
            {
                DeleteActiveItem(source);
                return null;
            }

            var recycleId = Guid.NewGuid();
            var pendingSlot = SafePath.CombineUnderRoot(_recycleRoot, $".pending-{recycleId:N}");
            var finalSlot = SafePath.CombineUnderRoot(_recycleRoot, recycleId.ToString("N"));
            Directory.CreateDirectory(pendingSlot);
            SafePath.EnsureNoReparsePointsUnderRoot(_recycleRoot, pendingSlot);
            var manifest = new RecycleManifest(
                1,
                recycleId,
                key.Kind,
                key.State,
                key.StorageName,
                DateTimeOffset.UtcNow);
            var payload = SafePath.CombineUnderRoot(pendingSlot, PayloadName);
            try
            {
                await WriteManifestAtomicallyAsync(pendingSlot, manifest, cancellationToken)
                    .ConfigureAwait(false);
                Move(source, payload);
                Directory.Move(pendingSlot, finalSlot);
            }
            catch
            {
                if (PathExists(payload) && !PathExists(source))
                {
                    Move(payload, source);
                }

                TryDeleteOwnedDirectory(_recycleRoot, pendingSlot);
                throw;
            }

            var recycledKey = new MinecraftClientContentItemKey(
                key.Kind,
                MinecraftClientContentState.Recycled,
                key.StorageName,
                recycleId);
            var finalPayload = SafePath.CombineUnderRoot(finalSlot, PayloadName);
            return InspectEntry(
                recycledKey,
                finalPayload,
                $"{ManagementDirectoryName}/recycle/{recycleId:N}/{PayloadName}",
                CancellationToken.None);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<MinecraftClientContentEntry> RestoreAsync(
        MinecraftClientContentItemKey key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateRecycledKey(key);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (slot, payload, manifest) = await ResolveRecycleSlotAsync(key, cancellationToken)
                .ConfigureAwait(false);
            var destinationRoot = manifest.OriginalState == MinecraftClientContentState.Disabled
                ? GetDisabledRoot(manifest.Kind)
                : GetEnabledRoot(manifest.Kind);
            if (Directory.Exists(payload))
            {
                SafePath.EnsureTreeContainsNoReparsePoints(payload);
            }

            EnsureNoDestinationConflicts(manifest.Kind, [manifest.StorageName]);
            var destination = SafePath.CombineUnderRoot(destinationRoot, manifest.StorageName);
            Move(payload, destination);
            TryDeleteOwnedDirectory(_recycleRoot, slot);
            var restoredKey = new MinecraftClientContentItemKey(
                manifest.Kind,
                manifest.OriginalState,
                manifest.StorageName);
            return InspectEntry(
                restoredKey,
                destination,
                GetRelativeDisplayPath(manifest.Kind, manifest.OriginalState, manifest.StorageName),
                CancellationToken.None);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<int> PurgeRecycleBinAsync(
        MinecraftClientContentKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (kind is { } selectedKind)
        {
            ValidateKind(selectedKind);
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = 0;
            foreach (var slot in new DirectoryInfo(_recycleRoot)
                         .EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(slot.Name, "N", out _))
                {
                    continue;
                }

                var manifest = await TryReadManifestAsync(slot.FullName, cancellationToken)
                    .ConfigureAwait(false);
                if (manifest is null || (kind is { } filter && manifest.Kind != filter))
                {
                    continue;
                }

                SafePath.DeleteTreeWithoutFollowingReparsePoints(_recycleRoot, slot.FullName);
                removed++;
            }

            return removed;
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
        _mutationGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<MinecraftClientContentEntry> GetEntryAsync(
        MinecraftClientContentItemKey key,
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = ResolveActiveItem(key, requireExists: true);
            return InspectEntry(
                key,
                path,
                GetRelativeDisplayPath(key.Kind, key.State, key.StorageName),
                cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private bool EnumerateActiveRoot(
        MinecraftClientContentKind kind,
        MinecraftClientContentState state,
        string root,
        List<MinecraftClientContentEntry> target,
        InspectionBudget inspectionBudget,
        CancellationToken cancellationToken)
    {
        ValidateManagedRoot(root);
        var limitReached = false;
        foreach (var item in new DirectoryInfo(root)
                     .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (target.Count >= _limits.MaximumItemsPerCategory ||
                !inspectionBudget.TryConsumeEntry())
            {
                limitReached = true;
                break;
            }

            if (!IsSupportedItem(kind, item.Name, item.Attributes.HasFlag(FileAttributes.Directory)))
            {
                continue;
            }

            var key = new MinecraftClientContentItemKey(kind, state, item.Name);
            try
            {
                target.Add(InspectEntry(
                    key,
                    item.FullName,
                    GetRelativeDisplayPath(kind, state, item.Name),
                    cancellationToken,
                    inspectionBudget));

                if (inspectionBudget.IsExhausted)
                {
                    limitReached = true;
                    break;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // The item was concurrently removed, became inaccessible, or was replaced by an
                // unsafe filesystem object. Never follow it and continue listing safe siblings.
            }
        }

        return limitReached;
    }

    private MinecraftClientContentEntry InspectEntry(
        MinecraftClientContentItemKey key,
        string path,
        string relativePath,
        CancellationToken cancellationToken,
        InspectionBudget? inspectionBudget = null)
    {
        var attributes = File.GetAttributes(path);
        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        var lastWrite = isDirectory
            ? new DirectoryInfo(path).LastWriteTimeUtc
            : new FileInfo(path).LastWriteTimeUtc;
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return new MinecraftClientContentEntry(
                key,
                key.StorageName,
                relativePath,
                isDirectory,
                0,
                0,
                lastWrite,
                false,
                false,
                "項目是連結或重新解析點，為避免存取實例外資料而拒絕操作。");
        }

        if (!isDirectory)
        {
            var file = new FileInfo(path);
            var fileSize = Math.Max(0, file.Length);
            var fileInspectionTruncated = inspectionBudget is not null &&
                !inspectionBudget.TryConsumeBytes(fileSize);
            return new MinecraftClientContentEntry(
                key,
                key.StorageName,
                relativePath,
                false,
                fileSize,
                1,
                lastWrite,
                fileInspectionTruncated,
                true);
        }

        long size = 0;
        var fileCount = 0;
        var truncated = false;
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((new DirectoryInfo(path), 0));
        var inspectedEntries = 0;
        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (directory, depth) = pending.Pop();
                if (depth > _limits.MaximumDirectoryDepth)
                {
                    truncated = true;
                    continue;
                }

                foreach (var child in directory.EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (inspectionBudget is not null && !inspectionBudget.TryConsumeEntry())
                    {
                        truncated = true;
                        pending.Clear();
                        break;
                    }

                    inspectedEntries++;
                    if (inspectedEntries > _limits.MaximumInspectionFilesPerItem)
                    {
                        truncated = true;
                        pending.Clear();
                        break;
                    }

                    if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        return new MinecraftClientContentEntry(
                            key,
                            key.StorageName,
                            relativePath,
                            true,
                            size,
                            fileCount,
                            lastWrite,
                            truncated,
                            false,
                            "項目內含連結或重新解析點，為避免越界存取而拒絕操作。");
                    }

                    if (child.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        pending.Push(((DirectoryInfo)child, depth + 1));
                        continue;
                    }

                    fileCount++;
                    var childLength = Math.Max(0, ((FileInfo)child).Length);
                    size = SaturatingAdd(size, childLength);
                    if (inspectionBudget is not null &&
                        !inspectionBudget.TryConsumeBytes(childLength))
                    {
                        truncated = true;
                        pending.Clear();
                        break;
                    }

                    if (size >= _limits.MaximumInspectionBytesPerItem)
                    {
                        truncated = true;
                        pending.Clear();
                        break;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new MinecraftClientContentEntry(
                key,
                key.StorageName,
                relativePath,
                true,
                size,
                fileCount,
                lastWrite,
                truncated,
                false,
                "無法安全檢查此項目：" + exception.Message);
        }

        return new MinecraftClientContentEntry(
            key,
            key.StorageName,
            relativePath,
            true,
            size,
            fileCount,
            lastWrite,
            truncated,
            true);
    }

    private IReadOnlyList<ImportSource> ResolveImportSources(MinecraftClientContentImportRequest request)
    {
        var sources = new List<ImportSource>(request.SourcePaths.Count);
        var names = new HashSet<string>(NameComparer);
        foreach (var requestedPath in request.SourcePaths)
        {
            if (string.IsNullOrWhiteSpace(requestedPath) || !Path.IsPathFullyQualified(requestedPath))
            {
                throw new ArgumentException("Every import source must be an absolute path.", nameof(request));
            }

            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
            if (SafePath.IsWithinRoot(_instanceRoot, path))
            {
                throw new UnauthorizedAccessException(
                    "Content cannot be imported from inside the managed client instance.");
            }

            if (!PathExists(path))
            {
                throw new FileNotFoundException("The content import source does not exist.", path);
            }

            EnsureExistingPathHasNoReparseComponents(path);
            var attributes = File.GetAttributes(path);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var name = isDirectory
                ? new DirectoryInfo(path).Name
                : Path.GetFileName(path);
            ValidateStorageName(name);
            if (!IsSupportedItem(request.Kind, name, isDirectory))
            {
                throw new InvalidDataException(
                    $"'{name}' is not a supported {request.Kind} item.");
            }

            if (!names.Add(name))
            {
                throw new InvalidOperationException($"The import contains duplicate item name '{name}'.");
            }

            sources.Add(new ImportSource(path, name));
        }

        return sources;
    }

    private async Task CopySourceAsync(
        MinecraftClientContentKind kind,
        string source,
        string destination,
        CopyBudget budget,
        CancellationToken cancellationToken)
    {
        var attributes = File.GetAttributes(source);
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            await CopyFileAsync(kind, source, destination, budget, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        Directory.CreateDirectory(destination);
        budget.ReserveDirectory();
        var pending = new Stack<(string Source, string Destination, int Depth)>();
        pending.Push((source, destination, 0));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            if (current.Depth > _limits.MaximumDirectoryDepth)
            {
                throw new InvalidDataException(
                    $"Content import exceeds the maximum directory depth of {_limits.MaximumDirectoryDepth}.");
            }

            EnsureExistingPathHasNoReparseComponents(current.Source);
            foreach (var child in new DirectoryInfo(current.Source).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new UnauthorizedAccessException(
                        $"Content import contains a link or reparse point: '{child.FullName}'.");
                }

                ValidateStorageName(child.Name);
                var childDestination = SafePath.CombineUnderRoot(current.Destination, child.Name);
                if (child.Attributes.HasFlag(FileAttributes.Directory))
                {
                    budget.ReserveDirectory();
                    Directory.CreateDirectory(childDestination);
                    pending.Push((child.FullName, childDestination, current.Depth + 1));
                }
                else
                {
                    await CopyFileAsync(kind, child.FullName, childDestination, budget, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private async Task CopyFileAsync(
        MinecraftClientContentKind kind,
        string source,
        string destination,
        CopyBudget budget,
        CancellationToken cancellationToken)
    {
        EnsureExistingPathHasNoReparseComponents(source);
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = input.Length;
        var singleFileLimit = kind == MinecraftClientContentKind.Screenshot
            ? Math.Min(_limits.MaximumSingleFileBytes, _limits.MaximumScreenshotBytes)
            : _limits.MaximumSingleFileBytes;
        budget.ReserveFile(length, singleFileLimit);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
        await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (input.Position != length)
        {
            throw new IOException("Content source changed while it was being imported.");
        }

        EnsureExistingPathHasNoReparseComponents(source);
    }

    private void EnsureNoDestinationConflicts(
        MinecraftClientContentKind kind,
        IEnumerable<string> names)
    {
        var enabledRoot = GetEnabledRoot(kind);
        var disabledRoot = GetDisabledRoot(kind);
        foreach (var name in names)
        {
            RejectExistingPath(SafePath.CombineUnderRoot(enabledRoot, name));
            RejectExistingPath(SafePath.CombineUnderRoot(disabledRoot, name));
        }
    }

    private string ResolveActiveItem(MinecraftClientContentItemKey key, bool requireExists)
    {
        ValidateActiveKey(key);
        var root = key.State == MinecraftClientContentState.Enabled
            ? GetEnabledRoot(key.Kind)
            : GetDisabledRoot(key.Kind);
        var candidate = SafePath.CombineUnderRoot(root, key.StorageName);
        if (requireExists && !PathExists(candidate))
        {
            throw new FileNotFoundException("The managed content item no longer exists.", candidate);
        }

        if (PathExists(candidate))
        {
            SafePath.EnsureNoReparsePointsUnderRoot(root, candidate);
        }

        return candidate;
    }

    private async Task<(string Slot, string Payload, RecycleManifest Manifest)> ResolveRecycleSlotAsync(
        MinecraftClientContentItemKey key,
        CancellationToken cancellationToken)
    {
        ValidateRecycledKey(key);
        var slot = SafePath.CombineUnderRoot(_recycleRoot, key.RecycleId!.Value.ToString("N"));
        if (!Directory.Exists(slot))
        {
            throw new DirectoryNotFoundException("The recycled content item no longer exists.");
        }

        SafePath.EnsureNoReparsePointsUnderRoot(_recycleRoot, slot);
        var manifest = await TryReadManifestAsync(slot, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The recycled content manifest is invalid.");
        if (manifest.RecycleId != key.RecycleId || manifest.Kind != key.Kind ||
            !NameComparer.Equals(manifest.StorageName, key.StorageName))
        {
            throw new UnauthorizedAccessException("The recycled content key does not match its manifest.");
        }

        var payload = SafePath.CombineUnderRoot(slot, PayloadName);
        if (!PathExists(payload))
        {
            throw new FileNotFoundException("The recycled content payload no longer exists.", payload);
        }

        SafePath.EnsureNoReparsePointsUnderRoot(slot, payload);
        return (slot, payload, manifest);
    }

    private async Task PermanentlyDeleteRecycledAsync(
        MinecraftClientContentItemKey key,
        CancellationToken cancellationToken)
    {
        ValidateRecycledKey(key);
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (slot, _, _) = await ResolveRecycleSlotAsync(key, cancellationToken)
                .ConfigureAwait(false);
            SafePath.DeleteTreeWithoutFollowingReparsePoints(_recycleRoot, slot);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private string GetEnabledRoot(MinecraftClientContentKind kind)
    {
        var root = SafePath.CombineUnderRoot(_instanceRoot, GetDirectoryName(kind));
        CreateAndValidateManagedDirectory(root);
        return root;
    }

    private string GetDisabledRoot(MinecraftClientContentKind kind)
    {
        var root = SafePath.CombineUnderRoot(_disabledRoot, GetDirectoryName(kind));
        CreateAndValidateManagedDirectory(root);
        return root;
    }

    private void CreateAndValidateManagedDirectory(string path)
    {
        Directory.CreateDirectory(path);
        SafePath.EnsureNoReparsePointsUnderRoot(_instanceRoot, path);
    }

    private void ValidateManagedRoot(string path)
        => SafePath.EnsureNoReparsePointsUnderRoot(_instanceRoot, path);

    private static string GetDirectoryName(MinecraftClientContentKind kind) => kind switch
    {
        MinecraftClientContentKind.Mod => "mods",
        MinecraftClientContentKind.ResourcePack => "resourcepacks",
        MinecraftClientContentKind.ShaderPack => "shaderpacks",
        MinecraftClientContentKind.Save => "saves",
        MinecraftClientContentKind.Screenshot => "screenshots",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported client content kind."),
    };

    private static string GetRelativeDisplayPath(
        MinecraftClientContentKind kind,
        MinecraftClientContentState state,
        string name)
        => state == MinecraftClientContentState.Enabled
            ? $"{GetDirectoryName(kind)}/{name}"
            : $"{ManagementDirectoryName}/disabled/{GetDirectoryName(kind)}/{name}";

    private static bool IsSupportedItem(
        MinecraftClientContentKind kind,
        string name,
        bool isDirectory)
    {
        var extension = Path.GetExtension(name);
        return kind switch
        {
            MinecraftClientContentKind.Mod => !isDirectory &&
                string.Equals(extension, ".jar", StringComparison.OrdinalIgnoreCase),
            MinecraftClientContentKind.ResourcePack or MinecraftClientContentKind.ShaderPack =>
                isDirectory || string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase),
            MinecraftClientContentKind.Save => isDirectory,
            MinecraftClientContentKind.Screenshot => !isDirectory && extension is not null &&
                (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    private static void ValidateKind(MinecraftClientContentKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported client content kind.");
        }
    }

    private void ValidateImportRequest(MinecraftClientContentImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateKind(request.Kind);
        ArgumentNullException.ThrowIfNull(request.SourcePaths);
        if (request.SourcePaths.Count is < 1 || request.SourcePaths.Count > 64 ||
            request.SourcePaths.Count > _limits.MaximumImportSources)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Import must contain 1-{_limits.MaximumImportSources} sources.");
        }
    }

    private static void ValidateActiveKey(MinecraftClientContentItemKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateKind(key.Kind);
        if (key.State is not (MinecraftClientContentState.Enabled or MinecraftClientContentState.Disabled) ||
            key.RecycleId is not null)
        {
            throw new ArgumentException("The content key does not identify an active item.", nameof(key));
        }

        ValidateStorageName(key.StorageName);
    }

    private static void ValidateRecycledKey(MinecraftClientContentItemKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateKind(key.Kind);
        if (key.State != MinecraftClientContentState.Recycled || key.RecycleId is null ||
            key.RecycleId == Guid.Empty)
        {
            throw new ArgumentException("The content key does not identify a recycled item.", nameof(key));
        }

        ValidateStorageName(key.StorageName);
    }

    private static void ValidateStorageName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 180 || name is "." or ".." ||
            !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) ||
            name.EndsWith(' ') || name.EndsWith('.') ||
            name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)))
        {
            throw new ArgumentException("The content item name is unsafe.", nameof(name));
        }

        var baseName = name.Split('.', 2)[0];
        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (baseName.Length == 4 &&
             (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
              baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             baseName[3] is >= '1' and <= '9'))
        {
            throw new ArgumentException("The content item name is a reserved device name.", nameof(name));
        }
    }

    private static void ValidateLimits(MinecraftClientContentLimits limits)
    {
        if (limits.MaximumItemsPerCategory is < 1 or > 100_000 ||
            limits.MaximumImportSources is < 1 or > 1_024 ||
            limits.MaximumImportFiles is < 1 or > 1_000_000 ||
            limits.MaximumImportBytes < 1 ||
            limits.MaximumSingleFileBytes < 1 ||
            limits.MaximumScreenshotBytes < 1 ||
            limits.MaximumDirectoryDepth is < 1 or > 256 ||
            limits.MaximumInspectionFilesPerItem is < 1 or > 1_000_000 ||
            limits.MaximumInspectionBytesPerItem < 1 ||
            limits.MaximumSnapshotInspectionEntries is < 1 or > 10_000_000 ||
            limits.MaximumSnapshotInspectionBytes < 1 ||
            limits.MaximumSnapshotInspectionMilliseconds is < 1 or > 60_000 ||
            limits.MaximumRecycleCandidates is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Client content limits are invalid.");
        }
    }

    private static void RejectExistingPath(string path)
    {
        if (PathExists(path))
        {
            throw new IOException($"A content item with the same name already exists: '{path}'.");
        }
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void Move(string source, string destination)
    {
        var attributes = File.GetAttributes(source);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException("Content links and reparse points cannot be moved.");
        }

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static void DeleteActiveItem(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            var parent = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("Content item has no parent directory.");
            SafePath.DeleteTreeWithoutFollowingReparsePoints(parent, path);
        }
        else
        {
            File.Delete(path);
        }
    }

    private static void RejectReparsePoint(string path, string label)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException($"{label} cannot be a reparse point: '{path}'.");
        }
    }

    private static void EnsureExistingPathHasNoReparseComponents(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("Path has no filesystem root.", nameof(path));
        var relative = Path.GetRelativePath(root, fullPath);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!PathExists(current))
            {
                throw new FileNotFoundException("An import path component no longer exists.", current);
            }

            RejectReparsePoint(current, "Content import source");
        }
    }

    private async Task<RecycleManifest?> TryReadManifestAsync(
        string slot,
        CancellationToken cancellationToken)
    {
        var manifestPath = SafePath.CombineUnderRoot(slot, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        SafePath.EnsureNoReparsePointsUnderRoot(slot, manifestPath);
        var info = new FileInfo(manifestPath);
        if (info.Length is < 2 or > 16 * 1024)
        {
            return null;
        }

        await using var input = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        RecycleManifest? manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync<RecycleManifest>(
                    input,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }

        if (manifest is null || manifest.SchemaVersion != 1 || manifest.RecycleId == Guid.Empty ||
            manifest.OriginalState is not (MinecraftClientContentState.Enabled or
                MinecraftClientContentState.Disabled))
        {
            return null;
        }

        try
        {
            ValidateKind(manifest.Kind);
            ValidateStorageName(manifest.StorageName);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return manifest;
    }

    private static async Task WriteManifestAtomicallyAsync(
        string slot,
        RecycleManifest manifest,
        CancellationToken cancellationToken)
    {
        var temporary = SafePath.CombineUnderRoot(slot, $"{ManifestFileName}.{Guid.NewGuid():N}.tmp");
        var destination = SafePath.CombineUnderRoot(slot, ManifestFileName);
        try
        {
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, manifest, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destination);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
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
    }

    private static long SaturatingAdd(long left, long right)
        => left > long.MaxValue - right ? long.MaxValue : left + right;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record ImportSource(string Path, string Name);

    private sealed record StagedImport(string Name, string StagedPath);

    private sealed record RecycleManifest(
        int SchemaVersion,
        Guid RecycleId,
        MinecraftClientContentKind Kind,
        MinecraftClientContentState OriginalState,
        string StorageName,
        DateTimeOffset RecycledAtUtc);

    private sealed class InspectionBudget
    {
        private readonly MinecraftClientContentLimits _limits;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private int _entries;
        private long _bytes;

        public InspectionBudget(MinecraftClientContentLimits limits)
        {
            _limits = limits;
        }

        public bool IsExhausted { get; private set; }

        public bool TryConsumeEntry()
        {
            if (!CanContinue() || _entries >= _limits.MaximumSnapshotInspectionEntries)
            {
                IsExhausted = true;
                return false;
            }

            _entries++;
            return true;
        }

        public bool TryConsumeBytes(long bytes)
        {
            if (bytes < 0 || !CanContinue() ||
                _bytes > _limits.MaximumSnapshotInspectionBytes - bytes)
            {
                IsExhausted = true;
                return false;
            }

            _bytes += bytes;
            return true;
        }

        private bool CanContinue()
            => !IsExhausted &&
               _elapsed.ElapsedMilliseconds < _limits.MaximumSnapshotInspectionMilliseconds;
    }

    private sealed class CopyBudget
    {
        private readonly MinecraftClientContentLimits _limits;

        public CopyBudget(MinecraftClientContentLimits limits)
        {
            _limits = limits;
        }

        public int Files { get; private set; }

        public int Entries { get; private set; }

        public long CopiedBytes { get; private set; }

        public void ReserveFile(long length, long singleFileLimit)
        {
            if (length < 0 || length > singleFileLimit)
            {
                throw new InvalidDataException(
                    $"Content file exceeds the per-file limit of {singleFileLimit} bytes.");
            }

            if (Entries >= _limits.MaximumImportFiles ||
                CopiedBytes > _limits.MaximumImportBytes - length)
            {
                throw new InvalidDataException("Content import exceeds its safe file-count or size limit.");
            }

            Files++;
            Entries++;
            CopiedBytes += length;
        }

        public void ReserveDirectory()
        {
            if (Entries >= _limits.MaximumImportFiles)
            {
                throw new InvalidDataException("Content import exceeds its safe entry-count limit.");
            }

            Entries++;
        }
    }
}

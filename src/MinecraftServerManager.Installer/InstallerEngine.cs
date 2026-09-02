using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;
using MinecraftServerManager.Updater;

namespace MinecraftServerManager.Installer;

internal sealed record InstallerProgress(int Percentage, string Message);

internal sealed class InstallerStageException : Exception
{
    public InstallerStageException(
        string stage,
        string targetPath,
        bool rollbackHadErrors,
        Exception innerException)
        : base(CreateMessage(stage, targetPath, rollbackHadErrors, innerException), innerException)
    {
        Stage = stage;
        TargetPath = targetPath;
        RollbackHadErrors = rollbackHadErrors;
    }

    public string Stage { get; }
    public string TargetPath { get; }
    public bool RollbackHadErrors { get; }

    private static string CreateMessage(
        string stage,
        string targetPath,
        bool rollbackHadErrors,
        Exception innerException)
    {
        var rollback = rollbackHadErrors
            ? "部分回復步驟未完成；受保護的安裝資料可能保留。"
            : "已執行已登記的回復步驟；不宣稱所有暫存均已刪除。";
        return
            $"X MCSV 安裝階段失敗：{stage}{Environment.NewLine}" +
            $"位置：{Path.GetFullPath(targetPath)}{Environment.NewLine}" +
            $"回復狀態：{rollback}{Environment.NewLine}" +
            $"原因：{innerException.Message}";
    }
}

internal sealed record InstallerServiceSnapshot(
    bool Existed,
    string? ImagePath,
    bool WasRunning,
    string? SecurityDescriptor,
    bool DelayedAutoStart);

internal sealed record InstallerLauncherRollback(
    string DestinationPath,
    string? BackupPath,
    bool Created);

internal sealed record InstallerRegistryValueSnapshot(
    bool Existed,
    object? Value,
    RegistryValueKind Kind);

internal sealed record InstallerRegistrationSnapshot(
    bool KeyExisted,
    IReadOnlyDictionary<string, InstallerRegistryValueSnapshot> Values);

internal sealed record InstallerShortcutSnapshot(
    string ProductDirectory,
    string ShortcutPath,
    bool ProductDirectoryExisted,
    bool ShortcutExisted,
    byte[]? Content);

internal sealed record InstallerShellIntegrationRollback(
    InstallerRegistrationSnapshot Registration,
    InstallerShortcutSnapshot Shortcut);

internal sealed class InstallerOperatorAccessRollback
{
    public required string GroupName { get; init; }
    public required string GroupDescription { get; init; }
    public required string InstallerSid { get; init; }
    public required string BindingPath { get; init; }
    public required bool BindingExisted { get; init; }
    public byte[]? PreviousBindingContent { get; init; }
    public string? PreviousBindingSecurityDescriptor { get; init; }
    public required byte[] IntendedBindingContent { get; init; }
    public string? IntendedBindingSecurityDescriptor { get; set; }
    public bool BindingWriteAttempted { get; set; }
    public byte[]? InstalledBindingContent { get; set; }
    public string? InstalledBindingSecurityDescriptor { get; set; }
    public string? HardenedBindingSecurityDescriptor { get; set; }
    public bool BindingAclMutationAttempted { get; set; }
    public string? GroupSid { get; set; }
    public bool GroupCreated { get; set; }
    public bool MemberAdded { get; set; }
}

internal sealed record InstallerActivationReadyResponse(
    string Status,
    string Product,
    string Version,
    Guid InstallationId,
    DateTimeOffset StartedAtUtc,
    bool Ready,
    InstallerActivationFailureDiagnostic? StartupFailure = null);

internal sealed record InstallerActivationFailureDiagnostic(
    string Code,
    string ExceptionType,
    int HResult,
    string? InnerExceptionType,
    int? InnerHResult);

internal sealed record InstallerAclGrant(
    SecurityIdentifier Sid,
    FileSystemRights Rights,
    InheritanceFlags InheritanceFlags = InheritanceFlags.None,
    PropagationFlags PropagationFlags = PropagationFlags.None);

internal interface IInstallerRootLease : IDisposable
{
    bool RootCreated { get; }
    void ValidateAndPinExistingManagedInstallation(
        InstallerLayout layout,
        string targetVersion,
        string currentUserSid,
        string serviceName);
    void ProtectOwnedRootAndPinExistingDirectories(IReadOnlyList<string> directories);
    void CreateAndProtectMissingDirectories();
    void ProtectOwnedRootAndDirectories(IReadOnlyList<string> directories);
    void PinAndHardenExistingVersionTree(
        string directory,
        int maximumEntries = 100_000,
        int maximumDepth = 64);
    void PinAndHardenNewVersionTree(
        string directory,
        int maximumEntries = 100_000,
        int maximumDepth = 64);
    void ReleaseFileForReplacement(string file);
    void ReleaseDirectoryForDeletion(string directory);
    void CommitProtectionChanges();
    void RollbackProtectionChanges();
    void DeleteNewRootIfEmpty();
}

/// <summary>
/// A machine-wide installer lease whose named mutex is owned exclusively by a dedicated thread.
/// Installation can resume on any worker after an await; disposal only signals the owner thread,
/// which releases the mutex itself. A process or owner-thread crash retains normal abandoned-mutex
/// recovery rather than leaving a semaphore count permanently consumed.
/// </summary>
internal sealed record InstallerMutexSecurityPolicy(
    MutexSecurity CreationSecurity,
    IReadOnlySet<SecurityIdentifier> AllowedOwners,
    IReadOnlySet<SecurityIdentifier> FullControlPrincipals);

internal sealed class InstallerProcessLock : IDisposable, IAsyncDisposable
{
    private readonly ManualResetEventSlim _releaseRequested;
    private readonly Thread _ownerThread;
    private readonly Task _ownerStopped;
    private int _disposeRequested;

    private InstallerProcessLock(
        ManualResetEventSlim releaseRequested,
        Thread ownerThread,
        Task ownerStopped,
        int ownerThreadId)
    {
        _releaseRequested = releaseRequested;
        _ownerThread = ownerThread;
        _ownerStopped = ownerStopped;
        OwnerThreadId = ownerThreadId;
    }

    internal int OwnerThreadId { get; }

    public static Task<InstallerProcessLock> AcquireAsync(string name)
        => AcquireAsync(name, CreateMachineMutexSecurityPolicy());

    internal static Task<InstallerProcessLock> AcquireAsync(
        string name,
        InstallerMutexSecurityPolicy securityPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(securityPolicy);
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromException<InstallerProcessLock>(
                new PlatformNotSupportedException("X MCSV 安裝鎖僅支援 Windows。"));
        }

        var acquired = new TaskCompletionSource<InstallerProcessLock>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerStopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequested = new ManualResetEventSlim(initialState: false);
        var ownerThread = new Thread(() => RunOwnerThread(
            name,
            securityPolicy,
            releaseRequested,
            acquired,
            ownerStopped))
        {
            IsBackground = true,
            Name = "X MCSV installer lock owner",
        };

        try
        {
            ownerThread.Start();
        }
        catch (ThreadStateException error)
        {
            releaseRequested.Dispose();
            acquired.TrySetException(new InvalidOperationException(
                "無法啟動 X MCSV 安裝鎖執行緒；安裝未開始。",
                error));
        }

        return acquired.Task;
    }

    internal static InstallerMutexSecurityPolicy CreateMachineMutexSecurityPolicy()
    {
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            domainSid: null);
        var localSystem = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            domainSid: null);
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        foreach (var sid in new[] { administrators, localSystem })
        {
            security.AddAccessRule(new MutexAccessRule(
                sid,
                MutexRights.FullControl,
                AccessControlType.Allow));
        }

        return new InstallerMutexSecurityPolicy(
            security,
            new HashSet<SecurityIdentifier> { administrators, localSystem },
            new HashSet<SecurityIdentifier> { administrators, localSystem });
    }

    public void Dispose()
    {
        RequestRelease();
        _ownerStopped.GetAwaiter().GetResult();
        JoinOwnerThread();
    }

    public async ValueTask DisposeAsync()
    {
        RequestRelease();
        await _ownerStopped.ConfigureAwait(false);
        JoinOwnerThread();
    }

    private void RequestRelease()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) == 0)
        {
            try
            {
                _releaseRequested.Set();
            }
            catch (ObjectDisposedException)
            {
                // The owner always completes _ownerStopped in its finally block. If it already
                // stopped unexpectedly, awaiting that completion remains the safe join path.
            }
        }
    }

    private static void RunOwnerThread(
        string name,
        InstallerMutexSecurityPolicy securityPolicy,
        ManualResetEventSlim releaseRequested,
        TaskCompletionSource<InstallerProcessLock> acquired,
        TaskCompletionSource ownerStopped)
    {
        Mutex? mutex = null;
        var ownsMutex = false;
        try
        {
            mutex = MutexAcl.Create(
                initiallyOwned: false,
                name,
                out _,
                securityPolicy.CreationSecurity);
            ValidateMutexSecurity(mutex, securityPolicy);
            try
            {
                ownsMutex = mutex.WaitOne(millisecondsTimeout: 0);
            }
            catch (AbandonedMutexException)
            {
                // Windows transfers ownership to this thread before reporting abandonment.
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                throw new InvalidOperationException("已有另一個 X MCSV 安裝工作正在執行。");
            }

            var lease = new InstallerProcessLock(
                releaseRequested,
                Thread.CurrentThread,
                ownerStopped.Task,
                Environment.CurrentManagedThreadId);
            acquired.TrySetResult(lease);
            releaseRequested.Wait();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            acquired.TrySetException(error is InvalidOperationException
                ? error
                : new InvalidOperationException("無法安全取得 X MCSV 安裝鎖；安裝未開始。", error));
        }
        finally
        {
            if (ownsMutex && mutex is not null)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Never replace the installation result with a cleanup exception. Exiting
                    // this owner thread still gives Windows abandoned-mutex recovery semantics.
                }
            }

            mutex?.Dispose();
            releaseRequested.Dispose();
            ownerStopped.TrySetResult();
        }
    }

    private void JoinOwnerThread()
    {
        if (!ReferenceEquals(Thread.CurrentThread, _ownerThread))
        {
            _ownerThread.Join();
        }
    }

    private static void ValidateMutexSecurity(
        Mutex mutex,
        InstallerMutexSecurityPolicy securityPolicy)
    {
        var actual = mutex.GetAccessControl();
        if (actual.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !actual.AreAccessRulesProtected ||
            !securityPolicy.AllowedOwners.Contains(owner))
        {
            throw new UnauthorizedAccessException("X MCSV 安裝鎖的擁有者或 ACL 不可信任。");
        }

        var fullControl = new HashSet<SecurityIdentifier>();
        foreach (MutexAccessRule rule in actual.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: false,
                     typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (rule.AccessControlType != AccessControlType.Allow ||
                !securityPolicy.FullControlPrincipals.Contains(sid) ||
                (rule.MutexRights & MutexRights.FullControl) != MutexRights.FullControl)
            {
                throw new UnauthorizedAccessException("X MCSV 安裝鎖的 ACL 不可信任。");
            }
            fullControl.Add(sid);
        }

        if (!securityPolicy.FullControlPrincipals.SetEquals(fullControl))
        {
            throw new UnauthorizedAccessException("X MCSV 安裝鎖的 ACL 不完整。");
        }
    }
}

internal sealed class InstallerEngine
{
    private const string ServiceName = "MuhunMCSV";
    internal static readonly string[] ServiceRuntimeDirectoryNames =
    [
        "data", "secrets", "operations", "servers", "runtimes",
        "backups", "updates", "plugins", "logs",
    ];
    internal static readonly string[] UserRuntimeDirectoryNames =
        ["client", "cache", "logs", "crash-reports", "runtimes", "themes"];
    private readonly IInstallerPlatform _platform;

    public InstallerEngine(IInstallerPlatform? platform = null)
    {
        _platform = platform ?? new WindowsInstallerPlatform();
    }

    public async Task<string> InstallAsync(
        InstallerBundle bundle,
        string selectedRoot,
        IProgress<InstallerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (!_platform.IsAdministrator())
        {
            throw new UnauthorizedAccessException("安裝 X MCSV 需要系統管理員權限。");
        }
        if (!_platform.IsCurrentIdentityInteractiveShellUser())
        {
            throw new UnauthorizedAccessException(
                "請使用目前登入 Windows 的同一帳號直接確認 UAC；不可在 UAC 輸入另一個管理員帳號。");
        }

        var layout = InstallerLayout.Resolve(selectedRoot, bundle.Metadata.Channel, _platform.CurrentUserSid);
        // Beta and stable share the same service, launcher and active pointer.  A single
        // machine-wide installer lock prevents two channels from mutating that shared
        // installation boundary at the same time.
        const string installLockName = @"Global\Muhun.MCSV.Installer";
        await using var installLock = await InstallerProcessLock.AcquireAsync(installLockName)
            .ConfigureAwait(false);

        var stage = Path.Combine(layout.StagingRoot, Guid.NewGuid().ToString("N"));
        var stagedVersion = Path.Combine(stage, "version");
        var packagePath = Path.Combine(stage, bundle.Metadata.PackageFileName);
        var targetVersion = Path.Combine(layout.VersionsRoot, bundle.Metadata.Version);
        var activePointer = Path.Combine(layout.Root, "active-version.v1");
        IInstallerRootLease? rootLease = null;
        var rootExistedBefore = true;
        var targetCreated = false;
        var pointerCreatedForAcl = false;
        var pointerCommitted = false;
        var rootProtectionCommitted = false;
        var installMarkerCreated = false;
        var createdRuntimeMarkers = new List<string>();
        string? previousActiveVersion = null;
        InstallerLauncherRollback? launcherRollback = null;
        InstallerServiceSnapshot? serviceSnapshot = null;
        InstallerShellIntegrationRollback? shellIntegrationRollback = null;
        InstallerOperatorAccessRollback? operatorAccessRollback = null;
        var failureStage = "取得安裝根目錄 lease";
        var failurePath = layout.Root;
        try
        {
            progress?.Report(new InstallerProgress(3, "正在檢查安裝位置…"));
            rootLease = _platform.AcquireInstallRootLease(layout.Root);
            rootExistedBefore = !rootLease.RootCreated;
            rootLease.ValidateAndPinExistingManagedInstallation(
                layout,
                targetVersion,
                _platform.CurrentUserSid,
                ServiceName);
            failureStage = "擷取並停止既有 Windows Service";
            serviceSnapshot = await _platform.CaptureAndStopServiceAsync(
                    ServiceName,
                    layout.Root,
                    snapshot => serviceSnapshot = snapshot,
                    cancellationToken)
                .ConfigureAwait(false);
            failureStage = "準備受管理安裝根目錄";
            installMarkerCreated = PrepareOwnedRoot(layout, rootLease);
            previousActiveVersion = ReadOptionalActiveVersion(activePointer);
            failureStage = "建立安裝暫存目錄";
            failurePath = stage;
            Directory.CreateDirectory(stage);
            File.WriteAllText(
                Path.Combine(stage, ".muhun-mcsv-install-staging"),
                "muhun.mcsv.install-staging:1\n",
                new UTF8Encoding(false));
            InstallerLayout.RejectExistingReparsePoints(stage);

            progress?.Report(new InstallerProgress(12, "正在驗證單一 EXE 內含資料…"));
            failureStage = "複製並驗證內含套件";
            failurePath = packagePath;
            await bundle.CopyPackageToAsync(packagePath, cancellationToken).ConfigureAwait(false);

            progress?.Report(new InstallerProgress(28, "正在建立 Beta 版本檔案…"));
            failureStage = "解壓並驗證版本檔案";
            failurePath = stagedVersion;
            await using (var package = new FileStream(
                             packagePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await new SafeProductPackageExtractor()
                    .ExtractAndVerifyAsync(package, stagedVersion, bundle.Manifest, cancellationToken)
                    .ConfigureAwait(false);
            }

            ProductInstalledVersionMetadataStore.Write(
                stagedVersion,
                new ProductInstalledVersionMetadata(
                    1,
                    "muhun.mcsv.manager",
                    bundle.Metadata.Version,
                    bundle.Manifest.EntryPoint));
            await ProductInstalledVersionVerifier.VerifyAsync(
                    stagedVersion,
                    bundle.Manifest,
                    cancellationToken,
                    requireVersionDirectoryName: false)
                .ConfigureAwait(false);

            progress?.Report(new InstallerProgress(54, "正在啟用版本目錄…"));
            failureStage = "啟用版本目錄";
            failurePath = targetVersion;
            if (Directory.Exists(targetVersion))
            {
                rootLease.ProtectOwnedRootAndDirectories([targetVersion]);
                rootLease.PinAndHardenExistingVersionTree(targetVersion);
                await ProductInstalledVersionVerifier.VerifyAsync(
                        targetVersion,
                        bundle.Manifest,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await MoveStagedVersionWithTransientLockRetryAsync(
                        stagedVersion,
                        targetVersion,
                        cancellationToken)
                    .ConfigureAwait(false);
                targetCreated = true;
                rootLease.ProtectOwnedRootAndDirectories([targetVersion]);
                rootLease.PinAndHardenNewVersionTree(targetVersion);
                await ProductInstalledVersionVerifier.VerifyAsync(
                        targetVersion,
                        bundle.Manifest,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var serviceExecutable = Path.Combine(
                targetVersion,
                "service-win-x64",
                "Muhun MCSV Service.exe");
            var updaterExecutable = Path.Combine(
                targetVersion,
                "updater-win-x64",
                "Muhun MCSV Updater.exe");
            var guiExecutable = Path.Combine(
                targetVersion,
                bundle.Manifest.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
            foreach (var required in new[] { serviceExecutable, updaterExecutable, guiExecutable })
            {
                if (!File.Exists(required))
                {
                    throw new FileNotFoundException("版本套件缺少必要的 X MCSV 執行檔。", required);
                }
            }

            progress?.Report(new InstallerProgress(64, "正在建立 Service、客戶端與交換資料目錄…"));
            failureStage = "建立執行階段資料目錄";
            failurePath = layout.Root;
            PrepareRuntimeDirectories(layout, createdRuntimeMarkers);
            var launcherPath = Path.Combine(layout.LauncherRoot, "Muhun MCSV Updater.exe");
            failureStage = "安裝啟動器";
            failurePath = launcherPath;
            launcherRollback = InstallLauncherTransactionally(updaterExecutable, launcherPath, stage);
            if (!File.Exists(activePointer))
            {
                WriteAtomicText(activePointer, "pending" + Environment.NewLine);
                pointerCreatedForAcl = true;
            }

            progress?.Report(new InstallerProgress(75, "正在設定 Windows Service 權限…"));
            failureStage = "設定 Windows Service 與檔案權限";
            failurePath = serviceExecutable;
            operatorAccessRollback = _platform.ProvisionOperatorAccess(
                layout,
                snapshot => operatorAccessRollback = snapshot);
            await _platform.ConfigureServiceAsync(
                    ServiceName,
                    serviceExecutable,
                    layout.ServiceRoot,
                    layout.ExchangeRoot,
                    layout.Root,
                    serviceSnapshot,
                    cancellationToken)
                .ConfigureAwait(false);
            _platform.HardenOperatorBindingAccess(
                layout,
                ServiceName,
                operatorAccessRollback);
            _platform.ApplyAccessControl(
                layout,
                ServiceName,
                targetVersion,
                operatorAccessRollback.GroupSid
                    ?? throw new InvalidOperationException("本機操作員群組 SID 尚未建立。"));

            progress?.Report(new InstallerProgress(84, "正在啟動背景服務…"));
            failureStage = "啟動並驗證背景服務";
            failurePath = serviceExecutable;
            await _platform.StartServiceAsync(ServiceName, cancellationToken).ConfigureAwait(false);
            await _platform.WaitForServiceHealthAsync(
                    serviceExecutable,
                    layout.ServiceRoot,
                    bundle.Metadata.Version,
                    cancellationToken)
                .ConfigureAwait(false);

            failureStage = "提交啟用版本指標";
            failurePath = activePointer;
            rootLease.ReleaseFileForReplacement(activePointer);
            WriteAtomicText(activePointer, bundle.Metadata.Version + Environment.NewLine);
            pointerCommitted = true;
            _platform.ApplyActivePointerAccessControl(layout, ServiceName);
            failureStage = "登錄程式與建立開始功能表捷徑";
            failurePath = layout.Root;
            shellIntegrationRollback = InstallerShellIntegrationTransaction.Apply(
                _platform,
                layout,
                bundle.Metadata.Version,
                guiExecutable,
                launcherPath,
                snapshot => shellIntegrationRollback = snapshot);

            progress?.Report(new InstallerProgress(96, "正在清除安裝暫存…"));
            failureStage = "結束安裝暫存";
            failurePath = stage;
            TryDeleteOwnedStage(stage, layout.StagingRoot);
            rootLease.CommitProtectionChanges();
            rootProtectionCommitted = true;
            progress?.Report(new InstallerProgress(100, "安裝完成。"));
            return guiExecutable;
        }
        catch (Exception installationError)
        {
            var rollbackErrors = new List<Exception>();
            var serviceQuiesced = serviceSnapshot is null;
            if (serviceSnapshot is not null)
            {
                try
                {
                    await _platform.RestoreServiceAsync(
                            ServiceName,
                            serviceSnapshot,
                            restart: false,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    serviceQuiesced = true;
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }
            try
            {
                InstallerShellIntegrationTransaction.Restore(_platform, shellIntegrationRollback);
            }
            catch (Exception rollbackError)
            {
                rollbackErrors.Add(rollbackError);
            }
            if (serviceQuiesced)
            {
                try
                {
                    _platform.RestoreOperatorAccess(operatorAccessRollback);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
                try
                {
                    RestoreLauncherTransaction(launcherRollback);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
                try
                {
                    RestoreActivePointer(
                        activePointer,
                        previousActiveVersion,
                        pointerCreatedForAcl || pointerCommitted);
                    if (serviceSnapshot?.Existed == true && previousActiveVersion is not null)
                    {
                        _platform.ApplyActivePointerAccessControl(layout, ServiceName);
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
                TryDeleteOwnedStage(stage, layout.StagingRoot);
                try
                {
                    if (targetCreated && !string.Equals(
                            ReadOptionalActiveVersion(activePointer),
                            bundle.Metadata.Version,
                            StringComparison.Ordinal))
                    {
                        rootLease?.ReleaseDirectoryForDeletion(targetVersion);
                        TryDeleteOwnedVersion(targetVersion, layout.VersionsRoot, bundle.Metadata.Version);
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
                foreach (var createdMarker in createdRuntimeMarkers.AsEnumerable().Reverse())
                {
                    TryDeleteOwnedRegularFile(createdMarker);
                }
                if (installMarkerCreated)
                {
                    TryDeleteOwnedRegularFile(Path.Combine(layout.Root, InstallerLayout.InstallMarkerName));
                }
                if (!rootProtectionCommitted && rootLease is not null)
                {
                    try
                    {
                        rootLease.RollbackProtectionChanges();
                    }
                    catch (Exception rollbackError)
                    {
                        rollbackErrors.Add(rollbackError);
                    }
                }
                if (!rootExistedBefore && rootLease is not null && rollbackErrors.Count == 0)
                {
                    try
                    {
                        rootLease.DeleteNewRootIfEmpty();
                    }
                    catch (Exception rollbackError)
                    {
                        rollbackErrors.Add(rollbackError);
                    }
                }
            }
            if (serviceSnapshot is not null && serviceQuiesced && rollbackErrors.Count == 0)
            {
                try
                {
                    await _platform.RestoreServiceAsync(
                            ServiceName,
                            serviceSnapshot,
                            restart: true,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }

            if (installationError is OperationCanceledException && rollbackErrors.Count == 0)
            {
                throw;
            }

            Exception failure = rollbackErrors.Count == 0
                ? installationError
                : new AggregateException(
                    "原始安裝錯誤與一或多個回復錯誤。",
                    [installationError, .. rollbackErrors]);
            throw new InstallerStageException(
                failureStage,
                failurePath,
                rollbackErrors.Count > 0,
                failure);
        }
        finally
        {
            rootLease?.Dispose();
        }
    }

    private static string? ReadOptionalActiveVersion(string path)
    {
        using var item = OpenOptionalLockedTextFile(path, 256, "active-version.v1");
        if (item is null)
        {
            return null;
        }

        var version = item.ReadText();
        ProductUpdateManifestParser.ValidateVersion(version);
        return version;
    }

    private sealed class LockedTextFile : IDisposable
    {
        private readonly FileStream _stream;
        private readonly int _maximumLength;
        private readonly string _description;

        public LockedTextFile(SafeFileHandle handle, int maximumLength, string description)
        {
            _stream = new FileStream(handle, FileAccess.Read, maximumLength, isAsync: false);
            _maximumLength = maximumLength;
            _description = description;
        }

        public string ReadText()
        {
            if (_stream.Length is < 1 || _stream.Length > _maximumLength)
            {
                throw new InvalidDataException($"{_description}大小無效。");
            }

            _stream.Position = 0;
            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)_stream.Length));
            _stream.ReadExactly(bytes);
            return new UTF8Encoding(false, true).GetString(bytes).Trim();
        }

        public void Dispose() => _stream.Dispose();
    }

    private static InstallerLauncherRollback InstallLauncherTransactionally(
        string source,
        string destination,
        string stage)
    {
        InstallerLayout.RejectExistingReparsePoints(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = Path.Combine(stage, "launcher-backup.exe");
        try
        {
            File.Copy(source, temporary, overwrite: false);
            if (File.Exists(destination))
            {
                InstallerLayout.RejectExistingReparsePoints(destination);
                File.Replace(temporary, destination, backup, ignoreMetadataErrors: true);
                return new InstallerLauncherRollback(destination, backup, Created: false);
            }

            File.Move(temporary, destination, overwrite: false);
            return new InstallerLauncherRollback(destination, BackupPath: null, Created: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void RestoreLauncherTransaction(InstallerLauncherRollback? rollback)
    {
        if (rollback is null)
        {
            return;
        }

        InstallerLayout.RejectExistingReparsePoints(Path.GetDirectoryName(rollback.DestinationPath)!);
        if (rollback.Created)
        {
            if (File.Exists(rollback.DestinationPath))
            {
                File.Delete(rollback.DestinationPath);
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(rollback.BackupPath) || !File.Exists(rollback.BackupPath))
        {
            throw new IOException("穩定 launcher 的回復副本遺失。");
        }

        var displaced = rollback.BackupPath + ".displaced";
        File.Replace(rollback.BackupPath, rollback.DestinationPath, displaced, ignoreMetadataErrors: true);
        if (File.Exists(displaced))
        {
            File.Delete(displaced);
        }
    }

    private static void RestoreActivePointer(
        string path,
        string? previousVersion,
        bool installerTouchedPointer)
    {
        if (!installerTouchedPointer)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(previousVersion))
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            return;
        }

        WriteAtomicText(path, previousVersion + Environment.NewLine);
    }

    private static bool PrepareOwnedRoot(InstallerLayout layout, IInstallerRootLease rootLease)
    {
        InstallerLayout.RejectExistingReparsePoints(layout.Root);
        var marker = Path.Combine(layout.Root, InstallerLayout.InstallMarkerName);
        using var existingMarker = OpenOptionalLockedTextFile(marker, 64, "安裝根標記");
        var existingMarkerValue = existingMarker?.ReadText();
        if (existingMarkerValue is not null)
        {
            if (!string.Equals(
                    existingMarkerValue,
                    InstallerLayout.InstallMarkerValue,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("現有安裝根目錄的 X MCSV 標記無效。");
            }
        }
        else if (!rootLease.RootCreated)
        {
            throw new IOException(
                "既有自訂安裝資料夾必須已有有效 X MCSV 管理標記；請改選尚不存在的新子目錄。");
        }
        else if (Directory.EnumerateFileSystemEntries(layout.Root).Any())
        {
            throw new IOException("選擇的安裝資料夾不是空白，也不是受管理的 X MCSV 目錄。");
        }

        string[] canonicalDirectories =
        [
            layout.VersionsRoot,
            layout.ActivationRoot,
            layout.ServiceRoot,
            layout.ExchangeRoot,
            layout.UserRoot,
            layout.StagingRoot,
            layout.LauncherRoot,
            .. ServiceRuntimeDirectoryNames.Select(name => Path.Combine(layout.ServiceRoot, name)),
            .. UserRuntimeDirectoryNames.Select(name => Path.Combine(layout.UserRoot, name)),
        ];
        rootLease.ProtectOwnedRootAndPinExistingDirectories(canonicalDirectories);

        if (existingMarker is not null)
        {
            if (!string.Equals(
                    existingMarker.ReadText(),
                    InstallerLayout.InstallMarkerValue,
                    StringComparison.Ordinal))
            {
                throw new IOException("安裝根標記在權限保護期間遭到變更。");
            }
        }
        else if (Directory.EnumerateFileSystemEntries(layout.Root).Any())
        {
            throw new IOException("空白安裝根目錄在權限保護期間遭到變更。");
        }

        rootLease.CreateAndProtectMissingDirectories();
        if (existingMarker is not null)
        {
            return false;
        }

        WriteAtomicText(
            marker,
            InstallerLayout.InstallMarkerValue + Environment.NewLine,
            overwrite: false);
        return true;
    }

    private static LockedTextFile? OpenOptionalLockedTextFile(
        string path,
        int maximumLength,
        string description)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException($"{description}缺少父目錄。");
        var matchingEntries = Directory.EnumerateFileSystemEntries(
                parent,
                Path.GetFileName(path),
                SearchOption.TopDirectoryOnly)
            .Take(2)
            .ToArray();
        if (matchingEntries.Length == 0)
        {
            return null;
        }
        if (matchingEntries.Length != 1)
        {
            throw new InvalidDataException($"{description}名稱不唯一。");
        }

        return new LockedTextFile(
            WindowsInstallerPlatform.OpenLockedRegularFileHandle(matchingEntries[0]),
            maximumLength,
            description);
    }

    private static void PrepareRuntimeDirectories(
        InstallerLayout layout,
        ICollection<string> createdMarkers)
    {
        foreach (var marker in new[]
                 {
                     Path.Combine(layout.ServiceRoot, ".muhun-mcsv-data-root"),
                     Path.Combine(layout.ExchangeRoot, ".muhun-mcsv-exchange-root"),
                     Path.Combine(layout.UserRoot, ".muhun-mcsv-user-data-root"),
                 })
        {
            using var existing = OpenOptionalLockedTextFile(marker, 64, "資料目錄標記");
            if (existing is not null)
            {
                if (!string.Equals(existing.ReadText(), "muhun.mcsv.manager:1", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("既有資料目錄標記無效。");
                }
                continue;
            }

            WriteAtomicText(marker, "muhun.mcsv.manager:1\n", overwrite: false);
            createdMarkers.Add(marker);
        }
        foreach (var name in InstallerEngine.ServiceRuntimeDirectoryNames)
        {
            Directory.CreateDirectory(Path.Combine(layout.ServiceRoot, name));
        }

        foreach (var name in InstallerEngine.UserRuntimeDirectoryNames)
        {
            Directory.CreateDirectory(Path.Combine(layout.UserRoot, name));
        }
    }

    private static void WriteAtomicText(string path, string value, bool overwrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, value, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static async Task MoveStagedVersionWithTransientLockRetryAsync(
        string stagedVersion,
        string targetVersion,
        CancellationToken cancellationToken,
        TimeSpan? timeoutOverride = null,
        int maximumAttempts = 24,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Action<string, string>? moveDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVersion);
        if (maximumAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        var timeout = timeoutOverride ?? TimeSpan.FromSeconds(20);
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutOverride));
        }

        var source = Path.GetFullPath(stagedVersion).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var destination = Path.GetFullPath(targetVersion).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("暫存版本與目標版本不可是同一路徑。", nameof(targetVersion));
        }

        Func<TimeSpan, CancellationToken, Task> wait =
            delayAsync ?? (static (delay, token) => Task.Delay(delay, token));
        var move = moveDirectory ?? Directory.Move;
        var elapsed = Stopwatch.StartNew();
        Exception? lastLockError = null;
        var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePendingStagedVersionMove(source, destination);
            attempts++;
            try
            {
                move(source, destination);
                return;
            }
            catch (Exception exception) when (IsTransientStagedVersionMoveLock(exception))
            {
                lastLockError = exception;
                if (IsCompletedStagedVersionMove(source, destination))
                {
                    return;
                }

                ValidatePendingStagedVersionMove(source, destination);
                if (attempts >= maximumAttempts || elapsed.Elapsed >= timeout)
                {
                    throw CreateStagedVersionMoveTimeout(
                        source,
                        destination,
                        attempts,
                        elapsed.Elapsed,
                        lastLockError);
                }

                var delay = ComputeStagedVersionMoveRetryDelay(attempts);
                var remaining = timeout - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw CreateStagedVersionMoveTimeout(
                        source,
                        destination,
                        attempts,
                        elapsed.Elapsed,
                        lastLockError);
                }
                if (delay > remaining)
                {
                    delay = remaining;
                }

                await wait(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void ValidatePendingStagedVersionMove(string source, string destination)
    {
        InstallerLayout.RejectExistingReparsePoints(source);
        var sourceAttributes = File.GetAttributes(source);
        if (!sourceAttributes.HasFlag(FileAttributes.Directory) ||
            sourceAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("待啟用的暫存版本不是非連結目錄。");
        }

        var destinationParent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("目標版本缺少父目錄。");
        InstallerLayout.RejectExistingReparsePoints(destinationParent);
        if (FindExactFileSystemEntry(destinationParent, destination) is not null)
        {
            throw new IOException("目標版本路徑在啟用前已存在；安裝已停止以避免覆寫。");
        }
    }

    private static bool IsCompletedStagedVersionMove(string source, string destination)
    {
        var sourceEntry = FindExactFileSystemEntry(
            Path.GetDirectoryName(source)
                ?? throw new InvalidDataException("暫存版本缺少父目錄。"),
            source);
        var destinationParent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("目標版本缺少父目錄。");
        var destinationEntry = FindExactFileSystemEntry(destinationParent, destination);
        if (sourceEntry is not null || destinationEntry is null)
        {
            return false;
        }

        InstallerLayout.RejectExistingReparsePoints(destinationEntry);
        var attributes = File.GetAttributes(destinationEntry);
        if (!attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("版本目錄啟用後的目標不是非連結目錄。");
        }
        return true;
    }

    private static string? FindExactFileSystemEntry(string parent, string expectedPath)
    {
        var normalizedParent = Path.GetFullPath(parent).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var normalizedExpected = Path.GetFullPath(expectedPath).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var expectedName = Path.GetFileName(normalizedExpected);
        return Directory.EnumerateFileSystemEntries(
                normalizedParent,
                expectedName,
                SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))
            .FirstOrDefault(path => string.Equals(
                path,
                normalizedExpected,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTransientStagedVersionMoveLock(Exception exception)
    {
        if (exception is not IOException and not UnauthorizedAccessException)
        {
            return false;
        }
        return (exception.HResult & 0xffff) is 5 or 32 or 33;
    }

    private static TimeSpan ComputeStagedVersionMoveRetryDelay(int attempts)
    {
        var exponent = Math.Min(Math.Max(attempts - 1, 0), 4);
        return TimeSpan.FromMilliseconds(Math.Min(1_500, 100 * (1 << exponent)));
    }

    private static TimeoutException CreateStagedVersionMoveTimeout(
        string source,
        string destination,
        int attempts,
        TimeSpan elapsed,
        Exception lastError)
        => new(
            $"Windows 安全掃描或其他程序持續鎖定待啟用版本；" +
            $"attempts={attempts}; elapsedMs={(long)elapsed.TotalMilliseconds}; " +
            $"lastHResult=0x{lastError.HResult:X8}; source={source}; destination={destination}",
            lastError);

    private static void TryDeleteOwnedRegularFile(string path)
    {
        try
        {
            using (var locked = OpenOptionalLockedTextFile(path, 256, "安裝程式建立的標記"))
            {
                if (locked is null)
                {
                    return;
                }
            }

            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
        }
    }

    private static void TryDeleteOwnedStage(string stage, string stagingRoot)
    {
        try
        {
            var parent = Path.GetFullPath(stagingRoot).TrimEnd(Path.DirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(stage);
            if (!candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(candidate, ".muhun-mcsv-install-staging")))
            {
                return;
            }

            InstallerLayout.RejectExistingReparsePoints(candidate);
            Directory.Delete(candidate, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteOwnedVersion(string target, string versionsRoot, string version)
    {
        try
        {
            if (string.Equals(Path.GetFileName(target), version, StringComparison.Ordinal) &&
                string.Equals(
                    Path.GetDirectoryName(Path.GetFullPath(target)),
                    Path.GetFullPath(versionsRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                InstallerLayout.RejectExistingReparsePoints(target);
                Directory.Delete(target, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal interface IInstallerShellIntegrationPlatform
{
    InstallerRegistrationSnapshot CaptureInstallationRegistration();
    InstallerShortcutSnapshot CaptureStartMenuShortcut(string channel);
    void WriteInstallationRegistration(
        InstallerLayout layout,
        string version,
        string guiExecutable,
        string launcherExecutable);
    void CreateStartMenuShortcut(string launcherExecutable, string installRoot, string channel);
    void RestoreInstallationRegistration(InstallerRegistrationSnapshot snapshot);
    void RestoreStartMenuShortcut(InstallerShortcutSnapshot snapshot);
}

internal static class InstallerShellIntegrationTransaction
{
    internal static InstallerShellIntegrationRollback Apply(
        IInstallerShellIntegrationPlatform platform,
        InstallerLayout layout,
        string version,
        string guiExecutable,
        string launcherExecutable,
        Action<InstallerShellIntegrationRollback> snapshotCaptured)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(snapshotCaptured);
        var rollback = new InstallerShellIntegrationRollback(
            platform.CaptureInstallationRegistration(),
            platform.CaptureStartMenuShortcut(layout.Channel));
        snapshotCaptured(rollback);
        try
        {
            platform.WriteInstallationRegistration(layout, version, guiExecutable, launcherExecutable);
            platform.CreateStartMenuShortcut(launcherExecutable, layout.Root, layout.Channel);
            return rollback;
        }
        catch (Exception mutationError)
        {
            try
            {
                Restore(platform, rollback);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "無法回復安裝登錄資料與開始功能表捷徑。",
                    mutationError,
                    rollbackError);
            }
            throw;
        }
    }

    internal static void Restore(
        IInstallerShellIntegrationPlatform platform,
        InstallerShellIntegrationRollback? rollback)
    {
        if (rollback is null)
        {
            return;
        }

        var errors = new List<Exception>();
        try
        {
            platform.RestoreStartMenuShortcut(rollback.Shortcut);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        try
        {
            platform.RestoreInstallationRegistration(rollback.Registration);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        if (errors.Count > 0)
        {
            throw new AggregateException("無法完整回復安裝登錄資料與開始功能表捷徑。", errors);
        }
    }
}

internal interface IInstallerPlatform : IInstallerShellIntegrationPlatform
{
    string CurrentUserSid { get; }
    bool IsAdministrator();
    bool IsCurrentIdentityInteractiveShellUser();
    IInstallerRootLease AcquireInstallRootLease(string installRoot);
    Task<InstallerServiceSnapshot> CaptureAndStopServiceAsync(
        string name,
        string installRoot,
        Action<InstallerServiceSnapshot> snapshotCaptured,
        CancellationToken cancellationToken);
    Task ConfigureServiceAsync(
        string name,
        string executable,
        string dataRoot,
        string exchangeRoot,
        string installRoot,
        InstallerServiceSnapshot snapshot,
        CancellationToken cancellationToken);
    Task StartServiceAsync(string name, CancellationToken cancellationToken);
    Task WaitForServiceHealthAsync(
        string serviceExecutable,
        string dataRoot,
        string version,
        CancellationToken cancellationToken);
    Task RestoreServiceAsync(
        string name,
        InstallerServiceSnapshot snapshot,
        bool restart,
        CancellationToken cancellationToken);
    InstallerOperatorAccessRollback ProvisionOperatorAccess(
        InstallerLayout layout,
        Action<InstallerOperatorAccessRollback> snapshotCaptured);
    void HardenOperatorBindingAccess(
        InstallerLayout layout,
        string serviceName,
        InstallerOperatorAccessRollback rollback);
    void RestoreOperatorAccess(InstallerOperatorAccessRollback? rollback);
    void ApplyAccessControl(
        InstallerLayout layout,
        string serviceName,
        string targetVersionRoot,
        string operatorsGroupSid);
    void ApplyActivePointerAccessControl(InstallerLayout layout, string serviceName);
}

internal sealed partial class WindowsInstallerPlatform : IInstallerPlatform
{
    private const string ProductRegistryPath = @"SOFTWARE\Muhun\MCSV";
    private const string ProductId = "muhun.mcsv.manager";
    private const int MaximumShortcutSnapshotBytes = 4 * 1024 * 1024;
    private static readonly string[] ProductRegistryValueNames =
    [
        "ProductId",
        "PublisherCertificateSha256",
        "InstallRoot",
        "Channel",
        "Version",
        "ServiceDataRoot",
        "UserDataRoot",
        "ExchangeRoot",
    ];
    private const string PublisherCertificateSha256 =
        "1a67e65dc9c367ac3247d0483edbe94dab38c5494859a43210c1ad4719e80b71";
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint FileReadAttributes = 0x0080;
    private const uint DeleteAccess = 0x00010000;
    private const uint GenericRead = 0x80000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAlreadyExists = 183;
    private const int FileStandardInfoClass = 1;
    private const int FileAttributeTagInfoClass = 9;
    private const int FileDispositionInfoClass = 4;
    private static readonly JsonSerializerOptions StrictActivationReadyJson = new(
        JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };
    private static readonly SecurityIdentifier LocalSystemSid =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly Lazy<SecurityIdentifier> TrustedInstallerSid = new(
        () => (SecurityIdentifier)new NTAccount("NT SERVICE", "TrustedInstaller")
            .Translate(typeof(SecurityIdentifier)));
    public string CurrentUserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value
                ?? throw new InvalidOperationException("無法取得目前 Windows 使用者 SID。");
        }
    }

    public bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public bool IsCurrentIdentityInteractiveShellUser()
    {
        var shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero ||
            GetWindowThreadProcessId(shellWindow, out var processId) == 0 ||
            processId == 0)
        {
            return false;
        }

        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token))
        {
            return false;
        }

        using (token)
        using (var shellIdentity = new WindowsIdentity(token.DangerousGetHandle()))
        using (var currentIdentity = WindowsIdentity.GetCurrent())
        {
            return shellIdentity.User is not null && currentIdentity.User is not null &&
                   shellIdentity.User.Equals(currentIdentity.User) &&
                   Process.GetProcessById(checked((int)processId)).SessionId ==
                   Process.GetCurrentProcess().SessionId;
        }
    }

    public IInstallerRootLease AcquireInstallRootLease(string installRoot)
    {
        InstallerLayout.RejectPreservedDataTreePath(installRoot);
        var normalizedRoot = Path.GetFullPath(installRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(normalizedRoot)
            ?? throw new InvalidDataException("安裝根目錄缺少本機磁碟區。");
        var segments = normalizedRoot[volumeRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new InvalidDataException("不可直接使用磁碟根目錄安裝。");
        }

        var handles = new List<SafeFileHandle>(segments.Length + 1);
        var rootCreated = false;
        try
        {
            var current = Path.GetFullPath(volumeRoot);
            var volumeHandle = OpenLockedDirectoryHandle(current);
            handles.Add(volumeHandle);
            ValidateTrustedInstallAncestorAcl(current);
            for (var index = 0; index < segments.Length; index++)
            {
                var next = Path.Combine(current, segments[index]);
                var isRoot = index == segments.Length - 1;
                // Existing directories keep a read-only, no-delete-sharing identity lease.
                // DELETE access is needed only for a directory created by this transaction.
                // Holding DELETE with FileShare.Read would make our own atomic child renames fail
                // with ERROR_SHARING_VIOLATION before the first install marker can be committed.
                var handle = TryOpenLockedDirectoryHandle(next);
                if (handle is null)
                {
                    if (!isRoot)
                    {
                        throw new DirectoryNotFoundException(
                            "自訂安裝位置的父目錄必須已存在，且必須由系統管理員保護。");
                    }

                    CreateAdministrativeOnlyDirectory(next);
                    rootCreated = true;
                    handle = OpenLockedDirectoryHandle(next, requireDelete: true);
                }
                else if (!isRoot)
                {
                    ValidateTrustedInstallAncestorAcl(next);
                }
                else if (IsRecoverableDefaultRootResidue(next))
                {
                    // A failed fresh install may have removed every owned child and marker before
                    // its final root deletion was blocked. Reacquire DELETE rights and revalidate
                    // the exact empty administrative-only residue before treating it as this
                    // transaction's newly created root. No custom or non-empty directory is
                    // eligible for this recovery path.
                    handle.Dispose();
                    handle = OpenLockedDirectoryHandle(next, requireDelete: true);
                    if (!IsRecoverableDefaultRootResidue(next))
                    {
                        throw new IOException(
                            "預設安裝根目錄在殘留目錄安全重驗期間遭到變更。");
                    }
                    rootCreated = true;
                }
                handles.Add(handle);
                current = next;
            }

            ValidateLockedDirectoryHandle(handles[^1], normalizedRoot);
            return new WindowsInstallerRootLease(handles, rootCreated, normalizedRoot);
        }
        catch
        {
            DisposeHandles(handles);
            if (rootCreated)
            {
                TryDeleteEmptyDirectory(normalizedRoot);
            }
            throw;
        }
    }

    private static void CreateAdministrativeOnlyDirectory(string path)
    {
        var descriptor = CreateAdministrativeOnlyDirectorySecurity().GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
                SecurityDescriptor = pinned.AddrOfPinnedObject(),
                InheritHandle = false,
            };
            if (CreateDirectoryWithSecurityW(path, ref attributes))
            {
                return;
            }

            var error = Marshal.GetLastWin32Error();
            if (error == ErrorAlreadyExists)
            {
                throw new IOException($"安裝目錄在安全建立期間已由其他程序建立：{path}");
            }
            throw new Win32Exception(error, $"無法以受保護權限建立安裝目錄：{path}");
        }
        finally
        {
            pinned.Free();
        }
    }

    internal static bool IsRecoverableDefaultRootResidue(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var defaultRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(InstallerLayout.DefaultRoot));
        if (!string.Equals(normalized, defaultRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        InstallerLayout.RejectExistingReparsePoints(normalized);
        if (!Directory.Exists(normalized) || Directory.EnumerateFileSystemEntries(normalized).Any())
        {
            return false;
        }

        var actual = new DirectoryInfo(normalized).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        var expected = CreateAdministrativeOnlyDirectorySecurity();
        return InstallerSecurityDescriptorComparer.EqualsAllowingDaclAutoInherited(
            actual.GetSecurityDescriptorSddlForm(
                AccessControlSections.Access | AccessControlSections.Owner),
            expected.GetSecurityDescriptorSddlForm(
                AccessControlSections.Access | AccessControlSections.Owner));
    }

    internal static SafeFileHandle OpenLockedDirectoryHandle(
        string path,
        bool requireDelete = false)
        => TryOpenLockedDirectoryHandle(path, requireDelete)
           ?? throw new DirectoryNotFoundException($"找不到要鎖定的安裝目錄：{path}");

    private static SafeFileHandle? TryOpenLockedDirectoryHandle(
        string path,
        bool requireDelete = false)
    {
        var shareMode = requireDelete
            ? FileShare.Read | FileShare.Write | FileShare.Delete
            : FileShare.Read;
        var handle = CreateFileW(
            path,
            FileReadAttributes | (requireDelete ? DeleteAccess : 0),
            shareMode,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return null;
            }
            throw new Win32Exception(error, $"無法鎖定安裝目錄以防止替換：{path}");
        }

        try
        {
            ValidateLockedDirectoryHandle(handle, path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static SafeFileHandle OpenLockedRegularFileHandle(string path)
    {
        var handle = CreateFileW(
            path,
            GenericRead,
            FileShare.Read,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"無法鎖定安裝標記以防止替換：{path}");
        }

        try
        {
            ValidateLockedRegularFileHandle(handle, path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void ValidateLockedRegularFileHandle(SafeFileHandle handle, string expectedPath)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfoClass,
                out var information,
                checked((uint)Marshal.SizeOf<FileAttributeTagInfo>())))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"無法驗證安裝檔案 handle：{expectedPath}");
        }
        if (information.FileAttributes.HasFlag(FileAttributes.Directory) ||
            information.FileAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"安裝檔案必須是非連結的一般檔案：{expectedPath}");
        }
        if (!GetFileStandardInformationByHandle(
                handle,
                FileStandardInfoClass,
                out var standardInformation,
                checked((uint)Marshal.SizeOf<FileStandardInfo>())) ||
            standardInformation.Directory)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"無法驗證安裝檔案連結數：{expectedPath}");
        }
        if (standardInformation.NumberOfLinks != 1)
        {
            throw new IOException($"安裝檔案不可使用 NTFS hard link：{expectedPath}");
        }
        if (!string.Equals(
                NormalizeFinalHandlePath(GetFinalPath(handle)),
                NormalizeFinalHandlePath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"安裝檔案 handle 在開啟期間被重新導向：{expectedPath}");
        }
    }

    private static void ValidateLockedDirectoryHandle(SafeFileHandle handle, string expectedPath)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfoClass,
                out var information,
                checked((uint)Marshal.SizeOf<FileAttributeTagInfo>())))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"無法驗證安裝目錄 handle：{expectedPath}");
        }
        if (!information.FileAttributes.HasFlag(FileAttributes.Directory) ||
            information.FileAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"安裝路徑含有連結或非目錄項目：{expectedPath}");
        }

        var actualPath = NormalizeFinalHandlePath(GetFinalPath(handle));
        var expected = NormalizeFinalHandlePath(expectedPath);
        if (!string.Equals(actualPath, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"安裝目錄 handle 指向非預期位置：{expectedPath}");
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[512];
        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, checked((uint)buffer.Length), 0);
            if (length == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "無法取得鎖定安裝目錄的最終路徑。");
            }
            if (length < buffer.Length)
            {
                return new string(buffer, 0, checked((int)length));
            }
            if (length > 32_767)
            {
                throw new PathTooLongException("鎖定安裝目錄的最終路徑過長。");
            }

            buffer = new char[checked((int)length + 1)];
        }
    }

    private static string NormalizeFinalHandlePath(string path)
    {
        const string devicePrefix = @"\\?\";
        if (path.StartsWith(devicePrefix + "UNC\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("安裝目錄 handle 不可指向 UNC 路徑。");
        }
        if (path.StartsWith(devicePrefix, StringComparison.Ordinal))
        {
            path = path[devicePrefix.Length..];
        }

        return Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static void DisposeHandles(IEnumerable<SafeFileHandle> handles)
    {
        foreach (var handle in handles.Reverse())
        {
            handle.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public FileAttributes FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;

        [MarshalAs(UnmanagedType.U1)]
        public bool DeletePending;

        [MarshalAs(UnmanagedType.U1)]
        public bool Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    private sealed class WindowsInstallerRootLease(
        IReadOnlyList<SafeFileHandle> handles,
        bool rootCreated,
        string normalizedRoot) : IInstallerRootLease
    {
        private List<SafeFileHandle>? _handles = [.. handles];
        private readonly int _rootHandleIndex = handles.Count - 1;
        private readonly string _normalizedRoot = NormalizeFinalHandlePath(normalizedRoot);
        private readonly Dictionary<string, SafeFileHandle> _directoryHandles =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SafeFileHandle> _fileHandles =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DirectorySecurity> _originalSecurity =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FileSecurity> _originalFileSecurity =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, bool>> _prevalidatedVersionNamespaces =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _prevalidatedMissingDirectories =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _createdDirectories = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _missingDirectories = [];
        private bool _initialProtectionPrepared;
        private bool _canonicalDirectoriesReady;
        private bool _protectionMutated;
        private bool _committed;
        private bool _rolledBack;
        private bool _rootDeletePending;

        public bool RootCreated { get; } = rootCreated;

        public void ValidateAndPinExistingManagedInstallation(
            InstallerLayout layout,
            string targetVersion,
            string currentUserSid,
            string serviceName)
        {
            ArgumentNullException.ThrowIfNull(layout);
            if (RootCreated)
            {
                return;
            }
            if (_initialProtectionPrepared)
            {
                throw new InvalidOperationException("既有安裝信任檢查必須先於任何權限變更。");
            }

            var owned = GetHandles();
            _directoryHandles.Add(_normalizedRoot, owned[_rootHandleIndex]);
            var userSid = new SecurityIdentifier(currentUserSid);
            var serviceSid = ResolveServiceSid(serviceName);
            var operatorsSid = ResolveRequiredOperatorGroupSid();
            var expectedDirectories = CreateExpectedManagedDirectoryAclMap(
                layout,
                userSid,
                serviceSid,
                operatorsSid);
            foreach (var pair in expectedDirectories.OrderBy(pair => pair.Key.Length))
            {
                if (_prevalidatedMissingDirectories.Any(missing => pair.Key.StartsWith(
                        missing + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    _prevalidatedMissingDirectories.Add(pair.Key);
                    continue;
                }

                SafeFileHandle handle;
                if (string.Equals(pair.Key, _normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    handle = owned[_rootHandleIndex];
                }
                else
                {
                    var optionalHandle = TryOpenLockedDirectoryHandle(pair.Key);
                    if (optionalHandle is null)
                    {
                        if (IsRequiredSharedManagedDirectory(layout, pair.Key))
                        {
                            throw new DirectoryNotFoundException(
                                $"既有 X MCSV 安裝缺少必要的共用受管理目錄：{pair.Key}");
                        }
                        _prevalidatedMissingDirectories.Add(pair.Key);
                        continue;
                    }
                    handle = optionalHandle;
                    owned.Add(handle);
                    _directoryHandles.Add(pair.Key, handle);
                }
                ValidateLockedDirectoryHandle(handle, pair.Key);
                ValidateExactDirectoryAcl(pair.Key, pair.Value);
            }

            var marker = Path.Combine(layout.Root, InstallerLayout.InstallMarkerName);
            var markerMatches = Directory.EnumerateFileSystemEntries(
                    layout.Root,
                    InstallerLayout.InstallMarkerName,
                    SearchOption.TopDirectoryOnly)
                .Take(2)
                .ToArray();
            if (markerMatches.Length != 1)
            {
                throw new IOException(
                    "既有自訂安裝資料夾必須已有唯一且有效的 X MCSV 管理標記；請改選尚不存在的新子目錄。");
            }
            var markerHandle = OpenLockedRegularFileHandle(markerMatches[0]);
            owned.Add(markerHandle);
            _fileHandles.Add(marker, markerHandle);
            if (new FileInfo(marker).Length is < 1 or > 64 ||
                !string.Equals(
                    File.ReadAllText(marker).Trim(),
                    InstallerLayout.InstallMarkerValue,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("既有安裝根目錄的 X MCSV 標記無效。");
            }
            ValidateExactFileAcl(
                marker,
                [
                    new InstallerAclGrant(LocalSystemSid, FileSystemRights.FullControl),
                    new InstallerAclGrant(AdministratorsSid, FileSystemRights.FullControl),
                    new InstallerAclGrant(userSid, FileSystemRights.Read),
                    new InstallerAclGrant(serviceSid, FileSystemRights.Read),
                ]);
            CaptureOriginalFileSecurity(marker);

            var activePointer = Path.Combine(layout.Root, "active-version.v1");
            if (Directory.Exists(activePointer))
            {
                throw new IOException("既有 active-version.v1 不是一般檔案。");
            }
            if (File.Exists(activePointer))
            {
                var pointerHandle = OpenLockedRegularFileHandle(activePointer);
                owned.Add(pointerHandle);
                _fileHandles.Add(activePointer, pointerHandle);
                ValidateExactFileAcl(
                    activePointer,
                    [
                        new InstallerAclGrant(LocalSystemSid, FileSystemRights.FullControl),
                        new InstallerAclGrant(AdministratorsSid, FileSystemRights.FullControl),
                        new InstallerAclGrant(userSid, FileSystemRights.Read),
                        new InstallerAclGrant(serviceSid, FileSystemRights.Modify),
                    ]);
                CaptureOriginalFileSecurity(activePointer);
            }

            if (File.Exists(targetVersion))
            {
                throw new IOException("既有版本路徑不是目錄。");
            }
            if (Directory.Exists(targetVersion))
            {
                var normalizedTarget = NormalizeOwnedDescendant(targetVersion);
                _prevalidatedVersionNamespaces.Add(
                    normalizedTarget,
                    PinAndValidateExistingVersionNamespace(
                        normalizedTarget,
                        userSid,
                        serviceSid,
                        maximumEntries: 100_000,
                        maximumDepth: 64));
            }
        }

        public void ProtectOwnedRootAndPinExistingDirectories(IReadOnlyList<string> directories)
        {
            ArgumentNullException.ThrowIfNull(directories);
            var owned = GetHandles();
            if (_initialProtectionPrepared)
            {
                throw new InvalidOperationException("安裝根目錄的初始權限保護已執行。");
            }

            _directoryHandles.TryAdd(_normalizedRoot, owned[_rootHandleIndex]);
            var expected = ExpandOwnedDirectoryPaths(directories);
            foreach (var path in expected)
            {
                if (_prevalidatedMissingDirectories.Contains(path))
                {
                    using var unexpected = TryOpenLockedDirectoryHandle(path);
                    if (unexpected is not null)
                    {
                        throw new IOException($"安裝目錄在嚴格信任檢查後遭到建立或替換：{path}");
                    }
                    _missingDirectories.Add(path);
                    continue;
                }
                if (_missingDirectories.Any(missing => path.StartsWith(
                        missing + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    _missingDirectories.Add(path);
                    continue;
                }

                if (_directoryHandles.ContainsKey(path))
                {
                    continue;
                }

                var handle = TryOpenLockedDirectoryHandle(path);
                if (handle is null)
                {
                    _missingDirectories.Add(path);
                    continue;
                }

                owned.Add(handle!);
                _directoryHandles.Add(path, handle!);
            }

            CaptureOriginalSecurity(_normalizedRoot, RootCreated);
            foreach (var path in expected.Where(_directoryHandles.ContainsKey))
            {
                CaptureOriginalSecurity(path, created: false);
            }

            _initialProtectionPrepared = true;
            _protectionMutated = true;
            SetAdministrativeOnlyDirectoryAcl(_normalizedRoot);
            ValidateLockedDirectoryHandle(owned[_rootHandleIndex], _normalizedRoot);
            foreach (var path in expected.Where(_directoryHandles.ContainsKey))
            {
                SetAdministrativeOnlyDirectoryAcl(path);
                ValidateLockedDirectoryHandle(_directoryHandles[path], path);
            }
        }

        public void CreateAndProtectMissingDirectories()
        {
            if (!_initialProtectionPrepared || _canonicalDirectoriesReady)
            {
                throw new InvalidOperationException("安裝目錄建立階段順序無效。");
            }

            var owned = GetHandles();
            foreach (var path in _missingDirectories)
            {
                using (var raced = TryOpenLockedDirectoryHandle(path))
                {
                    if (raced is not null)
                    {
                        throw new IOException($"安裝目錄在權限保護期間遭到建立或替換：{path}");
                    }
                }

                var parent = NormalizeFinalHandlePath(Path.GetDirectoryName(path)
                    ?? throw new InvalidDataException("安裝目錄缺少父目錄。"));
                if (!_directoryHandles.ContainsKey(parent))
                {
                    throw new InvalidDataException("安裝目錄的受保護父層遺失。");
                }

                CreateAdministrativeOnlyDirectory(path);
                SafeFileHandle handle;
                try
                {
                    handle = OpenLockedDirectoryHandle(path, requireDelete: true);
                }
                catch
                {
                    TryDeleteEmptyDirectory(path);
                    throw;
                }
                owned.Add(handle!);
                _directoryHandles.Add(path, handle);
                _createdDirectories.Add(path);
                ValidateLockedDirectoryHandle(handle, path);
            }

            _missingDirectories.Clear();
            _prevalidatedMissingDirectories.Clear();
            _canonicalDirectoriesReady = true;
        }

        public void ProtectOwnedRootAndDirectories(IReadOnlyList<string> directories)
        {
            ArgumentNullException.ThrowIfNull(directories);
            if (!_canonicalDirectoriesReady)
            {
                throw new InvalidOperationException("標準安裝目錄尚未完成保護。");
            }

            var owned = GetHandles();
            foreach (var path in ExpandOwnedDirectoryPaths(directories))
            {
                if (_directoryHandles.ContainsKey(path))
                {
                    continue;
                }

                var parent = NormalizeFinalHandlePath(Path.GetDirectoryName(path)
                    ?? throw new InvalidDataException("安裝目錄缺少父目錄。"));
                if (!_directoryHandles.ContainsKey(parent))
                {
                    throw new InvalidDataException("安裝目錄的受保護父層遺失。");
                }

                var handle = TryOpenLockedDirectoryHandle(path);
                var created = handle is null;
                if (created)
                {
                    CreateAdministrativeOnlyDirectory(path);
                    try
                    {
                        handle = OpenLockedDirectoryHandle(path, requireDelete: true);
                    }
                    catch
                    {
                        TryDeleteEmptyDirectory(path);
                        throw;
                    }
                    _createdDirectories.Add(path);
                }
                else
                {
                    CaptureOriginalSecurity(path, created: false);
                }

                var protectedHandle = handle ?? throw new IOException("無法取得受保護安裝目錄 handle。");
                owned.Add(protectedHandle);
                _directoryHandles.Add(path, protectedHandle);
                _protectionMutated = true;
                SetAdministrativeOnlyDirectoryAcl(path);
                ValidateLockedDirectoryHandle(protectedHandle, path);
            }
        }

        public void PinAndHardenExistingVersionTree(
            string directory,
            int maximumEntries = 100_000,
            int maximumDepth = 64)
        {
            if (maximumEntries <= 0 || maximumDepth <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumEntries),
                    "版本目錄的安全掃描限制必須大於零。");
            }
            if (!_canonicalDirectoriesReady)
            {
                throw new InvalidOperationException("標準安裝目錄尚未完成保護。");
            }

            var normalized = NormalizeOwnedDescendant(directory);
            if (!_directoryHandles.TryGetValue(normalized, out var rootHandle))
            {
                throw new InvalidOperationException("既有版本根目錄尚未鎖定與保護。");
            }
            ValidateLockedDirectoryHandle(rootHandle, normalized);

            if (!_prevalidatedVersionNamespaces.Remove(normalized, out var discovered))
            {
                throw new UnauthorizedAccessException(
                    "既有版本目錄未通過安裝前的嚴格 ACL 與完整樹鎖定檢查。");
            }

            _protectionMutated = true;

            foreach (var path in discovered.Where(pair => pair.Value &&
                                                          !string.Equals(
                                                              pair.Key,
                                                              normalized,
                                                              StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key)
                         .OrderBy(path => path.Length))
            {
                SetAdministrativeOnlyDirectoryAcl(path);
                ValidateLockedDirectoryHandle(_directoryHandles[path], path);
            }
            foreach (var pair in _fileHandles.Where(pair => pair.Key.StartsWith(
                         normalized + Path.DirectorySeparatorChar,
                         StringComparison.OrdinalIgnoreCase)))
            {
                SetAdministrativeOnlyFileAcl(pair.Key);
                ValidateLockedRegularFileHandle(pair.Value, pair.Key);
            }

            var verified = EnumerateVersionNamespace(normalized, maximumEntries, maximumDepth);
            if (verified.Count != discovered.Count || discovered.Any(pair =>
                    !verified.TryGetValue(pair.Key, out var isDirectory) || isDirectory != pair.Value))
            {
                throw new IOException("既有版本目錄在權限收斂期間遭到變更。");
            }
        }

        public void PinAndHardenNewVersionTree(
            string directory,
            int maximumEntries = 100_000,
            int maximumDepth = 64)
        {
            var normalized = NormalizeOwnedDescendant(directory);
            if (!_directoryHandles.ContainsKey(normalized))
            {
                throw new InvalidOperationException("新版本根目錄尚未鎖定與保護。");
            }
            var discovered = PinAndValidateExistingVersionNamespace(
                normalized,
                LocalSystemSid,
                LocalSystemSid,
                maximumEntries,
                maximumDepth,
                validateExactProductAcl: false,
                rootAlreadyPinned: true);
            _prevalidatedVersionNamespaces.Add(normalized, discovered);
            PinAndHardenExistingVersionTree(directory, maximumEntries, maximumDepth);
        }

        public void ReleaseDirectoryForDeletion(string directory)
        {
            var owned = GetHandles();
            var normalized = NormalizeOwnedDescendant(directory);
            var paths = _directoryHandles.Keys
                .Where(path => string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase) ||
                               path.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => path.Length)
                .ToArray();
            if (paths.Length == 0 || string.Equals(normalized, _normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"找不到可釋放的受保護安裝目錄：{directory}");
            }

            foreach (var path in paths)
            {
                var handle = _directoryHandles[path];
                _directoryHandles.Remove(path);
                _originalSecurity.Remove(path);
                _createdDirectories.Remove(path);
                owned.Remove(handle);
                handle.Dispose();
            }
            var files = _fileHandles.Keys
                .Where(path => path.StartsWith(
                    normalized + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var path in files)
            {
                var handle = _fileHandles[path];
                _fileHandles.Remove(path);
                _originalFileSecurity.Remove(path);
                owned.Remove(handle);
                handle.Dispose();
            }
        }

        public void ReleaseFileForReplacement(string file)
        {
            var owned = GetHandles();
            var normalized = NormalizeOwnedDescendant(file);
            if (!_fileHandles.Remove(normalized, out var handle))
            {
                // A fresh install creates active-version.v1 after the initial namespace pin.
                return;
            }

            ValidateLockedRegularFileHandle(handle, normalized);
            owned.Remove(handle);
            handle.Dispose();
            // Deliberately retain _originalFileSecurity. If a later step fails, rollback applies
            // the captured ACL to the replacement that RestoreActivePointer recreates.
        }

        public void CommitProtectionChanges()
        {
            if (_rolledBack)
            {
                throw new InvalidOperationException("已回復的安裝權限不可提交。");
            }
            _committed = true;
            _originalSecurity.Clear();
            _originalFileSecurity.Clear();
            _createdDirectories.Clear();
        }

        public void RollbackProtectionChanges()
        {
            if (_committed || _rolledBack)
            {
                return;
            }

            if (!_protectionMutated)
            {
                _createdDirectories.Clear();
                _originalSecurity.Clear();
                _originalFileSecurity.Clear();
                _rolledBack = true;
                return;
            }

            var owned = GetHandles();
            var errors = new List<Exception>();
            foreach (var pair in _originalFileSecurity.OrderByDescending(pair => pair.Key.Length))
            {
                try
                {
                    new FileInfo(pair.Key).SetAccessControl(pair.Value);
                    if (_fileHandles.TryGetValue(pair.Key, out var handle))
                    {
                        ValidateLockedRegularFileHandle(handle, pair.Key);
                    }
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
            var created = _createdDirectories.OrderByDescending(path => path.Length).ToArray();
            foreach (var path in created)
            {
                try
                {
                    var handle = _directoryHandles[path];
                    SetAdministrativeOnlyDirectoryAcl(path);
                    ValidateLockedDirectoryHandle(handle, path);
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }

            var createdRoots = created
                .Where(path => !created.Any(other =>
                    other.Length < path.Length &&
                    path.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(path => path.Length)
                .ToArray();
            if (errors.Count == 0)
            {
                foreach (var path in createdRoots)
                {
                    try
                    {
                        foreach (var descendant in created.Where(candidate =>
                                     !string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase) &&
                                     candidate.StartsWith(
                                         path + Path.DirectorySeparatorChar,
                                         StringComparison.OrdinalIgnoreCase)))
                        {
                            if (_directoryHandles.Remove(descendant, out var descendantHandle))
                            {
                                owned.Remove(descendantHandle);
                                descendantHandle.Dispose();
                            }
                        }

                        var rootHandle = _directoryHandles[path];
                        DeleteOwnedDirectoryContents(path);
                        SetAdministrativeOnlyDirectoryAcl(path);
                        ValidateLockedDirectoryHandle(rootHandle, path);
                        DeleteEmptyDirectoryByHandle(rootHandle, path);
                        _directoryHandles.Remove(path);
                        owned.Remove(rootHandle);
                        rootHandle.Dispose();
                    }
                    catch (Exception exception)
                    {
                        errors.Add(exception);
                    }
                }
            }

            if (errors.Count == 0)
            {
                foreach (var pair in _originalSecurity.OrderByDescending(pair => pair.Key.Length))
                {
                    try
                    {
                        new DirectoryInfo(pair.Key).SetAccessControl(pair.Value);
                        if (_directoryHandles.TryGetValue(pair.Key, out var handle))
                        {
                            ValidateLockedDirectoryHandle(handle, pair.Key);
                        }
                    }
                    catch (Exception exception)
                    {
                        errors.Add(exception);
                    }
                }
            }

            _createdDirectories.Clear();
            _originalSecurity.Clear();
            _originalFileSecurity.Clear();
            _rolledBack = true;
            if (errors.Count > 0)
            {
                throw new AggregateException("無法完整回復安裝目錄權限與新建目錄。", errors);
            }
        }

        public void DeleteNewRootIfEmpty()
        {
            if (!RootCreated || !_rolledBack)
            {
                throw new InvalidOperationException("只有已回復的新建安裝根目錄可以刪除。");
            }
            if (Directory.EnumerateFileSystemEntries(_normalizedRoot).Any())
            {
                throw new IOException("新建安裝根目錄仍含資料；為避免誤刪，已保留受保護目錄。");
            }

            var owned = GetHandles();
            DeleteEmptyDirectoryByHandle(owned[_rootHandleIndex], _normalizedRoot);
            _rootDeletePending = true;
        }

        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref _handles, null);
            if (owned is not null)
            {
                DisposeHandles(owned);
            }
            _directoryHandles.Clear();
            _fileHandles.Clear();
            _prevalidatedMissingDirectories.Clear();
            _prevalidatedVersionNamespaces.Clear();
        }

        private List<SafeFileHandle> GetHandles()
        {
            if (_rootDeletePending)
            {
                throw new InvalidOperationException("安裝根目錄已排定刪除。");
            }
            return _handles ?? throw new ObjectDisposedException(nameof(WindowsInstallerRootLease));
        }

        private Dictionary<string, bool> PinAndValidateExistingVersionNamespace(
            string root,
            SecurityIdentifier userSid,
            SecurityIdentifier serviceSid,
            int maximumEntries,
            int maximumDepth,
            bool validateExactProductAcl = true,
            bool rootAlreadyPinned = false)
        {
            var owned = GetHandles();
            if (!rootAlreadyPinned)
            {
                var rootHandle = OpenLockedDirectoryHandle(root);
                owned.Add(rootHandle);
                _directoryHandles.Add(root, rootHandle);
                CaptureOriginalSecurity(root, created: false);
            }
            else if (!_directoryHandles.ContainsKey(root))
            {
                throw new InvalidOperationException("版本根目錄的既有 lease 遺失。");
            }

            var discovered = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [root] = true,
            };
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            var entries = 0;
            while (pending.Count > 0)
            {
                var (parent, parentDepth) = pending.Pop();
                foreach (var rawEntry in Directory.EnumerateFileSystemEntries(parent))
                {
                    if (++entries > maximumEntries)
                    {
                        throw new InvalidDataException("既有版本目錄超出安全掃描數量限制。");
                    }
                    var depth = checked(parentDepth + 1);
                    if (depth > maximumDepth)
                    {
                        throw new InvalidDataException("既有版本目錄超出安全掃描深度限制。");
                    }
                    var path = NormalizeFinalHandlePath(rawEntry);
                    if (!string.Equals(
                            NormalizeFinalHandlePath(Path.GetDirectoryName(path)
                                ?? throw new InvalidDataException("版本項目缺少父目錄。")),
                            parent,
                            StringComparison.OrdinalIgnoreCase) ||
                        !discovered.TryAdd(path, false))
                    {
                        throw new IOException("既有版本目錄在安全掃描期間出現重複或越界項目。");
                    }

                    var attributes = File.GetAttributes(path);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        throw new IOException($"既有版本目錄不可包含連結或 reparse point：{path}");
                    }
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        var handle = OpenLockedDirectoryHandle(path);
                        owned.Add(handle);
                        _directoryHandles.Add(path, handle);
                        CaptureOriginalSecurity(path, created: false);
                        discovered[path] = true;
                        pending.Push((path, depth));
                    }
                    else
                    {
                        var handle = OpenLockedRegularFileHandle(path);
                        owned.Add(handle);
                        _fileHandles.Add(path, handle);
                        CaptureOriginalFileSecurity(path);
                    }
                }
            }

            if (validateExactProductAcl)
            {
                var directoryGrants = new[]
                {
                    DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
                    DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
                    DirectoryGrant(userSid, FileSystemRights.ReadAndExecute),
                    DirectoryGrant(serviceSid, FileSystemRights.ReadAndExecute),
                };
                var fileGrants = new[]
                {
                    new InstallerAclGrant(LocalSystemSid, FileSystemRights.FullControl),
                    new InstallerAclGrant(AdministratorsSid, FileSystemRights.FullControl),
                    new InstallerAclGrant(userSid, FileSystemRights.ReadAndExecute),
                    new InstallerAclGrant(serviceSid, FileSystemRights.ReadAndExecute),
                };
                foreach (var path in discovered.Where(pair => pair.Value).Select(pair => pair.Key))
                {
                    ValidateExactDirectoryAcl(path, directoryGrants);
                }
                foreach (var path in discovered.Where(pair => !pair.Value).Select(pair => pair.Key))
                {
                    ValidateExactFileAcl(path, fileGrants);
                }
            }

            var verified = EnumerateVersionNamespace(root, maximumEntries, maximumDepth);
            if (verified.Count != discovered.Count || discovered.Any(pair =>
                    !verified.TryGetValue(pair.Key, out var isDirectory) || isDirectory != pair.Value))
            {
                throw new IOException("既有版本目錄在嚴格信任檢查期間遭到變更。");
            }
            return discovered;
        }

        private void CaptureOriginalSecurity(string path, bool created)
        {
            if (created || _originalSecurity.ContainsKey(path))
            {
                return;
            }
            _originalSecurity.Add(
                path,
                new DirectoryInfo(path).GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group));
        }

        private void CaptureOriginalFileSecurity(string path)
        {
            if (_originalFileSecurity.ContainsKey(path))
            {
                return;
            }
            _originalFileSecurity.Add(
                path,
                new FileInfo(path).GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group));
        }

        private static Dictionary<string, bool> EnumerateVersionNamespace(
            string root,
            int maximumEntries,
            int maximumDepth)
        {
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [root] = true,
            };
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            var entries = 0;
            while (pending.Count > 0)
            {
                var (parent, parentDepth) = pending.Pop();
                foreach (var rawEntry in Directory.EnumerateFileSystemEntries(parent))
                {
                    if (++entries > maximumEntries || parentDepth + 1 > maximumDepth)
                    {
                        throw new InvalidDataException("既有版本目錄超出安全重驗限制。");
                    }
                    var path = NormalizeFinalHandlePath(rawEntry);
                    var attributes = File.GetAttributes(path);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        throw new IOException("既有版本目錄在安全重驗期間出現連結。");
                    }
                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    if (!result.TryAdd(path, isDirectory))
                    {
                        throw new IOException("既有版本目錄在安全重驗期間出現重複項目。");
                    }
                    if (isDirectory)
                    {
                        pending.Push((path, parentDepth + 1));
                    }
                }
            }
            return result;
        }

        private IReadOnlyList<string> ExpandOwnedDirectoryPaths(IReadOnlyList<string> directories)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in directories)
            {
                var normalized = NormalizeOwnedDescendant(directory);
                var current = _normalizedRoot;
                foreach (var segment in Path.GetRelativePath(_normalizedRoot, normalized).Split(
                             [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    current = NormalizeFinalHandlePath(Path.Combine(current, segment));
                    paths.Add(current);
                }
            }
            return paths.OrderBy(path => path.Count(character => character == Path.DirectorySeparatorChar))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private string NormalizeOwnedDescendant(string directory)
        {
            var normalized = NormalizeFinalHandlePath(directory);
            if (!normalized.StartsWith(
                    _normalizedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("受保護安裝目錄必須位於安裝根目錄內。");
            }
            return normalized;
        }
    }

    public async Task<InstallerServiceSnapshot> CaptureAndStopServiceAsync(
        string name,
        string installRoot,
        Action<InstallerServiceSnapshot> snapshotCaptured,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshotCaptured);
        var query = await RunScAsync(["query", name], allowMissing: true, cancellationToken)
            .ConfigureAwait(false);
        InstallerServiceSnapshot snapshot;
        if (query.ExitCode == 0)
        {
            var security = await RunScAsync(["sdshow", name], false, cancellationToken)
                .ConfigureAwait(false);
            var sidType = await RunScAsync(["qsidtype", name], false, cancellationToken)
                .ConfigureAwait(false);
            if (!sidType.Output.Contains("UNRESTRICTED", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("既有 X MCSV Service 未啟用受管理的 unrestricted Service SID。");
            }
            snapshot = ReadExistingServiceSnapshot(
                name,
                installRoot,
                query.Output,
                ExtractSecurityDescriptor(security.Output));
        }
        else
        {
            snapshot = new InstallerServiceSnapshot(
                Existed: false,
                ImagePath: null,
                WasRunning: false,
                SecurityDescriptor: null,
                DelayedAutoStart: true);
        }

        snapshotCaptured(snapshot);
        if (snapshot.WasRunning)
        {
            _ = await RunScAsync(["stop", name], allowMissing: false, cancellationToken)
                .ConfigureAwait(false);
            await WaitForServiceStateAsync(name, "STOPPED", cancellationToken).ConfigureAwait(false);
        }
        return snapshot;
    }

    public async Task ConfigureServiceAsync(
        string name,
        string executable,
        string dataRoot,
        string exchangeRoot,
        string installRoot,
        InstallerServiceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        _ = installRoot;
        var binaryPath =
            $"\"{Path.GetFullPath(executable)}\" " +
            $"\"--Mcsv:Service:DataRoot={Path.GetFullPath(dataRoot)}\" " +
            $"\"--Mcsv:Service:ExchangeRoot={Path.GetFullPath(exchangeRoot)}\"";
        if (snapshot.Existed)
        {
            _ = await RunScAsync(
                    ["config", name, "binPath=", binaryPath, "start=", "delayed-auto", "obj=", $@"NT SERVICE\{name}"],
                    allowMissing: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            _ = await RunScAsync(
                    ["create", name, "binPath=", binaryPath, "start=", "delayed-auto",
                     "DisplayName=", "Muhun MCSV Service", "obj=", $@"NT SERVICE\{name}"],
                    allowMissing: false,
                    cancellationToken)
                .ConfigureAwait(false);
            _ = await RunScAsync(["sidtype", name, "unrestricted"], false, cancellationToken)
                .ConfigureAwait(false);
            _ = await RunScAsync(["description", name, "Muhun MCSV background service."], false, cancellationToken)
                .ConfigureAwait(false);
            _ = await RunScAsync(
                    ["failure", name, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000"],
                    false,
                    cancellationToken)
                .ConfigureAwait(false);
            _ = await RunScAsync(["failureflag", name, "1"], false, cancellationToken)
                .ConfigureAwait(false);
        }

        await GrantServiceSelfUpdateRightsAsync(name, cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreServiceAsync(
        string name,
        InstallerServiceSnapshot snapshot,
        bool restart,
        CancellationToken cancellationToken)
    {
        var query = await RunScAsync(["query", name], allowMissing: true, cancellationToken)
            .ConfigureAwait(false);
        if (query.ExitCode == 0 && !IsStopped(query.Output))
        {
            _ = await RunScAsync(["stop", name], allowMissing: false, cancellationToken)
                .ConfigureAwait(false);
            await WaitForServiceStateAsync(name, "STOPPED", cancellationToken).ConfigureAwait(false);
        }

        if (!snapshot.Existed)
        {
            if (query.ExitCode == 0)
            {
                _ = await RunScAsync(["delete", name], allowMissing: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(snapshot.ImagePath) ||
            string.IsNullOrWhiteSpace(snapshot.SecurityDescriptor))
        {
            throw new InvalidDataException("既有 Service 回復快照不完整。");
        }

        _ = await RunScAsync(
                ["config", name, "binPath=", snapshot.ImagePath,
                 "start=", snapshot.DelayedAutoStart ? "delayed-auto" : "auto",
                 "obj=", $@"NT SERVICE\{name}"],
                false,
                cancellationToken)
            .ConfigureAwait(false);
        _ = await RunScAsync(["sdset", name, snapshot.SecurityDescriptor], false, cancellationToken)
            .ConfigureAwait(false);
        if (restart && snapshot.WasRunning)
        {
            _ = await RunScAsync(["start", name], false, cancellationToken).ConfigureAwait(false);
            await WaitForServiceStateAsync(name, "RUNNING", cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StartServiceAsync(string name, CancellationToken cancellationToken)
    {
        _ = await RunScAsync(["start", name], false, cancellationToken).ConfigureAwait(false);
        await WaitForServiceStateAsync(name, "RUNNING", cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitForServiceHealthAsync(
        string serviceExecutable,
        string dataRoot,
        string version,
        CancellationToken cancellationToken)
    {
        ProductUpdateManifestParser.ValidateVersion(version);
        var port = ReadServicePort(serviceExecutable);
        using var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        })
        {
            Timeout = TimeSpan.FromSeconds(3),
        };
        var endpoint = new Uri($"http://127.0.0.1:{port}/api/v1/system/activation-ready");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        Exception? lastError = null;
        string? lastSafeStartupDiagnostic = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var token = ReadBoundedAsciiFile(
                    Path.Combine(dataRoot, "secrets", "service-rest-token.v1"), 64, 128);
                var installationIdText = ReadBoundedAsciiFile(
                    Path.Combine(dataRoot, "data", "installation-id.v1"), 36, 128);
                if (token.Length != 64 || !token.All(Uri.IsHexDigit) ||
                    !Guid.TryParseExact(installationIdText, "D", out var installationId) ||
                    installationId == Guid.Empty)
                {
                    throw new InvalidDataException("Service 啟用憑證格式無效。");
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.TryAddWithoutValidation("X-MCSV-Service-Token", token);
                request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                using var response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK ||
                    response.Content.Headers.ContentLength is > 16 * 1024)
                {
                    throw new HttpRequestException("activation-ready HTTP 狀態或大小無效。");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var bounded = new MemoryStream(16 * 1024);
                await CopyBoundedAsync(stream, bounded, 16 * 1024, cancellationToken).ConfigureAwait(false);
                var ready = ReadActivationReadyResponse(bounded.ToArray());
                var identityMatches =
                    string.Equals(ready.Product, "Muhun MCSV Manager", StringComparison.Ordinal) &&
                    string.Equals(ready.Version, version, StringComparison.Ordinal) &&
                    ready.InstallationId == installationId &&
                    ready.StartedAtUtc != default &&
                    ready.StartedAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1);
                if (!identityMatches)
                {
                    throw new InvalidDataException(
                        "activation-ready 未證明版本與 installationId。");
                }

                if (ready.Ready)
                {
                    if (!string.Equals(ready.Status, "ready", StringComparison.Ordinal) ||
                        ready.StartupFailure is not null)
                    {
                        throw new InvalidDataException("activation-ready ready 狀態互相矛盾。");
                    }
                    return;
                }

                if (!string.Equals(ready.Status, "starting", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("activation-ready starting 狀態無效。");
                }
                lastSafeStartupDiagnostic = ready.StartupFailure is null
                    ? null
                    : FormatSafeStartupFailure(ready.StartupFailure);
                throw new InvalidDataException("Service 尚未完成 activation-ready。");
            }
            catch (Exception exception) when (
                exception is IOException or InvalidDataException or HttpRequestException or
                JsonException or TaskCanceledException)
            {
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                lastError = exception;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        var timeoutMessage = lastSafeStartupDiagnostic is null
            ? "Service 未通過 activation-ready 健康驗證。"
            : "Service 未通過 activation-ready 健康驗證。" + Environment.NewLine +
              lastSafeStartupDiagnostic;
        throw new TimeoutException(timeoutMessage, lastError);
    }

    internal static InstallerActivationReadyResponse ReadActivationReadyResponse(
        ReadOnlySpan<byte> json)
    {
        var ready = JsonSerializer.Deserialize<InstallerActivationReadyResponse>(
            json,
            StrictActivationReadyJson);
        return ready ?? throw new InvalidDataException("activation-ready JSON 內容為 null。");
    }

    internal static string FormatSafeStartupFailure(
        InstallerActivationFailureDiagnostic failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var reason = failure.Code switch
        {
            "ipc.operator_group_missing" => "找不到必要的本機操作員群組",
            "ipc.binding_missing" => "找不到安裝者 IPC SID 綁定",
            "ipc.binding_invalid" => "安裝者 IPC SID 綁定無效",
            "ipc.access_denied" => "IPC 管道或必要檔案的存取遭拒",
            "ipc.io_failure" => "IPC 管道發生 I/O 錯誤",
            "ipc.configuration_invalid" => "IPC 管道設定無效",
            _ => throw new InvalidDataException("activation-ready IPC 診斷代碼無效。"),
        };
        if (!IsSafeStartupExceptionType(failure.ExceptionType) ||
            (failure.InnerExceptionType is null) != (failure.InnerHResult is null) ||
            (failure.InnerExceptionType is not null &&
             !IsSafeStartupExceptionType(failure.InnerExceptionType)))
        {
            throw new InvalidDataException("activation-ready IPC 診斷型別無效。");
        }

        var exceptionIdentity =
            $"{failure.ExceptionType}, HRESULT 0x{unchecked((uint)failure.HResult):X8}";
        if (failure.InnerExceptionType is not null && failure.InnerHResult is { } innerHResult)
        {
            exceptionIdentity +=
                $"；內層 {failure.InnerExceptionType}, HRESULT 0x{unchecked((uint)innerHResult):X8}";
        }
        return $"安全診斷：{reason}（{exceptionIdentity}）。";
    }

    private static bool IsSafeStartupExceptionType(string value) => value is
        nameof(IdentityNotMappedException) or
        nameof(FileNotFoundException) or
        nameof(InvalidDataException) or
        nameof(UnauthorizedAccessException) or
        nameof(IOException) or
        nameof(Win32Exception) or
        nameof(InvalidOperationException) or
        nameof(Exception);

    public void ApplyAccessControl(
        InstallerLayout layout,
        string serviceName,
        string targetVersionRoot,
        string operatorsGroupSid)
    {
        var serviceSid = ResolveServiceSid(serviceName);
        var userSid = new SecurityIdentifier(CurrentUserSid);
        var operatorsSid = ValidateOperatorGroupSid(operatorsGroupSid);
        SetExactDirectoryAcl(
            layout.Root,
            [
                DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
                DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
                new InstallerAclGrant(userSid, FileSystemRights.ReadAndExecute),
                new InstallerAclGrant(operatorsSid, FileSystemRights.ReadAndExecute),
                new InstallerAclGrant(serviceSid, FileSystemRights.ReadAndExecute),
            ]);
        SetExactDirectoryAcl(
            layout.VersionsRoot,
            [
                DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
                DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
                DirectoryGrant(userSid, FileSystemRights.ReadAndExecute),
                new InstallerAclGrant(serviceSid, FileSystemRights.Modify),
                DirectoryGrant(serviceSid, FileSystemRights.ReadAndExecute),
            ]);
        ApplyReadOnlyExecutableTree(targetVersionRoot, userSid, serviceSid);
        SetExactDirectoryAcl(
            layout.ActivationRoot,
            [
                DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
                DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
                DirectoryGrant(serviceSid, FileSystemRights.Modify),
            ]);
        SetExactDirectoryAcl(
            layout.LauncherRoot,
            [
                DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
                DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
                DirectoryGrant(userSid, FileSystemRights.ReadAndExecute),
                DirectoryGrant(serviceSid, FileSystemRights.ReadAndExecute),
            ]);
        SetTraverseContainer(
            Path.GetDirectoryName(layout.ServiceRoot)!,
            [serviceSid, userSid, operatorsSid]);
        SetTraverseContainer(
            Path.GetDirectoryName(layout.ExchangeRoot)!,
            [serviceSid, userSid, operatorsSid]);
        var userSidRoot = Path.GetDirectoryName(layout.UserRoot)!;
        SetTraverseContainer(Path.GetDirectoryName(userSidRoot)!, [userSid]);
        SetTraverseContainer(userSidRoot, [userSid]);
        SetExactDirectoryAcl(
            layout.ServiceRoot,
            [
                DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
                DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
                DirectoryGrant(serviceSid, FileSystemRights.Modify),
                new InstallerAclGrant(userSid, FileSystemRights.ReadAndExecute),
                new InstallerAclGrant(operatorsSid, FileSystemRights.ReadAndExecute),
            ]);
        foreach (var name in InstallerEngine.ServiceRuntimeDirectoryNames)
        {
            var browseable = name is "servers" or "runtimes";
            SetExactDirectoryAcl(
                Path.Combine(layout.ServiceRoot, name),
                browseable
                    ? [
                        DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
                        DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
                        DirectoryGrant(serviceSid, FileSystemRights.Modify),
                        DirectoryGrant(userSid, FileSystemRights.ReadAndExecute),
                        DirectoryGrant(operatorsSid, FileSystemRights.ReadAndExecute),
                    ]
                    : [
                    DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
                    DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
                    DirectoryGrant(serviceSid, FileSystemRights.Modify),
                    ]);
        }
        SetExactDirectoryAcl(
            layout.ExchangeRoot,
            [
                DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
                DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
                DirectoryGrant(serviceSid, FileSystemRights.Modify),
                DirectoryGrant(userSid, FileSystemRights.Modify),
                DirectoryGrant(operatorsSid, FileSystemRights.Modify),
            ]);
        SetExactDirectoryAcl(
            layout.UserRoot,
            [
                DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
                DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
                DirectoryGrant(userSid, FileSystemRights.Modify),
            ]);
        foreach (var name in InstallerEngine.UserRuntimeDirectoryNames)
        {
            SetExactDirectoryAcl(
                Path.Combine(layout.UserRoot, name),
                [
                    DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
                    DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
                    DirectoryGrant(userSid, FileSystemRights.Modify),
                ]);
        }
        SetExactDirectoryAcl(
            layout.StagingRoot,
            [
                DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
                DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
            ]);
        SetExactFileAcl(
            Path.Combine(layout.Root, InstallerLayout.InstallMarkerName),
            [
                new InstallerAclGrant(LocalSystemSid, FileSystemRights.FullControl),
                new InstallerAclGrant(AdministratorsSid, FileSystemRights.FullControl),
                new InstallerAclGrant(userSid, FileSystemRights.Read),
                new InstallerAclGrant(serviceSid, FileSystemRights.Read),
            ]);
        ApplyActivePointerAccessControl(layout, serviceName);
    }

    public void ApplyActivePointerAccessControl(InstallerLayout layout, string serviceName)
    {
        SetExactFileAcl(
            Path.Combine(layout.Root, "active-version.v1"),
            [
                new InstallerAclGrant(LocalSystemSid, FileSystemRights.FullControl),
                new InstallerAclGrant(AdministratorsSid, FileSystemRights.FullControl),
                new InstallerAclGrant(new SecurityIdentifier(CurrentUserSid), FileSystemRights.Read),
                new InstallerAclGrant(ResolveServiceSid(serviceName), FileSystemRights.Modify),
            ]);
    }

    public InstallerRegistrationSnapshot CaptureInstallationRegistration()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(ProductRegistryPath, writable: false);
        var existingNames = key is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(key.GetValueNames(), StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, InstallerRegistryValueSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ProductRegistryValueNames)
        {
            var existed = existingNames.Contains(name);
            values.Add(
                name,
                new InstallerRegistryValueSnapshot(
                    existed,
                    existed
                        ? key!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                        : null,
                    existed ? key!.GetValueKind(name) : RegistryValueKind.Unknown));
        }

        return new InstallerRegistrationSnapshot(key is not null, values);
    }

    public InstallerShortcutSnapshot CaptureStartMenuShortcut(string channel)
    {
        var (productDirectory, shortcutPath) = ResolveStartMenuShortcutPath(channel);
        if (File.Exists(productDirectory))
        {
            throw new IOException("開始功能表的 X MCSV 路徑不是目錄。");
        }
        var productDirectoryExisted = Directory.Exists(productDirectory);
        if (productDirectoryExisted)
        {
            InstallerLayout.RejectExistingReparsePoints(productDirectory);
        }
        if (Directory.Exists(shortcutPath))
        {
            throw new IOException("開始功能表捷徑路徑不是一般檔案。");
        }
        if (!File.Exists(shortcutPath))
        {
            return new InstallerShortcutSnapshot(
                productDirectory,
                shortcutPath,
                productDirectoryExisted,
                ShortcutExisted: false,
                Content: null);
        }

        using var handle = OpenLockedRegularFileHandle(shortcutPath);
        using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
        if (stream.Length > MaximumShortcutSnapshotBytes)
        {
            throw new InvalidDataException("既有開始功能表捷徑異常過大，安裝已停止。");
        }
        var content = new byte[checked((int)stream.Length)];
        stream.ReadExactly(content);
        return new InstallerShortcutSnapshot(
            productDirectory,
            shortcutPath,
            productDirectoryExisted,
            ShortcutExisted: true,
            content);
    }

    public void WriteInstallationRegistration(
        InstallerLayout layout,
        string version,
        string guiExecutable,
        string launcherExecutable)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using (var key = machine.CreateSubKey(ProductRegistryPath, writable: true))
        {
            key.SetValue("ProductId", ProductId, RegistryValueKind.String);
            key.SetValue(
                "PublisherCertificateSha256",
                PublisherCertificateSha256,
                RegistryValueKind.String);
            key.SetValue("InstallRoot", layout.Root, RegistryValueKind.String);
            key.SetValue("Channel", layout.Channel, RegistryValueKind.String);
            key.SetValue("Version", version, RegistryValueKind.String);
            key.SetValue("ServiceDataRoot", layout.ServiceRoot, RegistryValueKind.String);
            key.SetValue("UserDataRoot", layout.UserRoot, RegistryValueKind.String);
            key.SetValue("ExchangeRoot", layout.ExchangeRoot, RegistryValueKind.String);
        }

        // Do not create an Apps & Features entry until Setup ships a managed EXE uninstaller.
        // A visible ARP row without a valid UninstallString is worse than no registration.
        _ = guiExecutable;
        _ = launcherExecutable;
    }

    public void CreateStartMenuShortcut(string launcherExecutable, string installRoot, string channel)
    {
        var (productDirectory, shortcutPath) = ResolveStartMenuShortcutPath(channel);
        Directory.CreateDirectory(productDirectory);
        InstallerLayout.RejectExistingReparsePoints(productDirectory);
        var temporaryPath = Path.Combine(
            productDirectory,
            $".X-MCSV-{Guid.NewGuid():N}.tmp.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host 無法建立開始功能表捷徑。");
        dynamic? shell = null;
        dynamic? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shell!.CreateShortcut(temporaryPath);
            shortcut.TargetPath = launcherExecutable;
            shortcut.Arguments = $"--launch-current --install-root \"{installRoot}\"";
            shortcut.WorkingDirectory = Path.GetDirectoryName(launcherExecutable)!;
            shortcut.Description = $"X MCSV {channel}";
            shortcut.Save();
            if (!File.Exists(temporaryPath))
            {
                throw new IOException("Windows Script Host 未產生開始功能表捷徑。");
            }
            if (File.Exists(shortcutPath))
            {
                File.Replace(temporaryPath, shortcutPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, shortcutPath, overwrite: false);
            }
        }
        finally
        {
            if (shortcut is not null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void RestoreInstallationRegistration(InstallerRegistrationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        if (snapshot.KeyExisted)
        {
            using var key = machine.CreateSubKey(ProductRegistryPath, writable: true)
                ?? throw new UnauthorizedAccessException("無法開啟 X MCSV 安裝登錄資料以回復。");
            RestoreRegistrationValues(key, snapshot);
            return;
        }

        using (var key = machine.OpenSubKey(ProductRegistryPath, writable: true))
        {
            if (key is null)
            {
                return;
            }
            RestoreRegistrationValues(key, snapshot);
            if (key.ValueCount != 0 || key.SubKeyCount != 0)
            {
                return;
            }
        }
        machine.DeleteSubKey(ProductRegistryPath, throwOnMissingSubKey: false);
    }

    public void RestoreStartMenuShortcut(InstallerShortcutSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ShortcutExisted)
        {
            if (snapshot.Content is null)
            {
                throw new InvalidDataException("開始功能表捷徑回復快照缺少內容。");
            }
            Directory.CreateDirectory(snapshot.ProductDirectory);
            InstallerLayout.RejectExistingReparsePoints(snapshot.ProductDirectory);
            WriteShortcutBytesAtomically(snapshot.ShortcutPath, snapshot.Content);
            return;
        }

        if (Directory.Exists(snapshot.ShortcutPath))
        {
            throw new IOException("無法安全移除非檔案的開始功能表捷徑路徑。");
        }
        File.Delete(snapshot.ShortcutPath);
        if (!snapshot.ProductDirectoryExisted && Directory.Exists(snapshot.ProductDirectory))
        {
            InstallerLayout.RejectExistingReparsePoints(snapshot.ProductDirectory);
            if (!Directory.EnumerateFileSystemEntries(snapshot.ProductDirectory).Any())
            {
                Directory.Delete(snapshot.ProductDirectory, recursive: false);
            }
        }
    }

    private static (string ProductDirectory, string ShortcutPath) ResolveStartMenuShortcutPath(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel) ||
            channel.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("安裝通道名稱不可用於開始功能表捷徑。");
        }
        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        var productDirectory = Path.Combine(startMenu, "X MCSV");
        return (productDirectory, Path.Combine(productDirectory, $"X MCSV ({channel}).lnk"));
    }

    private static void RestoreRegistrationValues(
        RegistryKey key,
        InstallerRegistrationSnapshot snapshot)
    {
        foreach (var name in ProductRegistryValueNames)
        {
            if (!snapshot.Values.TryGetValue(name, out var value))
            {
                throw new InvalidDataException($"安裝登錄資料回復快照缺少欄位：{name}");
            }
            if (value.Existed)
            {
                key.SetValue(
                    name,
                    value.Value ?? throw new InvalidDataException($"安裝登錄資料欄位缺少值：{name}"),
                    value.Kind);
            }
            else
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }
        }
    }

    private static void WriteShortcutBytesAtomically(string destination, byte[] content)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("開始功能表捷徑缺少父目錄。");
        var temporary = Path.Combine(directory, $".X-MCSV-{Guid.NewGuid():N}.restore.tmp.lnk");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(destination))
            {
                File.Replace(temporary, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, destination, overwrite: false);
            }
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static InstallerServiceSnapshot ReadExistingServiceSnapshot(
        string name,
        string installRoot,
        string queryOutput,
        string securityDescriptor)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var service = machine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{name}",
            writable: false) ?? throw new InvalidDataException("既有 Service 登錄資料遺失。");
        var objectName = service.GetValue(
            "ObjectName", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var imagePath = service.GetValue(
            "ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (!string.Equals(objectName, $@"NT SERVICE\{name}", StringComparison.OrdinalIgnoreCase) ||
            service.GetValue("Start", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not int start ||
            start != 2 || string.IsNullOrWhiteSpace(imagePath))
        {
            throw new InvalidDataException("既有同名 Service 不是 X MCSV 受管理服務。");
        }

        ValidateOwnedServiceImagePath(imagePath, installRoot);
        var delayed = service.GetValue(
            "DelayedAutoStart", 0, RegistryValueOptions.DoNotExpandEnvironmentNames) is int delayedValue &&
                      delayedValue == 1;
        return new InstallerServiceSnapshot(
            Existed: true,
            ImagePath: imagePath,
            WasRunning: !IsStopped(queryOutput),
            SecurityDescriptor: securityDescriptor,
            DelayedAutoStart: delayed);
    }

    private static void ValidateOwnedServiceImagePath(string imagePath, string expectedInstallRoot)
    {
        if (imagePath.Length > 4096 || imagePath[0] != '"')
        {
            throw new InvalidDataException("既有 Service ImagePath 無效。");
        }

        var end = imagePath.IndexOf('"', 1);
        if (end <= 1)
        {
            throw new InvalidDataException("既有 Service executable 綁定無效。");
        }

        var executable = Path.GetFullPath(imagePath[1..end]);
        InstallerLayout.RejectExistingReparsePoints(executable);
        if (!File.Exists(executable) ||
            !string.Equals(Path.GetFileName(executable), "Muhun MCSV Service.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("既有 Service executable 不存在或名稱無效。");
        }

        var serviceDirectory = Directory.GetParent(executable)?.FullName;
        var versionRoot = serviceDirectory is null ? null : Directory.GetParent(serviceDirectory)?.FullName;
        var versionsRoot = versionRoot is null ? null : Directory.GetParent(versionRoot)?.FullName;
        var installRoot = versionsRoot is null ? null : Directory.GetParent(versionsRoot)?.FullName;
        if (!string.Equals(Path.GetFileName(serviceDirectory), "service-win-x64", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(versionsRoot), "versions", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot!)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedInstallRoot)),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("既有 Service 不屬於選定的 X MCSV 安裝根目錄。");
        }

        var marker = Path.Combine(installRoot!, InstallerLayout.InstallMarkerName);
        if (!File.Exists(marker) ||
            !string.Equals(
                File.ReadAllText(marker).Trim(),
                InstallerLayout.InstallMarkerValue,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("既有 Service 的安裝根標記無效。");
        }
    }

    private static async Task GrantServiceSelfUpdateRightsAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var serviceSid = ResolveServiceSid(name).Value;
        var current = await RunScAsync(["sdshow", name], false, cancellationToken).ConfigureAwait(false);
        var descriptor = ExtractSecurityDescriptor(current.Output);
        var ace = $"(A;;CCDCLCRPWP;;;{serviceSid})";
        if (descriptor.Contains(ace, StringComparison.Ordinal))
        {
            return;
        }

        var saclIndex = descriptor.IndexOf("S:", StringComparison.Ordinal);
        var updated = saclIndex >= 0 ? descriptor.Insert(saclIndex, ace) : descriptor + ace;
        if (updated.Length > 8192)
        {
            throw new InvalidDataException("Service DACL 超出安全大小。");
        }
        _ = await RunScAsync(["sdset", name, updated], false, cancellationToken).ConfigureAwait(false);
        var committed = await RunScAsync(["sdshow", name], false, cancellationToken).ConfigureAwait(false);
        if (!ExtractSecurityDescriptor(committed.Output).Contains(ace, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Service 自我更新 ACE 未正確保存。");
        }
    }

    private static string ExtractSecurityDescriptor(string output)
    {
        var descriptor = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("D:", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(descriptor) || descriptor.Length > 8192)
        {
            throw new InvalidDataException("Service DACL 回覆遺失或過大。");
        }
        return descriptor;
    }

    private static int ReadServicePort(string serviceExecutable)
    {
        var settings = Path.Combine(Path.GetDirectoryName(serviceExecutable)!, "appsettings.json");
        if (!File.Exists(settings))
        {
            return 39050;
        }
        InstallerLayout.RejectExistingReparsePoints(settings);
        var item = new FileInfo(settings);
        if (item.Attributes.HasFlag(FileAttributes.ReparsePoint) || item.Length is < 2 or > 64 * 1024)
        {
            throw new InvalidDataException("Service appsettings.json 大小或型態無效。");
        }
        using var document = JsonDocument.Parse(File.ReadAllBytes(settings));
        var port = document.RootElement.TryGetProperty("Mcsv", out var mcsv) &&
                   mcsv.TryGetProperty("Service", out var service) &&
                   service.TryGetProperty("Port", out var value)
            ? value.GetInt32()
            : 39050;
        return port is >= 1024 and <= 65535
            ? port
            : throw new InvalidDataException("Service REST Port 超出允許範圍。");
    }

    private static string ReadBoundedAsciiFile(string path, int minimumBytes, int maximumBytes)
    {
        InstallerLayout.RejectExistingReparsePoints(path);
        var item = new FileInfo(path);
        if (!item.Exists || item.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            item.Length < minimumBytes || item.Length > maximumBytes)
        {
            throw new IOException("Service 啟用憑證尚未建立或大小無效。");
        }
        var bytes = File.ReadAllBytes(path);
        if (bytes.Any(value => value > 0x7f))
        {
            throw new InvalidDataException("Service 啟用憑證不是 ASCII。");
        }
        return Encoding.ASCII.GetString(bytes).Trim();
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(4096);
        var total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }
            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException("activation-ready 回覆超出大小限制。");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ApplyReadOnlyExecutableTree(
        string targetVersionRoot,
        SecurityIdentifier userSid,
        SecurityIdentifier serviceSid)
    {
        var pending = new Stack<string>();
        pending.Push(targetVersionRoot);
        var entries = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            InstallerLayout.RejectExistingReparsePoints(directory);
            if (++entries > 100_000)
            {
                throw new InvalidDataException("版本目錄超出 ACL 驗證數量限制。");
            }
            SetExactDirectoryAcl(
                directory,
                [
                    DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
                    DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
                    DirectoryGrant(userSid, FileSystemRights.ReadAndExecute),
                    DirectoryGrant(serviceSid, FileSystemRights.ReadAndExecute),
                ]);
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (++entries > 100_000)
                {
                    throw new InvalidDataException("版本目錄超出 ACL 驗證數量限制。");
                }
                InstallerLayout.RejectExistingReparsePoints(file);
                SetExactFileAcl(
                    file,
                    [
                        new InstallerAclGrant(LocalSystemSid, FileSystemRights.FullControl),
                        new InstallerAclGrant(AdministratorsSid, FileSystemRights.FullControl),
                        new InstallerAclGrant(userSid, FileSystemRights.ReadAndExecute),
                        new InstallerAclGrant(serviceSid, FileSystemRights.ReadAndExecute),
                    ]);
            }
        }
    }

    private static InstallerAclGrant DirectoryGrant(
        SecurityIdentifier sid,
        FileSystemRights rights)
        => new(
            sid,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None);

    private static IReadOnlyDictionary<string, IReadOnlyList<InstallerAclGrant>>
        CreateExpectedManagedDirectoryAclMap(
            InstallerLayout layout,
            SecurityIdentifier userSid,
            SecurityIdentifier serviceSid,
            SecurityIdentifier operatorsSid)
    {
        var result = new Dictionary<string, IReadOnlyList<InstallerAclGrant>>(
            StringComparer.OrdinalIgnoreCase);
        InstallerAclGrant[] AdministrativeOnly() =>
        [
            DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
            DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
        ];
        void Add(string path, params InstallerAclGrant[] grants)
            => result.Add(NormalizeFinalHandlePath(path), grants);

        Add(
            layout.Root,
            DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
            DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
            new InstallerAclGrant(userSid, FileSystemRights.ReadAndExecute),
            new InstallerAclGrant(operatorsSid, FileSystemRights.ReadAndExecute),
            new InstallerAclGrant(serviceSid, FileSystemRights.ReadAndExecute));
        Add(
            layout.VersionsRoot,
            DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
            DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
            DirectoryGrant(userSid, FileSystemRights.ReadAndExecute),
            new InstallerAclGrant(serviceSid, FileSystemRights.Modify),
            DirectoryGrant(serviceSid, FileSystemRights.ReadAndExecute));
        Add(
            layout.ActivationRoot,
            DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
            DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
            DirectoryGrant(serviceSid, FileSystemRights.Modify));
        Add(
            layout.LauncherRoot,
            DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
            DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
            DirectoryGrant(userSid, FileSystemRights.ReadAndExecute),
            DirectoryGrant(serviceSid, FileSystemRights.ReadAndExecute));
        Add(
            Path.GetDirectoryName(layout.ServiceRoot)!,
            DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
            DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
            new InstallerAclGrant(serviceSid, FileSystemRights.ReadAndExecute),
            new InstallerAclGrant(userSid, FileSystemRights.ReadAndExecute),
            new InstallerAclGrant(operatorsSid, FileSystemRights.ReadAndExecute));
        Add(
            Path.GetDirectoryName(layout.ExchangeRoot)!,
            DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
            DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
            new InstallerAclGrant(serviceSid, FileSystemRights.ReadAndExecute),
            new InstallerAclGrant(userSid, FileSystemRights.ReadAndExecute),
            new InstallerAclGrant(operatorsSid, FileSystemRights.ReadAndExecute));
        var userSidRoot = Path.GetDirectoryName(layout.UserRoot)!;
        Add(
            Path.GetDirectoryName(userSidRoot)!,
            DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
            DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
            new InstallerAclGrant(userSid, FileSystemRights.ReadAndExecute));
        Add(
            userSidRoot,
            DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
            DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
            new InstallerAclGrant(userSid, FileSystemRights.ReadAndExecute));
        Add(
            layout.ServiceRoot,
            DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
            DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
            DirectoryGrant(serviceSid, FileSystemRights.Modify),
            new InstallerAclGrant(userSid, FileSystemRights.ReadAndExecute),
            new InstallerAclGrant(operatorsSid, FileSystemRights.ReadAndExecute));
        foreach (var name in InstallerEngine.ServiceRuntimeDirectoryNames)
        {
            var grants = new List<InstallerAclGrant>
            {
                DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
                DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
                DirectoryGrant(serviceSid, FileSystemRights.Modify),
            };
            if (name is "servers" or "runtimes")
            {
                grants.Add(DirectoryGrant(userSid, FileSystemRights.ReadAndExecute));
                grants.Add(DirectoryGrant(operatorsSid, FileSystemRights.ReadAndExecute));
            }
            Add(Path.Combine(layout.ServiceRoot, name), [.. grants]);
        }
        Add(
            layout.ExchangeRoot,
            DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
            DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
            DirectoryGrant(serviceSid, FileSystemRights.Modify),
            DirectoryGrant(userSid, FileSystemRights.Modify),
            DirectoryGrant(operatorsSid, FileSystemRights.Modify));
        Add(
            layout.UserRoot,
            DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
            DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
            DirectoryGrant(userSid, FileSystemRights.Modify));
        foreach (var name in InstallerEngine.UserRuntimeDirectoryNames)
        {
            Add(
                Path.Combine(layout.UserRoot, name),
                DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
                DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
                DirectoryGrant(userSid, FileSystemRights.Modify));
        }
        Add(layout.StagingRoot, AdministrativeOnly());
        return result;
    }

    internal static bool IsRequiredSharedManagedDirectory(InstallerLayout layout, string path)
    {
        var normalized = NormalizeFinalHandlePath(path);
        var userSidRoot = Path.GetDirectoryName(layout.UserRoot)!;
        string[] required =
        [
            layout.Root,
            layout.VersionsRoot,
            layout.ActivationRoot,
            layout.StagingRoot,
            layout.LauncherRoot,
            Path.GetDirectoryName(layout.ServiceRoot)!,
            Path.GetDirectoryName(layout.ExchangeRoot)!,
            Path.GetDirectoryName(userSidRoot)!,
        ];
        return required.Any(candidate => string.Equals(
            normalized,
            NormalizeFinalHandlePath(candidate),
            StringComparison.OrdinalIgnoreCase));
    }

    private static void SetAdministrativeOnlyDirectoryAcl(string path)
        => new DirectoryInfo(path).SetAccessControl(CreateAdministrativeOnlyDirectorySecurity());

    private static void SetAdministrativeOnlyFileAcl(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        foreach (var sid in new[] { LocalSystemSid, AdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }
        new FileInfo(path).SetAccessControl(security);
    }

    private static DirectorySecurity CreateAdministrativeOnlyDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        foreach (var grant in new[]
                 {
                     DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
                     DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                grant.Sid,
                grant.Rights,
                grant.InheritanceFlags,
                grant.PropagationFlags,
                AccessControlType.Allow));
        }

        return security;
    }

    internal static void ValidateTrustedInstallAncestorAcl(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsTrustedInstallerPrincipal(owner))
        {
            throw new UnauthorizedAccessException(
                $"安裝位置的既有父目錄不是由系統管理員保護：{path}");
        }

        const FileSystemRights dangerous =
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) ||
                (rule.FileSystemRights & dangerous) == 0 ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                IsTrustedInstallerPrincipal(sid))
            {
                continue;
            }

            throw new UnauthorizedAccessException(
                $"安裝位置的既有父目錄允許非管理員刪除或改寫權限：{path}");
        }
    }

    private static bool IsTrustedInstallerPrincipal(SecurityIdentifier sid)
        => sid.Equals(LocalSystemSid) ||
           sid.Equals(AdministratorsSid) ||
           sid.Equals(TrustedInstallerSid.Value);

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            InstallerLayout.RejectExistingReparsePoints(path);
            Directory.Delete(path, recursive: false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }

    private static void DeleteOwnedDirectoryContents(string path)
    {
        InstallerLayout.RejectExistingReparsePoints(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    Directory.Delete(entry, recursive: false);
                }
                else
                {
                    File.Delete(entry);
                }
                continue;
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                DeleteOwnedDirectoryContents(entry);
                Directory.Delete(entry, recursive: false);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    private static void DeleteEmptyDirectoryByHandle(SafeFileHandle handle, string path)
    {
        ValidateLockedDirectoryHandle(handle, path);
        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new IOException($"無法刪除仍含資料的安裝目錄：{path}");
        }

        var disposition = new FileDispositionInfo { DeleteFile = true };
        if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfoClass,
                ref disposition,
                checked((uint)Marshal.SizeOf<FileDispositionInfo>())))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"無法刪除安裝目錄：{path}");
        }
    }

    private static void SetTraverseContainer(
        string path,
        IReadOnlyList<SecurityIdentifier> principals)
    {
        var grants = new List<InstallerAclGrant>
        {
            DirectoryGrant(LocalSystemSid, FileSystemRights.FullControl),
            DirectoryGrant(AdministratorsSid, FileSystemRights.FullControl),
        };
        grants.AddRange(principals.Select(
            sid => new InstallerAclGrant(sid, FileSystemRights.ReadAndExecute)));
        SetExactDirectoryAcl(path, grants);
    }

    private static void SetExactDirectoryAcl(string path, IReadOnlyList<InstallerAclGrant> grants)
    {
        InstallerLayout.RejectExistingReparsePoints(path);
        new DirectoryInfo(path).SetAccessControl(CreateExactDirectorySecurity(grants));
    }

    private static void SetExactFileAcl(string path, IReadOnlyList<InstallerAclGrant> grants)
    {
        InstallerLayout.RejectExistingReparsePoints(path);
        new FileInfo(path).SetAccessControl(CreateExactFileSecurity(grants));
    }

    private static DirectorySecurity CreateExactDirectorySecurity(
        IReadOnlyList<InstallerAclGrant> grants)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        foreach (var grant in grants)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                grant.Sid,
                grant.Rights,
                grant.InheritanceFlags,
                grant.PropagationFlags,
                AccessControlType.Allow));
        }
        return security;
    }

    private static FileSecurity CreateExactFileSecurity(IReadOnlyList<InstallerAclGrant> grants)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(AdministratorsSid);
        foreach (var grant in grants)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                grant.Sid,
                grant.Rights,
                AccessControlType.Allow));
        }
        return security;
    }

    private static void ValidateExactDirectoryAcl(
        string path,
        IReadOnlyList<InstallerAclGrant> grants)
    {
        var actual = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        var expected = CreateExactDirectorySecurity(grants);
        if (!InstallerSecurityDescriptorComparer.EqualsAllowingDaclAutoInherited(
                actual.GetSecurityDescriptorSddlForm(
                    AccessControlSections.Access | AccessControlSections.Owner),
                expected.GetSecurityDescriptorSddlForm(
                    AccessControlSections.Access | AccessControlSections.Owner)))
        {
            throw new UnauthorizedAccessException(
                $"既有 X MCSV 目錄不是受信任且受保護的產品 ACL：{path}");
        }
    }

    private static void ValidateExactFileAcl(
        string path,
        IReadOnlyList<InstallerAclGrant> grants)
    {
        var actual = new FileInfo(path).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        var expected = CreateExactFileSecurity(grants);
        if (!InstallerSecurityDescriptorComparer.EqualsAllowingDaclAutoInherited(
                actual.GetSecurityDescriptorSddlForm(
                    AccessControlSections.Access | AccessControlSections.Owner),
                expected.GetSecurityDescriptorSddlForm(
                    AccessControlSections.Access | AccessControlSections.Owner)))
        {
            throw new UnauthorizedAccessException(
                $"既有 X MCSV 檔案不是受信任且受保護的產品 ACL：{path}");
        }
    }

    private static SecurityIdentifier ResolveServiceSid(string name)
        => (SecurityIdentifier)new NTAccount("NT SERVICE", name)
            .Translate(typeof(SecurityIdentifier));

    private static bool IsStopped(string queryOutput)
        => queryOutput.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ||
           queryOutput.Contains("STATE              : 1", StringComparison.OrdinalIgnoreCase);

    private static async Task<(int ExitCode, string Output)> RunScAsync(
        IReadOnlyList<string> arguments,
        bool allowMissing,
        CancellationToken cancellationToken)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var executable = Path.Combine(windows, "System32", "sc.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("無法啟動 Windows Service Controller。");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        if (output.Length > 64 * 1024)
        {
            throw new InvalidDataException("Windows Service Controller 回覆過大。");
        }

        if (process.ExitCode != 0 && !(allowMissing && output.Contains("1060", StringComparison.Ordinal)))
        {
            throw new Win32Exception(process.ExitCode, "Windows Service 設定失敗。");
        }

        return (process.ExitCode, output);
    }

    private static async Task WaitForServiceStateAsync(
        string name,
        string expected,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(45))
        {
            var state = await RunScAsync(["query", name], allowMissing: false, cancellationToken)
                .ConfigureAwait(false);
            if (state.Output.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Windows Service 未進入 {expected} 狀態。");
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateDirectoryW",
        CharSet = CharSet.Unicode,
        SetLastError = true,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryWithSecurityW(
        string path,
        ref SecurityAttributes securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileStandardInformationByHandle(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileStandardInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle fileHandle,
        [Out] char[] filePath,
        uint filePathLength,
        uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);
}

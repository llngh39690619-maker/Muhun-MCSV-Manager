using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Runtime;

/// <summary>Launch and verified catalog fields produced by a committed modpack update.</summary>
public sealed record ModpackUpdateLaunchFields(
    string LiveDirectoryPath,
    string ServerJarPath,
    ServerLaunchKind LaunchKind,
    IReadOnlyList<string> JavaArgumentFilePaths,
    string? SourceLaunchScriptPath,
    CoreType CoreType,
    string? MinecraftVersion,
    int? JavaMajorVersion,
    string? JavaExecutablePath,
    IReadOnlyList<string> ServerArguments,
    ModpackSourceKind ModpackSource,
    string? ModpackProjectId,
    string? ModpackVersionId,
    string? ModpackVersionName,
    bool IsInstallerArtifact)
{
    /// <summary>
    /// Applies only installer-owned launch/provenance fields. The live instance identity, name,
    /// memory policy, port, watchdog, appearance and other user preferences remain unchanged.
    /// </summary>
    public void ApplyTo(ServerInstance liveInstance)
    {
        ArgumentNullException.ThrowIfNull(liveInstance);
        ArgumentException.ThrowIfNullOrWhiteSpace(liveInstance.DirectoryPath);
        if (!PathsEqual(liveInstance.DirectoryPath, LiveDirectoryPath))
        {
            throw new InvalidOperationException(
                "更新結果不屬於這個 Server 資料夾，拒絕合併啟動欄位。");
        }

        liveInstance.ServerJarPath = ServerJarPath;
        liveInstance.LaunchKind = LaunchKind;
        liveInstance.JavaArgumentFilePaths = [.. JavaArgumentFilePaths];
        liveInstance.SourceLaunchScriptPath = SourceLaunchScriptPath;
        liveInstance.CoreType = CoreType;
        liveInstance.MinecraftVersion = MinecraftVersion;
        liveInstance.JavaMajorVersion = JavaMajorVersion;
        liveInstance.JavaExecutablePath = JavaExecutablePath;
        liveInstance.ServerArguments = [.. ServerArguments];
        liveInstance.ModpackSource = ModpackSource;
        liveInstance.ModpackProjectId = ModpackProjectId;
        liveInstance.ModpackVersionId = ModpackVersionId;
        liveInstance.ModpackVersionName = ModpackVersionName;
        liveInstance.IsInstallerArtifact = IsInstallerArtifact;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public sealed record ModpackUpdateTransactionResult(
    Guid TransactionId,
    ModpackUpdateLaunchFields LaunchFields,
    ModpackUpdateLaunchFields PreviousLaunchFields,
    bool CleanupPending);

public enum ModpackUpdateRecoveryAction
{
    None,
    RolledBack,
    CommittedAwaitingAcknowledgement,
}

public sealed record ModpackUpdateRecoveryResult(
    ModpackUpdateRecoveryAction Action,
    Guid? TransactionId = null,
    ModpackUpdateLaunchFields? LaunchFields = null,
    ModpackUpdateLaunchFields? PreviousLaunchFields = null,
    bool CleanupPending = false);

/// <summary>
/// The original update failed and the automatic rollback also could not be completed. The durable
/// journal remains in place so startup recovery can retry without guessing which files moved.
/// </summary>
public sealed class ModpackUpdateRollbackException : IOException
{
    internal ModpackUpdateRollbackException(
        string journalPath,
        Exception updateError,
        Exception rollbackError)
        : base(
            "模組包更新失敗，且自動復原尚未完成；請勿手動移動 Server 檔案，"
            + "重新啟動管理器以依 journal 重試復原。",
            new AggregateException(updateError, rollbackError))
    {
        JournalPath = journalPath;
        UpdateError = updateError;
        RollbackError = rollbackError;
    }

    public string JournalPath { get; }

    public Exception UpdateError { get; }

    public Exception RollbackError { get; }
}

internal enum ModpackUpdateFaultPoint
{
    JournalPrepared,
    LiveEntryMoved,
    CandidateEntryMoved,
    CommitMarked,
}

/// <summary>Test-only crash boundary: unlike an ordinary fault it intentionally bypasses rollback.</summary>
internal sealed class ModpackUpdateSimulatedCrashException(string message) : IOException(message);

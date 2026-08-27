using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftServerManager.Updater;

public interface IProductUpdateHealthController
{
    Task LaunchAsync(string executablePath, CancellationToken cancellationToken);

    Task<bool> WaitForHealthyAsync(string version, TimeSpan timeout, CancellationToken cancellationToken);
}

public enum ProductUpdateActivationState
{
    Activating,
    HealthChecking,
    Committed,
    RollingBack,
    RolledBack,
    RecoveryFailed,
}

public sealed record ProductUpdateActivationJournal(
    int SchemaVersion,
    Guid OperationId,
    string PreviousVersion,
    string TargetVersion,
    ProductUpdateActivationState State,
    DateTimeOffset UpdatedAtUtc,
    string? FailureCode = null);

public sealed record ProductUpdateActivationResult(
    Guid OperationId,
    string ActiveVersion,
    bool RolledBack);

public enum ProductUpdateActivationCheckpoint
{
    JournalPersisted,
    PointerSwitched,
    HealthCheckingJournalPersisted,
    TerminalJournalPersisted,
}

internal sealed class ProductUpdateInterruptionException(string checkpoint)
    : Exception($"Simulated updater interruption at {checkpoint}.");

public sealed class ProductUpdateActivator
{
    public const string ActivationStateDirectoryName = "activation-state";
    private const string ActivePointerFileName = "active-version.v1";
    private const string JournalFileName = "activation-journal.v1.json";
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly string _updatesRoot;
    private readonly string _versionsRoot;
    private readonly string _activationRoot;
    private readonly IProductUpdateHealthController _healthController;
    private readonly TimeProvider _timeProvider;
    private readonly Action<ProductUpdateActivationCheckpoint>? _checkpointObserver;

    public ProductUpdateActivator(
        string updatesRoot,
        IProductUpdateHealthController healthController,
        TimeProvider? timeProvider = null,
        Action<ProductUpdateActivationCheckpoint>? checkpointObserver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(updatesRoot);
        _updatesRoot = Path.GetFullPath(updatesRoot);
        _versionsRoot = Path.Combine(_updatesRoot, "versions");
        _activationRoot = Path.Combine(_updatesRoot, ActivationStateDirectoryName);
        _healthController = healthController ?? throw new ArgumentNullException(nameof(healthController));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _checkpointObserver = checkpointObserver;
    }

    public async Task<ProductUpdateActivationResult> ActivateAsync(
        ProductUpdateManifest targetManifest,
        TimeSpan healthTimeout,
        CancellationToken cancellationToken = default,
        Guid? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(targetManifest);
        if (healthTimeout < TimeSpan.FromSeconds(5) || healthTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(healthTimeout));
        }

        Directory.CreateDirectory(_updatesRoot);
        Directory.CreateDirectory(_versionsRoot);
        EnsureActivationRoot();
        await using var updateLock = new FileStream(
            Path.Combine(_activationRoot, ".activation.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);

        var previousVersion = ReadActiveVersion();
        ValidateVersion(targetManifest.Version);
        var durableOperationId = operationId ?? Guid.NewGuid();
        if (durableOperationId == Guid.Empty)
        {
            throw new ArgumentException("Activation operation id must not be empty.", nameof(operationId));
        }

        var existingJournal = ReadJournal();
        if (existingJournal is not null &&
            existingJournal.State is not (ProductUpdateActivationState.Committed or
                ProductUpdateActivationState.RolledBack))
        {
            throw new InvalidOperationException(
                "A non-terminal activation must be recovered before a new switch.");
        }

        if (existingJournal?.OperationId == durableOperationId)
        {
            if (!string.Equals(existingJournal.TargetVersion, targetManifest.Version, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Activation operation target binding changed.");
            }

            return new ProductUpdateActivationResult(
                durableOperationId,
                existingJournal.State == ProductUpdateActivationState.Committed
                    ? existingJournal.TargetVersion
                    : existingJournal.PreviousVersion,
                RolledBack: existingJournal.State == ProductUpdateActivationState.RolledBack);
        }

        if (string.Equals(previousVersion, targetManifest.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Target version is already active.");
        }

        var previousInstallation = ReadInstalledVersion(previousVersion);
        var targetInstallation = ReadInstalledVersion(targetManifest.Version);
        if (!string.Equals(targetInstallation.EntryPoint, targetManifest.EntryPoint, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Target entry point does not match its installed-version metadata.");
        }

        await ProductInstalledVersionVerifier.VerifyAsync(
                Path.Combine(_versionsRoot, targetManifest.Version),
                targetManifest,
                cancellationToken)
            .ConfigureAwait(false);

        var targetEntryPoint = ResolveInstalledEntryPoint(targetInstallation);
        var journal = new ProductUpdateActivationJournal(
            1,
            durableOperationId,
            previousVersion,
            targetManifest.Version,
            ProductUpdateActivationState.Activating,
            _timeProvider.GetUtcNow());
        WriteJournal(journal);
        _checkpointObserver?.Invoke(ProductUpdateActivationCheckpoint.JournalPersisted);

        try
        {
            WriteActiveVersion(targetManifest.Version);
            _checkpointObserver?.Invoke(ProductUpdateActivationCheckpoint.PointerSwitched);
            journal = journal with
            {
                State = ProductUpdateActivationState.HealthChecking,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
            };
            WriteJournal(journal);
            _checkpointObserver?.Invoke(ProductUpdateActivationCheckpoint.HealthCheckingJournalPersisted);
            await _healthController.LaunchAsync(targetEntryPoint, cancellationToken).ConfigureAwait(false);
            if (!await _healthController
                    .WaitForHealthyAsync(targetManifest.Version, healthTimeout, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException("Target version failed its health handshake.");
            }

            WriteJournal(journal with
            {
                State = ProductUpdateActivationState.Committed,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
            });
            _checkpointObserver?.Invoke(ProductUpdateActivationCheckpoint.TerminalJournalPersisted);
            return new ProductUpdateActivationResult(durableOperationId, targetManifest.Version, RolledBack: false);
        }
        catch (Exception activationFailure) when (activationFailure is not ProductUpdateInterruptionException)
        {
            return await RollBackAsync(
                journal,
                previousInstallation,
                healthTimeout,
                activationFailure,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<ProductUpdateActivationResult?> RecoverInterruptedActivationAsync(
        TimeSpan healthTimeout,
        CancellationToken cancellationToken = default)
    {
        if (healthTimeout < TimeSpan.FromSeconds(5) || healthTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(healthTimeout));
        }

        Directory.CreateDirectory(_updatesRoot);
        Directory.CreateDirectory(_versionsRoot);
        EnsureActivationRoot();
        await using var updateLock = new FileStream(
            Path.Combine(_activationRoot, ".activation.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
        var journal = ReadJournal();
        if (journal is null ||
            journal.State is ProductUpdateActivationState.Committed or ProductUpdateActivationState.RolledBack)
        {
            return null;
        }

        var previousInstallation = ReadInstalledVersion(journal.PreviousVersion);
        var previousEntryPoint = ResolveInstalledEntryPoint(previousInstallation);
        var recoveryJournal = journal with
        {
            State = ProductUpdateActivationState.RollingBack,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
            FailureCode = "activation.interrupted",
        };
        _ = TryWriteJournal(recoveryJournal);
        try
        {
            WriteActiveVersion(previousInstallation.Version);
            await _healthController.LaunchAsync(previousEntryPoint, cancellationToken).ConfigureAwait(false);
            if (!await _healthController
                    .WaitForHealthyAsync(previousInstallation.Version, healthTimeout, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException("Previous version failed interrupted-activation recovery.");
            }

            _ = TryWriteJournal(recoveryJournal with
            {
                State = ProductUpdateActivationState.RolledBack,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
            });
            return new ProductUpdateActivationResult(
                journal.OperationId,
                previousInstallation.Version,
                RolledBack: true);
        }
        catch (Exception exception)
        {
            _ = TryWriteJournal(recoveryJournal with
            {
                State = ProductUpdateActivationState.RecoveryFailed,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                FailureCode = "interrupted.rollback_failed",
            });
            throw new InvalidOperationException(
                "Interrupted product activation could not be rolled back.",
                exception);
        }
    }

    public ProductUpdateActivationJournal? ReadActivationJournal()
    {
        Directory.CreateDirectory(_updatesRoot);
        EnsureActivationRoot();
        return ReadJournal();
    }

    public static ProductUpdateActivationJournal? ReadActivationJournal(string installRoot)
    {
        var root = NormalizeInstallRoot(installRoot);
        var activationRoot = Path.Combine(root, ActivationStateDirectoryName);
        if (!Directory.Exists(activationRoot))
        {
            return null;
        }

        RejectExistingReparsePoints(activationRoot);
        return ReadJournalFile(Path.Combine(activationRoot, JournalFileName));
    }

    /// <summary>
    /// Serializes the complete updater workflow, including provisioning before the narrower
    /// pointer-switch lock is acquired. A restarted Service may safely launch another updater;
    /// only one process can consume or mutate a given activation at a time.
    /// </summary>
    public IDisposable AcquireUpdaterLease()
    {
        Directory.CreateDirectory(_updatesRoot);
        EnsureActivationRoot();
        return new FileStream(
            Path.Combine(_activationRoot, ".updater.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
    }

    public string ReadActiveVersion()
        => ReadActiveVersion(_updatesRoot);

    public static string ReadActiveVersion(string installRoot)
    {
        var root = NormalizeInstallRoot(installRoot);
        RejectExistingReparsePoints(root);
        var path = Path.Combine(root, ActivePointerFileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("No active product version has been provisioned.");
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Active product version pointer cannot be a reparse point.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128);
        if (stream.Length is < 1 or > 128)
        {
            throw new InvalidDataException("Active product version pointer has an invalid size.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        stream.ReadExactly(bytes);
        var text = Encoding.ASCII.GetString(bytes).Trim();
        ValidateVersion(text);
        return text;
    }

    private async Task<ProductUpdateActivationResult> RollBackAsync(
        ProductUpdateActivationJournal journal,
        ProductInstalledVersionMetadata previousInstallation,
        TimeSpan healthTimeout,
        Exception activationFailure,
        CancellationToken cancellationToken)
    {
        _ = TryWriteJournal(journal with
        {
            State = ProductUpdateActivationState.RollingBack,
            UpdatedAtUtc = _timeProvider.GetUtcNow(),
            FailureCode = "target.health_failed",
        });

        try
        {
            var previousEntryPoint = ResolveInstalledEntryPoint(previousInstallation);
            WriteActiveVersion(previousInstallation.Version);
            await _healthController.LaunchAsync(previousEntryPoint, cancellationToken).ConfigureAwait(false);
            if (!await _healthController
                    .WaitForHealthyAsync(previousInstallation.Version, healthTimeout, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException("Previous version failed recovery health check.");
            }

            _ = TryWriteJournal(journal with
            {
                State = ProductUpdateActivationState.RolledBack,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                FailureCode = "target.health_failed",
            });
            return new ProductUpdateActivationResult(journal.OperationId, previousInstallation.Version, RolledBack: true);
        }
        catch (Exception recoveryFailure)
        {
            _ = TryWriteJournal(journal with
            {
                State = ProductUpdateActivationState.RecoveryFailed,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                FailureCode = "rollback.health_failed",
            });
            throw new AggregateException(
                "Product activation and automatic rollback both failed.",
                activationFailure,
                recoveryFailure);
        }
    }

    private ProductInstalledVersionMetadata ReadInstalledVersion(string version)
    {
        ValidateVersion(version);
        var versionRoot = Path.Combine(_versionsRoot, version);
        var metadata = ProductInstalledVersionMetadataStore.Read(versionRoot);
        if (!string.Equals(metadata.Version, version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Installed-version metadata does not match its directory.");
        }

        return metadata;
    }

    private string ResolveInstalledEntryPoint(ProductInstalledVersionMetadata installation)
    {
        var versionRoot = Path.Combine(_versionsRoot, installation.Version);
        return ProductUpdatePath.ResolveUnderRoot(versionRoot, installation.EntryPoint);
    }

    private void WriteActiveVersion(string version)
    {
        ValidateVersion(version);
        WriteAtomic(
            Path.Combine(_updatesRoot, ActivePointerFileName),
            Encoding.ASCII.GetBytes(version + Environment.NewLine),
            _activationRoot);
    }

    private void WriteJournal(ProductUpdateActivationJournal journal)
        => WriteAtomic(
            Path.Combine(_activationRoot, JournalFileName),
            Utf8NoBom.GetBytes(JsonSerializer.Serialize(journal, JsonOptions) + Environment.NewLine));

    private bool TryWriteJournal(ProductUpdateActivationJournal journal)
    {
        try
        {
            WriteJournal(journal);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Journal I/O must never prevent restoration of the previous A/B pointer. A stale
            // non-terminal journal causes the next updater run to repeat the same safe rollback.
            return false;
        }
    }

    private ProductUpdateActivationJournal? ReadJournal()
        => ReadJournalFile(Path.Combine(_activationRoot, JournalFileName));

    private static ProductUpdateActivationJournal? ReadJournalFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Product activation journal cannot be a reparse point.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4_096);
        if (stream.Length is < 1 or > 16 * 1024)
        {
            throw new InvalidDataException("Product activation journal has an invalid size.");
        }

        ProductUpdateActivationJournal journal;
        try
        {
            journal = JsonSerializer.Deserialize<ProductUpdateActivationJournal>(stream, JsonOptions)
                ?? throw new InvalidDataException("Product activation journal is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Product activation journal JSON is invalid.", exception);
        }

        if (journal.SchemaVersion != 1 || journal.OperationId == Guid.Empty ||
            !Enum.IsDefined(journal.State) || journal.UpdatedAtUtc.Offset != TimeSpan.Zero ||
            string.Equals(journal.PreviousVersion, journal.TargetVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Product activation journal is invalid or unsupported.");
        }

        ValidateVersion(journal.PreviousVersion);
        ValidateVersion(journal.TargetVersion);
        return journal;
    }

    private static void WriteAtomic(string path, byte[] bytes, string? temporaryDirectory = null)
    {
        if (Directory.Exists(path) ||
            (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
        {
            throw new IOException("Product activation state path is not a regular file.");
        }

        var temporaryPath = Path.Combine(
            temporaryDirectory ?? Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4_096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void ValidateVersion(string version)
        => ProductUpdateManifestParser.ValidateVersion(version);

    private static string NormalizeInstallRoot(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Path.IsPathFullyQualified(installRoot))
        {
            throw new InvalidDataException("Product install root must be an absolute path.");
        }

        return Path.GetFullPath(installRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static void RejectExistingReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Product activation paths must not traverse a reparse point.");
            }
        }
    }

    private void EnsureActivationRoot()
    {
        Directory.CreateDirectory(_activationRoot);
        if ((File.GetAttributes(_activationRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Product activation state directory cannot be a reparse point.");
        }
    }
}

namespace MinecraftServerManager.GameClient;

/// <summary>
/// Indicates that an FTB install crossed a durable transaction boundary whose ownership outcome
/// could not be proven. The retained receipt must be reconciled before another install continues.
/// </summary>
public sealed class FtbClientInstallRecoveryRequiredException : IOException
{
    internal FtbClientInstallRecoveryRequiredException(
        string stage,
        IEnumerable<Exception> failures)
        : this(NormalizeStage(stage), MaterializeFailures(failures))
    {
    }

    private FtbClientInstallRecoveryRequiredException(
        string stage,
        Exception[] failures)
        : base(
            "The FTB client installation requires durable recovery before it can continue.",
            new AggregateException(
                "One or more FTB transaction operations could not be proven complete.",
                failures))
    {
        Stage = stage;
        FailureCount = failures.Length;
    }

    public bool RecoveryRequired => true;

    public bool RollbackCompleted => false;

    /// <summary>A safe machine-readable token; it never contains a path or URI.</summary>
    public string Stage { get; }

    public int FailureCount { get; }

    private static Exception[] MaterializeFailures(IEnumerable<Exception> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        var materialized = failures.ToArray();
        if (materialized.Length == 0 || materialized.Any(static failure => failure is null))
        {
            throw new ArgumentException("At least one transaction failure is required.", nameof(failures));
        }

        foreach (var failure in materialized)
        {
            ExceptionGraphSafety.RethrowOutOfMemory(failure);
        }

        return materialized;
    }

    private static string NormalizeStage(string? stage) =>
        !string.IsNullOrWhiteSpace(stage) &&
        stage.Length <= 64 &&
        stage.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? stage
            : "unknown";
}

/// <summary>
/// Indicates that an FTB install failed and at least one owned tree or durable receipt could not
/// be completely rolled back. Recovery remains required and the receipt is intentionally kept.
/// </summary>
public sealed class FtbClientInstallRollbackIncompleteException : IOException
{
    internal FtbClientInstallRollbackIncompleteException(
        string stage,
        IEnumerable<Exception> failures)
        : this(NormalizeStage(stage), MaterializeFailures(failures))
    {
    }

    private FtbClientInstallRollbackIncompleteException(
        string stage,
        Exception[] failures)
        : base(
            "The FTB client installation failed and its durable rollback is incomplete.",
            new AggregateException(
                "One or more FTB rollback operations did not complete.",
                failures))
    {
        Stage = stage;
        FailureCount = failures.Length;
    }

    public bool RecoveryRequired => true;

    public bool RollbackCompleted => false;

    /// <summary>A safe machine-readable token; it never contains a path or URI.</summary>
    public string Stage { get; }

    public int FailureCount { get; }

    private static Exception[] MaterializeFailures(IEnumerable<Exception> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        var materialized = failures.ToArray();
        if (materialized.Length == 0 || materialized.Any(static failure => failure is null))
        {
            throw new ArgumentException("At least one rollback failure is required.", nameof(failures));
        }

        foreach (var failure in materialized)
        {
            ExceptionGraphSafety.RethrowOutOfMemory(failure);
        }

        return materialized;
    }

    private static string NormalizeStage(string? stage) =>
        !string.IsNullOrWhiteSpace(stage) &&
        stage.Length <= 64 &&
        stage.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? stage
            : "unknown";
}

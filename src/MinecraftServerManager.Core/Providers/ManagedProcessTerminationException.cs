namespace MinecraftServerManager.Core.Providers;

public sealed class ManagedProcessTerminationException : Exception
{
    public ManagedProcessTerminationException(
        string processDisplayName,
        TimeSpan confirmationTimeout,
        int killAttempts,
        Exception? innerException = null)
        : base(BuildMessage(processDisplayName, confirmationTimeout, killAttempts), innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDisplayName);
        if (confirmationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(confirmationTimeout));
        }

        if (killAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(killAttempts));
        }

        ProcessDisplayName = processDisplayName;
        ConfirmationTimeout = confirmationTimeout;
        KillAttempts = killAttempts;
    }

    public string ProcessDisplayName { get; }

    public TimeSpan ConfirmationTimeout { get; }

    public int KillAttempts { get; }

    private static string BuildMessage(
        string processDisplayName,
        TimeSpan confirmationTimeout,
        int killAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDisplayName);
        if (confirmationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(confirmationTimeout));
        }

        if (killAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(killAttempts));
        }

        return $"已嘗試終止 {processDisplayName} {killAttempts} 次，"
            + $"但每次等待 {confirmationTimeout.TotalSeconds:0.###} 秒後仍無法確認程序已退出。"
            + " 為避免將仍在使用中的暫存檔誤報為已清理，本次作業已標記為失敗。";
    }
}

internal static class ManagedProcessTermination
{
    private const int MaximumKillAttempts = 2;

    public static async Task EnsureExitedAfterCancellationAsync(
        string processDisplayName,
        Func<bool> hasExited,
        Action killProcessTree,
        Func<CancellationToken, Task> waitForExitAsync,
        TimeSpan confirmationTimeout,
        OperationCanceledException cancellationException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processDisplayName);
        ArgumentNullException.ThrowIfNull(hasExited);
        ArgumentNullException.ThrowIfNull(killProcessTree);
        ArgumentNullException.ThrowIfNull(waitForExitAsync);
        ArgumentNullException.ThrowIfNull(cancellationException);

        var terminationFailures = new List<Exception>();
        for (var attempt = 1; attempt <= MaximumKillAttempts; attempt++)
        {
            if (hasExited())
            {
                return;
            }

            try
            {
                killProcessTree();
            }
            catch (InvalidOperationException) when (hasExited())
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                terminationFailures.Add(exception);
            }

            if (hasExited())
            {
                return;
            }

            try
            {
                using var confirmation = new CancellationTokenSource(confirmationTimeout);
                await waitForExitAsync(confirmation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                terminationFailures.Add(new TimeoutException(
                    $"等待 {processDisplayName} 終止超過 {confirmationTimeout.TotalSeconds:0.###} 秒。",
                    exception));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                terminationFailures.Add(exception);
            }

            if (hasExited())
            {
                return;
            }
        }

        var causes = new Exception[terminationFailures.Count + 1];
        causes[0] = cancellationException;
        terminationFailures.CopyTo(causes, 1);
        throw new ManagedProcessTerminationException(
            processDisplayName,
            confirmationTimeout,
            MaximumKillAttempts,
            new AggregateException("取消後無法確認外部程序已終止。", causes));
    }
}

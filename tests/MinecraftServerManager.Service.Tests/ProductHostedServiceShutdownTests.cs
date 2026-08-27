using System.Diagnostics;
using MinecraftServerManager.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductHostedServiceShutdownTests
{
    [Fact]
    public async Task CancelledHostDeadline_AbortsWorkerWithoutRethrowingCancellation()
    {
        using var abort = new CancellationTokenSource();
        using var hostDeadline = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = Task.Run(async () =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, abort.Token);
        });
        await entered.Task;
        hostDeadline.Cancel();

        await ProductHostedServiceShutdown.DrainWorkerAsync(
            worker,
            abort,
            hostDeadline.Token,
            NullLogger.Instance,
            "test worker",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        Assert.True(abort.IsCancellationRequested);
        Assert.True(worker.IsCompleted);
    }

    [Fact]
    public async Task CancellationIgnoringWorker_CannotHoldShutdownPastBothBoundedWindows()
    {
        using var abort = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        await ProductHostedServiceShutdown.DrainWorkerAsync(
            release.Task,
            abort,
            CancellationToken.None,
            NullLogger.Instance,
            "test worker",
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromMilliseconds(25));
        stopwatch.Stop();

        Assert.True(abort.IsCancellationRequested);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        release.TrySetResult();
        await release.Task;
    }

    [Fact]
    public void NotificationShutdownBudgets_AreExplicitlyBounded()
    {
        Assert.InRange(
            ProductHostedServiceShutdown.NotificationDrainTimeout,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(10));
        Assert.InRange(
            ProductNotificationDispatchHostedService.FinalDispatchTimeout,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(10));
    }
}

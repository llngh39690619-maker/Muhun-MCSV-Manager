using System.Collections.Concurrent;
using System.IO;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class BackgroundServerJobCoordinatorTests
{
    [Fact]
    public async Task CanonicallyEquivalentActiveName_IsRejectedBeforeSecondExecution()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = CreateCoordinator(maximumConcurrentJobs: 2);
        var definition = CreateDefinition("Test Server", async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return CreateServer("Test Server");
        });

        Assert.True(coordinator.TryEnqueue(definition, out var first, out var firstError));
        Assert.NotNull(first);
        Assert.Null(firstError);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var canonicalDuplicate = definition with { ServerName = "Ｔｅｓｔ Ｓｅｒｖｅｒ" };
        Assert.False(coordinator.TryEnqueue(canonicalDuplicate, out var duplicate, out var duplicateError));
        Assert.Null(duplicate);
        Assert.Contains("已經在", duplicateError);

        var sameFilesystemTarget = definition with { ServerName = "Test Server?" };
        var targetCollision = definition with { ServerName = "Test Server*" };
        Assert.True(coordinator.TryEnqueue(sameFilesystemTarget, out var pathFirst, out _));
        Assert.False(coordinator.TryEnqueue(targetCollision, out _, out var pathCollisionError));
        Assert.Contains("已經在", pathCollisionError);

        release.TrySetResult();
        await WaitUntilAsync(() => first!.State == BackgroundServerJobState.Completed);
        await WaitUntilAsync(() => pathFirst!.State == BackgroundServerJobState.Completed);
    }

    [Fact]
    public async Task FixedWorkerPool_BoundsGeneralConcurrencyWithoutBoundingQueueLength()
    {
        const int jobCount = 12;
        var active = 0;
        var maximumObserved = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = CreateCoordinator(maximumConcurrentJobs: 3);

        for (var index = 0; index < jobCount; index++)
        {
            var name = $"Server {index}";
            Assert.True(coordinator.TryEnqueue(
                CreateDefinition(name, async (_, cancellationToken) =>
                {
                    var nowActive = Interlocked.Increment(ref active);
                    UpdateMaximum(ref maximumObserved, nowActive);
                    try
                    {
                        await release.Task.WaitAsync(cancellationToken);
                        return CreateServer(name);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                    }
                }),
                out _,
                out _));
        }

        await WaitUntilAsync(() =>
            Volatile.Read(ref active) == 3 &&
            coordinator.Jobs.Count(job => job.State == BackgroundServerJobState.Running) == 3);
        Assert.Equal(jobCount, coordinator.Jobs.Count);
        Assert.Equal(3, maximumObserved);
        Assert.Equal(3, coordinator.Jobs.Count(job => job.State == BackgroundServerJobState.Running));
        Assert.Equal(jobCount - 3, coordinator.Jobs.Count(job => job.State == BackgroundServerJobState.Queued));

        release.TrySetResult();
        await WaitUntilAsync(() => coordinator.Jobs.All(job => job.State == BackgroundServerJobState.Completed));
        Assert.Equal(3, maximumObserved);
    }

    [Fact]
    public async Task DifferentDisplayNames_WithSamePredictedTarget_AreNotRunConcurrently()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = Path.Combine(Path.GetTempPath(), "same-target");
        await using var coordinator = new BackgroundServerJobCoordinator(
            (_, _) => Task.CompletedTask,
            resolveTargetIdentity: _ => target,
            maximumConcurrentJobs: 2,
            maximumConcurrentBuildToolsJobs: 1,
            marshalToApplicationDispatcher: false);

        Assert.True(coordinator.TryEnqueue(
            CreateDefinition("Original", async (_, cancellationToken) =>
            {
                await release.Task.WaitAsync(cancellationToken);
                return CreateServer("Original");
            }),
            out var first,
            out _));
        Assert.False(coordinator.TryEnqueue(
            CreateDefinition("Different", (_, _) => Task.FromResult(CreateServer("Different"))),
            out _,
            out var error));
        Assert.Contains("預定 Server 資料夾", error);

        release.TrySetResult();
        await WaitUntilAsync(() => first!.State == BackgroundServerJobState.Completed);
    }

    [Fact]
    public async Task TargetResolutionFailure_DoesNotPoisonNameReservation()
    {
        var shouldFail = true;
        await using var coordinator = new BackgroundServerJobCoordinator(
            (_, _) => Task.CompletedTask,
            resolveTargetIdentity: name => shouldFail
                ? throw new InvalidOperationException("target unavailable")
                : Path.Combine(Path.GetTempPath(), name),
            maximumConcurrentJobs: 2,
            maximumConcurrentBuildToolsJobs: 1,
            marshalToApplicationDispatcher: false);
        var definition = CreateDefinition(
            "Retry Target",
            (_, _) => Task.FromResult(CreateServer("Retry Target")));

        Assert.False(coordinator.TryEnqueue(definition, out var rejected, out var firstError));
        Assert.Null(rejected);
        Assert.Contains("無法保留預定 Server 資料夾", firstError);

        shouldFail = false;
        Assert.True(coordinator.TryEnqueue(definition, out var accepted, out var retryError));
        Assert.NotNull(accepted);
        Assert.Null(retryError);
        await WaitUntilAsync(() => accepted!.State == BackgroundServerJobState.Completed);
    }

    [Fact]
    public async Task BuildToolsResourceClass_UsesIndependentHeavyWorkLimit()
    {
        var activeBuildTools = 0;
        var maximumBuildTools = 0;
        var activeGeneral = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = CreateCoordinator(
            maximumConcurrentJobs: 4,
            maximumConcurrentBuildToolsJobs: 1);

        for (var index = 0; index < 3; index++)
        {
            var name = $"BuildTools {index}";
            var definition = CreateDefinition(name, async (_, cancellationToken) =>
            {
                var nowActive = Interlocked.Increment(ref activeBuildTools);
                UpdateMaximum(ref maximumBuildTools, nowActive);
                try
                {
                    await release.Task.WaitAsync(cancellationToken);
                    return CreateServer(name);
                }
                finally
                {
                    Interlocked.Decrement(ref activeBuildTools);
                }
            }) with { ResourceClass = BackgroundServerJobResourceClass.BuildTools };
            Assert.True(coordinator.TryEnqueue(definition, out _, out _));
        }

        Assert.True(coordinator.TryEnqueue(
            CreateDefinition("General", async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref activeGeneral);
                try
                {
                    await release.Task.WaitAsync(cancellationToken);
                    return CreateServer("General");
                }
                finally
                {
                    Interlocked.Decrement(ref activeGeneral);
                }
            }),
            out _,
            out _));

        await WaitUntilAsync(() => Volatile.Read(ref activeBuildTools) == 1
                                   && Volatile.Read(ref activeGeneral) == 1);
        Assert.Equal(1, maximumBuildTools);

        release.TrySetResult();
        await WaitUntilAsync(() => coordinator.Jobs.All(job => job.State == BackgroundServerJobState.Completed));
        Assert.Equal(1, maximumBuildTools);
    }

    [Fact]
    public async Task BuildToolsBacklog_DoesNotStarveLaterGeneralWork()
    {
        var releaseBuildTools = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var generalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = CreateCoordinator(
            maximumConcurrentJobs: 4,
            maximumConcurrentBuildToolsJobs: 1);

        for (var index = 0; index < 6; index++)
        {
            var name = $"Heavy {index}";
            var definition = CreateDefinition(name, async (_, cancellationToken) =>
            {
                await releaseBuildTools.Task.WaitAsync(cancellationToken);
                return CreateServer(name);
            }) with { ResourceClass = BackgroundServerJobResourceClass.BuildTools };
            Assert.True(coordinator.TryEnqueue(definition, out _, out _));
        }

        Assert.True(coordinator.TryEnqueue(
            CreateDefinition("General Behind Heavy", (_, _) =>
            {
                generalStarted.TrySetResult();
                return Task.FromResult(CreateServer("General Behind Heavy"));
            }),
            out var general,
            out _));

        await generalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => general!.State == BackgroundServerJobState.Completed);
        Assert.Equal(BackgroundServerJobState.Completed, general!.State);
        Assert.Contains(coordinator.Jobs, job =>
            job.ServerName.StartsWith("Heavy ", StringComparison.Ordinal)
            && job.IsActive);

        releaseBuildTools.TrySetResult();
        await WaitUntilAsync(() => coordinator.Jobs.All(job => job.State == BackgroundServerJobState.Completed));
    }

    [Fact]
    public async Task ProgressBurst_CoalescesTenThousandReportsIntoOnePendingUiDrain()
    {
        var pendingUiDrains = new ConcurrentQueue<Action>();
        var burstReported = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWorkflow = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new BackgroundServerJobCoordinator(
            (_, _) => Task.CompletedTask,
            maximumConcurrentJobs: 1,
            maximumConcurrentBuildToolsJobs: 1,
            marshalToApplicationDispatcher: false,
            postProgressToUi: pendingUiDrains.Enqueue);

        Assert.True(coordinator.TryEnqueue(
            CreateDefinition("Progress Burst", async (progress, cancellationToken) =>
            {
                for (var index = 0; index < 10_000; index++)
                {
                    progress.Report(new BackgroundServerJobProgress(
                        $"stage-{index}",
                        index / 100d,
                        $"detail-{index}"));
                }

                burstReported.TrySetResult();
                await releaseWorkflow.Task.WaitAsync(cancellationToken);
                return CreateServer("Progress Burst");
            }),
            out var job,
            out var error));
        Assert.Null(error);

        await burstReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(pendingUiDrains);
        Assert.True(pendingUiDrains.TryDequeue(out var drain));
        drain();
        Assert.Empty(pendingUiDrains);
        Assert.Equal("stage-9999", job!.StageText);
        Assert.Equal("detail-9999", job.DetailText);
        Assert.Equal(99.99d, job.ProgressPercentage, 2);

        releaseWorkflow.TrySetResult();
        await WaitUntilAsync(() => job.State == BackgroundServerJobState.Completed);
    }

    [Fact]
    public async Task GeneralAndBuildToolsQueues_NeverExceedSharedGlobalLimit()
    {
        const int globalLimit = 4;
        const int buildToolsLimit = 2;
        var activeTotal = 0;
        var maximumTotal = 0;
        var activeBuildTools = 0;
        var maximumBuildTools = 0;
        await using var coordinator = CreateCoordinator(
            maximumConcurrentJobs: globalLimit,
            maximumConcurrentBuildToolsJobs: buildToolsLimit);

        async Task<ServerInstance> ExecuteAsync(
            string name,
            bool isBuildTools,
            CancellationToken cancellationToken)
        {
            var total = Interlocked.Increment(ref activeTotal);
            UpdateMaximum(ref maximumTotal, total);
            if (isBuildTools)
            {
                var heavy = Interlocked.Increment(ref activeBuildTools);
                UpdateMaximum(ref maximumBuildTools, heavy);
            }

            try
            {
                await Task.Delay(75, cancellationToken);
                return CreateServer(name);
            }
            finally
            {
                if (isBuildTools)
                {
                    Interlocked.Decrement(ref activeBuildTools);
                }

                Interlocked.Decrement(ref activeTotal);
            }
        }

        for (var index = 0; index < 12; index++)
        {
            var isBuildTools = index % 2 == 0;
            var name = $"Mixed {index}";
            var definition = CreateDefinition(
                name,
                (_, cancellationToken) => ExecuteAsync(name, isBuildTools, cancellationToken))
                with
                {
                    ResourceClass = isBuildTools
                        ? BackgroundServerJobResourceClass.BuildTools
                        : BackgroundServerJobResourceClass.General
                };
            Assert.True(coordinator.TryEnqueue(definition, out _, out _));
        }

        await WaitUntilAsync(() => coordinator.Jobs.All(job => job.State == BackgroundServerJobState.Completed));
        Assert.InRange(maximumTotal, 1, globalLimit);
        Assert.InRange(maximumBuildTools, 1, buildToolsLimit);
    }

    [Fact]
    public async Task CancelAndWait_CancelsRunningAndQueuedJobsAndDrainsWorkers()
    {
        var cleanupObserved = 0;
        await using var coordinator = CreateCoordinator(maximumConcurrentJobs: 2);
        for (var index = 0; index < 6; index++)
        {
            var name = $"Cancel {index}";
            Assert.True(coordinator.TryEnqueue(
                CreateDefinition(name, async (_, cancellationToken) =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return CreateServer(name);
                    }
                    finally
                    {
                        Interlocked.Increment(ref cleanupObserved);
                    }
                }),
                out _,
                out _));
        }

        await WaitUntilAsync(() => coordinator.Jobs.Count(job => job.State == BackgroundServerJobState.Running) == 2);
        await coordinator.CancelAndWaitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(coordinator.Jobs, job => Assert.Equal(BackgroundServerJobState.Cancelled, job.State));
        Assert.Equal(2, cleanupObserved);
        Assert.False(coordinator.HasActiveJobs);
    }

    [Fact]
    public async Task SuccessfulWorkflowReturn_IsNonCancellableFinalizationBoundary()
    {
        var releaseReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commitCount = 0;
        BackgroundServerJobViewModel? submittedJob = null;
        await using var coordinator = new BackgroundServerJobCoordinator(
            (_, _) =>
            {
                Interlocked.Increment(ref commitCount);
                return Task.CompletedTask;
            },
            maximumConcurrentJobs: 1,
            maximumConcurrentBuildToolsJobs: 1,
            marshalToApplicationDispatcher: false);
        var definition = CreateDefinition("Late Cancel", async (_, cancellationToken) =>
        {
            await releaseReturn.Task.WaitAsync(cancellationToken);
            submittedJob!.RequestCancellation();
            return CreateServer("Late Cancel");
        });

        Assert.True(coordinator.TryEnqueue(definition, out submittedJob, out var error));
        Assert.Null(error);
        releaseReturn.TrySetResult();

        await WaitUntilAsync(() => submittedJob!.IsFinished);
        Assert.Equal(BackgroundServerJobState.Completed, submittedJob!.State);
        Assert.Equal(1, Volatile.Read(ref commitCount));
    }

    [Fact]
    public async Task CompletedJob_IsAutomaticallyRemovedAfterRetentionDelay()
    {
        var delayStarted = new TaskCompletionSource<TimeSpan>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new BackgroundServerJobCoordinator(
            (_, _) => Task.CompletedTask,
            maximumConcurrentJobs: 1,
            maximumConcurrentBuildToolsJobs: 1,
            marshalToApplicationDispatcher: false,
            completedJobRetention: TimeSpan.FromSeconds(3),
            delayAsync: async (delay, cancellationToken) =>
            {
                delayStarted.TrySetResult(delay);
                await releaseDelay.Task.WaitAsync(cancellationToken);
            });

        Assert.True(coordinator.TryEnqueue(
            CreateDefinition(
                "Auto Clear",
                (_, _) => Task.FromResult(CreateServer("Auto Clear"))),
            out var job,
            out var error));
        Assert.Null(error);

        Assert.Equal(TimeSpan.FromSeconds(3),
            await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(BackgroundServerJobState.Completed, job!.State);
        Assert.Contains(job, coordinator.Jobs);

        releaseDelay.TrySetResult();
        await WaitUntilAsync(() => !coordinator.Jobs.Contains(job));

        Assert.False(coordinator.HasJobs);
        Assert.Equal(0, coordinator.FinishedCount);
        Assert.Equal("目前沒有背景工作", coordinator.SummaryText);
        Assert.Equal("沒有進行中的下載或建立工作", coordinator.LatestActivityText);
        Assert.Equal(0, coordinator.AggregateProgress);
        Assert.False(coordinator.ClearFinishedCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledJob_IsRetainedForInspection(bool cancel)
    {
        var delayCallCount = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new BackgroundServerJobCoordinator(
            (_, _) => Task.CompletedTask,
            maximumConcurrentJobs: 1,
            maximumConcurrentBuildToolsJobs: 1,
            marshalToApplicationDispatcher: false,
            completedJobRetention: TimeSpan.Zero,
            delayAsync: (_, _) =>
            {
                Interlocked.Increment(ref delayCallCount);
                return Task.CompletedTask;
            });
        var definition = CreateDefinition("Keep Terminal", async (_, cancellationToken) =>
        {
            started.TrySetResult();
            if (cancel)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            throw new InvalidOperationException("expected failure");
        });

        Assert.True(coordinator.TryEnqueue(definition, out var job, out var error));
        Assert.Null(error);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel)
        {
            coordinator.Cancel(job!);
        }

        await WaitUntilAsync(() => job!.IsFinished);

        Assert.Equal(cancel ? BackgroundServerJobState.Cancelled : BackgroundServerJobState.Failed, job!.State);
        Assert.Contains(job, coordinator.Jobs);
        Assert.Equal(0, Volatile.Read(ref delayCallCount));
    }

    [Fact]
    public async Task ManualClearBeforeAutomaticRemoval_IsSafe()
    {
        var releaseDelay = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new BackgroundServerJobCoordinator(
            (_, _) => Task.CompletedTask,
            maximumConcurrentJobs: 1,
            maximumConcurrentBuildToolsJobs: 1,
            marshalToApplicationDispatcher: false,
            delayAsync: (_, cancellationToken) => releaseDelay.Task.WaitAsync(cancellationToken));

        Assert.True(coordinator.TryEnqueue(
            CreateDefinition(
                "Manual Clear",
                (_, _) => Task.FromResult(CreateServer("Manual Clear"))),
            out var job,
            out _));
        await WaitUntilAsync(() => job!.State == BackgroundServerJobState.Completed);

        coordinator.ClearFinished();
        releaseDelay.TrySetResult();
        await Task.Delay(25);

        Assert.Empty(coordinator.Jobs);
        Assert.False(coordinator.ClearFinishedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Dispose_CancelsAndWaitsForPendingAutomaticRemoval()
    {
        var delayStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var delayCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new BackgroundServerJobCoordinator(
            (_, _) => Task.CompletedTask,
            maximumConcurrentJobs: 1,
            maximumConcurrentBuildToolsJobs: 1,
            marshalToApplicationDispatcher: false,
            delayAsync: async (_, cancellationToken) =>
            {
                delayStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        delayCancelled.TrySetResult();
                    }
                }
            });

        Assert.True(coordinator.TryEnqueue(
            CreateDefinition(
                "Dispose Cleanup",
                (_, _) => Task.FromResult(CreateServer("Dispose Cleanup"))),
            out _,
            out _));
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(delayCancelled.Task.IsCompletedSuccessfully);
        Assert.Empty(coordinator.Jobs);
    }

    private static BackgroundServerJobCoordinator CreateCoordinator(
        int maximumConcurrentJobs,
        int maximumConcurrentBuildToolsJobs = 1)
        => new(
            (_, _) => Task.CompletedTask,
            maximumConcurrentJobs: maximumConcurrentJobs,
            maximumConcurrentBuildToolsJobs: maximumConcurrentBuildToolsJobs,
            marshalToApplicationDispatcher: false);

    private static BackgroundServerJobDefinition CreateDefinition(
        string serverName,
        Func<IProgress<BackgroundServerJobProgress>, CancellationToken, Task<ServerInstance>> execute)
        => new(
            BackgroundServerJobKind.CoreServer,
            serverName,
            $"建立 {serverName}",
            execute);

    private static ServerInstance CreateServer(string name)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            DirectoryPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            ServerJarPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "server.jar")
        };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(15, timeout.Token);
        }
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current || Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }
}

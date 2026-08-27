using MinecraftServerManager.Remote;
using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.Remote.Tests;

public sealed class RemoteIdempotencyStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 21, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConcurrentSameRequest_SharesSingleInFlightOperation()
    {
        var store = new RemoteIdempotencyStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var sessionId = Guid.NewGuid();
        var key = Guid.NewGuid();
        var signature = RemoteMutationSignature.CreateCommand("server-01", "list");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async ValueTask<RemoteOperationResultDto> Operation(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new RemoteOperationResultDto(true, "done", "operation-01");
        }

        var first = store.ExecuteAsync(
            sessionId,
            key,
            signature,
            Operation,
            CancellationToken.None);
        await entered.Task;
        var second = store.ExecuteAsync(
            sessionId,
            key,
            signature,
            Operation,
            CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref calls));
        release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(RemoteIdempotencyOutcome.Completed, result.Outcome));
        Assert.Equal(results[0].Result, results[1].Result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CompletedSameRequest_ReplaysResultWithoutCallingBackendAgain()
    {
        var store = new RemoteIdempotencyStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var sessionId = Guid.NewGuid();
        var key = Guid.NewGuid();
        var signature = RemoteMutationSignature.CreateBackup("server-01");
        var calls = 0;

        ValueTask<RemoteOperationResultDto> Operation(CancellationToken _)
        {
            calls++;
            return ValueTask.FromResult(new RemoteOperationResultDto(true, "done", "backup-01"));
        }

        var first = await store.ExecuteAsync(
            sessionId,
            key,
            signature,
            Operation,
            CancellationToken.None);
        var replay = await store.ExecuteAsync(
            sessionId,
            key,
            signature,
            Operation,
            CancellationToken.None);

        Assert.Equal(RemoteIdempotencyOutcome.Completed, first.Outcome);
        Assert.Equal(first.Result, replay.Result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SameKeyWithDifferentCanonicalRequest_ReturnsConflict()
    {
        var store = new RemoteIdempotencyStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var sessionId = Guid.NewGuid();
        var key = Guid.NewGuid();
        var start = RemoteMutationSignature.CreateLifecycle("server-01", "start");
        var stop = RemoteMutationSignature.CreateLifecycle("server-01", "stop");
        var calls = 0;

        ValueTask<RemoteOperationResultDto> Operation(CancellationToken _)
        {
            calls++;
            return ValueTask.FromResult(new RemoteOperationResultDto(true, "done"));
        }

        _ = await store.ExecuteAsync(
            sessionId,
            key,
            start,
            Operation,
            CancellationToken.None);
        var conflict = await store.ExecuteAsync(
            sessionId,
            key,
            stop,
            Operation,
            CancellationToken.None);

        Assert.Equal(RemoteIdempotencyOutcome.Conflict, conflict.Outcome);
        Assert.Null(conflict.Result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BackupRestoreSignature_BindsOpaqueBackupIdAndExplicitConfirmation()
    {
        var store = new RemoteIdempotencyStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var sessionId = Guid.NewGuid();
        var key = Guid.NewGuid();
        var calls = 0;

        ValueTask<RemoteOperationResultDto> Operation(CancellationToken _)
        {
            calls++;
            return ValueTask.FromResult(new RemoteOperationResultDto(true, "restored"));
        }

        var first = await store.ExecuteAsync(
            sessionId,
            key,
            RemoteMutationSignature.CreateBackupRestore(
                "server-01",
                new string('a', 64),
                RemoteBackupRestoreContract.RequiredConfirmation),
            Operation,
            CancellationToken.None);
        var conflict = await store.ExecuteAsync(
            sessionId,
            key,
            RemoteMutationSignature.CreateBackupRestore(
                "server-01",
                new string('b', 64),
                RemoteBackupRestoreContract.RequiredConfirmation),
            Operation,
            CancellationToken.None);

        Assert.Equal(RemoteIdempotencyOutcome.Completed, first.Outcome);
        Assert.Equal(RemoteIdempotencyOutcome.Conflict, conflict.Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ClientWaitCancellation_DoesNotCancelMutationAndLaterReplayGetsResult()
    {
        var store = new RemoteIdempotencyStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var sessionId = Guid.NewGuid();
        var key = Guid.NewGuid();
        var signature = RemoteMutationSignature.CreateLifecycle("server-01", "restart");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var clientDisconnected = new CancellationTokenSource();
        var calls = 0;
        CancellationToken observedOperationToken = default;

        async ValueTask<RemoteOperationResultDto> Operation(CancellationToken cancellationToken)
        {
            calls++;
            observedOperationToken = cancellationToken;
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new RemoteOperationResultDto(true, "restart completed");
        }

        var disconnectedWait = store.ExecuteAsync(
            sessionId,
            key,
            signature,
            Operation,
            CancellationToken.None,
            clientDisconnected.Token);
        await entered.Task;
        clientDisconnected.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disconnectedWait);
        Assert.False(observedOperationToken.IsCancellationRequested);
        Assert.Equal(1, calls);

        release.TrySetResult();
        var replay = await store.ExecuteAsync(
            sessionId,
            key,
            signature,
            Operation,
            CancellationToken.None);

        Assert.Equal(RemoteIdempotencyOutcome.Completed, replay.Outcome);
        Assert.Equal("restart completed", replay.Result?.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LedgerIsBoundedAndExpiredEntriesBecomeReusableCapacity()
    {
        var time = new MutableTimeProvider(Start);
        var store = new RemoteIdempotencyStore(
            TestOptions.Create(
                idempotencyLifetime: TimeSpan.FromMinutes(1),
                maximumIdempotencyEntries: 16),
            time);
        var sessionId = Guid.NewGuid();
        var signature = RemoteMutationSignature.CreateBackup("server-01");

        for (var index = 0; index < 16; index++)
        {
            var result = await store.ExecuteAsync(
                sessionId,
                Guid.NewGuid(),
                signature,
                _ => ValueTask.FromResult(new RemoteOperationResultDto(true, "done")),
                CancellationToken.None);
            Assert.Equal(RemoteIdempotencyOutcome.Completed, result.Outcome);
        }

        var full = await store.ExecuteAsync(
            sessionId,
            Guid.NewGuid(),
            signature,
            _ => ValueTask.FromResult(new RemoteOperationResultDto(true, "must not run")),
            CancellationToken.None);
        Assert.Equal(RemoteIdempotencyOutcome.CapacityExceeded, full.Outcome);

        time.Advance(TimeSpan.FromMinutes(1));
        var afterExpiry = await store.ExecuteAsync(
            sessionId,
            Guid.NewGuid(),
            signature,
            _ => ValueTask.FromResult(new RemoteOperationResultDto(true, "new")),
            CancellationToken.None);
        Assert.Equal(RemoteIdempotencyOutcome.Completed, afterExpiry.Outcome);
    }

    [Fact]
    public async Task SameHeaderKeyInDifferentSessions_DoesNotShareResults()
    {
        var store = new RemoteIdempotencyStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var key = Guid.NewGuid();
        var signature = RemoteMutationSignature.CreateBackup("server-01");
        var calls = 0;

        ValueTask<RemoteOperationResultDto> Operation(CancellationToken _)
        {
            calls++;
            return ValueTask.FromResult(new RemoteOperationResultDto(true, calls.ToString()));
        }

        _ = await store.ExecuteAsync(Guid.NewGuid(), key, signature, Operation, CancellationToken.None);
        _ = await store.ExecuteAsync(Guid.NewGuid(), key, signature, Operation, CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Clear_DoesNotForgetDetachedOperationNeededByShutdownDrain()
    {
        var store = new RemoteIdempotencyStore(TestOptions.Create(), new MutableTimeProvider(Start));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask<RemoteOperationResultDto> Operation(CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new RemoteOperationResultDto(true, "done");
        }

        var execution = store.ExecuteAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            RemoteMutationSignature.CreateBackup("server-01"),
            Operation,
            CancellationToken.None);
        await entered.Task;

        store.Clear();
        var drain = store.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.False(drain.IsCompleted);

        release.TrySetResult();
        Assert.True(await drain);
        Assert.Equal(RemoteIdempotencyOutcome.Completed, (await execution).Outcome);
    }
}

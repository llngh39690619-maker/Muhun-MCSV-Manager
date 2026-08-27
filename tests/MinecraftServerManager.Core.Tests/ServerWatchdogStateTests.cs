using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class ServerWatchdogStateTests
{
    [Fact]
    public void Record_RequiresConsecutiveFailuresAfterStartupGraceAndTriggersOnce()
    {
        var state = new ServerWatchdogState();
        var instance = Guid.NewGuid();
        var session = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var policy = new ServerWatchdogPolicy(TimeSpan.FromMinutes(1), 3);
        state.StartSession(instance, session, started);

        Assert.True(state.Record(instance, session, started.AddSeconds(30), false, policy).IsInsideStartupGrace);
        Assert.False(state.Record(instance, session, started.AddMinutes(2), false, policy).ShouldRestart);
        Assert.False(state.Record(instance, session, started.AddMinutes(2).AddSeconds(1), false, policy).ShouldRestart);
        var trigger = state.Record(instance, session, started.AddMinutes(2).AddSeconds(2), false, policy);
        Assert.True(trigger.ShouldRestart);
        Assert.Equal(3, trigger.ConsecutiveFailures);
        Assert.False(state.Record(instance, session, started.AddMinutes(2).AddSeconds(3), false, policy).ShouldRestart);
    }

    [Fact]
    public void Record_HealthyProbeResetsFailuresAndStaleSessionIsIgnored()
    {
        var state = new ServerWatchdogState();
        var instance = Guid.NewGuid();
        var oldSession = Guid.NewGuid();
        var newSession = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var policy = new ServerWatchdogPolicy(TimeSpan.Zero, 2);
        state.StartSession(instance, oldSession, now);
        Assert.Equal(1, state.Record(instance, oldSession, now, false, policy).ConsecutiveFailures);
        Assert.True(state.Record(instance, oldSession, now, true, policy).IsHealthy);
        state.StartSession(instance, newSession, now);
        Assert.False(state.Record(instance, oldSession, now, false, policy).IsCurrentSession);
        Assert.Equal(1, state.Record(instance, newSession, now, false, policy).ConsecutiveFailures);
    }

    [Fact]
    public void EndSession_DoesNotRemoveNewerSession()
    {
        var state = new ServerWatchdogState();
        var instance = Guid.NewGuid();
        var oldSession = Guid.NewGuid();
        var newSession = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        state.StartSession(instance, oldSession, now);
        state.StartSession(instance, newSession, now);
        state.EndSession(instance, oldSession);
        Assert.True(state.Record(
            instance,
            newSession,
            now,
            true,
            new ServerWatchdogPolicy(TimeSpan.Zero, 2)).IsCurrentSession);
    }

    [Fact]
    public void CrashRestartLimiter_UsesBackoffAndOpensCircuitBreaker()
    {
        var limiter = new CrashRestartLimiter();
        var instance = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(TimeSpan.FromSeconds(5), limiter.RecordCrash(instance, now, TimeSpan.FromMinutes(1)).Delay);
        Assert.Equal(TimeSpan.FromSeconds(15), limiter.RecordCrash(instance, now.AddMinutes(1), TimeSpan.FromMinutes(1)).Delay);
        Assert.Equal(TimeSpan.FromSeconds(45), limiter.RecordCrash(instance, now.AddMinutes(2), TimeSpan.FromMinutes(1)).Delay);
        var blocked = limiter.RecordCrash(instance, now.AddMinutes(3), TimeSpan.FromMinutes(1));
        Assert.False(blocked.ShouldRestart);
        Assert.Contains("崩潰循環", blocked.Message);
    }

    [Fact]
    public void CrashRestartLimiter_StableSessionClearsHistory()
    {
        var limiter = new CrashRestartLimiter();
        var instance = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        _ = limiter.RecordCrash(instance, now, TimeSpan.FromMinutes(1));
        _ = limiter.RecordCrash(instance, now.AddMinutes(1), TimeSpan.FromMinutes(1));
        var afterStableRun = limiter.RecordCrash(instance, now.AddMinutes(2), TimeSpan.FromMinutes(11));
        Assert.True(afterStableRun.ShouldRestart);
        Assert.Equal(1, afterStableRun.CrashesInWindow);
        Assert.Equal(TimeSpan.FromSeconds(5), afterStableRun.Delay);
    }
}

using MinecraftServerManager.Remote;

namespace MinecraftServerManager.Remote.Tests;

internal static class TestOptions
{
    public static RemoteControlOptions Create(
        Uri? publicOrigin = null,
        IReadOnlyCollection<string>? allowedGoogleLogins = null,
        int port = 42871,
        TimeSpan? sessionLifetime = null,
        int maximumSessions = 16,
        int loginAttemptsPerMinute = 5,
        int globalRequestsPerMinute = 600,
        TimeSpan? idempotencyLifetime = null,
        int maximumIdempotencyEntries = 1024,
        CancellationToken operationCancellationToken = default,
        TimeSpan? mutationShutdownDrainTimeout = null) => new()
    {
        Port = port,
        PublicOrigin = publicOrigin ?? new Uri("https://mcsv-test.example.ts.net"),
        AllowedGoogleLogins = allowedGoogleLogins ?? ["owner@gmail.com"],
        SessionLifetime = sessionLifetime ?? TimeSpan.FromHours(12),
        MaximumSessions = maximumSessions,
        LoginAttemptsPerMinute = loginAttemptsPerMinute,
        GlobalRequestsPerMinute = globalRequestsPerMinute,
        IdempotencyLifetime = idempotencyLifetime ?? TimeSpan.FromMinutes(15),
        MaximumIdempotencyEntries = maximumIdempotencyEntries,
        OperationCancellationToken = operationCancellationToken,
        MutationShutdownDrainTimeout = mutationShutdownDrainTimeout ?? TimeSpan.FromSeconds(10)
    };
}

internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan duration) => _now = _now.Add(duration);
}

using MinecraftServerManager.Data;
using MinecraftServerManager.Remote;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductRemoteSecurityAuditSinkTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "muhun-mcsv-remote-audit-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ValidMutationDecision_IsDurableWithoutRequestBodyOrSecrets()
    {
        Directory.CreateDirectory(_root);
        var database = new ProductDatabase(Path.Combine(_root, "product.v1.db"));
        await database.InitializeAsync();
        var store = new ProductSecurityAuditStore(database);
        var sink = new ProductRemoteSecurityAuditSink(store);
        var serverId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        var written = sink.TryWrite(new RemoteSecurityAuditEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            RemoteSecurityAuditAction.ServerMutation,
            RemoteSecurityAuditOutcome.Accepted,
            "manager1",
            "server.start",
            serverId,
            "authorized",
            correlationId));

        Assert.True(written);
        var entry = Assert.Single(await store.ReadRecentAsync(10));
        Assert.Equal("server.mutation", entry.ActionCode);
        Assert.Equal("accepted", entry.OutcomeCode);
        Assert.Equal(serverId, entry.ServerId);
        Assert.Equal(correlationId, entry.CorrelationId);
        Assert.DoesNotContain("pin", entry.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("command", entry.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidAuditEvent_FailsClosedWithoutWriting()
    {
        Directory.CreateDirectory(_root);
        var database = new ProductDatabase(Path.Combine(_root, "product.v1.db"));
        await database.InitializeAsync();
        var store = new ProductSecurityAuditStore(database);
        var sink = new ProductRemoteSecurityAuditSink(store);

        Assert.False(sink.TryWrite(new RemoteSecurityAuditEvent(
            Guid.Empty,
            DateTimeOffset.UtcNow,
            RemoteSecurityAuditAction.CredentialLogin,
            RemoteSecurityAuditOutcome.Rejected,
            null,
            null,
            null,
            "invalid")));
        Assert.Empty(await store.ReadRecentAsync(10));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}

using MinecraftServerManager.Contracts.Notifications;

namespace MinecraftServerManager.Contracts.Tests;

public sealed class ProductEventEnvelopeTests
{
    [Fact]
    public void ValidServerEvent_IsAccepted()
    {
        var envelope = new ProductEventEnvelope(
            ProductEventEnvelopeValidator.CurrentSchemaVersion,
            Guid.NewGuid(),
            Sequence: 1,
            DateTimeOffset.UtcNow,
            "server.started",
            ProductEventSeverity.Information,
            "Notification.Server.Started",
            Guid.NewGuid(),
            Guid.NewGuid(),
            new Dictionary<string, string> { ["server_name"] = "Test" });

        Assert.True(ProductEventEnvelopeValidator.Validate(envelope).IsValid);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("access_token")]
    [InlineData("webhook_url")]
    [InlineData("authorization")]
    public void SensitiveDataKey_IsRejected(string key)
    {
        var envelope = new ProductEventEnvelope(
            1,
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            "auth.failed",
            ProductEventSeverity.Warning,
            "Notification.Auth.Failed",
            null,
            null,
            new Dictionary<string, string> { [key] = "redacted-or-not" });

        var result = ProductEventEnvelopeValidator.Validate(envelope);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("sensitive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LocalTimestamp_IsRejected()
    {
        var envelope = new ProductEventEnvelope(
            1,
            Guid.NewGuid(),
            1,
            new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.FromHours(8)),
            "server.stopped",
            ProductEventSeverity.Information,
            "Notification.Server.Stopped",
            Guid.NewGuid(),
            null,
            new Dictionary<string, string>());

        Assert.False(ProductEventEnvelopeValidator.Validate(envelope).IsValid);
    }

    [Fact]
    public void DataOutsideRegisteredEventSchema_IsRejected()
    {
        var envelope = new ProductEventEnvelope(
            1,
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            "server.started",
            ProductEventSeverity.Information,
            "Notification.Server.Started",
            Guid.NewGuid(),
            null,
            new Dictionary<string, string> { ["endpoint_url"] = "https://example.invalid/private" });

        var result = ProductEventEnvelopeValidator.Validate(envelope);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("not allowed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UndefinedSeverity_IsRejected()
    {
        var envelope = new ProductEventEnvelope(
            1,
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            "service.started",
            (ProductEventSeverity)999,
            "Notification.Service.Started",
            null,
            null,
            new Dictionary<string, string>());

        Assert.False(ProductEventEnvelopeValidator.Validate(envelope).IsValid);
    }
}

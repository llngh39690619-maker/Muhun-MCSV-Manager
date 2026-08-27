using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MinecraftServerManager.Contracts.Notifications;

namespace MinecraftServerManager.Notifications.Tests;

public sealed class DiscordWebhookProviderTests
{
    private const string ValidWebhook =
        "https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz_ABCDE12345";

    [Theory]
    [InlineData(ValidWebhook, true)]
    [InlineData("http://discord.com/api/webhooks/123456/abcdefghijklmnopqrstuvwxyz", false)]
    [InlineData("https://evil.discord.com/api/webhooks/123456/abcdefghijklmnopqrstuvwxyz", false)]
    [InlineData("https://discord.com.evil.invalid/api/webhooks/123456/abcdefghijklmnopqrstuvwxyz", false)]
    [InlineData("https://discord.com:8443/api/webhooks/123456/abcdefghijklmnopqrstuvwxyz", false)]
    [InlineData("https://discord.com/api/webhooks/123456/abcdefghijklmnopqrstuvwxyz?wait=true", false)]
    [InlineData("https://discord.com/api/webhooks/not-a-number/abcdefghijklmnopqrstuvwxyz", false)]
    public void WebhookUriValidation_IsExactAndHttps(string value, bool expected)
    {
        Assert.Equal(expected, DiscordWebhookProvider.TryValidateWebhookUri(value, out _));
    }

    [Fact]
    public async Task RateLimit_UsesRetryAfterWithoutExposingSecret()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("discord.com", request.RequestUri!.Host);
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });
        using var provider = CreateProvider(handler);

        var result = await provider.DeliverAsync(CreateEvent(), CancellationToken.None);

        Assert.Equal(NotificationProviderDeliveryStatus.Retry, result.Status);
        Assert.Equal("discord.rate_limited", result.FailureCode);
        Assert.Equal(TimeSpan.FromSeconds(7), result.RetryAfter);
    }

    [Fact]
    public async Task PayloadExplicitlyDisablesDiscordMentions()
    {
        var handler = new StubHandler(request =>
        {
            var json = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(json);
            Assert.Equal(
                "伺服器已啟動",
                document.RootElement.GetProperty("content").GetString());
            Assert.Empty(document.RootElement.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var provider = CreateProvider(handler);

        Assert.Equal(
            NotificationProviderDeliveryStatus.Delivered,
            (await provider.DeliverAsync(CreateEvent(), CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, NotificationProviderDeliveryStatus.Delivered)]
    [InlineData(HttpStatusCode.NotFound, NotificationProviderDeliveryStatus.DisableProvider)]
    [InlineData(HttpStatusCode.InternalServerError, NotificationProviderDeliveryStatus.Retry)]
    [InlineData(HttpStatusCode.BadRequest, NotificationProviderDeliveryStatus.TerminalFailure)]
    public async Task StatusClassification_IsBounded(
        HttpStatusCode statusCode,
        NotificationProviderDeliveryStatus expected)
    {
        using var provider = CreateProvider(new StubHandler(_ => new HttpResponseMessage(statusCode)));

        var result = await provider.DeliverAsync(CreateEvent(), CancellationToken.None);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task InvalidVaultValue_DisablesWithoutSendingNetworkRequest()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Must not send."));
        using var provider = new DiscordWebhookProvider(
            "discord.primary",
            "vault:discord",
            new FixedSecretResolver("https://attacker.invalid/webhook"),
            new FixedRenderer("message"),
            handler);

        var result = await provider.DeliverAsync(CreateEvent(), CancellationToken.None);

        Assert.Equal(NotificationProviderDeliveryStatus.DisableProvider, result.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task RejectedRequest_ReturnsTheExactOpaqueCredentialGeneration()
    {
        using var provider = new DiscordWebhookProvider(
            "discord.primary",
            "vault:discord",
            new VersionedSecretResolver(ValidWebhook, "generation-42"),
            new FixedRenderer("message"),
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var result = await provider.DeliverAsync(CreateEvent(), CancellationToken.None);

        Assert.Equal(NotificationProviderDeliveryStatus.DisableProvider, result.Status);
        Assert.Equal("generation-42", result.ProviderGeneration);
    }

    private static DiscordWebhookProvider CreateProvider(HttpMessageHandler handler)
        => new(
            "discord.primary",
            "vault:discord",
            new FixedSecretResolver(ValidWebhook),
            new FixedRenderer("伺服器已啟動"),
            handler);

    private static ProductEventEnvelope CreateEvent()
        => new(
            1,
            Guid.NewGuid(),
            1,
            DateTimeOffset.UtcNow,
            "server.started",
            ProductEventSeverity.Information,
            "Notification.Server.Started",
            Guid.NewGuid(),
            null,
            new Dictionary<string, string> { ["server_name"] = "Test" });

    private sealed class FixedSecretResolver(string value) : INotificationSecretResolver
    {
        public ValueTask<string?> ResolveSecretAsync(string secretReference, CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>(value);
    }

    private sealed class VersionedSecretResolver(string value, string generation)
        : IVersionedNotificationSecretResolver
    {
        public ValueTask<string?> ResolveSecretAsync(
            string secretReference,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>(value);

        public ValueTask<NotificationSecretSnapshot?> ResolveSecretSnapshotAsync(
            string secretReference,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<NotificationSecretSnapshot?>(new(value, generation));
    }

    private sealed class FixedRenderer(string value) : INotificationMessageRenderer
    {
        public ValueTask<string> RenderAsync(ProductEventEnvelope envelope, CancellationToken cancellationToken)
            => ValueTask.FromResult(value);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responseFactory(request));
        }
    }
}

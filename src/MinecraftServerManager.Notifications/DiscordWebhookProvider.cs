using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MinecraftServerManager.Contracts.Notifications;

namespace MinecraftServerManager.Notifications;

public sealed partial class DiscordWebhookProvider : INotificationDeliveryProvider, IDisposable
{
    private const int MaximumMessageLength = 1_800;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromMinutes(15);
    private readonly INotificationSecretResolver _secretResolver;
    private readonly INotificationMessageRenderer _renderer;
    private readonly string _secretReference;
    private readonly HttpMessageInvoker _transport;
    private readonly bool _ownsTransport;

    public DiscordWebhookProvider(
        string providerId,
        string secretReference,
        INotificationSecretResolver secretResolver,
        INotificationMessageRenderer renderer)
        : this(
            providerId,
            secretReference,
            secretResolver,
            renderer,
            CreateSecureHandler(),
            ownsTransport: true)
    {
    }

    internal DiscordWebhookProvider(
        string providerId,
        string secretReference,
        INotificationSecretResolver secretResolver,
        INotificationMessageRenderer renderer,
        HttpMessageHandler handler,
        bool ownsTransport = true)
    {
        ProviderId = ValidateProviderId(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretReference);
        if (secretReference.Length > 128 || !SecretReferencePattern().IsMatch(secretReference))
        {
            throw new ArgumentException("Secret reference is invalid.", nameof(secretReference));
        }

        _secretReference = secretReference;
        _secretResolver = secretResolver ?? throw new ArgumentNullException(nameof(secretResolver));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _transport = new HttpMessageInvoker(handler ?? throw new ArgumentNullException(nameof(handler)), ownsTransport);
        _ownsTransport = ownsTransport;
    }

    public string ProviderId { get; }

    public async Task<NotificationProviderDeliveryResult> DeliverAsync(
        ProductEventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!ProductEventEnvelopeValidator.Validate(envelope).IsValid)
        {
            return new NotificationProviderDeliveryResult(
                NotificationProviderDeliveryStatus.TerminalFailure,
                "event.invalid");
        }

        var snapshot = _secretResolver is IVersionedNotificationSecretResolver versioned
            ? await versioned.ResolveSecretSnapshotAsync(_secretReference, cancellationToken)
                .ConfigureAwait(false)
            : new NotificationSecretSnapshot(
                await _secretResolver.ResolveSecretAsync(_secretReference, cancellationToken)
                    .ConfigureAwait(false),
                "legacy");
        var secret = snapshot?.Value;
        var generation = snapshot?.Generation;
        if (!TryValidateWebhookUri(secret, out var webhookUri))
        {
            return new NotificationProviderDeliveryResult(
                NotificationProviderDeliveryStatus.DisableProvider,
                "discord.webhook_invalid",
                ProviderGeneration: generation);
        }

        var message = await _renderer.RenderAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(message))
        {
            return new NotificationProviderDeliveryResult(
                NotificationProviderDeliveryStatus.TerminalFailure,
                "notification.render_empty");
        }

        if (message.Length > MaximumMessageLength)
        {
            message = message[..MaximumMessageLength];
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, webhookUri)
        {
            Content = JsonContent.Create(new DiscordPayload(message, DiscordAllowedMentions.None)),
        };
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Muhun-MCSV", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);
        try
        {
            using var response = await _transport.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            var result = await ClassifyResponseAsync(response, timeoutSource.Token).ConfigureAwait(false);
            return result with { ProviderGeneration = generation };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new NotificationProviderDeliveryResult(
                NotificationProviderDeliveryStatus.Retry,
                "discord.timeout",
                TimeSpan.FromSeconds(5),
                generation);
        }
        catch (HttpRequestException)
        {
            return new NotificationProviderDeliveryResult(
                NotificationProviderDeliveryStatus.Retry,
                "discord.network",
                TimeSpan.FromSeconds(5),
                generation);
        }
    }

    public void Dispose()
    {
        if (_ownsTransport)
        {
            _transport.Dispose();
        }
    }

    public static bool TryValidateWebhookUri(string? value, out Uri webhookUri)
    {
        webhookUri = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var candidate))
        {
            return false;
        }

        if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.IdnHost, "discord.com", StringComparison.OrdinalIgnoreCase) ||
            !candidate.IsDefaultPort ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment))
        {
            return false;
        }

        var segments = candidate.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4 ||
            !string.Equals(segments[0], "api", StringComparison.Ordinal) ||
            !string.Equals(segments[1], "webhooks", StringComparison.Ordinal) ||
            !WebhookIdPattern().IsMatch(segments[2]) ||
            !WebhookTokenPattern().IsMatch(segments[3]))
        {
            return false;
        }

        webhookUri = candidate;
        return true;
    }

    private static async Task<NotificationProviderDeliveryResult> ClassifyResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var statusCode = response.StatusCode;
        if (statusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
        {
            return NotificationProviderDeliveryResult.Delivered;
        }

        if ((int)statusCode == 429)
        {
            var retryAfter = GetRetryAfter(response.Headers.RetryAfter, response, cancellationToken);
            return new NotificationProviderDeliveryResult(
                NotificationProviderDeliveryStatus.Retry,
                "discord.rate_limited",
                await retryAfter.ConfigureAwait(false));
        }

        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return new NotificationProviderDeliveryResult(
                NotificationProviderDeliveryStatus.DisableProvider,
                "discord.webhook_rejected");
        }

        if (statusCode == HttpStatusCode.RequestTimeout ||
            (int)statusCode == 425 ||
            (int)statusCode >= 500)
        {
            return new NotificationProviderDeliveryResult(
                NotificationProviderDeliveryStatus.Retry,
                $"discord.http_{(int)statusCode}",
                TimeSpan.FromSeconds(5));
        }

        return new NotificationProviderDeliveryResult(
            NotificationProviderDeliveryStatus.TerminalFailure,
            $"discord.http_{(int)statusCode}");
    }

    private static async Task<TimeSpan> GetRetryAfter(
        RetryConditionHeaderValue? header,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (header?.Delta is { } delta)
        {
            return ClampRetryAfter(delta);
        }

        if (header?.Date is { } date)
        {
            return ClampRetryAfter(date - DateTimeOffset.UtcNow);
        }

        try
        {
            if (response.Content.Headers.ContentLength is > 16 * 1024)
            {
                return TimeSpan.FromSeconds(5);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[16 * 1024 + 1];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            if (length > 16 * 1024)
            {
                return TimeSpan.FromSeconds(5);
            }

            using var document = JsonDocument.Parse(
                buffer.AsMemory(0, length),
                new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.TryGetProperty("retry_after", out var property) &&
                property.TryGetDouble(out var seconds) &&
                double.IsFinite(seconds))
            {
                return ClampRetryAfter(TimeSpan.FromSeconds(seconds));
            }
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or InvalidOperationException)
        {
        }

        return TimeSpan.FromSeconds(5);
    }

    private static TimeSpan ClampRetryAfter(TimeSpan value)
    {
        if (value < TimeSpan.FromSeconds(1))
        {
            return TimeSpan.FromSeconds(1);
        }

        return value > MaximumRetryAfter ? MaximumRetryAfter : value;
    }

    private static HttpMessageHandler CreateSecureHandler()
        => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxConnectionsPerServer = 2,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
        };

    private static string ValidateProviderId(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (!ProviderIdPattern().IsMatch(providerId))
        {
            throw new ArgumentException("Provider id is invalid.", nameof(providerId));
        }

        return providerId;
    }

    private sealed record DiscordPayload(
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("allowed_mentions")] DiscordAllowedMentions AllowedMentions);

    private sealed record DiscordAllowedMentions(
        [property: JsonPropertyName("parse")] IReadOnlyList<string> Parse)
    {
        public static DiscordAllowedMentions None { get; } = new(Array.Empty<string>());
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[._-][a-z0-9]+){0,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderIdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretReferencePattern();

    [GeneratedRegex("^[0-9]{5,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex WebhookIdPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{20,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex WebhookTokenPattern();
}

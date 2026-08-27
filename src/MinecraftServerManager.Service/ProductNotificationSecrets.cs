using MinecraftServerManager.Data;
using MinecraftServerManager.Notifications;
using MinecraftServerManager.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftServerManager.Service;

/// <summary>
/// The only notification secret reference accepted by the Service. The resolver is deliberately
/// not a general-purpose vault browser and no REST response returns the resolved value.
/// </summary>
public sealed class ProductNotificationSecretResolver(IProductSecretVault vault)
    : IVersionedNotificationSecretResolver
{
    public const string DiscordWebhookSecretReference = "notification.discord.primary.webhook";

    public ValueTask<string?> ResolveSecretAsync(
        string secretReference,
        CancellationToken cancellationToken)
        => ResolveValueAsync(secretReference, cancellationToken);

    public async ValueTask<NotificationSecretSnapshot?> ResolveSecretSnapshotAsync(
        string secretReference,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(secretReference, DiscordWebhookSecretReference, StringComparison.Ordinal))
        {
            return null;
        }

        var stored = await vault.GetSecretAsync(secretReference, cancellationToken).ConfigureAwait(false);
        var parsed = ProductDiscordWebhookSecretDocument.Parse(stored);
        return parsed is null
            ? new NotificationSecretSnapshot(null, "invalid")
            : new NotificationSecretSnapshot(
                parsed.Disabled ? null : parsed.WebhookUrl,
                parsed.Generation);
    }

    private async ValueTask<string?> ResolveValueAsync(
        string secretReference,
        CancellationToken cancellationToken)
        => (await ResolveSecretSnapshotAsync(secretReference, cancellationToken).ConfigureAwait(false))?.Value;
}

public sealed class ProductDiscordWebhookSettings
{
    private readonly IProductSecretVault _vault;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProductDiscordWebhookSettings(
        IProductSecretVault vault,
        ProductNotificationSecretResolver resolver)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        ArgumentNullException.ThrowIfNull(resolver);
    }

    public async Task<ProductDiscordWebhookConfiguration> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var stored = await _vault.GetSecretAsync(
                ProductNotificationSecretResolver.DiscordWebhookSecretReference,
                cancellationToken)
            .ConfigureAwait(false);
        var parsed = ProductDiscordWebhookSecretDocument.Parse(stored);
        var configured = parsed is not null &&
                         DiscordWebhookProvider.TryValidateWebhookUri(parsed.WebhookUrl, out _);
        return new ProductDiscordWebhookConfiguration(configured, configured && !parsed!.Disabled);
    }

    public async Task<ProductDiscordWebhookConfiguration> SetAsync(
        string webhookUrl,
        CancellationToken cancellationToken = default)
    {
        if (!DiscordWebhookProvider.TryValidateWebhookUri(webhookUrl, out var validated))
        {
            throw new ArgumentException(
                "Discord webhook must be an exact HTTPS discord.com API webhook URL.",
                nameof(webhookUrl));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = new ProductDiscordWebhookSecretDocument(
                1,
                Guid.NewGuid().ToString("N"),
                Disabled: false,
                validated.AbsoluteUri);
            await _vault.SetSecretAsync(
                    ProductNotificationSecretResolver.DiscordWebhookSecretReference,
                    JsonSerializer.Serialize(
                        document,
                        ProductDiscordWebhookSecretJsonContext.Default.ProductDiscordWebhookSecretDocument),
                    cancellationToken)
                .ConfigureAwait(false);
            return new ProductDiscordWebhookConfiguration(Configured: true, Enabled: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProductDiscordWebhookConfiguration> DeleteAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _vault.DeleteSecretAsync(
                    ProductNotificationSecretResolver.DiscordWebhookSecretReference,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ProductDiscordWebhookConfiguration(Configured: false, Enabled: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DisableGenerationAsync(
        string? expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedGeneration) || expectedGeneration.Length > 64)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await _vault.GetSecretAsync(
                    ProductNotificationSecretResolver.DiscordWebhookSecretReference,
                    cancellationToken)
                .ConfigureAwait(false);
            var parsed = ProductDiscordWebhookSecretDocument.Parse(stored);
            if (parsed is null || parsed.Disabled ||
                !string.Equals(parsed.Generation, expectedGeneration, StringComparison.Ordinal))
            {
                return false;
            }

            var disabled = parsed with { Disabled = true };
            await _vault.SetSecretAsync(
                    ProductNotificationSecretResolver.DiscordWebhookSecretReference,
                    JsonSerializer.Serialize(
                        disabled,
                        ProductDiscordWebhookSecretJsonContext.Default.ProductDiscordWebhookSecretDocument),
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsGenerationDisabledAsync(
        string? expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedGeneration) || expectedGeneration.Length > 64)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await _vault.GetSecretAsync(
                    ProductNotificationSecretResolver.DiscordWebhookSecretReference,
                    cancellationToken)
                .ConfigureAwait(false);
            var parsed = ProductDiscordWebhookSecretDocument.Parse(stored);
            return parsed is { Disabled: true } &&
                   string.Equals(parsed.Generation, expectedGeneration, StringComparison.Ordinal);
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal sealed record ProductDiscordWebhookSecretDocument(
    int SchemaVersion,
    string Generation,
    bool Disabled,
    string WebhookUrl)
{
    internal static ProductDiscordWebhookSecretDocument? Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        if (DiscordWebhookProvider.TryValidateWebhookUri(stored, out var legacy))
        {
            return new ProductDiscordWebhookSecretDocument(1, "legacy", false, legacy.AbsoluteUri);
        }

        try
        {
            var document = JsonSerializer.Deserialize(
                stored,
                ProductDiscordWebhookSecretJsonContext.Default.ProductDiscordWebhookSecretDocument);
            return document is not null && document.SchemaVersion == 1 &&
                   document.Generation.Length is >= 1 and <= 64 &&
                   document.Generation.All(character => char.IsAsciiLetterOrDigit(character)) &&
                   DiscordWebhookProvider.TryValidateWebhookUri(document.WebhookUrl, out _)
                ? document
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ProductDiscordWebhookSecretDocument))]
internal sealed partial class ProductDiscordWebhookSecretJsonContext : JsonSerializerContext;

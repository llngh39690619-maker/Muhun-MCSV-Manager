using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Localization;
using MinecraftServerManager.Notifications;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductNotificationSecretsTests
{
    internal const string ValidWebhook =
        "https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz_ABCDE12345";

    [Fact]
    public async Task FixedResolverAndSettings_NeverReturnWebhookInConfigurationResponse()
    {
        var vault = new MemoryProductSecretVault();
        var resolver = new ProductNotificationSecretResolver(vault);
        var settings = new ProductDiscordWebhookSettings(vault, resolver);

        Assert.False((await settings.GetAsync()).Configured);
        Assert.Null(await resolver.ResolveSecretAsync("notification.other.secret", default));
        var configured = await settings.SetAsync(ValidWebhook);
        var responseJson = JsonSerializer.Serialize(configured);

        Assert.True(configured.Configured);
        Assert.DoesNotContain("discord.com", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ABCDE12345", responseJson, StringComparison.Ordinal);
        Assert.Equal(
            ValidWebhook,
            await resolver.ResolveSecretAsync(
                ProductNotificationSecretResolver.DiscordWebhookSecretReference,
                default));

        Assert.False((await settings.DeleteAsync()).Configured);
        Assert.False((await settings.GetAsync()).Configured);
    }

    [Theory]
    [InlineData("http://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz_ABCDE12345")]
    [InlineData("https://evil.invalid/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz_ABCDE12345")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/short")]
    public async Task InvalidWebhook_IsRejectedWithoutChangingVault(string value)
    {
        var vault = new MemoryProductSecretVault();
        var settings = new ProductDiscordWebhookSettings(
            vault,
            new ProductNotificationSecretResolver(vault));

        await Assert.ThrowsAsync<ArgumentException>(() => settings.SetAsync(value));

        Assert.False((await settings.GetAsync()).Configured);
    }

    [Fact]
    public async Task MessageRenderer_UsesBoundedSchemaDataOnly()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var envelope = ProductNotificationOutboxIntegrationTests.CreateEvent(sequence: 1);
        try
        {
            var preferences = new ProductNotificationPreferenceStore(layout);
            var message = await new ProductNotificationMessageRenderer(preferences)
                .RenderAsync(envelope, CancellationToken.None);

            Assert.Contains("Test Server", message);
            Assert.DoesNotContain("webhook", message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(layout.Root, recursive: true);
        }
    }

    [Theory]
    [InlineData(ProductLocalizationCatalog.FallbackCulture, "已啟動")]
    [InlineData(ProductLocalizationCatalog.EnglishCulture, "started")]
    public async Task MessageRenderer_UsesDurableProductCulture(
        string cultureName,
        string expectedText)
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        try
        {
            var preferences = new ProductNotificationPreferenceStore(layout);
            await preferences.SetAsync(ProductNotificationPreferences.Default with
            {
                CultureName = cultureName,
            });
            preferences = new ProductNotificationPreferenceStore(layout);
            var envelope = ProductNotificationOutboxIntegrationTests.CreateEvent(sequence: 1);

            var message = await new ProductNotificationMessageRenderer(preferences)
                .RenderAsync(envelope, CancellationToken.None);

            Assert.Contains("Test Server", message, StringComparison.Ordinal);
            Assert.Contains(expectedText, message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(layout.Root, recursive: true);
        }
    }

    [Fact]
    public async Task LegacySchemaOnePreferences_DefaultToFallbackCulture()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        try
        {
            layout.EnsureCreated();
            var path = Path.Combine(
                layout.Operations,
                ProductNotificationPreferenceStore.FileName);
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "schemaVersion": 1,
                  "preferences": {
                    "schemaVersion": 1,
                    "serverLifecycle": true,
                    "backupOperations": true,
                    "modpackUpdates": true,
                    "productUpdates": true,
                    "providerHealth": true,
                    "externalThrottleSeconds": 30
                  },
                  "externalClaims": {}
                }
                """);

            var preferences = await new ProductNotificationPreferenceStore(layout).GetAsync();

            Assert.Equal(ProductLocalizationCatalog.FallbackCulture, preferences.CultureName);
        }
        finally
        {
            Directory.Delete(layout.Root, recursive: true);
        }
    }

    [Fact]
    public async Task MessageRenderer_LocalizesEverySupportedMessageWithoutFormatLeaks()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var types = new[]
        {
            "server.started", "server.stopped", "server.crashed",
            "backup.completed", "backup.restored", "backup.failed",
            "modpack.update.completed", "modpack.update.rolled-back", "modpack.update.failed",
            "product.update.available", "product.update.completed",
            "product.update.rolled-back", "product.update.failed", "provider.disabled",
            "future.event",
        };
        try
        {
            var preferences = new ProductNotificationPreferenceStore(layout);
            foreach (var cultureName in ProductLocalizationCatalog.SupportedCultures)
            {
                await preferences.SetAsync(ProductNotificationPreferences.Default with
                {
                    CultureName = cultureName,
                });
                var renderer = new ProductNotificationMessageRenderer(preferences);
                foreach (var type in types)
                {
                    var envelope = ProductNotificationOutboxIntegrationTests.CreateEvent(sequence: 1)
                        with { Type = type };
                    var message = await renderer.RenderAsync(envelope, CancellationToken.None);

                    Assert.NotEmpty(message);
                    Assert.DoesNotContain("{0}", message, StringComparison.Ordinal);
                    Assert.DoesNotContain("notification.message.", message, StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            Directory.Delete(layout.Root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectedGeneration_RemainsDisabledAcrossRestart_AndSameUrlCreatesNewGeneration()
    {
        var vault = new MemoryProductSecretVault();
        var firstResolver = new ProductNotificationSecretResolver(vault);
        var first = new ProductDiscordWebhookSettings(vault, firstResolver);
        await first.SetAsync(ValidWebhook);
        var generation = await firstResolver.ResolveSecretSnapshotAsync(
            ProductNotificationSecretResolver.DiscordWebhookSecretReference,
            default);
        Assert.NotNull(generation);

        Assert.True(await first.DisableGenerationAsync(generation!.Generation));
        Assert.False(await first.DisableGenerationAsync(generation.Generation));

        var restartedResolver = new ProductNotificationSecretResolver(vault);
        var restarted = new ProductDiscordWebhookSettings(vault, restartedResolver);
        var disabled = await restarted.GetAsync();
        Assert.True(disabled.Configured);
        Assert.False(disabled.Enabled);
        Assert.Null((await restartedResolver.ResolveSecretSnapshotAsync(
            ProductNotificationSecretResolver.DiscordWebhookSecretReference,
            default))!.Value);

        var enabled = await restarted.SetAsync(ValidWebhook);
        var replacement = await restartedResolver.ResolveSecretSnapshotAsync(
            ProductNotificationSecretResolver.DiscordWebhookSecretReference,
            default);
        Assert.NotNull(replacement);
        Assert.True(enabled.Enabled);
        Assert.Equal(ValidWebhook, replacement!.Value);
        Assert.NotEqual(generation.Generation, replacement.Generation);
        Assert.False(await restarted.DisableGenerationAsync(generation.Generation));
        Assert.True((await restarted.GetAsync()).Enabled);
    }
}

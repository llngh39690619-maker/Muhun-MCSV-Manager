using System.Buffers.Binary;
using System.Text.Json;
using MinecraftServerManager.Remote;

namespace MinecraftServerManager.Remote.Tests;

public sealed class RemoteWebPwaContractTests
{
    [Theory]
    [InlineData("manifest.zh-TW.webmanifest", "zh-TW")]
    [InlineData("manifest.en-US.webmanifest", "en-US")]
    public void Manifest_DefinesLocalizedSameOriginStandaloneApplicationAndDedicatedMaskableIcon(
        string fileName,
        string expectedCulture)
    {
        using var document = JsonDocument.Parse(ReadWebAssetText(fileName));
        var root = document.RootElement;

        Assert.Equal("/", root.GetProperty("id").GetString());
        Assert.Equal($"/?culture={expectedCulture}", root.GetProperty("start_url").GetString());
        Assert.Equal("/", root.GetProperty("scope").GetString());
        Assert.Equal("standalone", root.GetProperty("display").GetString());
        Assert.Equal(expectedCulture, root.GetProperty("lang").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("description").GetString()));

        var icons = root.GetProperty("icons").EnumerateArray().ToArray();
        Assert.Contains(icons, icon =>
            icon.GetProperty("src").GetString() == "/icon-192.png" &&
            icon.GetProperty("sizes").GetString() == "192x192" &&
            icon.GetProperty("purpose").GetString() == "any");
        Assert.Contains(icons, icon =>
            icon.GetProperty("src").GetString() == "/icon-512.png" &&
            icon.GetProperty("purpose").GetString() == "any");
        Assert.Contains(icons, icon =>
            icon.GetProperty("src").GetString() == "/icon-maskable-512.png" &&
            icon.GetProperty("purpose").GetString() == "maskable");
    }

    [Theory]
    [InlineData("icon-180.png", 180)]
    [InlineData("icon-192.png", 192)]
    [InlineData("icon-512.png", 512)]
    [InlineData("icon-maskable-512.png", 512)]
    public void Icons_AreEmbeddedPngsWithExactSquareDimensions(string fileName, int expectedSize)
    {
        var bytes = ReadWebAssetBytes(fileName);
        Assert.True(bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        Assert.Equal(expectedSize, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(expectedSize, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
    }

    [Fact]
    public void ServiceWorker_CachesOnlyInertOfflineAssetsAndNeverQueuesControlTraffic()
    {
        var worker = ReadWebAssetText("service-worker.js");

        Assert.Contains("request.method !== \"GET\"", worker, StringComparison.Ordinal);
        Assert.Contains("url.origin !== self.location.origin", worker, StringComparison.Ordinal);
        Assert.Contains("url.pathname.startsWith(\"/api/\")", worker, StringComparison.Ordinal);
        Assert.Contains("request.mode === \"navigate\"", worker, StringComparison.Ordinal);
        Assert.Contains("fetch(request)", worker, StringComparison.Ordinal);
        Assert.Contains("/offline.html?culture=zh-TW", worker, StringComparison.Ordinal);
        Assert.Contains("/offline.html?culture=en-US", worker, StringComparison.Ordinal);
        Assert.Contains("/manifest.webmanifest?culture=zh-TW", worker, StringComparison.Ordinal);
        Assert.Contains("/manifest.webmanifest?culture=en-US", worker, StringComparison.Ordinal);
        Assert.Contains("localizedAssetUrl(url, OFFLINE_URLS)", worker, StringComparison.Ordinal);
        Assert.Contains("localizedAssetUrl(url, MANIFEST_URLS)", worker, StringComparison.Ordinal);
        Assert.Contains("language === \"en\" ? \"en-US\" : FALLBACK_CULTURE", worker, StringComparison.Ordinal);
        Assert.Contains("OFFLINE_SUPPORT_PATHS.has(url.pathname)", worker, StringComparison.Ordinal);
        Assert.Contains("caches.match(request)", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("/app.js", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("/index.html\",", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("cache.put", worker, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backgroundsync", worker, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sync", worker, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("queue", worker, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("offline.zh-TW.html", "zh-TW", "離線期間不會暫存或稍後送出任何控制操作")]
    [InlineData("offline.en-US.html", "en-US", "No control operation is stored or sent later while offline")]
    public void OfflinePage_IsLocalizedInertAndContainsNoExternalOrControlSurface(
        string fileName,
        string expectedCulture,
        string safetyMessage)
    {
        var markup = ReadWebAssetText(fileName);

        Assert.Contains($"<html lang=\"{expectedCulture}\">", markup, StringComparison.Ordinal);
        Assert.Contains(safetyMessage, markup, StringComparison.Ordinal);
        Assert.Contains($"href=\"/?culture={expectedCulture}\"", markup, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'; style-src 'self'", markup, StringComparison.Ordinal);
        Assert.Contains("href=\"/offline.css\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api/", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MainPage_DeclaresIosStandaloneMetadataAndNonBlockingWorkerRegistration()
    {
        var markup = ReadWebAssetText("index.html");
        var style = ReadWebAssetText("app.css");
        var script = ReadWebAssetText("app.js");

        Assert.Contains("rel=\"manifest\" href=\"/manifest.webmanifest?culture=zh-TW\"", markup, StringComparison.Ordinal);
        Assert.Contains("rel=\"apple-touch-icon\" sizes=\"180x180\"", markup, StringComparison.Ordinal);
        Assert.Contains("apple-mobile-web-app-capable", markup, StringComparison.Ordinal);
        Assert.Contains("apple-mobile-web-app-title", markup, StringComparison.Ordinal);
        Assert.Contains("worker-src 'self'", markup, StringComparison.Ordinal);
        Assert.Contains("manifest-src 'self'", markup, StringComparison.Ordinal);
        Assert.Contains("env(safe-area-inset-top)", style, StringComparison.Ordinal);
        Assert.Contains("100dvh", style, StringComparison.Ordinal);
        Assert.Contains("navigator.serviceWorker.register(\"/service-worker.js\"", script, StringComparison.Ordinal);
        Assert.Contains("updateViaCache: \"none\"", script, StringComparison.Ordinal);
        Assert.Contains("window.navigator.standalone === true", script, StringComparison.Ordinal);
        Assert.Contains("searchParams.get(\"culture\")", script, StringComparison.Ordinal);
        Assert.Contains("searchParams.set(\"culture\", state.culture)", script, StringComparison.Ordinal);
        Assert.Contains("manifest.href = `/manifest.webmanifest?culture=${encodeURIComponent(state.culture)}`", script, StringComparison.Ordinal);
    }

    private static string ReadWebAssetText(string fileName)
        => System.Text.Encoding.UTF8.GetString(ReadWebAssetBytes(fileName));

    private static byte[] ReadWebAssetBytes(string fileName)
    {
        var assembly = typeof(RemoteControlHost).Assembly;
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith($".Web.{fileName}", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var memory = new MemoryStream();
        stream!.CopyTo(memory);
        return memory.ToArray();
    }
}

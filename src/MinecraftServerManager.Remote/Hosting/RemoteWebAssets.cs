using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MinecraftServerManager.Contracts.Localization;

namespace MinecraftServerManager.Remote;

/// <summary>
/// Serves the fixed mobile/PWA assets directly from this assembly. Keeping them embedded makes
/// the self-contained release genuinely portable and prevents a writable sidecar JavaScript file
/// from changing the privileged remote-control page after the EXE has been signed.
/// </summary>
internal static class RemoteWebAssets
{
    private static readonly IReadOnlyDictionary<string, WebAsset> Assets = LoadAssets();

    public static void MapEndpoints(WebApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.MapGet("/", () => CreateResult("index.html"));
        application.MapGet("/index.html", () => CreateResult("index.html"));
        application.MapGet("/app.css", () => CreateResult("app.css"));
        application.MapGet("/app.js", () => CreateResult("app.js"));
        application.MapGet("/manifest.webmanifest", (HttpContext context) =>
            CreateLocalizedResult(context, "manifest", "webmanifest"));
        application.MapGet("/offline.html", (HttpContext context) =>
            CreateLocalizedResult(context, "offline", "html"));
        application.MapGet("/offline.css", () => CreateResult("offline.css"));
        application.MapGet("/service-worker.js", (HttpContext context) =>
        {
            context.Response.Headers["Service-Worker-Allowed"] = "/";
            return CreateResult("service-worker.js");
        });
        application.MapGet("/icon-180.png", () => CreateResult("icon-180.png"));
        application.MapGet("/icon-192.png", () => CreateResult("icon-192.png"));
        application.MapGet("/icon-512.png", () => CreateResult("icon-512.png"));
        application.MapGet("/icon-maskable-512.png", () => CreateResult("icon-maskable-512.png"));
        application.MapGet("/localization/{culture}.json", (HttpContext context, string culture) =>
        {
            var normalized = ProductLocalizationCatalog.NormalizeCulture(culture);
            context.Response.Headers.ContentLanguage = normalized;
            context.Response.Headers.CacheControl = "no-store, max-age=0";
            return Results.Bytes(
                ProductLocalizationCatalog.GetJsonUtf8(normalized),
                "application/json; charset=utf-8");
        });
    }

    private static IResult CreateResult(string name)
    {
        var asset = Assets[name];
        return Results.Bytes(asset.Content, asset.ContentType);
    }

    private static IResult CreateLocalizedResult(HttpContext context, string stem, string extension)
    {
        var culture = ProductLocalizationCatalog.NormalizeCulture(context.Request.Query["culture"]);
        context.Response.Headers.ContentLanguage = culture;
        context.Response.Headers.CacheControl = "no-store, max-age=0";
        return CreateResult($"{stem}.{culture}.{extension}");
    }

    private static IReadOnlyDictionary<string, WebAsset> LoadAssets()
    {
        var assembly = typeof(RemoteWebAssets).Assembly;
        return new Dictionary<string, WebAsset>(StringComparer.Ordinal)
        {
            ["index.html"] = Load(assembly, "index.html", "text/html; charset=utf-8"),
            ["app.css"] = Load(assembly, "app.css", "text/css; charset=utf-8"),
            ["app.js"] = Load(assembly, "app.js", "text/javascript; charset=utf-8"),
            ["manifest.zh-TW.webmanifest"] = Load(assembly, "manifest.zh-TW.webmanifest", "application/manifest+json; charset=utf-8"),
            ["manifest.en-US.webmanifest"] = Load(assembly, "manifest.en-US.webmanifest", "application/manifest+json; charset=utf-8"),
            ["offline.zh-TW.html"] = Load(assembly, "offline.zh-TW.html", "text/html; charset=utf-8"),
            ["offline.en-US.html"] = Load(assembly, "offline.en-US.html", "text/html; charset=utf-8"),
            ["offline.css"] = Load(assembly, "offline.css", "text/css; charset=utf-8"),
            ["service-worker.js"] = Load(assembly, "service-worker.js", "text/javascript; charset=utf-8"),
            ["icon-180.png"] = Load(assembly, "icon-180.png", "image/png"),
            ["icon-192.png"] = Load(assembly, "icon-192.png", "image/png"),
            ["icon-512.png"] = Load(assembly, "icon-512.png", "image/png"),
            ["icon-maskable-512.png"] = Load(assembly, "icon-maskable-512.png", "image/png")
        };
    }

    private static WebAsset Load(Assembly assembly, string fileName, string contentType)
    {
        var suffix = $".Web.{fileName}";
        var matches = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(suffix, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException($"Embedded remote web asset is missing or ambiguous: {fileName}.");
        }

        using var stream = assembly.GetManifestResourceStream(matches[0])
            ?? throw new InvalidOperationException($"Unable to open embedded remote web asset: {fileName}.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        if (memory.Length is <= 0 or > 2 * 1024 * 1024)
        {
            throw new InvalidOperationException($"Embedded remote web asset has an unsafe size: {fileName}.");
        }

        return new WebAsset(memory.ToArray(), contentType);
    }

    private sealed record WebAsset(byte[] Content, string ContentType);
}

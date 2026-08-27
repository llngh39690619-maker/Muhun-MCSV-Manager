using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using MinecraftServerManager.Contracts.Localization;
using MinecraftServerManager.Remote;

namespace MinecraftServerManager.Remote.Tests;

public sealed class RemoteLocalizationContractTests
{
    [Fact]
    public void MainPage_LocalizationAttributesReferenceOnlyVersionedCatalogKeys()
    {
        var markup = ReadWebAsset("index.html");
        var matches = Regex.Matches(
            markup,
            "data-i18n(?:-placeholder|-aria-label)?=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        var referencedKeys = matches.Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(referencedKeys);
        Assert.All(referencedKeys, key => Assert.Contains(key, ProductLocalizationCatalog.Keys));
        foreach (var criticalKey in new[]
                 {
                     "web.auth.title",
                     "web.auth.username",
                     "web.auth.pin",
                     "web.server.readOnly",
                     "web.players.action",
                     "web.console.diagnostics",
                 })
        {
            Assert.Contains(criticalKey, referencedKeys);
        }
    }

    [Fact]
    public void Script_LoadsSameOriginVersionedCatalogAndHasNoHardcodedCjkRuntimeMessages()
    {
        var script = ReadWebAsset("app.js");

        Assert.Contains("const LOCALIZATION_SCHEMA_VERSION = 1;", script, StringComparison.Ordinal);
        Assert.Contains("/localization/${encodeURIComponent(culture)}.json", script, StringComparison.Ordinal);
        Assert.Contains("credentials: \"same-origin\"", script, StringComparison.Ordinal);
        Assert.Contains("cache: \"no-store\"", script, StringComparison.Ordinal);
        Assert.Contains("data-i18n-placeholder", ReadWebAsset("index.html"), StringComparison.Ordinal);
        Assert.Contains("applyLocalizationToDocument", script, StringComparison.Ordinal);
        Assert.Contains("normalizeCulture", script, StringComparison.Ordinal);
        Assert.Contains("Intl.Locale", script, StringComparison.Ordinal);
        Assert.Contains("t(\"web.error.default\")", script, StringComparison.Ordinal);
        Assert.Contains("t(\"web.error.forbidden\")", script, StringComparison.Ordinal);
        Assert.DoesNotMatch("[\\u3400-\\u9FFF]", script);
    }

    [Fact]
    public void LanguagePreference_StoresOnlyCanonicalCultureAndNeverCredentials()
    {
        var script = ReadWebAsset("app.js");

        Assert.Contains("const LANGUAGE_STORAGE_KEY = \"mcsv-language-v1\";", script, StringComparison.Ordinal);
        Assert.Contains("window.localStorage.setItem(LANGUAGE_STORAGE_KEY, culture)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage.setItem(\"username", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localStorage.setItem(\"pin", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localStorage.setItem(\"token", script, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("zh-TW", "帳號或密碼不正確。")]
    [InlineData("en-US", "The username or PIN is incorrect.")]
    [InlineData("fr-FR", "帳號或密碼不正確。")]
    public void RemoteApi_UsesCanonicalClientCultureForPublicProblemTitles(
        string requestedCulture,
        string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[RemoteControlOptions.CultureHeaderName] = requestedCulture;

        Assert.Equal(
            expected,
            RemoteApi.Localize(context, "web.api.credentialsInvalid"));
    }

    [Fact]
    public void Script_SendsSelectedCultureWithEveryApiRequest()
    {
        var script = ReadWebAsset("app.js");

        Assert.Contains(
            "headers.set(\"X-MCSV-Culture\", state.culture);",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteHost_PublicInfrastructureProblemsUseTheSharedCatalog()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src",
            "MinecraftServerManager.Remote",
            "Hosting",
            "RemoteControlHost.cs"));

        Assert.Contains("RemoteApi.Localize(rejected.HttpContext, \"web.api.rateLimited\")", source, StringComparison.Ordinal);
        Assert.Contains("RemoteApi.Localize(context, \"web.api.remoteUnavailable\")", source, StringComparison.Ordinal);
        Assert.Contains("RemoteApi.Localize(context, \"web.api.operationFailed\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Too many requests; retry later.\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Remote control is unavailable.\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"The remote-control operation failed.\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicProblemCatalog_ContainsEquivalentBilingualSurface()
    {
        var keys = ProductLocalizationCatalog.Keys
            .Where(key => key.StartsWith("web.api.", StringComparison.Ordinal))
            .ToArray();

        Assert.True(keys.Length >= 30);
        foreach (var key in keys)
        {
            var arguments = key == "web.api.commandTooLong"
                ? new object?[] { 512 }
                : [];
            var traditional = ProductLocalizationCatalog.Format("zh-TW", key, arguments);
            var english = ProductLocalizationCatalog.Format("en-US", key, arguments);
            Assert.False(string.IsNullOrWhiteSpace(traditional));
            Assert.False(string.IsNullOrWhiteSpace(english));
            Assert.NotEqual(traditional, english);
        }
    }

    private static string ReadWebAsset(string fileName)
    {
        var assembly = typeof(RemoteControlHost).Assembly;
        var resourceName = Assert.Single(
            assembly.GetManifestResourceNames(),
            name => name.EndsWith($".Web.{fileName}", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file: {Path.Combine(segments)}");
    }
}

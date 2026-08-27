using System.Text.Json;
using MinecraftServerManager.Contracts.Localization;

namespace MinecraftServerManager.Contracts.Tests;

public sealed class ProductLocalizationCatalogTests
{
    [Fact]
    public void Catalogs_HaveExactVersionedKeyAndPlaceholderParity()
    {
        var expected = ProductLocalizationCatalog.Keys.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(ProductLocalizationCatalog.Keys.Count, expected.Count);
        Assert.Equal(
            [ProductLocalizationCatalog.FallbackCulture, ProductLocalizationCatalog.EnglishCulture],
            ProductLocalizationCatalog.SupportedCultures);

        foreach (var culture in ProductLocalizationCatalog.SupportedCultures)
        {
            var document = ProductLocalizationCatalog.GetDocument(culture);
            Assert.Equal(ProductLocalizationCatalog.SchemaVersion, document.SchemaVersion);
            Assert.Equal(culture, document.Culture);
            Assert.Equal(expected, document.Strings.Keys.ToHashSet(StringComparer.Ordinal));
            Assert.All(document.Strings, item => Assert.False(string.IsNullOrWhiteSpace(item.Value)));

            using var json = JsonDocument.Parse(document.JsonUtf8);
            Assert.Equal(ProductLocalizationCatalog.SchemaVersion, json.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(culture, json.RootElement.GetProperty("Culture").GetString());
            Assert.Equal(expected.Count, json.RootElement.GetProperty("Strings").EnumerateObject().Count());
        }
    }

    [Theory]
    [InlineData("en", "en-US")]
    [InlineData("EN-us", "en-US")]
    [InlineData("en-GB", "en-US")]
    [InlineData("zh", "zh-TW")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-Hans-CN", "zh-TW")]
    public void NormalizeCulture_SupportedBcp47Tags_AreCanonicalized(string input, string expected)
    {
        Assert.True(ProductLocalizationCatalog.TryNormalizeCulture(input, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.Equal(expected, ProductLocalizationCatalog.NormalizeCulture(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("en_US")]
    [InlineData("not-a-language-tag")]
    [InlineData("fr-FR")]
    public void NormalizeCulture_InvalidOrUnsupportedTags_FailClosedToTraditionalChinese(string? input)
    {
        Assert.False(ProductLocalizationCatalog.TryNormalizeCulture(input, out var normalized));
        Assert.Equal(ProductLocalizationCatalog.FallbackCulture, normalized);
        Assert.Equal(ProductLocalizationCatalog.FallbackCulture, ProductLocalizationCatalog.NormalizeCulture(input));
    }

    [Fact]
    public void Format_UsesSelectedCultureAndEnforcesExactArguments()
    {
        Assert.Equal("作者：MCSV", ProductLocalizationCatalog.Format("zh-TW", "online.author", "MCSV"));
        Assert.Equal("By MCSV", ProductLocalizationCatalog.Format("en-GB", "online.author", "MCSV"));
        Assert.Equal("2h 3m", ProductLocalizationCatalog.Format("en-US", "web.time.hoursMinutes", 2, 3));

        Assert.Throws<FormatException>(() =>
            ProductLocalizationCatalog.Format("en-US", "web.time.hoursMinutes", 2));
        Assert.Throws<FormatException>(() =>
            ProductLocalizationCatalog.Format("en-US", "common.close", "unexpected"));
        Assert.Throws<KeyNotFoundException>(() =>
            ProductLocalizationCatalog.Format("en-US", "missing.key"));
    }

    [Fact]
    public void UnknownCulture_ReturnsFallbackDocumentWithoutReflectingInput()
    {
        var document = ProductLocalizationCatalog.GetDocument("../../attacker");

        Assert.Equal(ProductLocalizationCatalog.FallbackCulture, document.Culture);
        Assert.Equal("繁體中文", document.Strings["language.zh-TW"]);
    }
}

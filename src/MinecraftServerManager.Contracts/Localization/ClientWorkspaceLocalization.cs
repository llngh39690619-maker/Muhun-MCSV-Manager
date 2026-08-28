using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinecraftServerManager.Contracts.Localization;

/// <summary>
/// Versioned localization extension for the Minecraft client workspace. Keeping this vocabulary
/// separate from the shared Web catalog makes the desktop-only surface easier to audit while all
/// keys still participate in the product localization contract.
/// </summary>
internal static partial class ClientWorkspaceLocalization
{
    private static readonly IReadOnlyDictionary<string, ExtensionDocument> Documents = LoadDocuments();

    public static IReadOnlyList<string> Keys { get; } = Documents[ProductLocalizationCatalog.FallbackCulture]
        .Strings.Keys
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static readonly IReadOnlyDictionary<string, int> ParameterCounts =
        new ReadOnlyDictionary<string, int>(Documents[ProductLocalizationCatalog.FallbackCulture]
            .Strings
            .ToDictionary(
                pair => pair.Key,
                pair => GetPlaceholderCount(pair.Value),
                StringComparer.Ordinal));

    public static IReadOnlyDictionary<string, string> GetStrings(string culture) =>
        Documents[culture].Strings;

    public static int GetParameterCount(string key) =>
        ParameterCounts.TryGetValue(key, out var count) ? count : 0;

    private static IReadOnlyDictionary<string, ExtensionDocument> LoadDocuments()
    {
        var documents = new Dictionary<string, ExtensionDocument>(StringComparer.Ordinal);
        foreach (var culture in ProductLocalizationCatalog.SupportedCultures)
        {
            var assembly = typeof(ProductLocalizationCatalog).Assembly;
            var suffix = $".Localization.ClientWorkspace.{culture}.v{ProductLocalizationCatalog.SchemaVersion}.json";
            var resourceName = assembly.GetManifestResourceNames()
                .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Embedded client workspace localization extension is missing: {culture}.");
            using var stream = assembly.GetManifestResourceStream(resourceName)
                               ?? throw new InvalidOperationException(
                                   $"Unable to open client workspace localization extension: {culture}.");
            if (stream.Length is <= 0 or > 256 * 1024)
            {
                throw new InvalidOperationException(
                    $"Client workspace localization extension has an unsafe size: {culture}.");
            }

            var document = JsonSerializer.Deserialize<ExtensionDocument>(stream)
                           ?? throw new InvalidOperationException(
                               $"Client workspace localization extension is empty: {culture}.");
            if (document.SchemaVersion != ProductLocalizationCatalog.SchemaVersion
                || !culture.Equals(document.Culture, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Client workspace localization metadata is invalid: {culture}.");
            }

            documents.Add(culture, document);
        }

        var fallbackKeys = documents[ProductLocalizationCatalog.FallbackCulture]
            .Strings.Keys
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (culture, document) in documents)
        {
            var keys = document.Strings.Keys.ToHashSet(StringComparer.Ordinal);
            if (!fallbackKeys.SetEquals(keys))
            {
                throw new InvalidOperationException(
                    $"Client workspace localization key mismatch: {culture}.");
            }

            foreach (var (key, value) in document.Strings)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException(
                        $"Client workspace localization value is empty: {culture}/{key}.");
                }
            }
        }

        return new ReadOnlyDictionary<string, ExtensionDocument>(documents);
    }

    private static int GetPlaceholderCount(string value)
    {
        var indexes = PlaceholderRegex().Matches(value)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();
        return indexes.Length == 0 ? 0 : indexes.Max() + 1;
    }

    [GeneratedRegex(@"(?<!\{)\{(\d+)(?:,[^}:]+)?(?:\:[^}]+)?\}(?!\})", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    private sealed class ExtensionDocument
    {
        public int SchemaVersion { get; init; }
        public string Culture { get; init; } = string.Empty;
        public Dictionary<string, string> Strings { get; init; } = new(StringComparer.Ordinal);
    }
}

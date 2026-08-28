using System.Xml;
using System.Xml.Linq;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>Discovers stable NeoForge releases from the official NeoForged Maven repositories.</summary>
public sealed class NeoForgeLoaderCatalogProvider : IMinecraftLoaderCatalogProvider
{
    public static readonly Uri MetadataUri =
        new("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml");
    public static readonly Uri Legacy1201MetadataUri =
        new("https://maven.neoforged.net/releases/net/neoforged/forge/maven-metadata.xml");

    private const int MaximumVersions = 8_192;
    private const long MaximumCatalogBytes = 8L * 1024 * 1024;
    private static readonly IReadOnlySet<string> AllowedHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "maven.neoforged.net" };

    private readonly OfficialCatalogHttpReader _reader;

    public NeoForgeLoaderCatalogProvider(HttpClient httpClient, TimeSpan? requestTimeout = null)
    {
        _reader = new OfficialCatalogHttpReader(httpClient, requestTimeout);
    }

    public MinecraftClientLoader Loader => MinecraftClientLoader.NeoForge;

    public async Task<IReadOnlyList<MinecraftLoaderCatalogEntry>> GetVersionsAsync(
        MinecraftReleaseCatalogSnapshot stableMinecraftReleases,
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        if (!OfficialCatalogValidation.IsStableMinecraftRelease(stableMinecraftReleases, gameVersion) ||
            !IsSupportedMinecraftVersion(gameVersion))
        {
            return [];
        }

        var legacy = string.Equals(gameVersion, "1.20.1", StringComparison.Ordinal);
        var metadataUri = legacy ? Legacy1201MetadataUri : MetadataUri;
        var bytes = await _reader.GetAsync(
                metadataUri,
                AllowedHosts,
                MaximumCatalogBytes,
                cancellationToken)
            .ConfigureAwait(false);
        var versions = ParseMavenMetadata(bytes, legacy);

        var entries = new List<MinecraftLoaderCatalogEntry>();
        var matchingVersions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in versions)
        {
            if (!TryGetMinecraftVersion(version, legacy, out var compatibleGameVersion) ||
                !string.Equals(compatibleGameVersion, gameVersion, StringComparison.Ordinal))
            {
                continue;
            }

            if (!matchingVersions.Add(version))
            {
                throw new InvalidDataException($"NeoForge metadata contains duplicate version '{version}'.");
            }

            var artifactUri = CreateInstallerArtifactUri(gameVersion, version);
            entries.Add(new MinecraftLoaderCatalogEntry(
                Loader,
                gameVersion,
                version,
                MinecraftLoaderReleaseChannel.Stable,
                MinecraftClientLoaderInstallKind.Managed,
                metadataUri,
                artifactUri,
                legacy
                    ? "NeoForge 官方 1.20.1 legacy Forge artifact；官方建議此遊戲版本優先使用 Forge。"
                    : "NeoForge 官方穩定版；安裝前仍須驗證 Maven checksum sidecar。"));
        }

        entries.Sort(static (left, right) =>
            LoaderVersionComparer.Instance.Compare(right.Version, left.Version));
        return entries;
    }

    internal static Uri CreateInstallerArtifactUri(string gameVersion, string loaderVersion)
    {
        OfficialCatalogValidation.ValidateVersionToken(
            gameVersion,
            "Minecraft version",
            maximumLength: 64);
        OfficialCatalogValidation.ValidateVersionToken(
            loaderVersion,
            "NeoForge version",
            maximumLength: 128);
        if (!IsSupportedMinecraftVersion(gameVersion))
        {
            throw new InvalidDataException("NeoForge does not support the selected Minecraft version.");
        }

        var legacy = string.Equals(gameVersion, "1.20.1", StringComparison.Ordinal);
        if (!IsStableNeoForgeVersion(loaderVersion, legacy) ||
            !TryGetMinecraftVersion(loaderVersion, legacy, out var compatibleGameVersion) ||
            !string.Equals(compatibleGameVersion, gameVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The NeoForge version is not a stable release for the selected Minecraft version.");
        }

        var artifactName = legacy ? "forge" : "neoforge";
        return new Uri(
            $"https://maven.neoforged.net/releases/net/neoforged/{artifactName}/{loaderVersion}/{artifactName}-{loaderVersion}-installer.jar");
    }

    private static IReadOnlyList<string> ParseMavenMetadata(byte[] bytes, bool legacy)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumCatalogBytes,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        using var input = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(input, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root;
        if (root is null || root.Name.LocalName != "metadata")
        {
            throw new InvalidDataException("NeoForge Maven metadata root is invalid.");
        }

        var expectedArtifactId = legacy ? "forge" : "neoforge";
        if (RequireSingleChildValue(root, "groupId") != "net.neoforged" ||
            RequireSingleChildValue(root, "artifactId") != expectedArtifactId)
        {
            throw new InvalidDataException("NeoForge Maven coordinate is invalid.");
        }

        var versioning = RequireSingleChild(root, "versioning");
        var versionContainer = RequireSingleChild(versioning, "versions");
        var versionElements = versionContainer.Elements()
            .Where(element => element.Name.LocalName == "version")
            .ToList();
        if (versionElements.Count > MaximumVersions)
        {
            throw new InvalidDataException("NeoForge Maven metadata contains too many versions.");
        }

        var result = new List<string>(versionElements.Count);
        foreach (var element in versionElements)
        {
            var version = element.Value.Trim();
            OfficialCatalogValidation.ValidateVersionToken(version, "NeoForge version");
            if (IsStableNeoForgeVersion(version, legacy))
            {
                result.Add(version);
            }
        }

        return result;
    }

    private static bool IsStableNeoForgeVersion(string version, bool legacy)
    {
        if (legacy)
        {
            const string prefix = "1.20.1-";
            return version.StartsWith(prefix, StringComparison.Ordinal) &&
                   OfficialCatalogValidation.IsStrictStableNumericVersion(
                       version[prefix.Length..],
                       3,
                       3);
        }

        var firstDot = version.IndexOf('.');
        if (firstDot <= 0 ||
            !int.TryParse(
                version.AsSpan(0, firstDot),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var firstPart))
        {
            return false;
        }

        return firstPart >= 26
            ? OfficialCatalogValidation.IsStrictStableNumericVersion(version, 4, 4)
            : OfficialCatalogValidation.IsStrictStableNumericVersion(version, 3, 3);
    }

    private static bool TryGetMinecraftVersion(
        string neoForgeVersion,
        bool legacy,
        out string minecraftVersion)
    {
        if (legacy)
        {
            minecraftVersion = "1.20.1";
            return neoForgeVersion.StartsWith("1.20.1-", StringComparison.Ordinal);
        }

        var parts = neoForgeVersion.Split('.', StringSplitOptions.None);
        if (parts.Length < 3 ||
            !int.TryParse(parts[0], out var first) ||
            !int.TryParse(parts[1], out var second))
        {
            minecraftVersion = string.Empty;
            return false;
        }

        if (first >= 26)
        {
            if (parts.Length != 4 || !int.TryParse(parts[2], out var patch))
            {
                minecraftVersion = string.Empty;
                return false;
            }

            minecraftVersion = patch == 0
                ? $"{first}.{second}"
                : $"{first}.{second}.{patch}";
            return true;
        }

        minecraftVersion = second == 0
            ? $"1.{first}"
            : $"1.{first}.{second}";
        return true;
    }

    private static bool IsSupportedMinecraftVersion(string gameVersion)
    {
        if (string.Equals(gameVersion, "1.20.1", StringComparison.Ordinal))
        {
            return true;
        }

        var parts = gameVersion.Split('.', StringSplitOptions.None);
        if (parts.Length is < 2 or > 3 ||
            !int.TryParse(parts[0], out var first) ||
            !int.TryParse(parts[1], out var second))
        {
            return false;
        }

        if (first >= 26)
        {
            return true;
        }

        var patch = parts.Length == 3 && int.TryParse(parts[2], out var parsedPatch)
            ? parsedPatch
            : 0;
        return first == 1 && (second > 20 || second == 20 && patch >= 2);
    }

    private static XElement RequireSingleChild(XElement parent, string localName)
    {
        var matches = parent.Elements()
            .Where(element => element.Name.LocalName == localName)
            .Take(2)
            .ToList();
        if (matches.Count != 1)
        {
            throw new InvalidDataException($"NeoForge Maven metadata element '{localName}' is invalid.");
        }

        return matches[0];
    }

    private static string RequireSingleChildValue(XElement parent, string localName)
    {
        var value = RequireSingleChild(parent, localName).Value.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new InvalidDataException($"NeoForge Maven metadata value '{localName}' is invalid.");
        }

        return value;
    }
}

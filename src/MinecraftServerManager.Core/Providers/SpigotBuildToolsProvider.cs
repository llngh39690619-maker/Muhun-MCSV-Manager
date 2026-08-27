using System.Net;
using System.Net.Http.Headers;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Providers;

/// <summary>
/// Resolves official Spigot BuildTools plans. It never distributes Spigot/CraftBukkit binaries:
/// the reviewed BuildTools JAR is downloaded from Spigot Jenkins and builds an exact revision in
/// a fresh local workspace. Newer definitions are verified against their official output SHA-256;
/// historical definitions are accepted only through the stricter official-source-refs recipe.
/// </summary>
public sealed partial class SpigotBuildToolsProvider
{
    private const int MaximumSupportedToolsVersion = 197;
    private static readonly Uri VersionsBase = new("https://hub.spigotmc.org/versions/");

    public static SpigotBuildToolsArtifactInfo ReviewedBuildTools { get; } = new(
        200,
        "4ebb05d007acda41af924e4cc8075b385e69c5f1",
        new Uri("https://hub.spigotmc.org/jenkins/job/BuildTools/200/api/json"),
        new Uri("https://hub.spigotmc.org/jenkins/job/BuildTools/200/artifact/target/BuildTools.jar"),
        "BuildTools.jar",
        3_606_248,
        "b61fa90158f594ee95bea1a27399eb64d439b4c8ae9345bd4476a02ce49b06ff");

    private readonly HttpClient _metadataClient;
    private readonly HttpClient _artifactClient;
    private readonly ConcurrentDictionary<string, VersionMetadata> _metadataCache =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _metadataGates =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private IReadOnlyList<SpigotBuildToolsVersionInfo>? _versionCache;

    public SpigotBuildToolsProvider(
        HttpClient metadataClient,
        HttpClient artifactClient,
        string userAgent)
    {
        ArgumentNullException.ThrowIfNull(metadataClient);
        ArgumentNullException.ThrowIfNull(artifactClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        if (userAgent.Contains('\r') || userAgent.Contains('\n'))
        {
            throw new ArgumentException("User-Agent 不得包含換行。", nameof(userAgent));
        }

        _metadataClient = metadataClient;
        _artifactClient = artifactClient;
        ConfigureClient(_metadataClient, userAgent, "application/json");
        ConfigureClient(_artifactClient, userAgent, "application/java-archive");
    }

    public async Task<IReadOnlyList<SpigotBuildToolsVersionInfo>> GetVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_versionCache is not null)
        {
            return _versionCache;
        }

        await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_versionCache is not null)
            {
                return _versionCache;
            }

            var candidates = await GetOfficialReleaseVersionNamesAsync(cancellationToken)
                .ConfigureAwait(false);
            using var gate = new SemaphoreSlim(8);
            var tasks = candidates.Select(async version =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var metadata = await GetVersionMetadataAsync(version, cancellationToken)
                        .ConfigureAwait(false);
                    return new SpigotBuildToolsVersionInfo(
                        version,
                        metadata.JavaMajorVersion,
                        metadata.IsSupported,
                        metadata.UnsupportedReason,
                        metadata.VerificationKind);
                }
                finally
                {
                    gate.Release();
                }
            });
            var versions = await Task.WhenAll(tasks).ConfigureAwait(false);
            _versionCache = versions
                .Where(version => version.IsSupported)
                .OrderByDescending(version => ParseVersionKey(version.MinecraftVersion))
                .ToArray();
            return _versionCache;
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private async Task<IReadOnlyList<string>> GetOfficialReleaseVersionNamesAsync(
        CancellationToken cancellationToken)
    {
        var bytes = await FirstPartyArtifactHttp.GetBoundedBytesAsync(
                _metadataClient,
                VersionsBase,
                1024 * 1024,
                IsSpigotVersionUri,
                "Spigot official versions index",
                cancellationToken)
            .ConfigureAwait(false);
        string html;
        try
        {
            html = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Spigot versions index 不是有效 UTF-8。", exception);
        }

        var versions = VersionIndexLinkRegex()
            .Matches(html)
            .Select(match => match.Groups["version"].Value)
            .Where(IsSupportedStableVersionRange)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (versions.Length is < 1 or > 128)
        {
            throw new InvalidDataException("Spigot versions index 的正式版本數量不在安全範圍內。");
        }

        return versions;
    }

    public async Task<SpigotBuildPlanResolution> ResolvePlanAsync(
        CoreType coreType,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        if (coreType is not (CoreType.Spigot or CoreType.CraftBukkit))
        {
            throw new ArgumentOutOfRangeException(
                nameof(coreType),
                "BuildTools 只支援 Spigot 或 CraftBukkit (Bukkit)。");
        }

        ValidateMinecraftVersion(minecraftVersion);
        VersionMetadata aliasMetadata;
        try
        {
            aliasMetadata = await FetchVersionMetadataAsync(minecraftVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return new SpigotBuildPlanResolution(
                null,
                $"Spigot 官方 versions index 沒有 Minecraft {minecraftVersion} 的精確定義。");
        }

        VersionMetadata metadata;
        string buildRevision;
        if (aliasMetadata.VerificationKind ==
                SpigotBuildOutputVerificationKind.OfficialSourceRefs
            && aliasMetadata.VersionIdentity.Equals(minecraftVersion, StringComparison.Ordinal))
        {
            // The original 1.8 definition names itself "1.8" and never had a separate identity
            // document. The freshly fetched alias plus post-run ref equality remains the binding.
            metadata = aliasMetadata;
            buildRevision = minecraftVersion;
        }
        else
        {
            ValidateVersionIdentity(aliasMetadata.VersionIdentity);
            try
            {
                metadata = await FetchVersionIdentityMetadataAsync(
                        aliasMetadata.VersionIdentity,
                        cancellationToken)
                    .ConfigureAwait(false);
                EnsureEquivalentVersionMetadata(minecraftVersion, aliasMetadata, metadata);
                buildRevision = aliasMetadata.VersionIdentity;
            }
            catch (HttpRequestException exception) when (
                exception.StatusCode == HttpStatusCode.NotFound)
            {
                if (aliasMetadata.VerificationKind ==
                    SpigotBuildOutputVerificationKind.OfficialOutputSha256)
                {
                    throw new InvalidDataException(
                        $"Spigot 不可變版本定義 {aliasMetadata.VersionIdentity}.json 不存在。",
                        exception);
                }

                // Several historical aliases (for example 1.8.8 -> 582b) have no matching identity
                // endpoint. Build the freshly read alias itself and rely on the pre-pinned plus
                // post-run four-ref checks to detect an alias move during BuildTools execution.
                metadata = aliasMetadata;
                buildRevision = minecraftVersion;
            }
        }

        if (!metadata.IsSupported)
        {
            return new SpigotBuildPlanResolution(null, aliasMetadata.UnsupportedReason);
        }

        var outputSha256 = coreType == CoreType.Spigot
            ? metadata.SpigotSha256
            : metadata.CraftBukkitSha256;
        var displayName = coreType == CoreType.Spigot ? "Spigot" : "CraftBukkit (Bukkit)";
        return new SpigotBuildPlanResolution(
            new SpigotBuildPlan(
                coreType,
                displayName,
                minecraftVersion,
                metadata.JavaMajorVersion!.Value,
                "server.jar",
                outputSha256,
                metadata.ToolsVersion!.Value,
                metadata.VersionIdentity,
                metadata.SourceRefs,
                ReviewedBuildTools,
                metadata.VerificationKind,
                buildRevision),
            null);
    }

    public async Task<string> DownloadReviewedBuildToolsAsync(
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await VerifyReviewedBuildToolsMetadataAsync(cancellationToken).ConfigureAwait(false);
        return await FirstPartyArtifactHttp.DownloadVerifiedSha256Async(
                _artifactClient,
                ReviewedBuildTools.DownloadUri,
                destinationPath,
                ReviewedBuildTools.Sha256,
                ReviewedBuildTools.Size,
                IsSpigotArtifactUri,
                static (_, _) => false,
                expectedFileName: null,
                "Spigot BuildTools.jar",
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<VersionMetadata> GetVersionMetadataAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        ValidateMinecraftVersion(minecraftVersion);
        if (_metadataCache.TryGetValue(minecraftVersion, out var cached))
        {
            return cached;
        }

        var gate = _metadataGates.GetOrAdd(minecraftVersion, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_metadataCache.TryGetValue(minecraftVersion, out cached))
            {
                return cached;
            }

            var metadata = await FetchVersionMetadataAsync(
                    minecraftVersion,
                    cancellationToken)
                .ConfigureAwait(false);
            _metadataCache.TryAdd(minecraftVersion, metadata);
            return metadata;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<VersionMetadata> FetchVersionMetadataAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        ValidateMinecraftVersion(minecraftVersion);
        var uri = new Uri(VersionsBase, minecraftVersion + ".json");
        return await FetchVersionMetadataAsync(
                uri,
                IsSpigotVersionUri,
                minecraftVersion,
                $"Spigot {minecraftVersion} version JSON",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<VersionMetadata> FetchVersionIdentityMetadataAsync(
        string versionIdentity,
        CancellationToken cancellationToken)
    {
        ValidateVersionIdentity(versionIdentity);
        var uri = new Uri(VersionsBase, versionIdentity + ".json");
        return await FetchVersionMetadataAsync(
                uri,
                IsSpigotVersionIdentityUri,
                versionIdentity,
                $"Spigot immutable version {versionIdentity} JSON",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<VersionMetadata> FetchVersionMetadataAsync(
        Uri uri,
        Func<Uri, bool> uriPolicy,
        string versionLabel,
        string context,
        CancellationToken cancellationToken)
    {
        var bytes = await FirstPartyArtifactHttp.GetBoundedBytesAsync(
                _metadataClient,
                uri,
                256 * 1024,
                uriPolicy,
                context,
                cancellationToken)
            .ConfigureAwait(false);
        using var document = ParseJson(bytes, context);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Spigot version JSON root 必須是物件。");
        }

        var identity = ReadRequiredToken(root, "name", 64);
        var refsObject = ReadRequiredObject(root, "refs");
        var mutableRefs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "BuildData", "Bukkit", "CraftBukkit", "Spigot" })
        {
            var value = ReadRequiredToken(refsObject, name, 40);
            ValidateHex(value, 40, $"Spigot ref {name}");
            mutableRefs.Add(name, value.ToLowerInvariant());
        }

        var refs = new ReadOnlyDictionary<string, string>(mutableRefs);

        var declaredToolsVersion = TryReadInt(root, "toolsVersion", 1, 10_000);
        var javaClassVersions = TryReadJavaClassVersions(root);
        var toolsVersion = declaredToolsVersion ?? 1;
        int? javaMajor = javaClassVersions is null
            ? declaredToolsVersion is null or <= 47 ? 8 : null
            : javaClassVersions.Min() - 44;
        string? craftBukkitHash = null;
        string? spigotHash = null;
        if (root.TryGetProperty("hashes", out var hashes)
            && hashes.ValueKind == JsonValueKind.Object)
        {
            craftBukkitHash = ReadOptionalSha256(hashes, "CraftBukkit");
            spigotHash = ReadOptionalSha256(hashes, "Spigot");
        }

        var hasCraftHash = craftBukkitHash is not null;
        var hasSpigotHash = spigotHash is not null;
        var verificationKind = hasCraftHash && hasSpigotHash
            ? SpigotBuildOutputVerificationKind.OfficialOutputSha256
            : SpigotBuildOutputVerificationKind.OfficialSourceRefs;
        var unsupported = javaMajor is null
            ? $"Spigot 官方 {versionLabel}.json 無法解析安全的 Java 建置版本。"
            : toolsVersion > MaximumSupportedToolsVersion
            ? $"Minecraft {versionLabel} 需要 BuildTools schema {toolsVersion}，"
                + $"高於已審查工具的 {MaximumSupportedToolsVersion}。"
            : hasCraftHash != hasSpigotHash
                ? $"Spigot 官方 {versionLabel}.json 只提供單一 output SHA-256，"
                    + "無法建立完整的 Spigot／CraftBukkit 驗證契約。"
                : null;
        return new VersionMetadata(
            identity,
            refs,
            toolsVersion,
            javaMajor,
            javaClassVersions,
            craftBukkitHash,
            spigotHash,
            unsupported,
            verificationKind);
    }

    private static void EnsureEquivalentVersionMetadata(
        string minecraftVersion,
        VersionMetadata alias,
        VersionMetadata immutable)
    {
        EnsureEquivalentField(
            minecraftVersion,
            alias.VersionIdentity.Equals(immutable.VersionIdentity, StringComparison.Ordinal),
            "name");
        foreach (var name in new[] { "BuildData", "Bukkit", "CraftBukkit", "Spigot" })
        {
            EnsureEquivalentField(
                minecraftVersion,
                alias.SourceRefs.TryGetValue(name, out var aliasRef)
                    && immutable.SourceRefs.TryGetValue(name, out var immutableRef)
                    && aliasRef.Equals(immutableRef, StringComparison.Ordinal),
                $"refs.{name}");
        }

        EnsureEquivalentField(
            minecraftVersion,
            string.Equals(
                alias.CraftBukkitSha256,
                immutable.CraftBukkitSha256,
                StringComparison.Ordinal),
            "hashes.CraftBukkit");
        EnsureEquivalentField(
            minecraftVersion,
            string.Equals(alias.SpigotSha256, immutable.SpigotSha256, StringComparison.Ordinal),
            "hashes.Spigot");
        EnsureEquivalentField(
            minecraftVersion,
            alias.ToolsVersion == immutable.ToolsVersion,
            "toolsVersion");
        EnsureEquivalentField(
            minecraftVersion,
            alias.JavaClassVersions is null
                ? immutable.JavaClassVersions is null
                : immutable.JavaClassVersions is not null
                    && alias.JavaClassVersions.SequenceEqual(immutable.JavaClassVersions),
            "javaVersions");
    }

    private static void EnsureEquivalentField(
        string minecraftVersion,
        bool isEquivalent,
        string field)
    {
        if (!isEquivalent)
        {
            throw new InvalidDataException(
                $"Spigot 版本別名 {minecraftVersion} 與不可變版本定義的 {field} 不一致。");
        }
    }

    private async Task VerifyReviewedBuildToolsMetadataAsync(CancellationToken cancellationToken)
    {
        var bytes = await FirstPartyArtifactHttp.GetBoundedBytesAsync(
                _metadataClient,
                ReviewedBuildTools.MetadataUri,
                4L * 1024 * 1024,
                IsSpigotJenkinsApiUri,
                "Spigot BuildTools Jenkins metadata",
                cancellationToken)
            .ConfigureAwait(false);
        using var document = ParseJson(bytes, "Spigot BuildTools Jenkins metadata");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || ReadRequiredInt(root, "number", 1, int.MaxValue) != ReviewedBuildTools.BuildNumber
            || ReadRequiredBoolean(root, "building")
            || ReadRequiredBoolean(root, "inProgress")
            || !ReadRequiredToken(root, "result", 32).Equals("SUCCESS", StringComparison.Ordinal))
        {
            throw new InvalidDataException("BuildTools Jenkins build identity/status 不符。");
        }

        var commitCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("actions", out var actions)
            || actions.ValueKind != JsonValueKind.Array
            || actions.GetArrayLength() > 128)
        {
            throw new InvalidDataException("BuildTools Jenkins actions schema 無效。");
        }

        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind == JsonValueKind.Object
                && action.TryGetProperty("lastBuiltRevision", out var revision)
                && revision.ValueKind == JsonValueKind.Object
                && revision.TryGetProperty("SHA1", out var sha)
                && sha.ValueKind == JsonValueKind.String)
            {
                var value = sha.GetString() ?? string.Empty;
                ValidateHex(value, 40, "BuildTools Jenkins source revision");
                commitCandidates.Add(value);
            }
        }

        if (commitCandidates.Count != 1
            || !commitCandidates.Single().Equals(
                ReviewedBuildTools.SourceCommit,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("BuildTools Jenkins source commit 不符。");
        }

        if (!root.TryGetProperty("artifacts", out var artifacts)
            || artifacts.ValueKind != JsonValueKind.Array
            || artifacts.GetArrayLength() > 32)
        {
            throw new InvalidDataException("BuildTools Jenkins artifacts schema 無效。");
        }

        var matching = artifacts.EnumerateArray().Count(artifact =>
            artifact.ValueKind == JsonValueKind.Object
            && TryReadString(artifact, "fileName", out var fileName)
            && fileName.Equals(ReviewedBuildTools.FileName, StringComparison.Ordinal)
            && TryReadString(artifact, "relativePath", out var relativePath)
            && relativePath.Equals("target/BuildTools.jar", StringComparison.Ordinal));
        if (matching != 1)
        {
            throw new InvalidDataException("BuildTools.jar Jenkins artifact identity 不符。");
        }
    }

    internal static bool IsSpigotVersionUri(Uri uri) =>
        IsSpigotHost(uri)
        && (uri.AbsolutePath.Equals("/versions/", StringComparison.Ordinal)
            || (VersionPathRegex().Match(uri.AbsolutePath) is { Success: true } match
                && IsSupportedStableVersionRange(match.Groups["version"].Value)))
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);

    internal static bool IsSpigotVersionIdentityUri(Uri uri) =>
        IsSpigotHost(uri)
        && VersionIdentityPathRegex().IsMatch(uri.AbsolutePath)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);

    internal static bool IsSpigotJenkinsApiUri(Uri uri) =>
        IsSpigotHost(uri)
        && uri.AbsolutePath.Equals(
            "/jenkins/job/BuildTools/200/api/json",
            StringComparison.Ordinal)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);

    internal static bool IsSpigotArtifactUri(Uri uri) =>
        IsSpigotHost(uri)
        && uri.AbsolutePath.Equals(
            "/jenkins/job/BuildTools/200/artifact/target/BuildTools.jar",
            StringComparison.Ordinal)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);

    private static bool IsSpigotHost(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals("hub.spigotmc.org", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo);

    private static JsonDocument ParseJson(byte[] bytes, string context)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{context} JSON 無效。", exception);
        }
    }

    private static JsonElement ReadRequiredObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Spigot metadata 缺少 {name} 物件。");
        }

        return value;
    }

    private static string ReadRequiredToken(JsonElement parent, string name, int maximumLength)
    {
        if (!TryReadString(parent, name, out var value)
            || string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(character => char.IsControl(character)))
        {
            throw new InvalidDataException($"Spigot metadata 的 {name} 無效。");
        }

        return value;
    }

    private static string? ReadOptionalSha256(JsonElement parent, string name)
    {
        if (!TryReadString(parent, name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        ValidateHex(value, 64, $"Spigot {name} output SHA-256");
        return value.ToLowerInvariant();
    }

    private static bool TryReadString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static int ReadRequiredInt(JsonElement parent, string name, int minimum, int maximum)
    {
        if (!parent.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidDataException($"Spigot metadata 的 {name} 無效。");
        }

        return value;
    }

    private static int? TryReadInt(JsonElement parent, string name, int minimum, int maximum)
    {
        if (!parent.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidDataException($"Spigot metadata 的 {name} 無效。");
        }

        return value;
    }

    private static bool ReadRequiredBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"Spigot metadata 的 {name} 無效。");
        }

        return property.GetBoolean();
    }

    private static IReadOnlyList<int>? TryReadJavaClassVersions(JsonElement root)
    {
        if (!root.TryGetProperty("javaVersions", out var versions))
        {
            return null;
        }

        if (versions.ValueKind != JsonValueKind.Array
            || versions.GetArrayLength() is < 1 or > 8)
        {
            throw new InvalidDataException("Spigot javaVersions schema 無效。");
        }

        var classMajors = new List<int>();
        foreach (var item in versions.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number
                || !item.TryGetInt32(out var classMajor)
                || classMajor is < 52 or > 100)
            {
                throw new InvalidDataException("Spigot javaVersions 內容無效。");
            }

            classMajors.Add(classMajor);
        }

        return classMajors.AsReadOnly();
    }

    private static void ValidateHex(string value, int length, string context)
    {
        if (value.Length != length || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{context} 格式無效。");
        }
    }

    internal static void ValidateMinecraftVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!MinecraftVersionRegex().IsMatch(value)
            || !IsSupportedStableVersionRange(value))
        {
            throw new ArgumentException("Minecraft version 格式無效。", nameof(value));
        }
    }

    private static void ValidateVersionIdentity(string value)
    {
        if (!VersionIdentityRegex().IsMatch(value))
        {
            throw new InvalidDataException("Spigot version identity 格式無效。");
        }
    }

    private static bool IsSupportedStableVersionRange(string value)
    {
        var parts = value.Split('.');
        var patch = 0;
        if (parts.Length is < 2 or > 3
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor)
            || (parts.Length == 3 && !int.TryParse(parts[2], out patch)))
        {
            return false;
        }

        return major switch
        {
            1 => minor is >= 0 and <= 99 && patch is >= 0 and <= 99,
            26 => minor is >= 1 and <= 2 && patch is >= 0 and <= 99,
            _ => false
        };
    }

    private static void ConfigureClient(HttpClient client, string userAgent, string accept)
    {
        if (!client.DefaultRequestHeaders.UserAgent.Any())
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent.Trim());
        }

        if (!client.DefaultRequestHeaders.Accept.Any())
        {
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        }
    }

    private static long ParseVersionKey(string version)
    {
        var parts = version.Split('.').Select(int.Parse).ToArray();
        return parts[0] * 1_000_000L + parts[1] * 1_000L + (parts.Length > 2 ? parts[2] : 0);
    }

    [GeneratedRegex(@"^\d{1,2}\.\d{1,2}(?:\.\d{1,2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex MinecraftVersionRegex();

    [GeneratedRegex(
        @"^/versions/(?<version>\d{1,2}\.\d{1,2}(?:\.\d{1,2})?)\.json$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPathRegex();

    [GeneratedRegex(
        @"^/versions/[1-9][0-9A-Za-z-]{0,63}\.json$",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionIdentityPathRegex();

    [GeneratedRegex(@"^[1-9][0-9A-Za-z-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionIdentityRegex();

    [GeneratedRegex(
        """href=["'](?<version>\d{1,2}\.\d{1,2}(?:\.\d{1,2})?)\.json["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionIndexLinkRegex();

    private sealed record VersionMetadata(
        string VersionIdentity,
        IReadOnlyDictionary<string, string> SourceRefs,
        int? ToolsVersion,
        int? JavaMajorVersion,
        IReadOnlyList<int>? JavaClassVersions,
        string? CraftBukkitSha256,
        string? SpigotSha256,
        string? UnsupportedReason,
        SpigotBuildOutputVerificationKind VerificationKind)
    {
        public bool IsSupported =>
            ToolsVersion is not null
            && JavaMajorVersion is not null
            && string.IsNullOrWhiteSpace(UnsupportedReason);
    }
}

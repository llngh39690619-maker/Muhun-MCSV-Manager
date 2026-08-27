using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Providers;

/// <summary>
/// Reads first-party server product catalogs. This class only discovers releases and artifacts;
/// verified downloads and Java-installer execution remain the responsibility of the existing
/// PaperDownloadProvider and ModrinthOfficialLoaderArtifactProvider/bootstrapper pipeline.
/// </summary>
public sealed partial class OfficialServerCoreCatalogProvider
{
    private static readonly Uri PaperProjectUri = new("https://fill.papermc.io/v3/projects/paper");
    private static readonly Uri VelocityProjectUri = new("https://fill.papermc.io/v3/projects/velocity");
    private static readonly Uri MojangManifestUri = new(
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
    private static readonly Uri FabricGameVersionsUri = new("https://meta.fabricmc.net/v2/versions/game");
    private static readonly Uri FabricInstallerVersionsUri = new(
        "https://meta.fabricmc.net/v2/versions/installer");
    private static readonly Uri ForgeMetadataUri = new(
        "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml");
    private static readonly Uri NeoForgeMetadataUri = new(
        "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml");

    private const long MaximumJsonBytes = 16L * 1024 * 1024;
    private const long MaximumXmlBytes = 16L * 1024 * 1024;
    private const int MaximumCatalogEntries = 20_000;
    private const int MaximumConcurrentMetadataRequests = 8;
    private const long MaximumServerArtifactBytes = 2L * 1024 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly string _userAgent;
    private readonly JavaVersionRecommendationService _javaRecommendations = new();
    private readonly ConcurrentDictionary<CoreType, IReadOnlyList<OfficialServerCoreVersionInfo>>
        _versionCache = new();
    private readonly ConcurrentDictionary<(CoreType CoreType, string Version),
        IReadOnlyList<OfficialServerCoreBuildInfo>> _buildCache = new();
    private readonly ConcurrentDictionary<CoreType, SemaphoreSlim> _versionCacheGates = new();
    private readonly ConcurrentDictionary<(CoreType CoreType, string Version), SemaphoreSlim>
        _buildCacheGates = new();

    public OfficialServerCoreCatalogProvider(HttpClient httpClient, string userAgent)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        if (userAgent.Length > 512 || userAgent.Any(char.IsControl))
        {
            throw new ArgumentException("User-Agent 格式無效。", nameof(userAgent));
        }

        // Parse without retaining the temporary headers. Paper's contact requirement belongs to
        // the App integration because the Core library cannot invent a maintainer address.
        using var probe = new HttpRequestMessage();
        try
        {
            probe.Headers.UserAgent.ParseAdd(userAgent);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("User-Agent 格式無效。", nameof(userAgent), exception);
        }

        _userAgent = userAgent;
    }

    public static IReadOnlyList<OfficialServerCoreDescriptor> SupportedCores { get; } =
        Array.AsReadOnly<OfficialServerCoreDescriptor>(
        [
            new(CoreType.Paper, "Paper", false),
            new(CoreType.Velocity, "Velocity", true),
            new(CoreType.Vanilla, "Minecraft 原版", false),
            new(CoreType.Fabric, "Fabric", false),
            new(CoreType.Forge, "Forge", false),
            new(CoreType.NeoForge, "NeoForge", false)
        ]);

    public async Task<IReadOnlyList<OfficialServerCoreVersionInfo>> GetVersionsAsync(
        CoreType coreType,
        CancellationToken cancellationToken = default)
    {
        EnsureSupportedCore(coreType);
        if (_versionCache.TryGetValue(coreType, out var cached))
        {
            return cached;
        }

        var gate = _versionCacheGates.GetOrAdd(coreType, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_versionCache.TryGetValue(coreType, out cached))
            {
                return cached;
            }

            var discovered = await GetVersionsUncachedAsync(coreType, cancellationToken)
                .ConfigureAwait(false);
            _versionCache[coreType] = discovered;
            return discovered;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<OfficialServerCoreBuildInfo>> GetBuildsAsync(
        CoreType coreType,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestedVersion(minecraftVersion);
        EnsureSupportedCore(coreType);
        var key = (coreType, minecraftVersion);
        if (_buildCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var gate = _buildCacheGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_buildCache.TryGetValue(key, out cached))
            {
                return cached;
            }

            var discovered = await GetBuildsUncachedAsync(coreType, minecraftVersion, cancellationToken)
                .ConfigureAwait(false);
            _buildCache[key] = discovered;
            return discovered;
        }
        finally
        {
            gate.Release();
        }
    }

    private Task<IReadOnlyList<OfficialServerCoreVersionInfo>> GetVersionsUncachedAsync(
        CoreType coreType,
        CancellationToken cancellationToken)
        => coreType switch
        {
            CoreType.Paper => GetFillVersionsAsync(
                CoreType.Paper,
                PaperProjectUri,
                cancellationToken),
            CoreType.Velocity => GetFillVersionsAsync(
                CoreType.Velocity,
                VelocityProjectUri,
                cancellationToken),
            CoreType.Vanilla => GetVanillaVersionsAsync(cancellationToken),
            CoreType.Fabric => GetFabricVersionsAsync(cancellationToken),
            CoreType.Forge => GetForgeVersionsAsync(cancellationToken),
            CoreType.NeoForge => GetNeoForgeVersionsAsync(cancellationToken),
            _ => throw UnsupportedCore(coreType)
        };

    private Task<IReadOnlyList<OfficialServerCoreBuildInfo>> GetBuildsUncachedAsync(
        CoreType coreType,
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        return coreType switch
        {
            CoreType.Paper => GetFillBuildsAsync(
                CoreType.Paper,
                "paper",
                minecraftVersion,
                cancellationToken),
            CoreType.Velocity => GetFillBuildsAsync(
                CoreType.Velocity,
                "velocity",
                minecraftVersion,
                cancellationToken),
            CoreType.Vanilla => GetVanillaBuildsAsync(minecraftVersion, cancellationToken),
            CoreType.Fabric => GetFabricBuildsAsync(minecraftVersion, cancellationToken),
            CoreType.Forge => GetForgeBuildsAsync(minecraftVersion, cancellationToken),
            CoreType.NeoForge => GetNeoForgeBuildsAsync(minecraftVersion, cancellationToken),
            _ => throw UnsupportedCore(coreType)
        };
    }

    private async Task<IReadOnlyList<OfficialServerCoreVersionInfo>> GetFillVersionsAsync(
        CoreType coreType,
        Uri projectUri,
        CancellationToken cancellationToken)
    {
        var candidates = await GetFillProjectVersionsAsync(projectUri, cancellationToken)
            .ConfigureAwait(false);
        var buildLists = await SelectConcurrentAsync(
                candidates,
                version => GetBuildsAsync(coreType, version, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

        var versions = new List<OfficialServerCoreVersionInfo>();
        for (var index = 0; index < candidates.Count; index++)
        {
            if (buildLists[index].Count > 0)
            {
                versions.Add(CreateVersionInfo(coreType, candidates[index]));
            }
        }

        return versions;
    }

    private async Task<IReadOnlyList<string>> GetFillProjectVersionsAsync(
        Uri projectUri,
        CancellationToken cancellationToken)
    {
        EnsureExactOfficialUri(projectUri, "fill.papermc.io", projectUri.AbsolutePath);
        var bytes = await GetBoundedBytesAsync(
                projectUri,
                MaximumJsonBytes,
                ["application/json"],
                cancellationToken)
            .ConfigureAwait(false);
        using var document = ParseJson(bytes, "PaperMC Fill project");
        if (!document.RootElement.TryGetProperty("versions", out var groups)
            || groups.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("PaperMC Fill project 缺少 versions 物件。");
        }

        var versions = new HashSet<string>(StringComparer.Ordinal);
        var entries = 0;
        foreach (var group in groups.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("PaperMC Fill version group 必須是陣列。");
            }

            foreach (var item in group.Value.EnumerateArray())
            {
                entries++;
                if (entries > MaximumCatalogEntries)
                {
                    throw new InvalidDataException("PaperMC Fill 版本數量超過安全上限。");
                }

                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("PaperMC Fill version 必須是文字。");
                }

                var version = item.GetString()!;
                if (IsStableReleaseVersionInRange(version))
                {
                    versions.Add(version);
                }
            }
        }

        return SortVersionsDescending(versions);
    }

    private async Task<IReadOnlyList<OfficialServerCoreBuildInfo>> GetFillBuildsAsync(
        CoreType coreType,
        string project,
        string productVersion,
        CancellationToken cancellationToken)
    {
        ValidateRequestedVersion(productVersion);
        var source = new Uri(
            $"https://fill.papermc.io/v3/projects/{project}/versions/"
            + $"{Uri.EscapeDataString(productVersion)}/builds?channel=STABLE");
        EnsureExactOfficialUri(source, "fill.papermc.io", source.AbsolutePath, "channel=STABLE");
        var bytes = await GetBoundedBytesAsync(
                source,
                MaximumJsonBytes,
                ["application/json"],
                cancellationToken)
            .ConfigureAwait(false);
        using var document = ParseJson(bytes, "PaperMC Fill builds");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("PaperMC Fill builds 回應必須是陣列。");
        }

        var result = new List<(int Id, OfficialServerCoreBuildInfo Build)>();
        var seenIds = new HashSet<int>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (result.Count >= MaximumCatalogEntries)
            {
                throw new InvalidDataException("PaperMC Fill build 數量超過安全上限。");
            }

            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("channel", out var channel)
                || channel.ValueKind != JsonValueKind.String
                || !channel.GetString()!.Equals("STABLE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!element.TryGetProperty("id", out var idElement)
                || !idElement.TryGetInt32(out var id)
                || id < 0
                || !seenIds.Add(id))
            {
                throw new InvalidDataException("PaperMC Fill build id 無效或重複。");
            }

            if (!element.TryGetProperty("downloads", out var downloads)
                || downloads.ValueKind != JsonValueKind.Object
                || !downloads.TryGetProperty("server:default", out var artifact)
                || artifact.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var fileName = ReadSafeFileName(artifact, "name", "PaperMC artifact");
            var downloadUri = ReadRequiredUri(artifact, "url", "PaperMC artifact");
            EnsurePaperArtifactUri(downloadUri, fileName);
            var size = ReadRequiredSize(artifact, "size", MaximumServerArtifactBytes, "PaperMC artifact");
            if (!artifact.TryGetProperty("checksums", out var checksums)
                || checksums.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("PaperMC artifact 缺少 checksums。");
            }

            var sha256 = ReadRequiredHash(checksums, "sha256", 64, "PaperMC artifact");
            var objectHash = downloadUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (objectHash.Length < 4
                || !objectHash[2].Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("PaperMC artifact URL object 與 SHA-256 不一致。");
            }

            result.Add((id, new OfficialServerCoreBuildInfo(
                coreType,
                DisplayNameFor(coreType),
                productVersion,
                productVersion,
                LoaderVersion: null,
                id.ToString(CultureInfo.InvariantCulture),
                OfficialServerInstallStrategy.DirectServerJar,
                IsStable: true,
                downloadUri,
                fileName,
                size,
                "SHA-256",
                sha256.ToLowerInvariant())));
        }

        return result
            .OrderByDescending(static item => item.Id)
            .Select(static item => item.Build)
            .ToArray();
    }

    private async Task<IReadOnlyList<OfficialServerCoreVersionInfo>> GetVanillaVersionsAsync(
        CancellationToken cancellationToken)
    {
        var entries = await GetMojangManifestEntriesAsync(cancellationToken).ConfigureAwait(false);
        var builds = await SelectConcurrentAsync(
                entries,
                entry => GetMojangBuildAsync(entry, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        var result = new List<OfficialServerCoreVersionInfo>();
        for (var index = 0; index < entries.Count; index++)
        {
            var cacheKey = (CoreType.Vanilla, entries[index].Id);
            if (builds[index] is { } build)
            {
                _buildCache.TryAdd(cacheKey, [build]);
                result.Add(CreateVersionInfo(CoreType.Vanilla, entries[index].Id));
            }
            else
            {
                _buildCache.TryAdd(cacheKey, []);
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<OfficialServerCoreBuildInfo>> GetVanillaBuildsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var entries = await GetMojangManifestEntriesAsync(cancellationToken).ConfigureAwait(false);
        var selected = entries.SingleOrDefault(entry => entry.Id.Equals(minecraftVersion, StringComparison.Ordinal));
        if (selected is null)
        {
            return [];
        }

        var build = await GetMojangBuildAsync(selected, cancellationToken).ConfigureAwait(false);
        return build is null ? [] : [build];
    }

    private async Task<IReadOnlyList<MojangManifestEntry>> GetMojangManifestEntriesAsync(
        CancellationToken cancellationToken)
    {
        EnsureExactOfficialUri(
            MojangManifestUri,
            "piston-meta.mojang.com",
            "/mc/game/version_manifest_v2.json");
        var bytes = await GetBoundedBytesAsync(
                MojangManifestUri,
                MaximumJsonBytes,
                ["application/json"],
                cancellationToken)
            .ConfigureAwait(false);
        using var document = ParseJson(bytes, "Mojang version manifest");
        if (!document.RootElement.TryGetProperty("versions", out var versions)
            || versions.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Mojang version manifest 缺少 versions 陣列。");
        }

        var result = new List<MojangManifestEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in versions.EnumerateArray())
        {
            if (result.Count >= MaximumCatalogEntries)
            {
                throw new InvalidDataException("Mojang version 數量超過安全上限。");
            }

            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !type.GetString()!.Equals("release", StringComparison.Ordinal))
            {
                continue;
            }

            var id = ReadRequiredString(item, "id", "Mojang version");
            if (!IsStableReleaseVersionInRange(id))
            {
                continue;
            }

            if (!seen.Add(id))
            {
                throw new InvalidDataException($"Mojang version manifest 含有重複 ID：{id}");
            }

            var sha1 = ReadRequiredHash(item, "sha1", 40, "Mojang version metadata");
            var metadataUri = ReadRequiredUri(item, "url", "Mojang version");
            EnsureMojangMetadataUri(metadataUri, id, sha1);
            result.Add(new MojangManifestEntry(id, metadataUri, sha1));
        }

        result.Sort(static (left, right) => NaturalVersionComparer.Instance.Compare(right.Id, left.Id));
        return result;
    }

    private async Task<OfficialServerCoreBuildInfo?> GetMojangBuildAsync(
        MojangManifestEntry entry,
        CancellationToken cancellationToken)
    {
        var bytes = await GetBoundedBytesAsync(
                entry.MetadataUri,
                MaximumJsonBytes,
                ["application/json"],
                cancellationToken)
            .ConfigureAwait(false);
        VerifyHash(bytes, HashAlgorithmName.SHA1, entry.MetadataSha1, "Mojang version metadata");
        using var document = ParseJson(bytes, "Mojang version metadata");
        if (!ReadRequiredString(document.RootElement, "id", "Mojang version metadata")
            .Equals(entry.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Mojang version metadata ID 與 manifest 不一致。");
        }

        if (!document.RootElement.TryGetProperty("downloads", out var downloads)
            || downloads.ValueKind != JsonValueKind.Object
            || !downloads.TryGetProperty("server", out var server)
            || server.ValueKind != JsonValueKind.Object)
        {
            // Mojang retains some historical release metadata without a dedicated-server artifact.
            return null;
        }

        var source = ReadRequiredUri(server, "url", "Mojang server artifact");
        EnsureMojangServerUri(source);
        var size = ReadRequiredSize(server, "size", MaximumServerArtifactBytes, "Mojang server artifact");
        var sha1 = ReadRequiredHash(server, "sha1", 40, "Mojang server artifact");
        return new OfficialServerCoreBuildInfo(
            CoreType.Vanilla,
            DisplayNameFor(CoreType.Vanilla),
            entry.Id,
            entry.Id,
            LoaderVersion: null,
            entry.Id,
            OfficialServerInstallStrategy.DirectServerJar,
            IsStable: true,
            source,
            "server.jar",
            size,
            "SHA-1",
            sha1.ToLowerInvariant());
    }

    private async Task<IReadOnlyList<OfficialServerCoreVersionInfo>> GetFabricVersionsAsync(
        CancellationToken cancellationToken)
    {
        var versions = await GetFabricStableGameVersionsAsync(cancellationToken).ConfigureAwait(false);
        var installers = await GetFabricStableInstallersAsync(cancellationToken).ConfigureAwait(false);
        if (installers.Count == 0)
        {
            return [];
        }

        var loaders = await SelectConcurrentAsync(
                versions,
                version => GetFabricStableLoaderVersionsAsync(version, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        var result = new List<OfficialServerCoreVersionInfo>();
        for (var index = 0; index < versions.Count; index++)
        {
            if (loaders[index].Count > 0)
            {
                _buildCache.TryAdd(
                    (CoreType.Fabric, versions[index]),
                    CreateFabricBuilds(versions[index], loaders[index], installers[0]));
                result.Add(CreateVersionInfo(CoreType.Fabric, versions[index]));
            }
            else
            {
                _buildCache.TryAdd((CoreType.Fabric, versions[index]), []);
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<string>> GetFabricStableGameVersionsAsync(
        CancellationToken cancellationToken)
    {
        EnsureExactOfficialUri(
            FabricGameVersionsUri,
            "meta.fabricmc.net",
            "/v2/versions/game");
        var bytes = await GetBoundedBytesAsync(
                FabricGameVersionsUri,
                MaximumJsonBytes,
                ["application/json"],
                cancellationToken)
            .ConfigureAwait(false);
        using var document = ParseJson(bytes, "Fabric game versions");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Fabric game versions 回應必須是陣列。");
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (result.Count >= MaximumCatalogEntries)
            {
                throw new InvalidDataException("Fabric game version 數量超過安全上限。");
            }

            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("stable", out var stable)
                || stable.ValueKind != JsonValueKind.True)
            {
                continue;
            }

            var version = ReadRequiredString(item, "version", "Fabric game version");
            if (IsStableReleaseVersionInRange(version))
            {
                result.Add(version);
            }
        }

        return SortVersionsDescending(result);
    }

    private async Task<IReadOnlyList<OfficialServerCoreBuildInfo>> GetFabricBuildsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var games = await GetFabricStableGameVersionsAsync(cancellationToken).ConfigureAwait(false);
        if (!games.Contains(minecraftVersion, StringComparer.Ordinal))
        {
            return [];
        }

        var loaders = await GetFabricStableLoaderVersionsAsync(minecraftVersion, cancellationToken)
            .ConfigureAwait(false);
        var installers = await GetFabricStableInstallersAsync(cancellationToken).ConfigureAwait(false);
        if (loaders.Count == 0 || installers.Count == 0)
        {
            return [];
        }

        return CreateFabricBuilds(minecraftVersion, loaders, installers[0]);
    }

    private static IReadOnlyList<OfficialServerCoreBuildInfo> CreateFabricBuilds(
        string minecraftVersion,
        IReadOnlyList<string> loaders,
        FabricInstallerEntry latestInstaller)
        => loaders
            .Select(loader => new OfficialServerCoreBuildInfo(
                CoreType.Fabric,
                DisplayNameFor(CoreType.Fabric),
                minecraftVersion,
                minecraftVersion,
                loader,
                latestInstaller.Version,
                OfficialServerInstallStrategy.FabricInstaller,
                IsStable: true,
                latestInstaller.Source,
                latestInstaller.FileName,
                Size: null,
                HashAlgorithm: null,
                Hash: null))
            .ToArray();

    private async Task<IReadOnlyList<string>> GetFabricStableLoaderVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var loaderUri = new Uri(
            "https://meta.fabricmc.net/v2/versions/loader/" + Uri.EscapeDataString(minecraftVersion));
        EnsureExactOfficialUri(loaderUri, "meta.fabricmc.net", loaderUri.AbsolutePath);
        var loaderBytes = await GetBoundedBytesAsync(
                loaderUri,
                MaximumJsonBytes,
                ["application/json"],
                cancellationToken)
            .ConfigureAwait(false);
        using var loaderDocument = ParseJson(loaderBytes, "Fabric loader versions");
        if (loaderDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Fabric loader versions 回應必須是陣列。");
        }

        var loaders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in loaderDocument.RootElement.EnumerateArray())
        {
            if (loaders.Count >= MaximumCatalogEntries)
            {
                throw new InvalidDataException("Fabric loader 數量超過安全上限。");
            }

            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("loader", out var loader)
                || loader.ValueKind != JsonValueKind.Object
                || !loader.TryGetProperty("stable", out var stable)
                || stable.ValueKind != JsonValueKind.True)
            {
                continue;
            }

            var version = ReadRequiredString(loader, "version", "Fabric loader");
            ValidateMavenToken(version, "Fabric loader version");
            if (item.TryGetProperty("intermediary", out var intermediary))
            {
                if (intermediary.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Fabric intermediary 必須是物件。");
                }

                var intermediaryVersion = ReadRequiredString(
                    intermediary,
                    "version",
                    "Fabric intermediary");
                ValidateMavenToken(intermediaryVersion, "Fabric intermediary version");
                // Fabric Meta currently uses the explicit 0.0.0 sentinel for the 26.x mapping
                // transition; the game-specific loader endpoint remains the compatibility source.
                if (!intermediaryVersion.Equals(minecraftVersion, StringComparison.Ordinal)
                    && !intermediaryVersion.Equals("0.0.0", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Fabric intermediary 與請求的 Minecraft 版本不一致。");
                }

                if (intermediary.TryGetProperty("maven", out _)
                    && !ReadRequiredString(intermediary, "maven", "Fabric intermediary")
                        .Equals(
                            $"net.fabricmc:intermediary:{intermediaryVersion}",
                            StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Fabric intermediary Maven coordinate 格式不符。");
                }
            }

            if (!loaders.Add(version))
            {
                throw new InvalidDataException("Fabric loader version 重複。");
            }
        }

        return loaders
            .OrderByDescending(static version => version, NaturalVersionComparer.Instance)
            .ToArray();
    }

    private async Task<IReadOnlyList<FabricInstallerEntry>> GetFabricStableInstallersAsync(
        CancellationToken cancellationToken)
    {
        EnsureExactOfficialUri(
            FabricInstallerVersionsUri,
            "meta.fabricmc.net",
            "/v2/versions/installer");
        var bytes = await GetBoundedBytesAsync(
                FabricInstallerVersionsUri,
                MaximumJsonBytes,
                ["application/json"],
                cancellationToken)
            .ConfigureAwait(false);
        using var document = ParseJson(bytes, "Fabric installer versions");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Fabric installer versions 回應必須是陣列。");
        }

        var result = new List<FabricInstallerEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (result.Count >= MaximumCatalogEntries)
            {
                throw new InvalidDataException("Fabric installer 數量超過安全上限。");
            }

            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("stable", out var stable)
                || stable.ValueKind != JsonValueKind.True)
            {
                continue;
            }

            var version = ReadRequiredString(item, "version", "Fabric installer");
            ValidateMavenToken(version, "Fabric installer version");
            if (!seen.Add(version))
            {
                throw new InvalidDataException("Fabric installer version 重複。");
            }

            var expectedMaven = $"net.fabricmc:fabric-installer:{version}";
            if (!ReadRequiredString(item, "maven", "Fabric installer")
                .Equals(expectedMaven, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Fabric installer Maven coordinate 格式不符。");
            }

            var fileName = $"fabric-installer-{version}.jar";
            var expected = new Uri(
                $"https://maven.fabricmc.net/net/fabricmc/fabric-installer/{version}/{fileName}");
            var source = ReadRequiredUri(item, "url", "Fabric installer");
            if (!source.AbsoluteUri.Equals(expected.AbsoluteUri, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Fabric installer URL 與 Maven coordinate 不一致。");
            }

            EnsureExactOfficialUri(source, "maven.fabricmc.net", source.AbsolutePath);
            result.Add(new FabricInstallerEntry(version, source, fileName));
        }

        result.Sort(static (left, right) =>
            NaturalVersionComparer.Instance.Compare(right.Version, left.Version));
        return result;
    }

    private async Task<IReadOnlyList<OfficialServerCoreVersionInfo>> GetForgeVersionsAsync(
        CancellationToken cancellationToken)
    {
        var coordinates = await GetMavenVersionsAsync(
                ForgeMetadataUri,
                "maven.minecraftforge.net",
                "/net/minecraftforge/forge/maven-metadata.xml",
                "net.minecraftforge",
                "forge",
                cancellationToken)
            .ConfigureAwait(false);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var coordinate in coordinates)
        {
            if (TrySplitForgeCoordinate(coordinate, out var minecraftVersion, out _)
                && IsAtLeastForgeInstallerEra(minecraftVersion))
            {
                versions.Add(minecraftVersion);
            }
        }

        return SortVersionsDescending(versions)
            .Select(version => CreateVersionInfo(CoreType.Forge, version))
            .ToArray();
    }

    private async Task<IReadOnlyList<OfficialServerCoreBuildInfo>> GetForgeBuildsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        if (!IsAtLeastForgeInstallerEra(minecraftVersion))
        {
            return [];
        }

        var coordinates = await GetMavenVersionsAsync(
                ForgeMetadataUri,
                "maven.minecraftforge.net",
                "/net/minecraftforge/forge/maven-metadata.xml",
                "net.minecraftforge",
                "forge",
                cancellationToken)
            .ConfigureAwait(false);
        return coordinates
            .Select(coordinate =>
            {
                return TrySplitForgeCoordinate(coordinate, out var game, out var loader)
                    && game.Equals(minecraftVersion, StringComparison.Ordinal)
                    ? (Coordinate: coordinate, Loader: loader)
                    : default;
            })
            .Where(static pair => pair.Coordinate is not null)
            .OrderByDescending(static pair => pair.Loader, NaturalVersionComparer.Instance)
            .Select(pair =>
            {
                var fileName = $"forge-{pair.Coordinate}-installer.jar";
                var source = new Uri(
                    $"https://maven.minecraftforge.net/net/minecraftforge/forge/"
                    + $"{pair.Coordinate}/{fileName}");
                return new OfficialServerCoreBuildInfo(
                    CoreType.Forge,
                    DisplayNameFor(CoreType.Forge),
                    minecraftVersion,
                    minecraftVersion,
                    pair.Loader,
                    pair.Coordinate,
                    OfficialServerInstallStrategy.ForgeInstaller,
                    IsStable: true,
                    source,
                    fileName,
                    Size: null,
                    HashAlgorithm: null,
                    Hash: null);
            })
            .ToArray();
    }

    private async Task<IReadOnlyList<OfficialServerCoreVersionInfo>> GetNeoForgeVersionsAsync(
        CancellationToken cancellationToken)
    {
        var loaderVersions = await GetNeoForgeMavenVersionsAsync(cancellationToken).ConfigureAwait(false);
        var versions = loaderVersions
            .Select(version => TryMapNeoForgeVersion(version, out var game) ? game : null)
            .Where(static version => version is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal);
        return SortVersionsDescending(versions)
            .Select(version => CreateVersionInfo(CoreType.NeoForge, version))
            .ToArray();
    }

    private async Task<IReadOnlyList<OfficialServerCoreBuildInfo>> GetNeoForgeBuildsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var versions = await GetNeoForgeMavenVersionsAsync(cancellationToken).ConfigureAwait(false);
        return versions
            .Where(version => TryMapNeoForgeVersion(version, out var game)
                && game.Equals(minecraftVersion, StringComparison.Ordinal))
            .OrderByDescending(static version => version, NaturalVersionComparer.Instance)
            .Select(version =>
            {
                var fileName = $"neoforge-{version}-installer.jar";
                var source = new Uri(
                    $"https://maven.neoforged.net/releases/net/neoforged/neoforge/"
                    + $"{version}/{fileName}");
                return new OfficialServerCoreBuildInfo(
                    CoreType.NeoForge,
                    DisplayNameFor(CoreType.NeoForge),
                    minecraftVersion,
                    minecraftVersion,
                    version,
                    version,
                    OfficialServerInstallStrategy.NeoForgeInstaller,
                    IsStable: true,
                    source,
                    fileName,
                    Size: null,
                    HashAlgorithm: null,
                    Hash: null);
            })
            .ToArray();
    }

    private Task<IReadOnlyList<string>> GetNeoForgeMavenVersionsAsync(CancellationToken cancellationToken)
        => GetMavenVersionsAsync(
            NeoForgeMetadataUri,
            "maven.neoforged.net",
            "/releases/net/neoforged/neoforge/maven-metadata.xml",
            "net.neoforged",
            "neoforge",
            cancellationToken);

    private async Task<IReadOnlyList<string>> GetMavenVersionsAsync(
        Uri source,
        string expectedHost,
        string expectedPath,
        string expectedGroup,
        string expectedArtifact,
        CancellationToken cancellationToken)
    {
        EnsureExactOfficialUri(source, expectedHost, expectedPath);
        var bytes = await GetBoundedBytesAsync(
                source,
                MaximumXmlBytes,
                ["application/xml", "text/xml"],
                cancellationToken)
            .ConfigureAwait(false);
        using var input = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumXmlBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        });

        var document = new XmlDocument { XmlResolver = null };
        try
        {
            document.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("官方 Maven metadata 不是有效 XML。", exception);
        }

        if (!string.Equals(document.SelectSingleNode("/metadata/groupId")?.InnerText, expectedGroup,
                StringComparison.Ordinal)
            || !string.Equals(document.SelectSingleNode("/metadata/artifactId")?.InnerText,
                expectedArtifact, StringComparison.Ordinal))
        {
            throw new InvalidDataException("官方 Maven metadata coordinate 不符。");
        }

        var nodes = document.SelectNodes("/metadata/versioning/versions/version")
            ?? throw new InvalidDataException("官方 Maven metadata 缺少 versions。");
        if (nodes.Count > MaximumCatalogEntries)
        {
            throw new InvalidDataException("官方 Maven version 數量超過安全上限。");
        }

        var result = new List<string>(nodes.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (XmlNode node in nodes)
        {
            var version = node.InnerText.Trim();
            ValidateMavenToken(version, "Maven version");
            if (!seen.Add(version))
            {
                throw new InvalidDataException("官方 Maven metadata 含有重複 version。");
            }

            result.Add(version);
        }

        return result;
    }

    private async Task<byte[]> GetBoundedBytesAsync(
        Uri source,
        long maximumBytes,
        IReadOnlyCollection<string> acceptedMediaTypes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.UserAgent.ParseAdd(_userAgent);
        foreach (var mediaType in acceptedMediaTypes)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
        }

        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("官方 catalog 回應缺少最終 URI。");
        if (!finalUri.AbsoluteUri.Equals(source.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new InvalidDataException("官方 catalog 發生未允許的 redirect。");
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadBoundedErrorAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
            throw new HttpRequestException(
                $"官方 catalog 回應 HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {detail}");
        }

        var mediaTypeValue = response.Content.Headers.ContentType?.MediaType;
        if (mediaTypeValue is not null
            && !acceptedMediaTypes.Contains(mediaTypeValue, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"官方 catalog 回應了未預期的 Content-Type：{mediaTypeValue}");
        }

        if (response.Content.Headers.ContentLength is { } declared && declared > maximumBytes)
        {
            throw new InvalidDataException("官方 catalog 回應超過安全大小上限。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException("官方 catalog 回應超過安全大小上限。");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadBoundedErrorAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        const int maximumBytes = 64 * 1024;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[maximumBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private async Task<IReadOnlyList<TResult>> SelectConcurrentAsync<TSource, TResult>(
        IReadOnlyList<TSource> source,
        Func<TSource, Task<TResult>> selector,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(MaximumConcurrentMetadataRequests);
        var tasks = source.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await selector(item).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private OfficialServerCoreVersionInfo CreateVersionInfo(CoreType coreType, string version)
    {
        var java = coreType == CoreType.Velocity
            ? VelocityJavaVersionFor(version)
            : _javaRecommendations.GetRecommendation(version, coreType).MajorVersion;
        return new OfficialServerCoreVersionInfo(
            coreType,
            DisplayNameFor(coreType),
            version,
            version,
            StrategyFor(coreType),
            java);
    }

    private static OfficialServerInstallStrategy StrategyFor(CoreType coreType) => coreType switch
    {
        CoreType.Paper or CoreType.Velocity or CoreType.Vanilla =>
            OfficialServerInstallStrategy.DirectServerJar,
        CoreType.Fabric => OfficialServerInstallStrategy.FabricInstaller,
        CoreType.Forge => OfficialServerInstallStrategy.ForgeInstaller,
        CoreType.NeoForge => OfficialServerInstallStrategy.NeoForgeInstaller,
        _ => throw UnsupportedCore(coreType)
    };

    private static string DisplayNameFor(CoreType coreType) => coreType switch
    {
        CoreType.Paper => "Paper",
        CoreType.Velocity => "Velocity",
        CoreType.Vanilla => "Minecraft 原版",
        CoreType.Fabric => "Fabric",
        CoreType.Forge => "Forge",
        CoreType.NeoForge => "NeoForge",
        _ => throw UnsupportedCore(coreType)
    };

    private static ArgumentOutOfRangeException UnsupportedCore(CoreType coreType)
        => new(nameof(coreType), coreType, "此 CoreType 沒有 first-party catalog provider。");

    private static void EnsureSupportedCore(CoreType coreType)
    {
        if (coreType is not (CoreType.Paper or CoreType.Velocity or CoreType.Vanilla
            or CoreType.Fabric or CoreType.Forge or CoreType.NeoForge))
        {
            throw UnsupportedCore(coreType);
        }
    }

    private static int VelocityJavaVersionFor(string productVersion)
    {
        if (!TryGetVersionParts(productVersion, out var parts))
        {
            throw new InvalidDataException("Velocity product version 格式無效。");
        }

        // PaperMC documents Java 21 for Velocity 3.5+. Official stable artifacts provide the
        // remaining executable contract: 4.0.0 is class-file 69 (Java 25), 3.4.0 is 61
        // (Java 17), 3.1.x is 55 (Java 11), and 1.x is 52 (Java 8).
        if (CompareVersionParts(parts, [4, 0, 0]) >= 0)
        {
            return 25;
        }

        if (CompareVersionParts(parts, [3, 5, 0]) >= 0)
        {
            return 21;
        }

        if (CompareVersionParts(parts, [3, 2, 0]) >= 0)
        {
            return 17;
        }

        return CompareVersionParts(parts, [3, 0, 0]) >= 0 ? 11 : 8;
    }

    private static void ValidateRequestedVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!IsStableReleaseVersionInRange(version))
        {
            throw new ArgumentException("版本必須是 1.0/1.0.0 到 26.2 內的正式數字版本。", nameof(version));
        }
    }

    private static bool IsStableReleaseVersionInRange(string version)
    {
        if (!DottedReleaseRegex().IsMatch(version)
            || !TryGetVersionParts(version, out var parts))
        {
            return false;
        }

        return CompareVersionParts(parts, [1, 0, 0]) >= 0
            && CompareVersionParts(parts, [26, 2, 0]) <= 0;
    }

    private static bool IsAtLeastForgeInstallerEra(string version)
        => TryGetVersionParts(version, out var parts)
            && CompareVersionParts(parts, [1, 5, 2]) >= 0;

    private static bool TryGetVersionParts(string version, out int[] parts)
    {
        var strings = version.Split('.');
        parts = new int[3];
        if (strings.Length is < 2 or > 3)
        {
            return false;
        }

        for (var index = 0; index < strings.Length; index++)
        {
            if (!int.TryParse(strings[index], NumberStyles.None, CultureInfo.InvariantCulture, out parts[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static int CompareVersionParts(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        for (var index = 0; index < 3; index++)
        {
            var comparison = left[index].CompareTo(right[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static bool TrySplitForgeCoordinate(
        string coordinate,
        out string minecraftVersion,
        out string loaderVersion)
    {
        minecraftVersion = string.Empty;
        loaderVersion = string.Empty;
        var match = ForgeCoordinateRegex().Match(coordinate);
        if (!match.Success)
        {
            return false;
        }

        minecraftVersion = match.Groups["game"].Value;
        loaderVersion = match.Groups["loader"].Value;
        return IsStableReleaseVersionInRange(minecraftVersion)
            && IsStableLoaderVersion(loaderVersion);
    }

    private static bool TryMapNeoForgeVersion(string loaderVersion, out string minecraftVersion)
    {
        minecraftVersion = string.Empty;
        if (!IsStableLoaderVersion(loaderVersion))
        {
            return false;
        }

        var textParts = loaderVersion.Split('.');
        if (textParts.Length < 3
            || textParts.Any(part => !int.TryParse(
                part,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out _)))
        {
            return false;
        }

        var numbers = textParts
            .Select(part => int.Parse(part, CultureInfo.InvariantCulture))
            .ToArray();
        if (numbers[0] is 20 or 21)
        {
            minecraftVersion = numbers[1] == 0
                ? $"1.{numbers[0]}"
                : $"1.{numbers[0]}.{numbers[1]}";
        }
        else if (numbers[0] >= 26 && textParts.Length >= 4)
        {
            minecraftVersion = numbers[2] == 0
                ? $"{numbers[0]}.{numbers[1]}"
                : $"{numbers[0]}.{numbers[1]}.{numbers[2]}";
        }
        else
        {
            return false;
        }

        return IsStableReleaseVersionInRange(minecraftVersion);
    }

    private static bool IsStableLoaderVersion(string value)
    {
        ValidateMavenToken(value, "Loader version");
        return !UnstableQualifierRegex().IsMatch(value);
    }

    private static IReadOnlyList<string> SortVersionsDescending(IEnumerable<string> versions)
        => versions
            .OrderByDescending(static version => version, NaturalVersionComparer.Instance)
            .ToArray();

    private static JsonDocument ParseJson(byte[] bytes, string context)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 64,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{context} 不是有效 JSON。", exception);
        }
    }

    private static string ReadRequiredString(JsonElement element, string property, string context)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{context} 缺少文字欄位 {property}。");
        }

        return value.GetString()!;
    }

    private static Uri ReadRequiredUri(JsonElement element, string property, string context)
    {
        var text = ReadRequiredString(element, property, context);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var result))
        {
            throw new InvalidDataException($"{context} 的 {property} 不是絕對 URL。");
        }

        return result;
    }

    private static string ReadSafeFileName(JsonElement element, string property, string context)
    {
        var value = ReadRequiredString(element, property, context);
        if (value.Length > 255
            || !value.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains('/')
            || value.Contains('\\')
            || value is "." or "..")
        {
            throw new InvalidDataException($"{context} 的檔名不安全。");
        }

        return value;
    }

    private static string ReadRequiredHash(
        JsonElement element,
        string property,
        int length,
        string context)
    {
        var value = ReadRequiredString(element, property, context);
        if (value.Length != length || !HexRegex().IsMatch(value))
        {
            throw new InvalidDataException($"{context} 的 {property} 格式無效。");
        }

        return value;
    }

    private static long ReadRequiredSize(
        JsonElement element,
        string property,
        long maximum,
        string context)
    {
        if (!element.TryGetProperty(property, out var value)
            || !value.TryGetInt64(out var size)
            || size < 1
            || size > maximum)
        {
            throw new InvalidDataException($"{context} 的 {property} 無效。");
        }

        return size;
    }

    private static void VerifyHash(
        byte[] bytes,
        HashAlgorithmName algorithm,
        string expectedHash,
        string context)
    {
        var actual = algorithm == HashAlgorithmName.SHA1
            ? SHA1.HashData(bytes)
            : SHA256.HashData(bytes);
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHash);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"{context} hash 格式無效。", exception);
        }

        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidDataException($"{context} hash 驗證失敗。");
        }
    }

    private static void EnsureExactOfficialUri(
        Uri uri,
        string expectedHost,
        string expectedPath,
        string expectedQuery = "")
    {
        if (!IsSafeHttpsUri(uri)
            || !uri.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal)
            || !uri.Query.TrimStart('?').Equals(expectedQuery, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"官方 catalog URI 不符合固定來源契約：{uri}");
        }
    }

    private static void EnsurePaperArtifactUri(Uri uri, string expectedFileName)
    {
        if (!IsSafeHttpsUri(uri)
            || !uri.Host.Equals("fill-data.papermc.io", StringComparison.OrdinalIgnoreCase)
            || !PaperArtifactPathRegex().IsMatch(uri.AbsolutePath)
            || uri.Query.Length != 0
            || !Uri.UnescapeDataString(uri.Segments[^1]).Equals(expectedFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("PaperMC artifact URL 不符合官方 Fill data 契約。");
        }
    }

    private static void EnsureMojangMetadataUri(Uri uri, string versionId, string expectedSha1)
    {
        var expectedPath = $"/v1/packages/{expectedSha1.ToLowerInvariant()}/{versionId}.json";
        if (!IsSafeHttpsUri(uri)
            || !(uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase))
            || !uri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal)
            || uri.Query.Length != 0)
        {
            throw new InvalidDataException("Mojang metadata URL 不符合官方 object 契約。");
        }
    }

    private static void EnsureMojangServerUri(Uri uri)
    {
        if (!IsSafeHttpsUri(uri)
            || !(uri.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("launcher.mojang.com", StringComparison.OrdinalIgnoreCase))
            || !MojangServerPathRegex().IsMatch(uri.AbsolutePath)
            || uri.Query.Length != 0)
        {
            throw new InvalidDataException("Mojang server URL 不符合官方 object 契約。");
        }
    }

    private static bool IsSafeHttpsUri(Uri uri)
        => uri.IsAbsoluteUri
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment);

    private static void ValidateMavenToken(string value, string name)
    {
        if (!MavenTokenRegex().IsMatch(value))
        {
            throw new InvalidDataException($"{name} 不是安全的 Maven 版本值。");
        }
    }

    private sealed record MojangManifestEntry(string Id, Uri MetadataUri, string MetadataSha1);

    private sealed record FabricInstallerEntry(string Version, Uri Source, string FileName);

    private sealed class NaturalVersionComparer : IComparer<string>
    {
        public static NaturalVersionComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                var leftDigit = char.IsAsciiDigit(left[leftIndex]);
                var rightDigit = char.IsAsciiDigit(right[rightIndex]);
                if (leftDigit && rightDigit)
                {
                    var leftEnd = leftIndex;
                    var rightEnd = rightIndex;
                    while (leftEnd < left.Length && char.IsAsciiDigit(left[leftEnd])) leftEnd++;
                    while (rightEnd < right.Length && char.IsAsciiDigit(right[rightEnd])) rightEnd++;
                    var leftNumber = left.AsSpan(leftIndex, leftEnd - leftIndex).TrimStart('0');
                    var rightNumber = right.AsSpan(rightIndex, rightEnd - rightIndex).TrimStart('0');
                    var lengthComparison = leftNumber.Length.CompareTo(rightNumber.Length);
                    if (lengthComparison != 0)
                    {
                        return lengthComparison;
                    }

                    var numberComparison = leftNumber.SequenceCompareTo(rightNumber);
                    if (numberComparison != 0)
                    {
                        return numberComparison;
                    }

                    leftIndex = leftEnd;
                    rightIndex = rightEnd;
                    continue;
                }

                var characterComparison = char.ToUpperInvariant(left[leftIndex])
                    .CompareTo(char.ToUpperInvariant(right[rightIndex]));
                if (characterComparison != 0)
                {
                    return characterComparison;
                }

                leftIndex++;
                rightIndex++;
            }

            return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
        }
    }

    [GeneratedRegex("^[0-9]+\\.[0-9]+(?:\\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DottedReleaseRegex();

    [GeneratedRegex(
        "^(?<game>(?:1\\.[0-9]+(?:\\.[0-9]+)?|26\\.[0-9]+(?:\\.[0-9]+)?))-(?<loader>[0-9][0-9A-Za-z._+\\-]{0,127})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ForgeCoordinateRegex();

    [GeneratedRegex("(?:^|[._+\\-])(?:snapshot|alpha|beta|pre|rc)(?:[._+\\-]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnstableQualifierRegex();

    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z._+\\-]{0,191}$", RegexOptions.CultureInvariant)]
    private static partial Regex MavenTokenRegex();

    [GeneratedRegex("^[0-9a-fA-F]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HexRegex();

    [GeneratedRegex("^/v1/objects/[0-9a-f]{64}/[^/]+\\.jar$", RegexOptions.CultureInvariant)]
    private static partial Regex PaperArtifactPathRegex();

    [GeneratedRegex("^/v1/objects/[0-9a-f]{40}/server\\.jar$", RegexOptions.CultureInvariant)]
    private static partial Regex MojangServerPathRegex();
}

using System.Globalization;
using System.Security;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Services;

public sealed partial class OnlineModpackWorkflow
{
    private const int MaximumMetadataPreviews = 128;
    private static readonly TimeSpan MetadataPreviewLifetime = TimeSpan.FromMinutes(15);
    private readonly object _metadataPreviewGate = new();
    private readonly Dictionary<MetadataPreviewKey, MetadataPreview> _metadataPreviews = [];

    public async Task<OnlineModpackVersion> ResolveVersionMetadataAsync(
        OnlineModpackSearchResult project,
        OnlineModpackVersion version,
        SecureString? curseForgeApiKey = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(version);
        cancellationToken.ThrowIfCancellationRequested();
        if (project.Provider != version.Provider ||
            !string.Equals(project.ProjectId, version.ProjectId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The catalogue version does not belong to this project.", nameof(version));
        }

        if (project.Provider != OnlineModpackProvider.CurseForge ||
            !string.IsNullOrWhiteSpace(version.Loader))
        {
            return version;
        }

        var modId = ParsePositiveInt(project.ProjectId, "CurseForge Mod ID");
        var fileId = ParsePositiveInt(version.VersionId, "CurseForge File ID");
        var key = new MetadataPreviewKey(modId, fileId, version.MetadataFingerprint ?? string.Empty,
            version.MinecraftVersion);
        lock (_metadataPreviewGate)
        {
            if (_metadataPreviews.TryGetValue(key, out var cached) &&
                DateTimeOffset.UtcNow - cached.CreatedAtUtc < MetadataPreviewLifetime)
            {
                return ApplyPreview(version, cached);
            }
        }

        // A small, bounded byte-range read of the exact official export supplies display hints.
        // Never reuse this partial read as a verified archive or as permission to install it.
        var manifest = await WithApiKeyAsync(curseForgeApiKey,
            apiKey => _curseForge.InspectClientPackMetadataAsync(apiKey, modId, fileId, cancellationToken))
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (MinecraftVersionPattern().IsMatch(version.MinecraftVersion) &&
            !string.Equals(version.MinecraftVersion, manifest.MinecraftVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The pack manifest Minecraft version disagrees with the selected file.");
        }

        var loader = manifest.LoaderInstallRequest.Kind switch
        {
            ModrinthModpackLoaderKind.Forge => "Forge",
            ModrinthModpackLoaderKind.Fabric => "Fabric",
            ModrinthModpackLoaderKind.NeoForge => "NeoForge",
            ModrinthModpackLoaderKind.Quilt => "Quilt",
            ModrinthModpackLoaderKind.Vanilla => "Vanilla",
            _ => throw new InvalidDataException("The pack manifest declares an unsupported loader.")
        };
        var preview = new MetadataPreview(manifest.MinecraftVersion, loader, DateTimeOffset.UtcNow);
        lock (_metadataPreviewGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_metadataPreviews.Count >= MaximumMetadataPreviews && !_metadataPreviews.ContainsKey(key))
            {
                var oldest = _metadataPreviews.MinBy(entry => entry.Value.CreatedAtUtc).Key;
                _metadataPreviews.Remove(oldest);
            }
            _metadataPreviews[key] = preview;
        }
        return ApplyPreview(version, preview);
    }

    private static OnlineModpackVersion ApplyPreview(OnlineModpackVersion version, MetadataPreview preview)
        => version with { MinecraftVersion = preview.MinecraftVersion, Loader = preview.Loader };

    private static string GetMetadataFingerprint(CurseForgeModpackFile file)
        => string.Join('|', file.FileLength.ToString(CultureInfo.InvariantCulture),
            file.FileDate?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            string.Join(';', file.Hashes.OrderBy(hash => hash.Algorithm)
                .Select(hash => $"{(int)hash.Algorithm}:{hash.Value.ToUpperInvariant()}")));

    private readonly record struct MetadataPreviewKey(int ModId, int FileId, string Fingerprint,
        string ExpectedMinecraftVersion);
    private sealed record MetadataPreview(string MinecraftVersion, string Loader, DateTimeOffset CreatedAtUtc);
}

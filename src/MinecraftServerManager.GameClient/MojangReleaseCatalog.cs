using System.Net;
using System.Text.Json;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

/// <summary>Loads the Mojang Java manifest and exposes only exact <c>release</c> entries.</summary>
public sealed class MojangReleaseCatalog(HttpClient httpClient) : IMinecraftReleaseCatalog
{
    public static readonly Uri ManifestUri =
        new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");

    private const long MaximumManifestBytes = 8L * 1024 * 1024;
    private const int MaximumVersions = 2_048;
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };

    public async Task<MinecraftReleaseCatalogSnapshot> GetStableReleasesAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUri);
        using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        EnsureResponseSource(response);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Mojang release catalog failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.",
                null,
                response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is { } declared &&
            declared is < 1 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("Mojang release catalog Content-Length is outside the safe range.");
        }

        var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, JsonOptions);
        return Parse(document.RootElement, DateTimeOffset.UtcNow);
    }

    internal static MinecraftReleaseCatalogSnapshot Parse(
        JsonElement root,
        DateTimeOffset loadedAtUtc)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("latest", out var latest) ||
            latest.ValueKind != JsonValueKind.Object ||
            !latest.TryGetProperty("release", out var latestReleaseProperty) ||
            latestReleaseProperty.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("versions", out var versions) ||
            versions.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Mojang release catalog schema is invalid.");
        }

        if (versions.GetArrayLength() > MaximumVersions)
        {
            throw new InvalidDataException("Mojang release catalog contains too many versions.");
        }

        var latestRelease = RequireVersionId(latestReleaseProperty.GetString(), "latest release");
        var releases = new List<MinecraftReleaseInfo>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in versions.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String ||
                !string.Equals(type.GetString(), "release", StringComparison.Ordinal))
            {
                continue;
            }

            var id = RequireString(item, "id", 64);
            RequireVersionId(id, "release id");
            if (!ids.Add(id))
            {
                throw new InvalidDataException($"Mojang release catalog contains duplicate id '{id}'.");
            }

            var metadataUri = RequireOfficialMetadataUri(RequireString(item, "url", 2048));
            var sha1 = RequireSha1(RequireString(item, "sha1", 40));
            var releasedAt = RequireTimestamp(item, "releaseTime");
            var complianceLevel = item.TryGetProperty("complianceLevel", out var compliance) &&
                                  compliance.TryGetInt32(out var value)
                ? value
                : 0;
            if (complianceLevel is < 0 or > 1)
            {
                throw new InvalidDataException($"Mojang release '{id}' has an invalid compliance level.");
            }

            releases.Add(new MinecraftReleaseInfo(
                id,
                releasedAt,
                metadataUri,
                sha1,
                complianceLevel));
        }

        if (releases.Count == 0 || !ids.Contains(latestRelease))
        {
            throw new InvalidDataException("Mojang release catalog does not contain its latest release.");
        }

        releases.Sort(static (left, right) =>
        {
            var byTime = right.ReleasedAtUtc.CompareTo(left.ReleasedAtUtc);
            return byTime != 0 ? byTime : string.CompareOrdinal(right.Id, left.Id);
        });

        return new MinecraftReleaseCatalogSnapshot(latestRelease, loadedAtUtc, releases);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            total = checked(total + read);
            if (total > MaximumManifestBytes)
            {
                throw new InvalidDataException("Mojang release catalog exceeds the safe size limit.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static void EnsureResponseSource(HttpResponseMessage response)
    {
        var actual = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("Mojang release catalog response has no source URI.");
        if (!actual.AbsoluteUri.Equals(ManifestUri.AbsoluteUri, StringComparison.Ordinal) ||
            response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or
                HttpStatusCode.PermanentRedirect)
        {
            throw new InvalidDataException(
                "Mojang release catalog redirected outside the pinned request contract.");
        }
    }

    private static string RequireString(JsonElement item, string propertyName, int maximumLength)
    {
        if (!item.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()) ||
            property.GetString()!.Length > maximumLength)
        {
            throw new InvalidDataException($"Mojang release catalog property '{propertyName}' is invalid.");
        }

        return property.GetString()!;
    }

    private static string RequireVersionId(string? value, string context)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new InvalidDataException($"Mojang {context} is invalid.");
        }

        return value;
    }

    private static Uri RequireOfficialMetadataUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Mojang release metadata URI is not an official HTTPS source.");
        }

        return uri;
    }

    private static string RequireSha1(string value)
    {
        if (value.Length != 40 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Mojang release metadata SHA-1 is invalid.");
        }

        return value.ToLowerInvariant();
    }

    private static DateTimeOffset RequireTimestamp(JsonElement item, string propertyName)
    {
        var value = RequireString(item, propertyName, 64);
        if (!DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            throw new InvalidDataException($"Mojang release '{propertyName}' is invalid.");
        }

        return timestamp;
    }
}

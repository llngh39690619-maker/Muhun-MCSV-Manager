using System.Net;

namespace MinecraftServerManager.GameClient;

internal sealed class OfficialCatalogHttpReader
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    public OfficialCatalogHttpReader(HttpClient httpClient, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "Catalog request timeout must be between zero and two minutes.");
        }
    }

    public async Task<byte[]> GetAsync(
        Uri requestUri,
        IReadOnlySet<string> allowedHosts,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        ArgumentNullException.ThrowIfNull(allowedHosts);
        if (maximumBytes is < 1 or > 32L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        EnsureAllowedUri(requestUri, allowedHosts);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "X-MCSV/1.0 (official-loader-catalog; +https://github.com/llngh39690619-maker/Muhun-MCSV-Manager)");
            using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);

            var responseUri = response.RequestMessage?.RequestUri
                ?? throw new InvalidDataException("Catalog response has no source URI.");
            EnsureAllowedUri(responseUri, allowedHosts);
            if (!Uri.Equals(requestUri, responseUri) ||
                response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                    HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or
                    HttpStatusCode.PermanentRedirect)
            {
                throw new InvalidDataException("Catalog response redirected away from its pinned endpoint.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Official catalog request failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.",
                    null,
                    response.StatusCode);
            }

            if (response.Content.Headers.ContentLength is { } declared &&
                (declared < 0 || declared > maximumBytes))
            {
                throw new InvalidDataException("Catalog Content-Length is outside the safe range.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[32 * 1024];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    if (total == 0)
                    {
                        throw new InvalidDataException("Official catalog returned an empty response.");
                    }

                    return output.ToArray();
                }

                total = checked(total + read);
                if (total > maximumBytes)
                {
                    throw new InvalidDataException("Official catalog exceeds the safe size limit.");
                }

                output.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Official catalog request exceeded the {_timeout.TotalSeconds:0.###}-second timeout.",
                exception);
        }
    }

    private static void EnsureAllowedUri(Uri uri, IReadOnlySet<string> allowedHosts)
    {
        if (!uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (!uri.IsDefaultPort && uri.Port != 443) ||
            !allowedHosts.Contains(uri.IdnHost))
        {
            throw new InvalidDataException("Catalog URI is not an allowlisted official HTTPS endpoint.");
        }
    }
}

internal static class OfficialCatalogValidation
{
    public static bool IsStableMinecraftRelease(
        MinecraftServerManager.GameClient.Contracts.MinecraftReleaseCatalogSnapshot snapshot,
        string gameVersion)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateVersionToken(gameVersion, nameof(gameVersion));
        return snapshot.Releases.Any(
            release => string.Equals(release.Id, gameVersion, StringComparison.Ordinal));
    }

    public static string ValidateVersionToken(string? value, string fieldName, int maximumLength = 128)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '.' and not '-' and not '_' and not '+'))
        {
            throw new InvalidDataException($"Catalog field '{fieldName}' is not a safe version token.");
        }

        return value;
    }

    public static bool IsStrictStableNumericVersion(string value, int minimumParts, int maximumParts)
    {
        var parts = value.Split('.', StringSplitOptions.None);
        return parts.Length >= minimumParts &&
               parts.Length <= maximumParts &&
               parts.All(part =>
                   part.Length is > 0 and <= 9 &&
                   part.All(char.IsAsciiDigit));
    }
}

internal sealed class LoaderVersionComparer : IComparer<string>
{
    public static LoaderVersionComparer Instance { get; } = new();

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

        var leftParts = left.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries);
        var common = Math.Min(leftParts.Length, rightParts.Length);
        for (var index = 0; index < common; index++)
        {
            var result = ComparePart(leftParts[index], rightParts[index]);
            if (result != 0)
            {
                return result;
            }
        }

        var byLength = leftParts.Length.CompareTo(rightParts.Length);
        return byLength != 0 ? byLength : string.CompareOrdinal(left, right);
    }

    private static int ComparePart(string left, string right)
    {
        if (left.All(char.IsAsciiDigit) && right.All(char.IsAsciiDigit))
        {
            var normalizedLeft = left.TrimStart('0');
            var normalizedRight = right.TrimStart('0');
            normalizedLeft = normalizedLeft.Length == 0 ? "0" : normalizedLeft;
            normalizedRight = normalizedRight.Length == 0 ? "0" : normalizedRight;
            var byDigits = normalizedLeft.Length.CompareTo(normalizedRight.Length);
            if (byDigits != 0)
            {
                return byDigits;
            }

            return string.CompareOrdinal(normalizedLeft, normalizedRight);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}

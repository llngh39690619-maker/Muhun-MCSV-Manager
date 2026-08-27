using System.Net;

namespace MinecraftServerManager.Service;

public interface IProductUpdateTransport
{
    Task<byte[]> GetBytesAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken);

    Task DownloadAsync(
        Uri uri,
        string destinationPath,
        long expectedBytes,
        Action<long>? reportProgress,
        CancellationToken cancellationToken);
}

public sealed class ProductUpdateHttpTransport : IProductUpdateTransport, IDisposable
{
    private readonly HashSet<string> _allowedHosts;
    private readonly HttpClient _client;

    public ProductUpdateHttpTransport(
        IReadOnlyCollection<string> allowedHosts,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);
        _allowedHosts = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);
        if (_allowedHosts.Count is < 1 or > 8 ||
            _allowedHosts.Any(host => Uri.CheckHostName(host) != UriHostNameType.Dns))
        {
            throw new ArgumentException("Update transport requires one to eight exact DNS hosts.", nameof(allowedHosts));
        }

        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            Timeout = TimeSpan.FromMinutes(15),
        };
    }

    public async Task<byte[]> GetBytesAsync(
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ValidateUri(uri);
        if (maximumBytes is < 1 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        ValidateResponse(response, uri, maximumBytes);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        await CopyBoundedAsync(source, destination, maximumBytes, null, cancellationToken).ConfigureAwait(false);
        return destination.ToArray();
    }

    public async Task DownloadAsync(
        Uri uri,
        string destinationPath,
        long expectedBytes,
        Action<long>? reportProgress,
        CancellationToken cancellationToken)
    {
        ValidateUri(uri);
        if (!Path.IsPathFullyQualified(destinationPath) || expectedBytes is < 1 or > 2L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedBytes));
        }

        using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        ValidateResponse(response, uri, expectedBytes);
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != expectedBytes)
        {
            throw new InvalidDataException("Update package Content-Length does not match its signed manifest.");
        }

        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            var copied = await CopyBoundedAsync(
                    source,
                    destination,
                    expectedBytes,
                    reportProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (copied != expectedBytes)
            {
                throw new InvalidDataException("Update package length does not match its signed manifest.");
            }
        }
        catch
        {
            try
            {
                File.Delete(destinationPath);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    public void Dispose() => _client.Dispose();

    private void ValidateUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
            !_allowedHosts.Contains(uri.IdnHost))
        {
            throw new InvalidDataException("Update URI is not an exact allowlisted HTTPS endpoint.");
        }
    }

    private static void ValidateResponse(HttpResponseMessage response, Uri requestedUri, long maximumBytes)
    {
        if (response.StatusCode != HttpStatusCode.OK ||
            response.RequestMessage?.RequestUri is not { } finalUri ||
            !Uri.Compare(
                requestedUri,
                finalUri,
                UriComponents.AbsoluteUri,
                UriFormat.UriEscaped,
                StringComparison.Ordinal).Equals(0) ||
            response.Content.Headers.ContentLength is > 0 and var length && length > maximumBytes)
        {
            throw new HttpRequestException("Update endpoint was redirected, rejected or exceeded its signed limit.");
        }
    }

    private static async Task<long> CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        Action<long>? reportProgress,
        CancellationToken cancellationToken)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(128 * 1024);
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException("Update response exceeded its signed size limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            reportProgress?.Invoke(total);
        }
    }
}

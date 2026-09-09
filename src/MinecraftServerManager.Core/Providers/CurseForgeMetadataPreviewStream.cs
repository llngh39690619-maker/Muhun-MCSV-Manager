using System.Net;
using System.Net.Http.Headers;

namespace MinecraftServerManager.Core.Providers;

/// <summary>
/// A bounded, read-only ZIP metadata transport. Ranges are not a verification of the complete
/// archive hash and must never be reused as an installed or verified package. The caller runs
/// ZIP inspection on a worker thread because ZipArchive also performs synchronous seeks/reads.
/// </summary>
internal sealed class CurseForgeMetadataPreviewStream : Stream
{
    internal const int BlockBytes = 64 * 1024;
    internal const int MaximumRequests = 128;
    internal const int MaximumTransferredBytes = 8 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly Uri _source;
    private readonly string _userAgent;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<long, byte[]> _blocks = [];
    private readonly long _length;
    private long _position;
    private long _transferredBytes;
    private int _requestCount;
    private bool _disposed;
    private bool _hasResponse;
    private Uri? _responseUri;
    private EntityTagHeaderValue? _entityTag;
    private DateTimeOffset? _lastModified;
    private byte[]? _wholeFile;

    public CurseForgeMetadataPreviewStream(
        HttpClient client,
        Uri source,
        long length,
        string userAgent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        if (length < 1 || length > 2L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (!source.IsAbsoluteUri || source.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Metadata preview requires an official API-provided HTTPS URL.", nameof(source));
        }

        _client = client;
        _source = source;
        _length = length;
        _userAgent = userAgent;
        _cancellationToken = cancellationToken;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length { get { ThrowIfUnavailable(); return _length; } }
    public override long Position
    {
        get { ThrowIfUnavailable(); return _position; }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfUnavailable();
        var total = 0;
        while (total < buffer.Length && _position < _length)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var blockStart = _position / BlockBytes * BlockBytes;
            if (_wholeFile is null && !_blocks.ContainsKey(blockStart))
            {
                var block = FetchRangeAsync(blockStart).ConfigureAwait(false).GetAwaiter().GetResult();
                if (_wholeFile is null)
                {
                    _blocks.Add(blockStart, block);
                }
            }

            var bytes = _wholeFile ?? _blocks[blockStart];
            var offset = checked((int)(_wholeFile is null ? _position - blockStart : _position));
            var count = Math.Min(buffer.Length - total, bytes.Length - offset);
            bytes.AsSpan(offset, count).CopyTo(buffer[total..]);
            _position += count;
            total += count;
        }

        return total;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfUnavailable();
        var position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (position < 0 || position > _length)
        {
            throw new IOException("ZIP metadata seek is outside the API-declared archive length.");
        }

        _position = position;
        return position;
    }

    private async Task<byte[]> FetchRangeAsync(long start)
    {
        ThrowIfUnavailable();
        if (_client.DefaultRequestHeaders.Contains("x-api-key"))
        {
            throw new InvalidOperationException("CDN metadata requests must not contain x-api-key.");
        }

        if (++_requestCount > MaximumRequests)
        {
            throw new InvalidDataException("CurseForge ZIP metadata preview exceeded its request limit.");
        }

        var end = Math.Min(_length - 1, start + BlockBytes - 1);
        using var request = new HttpRequestMessage(HttpMethod.Get, _source);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        request.Headers.Range = new RangeHeaderValue(start, end);
        if (_entityTag is not null)
        {
            request.Headers.IfMatch.Add(_entityTag);
        }
        else if (_lastModified is not null)
        {
            request.Headers.IfUnmodifiedSince = _lastModified;
        }

        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, _cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentEncoding.Count != 0)
        {
            throw new InvalidDataException("Encoded responses cannot be used for ZIP byte ranges.");
        }

        var wholeFile = response.StatusCode == HttpStatusCode.OK;
        long expectedBytes;
        if (wholeFile)
        {
            // A server may ignore Range, but never read a large fallback response body.
            expectedBytes = _length;
            if (response.Content.Headers.ContentRange is not null)
            {
                throw new InvalidDataException("A complete ZIP response must not include Content-Range.");
            }
        }
        else if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range is null || !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
                || range.From != start || range.To != end || range.Length != _length)
            {
                throw new InvalidDataException("The ZIP metadata Content-Range does not match the requested file range.");
            }

            expectedBytes = end - start + 1;
        }
        else
        {
            throw new HttpRequestException(
                $"CurseForge ZIP metadata request failed: HTTP {(int)response.StatusCode}.",
                null, response.StatusCode);
        }

        if (expectedBytes > MaximumTransferredBytes - _transferredBytes)
        {
            throw new InvalidDataException("CurseForge ZIP metadata preview exceeded its transfer limit.");
        }

        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != expectedBytes)
        {
            throw new InvalidDataException("The ZIP metadata response length does not match its byte range.");
        }

        ValidateIdentity(response, wholeFile || expectedBytes == _length);
        var bytes = new byte[checked((int)expectedBytes)];
        await using var input = await response.Content.ReadAsStreamAsync(_cancellationToken).ConfigureAwait(false);
        var total = 0;
        while (total < bytes.Length)
        {
            var count = await input.ReadAsync(bytes.AsMemory(total), _cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new InvalidDataException("The ZIP metadata response ended before its declared length.");
            }

            total += count;
            _transferredBytes += count;
        }

        var extra = new byte[1];
        if (await input.ReadAsync(extra, _cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("The ZIP metadata response exceeds its declared length.");
        }

        if (wholeFile)
        {
            _wholeFile = bytes;
            _blocks.Clear();
        }

        return bytes;
    }

    private void ValidateIdentity(HttpResponseMessage response, bool containsWholeFile)
    {
        var responseUri = response.RequestMessage?.RequestUri;
        if (responseUri is null || !CurseForgeModpackProvider.IsAllowedMetadataRedirect(responseUri, _source)
            || (_responseUri is not null && _responseUri != responseUri))
        {
            throw new InvalidDataException("The ZIP metadata response changed its HTTPS resource identity.");
        }

        var strongTag = response.Headers.ETag is { IsWeak: false } tag ? tag : null;
        var modified = response.Content.Headers.LastModified;
        if (_hasResponse)
        {
            if ((_entityTag is not null && !_entityTag.Equals(strongTag))
                || (_entityTag is null && _lastModified != modified))
            {
                throw new InvalidDataException("The CurseForge ZIP changed between metadata range requests.");
            }
        }
        else
        {
            // Do not combine bytes that cannot be tied to a stable remote representation.
            if (!containsWholeFile && strongTag is null && modified is null)
            {
                throw new InvalidDataException("The CDN does not provide a stable validator for ZIP metadata ranges.");
            }

            _entityTag = strongTag;
            _lastModified = modified;
            _responseUri = responseUri;
            _hasResponse = true;
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cancellationToken.ThrowIfCancellationRequested();
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        _blocks.Clear();
        _wholeFile = null;
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

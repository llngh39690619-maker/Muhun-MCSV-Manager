using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Updater;

/// <summary>
/// Local formal-release repair deliberately proves only the Windows Service handoff. The loose
/// interactive GUI remains alive while UAC work is performed, then reconnects to the compatible
/// Service. Requiring a second GUI acknowledgement here would collide with the per-user GUI mutex.
/// </summary>
internal sealed class ProductServiceRepairHealthController : IProductUpdateHealthController, IDisposable
{
    private const int MaximumAppSettingsBytes = 64 * 1024;
    private const int MaximumHealthResponseBytes = 16 * 1024;
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly string _dataRoot;
    private readonly IProductWindowsServicePlatform _platform;
    private readonly HttpClient _httpClient;
    private readonly byte[] _serviceToken;
    private readonly Guid _installationId;
    private readonly string _requiredTargetVersion;
    private int? _servicePort;
    private string? _launchedVersion;
    private int _disposed;

    public ProductServiceRepairHealthController(
        string dataRoot,
        string requiredTargetVersion,
        IProductWindowsServicePlatform? platform = null,
        HttpMessageHandler? httpHandler = null)
    {
        ProductUpdateManifestParser.ValidateVersion(requiredTargetVersion);
        _dataRoot = ProductActivationCredentialReader.ValidateDataRoot(dataRoot);
        _requiredTargetVersion = requiredTargetVersion;
        (_serviceToken, _installationId) = ProductActivationCredentialReader.Read(_dataRoot);
        _platform = platform ?? new ProductWindowsServicePlatform();
        _httpClient = new HttpClient(httpHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        })
        {
            Timeout = TimeSpan.FromSeconds(3),
        };
    }

    public async Task LaunchAsync(string executablePath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var layout = ProductActivationPathPolicy.ResolveFormalLayout(executablePath);
        ProductActivationPathPolicy.ValidateMatchingProductVersions(layout);
        _servicePort = ReadServicePort(layout.VersionRoot);
        _launchedVersion = layout.Version;
        await _platform.ConfigureAndRestartAsync(layout.ServicePath, _dataRoot, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> WaitForHealthyAsync(
        string version,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ProductUpdateManifestParser.ValidateVersion(version);
        if (_servicePort is not { } servicePort ||
            !string.Equals(_launchedVersion, version, StringComparison.Ordinal))
        {
            return false;
        }

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        var endpoint = new Uri(
            $"http://127.0.0.1:{servicePort}/api/v1/system/activation-ready",
            UriKind.Absolute);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.TryAddWithoutValidation(
                    ProductLocalApiAuthentication.HeaderName,
                    Encoding.ASCII.GetString(_serviceToken));
                request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                using var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK &&
                    response.Content.Headers.ContentLength is not > MaximumHealthResponseBytes)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    using var bounded = new MemoryStream(MaximumHealthResponseBytes);
                    await CopyBoundedAsync(stream, bounded, cancellationToken).ConfigureAwait(false);
                    var ready = JsonSerializer.Deserialize<ProductActivationReadyResponse>(
                        bounded.ToArray(),
                        StrictJson);
                    if (ready is not null &&
                        ready.Ready &&
                        string.Equals(ready.Status, "ready", StringComparison.Ordinal) &&
                        string.Equals(ready.Product, "Muhun MCSV Manager", StringComparison.Ordinal) &&
                        string.Equals(ready.Version, version, StringComparison.Ordinal) &&
                        ready.InstallationId == _installationId &&
                        ready.StartedAtUtc != default)
                    {
                        // A rollback only needs to prove that the previous Service is alive under
                        // the same installation identity. The new target must additionally prove
                        // API 1.6 EULA support before the A/B pointer may be committed.
                        if (!string.Equals(version, _requiredTargetVersion, StringComparison.Ordinal))
                        {
                            return true;
                        }

                        var compatibility = await CheckCompatibleHandshakeAsync(
                                servicePort,
                                version,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (compatibility is not null)
                        {
                            return compatibility.Value;
                        }
                    }
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or JsonException or TaskCanceledException)
            {
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(_serviceToken);
            _httpClient.Dispose();
        }
    }

    internal static int ReadServicePort(string versionRoot)
    {
        var appSettings = ProductUpdatePath.ResolveUnderRoot(
            versionRoot,
            "service-win-x64/appsettings.json");
        ProductActivationPathPolicy.RejectExistingReparsePoints(appSettings);
        using var stream = new FileStream(appSettings, FileMode.Open, FileAccess.Read, FileShare.Read, 4_096);
        if (stream.Length is < 2 or > MaximumAppSettingsBytes ||
            (File.GetAttributes(appSettings) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The installed Service configuration has an invalid size or identity.");
        }

        try
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (!document.RootElement.TryGetProperty("Mcsv", out var mcsv) ||
                mcsv.ValueKind != JsonValueKind.Object ||
                !mcsv.TryGetProperty("Service", out var service) ||
                service.ValueKind != JsonValueKind.Object ||
                !service.TryGetProperty("Port", out var portElement) ||
                !portElement.TryGetInt32(out var port) ||
                port is < 1024 or > 65535)
            {
                throw new InvalidDataException("The installed Service port is invalid.");
            }

            return port;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The installed Service configuration JSON is invalid.", exception);
        }
    }

    private async Task<bool?> CheckCompatibleHandshakeAsync(
        int servicePort,
        string version,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(
            $"http://127.0.0.1:{servicePort}{ProductApiProtocol.RestBasePath}/system/handshake",
            UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation(
            ProductLocalApiAuthentication.HeaderName,
            Encoding.ASCII.GetString(_serviceToken));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentLength is > MaximumHealthResponseBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var bounded = new MemoryStream(MaximumHealthResponseBytes);
        await CopyBoundedAsync(stream, bounded, cancellationToken).ConfigureAwait(false);
        var handshake = JsonSerializer.Deserialize<ProductHandshakeResponse>(bounded.ToArray(), StrictJson);
        var required = ProductApiProtocol.MinecraftEulaConsentVersion;
        if (handshake is null ||
            !handshake.Ready ||
            !string.Equals(handshake.Product, "Muhun MCSV Manager", StringComparison.Ordinal) ||
            !string.Equals(handshake.ProductVersion, version, StringComparison.Ordinal))
        {
            return null;
        }

        return handshake.MinimumApiVersion.CompareTo(required) <= 0 &&
               handshake.ApiVersion.CompareTo(required) >= 0;
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(4 * 1024);
        var total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            total = checked(total + read);
            if (total > MaximumHealthResponseBytes)
            {
                throw new InvalidDataException("The Service repair health response exceeded its limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}

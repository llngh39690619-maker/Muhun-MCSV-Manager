using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftServerManager.Service;

/// <summary>
/// Persists only a route attested by the authenticated ServiceManage desktop client after it has
/// verified the official Tailscale CLI. The Service cannot safely query Windows Tailscale while
/// its tray UI is connected under another local SID, so this bounded receipt is restart-time
/// authority for the loopback Web host. It is trusted-client configuration evidence, not an
/// independent Service-side proof or a claim of current network liveness.
/// </summary>
public sealed class ProductRemoteWebRouteStore(ProductDataLayout layout)
{
    internal const string FileName = "remote-web.route.v1.json";
    internal const int SchemaVersion = 1;
    internal const int RequiredLocalPort = ProductRemoteWebSupervisor.LocalWebPort;
    internal static readonly string RequiredLocalTarget = ProductTailscalePlatform.CreateTarget(RequiredLocalPort);
    private const int MaximumFileBytes = 2 * 1024;
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(layout.Operations, FileName);

    public ProductRemoteWebRouteReceipt? Read()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            RejectReparsePoint(_path);
            var bytes = File.ReadAllBytes(_path);
            if (bytes.Length is 0 or > MaximumFileBytes)
            {
                throw new InvalidDataException("Remote Web route receipt has an invalid size.");
            }

            try
            {
                var document = JsonSerializer.Deserialize(
                                   bytes,
                                   ProductRemoteWebRouteJsonContext.Default.RouteDocument)
                               ?? throw new InvalidDataException("Remote Web route receipt is empty.");
                if (document.SchemaVersion != SchemaVersion ||
                    document.VerifiedAtUtc.Offset != TimeSpan.Zero ||
                    document.LocalPort != RequiredLocalPort ||
                    !string.Equals(document.LocalTarget, RequiredLocalTarget, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Remote Web route receipt schema, target, port, or time is invalid.");
                }

                Uri origin;
                try
                {
                    origin = ValidateCanonicalPublicOrigin(document.PublicOrigin);
                }
                catch (ArgumentException error)
                {
                    throw new InvalidDataException("Remote Web route receipt origin is invalid.", error);
                }
                return new ProductRemoteWebRouteReceipt(
                    origin,
                    document.LocalPort,
                    document.LocalTarget,
                    document.VerifiedAtUtc);
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("Remote Web route receipt JSON is invalid.", error);
            }
        }
    }

    public ProductRemoteWebRouteReceipt WriteVerified(
        string publicOrigin,
        DateTimeOffset verifiedAtUtc)
    {
        var origin = ValidateCanonicalPublicOrigin(publicOrigin);
        if (verifiedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Route verification time must use UTC.", nameof(verifiedAtUtc));
        }

        lock (_gate)
        {
            Directory.CreateDirectory(layout.Operations);
            RejectReparsePoint(layout.Operations);
            if (File.Exists(_path))
            {
                RejectReparsePoint(_path);
            }

            var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new RouteDocument(
                    SchemaVersion,
                    origin.ToString(),
                    RequiredLocalPort,
                    RequiredLocalTarget,
                    verifiedAtUtc),
                ProductRemoteWebRouteJsonContext.Default.RouteDocument);
            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4_096,
                           FileOptions.WriteThrough))
                {
                    stream.Write(payload);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }

            return new ProductRemoteWebRouteReceipt(
                origin,
                RequiredLocalPort,
                RequiredLocalTarget,
                verifiedAtUtc);
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return;
            }

            RejectReparsePoint(_path);
            File.Delete(_path);
        }
    }

    internal static Uri ValidateCanonicalPublicOrigin(string publicOrigin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicOrigin);
        if (publicOrigin.Length > 512 ||
            !Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin) ||
            !string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            origin.Port != 443 ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            !string.Equals(origin.AbsolutePath, "/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment) ||
            !ProductTailscaleProtocol.TryNormalizeDnsName(origin.IdnHost, out var dnsName) ||
            !dnsName.StartsWith(
                ProductRemoteWebSupervisor.RequiredMachineHostname + ".",
                StringComparison.Ordinal) ||
            dnsName.Length <= ProductRemoteWebSupervisor.RequiredMachineHostname.Length +
                              1 + ".ts.net".Length)
        {
            throw new ArgumentException(
                "Remote Web route must be the canonical x-mcsv Tailscale HTTPS origin.",
                nameof(publicOrigin));
        }

        var canonical = new Uri($"https://{dnsName}/", UriKind.Absolute);
        if (!string.Equals(publicOrigin, canonical.ToString(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Remote Web route origin is not in canonical form.",
                nameof(publicOrigin));
        }

        return canonical;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Remote Web route receipt path cannot be a reparse point.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal sealed record RouteDocument(
        int SchemaVersion,
        string PublicOrigin,
        int LocalPort,
        string LocalTarget,
        DateTimeOffset VerifiedAtUtc);
}

public sealed record ProductRemoteWebRouteReceipt(
    Uri PublicOrigin,
    int LocalPort,
    string LocalTarget,
    DateTimeOffset VerifiedAtUtc);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ProductRemoteWebRouteStore.RouteDocument))]
internal sealed partial class ProductRemoteWebRouteJsonContext : JsonSerializerContext;

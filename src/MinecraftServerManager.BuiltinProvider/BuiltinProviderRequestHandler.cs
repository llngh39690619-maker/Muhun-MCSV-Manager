using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.BuiltinProvider;

public static class BuiltinProviderRequestHandler
{
    public const string ProviderId = ProductFirstPartyProviderIdentities.CatalogProviderId;
    private const string ProductUserAgent = "MuhunMCSVManager/1.0 (builtin-provider)";
    private static readonly Uri ModrinthTarget = new("https://api.modrinth.com/");
    private static readonly Uri FtbTarget = new("https://api.feed-the-beast.com/");
    private static readonly HashSet<string> KnownLoaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "forge", "neoforge", "quilt",
    };

    public static async Task<ProductProviderRpcResponse> HandleAsync(
        ProductProviderRpcRequest request,
        CancellationToken cancellationToken,
        HttpClient? brokerClient = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsValidEnvelope(request))
        {
            return Error(request.RequestId, "provider.request_invalid", "Provider request envelope is invalid.");
        }

        try
        {
            return request.Operation switch
            {
                ProductProviderOperations.HealthGet when request.NetworkTarget is null =>
                    Success(request.RequestId, new
                    {
                        status = "healthy",
                        providerId = ProviderId,
                        apiVersion = ProductApiProtocol.CurrentVersion.ToString(),
                        capabilities = new[] { ProductProviderCapabilities.ModpackCatalog },
                    }),
                ProductProviderOperations.ModpackCatalogSearch =>
                    Success(
                        request.RequestId,
                        await SearchModpacksAsync(request, brokerClient, cancellationToken).ConfigureAwait(false)),
                _ => Error(
                    request.RequestId,
                    "provider.operation_unsupported",
                    "Provider operation is not supported."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is
            ArgumentException or
            JsonException or
            InvalidDataException)
        {
            return Error(
                request.RequestId,
                "provider.request_invalid",
                "Provider request payload is invalid.");
        }
        catch (Exception error) when (error is
            HttpRequestException or
            TaskCanceledException or
            IOException)
        {
            return Error(
                request.RequestId,
                "provider.upstream_unavailable",
                "The official catalogue endpoint is temporarily unavailable.",
                retryable: true);
        }
    }

    private static async Task<ProductProviderModpackSearchPage> SearchModpacksAsync(
        ProductProviderRpcRequest envelope,
        HttpClient? brokerClient,
        CancellationToken cancellationToken)
    {
        var request = envelope.Payload.Deserialize<ProductProviderModpackSearchRequest>(JsonOptions)
                      ?? throw new InvalidDataException("Modpack request is missing.");
        ValidateSearchRequest(request);
        var target = ParseAndValidateNetworkTarget(envelope.NetworkTarget, request.Source);
        var client = brokerClient
                     ?? throw new InvalidDataException("Trusted HTTP broker is unavailable.");
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(ProductUserAgent);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (request.Source == ProductModpackCatalogSources.Modrinth)
        {
            _ = target;
            var provider = new ModrinthModpackProvider(client, ProductUserAgent);
            var page = await provider.SearchAsync(
                    new ModrinthModpackSearchRequest(
                        request.Query,
                        request.GameVersion,
                        request.Loader,
                        request.Offset,
                        request.Limit,
                        IncludeUnknownEnvironment: false,
                        request.Sort,
                        request.Category),
                    cancellationToken)
                .ConfigureAwait(false);
            return new ProductProviderModpackSearchPage(
                page.Projects.Select(MapModrinthProject).ToArray(),
                page.Offset,
                page.Limit,
                page.TotalHits);
        }

        var ftb = new FtbCatalogProvider(client, ProductUserAgent);
        var fetchLimit = Math.Min(100, Math.Max(request.Limit, request.Offset + request.Limit));
        var source = string.IsNullOrWhiteSpace(request.Query)
            ? await ftb.GetFeaturedAsync(fetchLimit, cancellationToken).ConfigureAwait(false)
            : await ftb.SearchAsync(request.Query, fetchLimit, cancellationToken).ConfigureAwait(false);
        var filtered = source.Packs
            .Where(pack => MatchesFtbFilters(pack, request))
            .ToArray();
        var projects = filtered
            .Skip(request.Offset)
            .Take(request.Limit)
            .Select(MapFtbProject)
            .ToArray();
        return new ProductProviderModpackSearchPage(
            projects,
            request.Offset,
            request.Limit,
            filtered.Length);
    }

    private static ProductProviderModpackProject MapModrinthProject(ModrinthModpackProject project)
    {
        var loaders = project.Categories
            .Where(category => KnownLoaders.Contains(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var categories = project.Categories
            .Where(category => !KnownLoaders.Contains(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ProductProviderModpackProject(
            ProductModpackCatalogSources.Modrinth,
            project.ProjectId,
            project.Slug,
            project.Title,
            project.Description,
            project.Author,
            project.IconUri,
            project.GalleryImageUris.FirstOrDefault() ?? project.IconUri,
            project.GameVersions,
            loaders,
            categories,
            project.Downloads,
            project.DateModified.ToUniversalTime());
    }

    private static ProductProviderModpackProject MapFtbProject(FtbPack pack)
    {
        var gameVersions = pack.Versions
            .Select(version => version.MinecraftVersion)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var loaders = pack.Versions
            .Select(version => version.ModLoaderName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ProductProviderModpackProject(
            ProductModpackCatalogSources.Ftb,
            pack.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(pack.Slug)
                ? pack.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : pack.Slug,
            pack.Name,
            pack.Synopsis ?? string.Empty,
            "FTB Team",
            pack.IconUriCandidates.FirstOrDefault(),
            pack.PreviewImageUriCandidates.FirstOrDefault() ?? pack.IconUriCandidates.FirstOrDefault(),
            gameVersions,
            loaders,
            ["ftb-official"],
            pack.InstallCount.GetValueOrDefault(),
            UpdatedAtUtc: null);
    }

    private static bool MatchesFtbFilters(
        FtbPack pack,
        ProductProviderModpackSearchRequest request)
        => (string.IsNullOrWhiteSpace(request.GameVersion) || pack.Versions.Any(version =>
                string.Equals(version.MinecraftVersion, request.GameVersion, StringComparison.OrdinalIgnoreCase))) &&
           (string.IsNullOrWhiteSpace(request.Loader) || pack.Versions.Any(version =>
                string.Equals(version.ModLoaderName, request.Loader, StringComparison.OrdinalIgnoreCase)));

    private static Uri ParseAndValidateNetworkTarget(string? value, string source)
    {
        var expected = source switch
        {
            ProductModpackCatalogSources.Modrinth => ModrinthTarget,
            ProductModpackCatalogSources.Ftb => FtbTarget,
            _ => throw new InvalidDataException("Modpack source is unsupported."),
        };
        if (!Uri.TryCreate(value, UriKind.Absolute, out var target) ||
            !target.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            target.HostNameType != UriHostNameType.Dns ||
            !target.IdnHost.Equals(expected.IdnHost, StringComparison.Ordinal) ||
            (!target.IsDefaultPort && target.Port != 443) ||
            !string.IsNullOrEmpty(target.UserInfo) ||
            !string.IsNullOrEmpty(target.Fragment))
        {
            throw new InvalidDataException("Modpack source does not match its exact network target.");
        }

        return target;
    }

    private static void ValidateSearchRequest(ProductProviderModpackSearchRequest request)
    {
        if (!ProductModpackCatalogSources.All.Contains(request.Source) ||
            request.Query is null || request.Query.Length > 200 ||
            request.GameVersion?.Length > 64 ||
            request.Loader?.Length > 32 ||
            request.Category?.Length > 64 ||
            request.Sort is null || request.Sort.Length is < 1 or > 32 ||
            request.Offset is < 0 or > 10_000 ||
            request.Limit is < 1 or > 50 ||
            HasUnsafeText(request.Query) ||
            HasUnsafeText(request.GameVersion) ||
            HasUnsafeText(request.Loader) ||
            HasUnsafeText(request.Category) ||
            HasUnsafeText(request.Sort))
        {
            throw new InvalidDataException("Modpack search request is outside its bounds.");
        }
    }

    private static bool HasUnsafeText(string? value)
        => value?.Any(character => char.IsControl(character) || char.IsSurrogate(character)) == true;

    private static bool IsValidEnvelope(ProductProviderRpcRequest request)
        => request.ProtocolVersion == ProductProviderRpcProtocol.CurrentVersion &&
           request.MessageType == ProductProviderRpcProtocol.RequestMessageType &&
           request.RequestId is { Length: >= 1 and <= ProductProviderRpcProtocol.MaximumRequestIdLength } &&
           !request.RequestId.Any(character => char.IsControl(character) || char.IsSurrogate(character)) &&
           request.Operation is { Length: >= 1 and <= 128 } &&
           !request.Operation.Any(character => char.IsControl(character) || char.IsSurrogate(character)) &&
           request.Payload.ValueKind != JsonValueKind.Undefined;

    private static ProductProviderRpcResponse Success<T>(string requestId, T value)
        => new(
            ProductProviderRpcProtocol.CurrentVersion,
            ProductProviderRpcProtocol.ResponseMessageType,
            requestId,
            ProductProviderRpcProtocol.SuccessStatus,
            JsonSerializer.SerializeToElement(value, JsonOptions),
            Error: null);

    private static ProductProviderRpcResponse Error(
        string requestId,
        string code,
        string message,
        bool retryable = false)
        => new(
            ProductProviderRpcProtocol.CurrentVersion,
            ProductProviderRpcProtocol.ResponseMessageType,
            requestId,
            ProductProviderRpcProtocol.ErrorStatus,
            Result: null,
            new ProductProviderRpcError(code, message, retryable));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

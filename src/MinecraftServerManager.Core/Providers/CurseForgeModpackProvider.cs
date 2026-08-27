using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace MinecraftServerManager.Core.Providers;

/// <summary>
/// Discovers modpacks and downloads exact, verified client/server pack files through the official
/// CurseForge v1 API.
/// The caller supplies its own approved API key for each operation. The key is never cached and is
/// never installed as an <see cref="HttpClient.DefaultRequestHeaders"/> value.
/// </summary>
public sealed class CurseForgeModpackProvider
{
    private const int MaximumApiJsonBytes = 8 * 1024 * 1024;
    private const long MaximumPackFileBytes = 2L * 1024 * 1024 * 1024;
    private static readonly Uri ApiBaseUri = new("https://api.curseforge.com/");
    private const int MaximumPageSize = 50;
    private const int MaximumResultIndex = 10_000;
    private readonly HttpClient _apiClient;
    private readonly HttpClient _downloadClient;
    private readonly string _userAgent;

    public CurseForgeModpackProvider(
        HttpClient apiClient,
        HttpClient downloadClient,
        string userAgent)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(downloadClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        if (ReferenceEquals(apiClient, downloadClient))
        {
            throw new ArgumentException("CurseForge API 與 CDN 下載必須使用不同的 HttpClient。", nameof(downloadClient));
        }

        _apiClient = apiClient;
        _downloadClient = downloadClient;
        _userAgent = userAgent.Trim();
        EnsureNoDefaultApiKeyHeaders();
    }

    public async Task<CurseForgeCatalogIds> ResolveCatalogAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var minecraftGameId = 0;
        for (var index = 0; index < MaximumResultIndex; index += MaximumPageSize)
        {
            using var document = await GetApiJsonAsync(
                apiKey,
                BuildApiUri("v1/games", new Dictionary<string, string>
                {
                    ["index"] = Format(index),
                    ["pageSize"] = Format(MaximumPageSize)
                }),
                cancellationToken).ConfigureAwait(false);
            var data = ReadDataArray(document.RootElement, "games");
            foreach (var game in data.EnumerateArray())
            {
                var slug = ReadOptionalString(game, "slug");
                if (slug?.Equals("minecraft", StringComparison.OrdinalIgnoreCase) == true)
                {
                    minecraftGameId = ReadRequiredPositiveInt32(game, "id", "game");
                    break;
                }
            }

            if (minecraftGameId > 0)
            {
                break;
            }

            var pagination = ReadPagination(document.RootElement, index, MaximumPageSize, data.GetArrayLength());
            if (pagination.ResultCount < MaximumPageSize
                || pagination.Index + pagination.ResultCount >= pagination.TotalCount)
            {
                break;
            }
        }

        if (minecraftGameId <= 0)
        {
            throw new InvalidDataException("CurseForge API 沒有回傳可供此 API Key 使用的 Minecraft game。 ");
        }

        using var categoryDocument = await GetApiJsonAsync(
            apiKey,
            BuildApiUri("v1/categories", new Dictionary<string, string>
            {
                ["gameId"] = Format(minecraftGameId)
            }),
            cancellationToken).ConfigureAwait(false);
        var categories = ReadDataArray(categoryDocument.RootElement, "categories");
        var modpacksClassId = 0;
        foreach (var category in categories.EnumerateArray())
        {
            var isClass = ReadOptionalBoolean(category, "isClass") == true;
            var slug = ReadOptionalString(category, "slug");
            var name = ReadOptionalString(category, "name");
            if (isClass
                && (slug?.Equals("modpacks", StringComparison.OrdinalIgnoreCase) == true
                    || name?.Equals("modpacks", StringComparison.OrdinalIgnoreCase) == true))
            {
                modpacksClassId = ReadRequiredPositiveInt32(category, "id", "category");
                break;
            }
        }

        if (modpacksClassId <= 0)
        {
            throw new InvalidDataException("CurseForge API 沒有回傳 Minecraft 的 Modpacks class。 ");
        }

        return new CurseForgeCatalogIds(minecraftGameId, modpacksClassId);
    }

    public async Task<CurseForgeModpackSearchPage> SearchAsync(
        string apiKey,
        CurseForgeModpackSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIndex(request.Index);
        var pageSize = NormalizePageSize(request.PageSize);
        ValidateWindow(request.Index, pageSize);
        if (!Enum.IsDefined(request.ModLoader))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "不支援的 CurseForge Mod Loader。 ");
        }

        if (!Enum.IsDefined(request.SortField))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "不支援的 CurseForge 排序欄位。 ");
        }

        if (request.CategoryId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "CurseForge categoryId 必須是正整數。 ");
        }

        var catalog = await ResolveCatalogAsync(apiKey, cancellationToken).ConfigureAwait(false);
        var query = new Dictionary<string, string>
        {
            ["gameId"] = Format(catalog.MinecraftGameId),
            ["classId"] = Format(catalog.ModpacksClassId),
            ["searchFilter"] = request.Query?.Trim() ?? string.Empty,
            ["sortField"] = Format((int)request.SortField),
            ["sortOrder"] = request.SortDescending ? "desc" : "asc",
            ["index"] = Format(request.Index),
            ["pageSize"] = Format(pageSize)
        };
        if (!string.IsNullOrWhiteSpace(request.GameVersion))
        {
            query["gameVersion"] = request.GameVersion.Trim();
        }

        if (request.ModLoader != CurseForgeModLoaderType.Any)
        {
            query["modLoaderType"] = Format((int)request.ModLoader);
        }

        if (request.CategoryId is { } categoryId)
        {
            query["categoryId"] = Format(categoryId);
        }

        using var document = await GetApiJsonAsync(
            apiKey,
            BuildApiUri("v1/mods/search", query),
            cancellationToken).ConfigureAwait(false);
        var data = ReadDataArray(document.RootElement, "mods/search");
        var projects = data.EnumerateArray().Select(ParseProject).ToArray();
        return new CurseForgeModpackSearchPage(
            catalog,
            projects,
            ReadPagination(document.RootElement, request.Index, pageSize, projects.Length));
    }

    public async Task<CurseForgeModpackProject> GetProjectAsync(
        string apiKey,
        int modId,
        CancellationToken cancellationToken = default)
    {
        ValidatePositiveId(modId, nameof(modId));
        using var document = await GetApiJsonAsync(
            apiKey,
            new Uri(ApiBaseUri, $"v1/mods/{modId.ToString(CultureInfo.InvariantCulture)}"),
            cancellationToken).ConfigureAwait(false);
        var data = ReadDataObject(document.RootElement, "mod");
        var project = ParseProject(data);
        if (project.ModId != modId)
        {
            throw new InvalidDataException("CurseForge 專案回應的 modId 與要求不符。 ");
        }

        return project;
    }

    public async Task<CurseForgeModpackFilePage> GetFilesAsync(
        string apiKey,
        int modId,
        string? gameVersion = null,
        CurseForgeModLoaderType modLoader = CurseForgeModLoaderType.Any,
        int index = 0,
        int pageSize = MaximumPageSize,
        CancellationToken cancellationToken = default)
    {
        ValidatePositiveId(modId, nameof(modId));
        ValidateIndex(index);
        pageSize = NormalizePageSize(pageSize);
        ValidateWindow(index, pageSize);
        if (!Enum.IsDefined(modLoader))
        {
            throw new ArgumentOutOfRangeException(nameof(modLoader));
        }

        var query = new Dictionary<string, string>
        {
            ["index"] = Format(index),
            ["pageSize"] = Format(pageSize)
        };
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            query["gameVersion"] = gameVersion.Trim();
        }

        if (modLoader != CurseForgeModLoaderType.Any)
        {
            query["modLoaderType"] = Format((int)modLoader);
        }

        using var document = await GetApiJsonAsync(
            apiKey,
            BuildApiUri($"v1/mods/{Format(modId)}/files", query),
            cancellationToken).ConfigureAwait(false);
        var data = ReadDataArray(document.RootElement, "mod files");
        var files = data.EnumerateArray().Select(ParseFile).ToArray();
        foreach (var file in files)
        {
            ValidateFileIdentity(file, modId, file.FileId);
        }

        return new CurseForgeModpackFilePage(
            files,
            ReadPagination(document.RootElement, index, pageSize, files.Length));
    }

    public async Task<CurseForgeServerPackResolution> ResolveServerPackAsync(
        string apiKey,
        int modId,
        int selectedFileId,
        CancellationToken cancellationToken = default)
    {
        ValidatePositiveId(modId, nameof(modId));
        ValidatePositiveId(selectedFileId, nameof(selectedFileId));
        var project = await GetProjectAsync(apiKey, modId, cancellationToken).ConfigureAwait(false);
        if (!project.IsAvailable || !project.AllowModDistribution)
        {
            return Resolution(
                CurseForgeServerPackResolutionStatus.DistributionUnavailable,
                project,
                null,
                null,
                "作者沒有開放此 CurseForge 專案供第三方下載。 ");
        }

        var selectedFile = await GetFileAsync(apiKey, modId, selectedFileId, cancellationToken)
            .ConfigureAwait(false);
        ValidateFileIdentity(selectedFile, modId, selectedFileId);
        if (!selectedFile.IsAvailable)
        {
            return Resolution(
                CurseForgeServerPackResolutionStatus.SelectedFileUnavailable,
                project,
                selectedFile,
                null,
                "選取的 CurseForge 模組包版本目前無法下載。 ");
        }

        if (selectedFile.IsServerPack)
        {
            return Resolution(
                CurseForgeServerPackResolutionStatus.Available,
                project,
                selectedFile,
                selectedFile,
                "已找到作者提供的官方 Server Pack。 ");
        }

        if (selectedFile.ServerPackFileId is not { } serverPackFileId)
        {
            return Resolution(
                CurseForgeServerPackResolutionStatus.NoOfficialServerPack,
                project,
                selectedFile,
                null,
                "此模組包版本沒有作者關聯的官方 Server Pack。 ");
        }

        CurseForgeModpackFile serverPackFile;
        try
        {
            serverPackFile = await GetFileAsync(apiKey, modId, serverPackFileId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CurseForgeApiException exception) when (exception.ErrorCode == CurseForgeApiErrorCode.NotFound)
        {
            return Resolution(
                CurseForgeServerPackResolutionStatus.OfficialServerPackUnavailable,
                project,
                selectedFile,
                null,
                "作者關聯的 Server Pack 已不存在或不允許第三方存取。 ");
        }

        ValidateFileIdentity(serverPackFile, modId, serverPackFileId);
        if (!serverPackFile.IsAvailable || !serverPackFile.IsServerPack)
        {
            return Resolution(
                CurseForgeServerPackResolutionStatus.OfficialServerPackUnavailable,
                project,
                selectedFile,
                serverPackFile,
                "關聯檔案未通過 Server Pack 可用性驗證。 ");
        }

        return Resolution(
            CurseForgeServerPackResolutionStatus.Available,
            project,
            selectedFile,
            serverPackFile,
            "已找到作者提供的官方 Server Pack。 ");
    }

    public async Task<Uri> GetDownloadUriAsync(
        string apiKey,
        int modId,
        int fileId,
        CancellationToken cancellationToken = default)
    {
        ValidatePositiveId(modId, nameof(modId));
        ValidatePositiveId(fileId, nameof(fileId));
        using var document = await GetApiJsonAsync(
            apiKey,
            new Uri(ApiBaseUri, $"v1/mods/{Format(modId)}/files/{Format(fileId)}/download-url"),
            cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(data.GetString(), UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidDataException("CurseForge 沒有回傳有效的 HTTPS 下載 URL。 ");
        }

        return uri;
    }

    public async Task<CurseForgeModpackDownloadResult> DownloadServerPackAsync(
        string apiKey,
        int modId,
        int serverPackFileId,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
        => await DownloadVerifiedFileAsync(
                apiKey,
                modId,
                serverPackFileId,
                CurseForgeModpackFileRole.ServerPack,
                destinationPath,
                progress,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Downloads an exact CurseForge file after re-reading its project and file metadata, enforcing
    /// the expected client/server role, and validating both the API-declared length and strongest
    /// available API hash before atomically publishing the destination. The API key is never sent
    /// to the CDN client.
    /// </summary>
    public async Task<CurseForgeModpackDownloadResult> DownloadVerifiedFileAsync(
        string apiKey,
        int modId,
        int fileId,
        CurseForgeModpackFileRole expectedRole,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ValidatePositiveId(modId, nameof(modId));
        ValidatePositiveId(fileId, nameof(fileId));
        if (!Enum.IsDefined(expectedRole))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRole));
        }

        // Re-read all author-controlled metadata immediately before downloading. A previous search
        // result must never be treated as authority after distribution settings or files change.
        var project = await GetProjectAsync(apiKey, modId, cancellationToken).ConfigureAwait(false);
        if (!project.IsAvailable || !project.AllowModDistribution)
        {
            throw new CurseForgeServerPackException(
                CurseForgeServerPackResolutionStatus.DistributionUnavailable,
                "作者沒有開放此 CurseForge 專案供第三方下載。 ");
        }

        var file = await GetFileAsync(apiKey, modId, fileId, cancellationToken)
            .ConfigureAwait(false);
        ValidateFileIdentity(file, modId, fileId);
        if (!file.IsAvailable)
        {
            throw new CurseForgeServerPackException(
                expectedRole == CurseForgeModpackFileRole.ServerPack
                    ? CurseForgeServerPackResolutionStatus.OfficialServerPackUnavailable
                    : CurseForgeServerPackResolutionStatus.SelectedFileUnavailable,
                "指定的 CurseForge 檔案目前無法下載。 ");
        }

        var roleMatches = expectedRole switch
        {
            CurseForgeModpackFileRole.ClientPack => !file.IsServerPack,
            CurseForgeModpackFileRole.ServerPack => file.IsServerPack,
            _ => false
        };
        if (!roleMatches)
        {
            throw new InvalidDataException(
                $"CurseForge 檔案角色不符：預期 {expectedRole}，API 回傳 "
                + (file.IsServerPack ? "ServerPack。" : "ClientPack。"));
        }

        if (file.FileLength is < 1 or > MaximumPackFileBytes)
        {
            throw new InvalidDataException(
                $"CurseForge 檔案大小必須介於 1 byte 與 {MaximumPackFileBytes:N0} bytes。 ");
        }

        var expectedHash = SelectVerificationHash(file.Hashes);
        var downloadUri = await GetDownloadUriAsync(apiKey, modId, fileId, cancellationToken)
            .ConfigureAwait(false);
        var destination = Path.GetFullPath(destinationPath);
        if (File.Exists(destination))
        {
            throw new IOException($"下載目的檔已存在：{destination}");
        }

        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("下載目的路徑沒有父資料夾。 ");
        Directory.CreateDirectory(parent);
        var partialPath = destination + $".{Guid.NewGuid():N}.partial";
        try
        {
            await DownloadAndVerifyAsync(
                downloadUri,
                partialPath,
                file.FileLength,
                expectedHash,
                progress,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, destination, overwrite: false);
        }
        catch
        {
            TryDeleteFile(partialPath);
            throw;
        }

        return new CurseForgeModpackDownloadResult(
            modId,
            fileId,
            file.FileName,
            destination,
            file.FileLength,
            expectedHash.Algorithm,
            expectedHash.Value);
    }

    private async Task<CurseForgeModpackFile> GetFileAsync(
        string apiKey,
        int modId,
        int fileId,
        CancellationToken cancellationToken)
    {
        using var document = await GetApiJsonAsync(
            apiKey,
            new Uri(ApiBaseUri, $"v1/mods/{Format(modId)}/files/{Format(fileId)}"),
            cancellationToken).ConfigureAwait(false);
        return ParseFile(ReadDataObject(document.RootElement, "mod file"));
    }

    private async Task<JsonDocument> GetApiJsonAsync(
        string apiKey,
        Uri uri,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        EnsureNoDefaultApiKeyHeaders();
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals(ApiBaseUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("CurseForge API Key 只能傳送到 api.curseforge.com。 ");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey.Trim());
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _apiClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null
            || !finalUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !finalUri.Host.Equals(ApiBaseUri.Host, StringComparison.OrdinalIgnoreCase)
            || !finalUri.Equals(uri))
        {
            // Production disables redirects before the request is sent. This second check makes a
            // custom/misconfigured transport fail closed if it nevertheless reports a changed URI.
            throw new InvalidDataException("CurseForge API 回應的 final URI 與原始官方 API URI 不符。 ");
        }

        if ((int)response.StatusCode is >= 300 and <= 399)
        {
            // This client must have redirects disabled. Never read a redirect response body and
            // never replay the per-request x-api-key to a Location supplied by the server.
            throw CreateApiException(response);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException(response);
        }

        if (response.Content.Headers.ContentLength is { } declaredLength
            && declaredLength > MaximumApiJsonBytes)
        {
            throw new InvalidDataException(
                $"CurseForge API JSON 超過 {MaximumApiJsonBytes:N0} bytes 的安全上限。 ");
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = await ReadBoundedApiJsonAsync(stream, cancellationToken).ConfigureAwait(false);
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("CurseForge API 回傳的 JSON 無效。 ", exception);
        }
    }

    private static async Task<byte[]> ReadBoundedApiJsonAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(capacity: 64 * 1024);
        var buffer = new byte[32 * 1024];
        var total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (total > MaximumApiJsonBytes - count)
            {
                throw new InvalidDataException(
                    $"CurseForge API JSON 超過 {MaximumApiJsonBytes:N0} bytes 的安全上限。 ");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            total += count;
        }

        return output.ToArray();
    }

    private async Task DownloadAndVerifyAsync(
        Uri source,
        string partialPath,
        long expectedSize,
        CurseForgeFileHash expectedHash,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        EnsureNoDefaultApiKeyHeaders();
        if (_downloadClient.DefaultRequestHeaders.Contains("x-api-key"))
        {
            throw new InvalidOperationException("CDN 下載 HttpClient 不可包含 x-api-key。 ");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await _downloadClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"CurseForge 檔案下載失敗：HTTP {(int)response.StatusCode}。",
                null,
                response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != expectedSize)
        {
            throw new InvalidDataException(
                $"CurseForge 檔案的 Content-Length 不符，預期 {expectedSize} bytes，實際 {contentLength} bytes。 ");
        }

        var hashName = expectedHash.Algorithm == CurseForgeFileHashAlgorithm.Sha1
            ? HashAlgorithmName.SHA1
            : HashAlgorithmName.MD5;
        using var hasher = IncrementalHash.CreateHash(hashName);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            partialPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            total += count;
            if (total > expectedSize)
            {
                throw new InvalidDataException("CurseForge 檔案大於 API 宣告的檔案大小。 ");
            }

            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            hasher.AppendData(buffer, 0, count);
            if (expectedSize > 0)
            {
                progress?.Report(Math.Clamp((double)total / expectedSize, 0d, 1d));
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        if (total != expectedSize)
        {
            throw new InvalidDataException(
                $"CurseForge 檔案大小不符，預期 {expectedSize} bytes，實際 {total} bytes。 ");
        }

        var actual = hasher.GetHashAndReset();
        var expected = Convert.FromHexString(expectedHash.Value);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new InvalidDataException("CurseForge 檔案雜湊驗證失敗。 ");
        }

        progress?.Report(1d);
    }

    private void EnsureNoDefaultApiKeyHeaders()
    {
        if (_apiClient.DefaultRequestHeaders.Contains("x-api-key")
            || _downloadClient.DefaultRequestHeaders.Contains("x-api-key"))
        {
            throw new InvalidOperationException("x-api-key 不可設定為 HttpClient 的預設 Header。 ");
        }
    }

    private static CurseForgeApiException CreateApiException(HttpResponseMessage response)
    {
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => CurseForgeApiErrorCode.InvalidApiKey,
            HttpStatusCode.Forbidden => CurseForgeApiErrorCode.Forbidden,
            HttpStatusCode.NotFound => CurseForgeApiErrorCode.NotFound,
            HttpStatusCode.TooManyRequests => CurseForgeApiErrorCode.RateLimited,
            _ => CurseForgeApiErrorCode.ApiFailure
        };
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date)
        {
            retryAfter = date - DateTimeOffset.UtcNow;
            if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
        }

        // Response bodies are deliberately excluded: a proxy may echo a request header, and API
        // credentials must never be copied into exceptions, logs or UI error text.
        return new CurseForgeApiException(code, response.StatusCode, retryAfter);
    }

    private static CurseForgeModpackProject ParseProject(JsonElement element)
    {
        var website = element.TryGetProperty("links", out var links)
            ? ReadOptionalUri(links, "websiteUrl")
            : null;
        Uri? icon = null;
        if (element.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object)
        {
            icon = ReadOptionalUri(logo, "thumbnailUrl") ?? ReadOptionalUri(logo, "url");
        }

        Uri? preview = null;
        if (element.TryGetProperty("screenshots", out var screenshots)
            && screenshots.ValueKind == JsonValueKind.Array)
        {
            foreach (var screenshot in screenshots.EnumerateArray())
            {
                if (screenshot.ValueKind != JsonValueKind.Object) continue;
                preview = ReadOptionalUri(screenshot, "thumbnailUrl")
                          ?? ReadOptionalUri(screenshot, "url");
                if (preview is not null) break;
            }
        }

        var author = string.Empty;
        if (element.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array)
        {
            author = authors.EnumerateArray()
                .Select(static item => ReadOptionalString(item, "name"))
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        return new CurseForgeModpackProject(
            ReadRequiredPositiveInt32(element, "id", "mod"),
            ReadRequiredPositiveInt32(element, "gameId", "mod"),
            ReadRequiredPositiveInt32(element, "classId", "mod"),
            ReadRequiredString(element, "slug", "mod"),
            ReadRequiredString(element, "name", "mod"),
            ReadOptionalString(element, "summary") ?? string.Empty,
            author,
            website,
            icon,
            ReadRequiredBoolean(element, "isAvailable", "mod"),
            ReadRequiredBoolean(element, "allowModDistribution", "mod"),
            ReadOptionalInt64(element, "downloadCount") ?? 0,
            ReadOptionalDate(element, "dateModified"),
            preview);
    }

    private static CurseForgeModpackFile ParseFile(JsonElement element)
    {
        var hashes = new List<CurseForgeFileHash>();
        if (element.TryGetProperty("hashes", out var hashArray) && hashArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var hash in hashArray.EnumerateArray())
            {
                var algorithmValue = ReadOptionalInt32(hash, "algo");
                var value = ReadOptionalString(hash, "value");
                if (algorithmValue is 1 or 2 && !string.IsNullOrWhiteSpace(value))
                {
                    hashes.Add(new CurseForgeFileHash(
                        (CurseForgeFileHashAlgorithm)algorithmValue.Value,
                        value.Trim()));
                }
            }
        }

        var length = ReadRequiredInt64(element, "fileLength", "file");
        if (length < 0)
        {
            throw new InvalidDataException("CurseForge fileLength 不可小於 0。 ");
        }

        var serverPackFileId = ReadOptionalInt32(element, "serverPackFileId");
        if (serverPackFileId <= 0) serverPackFileId = null;
        return new CurseForgeModpackFile(
            ReadRequiredPositiveInt32(element, "id", "file"),
            ReadRequiredPositiveInt32(element, "gameId", "file"),
            ReadRequiredPositiveInt32(element, "modId", "file"),
            ReadRequiredString(element, "displayName", "file"),
            ReadRequiredString(element, "fileName", "file"),
            ReadRequiredBoolean(element, "isAvailable", "file"),
            ReadOptionalBoolean(element, "isServerPack") == true,
            serverPackFileId,
            ReadOptionalInt32(element, "releaseType") ?? 0,
            ReadOptionalInt32(element, "fileStatus") ?? 0,
            length,
            ReadOptionalDate(element, "fileDate"),
            ReadStringArray(element, "gameVersions"),
            hashes);
    }

    private static CurseForgeFileHash SelectVerificationHash(IReadOnlyList<CurseForgeFileHash> hashes)
    {
        var sha1 = hashes.FirstOrDefault(static hash =>
            hash.Algorithm == CurseForgeFileHashAlgorithm.Sha1 && IsHex(hash.Value, 40));
        if (sha1 is not null) return sha1 with { Value = sha1.Value.ToUpperInvariant() };

        var md5 = hashes.FirstOrDefault(static hash =>
            hash.Algorithm == CurseForgeFileHashAlgorithm.Md5 && IsHex(hash.Value, 32));
        if (md5 is not null) return md5 with { Value = md5.Value.ToUpperInvariant() };

        throw new InvalidDataException("CurseForge Server Pack 沒有可驗證的 SHA-1 或 MD5。 ");
    }

    private static CurseForgeServerPackResolution Resolution(
        CurseForgeServerPackResolutionStatus status,
        CurseForgeModpackProject project,
        CurseForgeModpackFile? selectedFile,
        CurseForgeModpackFile? serverPackFile,
        string message)
        => new(status, project, selectedFile, serverPackFile, message.Trim());

    private static void ValidateFileIdentity(CurseForgeModpackFile file, int modId, int fileId)
    {
        if (file.ModId != modId || file.FileId != fileId)
        {
            throw new InvalidDataException("CurseForge 檔案回應的 modId 或 fileId 與要求不符。 ");
        }
    }

    private static Uri BuildApiUri(string relativePath, IReadOnlyDictionary<string, string> query)
    {
        var queryText = string.Join('&', query.Select(static pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new Uri(ApiBaseUri, string.IsNullOrEmpty(queryText) ? relativePath : $"{relativePath}?{queryText}");
    }

    private static JsonElement ReadDataArray(JsonElement root, string responseName)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"CurseForge {responseName} 回應缺少 data 陣列。 ");
        }

        return data;
    }

    private static JsonElement ReadDataObject(JsonElement root, string responseName)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"CurseForge {responseName} 回應缺少 data 物件。 ");
        }

        return data;
    }

    private static CurseForgePagination ReadPagination(
        JsonElement root,
        int fallbackIndex,
        int fallbackPageSize,
        int fallbackResultCount)
    {
        if (!root.TryGetProperty("pagination", out var pagination)
            || pagination.ValueKind != JsonValueKind.Object)
        {
            return new CurseForgePagination(
                fallbackIndex,
                fallbackPageSize,
                fallbackResultCount,
                fallbackIndex + fallbackResultCount);
        }

        return new CurseForgePagination(
            ReadOptionalInt32(pagination, "index") ?? fallbackIndex,
            ReadOptionalInt32(pagination, "pageSize") ?? fallbackPageSize,
            ReadOptionalInt32(pagination, "resultCount") ?? fallbackResultCount,
            ReadOptionalInt32(pagination, "totalCount") ?? fallbackIndex + fallbackResultCount);
    }

    private static string ReadRequiredString(JsonElement element, string property, string responseName)
        => ReadOptionalString(element, property)
           ?? throw new InvalidDataException($"CurseForge {responseName} 回應缺少 {property}。 ");

    private static string? ReadOptionalString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadRequiredBoolean(JsonElement element, string property, string responseName)
        => ReadOptionalBoolean(element, property)
           ?? throw new InvalidDataException($"CurseForge {responseName} 回應缺少 {property}。 ");

    private static bool? ReadOptionalBoolean(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static int ReadRequiredPositiveInt32(JsonElement element, string property, string responseName)
    {
        var value = ReadOptionalInt32(element, property);
        return value is > 0
            ? value.Value
            : throw new InvalidDataException($"CurseForge {responseName} 回應的 {property} 無效。 ");
    }

    private static long ReadRequiredInt64(JsonElement element, string property, string responseName)
        => ReadOptionalInt64(element, property)
           ?? throw new InvalidDataException($"CurseForge {responseName} 回應缺少 {property}。 ");

    private static int? ReadOptionalInt32(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static long? ReadOptionalInt64(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static Uri? ReadOptionalUri(JsonElement element, string property)
        => Uri.TryCreate(ReadOptionalString(element, property), UriKind.Absolute, out var uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;

    private static DateTimeOffset? ReadOptionalDate(JsonElement element, string property)
        => DateTimeOffset.TryParse(
            ReadOptionalString(element, property),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()!)
                .ToArray()
            : [];

    private static bool IsHex(string value, int expectedLength)
        => value.Length == expectedLength && value.All(Uri.IsHexDigit);

    private static int NormalizePageSize(int pageSize)
    {
        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "PageSize 必須大於 0。 ");
        }

        return Math.Min(pageSize, MaximumPageSize);
    }

    private static void ValidateIndex(int index)
    {
        if (index < 0 || index >= MaximumResultIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Index 必須介於 0 與 9,999。 ");
        }
    }

    private static void ValidateWindow(int index, int pageSize)
    {
        if (index + pageSize > MaximumResultIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "CurseForge 查詢的 index + pageSize 不可超過 10,000。 ");
        }
    }

    private static void ValidatePositiveId(int value, string parameterName)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(parameterName);
    }

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Preserve the original download, verification or cancellation exception.
        }
    }
}

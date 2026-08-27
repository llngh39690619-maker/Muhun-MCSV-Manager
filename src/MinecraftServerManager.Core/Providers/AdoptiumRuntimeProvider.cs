using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Providers;

public sealed record JavaRuntimePackage(
    int MajorVersion,
    string ReleaseName,
    string ImageType,
    string Vendor,
    Uri DownloadUri,
    string FileName,
    string Sha256,
    long Size);

public sealed record InstalledJavaRuntime(
    int MajorVersion,
    string ReleaseName,
    string ImageType,
    string Vendor,
    string InstallDirectory,
    string JavaExecutablePath);

public sealed record InstalledJavaDevelopmentKit(
    int MajorVersion,
    string ReleaseName,
    string Vendor,
    string InstallDirectory,
    string JavaExecutablePath,
    string JavacExecutablePath);

public sealed partial class AdoptiumRuntimeProvider
{
    private static readonly Uri BaseUri = new("https://api.adoptium.net/");
    private const long MaximumApiResponseBytes = 16L * 1024 * 1024;
    private const long MaximumRuntimeArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumArchiveEntries = 100_000;
    private const long MaximumExtractedEntryBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumExtractedTotalBytes = 4L * 1024 * 1024 * 1024;
    private const double MaximumCompressionRatio = 1_000d;
    private const int DosReparsePointAttribute = (int)FileAttributes.ReparsePoint;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFileType = 0x8000;
    private const int UnixDirectoryType = 0x4000;
    private const int UnixSymbolicLinkType = 0xA000;
    private const int MaximumJavaVersionOutputLines = 1_024;
    private const int MaximumJavaVersionOutputCharacters = 256 * 1024;
    private static readonly TimeSpan JavaVersionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan JavaVersionDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RuntimeInstallGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
    private readonly HttpClient _httpClient;
    private readonly VerifiedDownloadClient _downloadClient;

    public AdoptiumRuntimeProvider(HttpClient httpClient, string userAgent)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= BaseUri;
        // Runtime ZIPs are large; keep a generous bound while still allowing caller cancellation.
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }

        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _downloadClient = new VerifiedDownloadClient(_httpClient);
    }

    public async Task<JavaRuntimePackage?> GetLatestPackageAsync(int majorVersion, CancellationToken cancellationToken = default)
    {
        if (majorVersion is < 8 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(majorVersion));
        }

        return await QueryPackageAsync(majorVersion, "jre", cancellationToken).ConfigureAwait(false)
            ?? await QueryPackageAsync(majorVersion, "jdk", cancellationToken).ConfigureAwait(false);
    }

    public async Task<JavaRuntimePackage?> GetLatestJdkPackageAsync(
        int majorVersion,
        CancellationToken cancellationToken = default)
    {
        if (majorVersion is < 8 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(majorVersion));
        }

        return await QueryPackageAsync(majorVersion, "jdk", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<InstalledJavaRuntime> InstallAsync(
        int majorVersion,
        string runtimeRoot,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var package = await GetLatestPackageAsync(majorVersion, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Adoptium 找不到 Windows x64 Java {majorVersion} JRE 或 JDK。");
        return await InstallPackageAsync(
                package,
                runtimeRoot,
                requireJdk: false,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<InstalledJavaDevelopmentKit> InstallJdkAsync(
        int majorVersion,
        string runtimeRoot,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var package = await GetLatestJdkPackageAsync(majorVersion, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Adoptium 找不到 Windows x64 Java {majorVersion} JDK。");
        var installed = await InstallPackageAsync(
                package,
                runtimeRoot,
                requireJdk: true,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        var javac = Path.Combine(installed.InstallDirectory, "bin", "javac.exe");
        return new InstalledJavaDevelopmentKit(
            installed.MajorVersion,
            installed.ReleaseName,
            installed.Vendor,
            installed.InstallDirectory,
            installed.JavaExecutablePath,
            javac);
    }

    private async Task<InstalledJavaRuntime> InstallPackageAsync(
        JavaRuntimePackage package,
        string runtimeRoot,
        bool requireJdk,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (requireJdk && !package.ImageType.Equals("jdk", StringComparison.Ordinal))
        {
            throw new InvalidDataException("JDK 安裝流程收到非 JDK package。");
        }

        var majorVersion = package.MajorVersion;
        var fullRuntimeRoot = Path.GetFullPath(runtimeRoot);
        Directory.CreateDirectory(fullRuntimeRoot);
        var safeRelease = SanitizeSegment(package.ReleaseName);
        var destination = Path.Combine(fullRuntimeRoot, $"temurin-{package.ImageType}-{majorVersion}-{safeRelease}");
        var canonicalRuntimeRoot = SafePath.GetCanonicalExistingPath(
            fullRuntimeRoot,
            followFinalReparsePoint: true);
        var gateKey = Path.Combine(canonicalRuntimeRoot, Path.GetFileName(destination));
        using var installLease = await AcquireRuntimeInstallGateAsync(gateKey, cancellationToken)
            .ConfigureAwait(false);

        var stagingRoot = Path.Combine(fullRuntimeRoot, ".staging");
        Directory.CreateDirectory(stagingRoot);
        if (File.GetAttributes(stagingRoot).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                $"Java Runtime staging 不得是 symbolic link 或 reparse point：{stagingRoot}");
        }

        var existingJava = Path.Combine(destination, "bin", "java.exe");
        if (File.Exists(existingJava))
        {
            SafePath.EnsureNoReparsePointsUnderRoot(fullRuntimeRoot, existingJava);
            EnsureRegularExecutable(existingJava, "既有 Java");
            var existingJavac = Path.Combine(destination, "bin", "javac.exe");
            if (requireJdk)
            {
                if (!File.Exists(existingJavac))
                {
                    throw new InvalidDataException(
                        $"既有 Java Runtime 缺少 JDK javac：{destination}");
                }

                SafePath.EnsureNoReparsePointsUnderRoot(fullRuntimeRoot, existingJavac);
                EnsureRegularExecutable(existingJavac, "既有 JDK javac");
            }

            var existingMajor = await ReadJavaMajorVersionAsync(existingJava, cancellationToken)
                .ConfigureAwait(false);
            if (existingMajor != majorVersion)
            {
                throw new InvalidDataException(
                    $"既有 Java Runtime 版本不符，預期 {majorVersion}，實際 {existingMajor}：{destination}");
            }

            if (requireJdk)
            {
                var existingJavacMajor = await ReadJavacMajorVersionAsync(
                        existingJavac,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (existingJavacMajor != majorVersion)
                {
                    throw new InvalidDataException(
                        $"既有 javac 版本不符，預期 {majorVersion}，"
                        + $"實際 {existingJavacMajor}：{destination}");
                }
            }

            return new InstalledJavaRuntime(majorVersion, package.ReleaseName, package.ImageType, package.Vendor, destination, existingJava);
        }

        var operationId = Guid.NewGuid().ToString("N");
        var archive = Path.Combine(stagingRoot, operationId + ".partial");
        var extraction = Path.Combine(stagingRoot, operationId);

        try
        {
            await _downloadClient.DownloadAsync(
                package.DownloadUri,
                archive,
                HashAlgorithmName.SHA256,
                package.Sha256,
                package.Size,
                progress,
                cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(extraction);
            await ExtractZipSafelyAsync(archive, extraction, cancellationToken).ConfigureAwait(false);

            var discoveredJava = Directory.EnumerateFiles(extraction, "java.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path => string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(path)),
                    "bin",
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Java 封裝內找不到 bin/java.exe。");

            var packageRoot = Directory.GetParent(Path.GetDirectoryName(discoveredJava)!)?.FullName
                ?? throw new InvalidDataException("無法判斷 Java 封裝根目錄。");
            EnsureWithinRoot(extraction, packageRoot);
            SafePath.EnsureTreeContainsNoReparsePoints(packageRoot);
            SafePath.EnsureNoReparsePointsUnderRoot(extraction, discoveredJava);
            EnsureRegularExecutable(discoveredJava, "解壓縮後 Java");
            var discoveredJavac = Path.Combine(
                Path.GetDirectoryName(discoveredJava)!,
                "javac.exe");
            if (requireJdk)
            {
                EnsureWithinRoot(extraction, discoveredJavac);
                EnsureRegularExecutable(discoveredJavac, "解壓縮後 JDK javac");
                SafePath.EnsureNoReparsePointsUnderRoot(extraction, discoveredJavac);
            }

            var actualMajor = await ReadJavaMajorVersionAsync(discoveredJava, cancellationToken).ConfigureAwait(false);
            if (actualMajor != majorVersion)
            {
                throw new InvalidDataException($"Java 版本驗證失敗，預期 {majorVersion}，實際 {actualMajor}。");
            }

            if (requireJdk)
            {
                var actualJavacMajor = await ReadJavacMajorVersionAsync(
                        discoveredJavac,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (actualJavacMajor != majorVersion)
                {
                    throw new InvalidDataException(
                        $"javac 版本驗證失敗，預期 {majorVersion}，實際 {actualJavacMajor}。");
                }
            }

            if (Directory.Exists(destination))
            {
                throw new IOException($"Runtime 目的資料夾已存在但不完整：{destination}");
            }

            await MoveDirectoryWithRetryAsync(
                    packageRoot,
                    destination,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                SafePath.EnsureTreeContainsNoReparsePoints(destination);
                var javaExecutable = Path.Combine(destination, "bin", "java.exe");
                if (!File.Exists(javaExecutable))
                {
                    throw new InvalidDataException("Java 安裝完成後找不到 bin/java.exe。");
                }
                SafePath.EnsureNoReparsePointsUnderRoot(fullRuntimeRoot, javaExecutable);
                EnsureRegularExecutable(javaExecutable, "安裝後 Java");
                if (requireJdk)
                {
                    var javacExecutable = Path.Combine(destination, "bin", "javac.exe");
                    EnsureRegularExecutable(javacExecutable, "安裝後 JDK javac");
                    SafePath.EnsureNoReparsePointsUnderRoot(fullRuntimeRoot, javacExecutable);
                }

                return new InstalledJavaRuntime(
                    majorVersion,
                    package.ReleaseName,
                    package.ImageType,
                    package.Vendor,
                    destination,
                    javaExecutable);
            }
            catch
            {
                await DeleteOwnedPathAsync(fullRuntimeRoot, destination).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            await DeleteOwnedPathAsync(fullRuntimeRoot, archive).ConfigureAwait(false);
            await DeleteOwnedPathAsync(fullRuntimeRoot, extraction).ConfigureAwait(false);
        }
    }

    internal static async ValueTask<IDisposable> AcquireRuntimeInstallGateAsync(
        string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var gate = RuntimeInstallGates.GetOrAdd(
            key,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new RuntimeInstallGateLease(gate);
    }

    private sealed class RuntimeInstallGateLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }

    public static Task<int> ReadJavaMajorVersionAsync(
        string javaExecutable,
        CancellationToken cancellationToken = default)
        => ReadJavaToolMajorVersionAsync(
            javaExecutable,
            "java",
            JavaVersionRegex(),
            cancellationToken);

    public static Task<int> ReadJavacMajorVersionAsync(
        string javacExecutable,
        CancellationToken cancellationToken = default)
        => ReadJavaToolMajorVersionAsync(
            javacExecutable,
            "javac",
            JavacVersionRegex(),
            cancellationToken);

    private static async Task<int> ReadJavaToolMajorVersionAsync(
        string executable,
        string toolName,
        Regex versionRegex,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException($"找不到 {toolName} 執行檔。", executable);
        }

        using var process = new Process
        {
            StartInfo = BuildJavaToolVersionStartInfo(executable)
        };
        process.Start();

        var stdoutTask = CaptureJavaVersionOutputAsync(process.StandardOutput);
        var stderrTask = CaptureJavaVersionOutputAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(JavaVersionTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            TryTerminateProcess(process);
            await TryDrainJavaVersionOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            if (exception is OperationCanceledException) throw;
            throw new InvalidDataException(
                $"{toolName} -version 超過 15 秒仍未結束，已終止驗證程序。",
                exception);
        }

        BoundedCapturedStream[] captured;
        try
        {
            captured = await Task.WhenAll(stderrTask, stdoutTask)
                .WaitAsync(JavaVersionDrainTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidDataException(
                $"{toolName} -version 已結束，但輸出串流未在安全時間內關閉。",
                exception);
        }

        if (captured.Any(static stream => stream.Truncated))
        {
            throw new InvalidDataException($"{toolName} -version 輸出超過安全大小上限。");
        }

        var output = string.Join(
            Environment.NewLine,
            captured.SelectMany(static stream => stream.Lines));
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"{toolName} -version 結束碼為 {process.ExitCode}："
                + CreateBoundedDiagnostic(output));
        }

        var match = versionRegex.Match(output);
        if (!match.Success)
        {
            throw new InvalidDataException(
                $"無法解析 {toolName} -version 輸出：{CreateBoundedDiagnostic(output)}");
        }

        return int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static ProcessStartInfo BuildJavaToolVersionStartInfo(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var fullExecutable = Path.GetFullPath(executable);
        var workingDirectory = Path.GetDirectoryName(fullExecutable)
            ?? throw new InvalidDataException("Java tool executable 缺少受控工作目錄。");
        var startInfo = new ProcessStartInfo
        {
            FileName = fullExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ManagedJavaProcessEnvironment.Configure(startInfo, fullExecutable);
        startInfo.ArgumentList.Add("-version");
        return startInfo;
    }

    private static Task<BoundedCapturedStream> CaptureJavaVersionOutputAsync(TextReader reader)
        => BoundedProcessOutputCapture.CaptureAsync(
            reader,
            maximumLines: MaximumJavaVersionOutputLines,
            maximumCharacters: MaximumJavaVersionOutputCharacters,
            maximumLineCharacters: BoundedProcessOutputCapture.DefaultMaximumLineCharacters);

    private static async Task TryDrainJavaVersionOutputAsync(
        Task<BoundedCapturedStream> stdoutTask,
        Task<BoundedCapturedStream> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(JavaVersionDrainTimeout)
                .ConfigureAwait(false);
        }
        catch
        {
            // The timeout/cancellation remains the primary result. Process disposal closes any
            // remaining redirected handles after this bounded drain attempt.
        }
    }

    private static string CreateBoundedDiagnostic(string value)
    {
        const int maximumCharacters = 8 * 1024;
        var tail = value.Length <= maximumCharacters ? value : value[^maximumCharacters..];
        var cleaned = new string(tail
            .Where(static character => !char.IsControl(character) || character is '\r' or '\n' or '\t')
            .ToArray())
            .Trim();
        return cleaned.Length == 0 ? "(no output)" : cleaned;
    }

    private static void TryTerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and termination.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Preserve the timeout/cancellation exception when Windows denies termination.
        }
    }

    private async Task<JavaRuntimePackage?> QueryPackageAsync(
        int majorVersion,
        string imageType,
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri(
            BaseUri,
            $"v3/assets/latest/{majorVersion}/hotspot?architecture=x64&image_type={imageType}&os=windows&vendor=eclipse");
        EnsureOfficialApiUri(requestUri, "Adoptium API request");
        using var response = await _httpClient.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri
            ?? throw new InvalidDataException("Adoptium API response is missing its final URI.");
        EnsureOfficialApiUri(finalUri, "Adoptium API response");
        if (!UrisEqual(requestUri, finalUri))
        {
            throw new InvalidDataException(
                $"Adoptium API redirected unexpectedly; the response was rejected: {finalUri}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Adoptium API 錯誤：HTTP {(int)response.StatusCode} {response.ReasonPhrase}。",
                null,
                response.StatusCode);
        }

        var bytes = await ReadBoundedBytesAsync(
                response.Content,
                MaximumApiResponseBytes,
                cancellationToken)
            .ConfigureAwait(false);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Adoptium API 回傳了無效 JSON。", exception);
        }

        using (document)
        {
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        foreach (var asset in document.RootElement.EnumerateArray())
        {
            if (!asset.TryGetProperty("binary", out var binary)
                || !binary.TryGetProperty("package", out var package))
            {
                continue;
            }

            var actualImageType = ReadRequiredString(binary, "image_type", "Adoptium binary");
            if (!actualImageType.Equals(imageType, StringComparison.Ordinal))
            {
                continue;
            }

            var linkText = ReadRequiredString(package, "link", "Adoptium package");
            if (!Uri.TryCreate(linkText, UriKind.Absolute, out var link)
                || !link.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !link.IsDefaultPort
                || !string.IsNullOrEmpty(link.UserInfo))
            {
                throw new InvalidDataException("Adoptium package link 不是安全的 HTTPS URL。");
            }

            var checksum = ReadRequiredString(package, "checksum", "Adoptium package");
            if (checksum.Length != 64 || !checksum.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("Adoptium package SHA-256 格式無效。");
            }

            var size = ReadRequiredInt64(package, "size", "Adoptium package");
            if (size is < 1 or > MaximumRuntimeArchiveBytes)
            {
                throw new InvalidDataException("Adoptium package 大小超過安全上限。");
            }

            var releaseName = ReadRequiredString(asset, "release_name", "Adoptium asset");
            if (releaseName.Length > 128 || releaseName.Any(char.IsControl))
            {
                throw new InvalidDataException("Adoptium release_name 無效。");
            }

            var fileName = ReadRequiredString(package, "name", "Adoptium package");
            if (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Adoptium Windows Runtime 必須是 ZIP 封裝。");
            }

            return new JavaRuntimePackage(
                majorVersion,
                releaseName,
                actualImageType,
                asset.TryGetProperty("vendor", out var vendor) ? vendor.GetString() ?? "eclipse" : "eclipse",
                link,
                fileName,
                checksum,
                size);
        }

        return null;
        }
    }

    private static void EnsureOfficialApiUri(Uri uri, string context)
    {
        if (!uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IdnHost.Equals(BaseUri.IdnHost, StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/v3/assets/latest/", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{context} is not the official Adoptium HTTPS API: {uri}");
        }
    }

    private static bool UrisEqual(Uri expected, Uri actual)
        => expected.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)
            .Equals(
                actual.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped),
                StringComparison.Ordinal);

    private static async Task<byte[]> ReadBoundedBytesAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } declared && declared > maximumBytes)
        {
            throw new InvalidDataException("Adoptium API 回應超過安全大小上限。");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException("Adoptium API 回應超過安全大小上限。");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ReadRequiredString(JsonElement element, string property, string context)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{context} 缺少有效的 {property}。");
        }

        return value.GetString()!;
    }

    private static long ReadRequiredInt64(JsonElement element, string property, string context)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value)
            || !value.TryGetInt64(out var result))
        {
            throw new InvalidDataException($"{context} 缺少有效的 {property}。");
        }

        return result;
    }

    internal static async Task ExtractZipSafelyAsync(
        string archivePath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumArchiveEntries)
        {
            throw new InvalidDataException("Java ZIP 的項目數異常，已停止解壓縮。");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalDeclaredBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectArchiveLinkOrSpecialEntry(entry);
            var relativePath = ValidateArchiveRelativePath(entry.FullName);
            if (!paths.Add(relativePath))
            {
                throw new InvalidDataException($"Java ZIP 包含大小寫或 Unicode 重複路徑：{entry.FullName}");
            }

            var destination = Path.GetFullPath(Path.Combine(
                normalizedRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"ZIP 包含不安全路徑：{entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                SafePath.EnsureNoReparsePointsUnderRoot(destinationRoot, destination);
                continue;
            }

            if (entry.Length is < 0 or > MaximumExtractedEntryBytes)
            {
                throw new InvalidDataException($"Java ZIP 項目超過安全大小：{entry.FullName}");
            }

            totalDeclaredBytes = checked(totalDeclaredBytes + entry.Length);
            if (totalDeclaredBytes > MaximumExtractedTotalBytes)
            {
                throw new InvalidDataException("Java ZIP 解壓縮總大小超過安全上限。");
            }

            if (entry.Length > 0
                && (entry.CompressedLength <= 0
                    || (double)entry.Length / entry.CompressedLength > MaximumCompressionRatio))
            {
                throw new InvalidDataException($"Java ZIP 項目的壓縮比超過安全上限：{entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            SafePath.EnsureNoReparsePointsUnderRoot(destinationRoot, Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            long actualBytes = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                actualBytes = checked(actualBytes + read);
                if (actualBytes > entry.Length)
                {
                    throw new InvalidDataException($"Java ZIP 項目超過宣告大小：{entry.FullName}");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (actualBytes != entry.Length)
            {
                throw new InvalidDataException($"Java ZIP 項目大小與宣告不符：{entry.FullName}");
            }
        }
    }

    private static string ValidateArchiveRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > 4096
            || path.Contains('\\')
            || path.StartsWith('/')
            || path.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
        {
            throw new InvalidDataException($"Java ZIP 包含不安全路徑：{path}");
        }

        var candidate = path.EndsWith("/", StringComparison.Ordinal) ? path[..^1] : path;
        if (candidate.Length == 0)
        {
            throw new InvalidDataException("Java ZIP 包含空白根目錄項目。");
        }

        var segments = candidate.Split('/');
        foreach (var segment in segments)
        {
            string normalized;
            try
            {
                normalized = segment.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException($"Java ZIP 包含無效 Unicode 路徑：{path}", exception);
            }

            if (segment.Length == 0
                || segment is "." or ".."
                || segment.EndsWith(' ')
                || segment.EndsWith('.')
                || segment.Contains(':')
                || segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0
                || normalized.Length > 255
                || ReservedWindowsNames.Contains(normalized.Split('.')[0]))
            {
                throw new InvalidDataException($"Java ZIP 包含 Windows 不支援的路徑：{path}");
            }
        }

        return string.Join('/', segments.Select(segment => segment.Normalize(NormalizationForm.FormC)));
    }

    private static void RejectArchiveLinkOrSpecialEntry(ZipArchiveEntry entry)
    {
        var attributes = entry.ExternalAttributes;
        var dosAttributes = attributes & 0xFFFF;
        var upperAttributes = (attributes >> 16) & 0xFFFF;
        var unixType = upperAttributes & UnixFileTypeMask;

        if ((dosAttributes & DosReparsePointAttribute) != 0
            // ZIP stores Unix mode bits and Windows/DOS attributes in overlapping fields.
            // 0x0400 in a Unix mode is the set-group-ID bit, not a reparse marker. Only
            // interpret the upper word as raw DOS attributes when it does not contain a Unix
            // file type.
            || (unixType == 0 && (upperAttributes & DosReparsePointAttribute) != 0)
            || unixType == UnixSymbolicLinkType
            || (unixType != 0 && unixType != UnixRegularFileType && unixType != UnixDirectoryType))
        {
            throw new InvalidDataException(
                $"Java ZIP 不可包含 symbolic link、reparse point 或特殊檔案：{entry.FullName}");
        }
    }

    internal static async Task MoveDirectoryWithRetryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken,
        Action<string, string>? moveDirectory = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        moveDirectory ??= Directory.Move;
        delayAsync ??= static (delay, token) => Task.Delay(delay, token);

        const int maximumAttempts = 8;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafePath.EnsureTreeContainsNoReparsePoints(source);
            try
            {
                moveDirectory(source, destination);
                return;
            }
            catch (Exception exception) when (
                attempt < maximumAttempts
                && IsTransientDirectoryMoveFailure(exception)
                && Directory.Exists(source)
                && !Directory.Exists(destination)
                && !File.Exists(destination))
            {
                // Windows may keep a just-executed java.exe/javac.exe image or antivirus scan
                // handle briefly after the process exits. Keep the verified staging tree intact
                // and retry the same-volume atomic rename for a bounded period.
                await delayAsync(TimeSpan.FromMilliseconds(attempt * 250d), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientDirectoryMoveFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException
            && exception.HResult is unchecked((int)0x80070005) // ERROR_ACCESS_DENIED
                or unchecked((int)0x80070020) // ERROR_SHARING_VIOLATION
                or unchecked((int)0x80070021); // ERROR_LOCK_VIOLATION

    private static void EnsureWithinRoot(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("解壓縮結果逃出暫存資料夾。");
        }
    }

    private static void EnsureRegularExecutable(string path, string context)
    {
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"{context} 不存在：{path}");
        }

        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"{context} 必須是非連結的一般檔案：{path}");
        }
    }

    private static string SanitizeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "java" : cleaned;
    }

    private static Task DeleteOwnedPathAsync(string trustedParent, string path)
        => SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
            trustedParent,
            path,
            CancellationToken.None);

    [GeneratedRegex("version\\s+\"(?:(1)\\.)?(\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JavaVersionRegex();

    [GeneratedRegex("^javac\\s+(?:(1)\\.)?(\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex JavacVersionRegex();
}

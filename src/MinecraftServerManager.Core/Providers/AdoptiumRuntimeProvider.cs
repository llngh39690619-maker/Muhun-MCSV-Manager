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
    private const string GithubHost = "github.com";
    private const string GithubReleaseAssetsHost = "release-assets.githubusercontent.com";
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
    private const int RuntimeOwnershipReceiptSchemaVersion = 1;
    private const int MaximumRuntimeOwnershipReceiptBytes = 16 * 1024;
    private const string RuntimeOwnershipReceiptFileName = ".muhun-mcsv-runtime.v1.json";
    private const int MaximumLegacyReleaseFileBytes = 64 * 1024;
    private const string LegacyRuntimeQuarantineDirectoryName = ".legacy-runtime-quarantine";
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
    private readonly Func<string, CancellationToken, Task<int>> _readJavaMajorVersionAsync;
    private readonly Func<string, CancellationToken, Task<int>> _readJavacMajorVersionAsync;

    public AdoptiumRuntimeProvider(HttpClient httpClient, string userAgent)
        : this(
            httpClient,
            userAgent,
            ReadJavaMajorVersionAsync,
            ReadJavacMajorVersionAsync)
    {
    }

    internal AdoptiumRuntimeProvider(
        HttpClient httpClient,
        string userAgent,
        Func<string, CancellationToken, Task<int>> readJavaMajorVersionAsync,
        Func<string, CancellationToken, Task<int>> readJavacMajorVersionAsync)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        _readJavaMajorVersionAsync = readJavaMajorVersionAsync
            ?? throw new ArgumentNullException(nameof(readJavaMajorVersionAsync));
        _readJavacMajorVersionAsync = readJavacMajorVersionAsync
            ?? throw new ArgumentNullException(nameof(readJavacMajorVersionAsync));
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
        string? preservedLegacyRuntime = null;
        if (Directory.Exists(destination))
        {
            SafePath.EnsureTreeContainsNoReparsePoints(destination);
            var existingIdentity = SafePath.GetExistingObjectIdentity(destination);
            var hasOwnershipReceipt = HasValidRuntimeOwnershipReceipt(
                destination,
                package,
                Path.GetFileName(destination));
            try
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

                // The locale settings probe inside the managed Java reader exercises the CLDR
                // module in addition to parsing `java -version`. A power loss can leave the
                // executable readable while lib/modules contains zeroed class data; such a tree
                // must never be returned merely because the version banner still works.
                var existingMajor = await _readJavaMajorVersionAsync(existingJava, cancellationToken)
                    .ConfigureAwait(false);
                if (existingMajor != majorVersion)
                {
                    throw new InvalidDataException(
                        $"既有 Java Runtime 版本不符，預期 {majorVersion}，實際 {existingMajor}：{destination}");
                }

                if (requireJdk)
                {
                    var existingJavacMajor = await _readJavacMajorVersionAsync(
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

                return new InstalledJavaRuntime(
                    majorVersion,
                    package.ReleaseName,
                    package.ImageType,
                    package.Vendor,
                    destination,
                    existingJava);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsRepairableExistingRuntimeFailure(exception))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!hasOwnershipReceipt)
                {
                    if (!IsEligibleLegacyAdoptiumRuntime(
                            destination,
                            package,
                            requireJdk))
                    {
                        throw new InvalidDataException(
                            "既有 Java Runtime 已損壞，但缺少 X MCSV 所有權憑證，"
                            + "且無法確認為舊版受管 Temurin；為避免移動手動放入的 Java，"
                            + "已停止自動修復。",
                            exception);
                    }

                    preservedLegacyRuntime = await QuarantineLegacyRuntimeAsync(
                            fullRuntimeRoot,
                            destination,
                            existingIdentity,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await DeleteInvalidRuntimeAsync(
                            fullRuntimeRoot,
                            destination,
                            existingIdentity,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
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
                cancellationToken,
                (source, destination) => IsAllowedPackageRedirect(
                    package.DownloadUri,
                    source,
                    destination)).ConfigureAwait(false);

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

            var actualMajor = await _readJavaMajorVersionAsync(discoveredJava, cancellationToken)
                .ConfigureAwait(false);
            if (actualMajor != majorVersion)
            {
                throw new InvalidDataException($"Java 版本驗證失敗，預期 {majorVersion}，實際 {actualMajor}。");
            }

            if (requireJdk)
            {
                var actualJavacMajor = await _readJavacMajorVersionAsync(
                        discoveredJavac,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (actualJavacMajor != majorVersion)
                {
                    throw new InvalidDataException(
                        $"javac 版本驗證失敗，預期 {majorVersion}，實際 {actualJavacMajor}。");
                }
            }

            await WriteRuntimeOwnershipReceiptAsync(
                    packageRoot,
                    package,
                    Path.GetFileName(destination),
                    cancellationToken)
                .ConfigureAwait(false);

            if (Directory.Exists(destination))
            {
                throw new IOException($"Runtime 目的資料夾已存在但不完整：{destination}");
            }

            var promotedIdentity = SafePath.GetExistingObjectIdentity(packageRoot);
            try
            {
                await MoveDirectoryWithRetryAsync(
                        packageRoot,
                        destination,
                        cancellationToken,
                        expectedSourceIdentity: promotedIdentity)
                    .ConfigureAwait(false);
                var destinationIdentity = SafePath.GetExistingObjectIdentity(destination);
                if (destinationIdentity != promotedIdentity)
                {
                    throw new UnauthorizedAccessException(
                        "Java Runtime promotion 期間資料夾 identity 已變更。");
                }

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

                if (!HasValidRuntimeOwnershipReceipt(
                        destination,
                        package,
                        Path.GetFileName(destination)))
                {
                    throw new InvalidDataException("Java 安裝完成後的 X MCSV 所有權憑證無效。");
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
                if (Directory.Exists(destination))
                {
                    await DeleteInvalidRuntimeAsync(
                            fullRuntimeRoot,
                            destination,
                            promotedIdentity,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }

                throw;
            }
        }
        catch (Exception exception) when (
            preservedLegacyRuntime is not null
            && exception is not OperationCanceledException)
        {
            throw new InvalidDataException(
                $"新 Java Runtime 安裝失敗；舊版 Runtime 已完整保留於：{preservedLegacyRuntime}",
                exception);
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
            StartInfo = BuildJavaToolVersionStartInfo(
                executable,
                probeLocale: string.Equals(toolName, "java", StringComparison.Ordinal))
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

    internal static ProcessStartInfo BuildJavaToolVersionStartInfo(
        string executable,
        bool probeLocale = true)
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
        if (probeLocale)
        {
            // `java -version` can succeed even when a power interruption has corrupted the CLDR
            // data inside lib/modules. Asking the VM to materialize its locale settings catches
            // that unusable runtime before Minecraft or an official loader installer receives it.
            startInfo.ArgumentList.Add("-XshowSettings:locale");
        }
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

            EnsureOfficialPackageDownloadUri(link, majorVersion, fileName);

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

    private static void EnsureOfficialPackageDownloadUri(
        Uri uri,
        int majorVersion,
        string expectedFileName)
    {
        if (!IsSafeHttpsUri(uri)
            || !uri.IdnHost.Equals(GithubHost, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException(
                "Adoptium package link is not an official GitHub release URL.");
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string decodedFileName;
        try
        {
            decodedFileName = segments.Length == 0
                ? string.Empty
                : Uri.UnescapeDataString(segments[^1]);
        }
        catch (UriFormatException exception)
        {
            throw new InvalidDataException("Adoptium package link contains invalid escaping.", exception);
        }

        if (segments.Length != 6
            || !segments[0].Equals("adoptium", StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals(
                $"temurin{majorVersion}-binaries",
                StringComparison.OrdinalIgnoreCase)
            || !segments[2].Equals("releases", StringComparison.OrdinalIgnoreCase)
            || !segments[3].Equals("download", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(segments[4])
            || !decodedFileName.Equals(expectedFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Adoptium package link is not bound to the expected official GitHub release asset.");
        }
    }

    internal static bool IsAllowedPackageRedirect(
        Uri originalPackageUri,
        Uri source,
        Uri destination)
    {
        ArgumentNullException.ThrowIfNull(originalPackageUri);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!IsSafeHttpsUri(source)
            || !IsSafeHttpsUri(destination)
            || !string.IsNullOrEmpty(destination.Fragment))
        {
            return false;
        }

        if (UrisEqual(originalPackageUri, source))
        {
            return source.IdnHost.Equals(GithubHost, StringComparison.OrdinalIgnoreCase)
                   && destination.IdnHost.Equals(
                       GithubReleaseAssetsHost,
                       StringComparison.OrdinalIgnoreCase)
                   && IsGithubReleaseAssetPath(destination);
        }

        return source.IdnHost.Equals(
                   GithubReleaseAssetsHost,
                   StringComparison.OrdinalIgnoreCase)
               && destination.IdnHost.Equals(
                   GithubReleaseAssetsHost,
                   StringComparison.OrdinalIgnoreCase)
               && IsGithubReleaseAssetPath(source)
               && IsGithubReleaseAssetPath(destination);
    }

    private static bool IsSafeHttpsUri(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo);

    private static bool IsGithubReleaseAssetPath(Uri uri) =>
        uri.AbsolutePath.StartsWith(
            "/github-production-release-asset/",
            StringComparison.Ordinal)
        && uri.AbsolutePath.Length > "/github-production-release-asset/".Length;

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
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        SafePathObjectIdentity? expectedSourceIdentity = null)
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
            if (expectedSourceIdentity is { } expected
                && SafePath.GetExistingObjectIdentity(source) != expected)
            {
                throw new UnauthorizedAccessException(
                    "Refused Java Runtime promotion because the verified source identity changed.");
            }

            try
            {
                moveDirectory(source, destination);
                if (expectedSourceIdentity is { } expectedDestinationIdentity
                    && SafePath.GetExistingObjectIdentity(destination) != expectedDestinationIdentity)
                {
                    throw new UnauthorizedAccessException(
                        "Refused Java Runtime promotion because the destination identity changed.");
                }

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

    private static bool IsRepairableExistingRuntimeFailure(Exception exception)
        => exception is InvalidDataException or FileNotFoundException or DirectoryNotFoundException;

    private static bool IsEligibleLegacyAdoptiumRuntime(
        string destination,
        JavaRuntimePackage package,
        bool requireJdk)
    {
        var java = Path.Combine(destination, "bin", "java.exe");
        var javac = Path.Combine(destination, "bin", "javac.exe");
        var releasePath = Path.Combine(destination, "release");
        if (!File.Exists(java)
            || (requireJdk && !File.Exists(javac))
            || !File.Exists(releasePath))
        {
            return false;
        }

        try
        {
            EnsureRegularExecutable(java, "舊版 Java");
            if (requireJdk)
            {
                EnsureRegularExecutable(javac, "舊版 javac");
            }

            SafePath.EnsureNoReparsePointsUnderRoot(destination, releasePath);
            var attributes = File.GetAttributes(releasePath);
            var length = new FileInfo(releasePath).Length;
            if (attributes.HasFlag(FileAttributes.Directory)
                || attributes.HasFlag(FileAttributes.ReparsePoint)
                || length is < 1 or > MaximumLegacyReleaseFileBytes)
            {
                return false;
            }

            var content = File.ReadAllText(releasePath, Encoding.UTF8);
            if (Encoding.UTF8.GetByteCount(content) > MaximumLegacyReleaseFileBytes)
            {
                return false;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rawLine in content.ReplaceLineEndings("\n").Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0 || !values.TryAdd(
                        line[..separator],
                        UnquoteReleaseValue(line[(separator + 1)..])))
                {
                    return false;
                }
            }

            var releaseVersion = package.ReleaseName.StartsWith("jdk-", StringComparison.Ordinal)
                ? package.ReleaseName[4..]
                : package.ReleaseName;
            return ReadReleaseValue(values, "IMPLEMENTOR") is "Eclipse Adoptium"
                   && string.Equals(
                       ReadReleaseValue(values, "IMPLEMENTOR_VERSION"),
                       $"Temurin-{releaseVersion}",
                       StringComparison.Ordinal)
                   && HasMatchingJavaMajor(
                       ReadReleaseValue(values, "JAVA_VERSION"),
                       package.MajorVersion)
                   && string.Equals(
                       ReadReleaseValue(values, "IMAGE_TYPE"),
                       package.ImageType,
                       StringComparison.OrdinalIgnoreCase)
                   && string.Equals(
                       ReadReleaseValue(values, "OS_NAME"),
                       "Windows",
                       StringComparison.Ordinal)
                   && ReadReleaseValue(values, "OS_ARCH") is { } architecture
                   && (architecture.Equals("x86_64", StringComparison.OrdinalIgnoreCase)
                       || architecture.Equals("amd64", StringComparison.OrdinalIgnoreCase))
                   && string.Equals(
                       ReadReleaseValue(values, "JVM_VARIANT"),
                       "Hotspot",
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException
                                           or DecoderFallbackException)
        {
            return false;
        }

        static string UnquoteReleaseValue(string value)
        {
            var trimmed = value.Trim();
            return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
                ? trimmed[1..^1]
                : trimmed;
        }

        static string? ReadReleaseValue(IReadOnlyDictionary<string, string> values, string key)
            => values.TryGetValue(key, out var value) ? value : null;

        static bool HasMatchingJavaMajor(string? version, int expectedMajor)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return false;
            }

            var separator = version.IndexOfAny(['.', '-', '+']);
            var majorText = separator < 0 ? version : version[..separator];
            return int.TryParse(
                       majorText,
                       System.Globalization.NumberStyles.None,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var actualMajor)
                   && actualMajor == expectedMajor;
        }
    }

    private static async Task<string> QuarantineLegacyRuntimeAsync(
        string runtimeRoot,
        string destination,
        SafePathObjectIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRoot));
        var fullDestination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (!string.Equals(
                Path.GetDirectoryName(fullDestination),
                fullRoot,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullDestination).StartsWith(
                "temurin-",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "Only a direct, manager-named legacy Temurin runtime may be quarantined.");
        }

        var quarantineRoot = Path.Combine(fullRoot, LegacyRuntimeQuarantineDirectoryName);
        Directory.CreateDirectory(quarantineRoot);
        SafePath.EnsureNoReparsePointsUnderRoot(fullRoot, quarantineRoot);
        var quarantinePath = Path.Combine(
            quarantineRoot,
            $"{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}");
        if (Directory.Exists(quarantinePath) || File.Exists(quarantinePath))
        {
            throw new IOException("Legacy Java Runtime quarantine path already exists.");
        }

        await MoveDirectoryWithRetryAsync(
                fullDestination,
                quarantinePath,
                cancellationToken,
                expectedSourceIdentity: expectedIdentity)
            .ConfigureAwait(false);
        if (SafePath.GetExistingObjectIdentity(quarantinePath) != expectedIdentity)
        {
            throw new UnauthorizedAccessException(
                "Legacy Java Runtime quarantine identity verification failed.");
        }

        return quarantinePath;
    }

    private static async Task WriteRuntimeOwnershipReceiptAsync(
        string packageRoot,
        JavaRuntimePackage package,
        string destinationLeaf,
        CancellationToken cancellationToken)
    {
        var receiptPath = Path.Combine(packageRoot, RuntimeOwnershipReceiptFileName);
        if (File.Exists(receiptPath) || Directory.Exists(receiptPath))
        {
            throw new InvalidDataException(
                $"Java 封裝不可預先包含 X MCSV 所有權憑證：{receiptPath}");
        }

        var receipt = new RuntimeOwnershipReceipt(
            RuntimeOwnershipReceiptSchemaVersion,
            "adoptium",
            package.MajorVersion,
            package.ReleaseName,
            package.ImageType,
            package.FileName,
            package.Sha256.ToLowerInvariant(),
            package.Size,
            destinationLeaf);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt);
        if (bytes.Length is < 1 or > MaximumRuntimeOwnershipReceiptBytes)
        {
            throw new InvalidDataException("Java Runtime 所有權憑證大小無效。");
        }

        await using var stream = new FileStream(
            receiptPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static bool HasValidRuntimeOwnershipReceipt(
        string runtimeDirectory,
        JavaRuntimePackage package,
        string destinationLeaf)
    {
        var receiptPath = Path.Combine(runtimeDirectory, RuntimeOwnershipReceiptFileName);
        if (!File.Exists(receiptPath))
        {
            if (Directory.Exists(receiptPath))
            {
                throw new InvalidDataException("Java Runtime 所有權憑證不可是資料夾。");
            }

            return false;
        }

        SafePath.EnsureNoReparsePointsUnderRoot(runtimeDirectory, receiptPath);
        var attributes = File.GetAttributes(receiptPath);
        if (attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Java Runtime 所有權憑證必須是非連結的一般檔案。");
        }

        var length = new FileInfo(receiptPath).Length;
        if (length is < 1 or > MaximumRuntimeOwnershipReceiptBytes)
        {
            throw new InvalidDataException("Java Runtime 所有權憑證大小無效。");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(receiptPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("無法安全讀取 Java Runtime 所有權憑證。", exception);
        }

        if (bytes.Length is < 1 or > MaximumRuntimeOwnershipReceiptBytes)
        {
            throw new InvalidDataException("Java Runtime 所有權憑證大小無效。");
        }

        try
        {
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Java Runtime 所有權憑證根節點無效。");
            }

            string[] expectedNames =
            [
                nameof(RuntimeOwnershipReceipt.SchemaVersion),
                nameof(RuntimeOwnershipReceipt.Provider),
                nameof(RuntimeOwnershipReceipt.MajorVersion),
                nameof(RuntimeOwnershipReceipt.ReleaseName),
                nameof(RuntimeOwnershipReceipt.ImageType),
                nameof(RuntimeOwnershipReceipt.FileName),
                nameof(RuntimeOwnershipReceipt.ArchiveSha256),
                nameof(RuntimeOwnershipReceipt.ArchiveSize),
                nameof(RuntimeOwnershipReceipt.DestinationLeaf)
            ];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!expectedNames.Contains(property.Name, StringComparer.Ordinal)
                    || !seen.Add(property.Name))
                {
                    throw new InvalidDataException("Java Runtime 所有權憑證包含未知或重複欄位。");
                }
            }

            if (seen.Count != expectedNames.Length
                || !TryReadInt32(root, nameof(RuntimeOwnershipReceipt.SchemaVersion), out var schemaVersion)
                || schemaVersion != RuntimeOwnershipReceiptSchemaVersion
                || !TryReadString(root, nameof(RuntimeOwnershipReceipt.Provider), out var provider)
                || !string.Equals(provider, "adoptium", StringComparison.Ordinal)
                || !TryReadInt32(root, nameof(RuntimeOwnershipReceipt.MajorVersion), out var majorVersion)
                || majorVersion != package.MajorVersion
                || !TryReadString(root, nameof(RuntimeOwnershipReceipt.ReleaseName), out var releaseName)
                || !string.Equals(releaseName, package.ReleaseName, StringComparison.Ordinal)
                || !TryReadString(root, nameof(RuntimeOwnershipReceipt.ImageType), out var imageType)
                || !string.Equals(imageType, package.ImageType, StringComparison.Ordinal)
                || !TryReadString(root, nameof(RuntimeOwnershipReceipt.FileName), out var fileName)
                || !string.Equals(fileName, package.FileName, StringComparison.Ordinal)
                || !TryReadString(root, nameof(RuntimeOwnershipReceipt.ArchiveSha256), out var archiveSha256)
                || !string.Equals(archiveSha256, package.Sha256, StringComparison.OrdinalIgnoreCase)
                || !TryReadInt64(root, nameof(RuntimeOwnershipReceipt.ArchiveSize), out var archiveSize)
                || archiveSize != package.Size
                || !TryReadString(root, nameof(RuntimeOwnershipReceipt.DestinationLeaf), out var recordedLeaf)
                || !string.Equals(recordedLeaf, destinationLeaf, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Java Runtime 所有權憑證與官方套件不相符。");
            }

            return true;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Java Runtime 所有權憑證不是有效 JSON。", exception);
        }

        static bool TryReadString(JsonElement root, string name, out string? value)
        {
            value = null;
            return root.TryGetProperty(name, out var element)
                   && element.ValueKind == JsonValueKind.String
                   && (value = element.GetString()) is not null;
        }

        static bool TryReadInt32(JsonElement root, string name, out int value)
        {
            value = default;
            return root.TryGetProperty(name, out var element)
                   && element.ValueKind == JsonValueKind.Number
                   && element.TryGetInt32(out value);
        }

        static bool TryReadInt64(JsonElement root, string name, out long value)
        {
            value = default;
            return root.TryGetProperty(name, out var element)
                   && element.ValueKind == JsonValueKind.Number
                   && element.TryGetInt64(out value);
        }
    }

    private sealed record RuntimeOwnershipReceipt(
        int SchemaVersion,
        string Provider,
        int MajorVersion,
        string ReleaseName,
        string ImageType,
        string FileName,
        string ArchiveSha256,
        long ArchiveSize,
        string DestinationLeaf);

    internal static Task DeleteInvalidRuntimeAsync(
        string runtimeRoot,
        string destination,
        SafePathObjectIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRoot));
        var fullDestination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (!string.Equals(
                Path.GetDirectoryName(fullDestination),
                fullRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullDestination).StartsWith("temurin-", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "Only a direct, manager-named Temurin runtime may be repaired automatically.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
            fullRoot,
            fullDestination,
            expectedIdentity,
            protectedObjectIdentities: null,
            cancellationToken);
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

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.GameClient.Contracts;

namespace MinecraftServerManager.GameClient;

internal interface IOfficialMavenClientLoaderHttpTransport
{
    Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken);
}

internal sealed class HttpClientOfficialMavenClientLoaderHttpTransport
    : IOfficialMavenClientLoaderHttpTransport
{
    private static readonly HttpClient SharedClient = CreateSecureClient();

    public async Task<HttpResponseMessage> GetAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var response = await SharedClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        var effectiveUri = response.RequestMessage?.RequestUri;
        if (effectiveUri is null || !UrisEqual(uri, effectiveUri))
        {
            response.Dispose();
            throw new InvalidDataException(
                "The official Maven request was redirected or lost its exact request identity.");
        }

        return response;
    }

    private static HttpClient CreateSecureClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxResponseHeadersLength = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("X-MCSV/1.1");
        return client;
    }

    private static bool UrisEqual(Uri expected, Uri actual) =>
        expected.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)
            .Equals(
                actual.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped),
                StringComparison.Ordinal);
}

internal sealed record VerifiedMavenClientLoaderArtifact(
    string Path,
    Uri SourceUri,
    byte[] ExpectedSha256,
    long Length);

internal interface IVerifiedMavenClientLoaderProcessRunner
{
    Task RunAsync(
        VerifiedMavenClientLoaderArtifact artifact,
        string javaExecutablePath,
        string instanceDirectory,
        CancellationToken cancellationToken);
}

internal sealed class VerifiedMavenClientLoaderProcessRunner
    : IVerifiedMavenClientLoaderProcessRunner
{
    internal const int MaximumDiagnosticCharacters = 64 * 1024;
    internal static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(20);

    public async Task RunAsync(
        VerifiedMavenClientLoaderArtifact artifact,
        string javaExecutablePath,
        string instanceDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ValidateJava(javaExecutablePath);
        var fullInstanceDirectory = ValidateDirectory(instanceDirectory, "instance directory");
        var fullInstallerPath = ValidateRegularFile(artifact.Path, "verified loader installer");
        if (artifact.ExpectedSha256.Length != SHA256.HashSizeInBytes || artifact.Length <= 0)
        {
            throw new InvalidDataException("The verified loader installer identity is invalid.");
        }

        // Keep a non-writable, non-delete-sharing handle open from the final hash check until the
        // Java child exits. This closes the verify/use replacement window for the executable JAR.
        await using var installerLock = new FileStream(
            fullInstallerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (installerLock.Length != artifact.Length)
        {
            throw new InvalidDataException("The verified loader installer changed before execution.");
        }

        var actualSha256 = await SHA256.HashDataAsync(installerLock, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(artifact.ExpectedSha256, actualSha256))
            {
                throw new InvalidDataException("The verified loader installer changed before execution.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualSha256);
        }

        using var process = new Process
        {
            StartInfo = CreateStartInfo(
                javaExecutablePath,
                fullInstallerPath,
                fullInstanceDirectory),
            EnableRaisingEvents = true,
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("The verified loader installer process did not start.");
        }

        var standardOutput = DrainBoundedAsync(
            process.StandardOutput,
            MaximumDiagnosticCharacters,
            CancellationToken.None);
        var standardError = DrainBoundedAsync(
            process.StandardError,
            MaximumDiagnosticCharacters,
            CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProcessTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!process.HasExited)
        {
            TryKill(process);
            await WaitForDrainAsync(standardOutput, standardError).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"The verified loader installer exceeded the {ProcessTimeout.TotalMinutes:0}-minute limit.");
        }

        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var diagnostic = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(
                $"The verified loader installer exited with code {process.ExitCode}. {diagnostic}".Trim());
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string javaExecutablePath,
        string installerPath,
        string instanceDirectory)
    {
        var startInfo = new ProcessStartInfo(javaExecutablePath)
        {
            WorkingDirectory = instanceDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(installerPath);
        startInfo.ArgumentList.Add("--installClient");
        startInfo.ArgumentList.Add(instanceDirectory);
        return startInfo;
    }

    private static async Task<string> DrainBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var captured = new StringBuilder(Math.Min(4_096, maximumCharacters));
        var buffer = new char[4_096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return captured.ToString();
            }

            var remaining = maximumCharacters - captured.Length;
            if (remaining > 0)
            {
                captured.Append(buffer, 0, Math.Min(remaining, read));
            }
        }
    }

    private static async Task WaitForDrainAsync(Task<string> output, Task<string> error)
    {
        try
        {
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            ExceptionGraphSafety.RethrowOutOfMemory(exception);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ExceptionGraphSafety.RethrowOutOfMemory(exception);
        }
    }

    private static void ValidateJava(string path)
    {
        var fullPath = ValidateRegularFile(path, "Java executable");
        var name = System.IO.Path.GetFileName(fullPath);
        if (!name.Equals("java.exe", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Loader installation requires java.exe or javaw.exe.");
        }
    }

    private static string ValidateDirectory(string path, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!Directory.Exists(fullPath) ||
            File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"The {label} is missing or unsafe.");
        }

        return fullPath;
    }

    private static string ValidateRegularFile(string path, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The {label} does not exist.", fullPath);
        }

        var attributes = File.GetAttributes(fullPath);
        if (attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"The {label} is not a regular file.");
        }

        return fullPath;
    }
}

/// <summary>
/// Downloads only the exact official Maven installer selected by the bounded catalog, binds it to
/// the repository's SHA-256 sidecar, and executes it without shell activation.
/// </summary>
internal sealed class OfficialMavenClientLoaderInstaller
{
    internal const long MaximumInstallerBytes = 512L * 1024 * 1024;
    internal const int MaximumSidecarBytes = 256;
    internal const int MaximumVersionProfiles = 256;
    internal const long MaximumVersionProfileBytes = 4L * 1024 * 1024;
    internal static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private readonly IOfficialMavenClientLoaderHttpTransport _transport;
    private readonly IVerifiedMavenClientLoaderProcessRunner _processRunner;
    private readonly CmlDownloadReliabilityOptions _reliabilityOptions;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public OfficialMavenClientLoaderInstaller()
        : this(
            new HttpClientOfficialMavenClientLoaderHttpTransport(),
            new VerifiedMavenClientLoaderProcessRunner())
    {
    }

    internal OfficialMavenClientLoaderInstaller(
        IOfficialMavenClientLoaderHttpTransport transport,
        IVerifiedMavenClientLoaderProcessRunner processRunner)
        : this(
            transport,
            processRunner,
            CmlDownloadReliabilityOptions.Default,
            delayAsync: null)
    {
    }

    internal OfficialMavenClientLoaderInstaller(
        IOfficialMavenClientLoaderHttpTransport transport,
        IVerifiedMavenClientLoaderProcessRunner processRunner,
        CmlDownloadReliabilityOptions reliabilityOptions,
        Func<TimeSpan, CancellationToken, Task>? delayAsync)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _reliabilityOptions = (reliabilityOptions ??
            throw new ArgumentNullException(nameof(reliabilityOptions))).Validate();
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public async Task<string> InstallAsync(
        MinecraftClientLoader loader,
        string gameVersion,
        string loaderVersion,
        string stagingDirectory,
        string javaExecutablePath,
        IProgress<MinecraftClientInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var artifactUri = CreateArtifactUri(loader, gameVersion, loaderVersion);
        EnsureAllowed(loader, artifactUri);
        var sidecarUri = new Uri(artifactUri.AbsoluteUri + ".sha256", UriKind.Absolute);
        EnsureAllowed(loader, sidecarUri);
        var fullStagingDirectory = ValidateStagingDirectory(stagingDirectory);
        var versionsBefore = CaptureVersionDirectories(fullStagingDirectory);
        var operationId = Guid.NewGuid().ToString("N");
        var partialPath = Path.Combine(fullStagingDirectory, $".loader-{operationId}.partial");
        var verifiedPath = Path.Combine(fullStagingDirectory, $".loader-{operationId}.verified.jar");
        byte[]? expectedSha256 = null;
        try
        {
            progress?.Report(new MinecraftClientInstallProgress(
                "download",
                $"Downloading and verifying the official {loader} installer…"));
            expectedSha256 = await ExecuteDownloadWithRetryAsync(
                    sidecarUri,
                    "loader-sidecar",
                    token => DownloadSha256SidecarOnceAsync(loader, sidecarUri, token),
                    cleanup: null,
                    cancellationToken)
                .ConfigureAwait(false);
            var length = await ExecuteDownloadWithRetryAsync(
                    artifactUri,
                    "loader-installer",
                    token => DownloadAndVerifyInstallerOnceAsync(
                        loader,
                        artifactUri,
                        partialPath,
                        expectedSha256,
                        token),
                    () => TryDelete(partialPath),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(partialPath, verifiedPath, overwrite: false);

            var artifact = new VerifiedMavenClientLoaderArtifact(
                verifiedPath,
                artifactUri,
                expectedSha256.ToArray(),
                length);
            try
            {
                await _processRunner.RunAsync(
                        artifact,
                        javaExecutablePath,
                        fullStagingDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception processError)
            {
                ExceptionGraphSafety.RethrowOutOfMemory(processError);
                cancellationToken.ThrowIfCancellationRequested();
                throw new MinecraftClientLoaderProcessException(
                    "loader-process",
                    artifactUri.IdnHost,
                    processError);
            }

            try
            {
                return FindInstalledProfile(
                    loader,
                    gameVersion,
                    loaderVersion,
                    fullStagingDirectory,
                    versionsBefore,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception profileError)
            {
                ExceptionGraphSafety.RethrowOutOfMemory(profileError);
                cancellationToken.ThrowIfCancellationRequested();
                throw new MinecraftClientLoaderProcessException(
                    "loader-profile-verification",
                    artifactUri.IdnHost,
                    profileError);
            }
        }
        finally
        {
            if (expectedSha256 is not null)
            {
                CryptographicOperations.ZeroMemory(expectedSha256);
            }

            TryDelete(partialPath);
            TryDelete(verifiedPath);
        }
    }

    internal static Uri CreateArtifactUri(
        MinecraftClientLoader loader,
        string gameVersion,
        string loaderVersion) => loader switch
        {
            MinecraftClientLoader.Forge =>
                ForgeLoaderCatalogProvider.CreateInstallerArtifactUri(gameVersion, loaderVersion),
            MinecraftClientLoader.NeoForge =>
                NeoForgeLoaderCatalogProvider.CreateInstallerArtifactUri(gameVersion, loaderVersion),
            _ => throw new ArgumentOutOfRangeException(
                nameof(loader),
                loader,
                "Only Forge and NeoForge use the verified official Maven installer."),
        };

    internal static void EnsureAllowed(MinecraftClientLoader loader, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var expectedHost = loader switch
        {
            MinecraftClientLoader.Forge => "maven.minecraftforge.net",
            MinecraftClientLoader.NeoForge => "maven.neoforged.net",
            _ => throw new ArgumentOutOfRangeException(nameof(loader)),
        };
        var expectedPrefix = loader switch
        {
            MinecraftClientLoader.Forge => "/net/minecraftforge/forge/",
            MinecraftClientLoader.NeoForge => "/releases/net/neoforged/",
            _ => throw new ArgumentOutOfRangeException(nameof(loader)),
        };
        if (!uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.IdnHost.Equals(expectedHost, StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            (!uri.AbsolutePath.EndsWith("-installer.jar", StringComparison.Ordinal) &&
             !uri.AbsolutePath.EndsWith("-installer.jar.sha256", StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"The {loader} installer URI is not an allowlisted official Maven artifact.");
        }
    }

    private async Task<byte[]> DownloadSha256SidecarOnceAsync(
        MinecraftClientLoader loader,
        Uri uri,
        CancellationToken cancellationToken)
    {
        var bytes = await DownloadBoundedBytesAsync(
                loader,
                uri,
                MaximumSidecarBytes,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var text = Encoding.ASCII.GetString(bytes).Trim();
            if (text.Length != SHA256.HashSizeInBytes * 2 || !text.All(Uri.IsHexDigit))
            {
                throw new DownloadedFileValidationException(
                    MinecraftClientDownloadFailureKind.InvalidResponse);
            }

            return Convert.FromHexString(text);
        }
        catch (FormatException)
        {
            throw new DownloadedFileValidationException(
                MinecraftClientDownloadFailureKind.InvalidResponse);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<byte[]> DownloadBoundedBytesAsync(
        MinecraftClientLoader loader,
        Uri uri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        EnsureAllowed(loader, uri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);
        try
        {
            using var response = await _transport.GetAsync(uri, timeout.Token).ConfigureAwait(false);
            ValidateResponse(loader, uri, response, maximumBytes);
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            using var output = new MemoryStream(maximumBytes);
            var buffer = new byte[Math.Min(4_096, maximumBytes + 1)];
            while (true)
            {
                var read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (output.Length > maximumBytes - read)
                {
                    throw new InvalidDataException("The official Maven response exceeded its safety limit.");
                }

                output.Write(buffer, 0, read);
            }

            if (output.Length <= 0 ||
                response.Content.Headers.ContentLength is { } declared && declared != output.Length)
            {
                throw new DownloadedFileValidationException(
                    MinecraftClientDownloadFailureKind.SizeMismatch);
            }

            return output.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The official Maven request exceeded the {DownloadTimeout.TotalMinutes:0}-minute limit.");
        }
    }

    private async Task<long> DownloadAndVerifyInstallerOnceAsync(
        MinecraftClientLoader loader,
        Uri uri,
        string destinationPath,
        byte[] expectedSha256,
        CancellationToken cancellationToken)
    {
        EnsureAllowed(loader, uri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);
        try
        {
            using var response = await _transport.GetAsync(uri, timeout.Token).ConfigureAwait(false);
            ValidateResponse(loader, uri, response, MaximumInstallerBytes);
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > MaximumInstallerBytes)
                {
                    throw new InvalidDataException("The official loader installer exceeded its safety limit.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }

            await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            if (total <= 0 ||
                response.Content.Headers.ContentLength is { } declared && declared != total)
            {
                throw new DownloadedFileValidationException(
                    MinecraftClientDownloadFailureKind.SizeMismatch);
            }

            var actualSha256 = hash.GetHashAndReset();
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expectedSha256, actualSha256))
                {
                    throw new DownloadedFileValidationException(
                        MinecraftClientDownloadFailureKind.Sha256Mismatch);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actualSha256);
            }

            return total;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The official loader installer download exceeded the {DownloadTimeout.TotalMinutes:0}-minute limit.");
        }
    }

    private async Task<T> ExecuteDownloadWithRetryAsync<T>(
        Uri uri,
        string stage,
        Func<CancellationToken, Task<T>> operation,
        Action? cleanup,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _reliabilityOptions.MaximumFileAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                ExceptionGraphSafety.RethrowOutOfMemory(exception);
                cancellationToken.ThrowIfCancellationRequested();
                cleanup?.Invoke();
                var retryable = IsRetryableMavenDownloadFailure(exception);
                if (!retryable || attempt >= _reliabilityOptions.MaximumFileAttempts)
                {
                    throw new MinecraftClientDownloadException(
                        attempt,
                        uri.IdnHost,
                        CmlDownloadRetryPolicy.GetHttpStatusCode(exception),
                        CmlDownloadRetryPolicy.GetFailureKind(exception),
                        stage,
                        exception);
                }

                await _delayAsync(
                        _reliabilityOptions.GetDelayAfterAttempt(attempt),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("The bounded Maven retry loop did not complete.");
    }

    private static bool IsRetryableMavenDownloadFailure(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            var failures = aggregate.Flatten().InnerExceptions;
            return failures.Count > 0 && failures.All(IsRetryableMavenDownloadFailure);
        }

        if (exception is DownloadedFileValidationException)
        {
            return true;
        }

        if (exception is HttpRequestException or TimeoutException or TaskCanceledException or
            HttpIOException or System.Net.Sockets.SocketException)
        {
            return CmlDownloadRetryPolicy.IsRetryable(exception, CancellationToken.None);
        }

        return exception is IOException { InnerException: { } inner } &&
            IsRetryableMavenDownloadFailure(inner);
    }

    private static void ValidateResponse(
        MinecraftClientLoader loader,
        Uri requestedUri,
        HttpResponseMessage response,
        long maximumBytes)
    {
        var effectiveUri = response.RequestMessage?.RequestUri;
        if (effectiveUri is null)
        {
            throw new InvalidDataException("The official Maven response has no request identity.");
        }

        EnsureAllowed(loader, effectiveUri);
        if (!requestedUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)
                .Equals(
                    effectiveUri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped),
                    StringComparison.Ordinal))
        {
            throw new InvalidDataException("Official Maven redirects are not allowed.");
        }

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new InvalidDataException("Official Maven redirects are not allowed.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Official Maven returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is not { } length || length <= 0)
        {
            throw new DownloadedFileValidationException(
                MinecraftClientDownloadFailureKind.InvalidResponse);
        }

        if (length > maximumBytes)
        {
            throw new InvalidDataException("The official Maven response has an invalid declared length.");
        }
    }

    private static HashSet<string> CaptureVersionDirectories(string stagingDirectory)
    {
        var versionsRoot = Path.Combine(stagingDirectory, "versions");
        if (!Directory.Exists(versionsRoot))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        RejectReparsePoint(versionsRoot, "versions directory");
        return Directory.EnumerateDirectories(versionsRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string FindInstalledProfile(
        MinecraftClientLoader loader,
        string gameVersion,
        string loaderVersion,
        string stagingDirectory,
        IReadOnlySet<string> versionsBefore,
        CancellationToken cancellationToken)
    {
        var versionsRoot = Path.Combine(stagingDirectory, "versions");
        if (!Directory.Exists(versionsRoot))
        {
            throw new InvalidDataException("The verified loader installer did not create a launch profile.");
        }

        RejectReparsePoint(versionsRoot, "versions directory");
        var directories = Directory.EnumerateDirectories(versionsRoot, "*", SearchOption.TopDirectoryOnly)
            .Take(MaximumVersionProfiles + 1)
            .ToArray();
        if (directories.Length > MaximumVersionProfiles)
        {
            throw new InvalidDataException("The loader installer created too many version profiles.");
        }

        var expectedLibrary = GetExpectedLoaderLibrary(loader, gameVersion, loaderVersion);
        var candidates = new List<string>();
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profileId = Path.GetFileName(directory);
            if (versionsBefore.Contains(profileId) || !IsSafeProfileId(profileId))
            {
                continue;
            }

            RejectReparsePoint(directory, "loader version profile directory");
            var jsonPath = Path.Combine(directory, profileId + ".json");
            if (!File.Exists(jsonPath))
            {
                continue;
            }

            RejectReparsePoint(jsonPath, "loader version profile");
            var file = new FileInfo(jsonPath);
            if (file.Length is <= 0 or > MaximumVersionProfileBytes ||
                !ProfileContainsExpectedLibrary(jsonPath, profileId, expectedLibrary))
            {
                continue;
            }

            candidates.Add(profileId);
        }

        return candidates.Count == 1
            ? candidates[0]
            : throw new InvalidDataException(
                "The verified loader installer did not create exactly one matching launch profile.");
    }

    private static bool ProfileContainsExpectedLibrary(
        string jsonPath,
        string profileId,
        string expectedLibrary)
    {
        using var stream = new FileStream(
            jsonPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        using var document = JsonDocument.Parse(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.String ||
            !string.Equals(id.GetString(), profileId, StringComparison.Ordinal) ||
            !document.RootElement.TryGetProperty("libraries", out var libraries) ||
            libraries.ValueKind != JsonValueKind.Array ||
            libraries.GetArrayLength() > 8_192)
        {
            return false;
        }

        foreach (var library in libraries.EnumerateArray())
        {
            if (library.ValueKind == JsonValueKind.Object &&
                library.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String &&
                string.Equals(name.GetString(), expectedLibrary, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetExpectedLoaderLibrary(
        MinecraftClientLoader loader,
        string gameVersion,
        string loaderVersion) => loader switch
        {
            MinecraftClientLoader.Forge => $"net.minecraftforge:forge:{gameVersion}-{loaderVersion}",
            MinecraftClientLoader.NeoForge when gameVersion == "1.20.1" =>
                $"net.neoforged:forge:{loaderVersion}",
            MinecraftClientLoader.NeoForge => $"net.neoforged:neoforge:{loaderVersion}",
            _ => throw new ArgumentOutOfRangeException(nameof(loader)),
        };

    private static bool IsSafeProfileId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 192 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static string ValidateStagingDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("The client staging directory does not exist.");
        }

        RejectReparsePoint(fullPath, "client staging directory");
        return fullPath;
    }

    private static void RejectReparsePoint(string path, string label)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"The {label} cannot be a reparse point.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ExceptionGraphSafety.RethrowOutOfMemory(exception);
        }
    }
}

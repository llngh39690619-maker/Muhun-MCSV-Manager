using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Updater;

public sealed record ProductGuiActivationAck(
    int SessionId,
    int ProcessId,
    string Version,
    string Nonce,
    string GuiExecutablePath);

public interface IProductWindowsServicePlatform
{
    Task ConfigureAndRestartAsync(
        string serviceExecutablePath,
        string dataRoot,
        CancellationToken cancellationToken);

    Task<ProductGuiActivationAck> RequestGuiActivationAsync(
        string guiExecutablePath,
        string expectedVersion,
        CancellationToken cancellationToken);

    bool IsGuiActivationAlive(ProductGuiActivationAck acknowledgement);
}

public sealed class ProductWindowsServicePlatform : IProductWindowsServicePlatform
{
    public const string ServiceName = "MuhunMCSV";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BrokerConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BrokerRequestTimeout = TimeSpan.FromSeconds(100);

    public async Task ConfigureAndRestartAsync(
        string serviceExecutablePath,
        string dataRoot,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Product activation requires Windows Service control.");
        }

        var servicePath = ProductActivationPathPolicy.ValidateExecutable(
            serviceExecutablePath,
            "Muhun MCSV Service.exe");
        var normalizedDataRoot = ProductActivationCredentialReader.ValidateDataRoot(dataRoot);
        _ = await RunScAsync(["stop", ServiceName], allowNotStarted: true, cancellationToken)
            .ConfigureAwait(false);
        await WaitForStoppedAsync(cancellationToken).ConfigureAwait(false);
        _ = await RunScAsync(
                [
                    "config",
                    ServiceName,
                    "binPath=",
                    $"\"{servicePath}\" \"--Mcsv:Service:DataRoot={normalizedDataRoot}\"",
                    "start=",
                    "delayed-auto",
                ],
                allowNotStarted: false,
                cancellationToken)
            .ConfigureAwait(false);
        _ = await RunScAsync(["start", ServiceName], allowNotStarted: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ProductGuiActivationAck> RequestGuiActivationAsync(
        string guiExecutablePath,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        var guiPath = ProductActivationPathPolicy.ValidateExecutable(
            guiExecutablePath,
            "Muhun MCSV Manager.exe");
        ProductUpdateManifestParser.ValidateVersion(expectedVersion);
        var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var request = new ProductGuiActivationRequest(
            ProductGuiActivationProtocol.SchemaVersion,
            guiPath,
            expectedVersion,
            nonce);

        using var pipe = new NamedPipeClientStream(
            ".",
            ProductGuiActivationProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectDeadline.CancelAfter(BrokerConnectTimeout);
        try
        {
            await pipe.ConnectAsync(connectDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "No interactive-user GUI activation broker acknowledged the update request.");
        }

        using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestDeadline.CancelAfter(BrokerRequestTimeout);
        ProductGuiActivationResponse response;
        try
        {
            await ProductGuiActivationProtocol.WriteAsync(pipe, request, requestDeadline.Token)
                .ConfigureAwait(false);
            response = await ProductGuiActivationProtocol
                .ReadAsync<ProductGuiActivationResponse>(pipe, requestDeadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Interactive-user GUI activation did not complete before the authenticated deadline.");
        }
        if (!response.Accepted ||
            response.SessionId <= 0 ||
            response.ProcessId <= 0 ||
            !string.Equals(response.Version, expectedVersion, StringComparison.Ordinal) ||
            !string.Equals(response.Nonce, nonce, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Interactive-user GUI activation was rejected.");
        }

        var acknowledgement = new ProductGuiActivationAck(
            response.SessionId,
            response.ProcessId,
            response.Version,
            response.Nonce,
            guiPath);
        if (!IsGuiActivationAlive(acknowledgement))
        {
            throw new InvalidOperationException("Activated GUI did not remain alive in an interactive session.");
        }

        return acknowledgement;
    }

    public bool IsGuiActivationAlive(ProductGuiActivationAck acknowledgement)
    {
        if (acknowledgement.SessionId <= 0 || acknowledgement.ProcessId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(acknowledgement.ProcessId);
            if (process.HasExited || process.SessionId != acknowledgement.SessionId)
            {
                return false;
            }

            var actualPath = process.MainModule?.FileName;
            return actualPath is not null &&
                   string.Equals(
                       Path.GetFullPath(actualPath),
                       acknowledgement.GuiExecutablePath,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task WaitForStoppedAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(CommandTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await RunScAsync(["query", ServiceName], allowNotStarted: true, cancellationToken)
                .ConfigureAwait(false);
            if (result.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ||
                result.Contains("1060", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Muhun MCSV Service did not stop before the activation deadline.");
    }

    private static async Task<string> RunScAsync(
        IReadOnlyList<string> arguments,
        bool allowNotStarted,
        CancellationToken cancellationToken)
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var scPath = Path.Combine(windowsDirectory, "System32", "sc.exe");
        if (!File.Exists(scPath))
        {
            throw new FileNotFoundException("Windows Service controller was not found.", scPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = scPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows Service controller could not be started.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CommandTimeout);
        var standardOutput = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var standardError = process.StandardError.ReadToEndAsync(deadline.Token);
        await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        var output = (await standardOutput.ConfigureAwait(false)) + (await standardError.ConfigureAwait(false));
        if (output.Length > 64 * 1024)
        {
            throw new InvalidDataException("Windows Service controller returned an oversized response.");
        }

        if (process.ExitCode != 0 &&
            !(allowNotStarted &&
              (output.Contains("1060", StringComparison.Ordinal) ||
               output.Contains("1062", StringComparison.Ordinal))))
        {
            throw new InvalidOperationException($"Windows Service controller failed with exit code {process.ExitCode}.");
        }

        return output;
    }
}

public sealed class ProductWindowsActivationHealthController : IProductUpdateHealthController, IDisposable
{
    private const int MaximumHealthResponseBytes = 16 * 1024;
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly int _servicePort;
    private readonly string _dataRoot;
    private readonly IProductWindowsServicePlatform _platform;
    private readonly HttpClient _httpClient;
    private readonly byte[] _serviceToken;
    private readonly Guid _installationId;
    private ProductGuiActivationAck? _guiAcknowledgement;
    private int _disposed;

    public ProductWindowsActivationHealthController(
        int servicePort,
        string dataRoot,
        IProductWindowsServicePlatform? platform = null,
        HttpMessageHandler? httpHandler = null)
    {
        if (servicePort is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(servicePort));
        }

        _servicePort = servicePort;
        _dataRoot = ProductActivationCredentialReader.ValidateDataRoot(dataRoot);
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
        _guiAcknowledgement = null;
        var layout = ProductActivationPathPolicy.ResolveFormalLayout(executablePath);
        ProductActivationPathPolicy.ValidateMatchingProductVersions(layout);

        await _platform.ConfigureAndRestartAsync(layout.ServicePath, _dataRoot, cancellationToken)
            .ConfigureAwait(false);
        _guiAcknowledgement = await _platform
            .RequestGuiActivationAsync(layout.GuiPath, layout.Version, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> WaitForHealthyAsync(
        string version,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ProductUpdateManifestParser.ValidateVersion(version);
        if (_guiAcknowledgement is null ||
            !string.Equals(_guiAcknowledgement.Version, version, StringComparison.Ordinal))
        {
            return false;
        }

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        var endpoint = new Uri(
            $"http://127.0.0.1:{_servicePort}/api/v1/system/activation-ready",
            UriKind.Absolute);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_platform.IsGuiActivationAlive(_guiAcknowledgement))
            {
                return false;
            }

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
                        return true;
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
                throw new InvalidDataException("Service activation response exceeded its limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed record ProductFormalActivationLayout(
    string VersionRoot,
    string Version,
    string GuiPath,
    string ServicePath,
    string UpdaterPath);

internal static class ProductActivationPathPolicy
{
    public static ProductFormalActivationLayout ResolveFormalLayout(string guiExecutablePath)
    {
        var guiPath = ValidateExecutable(guiExecutablePath, "Muhun MCSV Manager.exe");
        var guiDirectory = Directory.GetParent(guiPath)?.FullName
            ?? throw new InvalidDataException("GUI directory is invalid.");
        if (!string.Equals(Path.GetFileName(guiDirectory), "gui-win-x64", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GUI is not in the formal gui-win-x64 payload directory.");
        }

        var versionRoot = Directory.GetParent(guiDirectory)?.FullName
            ?? throw new InvalidDataException("Formal version directory is invalid.");
        var version = Path.GetFileName(versionRoot);
        ProductUpdateManifestParser.ValidateVersion(version);
        RejectExistingReparsePoints(versionRoot);
        var servicePath = ValidateExecutable(
            Path.Combine(versionRoot, "service-win-x64", "Muhun MCSV Service.exe"),
            "Muhun MCSV Service.exe");
        var updaterPath = ValidateExecutable(
            Path.Combine(versionRoot, "updater-win-x64", "Muhun MCSV Updater.exe"),
            "Muhun MCSV Updater.exe");
        return new ProductFormalActivationLayout(versionRoot, version, guiPath, servicePath, updaterPath);
    }

    public static void ValidateMatchingProductVersions(ProductFormalActivationLayout layout)
    {
        foreach (var path in new[] { layout.GuiPath, layout.ServicePath, layout.UpdaterPath })
        {
            var productVersion = NormalizeProductVersion(FileVersionInfo.GetVersionInfo(path).ProductVersion);
            if (!string.Equals(productVersion, layout.Version, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "GUI, Service, Updater and version directory product versions do not match.");
            }
        }
    }

    public static string ValidateExecutable(string path, string requiredFileName)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Activation executable path must be absolute.");
        }

        var fullPath = Path.GetFullPath(path);
        RejectExistingReparsePoints(fullPath);
        if (!string.Equals(Path.GetFileName(fullPath), requiredFileName, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(fullPath) ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Activation executable is missing or has an unexpected identity.");
        }

        return fullPath;
    }

    public static void RejectExistingReparsePoints(string path)
    {
        FileSystemInfo? current = File.Exists(path)
            ? new FileInfo(Path.GetFullPath(path))
            : new DirectoryInfo(Path.GetFullPath(path));
        for (; current is not null; current = current switch
               {
                   FileInfo file => file.Directory,
                   DirectoryInfo directory => directory.Parent,
                   _ => null,
               })
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Activation paths cannot traverse a reparse point.");
            }
        }
    }

    private static string NormalizeProductVersion(string? value)
    {
        var normalized = value?.Split('+', 2)[0] ?? string.Empty;
        ProductUpdateManifestParser.ValidateVersion(normalized);
        return normalized;
    }
}

internal static class ProductActivationCredentialReader
{
    private const string MarkerName = ".muhun-mcsv-data-root";
    private const string ExpectedMarker = "muhun.mcsv.manager:1";

    public static string ValidateDataRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Product data root must be absolute.");
        }

        var fullPath = Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.IndexOf('"') >= 0)
        {
            throw new InvalidDataException("Product data root must be a safe local path.");
        }

        ProductActivationPathPolicy.RejectExistingReparsePoints(fullPath);
        var marker = Path.Combine(fullPath, MarkerName);
        var markerText = ReadBoundedAscii(marker, 64).Trim();
        if (!string.Equals(markerText, ExpectedMarker, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Product data root marker is missing or invalid.");
        }

        return fullPath;
    }

    public static (byte[] Token, Guid InstallationId) Read(string dataRoot)
    {
        var root = ValidateDataRoot(dataRoot);
        var tokenPath = ResolveUnderRoot(root, ProductLocalApiAuthentication.TokenRelativePath);
        var token = ReadBoundedAscii(
            tokenPath,
            ProductLocalApiAuthentication.MaximumCredentialFileBytes).Trim();
        if (token.Length != 64 || token.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Stored Service REST token is invalid.");
        }

        var identityPath = ResolveUnderRoot(
            root,
            ProductLocalApiAuthentication.InstallationIdentityRelativePath);
        var identity = ReadBoundedAscii(
            identityPath,
            ProductLocalApiAuthentication.MaximumCredentialFileBytes).Trim();
        if (!Guid.TryParseExact(identity, "D", out var installationId) || installationId == Guid.Empty)
        {
            throw new InvalidDataException("Stored installation identity is invalid.");
        }

        return (Encoding.ASCII.GetBytes(token.ToUpperInvariant()), installationId);
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Activation credential escaped the managed data root.");
        }

        ProductActivationPathPolicy.RejectExistingReparsePoints(candidate);
        return candidate;
    }

    private static string ReadBoundedAscii(string path, int maximumBytes)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException("Required activation credential is unavailable.", path);
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            256,
            FileOptions.SequentialScan);
        if (stream.Length is < 1 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException("Activation credential has an invalid size.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        stream.ReadExactly(bytes);
        if (bytes.Any(value => value > 0x7f))
        {
            throw new InvalidDataException("Activation credential is not ASCII.");
        }

        return Encoding.ASCII.GetString(bytes);
    }
}

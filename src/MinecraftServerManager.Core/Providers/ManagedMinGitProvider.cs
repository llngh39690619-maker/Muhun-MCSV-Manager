using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Providers;

internal sealed record ManagedMinGitArtifact(
    string Version,
    Uri DownloadUri,
    string FileName,
    long Size,
    string Sha256,
    long RepositoryId,
    long ReleaseId,
    long AssetId);

public sealed record ManagedMinGitInstallation(
    string Version,
    string InstallDirectory,
    string CommandGitExecutablePath,
    string MingwGitExecutablePath,
    string ShellExecutablePath)
{
    public string CommandDirectory => Path.GetDirectoryName(CommandGitExecutablePath)!;

    public string MingwBinDirectory => Path.GetDirectoryName(MingwGitExecutablePath)!;

    public string UsrBinDirectory => Path.GetDirectoryName(ShellExecutablePath)!;
}

internal enum ManagedMinGitProgressPhase
{
    CheckingCache,
    Downloading,
    Extracting,
    Verifying
}

internal readonly record struct ManagedMinGitProgress(
    ManagedMinGitProgressPhase Phase,
    double? Percentage);

internal interface IManagedMinGitProvider
{
    Task<ManagedMinGitInstallation> EnsureInstalledAsync(
        IProgress<ManagedMinGitProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

internal interface IManagedMinGitVersionVerifier
{
    Task VerifyAsync(
        ManagedMinGitInstallation installation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Installs a hash-pinned official MinGit ZIP without executing its contents during extraction.
/// This deliberately avoids BuildTools' PortableGit self-extracting EXE, whose native progress
/// dialog cannot be hosted by the WPF progress surface.
/// </summary>
internal sealed class ManagedMinGitProvider : IManagedMinGitProvider
{
    private const int MaximumArchiveEntries = 2_048;
    private const long MaximumExtractedEntryBytes = 128L * 1024 * 1024;
    private const long MaximumExtractedTotalBytes = 512L * 1024 * 1024;
    private const double MaximumCompressionRatio = 500d;
    private const int DosReparsePointAttribute = (int)FileAttributes.ReparsePoint;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFileType = 0x8000;
    private const int UnixDirectoryType = 0x4000;
    private const int UnixSymbolicLinkType = 0xA000;
    private const string InstallDirectoryName = "mingit-2.45.2-windows-x64";
    private const string SourceArchiveName = ".managed-source.zip";
    private static readonly HashSet<string> ReservedWindowsNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
    private static readonly HttpClient SharedHttpClient = CreateDefaultHttpClient();
    private readonly HttpClient _httpClient;
    private readonly string _cacheRoot;
    private readonly ManagedMinGitArtifact _artifact;
    private readonly IManagedMinGitVersionVerifier _versionVerifier;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal static ManagedMinGitArtifact ReviewedArtifact { get; } = new(
        "2.45.2.windows.1",
        new Uri(
            "https://github.com/git-for-windows/git/releases/download/"
            + "v2.45.2.windows.1/MinGit-2.45.2-64-bit.zip"),
        "MinGit-2.45.2-64-bit.zip",
        46_444_520,
        "7ed2a3ce5bbbf8eea976488de5416894ca3e6a0347cee195a7d768ac146d5290",
        23_216_272,
        158_570_707,
        171_597_223);

    public ManagedMinGitProvider(string cacheRoot)
        : this(
            SharedHttpClient,
            cacheRoot,
            ReviewedArtifact,
            new ManagedMinGitSystemVersionVerifier())
    {
    }

    internal ManagedMinGitProvider(
        HttpClient httpClient,
        string cacheRoot,
        ManagedMinGitArtifact artifact,
        IManagedMinGitVersionVerifier versionVerifier)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        _versionVerifier = versionVerifier
            ?? throw new ArgumentNullException(nameof(versionVerifier));
        ValidateArtifact(_artifact);
    }

    public async Task<ManagedMinGitInstallation> EnsureInstalledAsync(
        IProgress<ManagedMinGitProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("受管理 MinGit 目前只支援 Windows x64。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_cacheRoot);
            EnsureDirectoryIsNotReparsePoint(_cacheRoot, "MinGit cache 根目錄");
            var stagingRoot = SafePath.CombineUnderRoot(_cacheRoot, ".staging");
            Directory.CreateDirectory(stagingRoot);
            SafePath.EnsureNoReparsePointsUnderRoot(_cacheRoot, stagingRoot);

            var destination = SafePath.CombineUnderRoot(
                _cacheRoot,
                InstallDirectoryName);
            progress?.Report(new ManagedMinGitProgress(
                ManagedMinGitProgressPhase.CheckingCache,
                null));
            if (Directory.Exists(destination))
            {
                try
                {
                    return await ValidateInstallationAsync(
                            destination,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is InvalidDataException
                        or FileNotFoundException
                        or UnauthorizedAccessException
                        or System.ComponentModel.Win32Exception)
                {
                    await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                            _cacheRoot,
                            destination,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }

            if (File.Exists(destination))
            {
                throw new InvalidDataException(
                    $"MinGit cache 目的路徑被非資料夾占用：{destination}");
            }

            var operationId = Guid.NewGuid().ToString("N");
            var operationRoot = SafePath.CombineUnderRoot(stagingRoot, operationId);
            var archivePath = SafePath.CombineUnderRoot(
                operationRoot,
                _artifact.FileName);
            var extractionRoot = SafePath.CombineUnderRoot(
                operationRoot,
                "extracted");
            var promotedByThisOperation = false;
            Exception? operationFailure = null;
            try
            {
                Directory.CreateDirectory(operationRoot);
                SafePath.EnsureNoReparsePointsUnderRoot(stagingRoot, operationRoot);
                progress?.Report(new ManagedMinGitProgress(
                    ManagedMinGitProgressPhase.Downloading,
                    0d));
                await FirstPartyArtifactHttp.DownloadVerifiedSha256Async(
                        _httpClient,
                        _artifact.DownloadUri,
                        archivePath,
                        _artifact.Sha256,
                        _artifact.Size,
                        IsAllowedArtifactUri,
                        IsAllowedRedirect,
                        _artifact.FileName,
                        "Git for Windows MinGit",
                        new PhaseProgress(progress, ManagedMinGitProgressPhase.Downloading),
                        cancellationToken)
                    .ConfigureAwait(false);

                Directory.CreateDirectory(extractionRoot);
                SafePath.EnsureNoReparsePointsUnderRoot(stagingRoot, extractionRoot);
                progress?.Report(new ManagedMinGitProgress(
                    ManagedMinGitProgressPhase.Extracting,
                    0d));
                await ExtractZipSafelyAsync(
                        archivePath,
                        extractionRoot,
                        new PhaseProgress(progress, ManagedMinGitProgressPhase.Extracting),
                        cancellationToken)
                    .ConfigureAwait(false);
                File.Move(
                    archivePath,
                    SafePath.CombineUnderRoot(extractionRoot, SourceArchiveName),
                    overwrite: false);
                SafePath.EnsureTreeContainsNoReparsePoints(
                    extractionRoot,
                    MaximumArchiveEntries);

                progress?.Report(new ManagedMinGitProgress(
                    ManagedMinGitProgressPhase.Verifying,
                    null));
                _ = await ValidateInstallationAsync(
                        extractionRoot,
                        cancellationToken)
                    .ConfigureAwait(false);

                // Another application process can win the same atomic cache promotion. Accept
                // only a fully revalidated winner; never merge two extracted trees.
                if (Directory.Exists(destination))
                {
                    return await ValidateInstallationAsync(destination, cancellationToken)
                        .ConfigureAwait(false);
                }

                try
                {
                    await AdoptiumRuntimeProvider.MoveDirectoryWithRetryAsync(
                            extractionRoot,
                            destination,
                            cancellationToken)
                        .ConfigureAwait(false);
                    promotedByThisOperation = true;
                }
                catch (IOException) when (Directory.Exists(destination))
                {
                    return await ValidateInstallationAsync(destination, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) when (Directory.Exists(destination))
                {
                    return await ValidateInstallationAsync(destination, cancellationToken)
                        .ConfigureAwait(false);
                }

                try
                {
                    return await ValidateInstallationAsync(destination, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception validationFailure)
                {
                    if (promotedByThisOperation)
                    {
                        try
                        {
                            await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                                    _cacheRoot,
                                    destination,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch (Exception cleanupFailure)
                        {
                            throw CreateCleanupFailure(validationFailure, cleanupFailure);
                        }
                    }

                    throw;
                }
            }
            catch (Exception exception)
            {
                operationFailure = exception;
                throw;
            }
            finally
            {
                try
                {
                    await CleanupOperationArtifactsAsync(operationRoot)
                        .ConfigureAwait(false);
                }
                catch (Exception cleanupFailure) when (operationFailure is not null)
                {
                    throw CreateCleanupFailure(operationFailure, cleanupFailure);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static async Task ExtractZipSafelyAsync(
        string archivePath,
        string destinationRoot,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(destinationRoot));
        Directory.CreateDirectory(normalizedRoot);
        EnsureDirectoryIsNotReparsePoint(normalizedRoot, "MinGit 解壓根目錄");

        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is < 1 or > MaximumArchiveEntries)
        {
            throw new InvalidDataException("MinGit ZIP 的項目數不在安全範圍內。");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validated = new List<ValidatedZipEntry>(archive.Entries.Count);
        long totalDeclaredBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectArchiveLinkOrSpecialEntry(entry);
            var relativePath = ValidateArchiveRelativePath(entry.FullName);
            if (!paths.Add(relativePath))
            {
                throw new InvalidDataException(
                    $"MinGit ZIP 包含大小寫或 Unicode 重複路徑：{entry.FullName}");
            }

            var isDirectory = string.IsNullOrEmpty(entry.Name);
            if (!isDirectory)
            {
                files.Add(relativePath);
                if (entry.Length is < 0 or > MaximumExtractedEntryBytes)
                {
                    throw new InvalidDataException(
                        $"MinGit ZIP 項目超過安全大小：{entry.FullName}");
                }

                totalDeclaredBytes = checked(totalDeclaredBytes + entry.Length);
                if (totalDeclaredBytes > MaximumExtractedTotalBytes)
                {
                    throw new InvalidDataException(
                        "MinGit ZIP 解壓縮總大小超過安全上限。");
                }

                if (entry.Length > 0
                    && (entry.CompressedLength <= 0
                        || (double)entry.Length / entry.CompressedLength
                        > MaximumCompressionRatio))
                {
                    throw new InvalidDataException(
                        $"MinGit ZIP 項目的壓縮比超過安全上限：{entry.FullName}");
                }
            }

            var destination = Path.GetFullPath(Path.Combine(
                normalizedRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!SafePath.IsWithinRoot(normalizedRoot, destination))
            {
                throw new InvalidDataException(
                    $"MinGit ZIP 包含逃出目的資料夾的路徑：{entry.FullName}");
            }

            validated.Add(new ValidatedZipEntry(
                entry,
                relativePath,
                destination,
                isDirectory));
        }

        foreach (var path in paths)
        {
            for (var index = path.IndexOf('/'); index >= 0; index = path.IndexOf('/', index + 1))
            {
                if (files.Contains(path[..index]))
                {
                    throw new InvalidDataException(
                        $"MinGit ZIP 同時把父路徑當成檔案與資料夾：{path}");
                }
            }
        }

        var completed = 0;
        foreach (var item in validated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(item.DestinationPath);
                SafePath.EnsureNoReparsePointsUnderRoot(
                    normalizedRoot,
                    item.DestinationPath);
            }
            else
            {
                var parent = Path.GetDirectoryName(item.DestinationPath)
                    ?? throw new InvalidDataException("MinGit ZIP 項目缺少父資料夾。");
                Directory.CreateDirectory(parent);
                SafePath.EnsureNoReparsePointsUnderRoot(normalizedRoot, parent);
                await using var input = item.Entry.Open();
                await using var output = new FileStream(
                    item.DestinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[128 * 1024];
                long actualBytes = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    actualBytes = checked(actualBytes + read);
                    if (actualBytes > item.Entry.Length)
                    {
                        throw new InvalidDataException(
                            $"MinGit ZIP 項目超過宣告大小：{item.Entry.FullName}");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                }

                if (actualBytes != item.Entry.Length)
                {
                    throw new InvalidDataException(
                        $"MinGit ZIP 項目大小與宣告不符：{item.Entry.FullName}");
                }
            }

            completed++;
            progress?.Report((double)completed / validated.Count);
        }
    }

    internal static bool IsAllowedArtifactUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        if (uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsolutePath.Equals(
                    "/git-for-windows/git/releases/download/"
                    + "v2.45.2.windows.1/MinGit-2.45.2-64-bit.zip",
                    StringComparison.Ordinal)
                && string.IsNullOrEmpty(uri.Query)
                && string.IsNullOrEmpty(uri.Fragment);
        }

        return uri.IdnHost.Equals(
                "release-assets.githubusercontent.com",
                StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith(
                $"/github-production-release-asset/{ReviewedArtifact.RepositoryId}/",
                StringComparison.Ordinal)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    internal static bool IsAllowedRedirect(Uri from, Uri to) =>
        IsAllowedArtifactUri(from)
        && IsAllowedArtifactUri(to)
        && (from.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && to.IdnHost.Equals(
                "release-assets.githubusercontent.com",
                StringComparison.OrdinalIgnoreCase)
            || from.IdnHost.Equals(
                "release-assets.githubusercontent.com",
                StringComparison.OrdinalIgnoreCase)
            && to.IdnHost.Equals(
                "release-assets.githubusercontent.com",
                StringComparison.OrdinalIgnoreCase));

    private async Task<ManagedMinGitInstallation> ValidateInstallationAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedDirectory = SafePath.EnsureNoReparsePointsUnderRoot(
            _cacheRoot,
            installDirectory);
        SafePath.EnsureTreeContainsNoReparsePoints(
            normalizedDirectory,
            MaximumArchiveEntries);
        await VerifyInstallationMatchesSourceArchiveAsync(
                normalizedDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        var installation = new ManagedMinGitInstallation(
            _artifact.Version,
            normalizedDirectory,
            Path.Combine(normalizedDirectory, "cmd", "git.exe"),
            Path.Combine(normalizedDirectory, "mingw64", "bin", "git.exe"),
            Path.Combine(normalizedDirectory, "usr", "bin", "sh.exe"));
        EnsureRegularFile(
            normalizedDirectory,
            installation.CommandGitExecutablePath,
            "MinGit cmd/git.exe");
        EnsureRegularFile(
            normalizedDirectory,
            installation.MingwGitExecutablePath,
            "MinGit mingw64/bin/git.exe");
        EnsureRegularFile(
            normalizedDirectory,
            installation.ShellExecutablePath,
            "MinGit usr/bin/sh.exe");

        await _versionVerifier.VerifyAsync(installation, cancellationToken)
            .ConfigureAwait(false);

        // Recheck the complete immutable cache tree and both executables after executing Git so
        // a concurrent path swap cannot be accepted merely because the preflight was valid.
        SafePath.EnsureTreeContainsNoReparsePoints(
            normalizedDirectory,
            MaximumArchiveEntries);
        EnsureRegularFile(
            normalizedDirectory,
            installation.CommandGitExecutablePath,
            "已驗證 MinGit cmd/git.exe");
        EnsureRegularFile(
            normalizedDirectory,
            installation.ShellExecutablePath,
            "已驗證 MinGit usr/bin/sh.exe");
        return installation;
    }

    private async Task VerifyInstallationMatchesSourceArchiveAsync(
        string installDirectory,
        CancellationToken cancellationToken)
    {
        var sourceArchive = SafePath.CombineUnderRoot(
            installDirectory,
            SourceArchiveName);
        EnsureRegularFile(installDirectory, sourceArchive, "MinGit pinned source ZIP");
        await using var sourceStream = new FileStream(
            sourceArchive,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await VerifyFileSha256Async(
                sourceStream,
                _artifact.Size,
                _artifact.Sha256,
                cancellationToken)
            .ConfigureAwait(false);

        sourceStream.Position = 0;
        using var archive = new ZipArchive(
            sourceStream,
            ZipArchiveMode.Read,
            leaveOpen: true);
        if (archive.Entries.Count is < 1 or > MaximumArchiveEntries)
        {
            throw new InvalidDataException("MinGit pinned source ZIP 的項目數不在安全範圍內。");
        }

        var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            SourceArchiveName
        };
        var archivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectArchiveLinkOrSpecialEntry(entry);
            var relativePath = ValidateArchiveRelativePath(entry.FullName);
            if (!archivePaths.Add(relativePath))
            {
                throw new InvalidDataException(
                    $"MinGit pinned source ZIP 包含重複路徑：{entry.FullName}");
            }

            expectedPaths.Add(relativePath);
            AddAncestorPaths(expectedPaths, relativePath);
            var installedPath = SafePath.CombineUnderRoot(
                installDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(entry.Name))
            {
                if (!Directory.Exists(installedPath)
                    || File.GetAttributes(installedPath).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException(
                        $"MinGit cache 資料夾與 pinned source 不符：{relativePath}");
                }

                continue;
            }

            EnsureRegularFile(
                installDirectory,
                installedPath,
                $"MinGit cache file {relativePath}");
            var installedInfo = new FileInfo(installedPath);
            if (installedInfo.Length != entry.Length)
            {
                throw new InvalidDataException(
                    $"MinGit cache file 大小與 pinned source 不符：{relativePath}");
            }

            await using var expected = entry.Open();
            await using var actual = new FileStream(
                installedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await EnsureStreamsEqualAsync(
                    expected,
                    actual,
                    relativePath,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };
        var actualEntryCount = 0;
        foreach (var item in new DirectoryInfo(installDirectory)
                     .EnumerateFileSystemInfos("*", enumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++actualEntryCount > MaximumArchiveEntries)
            {
                throw new InvalidDataException("MinGit cache 項目數超過安全上限。");
            }

            var relative = Path.GetRelativePath(installDirectory, item.FullName)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!expectedPaths.Contains(relative))
            {
                throw new InvalidDataException(
                    $"MinGit cache 包含 pinned source 沒有的額外項目：{relative}");
            }
        }

        static void AddAncestorPaths(HashSet<string> expectedPaths, string relativePath)
        {
            for (var index = relativePath.IndexOf('/');
                 index >= 0;
                 index = relativePath.IndexOf('/', index + 1))
            {
                expectedPaths.Add(relativePath[..index]);
            }
        }
    }

    private static async Task EnsureStreamsEqualAsync(
        Stream expected,
        Stream actual,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var expectedHash = await SHA256.HashDataAsync(expected, cancellationToken)
            .ConfigureAwait(false);
        var actualHash = await SHA256.HashDataAsync(actual, cancellationToken)
            .ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            throw new InvalidDataException(
                $"MinGit cache file 內容與 pinned source 不符：{relativePath}");
        }
    }

    private static async Task VerifyFileSha256Async(
        FileStream stream,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (stream.Length != expectedSize)
        {
            throw new InvalidDataException("MinGit pinned source ZIP 大小驗證失敗。");
        }

        stream.Position = 0;
        var actual = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        var expected = Convert.FromHexString(expectedSha256);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new InvalidDataException("MinGit pinned source ZIP SHA-256 驗證失敗。");
        }
    }

    private static void ValidateArtifact(ManagedMinGitArtifact artifact)
    {
        if (!artifact.Version.Equals("2.45.2.windows.1", StringComparison.Ordinal)
            || !artifact.FileName.Equals("MinGit-2.45.2-64-bit.zip", StringComparison.Ordinal)
            || artifact.Size is < 1 or > FirstPartyArtifactHttp.MaximumArtifactBytes
            || artifact.Sha256.Length != 64
            || artifact.Sha256.Any(static character => !Uri.IsHexDigit(character))
            || artifact.RepositoryId != 23_216_272
            || artifact.ReleaseId != 158_570_707
            || artifact.AssetId != 171_597_223
            || !IsAllowedArtifactUri(artifact.DownloadUri))
        {
            throw new InvalidDataException("受管理 MinGit artifact 未通過固定安全契約。");
        }
    }

    private static string ValidateArchiveRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > 4_096
            || path.Contains('\\')
            || path.StartsWith('/')
            || path.Any(static character =>
                char.IsControl(character) || char.IsSurrogate(character)))
        {
            throw new InvalidDataException($"MinGit ZIP 包含不安全路徑：{path}");
        }

        var candidate = path.EndsWith("/", StringComparison.Ordinal) ? path[..^1] : path;
        if (candidate.Length == 0)
        {
            throw new InvalidDataException("MinGit ZIP 包含空白根目錄項目。");
        }

        var segments = candidate.Split('/');
        var normalizedSegments = new string[segments.Length];
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            string normalized;
            try
            {
                normalized = segment.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    $"MinGit ZIP 包含無效 Unicode 路徑：{path}",
                    exception);
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
                throw new InvalidDataException(
                    $"MinGit ZIP 包含 Windows 不支援的路徑：{path}");
            }

            normalizedSegments[index] = normalized;
        }

        return string.Join('/', normalizedSegments);
    }

    private static void RejectArchiveLinkOrSpecialEntry(ZipArchiveEntry entry)
    {
        var attributes = entry.ExternalAttributes;
        var dosAttributes = attributes & 0xFFFF;
        var upperAttributes = (attributes >> 16) & 0xFFFF;
        var unixType = upperAttributes & UnixFileTypeMask;
        var isDirectory = string.IsNullOrEmpty(entry.Name);
        if ((dosAttributes & DosReparsePointAttribute) != 0
            || (unixType == 0 && (upperAttributes & DosReparsePointAttribute) != 0)
            || unixType == UnixSymbolicLinkType
            || unixType != 0
            && unixType != UnixRegularFileType
            && unixType != UnixDirectoryType
            || isDirectory && unixType == UnixRegularFileType
            || !isDirectory && unixType == UnixDirectoryType)
        {
            throw new InvalidDataException(
                "MinGit ZIP 不可包含 symbolic link、reparse point 或特殊檔案："
                + entry.FullName);
        }
    }

    private static void EnsureRegularFile(
        string root,
        string path,
        string context)
    {
        var normalized = SafePath.EnsureWithinRoot(root, path);
        if (!File.Exists(normalized))
        {
            throw new InvalidDataException($"{context} 不存在：{normalized}");
        }

        normalized = SafePath.EnsureNoReparsePointsUnderRoot(root, normalized);

        var attributes = File.GetAttributes(normalized);
        if (attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"{context} 必須是非連結的一般檔案。");
        }
    }

    private static void EnsureDirectoryIsNotReparsePoint(string path, string context)
    {
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"{context} 必須是非連結的一般資料夾：{path}");
        }
    }

    private async Task CleanupOperationArtifactsAsync(params string[] paths)
    {
        Exception? failure = null;
        foreach (var path in paths)
        {
            try
            {
                await SafePath.DeleteTreeWithoutFollowingReparsePointsWithRetryAsync(
                        _cacheRoot,
                        path,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure is not null)
        {
            throw new IOException(
                "無法完整清除未完成的 MinGit 下載或解壓暫存。",
                failure);
        }
    }

    private static Exception CreateCleanupFailure(
        Exception operationFailure,
        Exception cleanupFailure)
    {
        var combined = new AggregateException(operationFailure, cleanupFailure);
        return operationFailure is OperationCanceledException canceled
            ? new OperationCanceledException(
                "MinGit 作業已取消，但未完成的暫存檔案無法完整清除。",
                combined,
                canceled.CancellationToken)
            : new IOException(
                "MinGit 作業失敗，且未完成的暫存檔案無法完整清除。",
                combined);
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Muhun-MCSV-Manager/managed-mingit");
        return client;
    }

    private sealed record ValidatedZipEntry(
        ZipArchiveEntry Entry,
        string RelativePath,
        string DestinationPath,
        bool IsDirectory);

    private sealed class PhaseProgress(
        IProgress<ManagedMinGitProgress>? progress,
        ManagedMinGitProgressPhase phase) : IProgress<double>
    {
        public void Report(double value) => progress?.Report(new ManagedMinGitProgress(
            phase,
            Math.Clamp(value, 0d, 1d)));
    }
}

internal sealed class ManagedMinGitVersionCommandBuilder
{
    public ProcessStartInfo Build(ManagedMinGitInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(installation.CommandGitExecutablePath),
            WorkingDirectory = Path.GetFullPath(installation.InstallDirectory),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("--version");
        startInfo.Environment.Clear();
        var trustedPath = new[]
        {
            installation.CommandDirectory,
            installation.MingwBinDirectory,
            installation.UsrBinDirectory,
            Environment.SystemDirectory
        }.Where(static path => !string.IsNullOrWhiteSpace(path));
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, trustedPath);
        startInfo.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        startInfo.Environment["SHELL"] = installation.ShellExecutablePath;
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            startInfo.Environment["SystemRoot"] = windowsDirectory;
            startInfo.Environment["WINDIR"] = windowsDirectory;
        }

        return startInfo;
    }
}

internal sealed class ManagedMinGitSystemVersionVerifier(
    ManagedMinGitVersionCommandBuilder? commandBuilder = null)
    : IManagedMinGitVersionVerifier
{
    private const string ExpectedVersionLine = "git version 2.45.2.windows.1";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);
    private readonly ManagedMinGitVersionCommandBuilder _commandBuilder =
        commandBuilder ?? new ManagedMinGitVersionCommandBuilder();

    public async Task VerifyAsync(
        ManagedMinGitInstallation installation,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = _commandBuilder.Build(installation),
            EnableRaisingEvents = true
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("無法啟動受管理 MinGit 版本驗證。");
        }

        var stdoutTask = CaptureAsync(process.StandardOutput);
        var stderrTask = CaptureAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(ProcessTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is TimeoutException or OperationCanceledException)
        {
            TryTerminate(process);
            await TryDrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            if (exception is OperationCanceledException)
            {
                throw;
            }

            throw new InvalidDataException(
                "受管理 MinGit 的 git --version 超時，已終止驗證程序。",
                exception);
        }

        BoundedCapturedStream[] captured;
        try
        {
            captured = await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(DrainTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidDataException(
                "受管理 MinGit 已結束，但版本輸出未在安全時間內關閉。",
                exception);
        }

        var stdout = captured[0];
        var stderr = captured[1];
        if (process.ExitCode != 0
            || stdout.Truncated
            || stderr.Truncated
            || stdout.Lines.Count != 1
            || stderr.Lines.Count != 0
            || !stdout.Lines[0].Equals(ExpectedVersionLine, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "受管理 MinGit 版本驗證失敗；預期唯一輸出："
                + ExpectedVersionLine);
        }
    }

    internal static bool IsExactVersionOutput(
        int exitCode,
        IReadOnlyList<string> stdout,
        IReadOnlyList<string> stderr,
        bool truncated = false) =>
        exitCode == 0
        && !truncated
        && stdout.Count == 1
        && stderr.Count == 0
        && stdout[0].Equals(ExpectedVersionLine, StringComparison.Ordinal);

    private static Task<BoundedCapturedStream> CaptureAsync(TextReader reader) =>
        BoundedProcessOutputCapture.CaptureAsync(
            reader,
            maximumLines: 64,
            maximumCharacters: 32 * 1024,
            maximumLineCharacters: 4 * 1024);

    private static async Task TryDrainAsync(
        Task<BoundedCapturedStream> stdout,
        Task<BoundedCapturedStream> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch
        {
            // Timeout/cancellation remains the primary result.
        }
    }

    private static void TryTerminate(Process process)
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
            // Preserve the timeout/cancellation result if Windows denies termination.
        }
    }
}

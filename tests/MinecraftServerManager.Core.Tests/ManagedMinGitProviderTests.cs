using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Tests;

public sealed class ManagedMinGitProviderTests
{
    [Fact]
    public void ReviewedArtifact_IsPinnedToAuditedOfficialMinGitAsset()
    {
        var artifact = ManagedMinGitProvider.ReviewedArtifact;

        Assert.Equal("2.45.2.windows.1", artifact.Version);
        Assert.Equal("MinGit-2.45.2-64-bit.zip", artifact.FileName);
        Assert.Equal(46_444_520, artifact.Size);
        Assert.Equal(
            "7ed2a3ce5bbbf8eea976488de5416894ca3e6a0347cee195a7d768ac146d5290",
            artifact.Sha256);
        Assert.Equal(23_216_272, artifact.RepositoryId);
        Assert.Equal(158_570_707, artifact.ReleaseId);
        Assert.Equal(171_597_223, artifact.AssetId);
        Assert.True(ManagedMinGitProvider.IsAllowedArtifactUri(artifact.DownloadUri));
        Assert.False(ManagedMinGitProvider.IsAllowedArtifactUri(
            new Uri("https://example.invalid/MinGit-2.45.2-64-bit.zip")));
    }

    [Fact]
    public async Task EnsureInstalled_DownloadsOnceAtomicallyAndRevalidatesCachedVersion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            ("cmd/git.exe", "command git", null),
            ("mingw64/bin/git.exe", "mingw git", null),
            ("usr/bin/sh.exe", "shell", null));
        var artifact = TestArtifact(archive);
        var requests = 0;
        using var client = Client(_ =>
        {
            Interlocked.Increment(ref requests);
            return Download(archive, artifact.FileName);
        });
        var verifier = new RecordingVersionVerifier();
        var progress = new InlineProgress<ManagedMinGitProgress>();
        var provider = new ManagedMinGitProvider(
            client,
            Path.Combine(directory.Path, "cache"),
            artifact,
            verifier);

        var first = await provider.EnsureInstalledAsync(progress);
        var second = await provider.EnsureInstalledAsync(progress);

        Assert.Equal(first, second);
        Assert.Equal(1, requests);
        Assert.Equal(3, verifier.Installations.Count);
        Assert.Equal("command git", await File.ReadAllTextAsync(first.CommandGitExecutablePath));
        Assert.Equal("mingw git", await File.ReadAllTextAsync(first.MingwGitExecutablePath));
        Assert.Equal("shell", await File.ReadAllTextAsync(first.ShellExecutablePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            Path.Combine(directory.Path, "cache", ".staging")));
        Assert.Contains(progress.Values, value =>
            value.Phase == ManagedMinGitProgressPhase.Downloading);
        Assert.Contains(progress.Values, value =>
            value.Phase == ManagedMinGitProgressPhase.Extracting);
        Assert.Contains(progress.Values, value =>
            value.Phase == ManagedMinGitProgressPhase.Verifying);
    }

    [Fact]
    public async Task EnsureInstalled_MissingRequiredExecutableFailsClosedAndCleansOperation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            ("cmd/git.exe", "command git", null),
            ("usr/bin/sh.exe", "shell", null));
        var artifact = TestArtifact(archive);
        using var client = Client(_ => Download(archive, artifact.FileName));
        var cache = Path.Combine(directory.Path, "cache");
        var provider = new ManagedMinGitProvider(
            client,
            cache,
            artifact,
            new RecordingVersionVerifier());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.EnsureInstalledAsync());

        Assert.False(Directory.Exists(Path.Combine(cache, "mingit-2.45.2-windows-x64")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(cache, ".staging")));
    }

    [Fact]
    public async Task EnsureInstalled_TamperedCacheIsRejectedBeforeExecutionAndReinstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            ("cmd/git.exe", "command git", null),
            ("mingw64/bin/git.exe", "mingw git", null),
            ("usr/bin/sh.exe", "shell", null));
        var artifact = TestArtifact(archive);
        var requests = 0;
        using var client = Client(_ =>
        {
            requests++;
            return Download(archive, artifact.FileName);
        });
        var verifier = new RecordingVersionVerifier();
        var provider = new ManagedMinGitProvider(
            client,
            Path.Combine(directory.Path, "cache"),
            artifact,
            verifier);
        var installed = await provider.EnsureInstalledAsync();
        Assert.Equal(2, verifier.Installations.Count);
        await File.WriteAllTextAsync(installed.CommandGitExecutablePath, "tampered");

        var repaired = await provider.EnsureInstalledAsync();

        Assert.Equal(2, requests);
        Assert.Equal(4, verifier.Installations.Count);
        Assert.Equal(
            "command git",
            await File.ReadAllTextAsync(repaired.CommandGitExecutablePath));
    }

    [Fact]
    public async Task EnsureInstalled_CancellationDuringExtractionRemovesAllPartialFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var archive = CreateArchive(
            ("cmd/git.exe", new string('a', 8_192), null),
            ("mingw64/bin/git.exe", "mingw git", null),
            ("usr/bin/sh.exe", "shell", null));
        var artifact = TestArtifact(archive);
        using var client = Client(_ => Download(archive, artifact.FileName));
        var cache = Path.Combine(directory.Path, "cache");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<ManagedMinGitProgress>(value =>
        {
            if (value.Phase == ManagedMinGitProgressPhase.Extracting
                && value.Percentage is > 0d)
            {
                cancellation.Cancel();
            }
        });
        var provider = new ManagedMinGitProvider(
            client,
            cache,
            artifact,
            new RecordingVersionVerifier());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.EnsureInstalledAsync(progress, cancellation.Token));

        Assert.False(Directory.Exists(Path.Combine(cache, "mingit-2.45.2-windows-x64")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(cache, ".staging")));
    }

    [Fact]
    public async Task ExtractZipSafely_RejectsTraversalAndDoesNotWriteOutsideRoot()
    {
        using var directory = new TemporaryDirectory();
        var archiveBytes = CreateArchive(("../escaped.exe", "bad", null));
        var archivePath = Path.Combine(directory.Path, "bad.zip");
        await File.WriteAllBytesAsync(archivePath, archiveBytes);
        var destination = Path.Combine(directory.Path, "extract");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedMinGitProvider.ExtractZipSafelyAsync(
                archivePath,
                destination,
                null,
                CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(directory.Path, "escaped.exe")));
    }

    [Fact]
    public async Task ExtractZipSafely_RejectsSymlinkAndCompressionBombEntries()
    {
        using var directory = new TemporaryDirectory();
        var symlink = CreateArchive(
            ("cmd/git.exe", "target", unchecked((int)0xA1FF0000)));
        var symlinkPath = Path.Combine(directory.Path, "symlink.zip");
        await File.WriteAllBytesAsync(symlinkPath, symlink);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedMinGitProvider.ExtractZipSafelyAsync(
                symlinkPath,
                Path.Combine(directory.Path, "symlink"),
                null,
                CancellationToken.None));

        var bomb = CreateArchive(
            ("large.bin", new string('\0', 2 * 1024 * 1024), null));
        var bombPath = Path.Combine(directory.Path, "bomb.zip");
        await File.WriteAllBytesAsync(bombPath, bomb);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedMinGitProvider.ExtractZipSafelyAsync(
                bombPath,
                Path.Combine(directory.Path, "bomb"),
                null,
                CancellationToken.None));
    }

    [Fact]
    public async Task ExtractZipSafely_RejectsCaseDuplicatesAndFileAsParent()
    {
        using var directory = new TemporaryDirectory();
        var duplicate = CreateArchive(
            ("cmd/GIT.exe", "one", null),
            ("cmd/git.exe", "two", null));
        var duplicatePath = Path.Combine(directory.Path, "duplicate.zip");
        await File.WriteAllBytesAsync(duplicatePath, duplicate);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedMinGitProvider.ExtractZipSafelyAsync(
                duplicatePath,
                Path.Combine(directory.Path, "duplicate"),
                null,
                CancellationToken.None));

        var parentFile = CreateArchive(
            ("usr", "file", null),
            ("usr/bin/", string.Empty, null));
        var parentFilePath = Path.Combine(directory.Path, "parent-file.zip");
        await File.WriteAllBytesAsync(parentFilePath, parentFile);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ManagedMinGitProvider.ExtractZipSafelyAsync(
                parentFilePath,
                Path.Combine(directory.Path, "parent-file"),
                null,
                CancellationToken.None));
    }

    [Fact]
    public void VersionCommand_UsesIsolatedEnvironmentAndRequiresExactOutput()
    {
        using var directory = new TemporaryDirectory();
        var installation = CreateInstallation(directory.Path);

        var startInfo = new ManagedMinGitVersionCommandBuilder().Build(installation);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(["--version"], startInfo.ArgumentList);
        Assert.Equal(installation.ShellExecutablePath, startInfo.Environment["SHELL"]);
        Assert.Equal(
            string.Join(
                Path.PathSeparator,
                installation.CommandDirectory,
                installation.MingwBinDirectory,
                installation.UsrBinDirectory,
                Environment.SystemDirectory),
            startInfo.Environment["PATH"]);
        Assert.False(startInfo.Environment.ContainsKey("GIT_CONFIG_COUNT"));
        Assert.False(startInfo.Environment.ContainsKey("BASH_ENV"));
        Assert.False(startInfo.Environment.ContainsKey("MAVEN_OPTS"));
        Assert.True(ManagedMinGitSystemVersionVerifier.IsExactVersionOutput(
            0,
            ["git version 2.45.2.windows.1"],
            []));
        Assert.False(ManagedMinGitSystemVersionVerifier.IsExactVersionOutput(
            0,
            ["git version 2.45.2.windows.1", "extra"],
            []));
        Assert.False(ManagedMinGitSystemVersionVerifier.IsExactVersionOutput(
            0,
            ["git version 2.45.2.windows.2"],
            []));
    }

    [Fact]
    public async Task EnsureInstalled_RejectsReparsePointCacheBeforeNetworkRequest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var target = Path.Combine(directory.Path, "target");
        var cache = Path.Combine(directory.Path, "cache-link");
        Directory.CreateDirectory(target);
        ReparsePointTestHelper.CreateDirectoryLink(cache, target);
        var requests = 0;
        try
        {
            using var client = Client(_ =>
            {
                requests++;
                throw new Xunit.Sdk.XunitException("Network must not be used.");
            });
            var provider = new ManagedMinGitProvider(
                client,
                cache,
                ManagedMinGitProvider.ReviewedArtifact,
                new RecordingVersionVerifier());

            await Assert.ThrowsAnyAsync<Exception>(() => provider.EnsureInstalledAsync());

            Assert.Equal(0, requests);
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: false);
            }
        }
    }

    private static ManagedMinGitArtifact TestArtifact(byte[] archive) =>
        ManagedMinGitProvider.ReviewedArtifact with
        {
            Size = archive.LongLength,
            Sha256 = Sha256(archive)
        };

    private static ManagedMinGitInstallation CreateInstallation(string root)
    {
        var install = Path.Combine(root, "managed-git");
        var command = Write(install, "cmd", "git.exe", "git");
        var mingw = Write(install, "mingw64", "bin", "git.exe", "git");
        var shell = Write(install, "usr", "bin", "sh.exe", "shell");
        return new ManagedMinGitInstallation(
            "2.45.2.windows.1",
            install,
            command,
            mingw,
            shell);
    }

    private static byte[] CreateArchive(
        params (string Path, string Content, int? ExternalAttributes)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var specification in entries)
            {
                var entry = archive.CreateEntry(
                    specification.Path,
                    CompressionLevel.SmallestSize);
                if (specification.ExternalAttributes is { } attributes)
                {
                    entry.ExternalAttributes = attributes;
                }

                using var stream = entry.Open();
                var content = Encoding.UTF8.GetBytes(specification.Content);
                stream.Write(content);
            }
        }

        return output.ToArray();
    }

    private static string Write(string root, params string[] segmentsAndContent)
    {
        var segments = segmentsAndContent[..^1];
        var content = segmentsAndContent[^1];
        var path = Path.Combine([root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new StubHandler(response));

    private static HttpResponseMessage Download(byte[] bytes, string fileName)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentLength = bytes.LongLength;
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = $"\"{fileName}\""
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = response(request);
            result.RequestMessage ??= request;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingVersionVerifier : IManagedMinGitVersionVerifier
    {
        public List<ManagedMinGitInstallation> Installations { get; } = [];

        public Task VerifyAsync(
            ManagedMinGitInstallation installation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Installations.Add(installation);
            return Task.CompletedTask;
        }
    }

    private sealed class InlineProgress<T>(Action<T>? onReport = null) : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value)
        {
            Values.Add(value);
            onReport?.Invoke(value);
        }
    }
}

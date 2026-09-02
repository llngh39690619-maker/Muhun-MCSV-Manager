using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Core.Providers;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class AdoptiumRuntimeHealthTests
{
    private const string ReleaseName = "jdk-21.0.9+10";

    [Fact]
    public void JavacVersionProbe_DoesNotRequestJavaLocaleSettings()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var bin = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "managed jdk", "bin")).FullName;
        var javac = Path.Combine(bin, "javac.exe");

        var startInfo = AdoptiumRuntimeProvider.BuildJavaToolVersionStartInfo(
            javac,
            probeLocale: false);

        Assert.Equal(["-version"], startInfo.ArgumentList);
    }

    [Fact]
    public async Task InstallAsync_CorruptExistingRuntime_IsRemovedAndDownloadedOnce()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temporaryDirectory = new TemporaryDirectory();
        var runtimeRoot = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "runtimes")).FullName;
        var destination = Path.Combine(
            runtimeRoot,
            $"temurin-jre-21-{ReleaseName}");
        var existingJava = Path.Combine(destination, "bin", "java.exe");

        var archive = CreateRuntimeArchive();
        var archiveDownloads = 0;
        using var client = CreateAdoptiumClient(archive, () => archiveDownloads++);
        var javaProbes = new List<string>();
        var rejectInstalledRuntime = false;
        var provider = new AdoptiumRuntimeProvider(
            client,
            "MinecraftServerManager.Tests/1.0",
            (path, _) =>
            {
                javaProbes.Add(path);
                if (rejectInstalledRuntime
                    && string.Equals(path, existingJava, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "java -version passed, but -XshowSettings:locale failed");
                }

                return Task.FromResult(21);
            },
            (_, _) => throw new Xunit.Sdk.XunitException(
                "A JRE repair must not invoke the javac probe."));

        await provider.InstallAsync(21, runtimeRoot);
        Assert.True(File.Exists(Path.Combine(
            destination,
            ".muhun-mcsv-runtime.v1.json")));
        var staleMarker = Path.Combine(destination, "stale-runtime.txt");
        await File.WriteAllTextAsync(staleMarker, "must be removed");
        archiveDownloads = 0;
        javaProbes.Clear();
        rejectInstalledRuntime = true;

        var installed = await provider.InstallAsync(21, runtimeRoot);

        Assert.Equal(destination, installed.InstallDirectory);
        Assert.Equal(Path.Combine(destination, "bin", "java.exe"), installed.JavaExecutablePath);
        Assert.False(File.Exists(staleMarker));
        Assert.Equal("replacement runtime", await File.ReadAllTextAsync(
            Path.Combine(destination, "payload-marker.txt")));
        Assert.Equal(1, archiveDownloads);
        Assert.Equal(2, javaProbes.Count);
        Assert.Equal(existingJava, javaProbes[0]);
        Assert.Contains(
            Path.Combine(runtimeRoot, ".staging"),
            javaProbes[1],
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_CorruptIneligibleMarkerlessRuntime_IsPreservedAndNotDownloaded()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temporaryDirectory = new TemporaryDirectory();
        var runtimeRoot = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "runtimes")).FullName;
        var destination = Path.Combine(
            runtimeRoot,
            $"temurin-jre-21-{ReleaseName}");
        var bin = Directory.CreateDirectory(Path.Combine(destination, "bin")).FullName;
        await File.WriteAllTextAsync(Path.Combine(bin, "java.exe"), "unowned runtime");
        var keepMarker = Path.Combine(destination, "keep.txt");
        await File.WriteAllTextAsync(keepMarker, "must not be deleted");

        var archive = CreateRuntimeArchive();
        var archiveDownloads = 0;
        using var client = CreateAdoptiumClient(archive, () => archiveDownloads++);
        var provider = new AdoptiumRuntimeProvider(
            client,
            "MinecraftServerManager.Tests/1.0",
            (_, _) => throw new InvalidDataException("CLDR is corrupt"),
            (_, _) => throw new Xunit.Sdk.XunitException(
                "A JRE repair must not invoke the javac probe."));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.InstallAsync(21, runtimeRoot));

        Assert.Contains("所有權憑證", error.Message, StringComparison.Ordinal);
        Assert.Equal("must not be deleted", await File.ReadAllTextAsync(keepMarker));
        Assert.Equal(0, archiveDownloads);
    }

    [Fact]
    public async Task InstallAsync_CorruptEligibleLegacyRuntime_IsQuarantinedAndReinstalled()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temporaryDirectory = new TemporaryDirectory();
        var runtimeRoot = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "runtimes")).FullName;
        var destination = Path.Combine(
            runtimeRoot,
            $"temurin-jre-21-{ReleaseName}");
        var bin = Directory.CreateDirectory(Path.Combine(destination, "bin")).FullName;
        var existingJava = Path.Combine(bin, "java.exe");
        await File.WriteAllTextAsync(existingJava, "legacy corrupt runtime");
        await File.WriteAllTextAsync(
            Path.Combine(destination, "release"),
            """
            IMPLEMENTOR="Eclipse Adoptium"
            IMPLEMENTOR_VERSION="Temurin-21.0.9+10"
            JAVA_VERSION="21.0.9"
            IMAGE_TYPE="JRE"
            OS_NAME="Windows"
            OS_ARCH="x86_64"
            JVM_VARIANT="Hotspot"
            """);
        var keepMarker = Path.Combine(destination, "legacy-sentinel.txt");
        await File.WriteAllTextAsync(keepMarker, "preserved legacy bytes");
        var legacyIdentity = SafePath.GetExistingObjectIdentity(destination);

        var archive = CreateRuntimeArchive();
        var archiveDownloads = 0;
        using var client = CreateAdoptiumClient(archive, () => archiveDownloads++);
        var provider = new AdoptiumRuntimeProvider(
            client,
            "MinecraftServerManager.Tests/1.0",
            (path, _) => string.Equals(path, existingJava, StringComparison.OrdinalIgnoreCase)
                ? throw new InvalidDataException("CLDR is corrupt")
                : Task.FromResult(21),
            (_, _) => throw new Xunit.Sdk.XunitException(
                "A JRE repair must not invoke the javac probe."));

        var installed = await provider.InstallAsync(21, runtimeRoot);

        Assert.Equal(destination, installed.InstallDirectory);
        Assert.Equal(1, archiveDownloads);
        Assert.Equal(
            "replacement runtime",
            await File.ReadAllTextAsync(Path.Combine(destination, "payload-marker.txt")));
        Assert.True(File.Exists(Path.Combine(
            destination,
            ".muhun-mcsv-runtime.v1.json")));
        var quarantineRoot = Path.Combine(runtimeRoot, ".legacy-runtime-quarantine");
        var quarantined = Assert.Single(Directory.EnumerateDirectories(quarantineRoot));
        Assert.Equal(legacyIdentity, SafePath.GetExistingObjectIdentity(quarantined));
        Assert.Equal(
            "preserved legacy bytes",
            await File.ReadAllTextAsync(Path.Combine(quarantined, "legacy-sentinel.txt")));
    }

    [Fact]
    public async Task InstallAsync_TamperedOwnershipReceipt_FailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temporaryDirectory = new TemporaryDirectory();
        var runtimeRoot = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "runtimes")).FullName;
        var destination = Path.Combine(
            runtimeRoot,
            $"temurin-jre-21-{ReleaseName}");
        var archive = CreateRuntimeArchive();
        var archiveDownloads = 0;
        using var client = CreateAdoptiumClient(archive, () => archiveDownloads++);
        var probes = 0;
        var provider = new AdoptiumRuntimeProvider(
            client,
            "MinecraftServerManager.Tests/1.0",
            (_, _) =>
            {
                probes++;
                return Task.FromResult(21);
            },
            (_, _) => throw new Xunit.Sdk.XunitException(
                "A JRE operation must not invoke the javac probe."));

        await provider.InstallAsync(21, runtimeRoot);
        var payload = Path.Combine(destination, "payload-marker.txt");
        await File.WriteAllTextAsync(
            Path.Combine(destination, ".muhun-mcsv-runtime.v1.json"),
            "{\"SchemaVersion\":1,\"Provider\":\"not-adoptium\"}");
        archiveDownloads = 0;
        probes = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.InstallAsync(21, runtimeRoot));

        Assert.Equal("replacement runtime", await File.ReadAllTextAsync(payload));
        Assert.Equal(0, archiveDownloads);
        Assert.Equal(0, probes);
    }

    [Fact]
    public async Task InstallAsync_HealthyExistingRuntime_IsReusedWithoutDownloading()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temporaryDirectory = new TemporaryDirectory();
        var runtimeRoot = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "runtimes")).FullName;
        var destination = Path.Combine(
            runtimeRoot,
            $"temurin-jre-21-{ReleaseName}");
        var bin = Directory.CreateDirectory(Path.Combine(destination, "bin")).FullName;
        var java = Path.Combine(bin, "java.exe");
        await File.WriteAllTextAsync(java, "healthy runtime");
        var keepMarker = Path.Combine(destination, "keep.txt");
        await File.WriteAllTextAsync(keepMarker, "keep");

        var archive = CreateRuntimeArchive();
        var archiveDownloads = 0;
        var probes = 0;
        using var client = CreateAdoptiumClient(archive, () => archiveDownloads++);
        var provider = new AdoptiumRuntimeProvider(
            client,
            "MinecraftServerManager.Tests/1.0",
            (_, _) =>
            {
                probes++;
                return Task.FromResult(21);
            },
            (_, _) => throw new Xunit.Sdk.XunitException(
                "A JRE reuse must not invoke the javac probe."));

        var installed = await provider.InstallAsync(21, runtimeRoot);

        Assert.Equal(java, installed.JavaExecutablePath);
        Assert.Equal("keep", await File.ReadAllTextAsync(keepMarker));
        Assert.Equal(1, probes);
        Assert.Equal(0, archiveDownloads);
    }

    [Fact]
    public async Task InstallAsync_CancelledHealthProbe_DoesNotDeleteOrDownload()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temporaryDirectory = new TemporaryDirectory();
        var runtimeRoot = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "runtimes")).FullName;
        var destination = Path.Combine(
            runtimeRoot,
            $"temurin-jre-21-{ReleaseName}");
        var bin = Directory.CreateDirectory(Path.Combine(destination, "bin")).FullName;
        await File.WriteAllTextAsync(Path.Combine(bin, "java.exe"), "existing runtime");
        var keepMarker = Path.Combine(destination, "keep.txt");
        await File.WriteAllTextAsync(keepMarker, "keep after cancellation");

        var archive = CreateRuntimeArchive();
        var archiveDownloads = 0;
        using var cancellation = new CancellationTokenSource();
        using var client = CreateAdoptiumClient(archive, () => archiveDownloads++);
        var provider = new AdoptiumRuntimeProvider(
            client,
            "MinecraftServerManager.Tests/1.0",
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<int>(cancellation.Token);
            },
            (_, _) => throw new Xunit.Sdk.XunitException(
                "A JRE health probe must not invoke the javac probe."));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.InstallAsync(21, runtimeRoot, cancellationToken: cancellation.Token));

        Assert.Equal("keep after cancellation", await File.ReadAllTextAsync(keepMarker));
        Assert.Equal(0, archiveDownloads);
    }

    [Fact]
    public async Task DeleteInvalidRuntimeAsync_IdentityMismatchPreservesReplacement()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temporaryDirectory = new TemporaryDirectory();
        var runtimeRoot = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "runtimes")).FullName;
        var destination = Path.Combine(runtimeRoot, "temurin-jre-21-test");
        var movedOriginal = Path.Combine(runtimeRoot, "original-runtime");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "original.txt"), "original");
        var expectedIdentity = SafePath.GetExistingObjectIdentity(destination);

        Directory.Move(destination, movedOriginal);
        Directory.CreateDirectory(destination);
        var replacementMarker = Path.Combine(destination, "replacement.txt");
        await File.WriteAllTextAsync(replacementMarker, "replacement");

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
            AdoptiumRuntimeProvider.DeleteInvalidRuntimeAsync(
                runtimeRoot,
                destination,
                expectedIdentity,
                CancellationToken.None));

        Assert.Equal("replacement", await File.ReadAllTextAsync(replacementMarker));
        Assert.Equal(
            "original",
            await File.ReadAllTextAsync(Path.Combine(movedOriginal, "original.txt")));
    }

    [Fact]
    public async Task DeleteInvalidRuntimeAsync_PreCancelledTokenPreservesRuntime()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temporaryDirectory = new TemporaryDirectory();
        var runtimeRoot = Directory.CreateDirectory(
            Path.Combine(temporaryDirectory.Path, "runtimes")).FullName;
        var destination = Path.Combine(runtimeRoot, "temurin-jre-21-test");
        Directory.CreateDirectory(destination);
        var marker = Path.Combine(destination, "keep.txt");
        await File.WriteAllTextAsync(marker, "keep");
        var expectedIdentity = SafePath.GetExistingObjectIdentity(destination);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AdoptiumRuntimeProvider.DeleteInvalidRuntimeAsync(
                runtimeRoot,
                destination,
                expectedIdentity,
                cancellation.Token));

        Assert.Equal("keep", await File.ReadAllTextAsync(marker));
    }

    private static HttpClient CreateAdoptiumClient(byte[] archive, Action onArchiveDownload)
    {
        var sha256 = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        return new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.Host.Equals(
                    "api.adoptium.net",
                    StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse($$"""
                    [{
                      "release_name": "{{ReleaseName}}",
                      "vendor": "eclipse",
                      "binary": {
                        "image_type": "jre",
                        "package": {
                          "name": "OpenJDK21U-jre.zip",
                          "link": "https://github.com/adoptium/temurin21-binaries/releases/download/{{ReleaseName}}/OpenJDK21U-jre.zip",
                          "size": {{archive.Length}},
                          "checksum": "{{sha256}}"
                        }
                      }
                    }]
                    """);
            }

            onArchiveDownload();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            };
        }));
    }

    private static byte[] CreateRuntimeArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "jdk-21/bin/java.exe", "replacement executable");
            WriteEntry(archive, "jdk-21/payload-marker.txt", "replacement runtime");
        }

        return stream.ToArray();

        static void WriteEntry(ZipArchive archive, string name, string value)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(value);
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Updater;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductUpdateCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MuhunMCSV-ProductUpdateCoordinatorTests",
        Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    public ProductUpdateCoordinatorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task SignedFeed_DownloadsVerifiesSchedulesAndCreatesAuthenticatedHandoff()
    {
        var version = CreateFutureProductVersion();
        var layout = new ProductDataLayout(Path.Combine(_directory, "data-root"));
        layout.EnsureCreated();
        var fixture = CreateSignedFixture(version);
        var publicKeyPath = Path.Combine(_directory, "update-signing-public-key.json");
        await File.WriteAllBytesAsync(publicKeyPath, fixture.PublicKeyDocument);
        var manifestUri = new Uri("https://updates.example.com/stable/update-manifest.json");
        var packageUri = new Uri(fixture.Manifest.Package.Url);
        var transport = new FixtureTransport(new Dictionary<Uri, byte[]>
        {
            [manifestUri] = fixture.ManifestBytes,
            [new Uri(manifestUri.AbsoluteUri + ".sig")] = fixture.Signature,
            [packageUri] = fixture.PackageBytes,
        });
        var options = new ProductUpdateOptions
        {
            StableManifestUrl = manifestUri.AbsoluteUri,
            AllowedFeedHosts = ["updates.example.com"],
            PublicKeyDocumentPath = publicKeyPath,
        };
        var serviceOptions = new ProductServiceOptions
        {
            Port = 39050,
            DataRoot = layout.Root,
            Updates = options,
        };
        var launcher = new CapturingLauncher();
        using var coordinator = new ProductUpdateCoordinator(
            options,
            serviceOptions,
            layout,
            new ProductInstallationIdentityStore(layout),
            launcher,
            new FixedTimeProvider(_now),
            transport,
            updaterPathResolver: () => Path.Combine(_directory, "captured-updater.exe"));
        var changes = new List<ProductUpdateStatusChangedEventArgs>();
        coordinator.StatusChanged += (_, args) => changes.Add(args);

        var check = await coordinator.CheckAsync(ProductUpdateChannel.Stable, CancellationToken.None);
        var download = await coordinator.DownloadAsync(ProductUpdateChannel.Stable, CancellationToken.None);
        var schedule = await coordinator.ScheduleAsync(
            ProductUpdateChannel.Stable,
            _now,
            CancellationToken.None);
        var launched = await coordinator.LaunchDueActivationAsync(CancellationToken.None);

        Assert.True(check.Accepted);
        Assert.Equal(ProductUpdatePhase.Available, check.Status.Phase);
        Assert.True(download.Accepted);
        Assert.Equal(ProductUpdatePhase.Ready, download.Status.Phase);
        Assert.True(schedule.Accepted);
        Assert.True(launched);
        Assert.NotNull(launcher.RequestPath);
        var verifiedRequest = ProductUpdateActivationRequestProtocol.Verify(
            launcher.RequestPath!,
            new FixedTimeProvider(_now));
        Assert.Equal(version, verifiedRequest.Request.TargetVersion);
        Assert.Equal("stable", verifiedRequest.Request.Channel);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(fixture.ManifestBytes)),
            verifiedRequest.Request.ManifestSha256);
        Assert.Equal(fixture.Manifest.Package.Sha256, verifiedRequest.Request.PackageSha256);
        Assert.Equal("muhun.release", verifiedRequest.Request.SigningKeyId);
        Assert.True(File.Exists(Path.Combine(layout.Updates, "packages", version + ".zip")));
        var pendingPath = Path.Combine(
            layout.Updates,
            ProductUpdatePendingActivationProtocol.FileName);
        Assert.True(File.Exists(pendingPath));
        Assert.Equal(1, launcher.LaunchCount);

        // A restarted Service does not lose the handoff. Once the durable launch lease is stale,
        // it relaunches the exact same authenticated operation rather than creating a second one.
        var retryLauncher = new CapturingLauncher();
        using var restarted = new ProductUpdateCoordinator(
            options,
            serviceOptions,
            layout,
            new ProductInstallationIdentityStore(layout),
            retryLauncher,
            new FixedTimeProvider(_now.AddSeconds(16)),
            updaterPathResolver: () => Path.Combine(_directory, "captured-updater.exe"));
        Assert.Equal(ProductUpdatePhase.Applying, restarted.GetStatus(ProductUpdateChannel.Stable).Phase);
        Assert.True(await restarted.LaunchDueActivationAsync(CancellationToken.None));
        Assert.Equal(launcher.RequestPath, retryLauncher.RequestPath);
        Assert.Equal(1, retryLauncher.LaunchCount);

        ProductUpdateActivationReceiptProtocol.WriteRejected(
            verifiedRequest,
            "activation.preflight_rejected",
            new FixedTimeProvider(_now.AddSeconds(17)));
        Assert.False(await restarted.LaunchDueActivationAsync(CancellationToken.None));
        Assert.True(File.Exists(pendingPath));
        Assert.Equal(ProductUpdatePhase.Failed, restarted.GetStatus(ProductUpdateChannel.Stable).Phase);

        // Pending remains authoritative until the updater has durably published a terminal
        // journal acknowledgement. Only then are pending/request artifacts cleaned.
        ProductUpdateActivationReceiptProtocol.WriteTerminal(
            verifiedRequest,
            new ProductUpdateActivationJournal(
                1,
                verifiedRequest.Request.OperationId,
                ProductServiceApplication.ProductVersion,
                version,
                ProductUpdateActivationState.Committed,
                _now.AddSeconds(17)),
            new FixedTimeProvider(_now.AddSeconds(17)));
        Assert.True(await restarted.LaunchDueActivationAsync(CancellationToken.None));
        Assert.False(File.Exists(pendingPath));
        Assert.False(File.Exists(launcher.RequestPath));
        Assert.Equal(ProductUpdatePhase.Idle, restarted.GetStatus(ProductUpdateChannel.Stable).Phase);
        Assert.Contains(changes, change =>
            change.Previous.Phase == ProductUpdatePhase.Checking &&
            change.Current.Phase == ProductUpdatePhase.Available &&
            change.Current.AvailableVersion == version);
    }

    [Fact]
    public async Task RedirectResponse_IsRejectedWithoutFollowingLocation()
    {
        using var transport = new ProductUpdateHttpTransport(
            ["updates.example.com"],
            new DelegateHandler(request => new HttpResponseMessage(System.Net.HttpStatusCode.Redirect)
            {
                RequestMessage = request,
                Headers = { Location = new Uri("https://attacker.example/file") },
            }));

        await Assert.ThrowsAsync<HttpRequestException>(() => transport.GetBytesAsync(
            new Uri("https://updates.example.com/manifest.json"),
            1024,
            CancellationToken.None));
    }

    [Fact]
    public async Task OversizedBody_IsRejectedEvenWithoutContentLength()
    {
        using var transport = new ProductUpdateHttpTransport(
            ["updates.example.com"],
            new DelegateHandler(request => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new UnknownLengthContent(new byte[1025]),
            }));

        await Assert.ThrowsAsync<InvalidDataException>(() => transport.GetBytesAsync(
            new Uri("https://updates.example.com/manifest.json"),
            1024,
            CancellationToken.None));
    }

    [Fact]
    public async Task RestartedCoordinator_RejectsTamperedDurableScheduleBinding()
    {
        var version = CreateFutureProductVersion();
        var layout = new ProductDataLayout(Path.Combine(_directory, "restart-data-root"));
        layout.EnsureCreated();
        var fixture = CreateSignedFixture(version);
        var publicKeyPath = Path.Combine(_directory, "restart-update-signing-public-key.json");
        await File.WriteAllBytesAsync(publicKeyPath, fixture.PublicKeyDocument);
        var manifestUri = new Uri("https://updates.example.com/stable/restart-manifest.json");
        var transport = new FixtureTransport(new Dictionary<Uri, byte[]>
        {
            [manifestUri] = fixture.ManifestBytes,
            [new Uri(manifestUri.AbsoluteUri + ".sig")] = fixture.Signature,
            [new Uri(fixture.Manifest.Package.Url)] = fixture.PackageBytes,
        });
        var options = new ProductUpdateOptions
        {
            StableManifestUrl = manifestUri.AbsoluteUri,
            AllowedFeedHosts = ["updates.example.com"],
            PublicKeyDocumentPath = publicKeyPath,
        };
        var serviceOptions = new ProductServiceOptions
        {
            Port = 39050,
            DataRoot = layout.Root,
            Updates = options,
        };
        using (var first = new ProductUpdateCoordinator(
                   options,
                   serviceOptions,
                   layout,
                   new ProductInstallationIdentityStore(layout),
                   new CapturingLauncher(),
                   new FixedTimeProvider(_now),
                   transport,
                   updaterPathResolver: () => Path.Combine(_directory, "captured-updater.exe")))
        {
            Assert.True((await first.CheckAsync(ProductUpdateChannel.Stable, default)).Accepted);
            Assert.True((await first.DownloadAsync(ProductUpdateChannel.Stable, default)).Accepted);
            Assert.True((await first.ScheduleAsync(ProductUpdateChannel.Stable, _now, default)).Accepted);
        }

        var pendingPath = Path.Combine(layout.Updates, "pending-activation.v1.json");
        var pending = await File.ReadAllTextAsync(pendingPath);
        var expectedHash = Convert.ToHexString(SHA256.HashData(fixture.ManifestBytes));
        Assert.Contains(expectedHash, pending, StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            pendingPath,
            pending.Replace(expectedHash, new string('0', 64), StringComparison.Ordinal));
        var launcher = new CapturingLauncher();
        using var recovered = new ProductUpdateCoordinator(
            options,
            serviceOptions,
            layout,
            new ProductInstallationIdentityStore(layout),
            launcher,
            new FixedTimeProvider(_now),
            updaterPathResolver: () => Path.Combine(_directory, "captured-updater.exe"));

        Assert.Equal(ProductUpdatePhase.Scheduled, recovered.GetStatus(ProductUpdateChannel.Stable).Phase);
        Assert.False(await recovered.LaunchDueActivationAsync(default));
        Assert.Null(launcher.RequestPath);
        Assert.Equal(ProductUpdatePhase.Failed, recovered.GetStatus(ProductUpdateChannel.Stable).Phase);
        Assert.True(File.Exists(pendingPath));
    }

    [Fact]
    public void FormalServiceBase_ResolvesOnlyExactSiblingPayloadAndVersion()
    {
        var source = typeof(ProductUpdateCoordinator).Assembly.Location;
        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(source)
            .ProductVersion?.Split('+', 2)[0]
            ?? throw new InvalidOperationException("Service test assembly has no ProductVersion.");
        var versionRoot = Path.Combine(_directory, "formal-install", "versions", version);
        var serviceExecutable = Copy("service-win-x64", "Muhun MCSV Service.exe");
        var serviceBase = Path.GetDirectoryName(serviceExecutable)
            ?? throw new InvalidOperationException("Formal service directory is unavailable.");
        _ = Copy("gui-win-x64", "Muhun MCSV Manager.exe");
        var updater = Copy("updater-win-x64", "Muhun MCSV Updater.exe");

        Assert.Equal(
            updater,
            ProductUpdateCoordinator.ResolveSiblingProductExecutableFromBaseDirectory(
                serviceBase,
                "updater-win-x64",
                "Muhun MCSV Updater.exe"));
        Assert.Throws<InvalidDataException>(() =>
            ProductUpdateCoordinator.ResolveSiblingProductExecutableFromBaseDirectory(
                serviceBase,
                "gui-win-x64",
                "Muhun MCSV Updater.exe"));

        return;
        string Copy(string directory, string name)
        {
            var root = Path.Combine(versionRoot, directory);
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, name);
            File.Copy(source, path);
            return path;
        }
    }

    private SignedFixture CreateSignedFixture(string version)
    {
        var gui = "MZ gui"u8.ToArray();
        var service = "MZ service"u8.ToArray();
        var updater = "MZ updater"u8.ToArray();
        var package = CreatePackage(
            (ProductFormalUpdateManifestValidator.GuiEntryPoint, gui),
            (ProductFormalUpdateManifestValidator.ServiceEntryPoint, service),
            (ProductFormalUpdateManifestValidator.UpdaterEntryPoint, updater));
        using var rsa = RSA.Create(3072);
        var publicKey = rsa.ExportSubjectPublicKeyInfo();
        var keyDocument = new ProductUpdatePublicKeyDocument(
            1,
            "muhun.mcsv.manager",
            "muhun.release",
            "rsa-pss-sha256",
            "RSA",
            rsa.KeySize,
            Convert.ToHexString(SHA256.HashData(publicKey)),
            Convert.ToBase64String(publicKey),
            new string('a', 64),
            "CN=Muhun MCSV Manager Release Signing, O=Muhun",
            _now.AddDays(-1),
            _now.AddYears(1));
        var manifest = new ProductUpdateManifest(
            1,
            "muhun.mcsv.manager",
            version,
            "stable",
            "win-x64",
            _now.AddMinutes(-1),
            keyDocument.KeyId,
            "rsa-pss-sha256",
            new ProductUpdatePackage(
                "https://updates.example.com/stable/product.zip",
                package.Length,
                Convert.ToHexString(SHA256.HashData(package))),
            ProductFormalUpdateManifestValidator.GuiEntryPoint,
            [
                CreateFile(ProductFormalUpdateManifestValidator.GuiEntryPoint, gui),
                CreateFile(ProductFormalUpdateManifestValidator.ServiceEntryPoint, service),
                CreateFile(ProductFormalUpdateManifestValidator.UpdaterEntryPoint, updater),
            ]);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, jsonOptions);
        return new SignedFixture(
            manifest,
            manifestBytes,
            rsa.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
            package,
            JsonSerializer.SerializeToUtf8Bytes(keyDocument, jsonOptions));
    }

    private static string CreateFutureProductVersion()
    {
        var current = Version.Parse(ProductServiceApplication.ProductVersion.Split('-', 2)[0]);
        return $"{current.Major}.{current.Minor}.{current.Build + 1}";
    }

    private static ProductUpdateFile CreateFile(string path, byte[] content)
        => new(path, content.Length, Convert.ToHexString(SHA256.HashData(content)));

    private static byte[] CreatePackage(params (string Path, byte[] Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Fastest);
                using var destination = entry.Open();
                destination.Write(file.Content);
            }
        }

        return stream.ToArray();
    }

    private sealed record SignedFixture(
        ProductUpdateManifest Manifest,
        byte[] ManifestBytes,
        byte[] Signature,
        byte[] PackageBytes,
        byte[] PublicKeyDocument);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixtureTransport(IReadOnlyDictionary<Uri, byte[]> values)
        : IProductUpdateTransport
    {
        public Task<byte[]> GetBytesAsync(Uri uri, int maximumBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = values[uri];
            if (value.Length > maximumBytes)
            {
                throw new InvalidDataException();
            }

            return Task.FromResult(value.ToArray());
        }

        public async Task DownloadAsync(
            Uri uri,
            string destinationPath,
            long expectedBytes,
            Action<long>? reportProgress,
            CancellationToken cancellationToken)
        {
            var value = values[uri];
            Assert.Equal(expectedBytes, value.LongLength);
            await File.WriteAllBytesAsync(destinationPath, value, cancellationToken);
            reportProgress?.Invoke(value.LongLength);
        }
    }

    private sealed class CapturingLauncher : IProductUpdateActivationLauncher
    {
        public string? RequestPath { get; private set; }
        public int LaunchCount { get; private set; }

        public Task LaunchAsync(
            string updaterExecutablePath,
            string requestPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestPath = requestPath;
            LaunchCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(callback(request));
        }
    }

    private sealed class UnknownLengthContent(byte[] content) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context)
            => stream.WriteAsync(content).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}

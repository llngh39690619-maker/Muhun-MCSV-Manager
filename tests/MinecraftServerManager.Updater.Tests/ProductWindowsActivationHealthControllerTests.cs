using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using MinecraftServerManager.Contracts;

namespace MinecraftServerManager.Updater.Tests;

public sealed class ProductWindowsActivationHealthControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MuhunMCSV-ActivationHealthTests",
        Guid.NewGuid().ToString("N"));

    public ProductWindowsActivationHealthControllerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task FormalSiblingLayout_UsesAuthenticatedExactIdentityReadiness()
    {
        var (guiPath, version) = CreateFormalLayout();
        var (dataRoot, token, installationId) = CreateDataRoot();
        var platform = new CapturingPlatform();
        var handler = new DelegateHandler(request =>
        {
            Assert.Equal(
                $"http://127.0.0.1:39050{ProductApiProtocol.RestBasePath}/system/activation-ready",
                request.RequestUri?.AbsoluteUri);
            Assert.Equal(
                token,
                Assert.Single(request.Headers.GetValues(ProductLocalApiAuthentication.HeaderName)));
            return Json(HttpStatusCode.OK, new ProductActivationReadyResponse(
                "ready",
                "Muhun MCSV Manager",
                version,
                installationId,
                DateTimeOffset.UtcNow,
                Ready: true));
        });
        using var controller = new ProductWindowsActivationHealthController(
            39050,
            dataRoot,
            platform,
            handler);

        await controller.LaunchAsync(guiPath, CancellationToken.None);
        var healthy = await controller.WaitForHealthyAsync(
            version,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.True(healthy);
        Assert.EndsWith(
            Path.Combine("service-win-x64", "Muhun MCSV Service.exe"),
            platform.ServicePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(guiPath, platform.GuiPath);
        Assert.Equal(version, platform.Version);
    }

    [Fact]
    public async Task WrongInstallationIdentityOrDeadInteractiveGui_FailsClosed()
    {
        var (guiPath, version) = CreateFormalLayout();
        var (dataRoot, _, _) = CreateDataRoot();
        var platform = new CapturingPlatform();
        using var controller = new ProductWindowsActivationHealthController(
            39050,
            dataRoot,
            platform,
            new DelegateHandler(_ => Json(
                HttpStatusCode.OK,
                new ProductActivationReadyResponse(
                    "ready",
                    "Muhun MCSV Manager",
                    version,
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    Ready: true))));

        await controller.LaunchAsync(guiPath, CancellationToken.None);
        Assert.False(await controller.WaitForHealthyAsync(
            version,
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None));

        platform.Alive = false;
        Assert.False(await controller.WaitForHealthyAsync(
            version,
            TimeSpan.FromSeconds(1),
            CancellationToken.None));
    }

    [Fact]
    public void MissingSiblingAndMalformedCredential_AreRejectedBeforeServiceMutation()
    {
        var (guiPath, _) = CreateFormalLayout();
        var (dataRoot, _, _) = CreateDataRoot();
        File.WriteAllText(
            Path.Combine(dataRoot, ProductLocalApiAuthentication.TokenRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            "not-a-token");

        Assert.Throws<InvalidDataException>(() =>
            new ProductWindowsActivationHealthController(
                39050,
                dataRoot,
                new CapturingPlatform(),
                new DelegateHandler(_ => throw new InvalidOperationException())));

        _ = CreateDataRoot();
        var updater = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(guiPath))!,
            "updater-win-x64",
            "Muhun MCSV Updater.exe");
        File.Delete(updater);
        Assert.Throws<InvalidDataException>(() => ProductActivationPathPolicy.ResolveFormalLayout(guiPath));
    }

    [Fact]
    public void FormalManifestRequiresAllThreePayloadSubdirectories()
    {
        var baseManifest = ProductUpdateManifestTests.CreateManifest();
        var formal = baseManifest with
        {
            EntryPoint = ProductFormalUpdateManifestValidator.GuiEntryPoint,
            Files =
            [
                baseManifest.Files[0] with { Path = ProductFormalUpdateManifestValidator.GuiEntryPoint },
                baseManifest.Files[0] with { Path = ProductFormalUpdateManifestValidator.ServiceEntryPoint },
                baseManifest.Files[0] with { Path = ProductFormalUpdateManifestValidator.UpdaterEntryPoint },
            ],
        };

        ProductFormalUpdateManifestValidator.Validate(formal);
        Assert.Throws<InvalidDataException>(() =>
            ProductFormalUpdateManifestValidator.Validate(formal with
            {
                Files = formal.Files.Where(file =>
                    file.Path != ProductFormalUpdateManifestValidator.UpdaterEntryPoint).ToArray(),
            }));
        Assert.Throws<InvalidDataException>(() =>
            ProductFormalUpdateManifestValidator.Validate(formal with
            {
                Files = formal.Files.Append(
                    baseManifest.Files[0] with
                    {
                        Path = ProductInstalledVersionMetadataStore.FileName,
                    }).ToArray(),
            }));
    }

    [Fact]
    public void UpdaterBaseDirectory_ResolvesManagedInstallRootThroughFormalSubdirectory()
    {
        var updaterBase = Path.Combine(
            _root,
            "resolver-install",
            "versions",
            "1.2.3",
            "updater-win-x64");
        Directory.CreateDirectory(updaterBase);

        var resolved = ProductUpdaterApplication.ResolveInstallRootFromUpdaterBase(updaterBase);

        Assert.Equal(Path.Combine(_root, "resolver-install"), resolved);
        Assert.Throws<InvalidDataException>(() =>
            ProductUpdaterApplication.ResolveInstallRootFromUpdaterBase(
                Path.Combine(_root, "resolver-install", "versions", "1.2.3")));
    }

    [Fact]
    public void StableLauncherModes_AreStrictAndUnambiguous()
    {
        Assert.True(ProductGuiActivationBroker.IsBrokerRequest(
            ["--gui-activation-broker", "--install-root", _root]));
        Assert.True(ProductGuiActivationBroker.IsActivateCurrentRequest(
            ["--activate-current", "--install-root", _root]));
        Assert.False(ProductGuiActivationBroker.IsActivateCurrentRequest(
            ["--activate-current", "--install-root", _root, "unexpected"]));
        Assert.False(ProductGuiActivationBroker.IsActivateCurrentRequest(
            ["--gui-activation-broker", "--install-root", _root]));
    }

    [Fact]
    public void GuiLiveness_IsBoundToExactInteractiveProcessPath()
    {
        using var process = Process.GetCurrentProcess();
        var path = Environment.ProcessPath
            ?? throw new InvalidOperationException("Test process path is unavailable.");
        var platform = new ProductWindowsServicePlatform();
        var acknowledgement = new ProductGuiActivationAck(
            process.SessionId,
            process.Id,
            "1.0.0",
            new string('A', 64),
            Path.GetFullPath(path));

        Assert.True(platform.IsGuiActivationAlive(acknowledgement));
        Assert.False(platform.IsGuiActivationAlive(acknowledgement with
        {
            GuiExecutablePath = Path.Combine(_root, "wrong.exe"),
        }));
    }

    [Fact]
    public async Task GuiHelperAndBroker_CompleteBoundInitializedServiceReadyAck()
    {
        const string version = "1.0.0";
        var nonce = new string('A', 64);
        var pipeName = ProductGuiActivationAcknowledgement.PipePrefix + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous |
            PipeOptions.WriteThrough |
            PipeOptions.CurrentUserOnly |
            PipeOptions.FirstPipeInstance);
        using var process = Process.GetCurrentProcess();
        var broker = ProductGuiActivationBroker.WaitForExplicitGuiReadyAsync(
            server,
            process,
            version,
            nonce,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        await ProductGuiActivationAcknowledgement.SendReadyAsync(
            new ProductGuiActivationAcknowledgementRequest(pipeName, nonce, version),
            version,
            serviceReady: true,
            negotiatedApiVersion: ProductApiProtocol.CurrentVersion,
            CancellationToken.None);
        await broker;
    }

    [Fact]
    public async Task ActivationReadiness_AcceptsAckBeforeInteractiveStabilityCompletes()
    {
        const string version = "1.0.0";
        var nonce = new string('A', 64);
        var pipeName = ProductGuiActivationAcknowledgement.PipePrefix + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous |
            PipeOptions.WriteThrough |
            PipeOptions.CurrentUserOnly |
            PipeOptions.FirstPipeInstance);
        using var process = Process.GetCurrentProcess();
        var interactiveStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishInteractiveStability = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var readiness = ProductGuiActivationBroker.WaitForActivatedGuiReadinessAsync(
            server,
            process,
            version,
            nonce,
            TimeSpan.FromSeconds(5),
            async cancellationToken =>
            {
                interactiveStarted.SetResult();
                await finishInteractiveStability.Task.WaitAsync(cancellationToken);
            },
            CancellationToken.None);

        await interactiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await ProductGuiActivationAcknowledgement.SendReadyAsync(
                new ProductGuiActivationAcknowledgementRequest(pipeName, nonce, version),
                version,
                serviceReady: true,
                negotiatedApiVersion: ProductApiProtocol.CurrentVersion,
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(server.IsConnected);
        Assert.False(readiness.IsCompleted);
        finishInteractiveStability.SetResult();
        await readiness.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ActivationReadiness_ExplicitFailureCancelsAndObservesInteractiveVerifier()
    {
        const string version = "1.0.0";
        var expectedNonce = new string('A', 64);
        var pipeName = ProductGuiActivationAcknowledgement.PipePrefix + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous |
            PipeOptions.WriteThrough |
            PipeOptions.CurrentUserOnly |
            PipeOptions.FirstPipeInstance);
        using var process = Process.GetCurrentProcess();
        var interactiveCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var readiness = ProductGuiActivationBroker.WaitForActivatedGuiReadinessAsync(
            server,
            process,
            version,
            expectedNonce,
            TimeSpan.FromSeconds(5),
            async cancellationToken =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        interactiveCancelled.SetResult();
                    }
                }
            },
            CancellationToken.None);

        await ProductGuiActivationAcknowledgement.SendReadyAsync(
            new ProductGuiActivationAcknowledgementRequest(
                pipeName,
                new string('B', 64),
                version),
            version,
            serviceReady: true,
            negotiatedApiVersion: ProductApiProtocol.CurrentVersion,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => readiness.WaitAsync(TimeSpan.FromSeconds(5)));
        await interactiveCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GuiHelperCannotAckBeforeServiceReadyOrWithWrongNonce()
    {
        const string version = "1.0.0";
        var pipeName = ProductGuiActivationAcknowledgement.PipePrefix + Guid.NewGuid().ToString("N");
        var nonce = new string('A', 64);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProductGuiActivationAcknowledgement.SendReadyAsync(
                new ProductGuiActivationAcknowledgementRequest(pipeName, nonce, version),
                version,
                serviceReady: false,
                negotiatedApiVersion: ProductApiProtocol.CurrentVersion,
                CancellationToken.None));

        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous |
            PipeOptions.WriteThrough |
            PipeOptions.CurrentUserOnly |
            PipeOptions.FirstPipeInstance);
        using var process = Process.GetCurrentProcess();
        var broker = ProductGuiActivationBroker.WaitForExplicitGuiReadyAsync(
            server,
            process,
            version,
            nonce,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        await ProductGuiActivationAcknowledgement.SendReadyAsync(
            new ProductGuiActivationAcknowledgementRequest(pipeName, new string('B', 64), version),
            version,
            serviceReady: true,
            negotiatedApiVersion: ProductApiProtocol.CurrentVersion,
            CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker);
    }

    private (string GuiPath, string Version) CreateFormalLayout()
    {
        var source = typeof(ProductWindowsActivationHealthController).Assembly.Location;
        var version = FileVersionInfo.GetVersionInfo(source).ProductVersion?.Split('+', 2)[0]
            ?? throw new InvalidOperationException("Test updater assembly has no ProductVersion.");
        var versionRoot = Path.Combine(_root, "install", "versions", version);
        var gui = Copy("gui-win-x64", "Muhun MCSV Manager.exe");
        _ = Copy("service-win-x64", "Muhun MCSV Service.exe");
        _ = Copy("updater-win-x64", "Muhun MCSV Updater.exe");
        return (gui, version);

        string Copy(string directory, string name)
        {
            var targetRoot = Path.Combine(versionRoot, directory);
            Directory.CreateDirectory(targetRoot);
            var target = Path.Combine(targetRoot, name);
            File.Copy(source, target, overwrite: true);
            return target;
        }
    }

    private (string DataRoot, string Token, Guid InstallationId) CreateDataRoot()
    {
        var root = Path.Combine(_root, "data-root");
        Directory.CreateDirectory(Path.Combine(root, "secrets"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        File.WriteAllText(Path.Combine(root, ".muhun-mcsv-data-root"), "muhun.mcsv.manager:1\n");
        var token = new string('A', 64);
        var installationId = Guid.Parse("5d37a72c-0980-4df0-b85a-98ac5efabc85");
        File.WriteAllText(
            Path.Combine(root, "secrets", "service-rest-token.v1"),
            token + "\n");
        File.WriteAllText(
            Path.Combine(root, "data", "installation-id.v1"),
            installationId.ToString("D") + "\n");
        return (root, token, installationId);
    }

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T value)
        => new(status)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
                value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))),
        };

    private sealed class CapturingPlatform : IProductWindowsServicePlatform
    {
        public string? ServicePath { get; private set; }
        public string? GuiPath { get; private set; }
        public string? Version { get; private set; }
        public bool Alive { get; set; } = true;

        public Task ConfigureAndRestartAsync(
            string serviceExecutablePath,
            string dataRoot,
            CancellationToken cancellationToken)
        {
            ServicePath = serviceExecutablePath;
            return Task.CompletedTask;
        }

        public Task<ProductGuiActivationAck> RequestGuiActivationAsync(
            string guiExecutablePath,
            string expectedVersion,
            CancellationToken cancellationToken)
        {
            GuiPath = guiExecutablePath;
            Version = expectedVersion;
            return Task.FromResult(new ProductGuiActivationAck(
                1,
                1,
                expectedVersion,
                new string('A', 64),
                guiExecutablePath));
        }

        public bool IsGuiActivationAlive(ProductGuiActivationAck acknowledgement) => Alive;
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}

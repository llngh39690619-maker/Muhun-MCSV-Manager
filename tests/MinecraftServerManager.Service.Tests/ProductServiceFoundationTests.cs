using System.IO.Pipes;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Data;
using MinecraftServerManager.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductServiceFoundationTests
{
    [Fact]
    public async Task HostShutdownDeadline_CoversOrderedBoundedWindowsServiceTeardown()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        await using var application = ProductServiceApplication.Build(
        [
            $"--{ProductServiceOptions.SectionName}:DataRoot={layout.Root}",
            $"--{ProductServiceOptions.SectionName}:Port=39058",
            $"--{ProductServiceOptions.SectionName}:IpcPipeName=muhun.mcsv.shutdown.{Guid.NewGuid():N}",
        ]);

        var hostOptions = application.Services
            .GetRequiredService<IOptions<HostOptions>>()
            .Value;

        Assert.Equal(ProductServiceApplication.GracefulShutdownTimeout, hostOptions.ShutdownTimeout);
        Assert.True(hostOptions.ShutdownTimeout >= TimeSpan.FromMinutes(1));
        Assert.False(hostOptions.ServicesStopConcurrently);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(65536)]
    public void UnsafePort_IsRejected(int port)
    {
        var errors = ProductServiceOptionsValidator.Validate(new ProductServiceOptions { Port = port });

        Assert.Contains(errors, error => error.Contains("port", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RelativeDataRoot_IsRejected()
    {
        var errors = ProductServiceOptionsValidator.Validate(new ProductServiceOptions
        {
            Port = ProductServiceOptions.DefaultPort,
            DataRoot = "relative-data",
        });

        Assert.Contains(errors, error => error.Contains("absolute", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DriveRoot_IsRejected()
    {
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(AppContext.BaseDirectory))!;
        var errors = ProductServiceOptionsValidator.Validate(new ProductServiceOptions
        {
            Port = ProductServiceOptions.DefaultPort,
            DataRoot = driveRoot,
        });

        Assert.Contains(errors, error => error.Contains("drive root", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UncDataRoot_IsRejectedWithoutNetworkAccess()
    {
        var errors = ProductServiceOptionsValidator.Validate(new ProductServiceOptions
        {
            Port = ProductServiceOptions.DefaultPort,
            DataRoot = @"\\unreachable.invalid\share\muhun",
        });

        Assert.Contains(errors, error => error.Contains("UNC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExplicitDataRoot_ProducesSeparatedProductDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "muhun-mcsv-layout-test", Guid.NewGuid().ToString("N"));
        var layout = ProductDataLayout.FromOptions(new ProductServiceOptions
        {
            Port = ProductServiceOptions.DefaultPort,
            DataRoot = root,
        });

        Assert.Equal(Path.GetFullPath(root), layout.Root);
        Assert.Equal(Path.Combine(layout.Root, "servers"), layout.Servers);
        Assert.Equal(Path.Combine(layout.Root, "secrets"), layout.Secrets);
        Assert.Equal(Path.Combine(layout.Root, "operations"), layout.Operations);
        Assert.Equal(Path.Combine(layout.Root, "plugins"), layout.Plugins);
        Assert.Equal(Path.Combine(layout.Root, "updates"), layout.Updates);
    }

    [Fact]
    public void ServiceIdentityAndEndpointDefaults_AreStable()
    {
        Assert.Equal("MuhunMCSV", ProductServiceOptions.WindowsServiceName);
        Assert.Equal("Muhun MCSV Service", ProductServiceOptions.WindowsServiceDisplayName);
        Assert.Equal(39050, ProductServiceOptions.DefaultPort);
        Assert.Equal("muhun.mcsv.ipc.v1", new ProductServiceOptions().IpcPipeName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains/slash")]
    [InlineData("contains\\slash")]
    [InlineData("contains space")]
    public void UnsafeIpcPipeName_IsRejected(string pipeName)
    {
        var errors = ProductServiceOptionsValidator.Validate(new ProductServiceOptions
        {
            IpcPipeName = pipeName,
        });

        Assert.Contains(errors, error => error.Contains("pipe name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ServiceReadiness_RequiresFoundationAndIpcListener()
    {
        var state = new ProductServiceState(TimeProvider.System);
        state.Initialize(Guid.NewGuid());

        state.MarkFoundationReady();
        Assert.False(state.IsReady);

        state.MarkIpcReady();
        Assert.True(state.IsReady);

        state.MarkIpcNotReady();
        Assert.False(state.IsReady);

        state.MarkIpcReady();
        state.MarkFoundationNotReady();
        Assert.False(state.IsReady);
    }

    [Fact]
    public async Task IpcHost_ReadinessRecoversOnlyAfterListenerIsBoundAndClearsOnStop()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "muhun-mcsv-ipc-readiness-test",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new ProductDatabase(Path.Combine(root, "product.v1.db"));
            await database.InitializeAsync();
            var audit = new ProductLocalIpcAuditPolicy(
                new ProductSecurityAuditStore(database),
                TimeProvider.System);
            var state = new ProductServiceState(TimeProvider.System);
            state.Initialize(Guid.NewGuid());
            state.MarkFoundationReady();
            var attempts = 0;
            var pipeName = $"muhun.mcsv.readiness.{Guid.NewGuid():N}";
            using var service = new ProductIpcHostedService(
                (request, _) => Task.FromResult(new ProductIpcResponse(
                    ProductIpcProtocol.CurrentSchemaVersion,
                    request?.RequestId ?? Guid.Empty,
                    true,
                    null,
                    null)),
                audit,
                NullLogger<ProductIpcHostedService>.Instance,
                _ =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        throw new UnauthorizedAccessException("simulated listener collision");
                    }

                    return new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        ProductNamedPipeFactory.MaximumServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.WriteThrough);
                },
                new ProductIpcHostOptions(),
                state);

            await service.StartAsync(CancellationToken.None);
            try
            {
                Assert.False(state.IsReady);
                await WaitUntilAsync(
                    () => state.IsReady && Volatile.Read(ref attempts) >= 2,
                    TimeSpan.FromSeconds(3));
                Assert.True(state.IsReady);
            }
            finally
            {
                using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await service.StopAsync(stopDeadline.Token);
            }

            Assert.False(state.IsReady);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (!predicate())
        {
            await Task.Delay(25, deadline.Token);
        }
    }

    [Fact]
    public void InstallationIdentity_IsStableAcrossStoreInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), "muhun-mcsv-identity-test", Guid.NewGuid().ToString("N"));
        var layout = new ProductDataLayout(root);
        layout.EnsureCreated();

        var first = new ProductInstallationIdentityStore(layout).GetOrCreate();
        var second = new ProductInstallationIdentityStore(layout).GetOrCreate();

        Assert.NotEqual(Guid.Empty, first);
        Assert.Equal(first, second);
        Assert.Equal(first.ToString("D"), File.ReadAllText(Path.Combine(layout.Data, ProductInstallationIdentityStore.FileName)).Trim());
    }

    [Fact]
    public void CorruptInstallationIdentity_IsRejectedInsteadOfSilentlyReplaced()
    {
        var root = Path.Combine(Path.GetTempPath(), "muhun-mcsv-identity-test", Guid.NewGuid().ToString("N"));
        var layout = new ProductDataLayout(root);
        layout.EnsureCreated();
        File.WriteAllText(Path.Combine(layout.Data, ProductInstallationIdentityStore.FileName), "not-a-valid-installation-id");

        Assert.Throws<InvalidDataException>(() => new ProductInstallationIdentityStore(layout).GetOrCreate());
    }

    [Fact]
    public async Task ConcurrentStoreInstances_CommitOneStableIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "muhun-mcsv-identity-test", Guid.NewGuid().ToString("N"));
        var layout = new ProductDataLayout(root);
        layout.EnsureCreated();

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => new ProductInstallationIdentityStore(layout).GetOrCreate()))
            .ToArray();
        var identities = await Task.WhenAll(tasks);

        Assert.Single(identities.Distinct());
        Assert.Equal(identities[0].ToString("D"), File.ReadAllText(Path.Combine(layout.Data, ProductInstallationIdentityStore.FileName)).Trim());
    }
}

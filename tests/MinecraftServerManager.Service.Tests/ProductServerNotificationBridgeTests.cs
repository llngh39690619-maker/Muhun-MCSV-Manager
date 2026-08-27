using System.Diagnostics;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductServerNotificationBridgeTests
{
    [Fact]
    public async Task SlowDurableSink_DoesNotBlockCoreStateEventThread()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        var factory = new ProductServerTestProcessFactory();
        await using var manager = new ServerProcessManager(
            new ServerProcessManagerOptions
            {
                ResourceSamplingInterval = Timeout.InfiniteTimeSpan,
                GracefulStopTimeout = TimeSpan.FromSeconds(1),
                ForcedKillWaitTimeout = TimeSpan.FromSeconds(1),
                MonitorDrainTimeout = TimeSpan.FromSeconds(1),
            },
            factory);
        var sink = new BlockingSink();
        using var bridge = new ProductServerNotificationBridge(
            manager,
            registry,
            sink,
            TimeProvider.System,
            NullLogger<ProductServerNotificationBridge>.Instance);
        await bridge.StartAsync(CancellationToken.None);
        var instance = CreateCoreInstance();

        var stopwatch = Stopwatch.StartNew();
        await manager.StartAsync(instance);
        stopwatch.Stop();
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(sink.Release.Task.IsCompleted);

        sink.Release.TrySetResult();
        await manager.StopAsync(instance.Id);
        await bridge.StopAsync(CancellationToken.None);
        Assert.True(sink.CallCount >= 2);
    }

    [Fact]
    public void QueueAndDispatcherBatch_AreExplicitlyBounded()
    {
        Assert.Equal(512, ProductServerNotificationBridge.QueueCapacity);
        Assert.Equal(20, ProductNotificationDispatchHostedService.DispatchBatchSize);
    }

    private static ServerInstance CreateCoreInstance()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "muhun-notification-bridge-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "server.jar"), []);
        return new ServerInstance
        {
            Id = Guid.NewGuid(),
            Name = "Bridge Test",
            DirectoryPath = directory,
            JavaExecutablePath = "java.exe",
            ServerJarPath = "server.jar",
            CoreType = CoreType.Paper,
            MinimumMemoryMb = 1024,
            MaximumMemoryMb = 2048,
            ServerArguments = ["nogui"],
        };
    }

    private sealed class BlockingSink : IProductServerNotificationSink
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public async Task StoreAsync(
            ProductServerStateNotification notification,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
        }
    }
}

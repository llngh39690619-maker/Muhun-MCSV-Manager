using System.IO;
using System.Reflection;
using System.Windows.Threading;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;

namespace MinecraftServerManager.App.Tests;

public sealed class ResourceSampleDispatchPerformanceTests
{
    [Fact]
    public async Task TenThousandSampleBurst_KeepsOnlyLatestAndCommitsOnceOnRealSta()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        Directory.CreateDirectory(temporary.Path);
        MainWindowViewModel? main = null;

        try
        {
            WpfStaTestHost.Run(() =>
            {
                main = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
                var instanceId = Guid.NewGuid();
                var sessionId = Guid.NewGuid();
                var server = new ServerInstanceViewModel(
                    new ServerInstance
                    {
                        Id = instanceId,
                        Name = "Metrics Burst",
                        DirectoryPath = temporary.Path,
                        ServerJarPath = "server.jar"
                    },
                    static (_, _) => Task.CompletedTask);
                main.Servers.Add(server);

                var stateHandler = GetPrivateHandler("OnServerStateChanged");
                stateHandler.Invoke(main, [
                    null,
                    new ServerStateChangedEventArgs(
                        instanceId,
                        sessionId,
                        ServerState.Stopped,
                        ServerState.Starting)
                ]);
                stateHandler.Invoke(main, [
                    null,
                    new ServerStateChangedEventArgs(
                        instanceId,
                        sessionId,
                        ServerState.Starting,
                        ServerState.Running)
                ]);
                DrainDispatcher();
                Assert.Equal(ServerState.Running, server.State);

                var metricNotifications = 0;
                server.PropertyChanged += (_, eventArgs) =>
                {
                    if (eventArgs.PropertyName is nameof(server.CpuDisplay)
                        or nameof(server.MemoryDisplay)
                        or nameof(server.UptimeDisplay))
                    {
                        metricNotifications++;
                    }
                };
                var resourceHandler = GetPrivateHandler("OnResourceSampled");
                var timestamp = DateTimeOffset.UtcNow;

                Task.Run(() =>
                {
                    for (var index = 0; index < 10_000; index++)
                    {
                        resourceHandler.Invoke(main, [
                            null,
                            new ServerResourceSampledEventArgs(new ServerResourceSample(
                                instanceId,
                                sessionId,
                                timestamp.AddMilliseconds(index),
                                index % 100,
                                (index + 1L) * 1024 * 1024,
                                (index + 1L) * 1024 * 1024,
                                TimeSpan.FromSeconds(index)))
                        ]);
                    }
                }).GetAwaiter().GetResult();

                Assert.Equal(1, main.GetPendingResourceSampleCount(instanceId));
                Assert.True(main.HasScheduledResourceSampleDrain(instanceId));
                Assert.Equal(0, metricNotifications);

                Assert.True(PumpDispatcherUntil(
                    () => !main.HasScheduledResourceSampleDrain(instanceId),
                    TimeSpan.FromSeconds(5)));

                Assert.Equal("99.0%", server.CpuDisplay);
                Assert.Equal("9.77 GB", server.MemoryDisplay);
                Assert.Equal("02:46:39", server.UptimeDisplay);
                Assert.Equal(3, metricNotifications);
                Assert.Equal(0, main.GetPendingResourceSampleCount(instanceId));
            });
        }
        finally
        {
            if (main is not null)
            {
                await main.DisposeAsync();
            }
        }
    }

    private static MethodInfo GetPrivateHandler(string name)
        => typeof(MainWindowViewModel).GetMethod(
               name,
               BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new MissingMethodException(nameof(MainWindowViewModel), name);

    private static bool PumpDispatcherUntil(Func<bool> predicate, TimeSpan timeout)
    {
        if (predicate()) return true;

        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow + timeout;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        timer.Tick += (_, _) =>
        {
            if (!predicate() && DateTime.UtcNow < deadline) return;
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        return predicate();
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            () => frame.Continue = false,
            DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
    }
}

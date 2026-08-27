using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class PlayerPresenceDispatchPerformanceTests
{
    [Fact]
    public async Task FiveThousandJoinBurst_IsBoundedAndPublishesOneUiResetOnRealSta()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        Directory.CreateDirectory(temporary.Path);
        MainWindowViewModel? main = null;

        try
        {
            WpfStaTestHost.Run(() =>
            {
                main = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
                var dispatcher = Dispatcher.CurrentDispatcher;
                var instanceId = Guid.NewGuid();
                var sessionId = Guid.NewGuid();
                var server = new ServerInstanceViewModel(
                    new ServerInstance
                    {
                        Id = instanceId,
                        Name = "Presence Burst",
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

                var playerEvents = new List<NotifyCollectionChangedEventArgs>();
                var visibleEvents = new List<NotifyCollectionChangedEventArgs>();
                server.Players.CollectionChanged += (_, args) => playerEvents.Add(args);
                server.VisiblePlayers.CollectionChanged += (_, args) => visibleEvents.Add(args);
                var enqueue = GetPrivateHandler("EnqueuePresenceChange");

                Task.Run(() =>
                {
                    for (var index = 0; index < 5_000; index++)
                    {
                        enqueue.Invoke(main, [
                            dispatcher,
                            instanceId,
                            sessionId,
                            new PlayerPresenceChange($"P{index:D4}", true)
                        ]);
                    }
                }).GetAwaiter().GetResult();

                Assert.Equal(4_096, main.GetBufferedOnlinePlayerCount(instanceId));
                Assert.True(main.HasScheduledPresenceDrain(instanceId));
                Assert.Empty(server.Players);
                Assert.Empty(playerEvents);

                Assert.True(PumpDispatcherUntil(
                    () => !main.HasScheduledPresenceDrain(instanceId),
                    TimeSpan.FromSeconds(5)));

                Assert.Equal(4_096, server.Players.Count);
                Assert.Equal(4_096, server.VisiblePlayers.Count);
                Assert.Collection(playerEvents, change =>
                    Assert.Equal(NotifyCollectionChangedAction.Reset, change.Action));
                Assert.Collection(visibleEvents, change =>
                    Assert.Equal(NotifyCollectionChangedAction.Reset, change.Action));
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

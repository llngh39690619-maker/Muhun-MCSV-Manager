using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Runtime;

namespace MinecraftServerManager.App.Tests;

public sealed class ConsoleDispatchPerformanceTests
{
    [Fact]
    public async Task CancelledServiceWaitGeneration_DoesNotCommitCursorOrPlayerEffectsOnRealSta()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        Directory.CreateDirectory(temporary.Path);
        MainWindowViewModel? main = null;
        MainWindowProductServiceConsoleRealtimeTests.RealtimeServiceClient? client = null;
        Task initialization = Task.CompletedTask;
        using var releaseDispatcher = new ManualResetEventSlim();

        try
        {
            WpfStaTestHost.Run(() =>
            {
                client = new MainWindowProductServiceConsoleRealtimeTests.RealtimeServiceClient(
                    serverCount: 2)
                {
                    ServersRunning = true,
                };
                main = MainWindowViewModel.CreateServiceOwned(
                    new ApplicationPaths(temporary.Path),
                    client);
                initialization = main.InitializeAsync(allowInteractiveAutoImport: false);
            });
            await initialization.WaitAsync(TimeSpan.FromSeconds(3));

            var first = main!.Servers[0];
            var second = main.Servers[1];
            await client!.WaitUntilConsoleWaitStartsAsync(first.Id);

            var dispatcherBlocked = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            WpfStaTestHost.Run(() =>
            {
                _ = Application.Current!.Dispatcher.BeginInvoke(
                    () =>
                    {
                        dispatcherBlocked.TrySetResult();
                        _ = releaseDispatcher.Wait(TimeSpan.FromSeconds(5));
                    },
                    DispatcherPriority.Send);
            });
            await dispatcherBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

            client.Publish(
                first.Id,
                "[12:34:56] [Server thread/INFO]: Alice joined the game");
            await client.WaitUntilConsoleWaitResponseReturnsAsync(first.Id);
            await Task.Delay(50);

            // Simulate the UI selection changing after the pipe response arrived but before the
            // dispatcher was able to accept it. The cancelled generation must have no effects.
            main.SelectedServer = second;
            await client.WaitUntilConsoleWaitStartsAsync(second.Id);
            releaseDispatcher.Set();

            WpfStaTestHost.Run(() => main.SelectedServer = first);
            await WaitUntilAsync(
                () => client.ConsoleWaitRequests.Count(request => request.ServerId == first.Id) >= 2,
                TimeSpan.FromSeconds(2));

            var resumedRequest = client.ConsoleWaitRequests
                .Where(request => request.ServerId == first.Id)
                .Last();
            Assert.Equal(0, resumedRequest.AfterCursor);
            WpfStaTestHost.Run(() =>
            {
                Assert.DoesNotContain(first.ConsoleLines, line => line.Text.Contains("Alice"));
                Assert.DoesNotContain(first.Players, player => player.Name == "Alice");
            });
        }
        finally
        {
            releaseDispatcher.Set();
            if (main is not null)
            {
                await main.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task TenThousandLineBurst_HasBoundedBacklogAndChunkedUiCommitsOnRealSta()
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
                        Name = "Burst Server",
                        DirectoryPath = temporary.Path,
                        ServerJarPath = "server.jar",
                        SeparateDiagnosticOutput = false
                    },
                    static (_, _) => Task.CompletedTask);
                main.Servers.Add(server);

                var stateHandler = typeof(MainWindowViewModel).GetMethod(
                    "OnServerStateChanged",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(
                        nameof(MainWindowViewModel),
                        "OnServerStateChanged");
                stateHandler.Invoke(main, [
                    null,
                    new ServerStateChangedEventArgs(
                        instanceId,
                        sessionId,
                        ServerState.Stopped,
                        ServerState.Starting)
                ]);

                var consoleHandler = typeof(MainWindowViewModel).GetMethod(
                    "OnConsoleLineReceived",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(
                        nameof(MainWindowViewModel),
                        "OnConsoleLineReceived");
                var timestamp = DateTimeOffset.UtcNow;
                var consoleEvents = new List<NotifyCollectionChangedEventArgs>();
                var diagnosticEvents = new List<NotifyCollectionChangedEventArgs>();
                var consoleCountsAtCommit = new List<int>();
                server.ConsoleLines.CollectionChanged += (_, eventArgs) =>
                {
                    consoleEvents.Add(eventArgs);
                    consoleCountsAtCommit.Add(server.ConsoleLines.Count);
                };
                server.DiagnosticLines.CollectionChanged += (_, eventArgs) => diagnosticEvents.Add(eventArgs);

                var burstStopwatch = Stopwatch.StartNew();
                Task.Run(() =>
                {
                    for (var index = 0; index < 10_000; index++)
                    {
                        consoleHandler.Invoke(main, [
                            null,
                            new ConsoleLineReceivedEventArgs(
                                instanceId,
                                sessionId,
                                new ConsoleLine(
                                    timestamp.AddMilliseconds(index),
                                    $"line-{index}",
                                    ConsoleStream.StandardOutput)
                                {
                                    SessionId = sessionId,
                                    Severity = index % 2 == 0
                                        ? ConsoleLineSeverity.Information
                                        : ConsoleLineSeverity.Warning
                                })
                        ]);
                    }
                }).GetAwaiter().GetResult();

                Assert.Equal(
                    MainWindowViewModel.MaximumPendingConsoleLinesPerInstance,
                    main.GetPendingConsoleLineCount(instanceId));
                Assert.True(main.HasScheduledConsoleDrain(instanceId));
                Assert.Empty(consoleEvents);
                Assert.Empty(diagnosticEvents);

                Assert.True(PumpDispatcherUntil(
                    () => !main.HasScheduledConsoleDrain(instanceId),
                    TimeSpan.FromSeconds(5)));

                Assert.Equal(2_000, server.ConsoleLines.Count);
                Assert.Equal(1_000, server.DiagnosticLines.Count);
                Assert.Equal("line-8000", server.ConsoleLines[0].Text);
                Assert.Equal("line-9999", server.DiagnosticLines[^1].Text);
                Assert.True(
                    consoleEvents.Count >= 20,
                    $"Expected a 4,096-line catch-up to use small UI batches, but observed {consoleEvents.Count} commit(s).");
                Assert.Equal(consoleEvents.Count, diagnosticEvents.Count);
                Assert.InRange(consoleCountsAtCommit[0], 1, 200);
                Assert.All(consoleEvents.Concat(diagnosticEvents), eventArgs =>
                    Assert.Equal(NotifyCollectionChangedAction.Reset, eventArgs.Action));
                Assert.Equal(0, main.GetPendingConsoleLineCount(instanceId));
                burstStopwatch.Stop();
                Assert.True(
                    burstStopwatch.Elapsed < TimeSpan.FromSeconds(5),
                    $"10,000-line UI burst took {burstStopwatch.Elapsed}.");
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

    [Fact]
    public async Task ProjectionReset_DoesNotForceScrollToEndAfterUserLeavesTailOnRealSta()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        Directory.CreateDirectory(temporary.Path);
        MainWindowViewModel? main = null;

        try
        {
            WpfStaTestHost.Run(() =>
            {
                main = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
                var server = new ServerInstanceViewModel(
                    new ServerInstance
                    {
                        Id = Guid.NewGuid(),
                        Name = "Scroll Server",
                        DirectoryPath = temporary.Path,
                        ServerJarPath = "server.jar",
                        SeparateDiagnosticOutput = false
                    },
                    static (_, _) => Task.CompletedTask);
                server.AppendConsoleBatch(Enumerable.Range(0, 2_000).Select(index =>
                    new ConsoleLine(
                        DateTimeOffset.UtcNow.AddMilliseconds(index),
                        $"initial-{index}",
                        ConsoleStream.StandardOutput)
                    {
                        Severity = ConsoleLineSeverity.Information
                    }));
                main.Servers.Add(server);
                main.SelectedServer = server;

                var window = new MainWindow(main)
                {
                    Width = 1180,
                    Height = 760,
                    Left = -10_000,
                    Top = -10_000,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                window.Show();
                try
                {
                    ListBox? consoleList = null;
                    ScrollViewer? scrollViewer = null;
                    Assert.True(PumpDispatcherUntil(
                        () =>
                        {
                            consoleList ??= VisualDescendants<ListBox>(window)
                                .FirstOrDefault(list => ReferenceEquals(list.ItemsSource, server.ConsoleLines));
                            if (consoleList is null) return false;
                            scrollViewer ??= VisualDescendants<ScrollViewer>(consoleList).FirstOrDefault();
                            return scrollViewer is { ScrollableHeight: > 0 }
                                && scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset <= 2;
                        },
                        TimeSpan.FromSeconds(5)));

                    Assert.NotNull(scrollViewer);
                    scrollViewer.ScrollToTop();
                    Assert.True(PumpDispatcherUntil(
                        () => scrollViewer.VerticalOffset <= 1,
                        TimeSpan.FromSeconds(2)));

                    server.AppendConsoleBatch(Enumerable.Range(2_000, 100).Select(index =>
                        new ConsoleLine(
                            DateTimeOffset.UtcNow.AddMilliseconds(index),
                            $"next-{index}",
                            ConsoleStream.StandardOutput)
                        {
                            Severity = ConsoleLineSeverity.Information
                        }));
                    PumpDispatcherFor(TimeSpan.FromMilliseconds(300));

                    Assert.True(scrollViewer.VerticalOffset <= 1);
                    Assert.True(scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset > 2);
                }
                finally
                {
                    window.PrepareForApplicationShutdown();
                    window.Close();
                }
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

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!predicate())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("The expected Service console transition was not observed.");
            }

            await Task.Delay(10);
        }
    }

    private static void PumpDispatcherFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in VisualDescendants<T>(child)) yield return nested;
        }
    }
}

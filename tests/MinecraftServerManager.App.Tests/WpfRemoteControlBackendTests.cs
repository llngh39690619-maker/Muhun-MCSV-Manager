using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.App.Tests;

public sealed class WpfRemoteControlBackendTests
{
    [Fact]
    public async Task Dashboard_MapsBoundedSafeDataWithoutLocalPaths()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        MainWindowViewModel? main = null;
        try
        {
            WpfStaTestHost.Run(() =>
            {
                main = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
                var server = CreateServer(Path.Combine(temporary.Path, "private-world"));
                server.SetState(ServerState.Running);
                server.UpdateMetrics(12.5, 768L * 1024 * 1024, TimeSpan.FromMinutes(3));
                server.UpdatePlayerPresence("Alex", isOnline: true);
                main.Servers.Add(server);
                var backend = new WpfRemoteControlBackend(main, Dispatcher.CurrentDispatcher);

                var dashboard = backend.GetDashboardAsync(CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();

                var summary = Assert.Single(dashboard.Servers);
                Assert.Equal(server.Id.ToString("N"), summary.Id);
                Assert.Equal(RemoteServerState.Running, summary.State);
                Assert.Equal(1, summary.PlayerCount);
                Assert.Equal(12.5, summary.CpuPercent);
                Assert.Equal(768L * 1024 * 1024, summary.MemoryBytes);
                Assert.DoesNotContain(temporary.Path, JsonSerializer.Serialize(dashboard), StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            if (main is not null) await main.DisposeAsync();
        }
    }

    [Fact]
    public async Task Console_UsesStableSequenceAndKeepsDiagnosticStreamSeparate()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        MainWindowViewModel? main = null;
        try
        {
            WpfStaTestHost.Run(() =>
            {
                main = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
                var server = CreateServer(temporary.Path);
                server.AppendConsoleBatch(
                [
                    new ConsoleLine(DateTimeOffset.UtcNow, "ready")
                    {
                        Severity = ConsoleLineSeverity.Information
                    },
                    new ConsoleLine(DateTimeOffset.UtcNow.AddMilliseconds(1), "warning")
                    {
                        Severity = ConsoleLineSeverity.Warning
                    }
                ]);
                main.Servers.Add(server);
                var backend = new WpfRemoteControlBackend(main, Dispatcher.CurrentDispatcher);

                var unsplitOrdinary = backend.GetConsoleAsync(
                        server.Id.ToString("N"),
                        new RemoteConsoleQuery(RemoteConsoleStream.Ordinary, null, 10),
                        CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                Assert.Equal(2, Assert.IsType<RemoteConsolePageDto>(unsplitOrdinary).Lines.Count);

                server.SeparateDiagnosticOutput = true;

                var all = backend.GetConsoleAsync(
                        server.Id.ToString("N"),
                        new RemoteConsoleQuery(RemoteConsoleStream.All, null, 10),
                        CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
                var diagnostic = backend.GetConsoleAsync(
                        server.Id.ToString("N"),
                        new RemoteConsoleQuery(RemoteConsoleStream.Diagnostic, null, 10),
                        CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();

                Assert.NotNull(all);
                Assert.Equal([1L, 2L], all.Lines.Select(line => line.Sequence).ToArray());
                var warning = Assert.Single(Assert.IsType<RemoteConsolePageDto>(diagnostic).Lines);
                Assert.Equal(RemoteConsoleStream.Diagnostic, warning.Stream);
                Assert.Equal(RemoteConsoleSeverity.Warning, warning.Severity);
            });
        }
        finally
        {
            if (main is not null) await main.DisposeAsync();
        }
    }

    [Fact]
    public async Task Mutation_RejectsNonOpaqueServerIdentifier()
    {
        using var temporary = new AppearanceThemeServiceTests.TestDirectory();
        MainWindowViewModel? main = null;
        try
        {
            WpfStaTestHost.Run(() =>
            {
                main = new MainWindowViewModel(new ApplicationPaths(temporary.Path));
                var backend = new WpfRemoteControlBackend(main, Dispatcher.CurrentDispatcher);

                var result = backend.StartServerAsync("C:\\private\\world", CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();

                Assert.False(result.Accepted);
                Assert.DoesNotContain("private", result.Message, StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            if (main is not null) await main.DisposeAsync();
        }
    }

    private static ServerInstanceViewModel CreateServer(string directoryPath)
        => new(
            new ServerInstance
            {
                Id = Guid.NewGuid(),
                Name = "Remote Test",
                DirectoryPath = directoryPath,
                ServerJarPath = Path.Combine(directoryPath, "server.jar"),
                CoreType = CoreType.Paper,
                MinecraftVersion = "26.2",
                JavaMajorVersion = 25,
                Port = 25565
            },
            (_, _) => Task.CompletedTask);
}

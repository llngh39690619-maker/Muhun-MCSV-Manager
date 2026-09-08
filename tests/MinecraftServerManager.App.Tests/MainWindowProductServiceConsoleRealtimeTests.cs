using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Channels;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Client;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class MainWindowProductServiceConsoleRealtimeTests
{
    [Fact]
    public async Task ConsoleCursor_CommitsOnlyAtTheUiAcceptanceBoundary()
    {
        var client = new RealtimeServiceClient(serverCount: 1);
        await using var controller = new ProductServiceDesktopController(client);
        var serverId = client.ServerIds[0];

        var waitTask = controller.WaitForConsoleAsync(serverId, afterCursor: 0);
        await client.WaitUntilConsoleWaitStartsAsync(serverId);
        client.Publish(serverId, "wait-page");
        var waitPage = await waitTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, controller.GetAcceptedConsoleCursor(serverId));
        Assert.True(controller.TryAcceptConsolePage(waitPage));
        Assert.Equal(1, controller.GetAcceptedConsoleCursor(serverId));
        Assert.False(controller.TryAcceptConsolePage(waitPage));

        client.AppendForLegacyPolling(serverId, "legacy-page");
        var legacySnapshot = await controller.RefreshFocusedAsync(serverId);
        var legacyPage = Assert.Single(legacySnapshot.Servers).Console;

        Assert.Equal(1, legacyPage.RequestedAfterCursor);
        Assert.Equal(1, controller.GetAcceptedConsoleCursor(serverId));
        Assert.True(controller.TryAcceptConsolePage(legacyPage));
        Assert.Equal(2, controller.GetAcceptedConsoleCursor(serverId));
    }

    [Fact]
    public async Task Api111Wait_NewLineAppearsWellBeforeTheStatusPollingInterval()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var client = new RealtimeServiceClient(serverCount: 1);
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);

        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var server = Assert.Single(viewModel.Servers);
        await client.WaitUntilConsoleWaitStartsAsync(server.Id);

        var started = Stopwatch.GetTimestamp();
        client.Publish(server.Id, "realtime-line");
        await WaitUntilAsync(
            () => server.ConsoleLines.Any(line => line.Text == "realtime-line"),
            TimeSpan.FromMilliseconds(1_250));

        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(1_250),
            $"The Service console line took {elapsed.TotalMilliseconds:N0} ms to reach the GUI.");
        Assert.Equal(0, client.ReadConsoleCallCount);
    }

    [Fact]
    public async Task Api111StatusPolling_DoesNotReadConsole()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var client = new RealtimeServiceClient(serverCount: 1);
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);

        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        await client.WaitUntilConsoleWaitStartsAsync(client.ServerIds[0]);
        await client.SecondStatusPoll.Task.WaitAsync(TimeSpan.FromSeconds(3.5));

        Assert.True(client.StatusListCallCount >= 2);
        Assert.Equal(0, client.ReadConsoleCallCount);
        Assert.True(client.WaitCallCount >= 1);
    }

    [Fact]
    public async Task SelectionAndWorkspaceChanges_CancelThePreviousConsoleWait()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var client = new RealtimeServiceClient(serverCount: 2);
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);

        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var first = viewModel.Servers[0];
        var second = viewModel.Servers[1];
        await client.WaitUntilConsoleWaitStartsAsync(first.Id);

        viewModel.SelectedServer = second;

        await client.WaitUntilConsoleWaitIsCancelledAsync(first.Id);
        await client.WaitUntilConsoleWaitStartsAsync(second.Id);

        viewModel.ShowClientWorkspaceCommand.Execute(null);

        await client.WaitUntilConsoleWaitIsCancelledAsync(second.Id);
        Assert.True(viewModel.IsClientWorkspace);
        Assert.Equal(second.Id, viewModel.SelectedServer?.Id);
    }

    [Fact]
    public async Task ImmediateEmptyWaits_AreBackedOffInsteadOfHotSpinning()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var client = new RealtimeServiceClient(serverCount: 1)
        {
            ReturnImmediateEmptyWaits = true,
        };
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);

        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        await client.WaitUntilConsoleWaitStartsAsync(client.ServerIds[0]);
        await Task.Delay(TimeSpan.FromMilliseconds(450));

        Assert.InRange(client.WaitCallCount, 1, 5);
        Assert.Equal(0, client.ReadConsoleCallCount);
    }

    [Fact]
    public async Task Shutdown_CancelsTheActiveConsoleWaitBeforeDisposingTheClient()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var client = new RealtimeServiceClient(serverCount: 1);
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);

        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var serverId = client.ServerIds[0];
        await client.WaitUntilConsoleWaitStartsAsync(serverId);

        await viewModel.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(3));

        await client.WaitUntilConsoleWaitIsCancelledAsync(serverId);
        Assert.True(client.IsDisposed);
    }

    [Fact]
    public async Task Api111DowngradeToLegacyPolling_ContinuesFromTheAcceptedCursor()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var client = new RealtimeServiceClient(serverCount: 1);
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);

        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var server = Assert.Single(viewModel.Servers);
        await client.WaitUntilConsoleWaitStartsAsync(server.Id);

        client.Publish(server.Id, "wait-1");
        await WaitUntilAsync(
            () => server.ConsoleLines.Any(line => line.Text == "wait-1"),
            TimeSpan.FromSeconds(1));
        client.Publish(server.Id, "wait-2");
        await WaitUntilAsync(
            () => server.ConsoleLines.Any(line => line.Text == "wait-2") && client.WaitCallCount >= 3,
            TimeSpan.FromSeconds(1));

        client.MaximumApiVersion = new ProductApiVersion(1, 10);
        await ApplyServicePollAsync(viewModel, consoleServerId: null);
        Assert.False(viewModel.SupportsProductServiceConsoleWait);
        client.AppendForLegacyPolling(server.Id, "legacy-3");
        await ApplyServicePollAsync(viewModel, server.Id);

        Assert.Equal(2, client.ReadConsoleRequests.First().AfterCursor);
        Assert.Equal(
            ["wait-1", "wait-2", "legacy-3"],
            server.ConsoleLines
                .Where(line => line.Text.StartsWith("wait-", StringComparison.Ordinal) ||
                               line.Text.StartsWith("legacy-", StringComparison.Ordinal))
                .Select(line => line.Text)
                .ToArray());
    }

    [Fact]
    public async Task HistoryGap_ReplacesEventDerivedPresenceWithAuthoritativePlayers()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        var client = new RealtimeServiceClient(serverCount: 1)
        {
            ServersRunning = true,
            AuthoritativeOnlinePlayers = ["Bob"],
        };
        await using var viewModel = MainWindowViewModel.CreateServiceOwned(paths, client);

        await viewModel.InitializeAsync(allowInteractiveAutoImport: false);
        var server = Assert.Single(viewModel.Servers);
        await client.WaitUntilConsoleWaitStartsAsync(server.Id);

        client.Publish(
            server.Id,
            "[12:34:56] [Server thread/INFO]: Alice joined the game");
        await WaitUntilAsync(
            () => server.Players.Any(player => player.Name == "Alice" && player.IsOnline),
            TimeSpan.FromSeconds(1));

        client.PublishHistoryGap(server.Id);
        await client.PlayersListed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () =>
            {
                try
                {
                    var players = server.Players.ToArray();
                    return players.Any(player => player.Name == "Bob" && player.IsOnline) &&
                           players.All(player => player.Name != "Alice" || !player.IsOnline);
                }
                catch (InvalidOperationException)
                {
                    // The dispatcher-free test host can observe the brief replacement window.
                    // Retry against a fresh snapshot instead of enumerating a mutating collection.
                    return false;
                }
            },
            TimeSpan.FromSeconds(1));

        Assert.Equal(1, client.PlayerListCallCount);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!predicate())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("The expected realtime console state was not observed.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task ApplyServicePollAsync(
        MainWindowViewModel viewModel,
        Guid? consoleServerId)
    {
        var controller = (ProductServiceDesktopController?)typeof(MainWindowViewModel)
            .GetField("_productServiceController", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(viewModel)
            ?? throw new MissingFieldException(
                nameof(MainWindowViewModel),
                "_productServiceController");
        var snapshot = await controller.RefreshFocusedAsync(consoleServerId);
        var applySnapshot = typeof(MainWindowViewModel).GetMethod(
            "ApplyProductServiceSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(MainWindowViewModel),
                "ApplyProductServiceSnapshot");
        applySnapshot.Invoke(viewModel, [snapshot]);
    }

    internal sealed class RealtimeServiceClient : IProductServiceClient
    {
        private readonly Guid[] _serverIds;
        private readonly IReadOnlyDictionary<Guid, Guid> _sessionIds;
        private readonly IReadOnlyDictionary<Guid, Channel<ConsoleWaitResponse>> _consoleResponses;
        private readonly IReadOnlyDictionary<Guid, TaskCompletionSource> _waitStarted;
        private readonly IReadOnlyDictionary<Guid, TaskCompletionSource> _waitCancelled;
        private readonly IReadOnlyDictionary<Guid, TaskCompletionSource> _waitResponseReturned;
        private readonly ConcurrentDictionary<Guid, long> _nextCursors = [];
        private readonly ConcurrentDictionary<Guid, List<ProductConsoleEntry>> _journals = [];
        private readonly object _journalSync = new();
        private readonly Guid _serviceInstanceId = Guid.NewGuid();
        private int _maximumApiMinor = ProductApiProtocol.ConsoleWaitVersion.Minor;
        private int _statusListCallCount;
        private int _readConsoleCallCount;
        private int _waitCallCount;
        private int _playerListCallCount;
        private int _disposed;

        public RealtimeServiceClient(int serverCount)
        {
            _serverIds = Enumerable.Range(0, serverCount).Select(_ => Guid.NewGuid()).ToArray();
            _sessionIds = _serverIds.ToDictionary(id => id, _ => Guid.NewGuid());
            _consoleResponses = _serverIds.ToDictionary(
                id => id,
                _ => Channel.CreateUnbounded<ConsoleWaitResponse>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                }));
            _waitStarted = _serverIds.ToDictionary(
                id => id,
                _ => NewSignal());
            _waitCancelled = _serverIds.ToDictionary(
                id => id,
                _ => NewSignal());
            _waitResponseReturned = _serverIds.ToDictionary(
                id => id,
                _ => NewSignal());
            foreach (var id in _serverIds)
            {
                _journals[id] = [];
            }
        }

        public IReadOnlyList<Guid> ServerIds => _serverIds;

        public bool ReturnImmediateEmptyWaits { get; init; }

        public bool ServersRunning { get; init; }

        public IReadOnlyList<string> AuthoritativeOnlinePlayers { get; init; } = [];

        public ProductApiVersion MaximumApiVersion
        {
            get => new(1, Volatile.Read(ref _maximumApiMinor));
            set
            {
                if (value.Major != 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                Volatile.Write(ref _maximumApiMinor, value.Minor);
            }
        }

        public int StatusListCallCount => Volatile.Read(ref _statusListCallCount);

        public int ReadConsoleCallCount => Volatile.Read(ref _readConsoleCallCount);

        public int WaitCallCount => Volatile.Read(ref _waitCallCount);

        public int PlayerListCallCount => Volatile.Read(ref _playerListCallCount);

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public TaskCompletionSource SecondStatusPoll { get; } = NewSignal();

        public TaskCompletionSource PlayersListed { get; } = NewSignal();

        public ConcurrentQueue<(Guid ServerId, long AfterCursor)> ReadConsoleRequests { get; } = [];

        public ConcurrentQueue<(Guid ServerId, long AfterCursor)> ConsoleWaitRequests { get; } = [];

        public Task WaitUntilConsoleWaitStartsAsync(Guid serverId)
            => _waitStarted[serverId].Task.WaitAsync(TimeSpan.FromSeconds(2));

        public Task WaitUntilConsoleWaitIsCancelledAsync(Guid serverId)
            => _waitCancelled[serverId].Task.WaitAsync(TimeSpan.FromSeconds(2));

        public Task WaitUntilConsoleWaitResponseReturnsAsync(Guid serverId)
            => _waitResponseReturned[serverId].Task.WaitAsync(TimeSpan.FromSeconds(2));

        public void Publish(Guid serverId, string text)
        {
            var entry = AppendToJournal(serverId, text);
            if (!_consoleResponses[serverId].Writer.TryWrite(new ConsoleWaitResponse(
                    OldestAvailableCursor: 1,
                    NextCursor: entry.Cursor,
                    HistoryGap: false,
                    Entries: [entry])))
            {
                throw new InvalidOperationException("The fake console stream rejected a test line.");
            }
        }

        public void AppendForLegacyPolling(Guid serverId, string text)
            => _ = AppendToJournal(serverId, text);

        public void PublishHistoryGap(Guid serverId)
        {
            var nextCursor = _nextCursors.AddOrUpdate(
                serverId,
                1,
                static (_, current) => current + 1);
            if (!_consoleResponses[serverId].Writer.TryWrite(new ConsoleWaitResponse(
                    OldestAvailableCursor: nextCursor + 1,
                    NextCursor: nextCursor,
                    HistoryGap: true,
                    Entries: [])))
            {
                throw new InvalidOperationException("The fake console stream rejected a history gap.");
            }
        }

        public Task<ProductLocalHandshakePayload> HandshakeAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ProductLocalHandshakePayload(
                new ProductHandshakeResponse(
                    "Muhun MCSV Manager",
                    "test",
                    MaximumApiVersion,
                    ProductApiProtocol.MinimumSupportedVersion,
                    Ready: true),
                _serviceInstanceId,
                DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<ProductServerSummary>> ListServersAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProductServerSummary>>(
                _serverIds.Select(Summary).ToArray());

        public Task<IReadOnlyList<ProductServerStatus>> ListStatusesAsync(
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _statusListCallCount);
            if (call >= 2)
            {
                SecondStatusPoll.TrySetResult();
            }

            return Task.FromResult<IReadOnlyList<ProductServerStatus>>(
                _serverIds.Select(Status).ToArray());
        }

        public Task<ProductServerStatus> GetStatusAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Status(serverId));

        public Task<ProductServerRegistration> GetRegistrationAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Registration(serverId));

        public Task<ProductConsolePage> ReadConsoleAsync(
            Guid serverId,
            long afterCursor,
            int limit = ProductConsoleContract.MaximumPageSize,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readConsoleCallCount);
            ReadConsoleRequests.Enqueue((serverId, afterCursor));
            ProductConsoleEntry[] entries;
            lock (_journalSync)
            {
                entries = _journals[serverId]
                    .Where(entry => entry.Cursor > afterCursor)
                    .Take(limit)
                    .ToArray();
            }

            var nextCursor = entries.Length == 0 ? afterCursor : entries[^1].Cursor;
            return Task.FromResult(new ProductConsolePage(
                serverId,
                afterCursor,
                OldestAvailableCursor: entries.Length == 0 ? 0 : 1,
                NextCursor: nextCursor,
                HistoryGap: false,
                Entries: entries));
        }

        public async Task<ProductConsolePage> WaitForConsoleAsync(
            Guid serverId,
            long afterCursor,
            int limit = ProductConsoleContract.MaximumPageSize,
            int waitTimeoutMilliseconds = ProductConsoleContract.DefaultWaitTimeoutMilliseconds,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _waitCallCount);
            ConsoleWaitRequests.Enqueue((serverId, afterCursor));
            _waitStarted[serverId].TrySetResult();
            cancellationToken.ThrowIfCancellationRequested();

            if (ReturnImmediateEmptyWaits)
            {
                _waitResponseReturned[serverId].TrySetResult();
                return EmptyWaitPage(serverId, afterCursor);
            }

            try
            {
                var response = await _consoleResponses[serverId].Reader
                    .ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
                _waitResponseReturned[serverId].TrySetResult();
                return new ProductConsolePage(
                    serverId,
                    afterCursor,
                    response.OldestAvailableCursor,
                    response.NextCursor,
                    response.HistoryGap,
                    response.Entries);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _waitCancelled[serverId].TrySetResult();
                throw;
            }
        }

        public Task<ProductServerPropertiesDocument> ReadServerPropertiesAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ProductServerPropertiesDocument(
                serverId,
                Exists: true,
                Text: $"server-port={Registration(serverId).Port}\n",
                RevisionSha256: new string('a', 64)));

        public Task<IReadOnlyList<ProductServerBackupSummary>> ListBackupsAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProductServerBackupSummary>>([]);

        public Task<ProductServerPlayerList> ListPlayersAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _playerListCallCount);
            PlayersListed.TrySetResult();
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ProductServerPlayerList(
                serverId,
                now,
                AuthoritativeOnlinePlayers
                    .Select(name => new ProductServerPlayerSummary(name, now))
                    .ToArray())
            {
                KnownPlayers = AuthoritativeOnlinePlayers
                    .Select(name => new ProductKnownPlayerSummary(
                        name,
                        Uuid: null,
                        Online: true,
                        Operator: false,
                        Whitelisted: false,
                        Banned: false,
                        LastSeenUtc: now))
                    .ToArray(),
            });
        }

        public Task<ProductServerStatus> RegisterAsync(
            ProductServerRegistration registration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Status(registration.Id));

        public Task RemoveAsync(Guid serverId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ProductServerMutationResult> StartAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Mutation(serverId));

        public Task<ProductServerMutationResult> StopAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Mutation(serverId));

        public Task<ProductServerMutationResult> RestartAsync(
            Guid serverId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Mutation(serverId));

        public Task<ProductServerStatus> SendCommandAsync(
            Guid serverId,
            string command,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Status(serverId));

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }

        private ProductServerSummary Summary(Guid serverId)
        {
            var index = Array.IndexOf(_serverIds, serverId);
            return new ProductServerSummary(
                serverId,
                $"Service Server {index + 1}",
                ServersRunning ? ProductServerState.Running : ProductServerState.Stopped,
                25_565 + index,
                "NeoForge",
                "1.21.1");
        }

        private ProductServerStatus Status(Guid serverId)
            => new(
                Summary(serverId),
                SessionId: ServersRunning ? _sessionIds[serverId] : null,
                ProcessId: ServersRunning ? 1234 : null,
                StartedAtUtc: ServersRunning ? DateTimeOffset.UtcNow : null,
                LastExitCode: null,
                Resource: null,
                LastError: null);

        private ProductServerRegistration Registration(Guid serverId)
        {
            var summary = Summary(serverId);
            return new ProductServerRegistration
            {
                Id = serverId,
                Name = summary.Name,
                ServerDirectory = $"service-server-{serverId:N}",
                JavaRuntimePath = "temurin-21/bin/java.exe",
                CoreType = summary.CoreType,
                MinecraftVersion = summary.MinecraftVersion,
                MinimumMemoryMb = 1_024,
                MaximumMemoryMb = 2_048,
                Port = summary.Port,
                ServerJarPath = "server.jar",
                ServerArguments = ["nogui"],
            };
        }

        private ProductServerMutationResult Mutation(Guid serverId)
            => new(serverId, Changed: true, Status(serverId));

        private ProductConsoleEntry AppendToJournal(Guid serverId, string text)
        {
            var cursor = _nextCursors.AddOrUpdate(serverId, 1, static (_, current) => current + 1);
            var entry = new ProductConsoleEntry(
                cursor,
                _sessionIds[serverId],
                DateTimeOffset.UtcNow,
                text,
                ProductConsoleStream.StandardOutput,
                ProductConsoleSeverity.Information,
                DiagnosticId: null,
                IsDiagnosticContinuation: false,
                TextTruncated: false);
            lock (_journalSync)
            {
                _journals[serverId].Add(entry);
            }

            return entry;
        }

        private static ProductConsolePage EmptyWaitPage(Guid serverId, long afterCursor)
            => new(
                serverId,
                afterCursor,
                OldestAvailableCursor: 1,
                NextCursor: afterCursor,
                HistoryGap: false,
                Entries: []);

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed record ConsoleWaitResponse(
            long OldestAvailableCursor,
            long NextCursor,
            bool HistoryGap,
            IReadOnlyList<ProductConsoleEntry> Entries);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "mcsv-console-realtime-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

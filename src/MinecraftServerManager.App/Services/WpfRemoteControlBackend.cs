using System.Windows.Threading;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Remote;
using MinecraftServerManager.Remote.Contracts;

namespace MinecraftServerManager.App.Services;

/// <summary>
/// Transitional adapter that keeps every WPF collection access on the application dispatcher and
/// routes mutations through the existing lifecycle/backup coordination in MainWindowViewModel.
/// Remote requests never change the desktop's SelectedServer and never receive local paths.
/// </summary>
public sealed class WpfRemoteControlBackend : IRemoteControlBackend
{
    private readonly MainWindowViewModel _viewModel;
    private readonly Dispatcher _dispatcher;

    public WpfRemoteControlBackend(MainWindowViewModel viewModel, Dispatcher dispatcher)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async ValueTask<RemoteDashboardDto> GetDashboardAsync(CancellationToken cancellationToken)
        => await ReadOnDispatcherAsync(
            () => new RemoteDashboardDto(
                DateTimeOffset.UtcNow,
                _viewModel.Servers.Select(CreateSummary).ToArray()),
            cancellationToken);

    public async ValueTask<RemoteServerDetailDto?> GetServerAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        if (!TryParseServerId(serverId, out var instanceId)) return null;
        return await ReadOnDispatcherAsync(
            () =>
            {
                var server = FindServer(instanceId);
                return server is null
                    ? null
                    : new RemoteServerDetailDto(
                        CreateSummary(server),
                        server.Model.JavaMajorVersion is { } javaMajor ? $"Java {javaMajor}" : null,
                        SupportsPlayerManagement: true,
                        SupportsBackups: server.CanAccessLocalFiles,
                        HasDiagnosticConsole: server.SeparateDiagnosticOutput);
            },
            cancellationToken);
    }

    public async ValueTask<RemoteConsolePageDto?> GetConsoleAsync(
        string serverId,
        RemoteConsoleQuery query,
        CancellationToken cancellationToken)
    {
        if (!TryParseServerId(serverId, out var instanceId)) return null;
        return await ReadOnDispatcherAsync(
            () =>
            {
                var server = FindServer(instanceId);
                if (server is null) return null;

                IEnumerable<ConsoleLineViewModel> source = query.Stream switch
                {
                    RemoteConsoleStream.Diagnostic => server.DiagnosticLines,
                    RemoteConsoleStream.Ordinary when server.SeparateDiagnosticOutput =>
                        server.ConsoleLines.Where(line => !line.IsDiagnostic),
                    RemoteConsoleStream.Ordinary => server.ConsoleLines,
                    _ when server.SeparateDiagnosticOutput => server.ConsoleLines
                        .Concat(server.DiagnosticLines)
                        .DistinctBy(line => line.Sequence),
                    _ => server.ConsoleLines
                };
                if (query.After is { } after)
                {
                    source = source.Where(line => line.Sequence > after);
                }

                var page = source
                    .OrderBy(line => line.Sequence)
                    .Take(query.Limit + 1)
                    .ToArray();
                var hasMore = page.Length > query.Limit;
                var lines = page
                    .Take(query.Limit)
                    .Select(MapConsoleLine)
                    .ToArray();
                return new RemoteConsolePageDto(
                    lines,
                    lines.Length == 0 ? query.After : lines[^1].Sequence,
                    hasMore);
            },
            cancellationToken);
    }

    public async ValueTask<RemotePlayerListDto?> GetPlayersAsync(
        string serverId,
        CancellationToken cancellationToken)
    {
        if (!TryParseServerId(serverId, out var instanceId)) return null;
        return await ReadOnDispatcherAsync(
            () =>
            {
                var server = FindServer(instanceId);
                return server is null
                    ? null
                    : new RemotePlayerListDto(
                        DateTimeOffset.UtcNow,
                        server.Players
                            .Take(4_096)
                            .Select(player => new RemotePlayerDto(
                                player.Name,
                                TryParsePlayerUuid(player.Uuid),
                                player.IsOnline,
                                player.IsOperator,
                                player.IsBanned,
                                LastSeenUtc: null))
                            .ToArray());
            },
            cancellationToken);
    }

    public ValueTask<RemoteOperationResultDto> StartServerAsync(
        string serverId,
        CancellationToken cancellationToken)
        => RunMutationAsync(
            serverId,
            (id, token) => _viewModel.StartServerForRemoteAsync(id, token),
            "已接受啟動操作。",
            cancellationToken);

    public ValueTask<RemoteOperationResultDto> StopServerAsync(
        string serverId,
        CancellationToken cancellationToken)
        => RunMutationAsync(
            serverId,
            (id, token) => _viewModel.StopServerForRemoteAsync(id, token),
            "已完成安全停止操作。",
            cancellationToken);

    public ValueTask<RemoteOperationResultDto> RestartServerAsync(
        string serverId,
        CancellationToken cancellationToken)
        => RunMutationAsync(
            serverId,
            (id, token) => _viewModel.RestartServerForRemoteAsync(id, token),
            "已完成重新啟動操作。",
            cancellationToken);

    public ValueTask<RemoteOperationResultDto> SendConsoleCommandAsync(
        string serverId,
        string command,
        CancellationToken cancellationToken)
        => RunMutationAsync(
            serverId,
            (id, token) => _viewModel.SendCommandForRemoteAsync(id, command, token),
            "指令已傳送。",
            cancellationToken);

    public ValueTask<RemoteOperationResultDto> PerformPlayerActionAsync(
        string serverId,
        RemotePlayerActionRequestDto request,
        CancellationToken cancellationToken)
        => RunMutationAsync(
            serverId,
            (id, token) => _viewModel.ExecutePlayerActionForRemoteAsync(
                id,
                MapPlayerAction(request.Action),
                request.PlayerName,
                request.Reason,
                token),
            "玩家管理操作已傳送。",
            cancellationToken);

    public ValueTask<RemoteOperationResultDto> CreateBackupAsync(
        string serverId,
        CancellationToken cancellationToken)
        => RunMutationAsync(
            serverId,
            (id, token) => _viewModel.CreateBackupForRemoteAsync(id, token),
            "備份已建立。",
            cancellationToken);

    private async ValueTask<RemoteOperationResultDto> RunMutationAsync(
        string serverId,
        Func<Guid, CancellationToken, Task> operation,
        string successMessage,
        CancellationToken cancellationToken)
    {
        if (!TryParseServerId(serverId, out var instanceId))
        {
            return new RemoteOperationResultDto(false, "找不到指定的 Server。");
        }

        try
        {
            await InvokeOnDispatcherAsync(
                () => operation(instanceId, cancellationToken),
                cancellationToken);
            return new RemoteOperationResultDto(true, successMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RemoteOperationResultDto(false, "操作已取消。");
        }
        catch (KeyNotFoundException)
        {
            return new RemoteOperationResultDto(false, "找不到指定的 Server。");
        }
        catch (InvalidOperationException exception)
        {
            return new RemoteOperationResultDto(false, SanitizeExpectedError(exception.Message));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new RemoteOperationResultDto(false, "操作未完成；請查看主機端 MCSV Manager 狀態。");
        }
    }

    private async Task<T> ReadOnDispatcherAsync<T>(Func<T> read, CancellationToken cancellationToken)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            throw new InvalidOperationException("MCSV Manager 正在關閉。");
        }

        if (_dispatcher.CheckAccess()) return read();
        return await _dispatcher.InvokeAsync(
            read,
            DispatcherPriority.Background,
            cancellationToken);
    }

    private async Task InvokeOnDispatcherAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            throw new InvalidOperationException("MCSV Manager 正在關閉。");
        }

        if (_dispatcher.CheckAccess())
        {
            await operation();
            return;
        }

        await _dispatcher.InvokeAsync(
                operation,
                DispatcherPriority.Normal,
                cancellationToken)
            .Task
            .Unwrap();
    }

    private ServerInstanceViewModel? FindServer(Guid instanceId)
        => _viewModel.Servers.FirstOrDefault(server => server.Id == instanceId);

    private static RemoteServerSummaryDto CreateSummary(ServerInstanceViewModel server)
    {
        var isRunning = server.State is ServerState.Starting or ServerState.Running or ServerState.Stopping;
        return new RemoteServerSummaryDto(
            server.Id.ToString("N"),
            server.Name,
            server.CoreTypeText,
            server.MinecraftVersionDisplay,
            MapState(server.State),
            isRunning,
            server.OnlinePlayerCount,
            MaximumPlayers: null,
            CpuPercent: server.State == ServerState.Running ? server.CpuPercent : null,
            MemoryBytes: server.State == ServerState.Running ? server.WorkingSetBytes : null,
            Port: server.ActivePort ?? server.Port,
            UptimeSeconds: server.State == ServerState.Running ? (long)server.Uptime.TotalSeconds : null);
    }

    private static RemoteConsoleLineDto MapConsoleLine(ConsoleLineViewModel line)
        => new(
            line.Sequence,
            line.TimestampUtc,
            line.Severity switch
            {
                ConsoleLineSeverity.Warning => RemoteConsoleSeverity.Warning,
                ConsoleLineSeverity.Error or ConsoleLineSeverity.Fatal => RemoteConsoleSeverity.Error,
                _ => RemoteConsoleSeverity.Information
            },
            line.IsDiagnostic ? RemoteConsoleStream.Diagnostic : RemoteConsoleStream.Ordinary,
            line.Text);

    private static RemoteServerState MapState(ServerState state) => state switch
    {
        ServerState.Starting => RemoteServerState.Starting,
        ServerState.Running => RemoteServerState.Running,
        ServerState.Stopping => RemoteServerState.Stopping,
        ServerState.Crashed or ServerState.Faulted => RemoteServerState.Failed,
        _ => RemoteServerState.Stopped
    };

    private static string MapPlayerAction(RemotePlayerActionKind action) => action switch
    {
        RemotePlayerActionKind.Kick => "kick",
        RemotePlayerActionKind.Ban => "ban",
        RemotePlayerActionKind.Pardon => "pardon",
        RemotePlayerActionKind.Op => "op",
        RemotePlayerActionKind.Deop => "deop",
        RemotePlayerActionKind.WhitelistAdd => "whitelist-add",
        RemotePlayerActionKind.WhitelistRemove => "whitelist-remove",
        RemotePlayerActionKind.WhitelistOn => "whitelist-on",
        RemotePlayerActionKind.WhitelistOff => "whitelist-off",
        _ => throw new InvalidOperationException("不支援的玩家管理操作。")
    };

    private static bool TryParseServerId(string? value, out Guid instanceId)
        => Guid.TryParseExact(value, "N", out instanceId);

    private static Guid? TryParsePlayerUuid(string? value)
    {
        if (Guid.TryParse(value, out var parsed)) return parsed;
        if (value?.Length == 32 && Guid.TryParseExact(value, "N", out parsed)) return parsed;
        return null;
    }

    private static string SanitizeExpectedError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "操作條件不成立。";
        var normalized = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }
}

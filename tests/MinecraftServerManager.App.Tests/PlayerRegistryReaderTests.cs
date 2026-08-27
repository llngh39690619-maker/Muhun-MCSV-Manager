using System.IO;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class PlayerRegistryReaderTests
{
    [Fact]
    public async Task ReadAsync_MergesRegistriesAndIgnoresInvalidNames()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "usercache.json"),
            """[{"name":"KnownUser","uuid":"known-id"},{"name":"bad name","uuid":"ignored"}]""");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "ops.json"),
            """[{"name":"KnownUser","uuid":"known-id"},{"name":"Admin","uuid":"admin-id"}]""");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "whitelist.json"),
            """[{"name":"KnownUser","uuid":"known-id"}]""");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "banned-players.json"),
            """[{"name":"BannedUser","uuid":"banned-id"}]""");

        var result = await PlayerRegistryReader.ReadAsync(directory.Path, CancellationToken.None);

        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Players.Count);
        var known = Assert.Single(result.Players, player => player.Name == "KnownUser");
        Assert.True(known.IsOperator);
        Assert.True(known.IsWhitelisted);
        Assert.False(known.IsBanned);
        Assert.False(known.IsOnline);
        Assert.True(Assert.Single(result.Players, player => player.Name == "Admin").IsOperator);
        Assert.True(Assert.Single(result.Players, player => player.Name == "BannedUser").IsBanned);
    }

    [Fact]
    public async Task ReadAsync_MalformedRegistryIsReportedWithoutDiscardingOtherFiles()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "usercache.json"), "not-json");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "ops.json"),
            """[{"name":"Admin","uuid":"admin-id"}]""");

        var result = await PlayerRegistryReader.ReadAsync(directory.Path, CancellationToken.None);

        Assert.Single(result.Warnings);
        Assert.Equal("usercache.json", result.Warnings[0].FileName);
        Assert.True(Assert.Single(result.Players).IsOperator);
    }

    [Fact]
    public async Task ReadAsync_PreCanceledRequestDoesNoWork()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PlayerRegistryReader.ReadAsync(directory.Path, cancellation.Token));
    }

    [Fact]
    public async Task ReadAsync_ExcessUniquePlayersAreBoundedAndReported()
    {
        using var directory = new TemporaryDirectory();
        var entries = Enumerable.Range(0, 5_000)
            .Select(index => $"{{\"name\":\"P{index:D4}\",\"uuid\":\"id-{index}\"}}");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "usercache.json"),
            $"[{string.Join(',', entries)}]");

        var result = await PlayerRegistryReader.ReadAsync(directory.Path, CancellationToken.None);

        Assert.Equal(PlayerRegistryReader.MaximumPlayerRecords, result.Players.Count);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("玩家登錄檔", warning.FileName);
        Assert.Contains("4,096", warning.Message, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"McsvPlayerRegistry-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

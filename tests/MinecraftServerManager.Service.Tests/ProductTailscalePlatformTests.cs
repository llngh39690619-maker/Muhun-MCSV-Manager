using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductTailscalePlatformTests
{
    [Fact]
    public async Task Platform_UsesOnlyBoundedExactStatusAndForegroundFunnelArguments()
    {
        var runner = new RecordingRunner
        {
            CommandResults = new Queue<ProductTailscaleCommandResult>(
            [
                new ProductTailscaleCommandResult(
                    0,
                    "{\"BackendState\":\"Running\",\"Self\":{\"DNSName\":\"box.tail.ts.net\"},\"CertDomains\":[\"box.tail.ts.net\"]}",
                    string.Empty,
                    false),
                new ProductTailscaleCommandResult(0, "{}", string.Empty, false),
            ]),
        };
        var platform = new ProductTailscalePlatform(new FixedLocator(), runner);

        var node = await platform.GetNodeStatusAsync(CancellationToken.None);
        var route = await platform.GetFunnelStatusAsync(node.DnsName!, 42871, CancellationToken.None);
        await using var process = await platform.StartFunnelAsync(42871, CancellationToken.None);

        Assert.Equal(["status", "--json"], runner.Commands[0]);
        Assert.Equal(["funnel", "status", "--json"], runner.Commands[1]);
        Assert.All(runner.Timeouts, timeout => Assert.Equal(ProductTailscalePlatform.CommandTimeout, timeout));
        Assert.Equal(
            ["funnel", "--yes", "--https=443", "http://127.0.0.1:42871"],
            runner.ForegroundArguments);
        Assert.Equal(ProductFunnelRouteDisposition.Absent, route.Disposition);
    }

    [Fact]
    public async Task FailedOrTimedOutCommands_NeverBecomeAvailableState()
    {
        var runner = new RecordingRunner
        {
            CommandResults = new Queue<ProductTailscaleCommandResult>(
            [
                new ProductTailscaleCommandResult(null, string.Empty, "secret path", true),
                new ProductTailscaleCommandResult(1, string.Empty, "secret path", false),
            ]),
        };
        var platform = new ProductTailscalePlatform(new FixedLocator(), runner);

        var node = await platform.GetNodeStatusAsync(CancellationToken.None);
        var route = await platform.GetFunnelStatusAsync("box.tail.ts.net", 42871, CancellationToken.None);

        Assert.Equal("tailscale.status_timeout", node.ErrorCode);
        Assert.Equal(ProductFunnelRouteDisposition.Indeterminate, route.Disposition);
        Assert.Equal("tailscale.funnel_status_failed", route.ErrorCode);
        Assert.DoesNotContain("secret", node.ErrorCode, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", route.ErrorCode, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        "Tailscale already in use by another Windows user",
        "tailscale.localapi_identity_conflict")]
    [InlineData(
        "401 Unauthorized",
        "tailscale.localapi_identity_conflict")]
    [InlineData(
        @"open \\.\pipe\ProtectedPrefix\Administrators\Tailscale\tailscaled: Access is denied.",
        "tailscale.localapi_access_denied")]
    public async Task LocalApiIdentityFailures_AreClassifiedAcrossEveryBoundedCommand(
        string standardError,
        string expectedErrorCode)
    {
        var denied = new ProductTailscaleCommandResult(1, string.Empty, standardError, false);
        var runner = new RecordingRunner
        {
            CommandResults = new Queue<ProductTailscaleCommandResult>(
            [
                denied,
                denied,
                denied,
                new ProductTailscaleCommandResult(0, "old-machine\n", string.Empty, false),
                denied,
            ]),
        };
        var platform = new ProductTailscalePlatform(new FixedLocator(), runner);

        var node = await platform.GetNodeStatusAsync(CancellationToken.None);
        var route = await platform.GetFunnelStatusAsync(
            "x-mcsv.tail.ts.net",
            42871,
            CancellationToken.None);
        var getHostname = await platform.EnsureMachineHostnameAsync(
            "x-mcsv",
            CancellationToken.None);
        var setHostname = await platform.EnsureMachineHostnameAsync(
            "x-mcsv",
            CancellationToken.None);

        Assert.Equal(expectedErrorCode, node.ErrorCode);
        Assert.Equal(ProductFunnelRouteDisposition.Indeterminate, route.Disposition);
        Assert.Equal(expectedErrorCode, route.ErrorCode);
        Assert.False(getHostname.Succeeded);
        Assert.False(getHostname.Changed);
        Assert.Equal(expectedErrorCode, getHostname.ErrorCode);
        Assert.False(setHostname.Succeeded);
        Assert.False(setHostname.Changed);
        Assert.Equal(expectedErrorCode, setHostname.ErrorCode);
    }

    [Fact]
    public async Task EnsureMachineHostname_LeavesExactXMcsvNameUnchanged()
    {
        var runner = new RecordingRunner
        {
            CommandResults = new Queue<ProductTailscaleCommandResult>(
            [
                new ProductTailscaleCommandResult(0, "x-mcsv\r\n", string.Empty, false),
            ]),
        };
        var platform = new ProductTailscalePlatform(new FixedLocator(), runner);

        var result = await platform.EnsureMachineHostnameAsync("x-mcsv", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Changed);
        Assert.Null(result.ErrorCode);
        Assert.Collection(
            runner.Commands,
            command => Assert.Equal(["get", "hostname"], command));
    }

    [Fact]
    public async Task EnsureMachineHostname_ReplacesDifferentNameWithExactXMcsvName()
    {
        var runner = new RecordingRunner
        {
            CommandResults = new Queue<ProductTailscaleCommandResult>(
            [
                new ProductTailscaleCommandResult(0, "old-machine\n", string.Empty, false),
                new ProductTailscaleCommandResult(0, string.Empty, string.Empty, false),
            ]),
        };
        var platform = new ProductTailscalePlatform(new FixedLocator(), runner);

        var result = await platform.EnsureMachineHostnameAsync("x-mcsv", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.Null(result.ErrorCode);
        Assert.Collection(
            runner.Commands,
            command => Assert.Equal(["get", "hostname"], command),
            command => Assert.Equal(["set", "--hostname=x-mcsv"], command));
    }

    [Fact]
    public async Task EnsureMachineHostname_EmptyUnsetPreferenceSetsExactXMcsvNameOnce()
    {
        var runner = new RecordingRunner
        {
            CommandResults = new Queue<ProductTailscaleCommandResult>(
            [
                new ProductTailscaleCommandResult(0, string.Empty, string.Empty, false),
                new ProductTailscaleCommandResult(0, string.Empty, string.Empty, false),
            ]),
        };
        var platform = new ProductTailscalePlatform(new FixedLocator(), runner);

        var result = await platform.EnsureMachineHostnameAsync("x-mcsv", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.Collection(
            runner.Commands,
            command => Assert.Equal(["get", "hostname"], command),
            command => Assert.Equal(["set", "--hostname=x-mcsv"], command));
    }

    private sealed class FixedLocator : IProductTailscaleExecutableLocator
    {
        public string? FindTrustedExecutable() => @"C:\Program Files\Tailscale\tailscale.exe";
    }

    private sealed class RecordingRunner : IProductTailscaleProcessRunner
    {
        public Queue<ProductTailscaleCommandResult> CommandResults { get; init; } = new();
        public List<IReadOnlyList<string>> Commands { get; } = [];
        public List<TimeSpan> Timeouts { get; } = [];
        public IReadOnlyList<string>? ForegroundArguments { get; private set; }

        public Task<ProductTailscaleCommandResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Commands.Add(arguments.ToArray());
            Timeouts.Add(timeout);
            return Task.FromResult(CommandResults.Dequeue());
        }

        public Task<IProductOwnedFunnelProcess> StartForegroundAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            ForegroundArguments = arguments.ToArray();
            return Task.FromResult<IProductOwnedFunnelProcess>(new FakeProcess());
        }
    }

    private sealed class FakeProcess : IProductOwnedFunnelProcess
    {
        public bool HasExited { get; private set; }
        public int? ExitCode => HasExited ? 0 : null;
        public string StandardOutput => "https://box.tail.ts.net";
        public string StandardError => string.Empty;
        public Task Completion => Task.Delay(Timeout.InfiniteTimeSpan);
        public Task StopAsync(CancellationToken cancellationToken)
        {
            HasExited = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

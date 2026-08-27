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

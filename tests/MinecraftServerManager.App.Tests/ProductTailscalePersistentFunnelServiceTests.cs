using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class ProductTailscalePersistentFunnelServiceTests
{
    private static readonly Uri PublicOrigin = new(
        "https://x-mcsv.tailafea21.ts.net/",
        UriKind.Absolute);
    private const string Executable = @"C:\Program Files\Tailscale\tailscale.exe";
    private const int LocalPort = 42_871;

    [Fact]
    public async Task EnsureIdentity_RenamesMachineAndVerifiesCanonicalCertificateOrigin()
    {
        var runner = new RecordingRunner(
            Success(NodeJson("muhun.tailafea21.ts.net.")),
            Success("muhun\n"),
            Success(),
            Success(NodeJson("x-mcsv.tailafea21.ts.net.")));
        var service = CreateService(runner);

        var result = await service.EnsureProductIdentityAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(PublicOrigin, result.PublicOrigin);
        Assert.Equal(
            [
                "status --json",
                "get hostname",
                "set --hostname=x-mcsv",
                "status --json",
            ],
            runner.Commands);
    }

    [Fact]
    public async Task EnsureRoute_UsesOnlyPersistentBackgroundCommandAndVerifiesExactRoute()
    {
        var runner = new RecordingRunner(
            Success("{}"),
            Success(),
            Success(ExactRouteJson()));
        var service = CreateService(runner);

        var result = await service.EnsureRouteAsync(
            PublicOrigin,
            LocalPort,
            allowExistingVerifiedRoute: false);

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.Equal(ProductPersistentFunnelDisposition.ExactTarget, result.Disposition);
        Assert.NotNull(result.VerifiedAtUtc);
        Assert.Equal(
            [
                "funnel status --json",
                "funnel --bg --yes --https=443 http://127.0.0.1:42871",
                "funnel status --json",
            ],
            runner.Commands);
    }

    [Fact]
    public async Task EnsureRoute_DoesNotAdoptUnreceiptedPreexistingExactRoute()
    {
        var runner = new RecordingRunner(Success(ExactRouteJson()));
        var service = CreateService(runner);

        var result = await service.EnsureRouteAsync(
            PublicOrigin,
            LocalPort,
            allowExistingVerifiedRoute: false);

        Assert.False(result.Succeeded);
        Assert.Equal("tailscale.funnel_unowned_exact_route", result.ErrorCode);
        Assert.Equal(["funnel status --json"], runner.Commands);
    }

    [Fact]
    public async Task RemoveRoute_ResetsOnlySoleExactConfigAndVerifiesEmptyDocument()
    {
        var runner = new RecordingRunner(
            Success(ExactRouteJson()),
            Success(),
            Success("{}"));
        var service = CreateService(runner);

        var result = await service.RemoveRouteAsync(PublicOrigin, LocalPort);

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.Equal(ProductPersistentFunnelDisposition.Absent, result.Disposition);
        Assert.Equal(
            [
                "funnel status --json",
                "funnel reset",
                "funnel status --json",
            ],
            runner.Commands);
    }

    [Theory]
    [MemberData(nameof(UnsafeResetDocuments))]
    public async Task RemoveRoute_NeverResetsNonExclusiveOrUnknownConfig(string json)
    {
        var runner = new RecordingRunner(Success(json));
        var service = CreateService(runner);

        var result = await service.RemoveRouteAsync(PublicOrigin, LocalPort);

        Assert.False(result.Succeeded);
        Assert.Single(runner.Commands);
        Assert.Equal("funnel status --json", runner.Commands[0]);
        Assert.DoesNotContain("funnel reset", runner.Commands);
    }

    public static TheoryData<string> UnsafeResetDocuments => new()
    {
        // An additional port is a user-owned route even if 443 itself is exact.
        ExactRouteJson().Replace(
            "\"443\":{\"HTTPS\":true}",
            "\"443\":{\"HTTPS\":true},\"8443\":{\"HTTPS\":true}",
            StringComparison.Ordinal),
        // Persistent config must not contain any named service or foreground session.
        ExactRouteJson()[..^1] + ",\"Services\":{}}",
        ExactRouteJson()[..^1] + ",\"Foreground\":{}}",
        // Future schema is indeterminate and therefore cannot authorize a whole-config reset.
        ExactRouteJson()[..^1] + ",\"FutureField\":{}}",
    };

    [Fact]
    public void MachineLocator_IgnoresPathAndAcceptsOnlyFixedProgramFilesLocation()
    {
        var locator = new ProductTailscaleMachineExecutableLocator(
            folder => folder switch
            {
                Environment.SpecialFolder.ProgramFiles => @"C:\Program Files",
                Environment.SpecialFolder.ProgramFilesX86 => @"C:\Program Files (x86)",
                _ => string.Empty,
            },
            path => path.Equals(Executable, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(Executable, locator.FindTrustedExecutable());
    }

    private static ProductTailscalePersistentFunnelService CreateService(RecordingRunner runner)
        => new(
            new StubMachineLocator(),
            runner,
            new ImmediateDelay(),
            TimeProvider.System,
            TimeSpan.FromSeconds(1),
            verificationAttempts: 1,
            verificationInterval: TimeSpan.Zero);

    private static TailscaleCommandResult Success(string stdout = "")
        => new(0, stdout, string.Empty);

    private static string NodeJson(string dnsName)
        => $$"""
             {
               "BackendState":"Running",
               "Self":{"DNSName":"{{dnsName}}"},
               "CertDomains":["{{dnsName.TrimEnd('.')}}"]
             }
             """;

    private static string ExactRouteJson()
        => """
           {
             "TCP":{"443":{"HTTPS":true}},
             "Web":{"x-mcsv.tailafea21.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:42871"}}}},
             "AllowFunnel":{"x-mcsv.tailafea21.ts.net:443":true}
           }
           """;

    private sealed class StubMachineLocator : IProductTailscaleMachineExecutableLocator
    {
        public string? FindTrustedExecutable() => Executable;
    }

    private sealed class ImmediateDelay : ITailscaleDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RecordingRunner(params TailscaleCommandResult[] responses)
        : ITailscaleCommandRunner
    {
        private readonly Queue<TailscaleCommandResult> _responses = new(responses);
        public List<string> Commands { get; } = [];

        public Task<TailscaleCommandResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Executable, executablePath);
            Commands.Add(string.Join(' ', arguments));
            return Task.FromResult(_responses.Dequeue());
        }
    }
}

using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductRemoteWebRouteStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "muhun-remote-route-store-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExactVerifiedRoute_SurvivesServiceStoreRecreationUntilExplicitDelete()
    {
        var layout = CreateLayout();
        var verifiedAtUtc = new DateTimeOffset(2026, 9, 5, 1, 2, 3, TimeSpan.Zero);
        var writer = new ProductRemoteWebRouteStore(layout);

        var written = writer.WriteVerified(
            "https://x-mcsv.tail123.ts.net/",
            verifiedAtUtc);
        var recovered = new ProductRemoteWebRouteStore(layout).Read();

        Assert.Equal(new Uri("https://x-mcsv.tail123.ts.net/"), written.PublicOrigin);
        Assert.Equal(ProductRemoteWebSupervisor.LocalWebPort, written.LocalPort);
        Assert.Equal("http://127.0.0.1:42871", written.LocalTarget);
        Assert.Equal(verifiedAtUtc, written.VerifiedAtUtc);
        Assert.Equal(written, recovered);
        Assert.Single(
            Directory.EnumerateFiles(layout.Operations),
            path => string.Equals(
                Path.GetFileName(path),
                ProductRemoteWebRouteStore.FileName,
                StringComparison.Ordinal));

        new ProductRemoteWebRouteStore(layout).Delete();

        Assert.Null(writer.Read());
    }

    [Theory]
    [InlineData("http://x-mcsv.tail123.ts.net/")]
    [InlineData("https://other.tail123.ts.net/")]
    [InlineData("https://x-mcsv-evil.tail123.ts.net/")]
    [InlineData("https://x-mcsv.example.com/")]
    [InlineData("https://x-mcsv.tail123.ts.net:8443/")]
    [InlineData("https://user@x-mcsv.tail123.ts.net/")]
    [InlineData("https://x-mcsv.tail123.ts.net/admin")]
    [InlineData("https://x-mcsv.tail123.ts.net/?route=other")]
    [InlineData("https://x-mcsv.tail123.ts.net/#fragment")]
    [InlineData("https://X-MCSV.tail123.ts.net/")]
    [InlineData("https://x-mcsv.tail123.ts.net")]
    public void WriteVerified_RejectsEveryNonCanonicalOrNonXMcsvOrigin(string publicOrigin)
    {
        var store = new ProductRemoteWebRouteStore(CreateLayout());

        Assert.Throws<ArgumentException>(() => store.WriteVerified(
            publicOrigin,
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Read_RejectsUnknownReceiptFieldsInsteadOfTrustingFutureOrInjectedState()
    {
        var layout = CreateLayout();
        File.WriteAllText(
            Path.Combine(layout.Operations, ProductRemoteWebRouteStore.FileName),
            """
            {
              "schemaVersion": 1,
              "publicOrigin": "https://x-mcsv.tail123.ts.net/",
              "localPort": 42871,
              "localTarget": "http://127.0.0.1:42871",
              "verifiedAtUtc": "2026-09-05T01:02:03+00:00",
              "routeTarget": "http://attacker.invalid/"
            }
            """);

        var error = Assert.Throws<InvalidDataException>(() =>
            new ProductRemoteWebRouteStore(layout).Read());

        Assert.IsType<System.Text.Json.JsonException>(error.InnerException);
    }

    [Theory]
    [InlineData(2, "2026-09-05T01:02:03+00:00")]
    [InlineData(1, "2026-09-05T09:02:03+08:00")]
    public void Read_RejectsWrongSchemaOrNonUtcVerificationTime(
        int schemaVersion,
        string verifiedAtUtc)
    {
        var layout = CreateLayout();
        File.WriteAllText(
            Path.Combine(layout.Operations, ProductRemoteWebRouteStore.FileName),
            $$"""
            {
              "schemaVersion": {{schemaVersion}},
              "publicOrigin": "https://x-mcsv.tail123.ts.net/",
              "localPort": 42871,
              "localTarget": "http://127.0.0.1:42871",
              "verifiedAtUtc": "{{verifiedAtUtc}}"
            }
            """);

        Assert.Throws<InvalidDataException>(() =>
            new ProductRemoteWebRouteStore(layout).Read());
    }

    [Theory]
    [InlineData(25565, "http://127.0.0.1:42871")]
    [InlineData(42871, "http://127.0.0.1:25565")]
    [InlineData(42871, "http://localhost:42871")]
    [InlineData(42871, "http://127.0.0.1:42871/")]
    public void Read_RejectsReceiptNotBoundToExactProductPortAndCanonicalTarget(
        int localPort,
        string localTarget)
    {
        var layout = CreateLayout();
        File.WriteAllText(
            Path.Combine(layout.Operations, ProductRemoteWebRouteStore.FileName),
            $$"""
            {
              "schemaVersion": 1,
              "publicOrigin": "https://x-mcsv.tail123.ts.net/",
              "localPort": {{localPort}},
              "localTarget": "{{localTarget}}",
              "verifiedAtUtc": "2026-09-05T01:02:03+00:00"
            }
            """);

        Assert.Throws<InvalidDataException>(() =>
            new ProductRemoteWebRouteStore(layout).Read());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup on Windows CI.
        }
    }

    private ProductDataLayout CreateLayout()
    {
        var layout = new ProductDataLayout(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        layout.EnsureCreated();
        return layout;
    }
}

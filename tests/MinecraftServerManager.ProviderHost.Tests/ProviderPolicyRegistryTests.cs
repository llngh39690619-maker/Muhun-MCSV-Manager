using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerManager.Contracts;
using MinecraftServerManager.Contracts.Plugins;

namespace MinecraftServerManager.ProviderHost.Tests;

public sealed class ProviderPolicyRegistryTests
{
    [Fact]
    public void Policy_AllowsDeclaredExactHttpsHost()
    {
        var registration = Registration(enabled: true);
        var request = new ProviderInvocationRequest(
            ProductProviderOperations.ModpackCatalogSearch,
            JsonSerializer.SerializeToElement(new { query = "sky" }),
            new Uri("https://api.example.com/v1/search?q=sky"));

        ProviderInvocationPolicy.EnsureAllowed(registration, request);
    }

    [Theory]
    [InlineData("https://sub.api.example.com/v1")]
    [InlineData("https://127.0.0.1/v1")]
    [InlineData("http://api.example.com/v1")]
    [InlineData("https://api.example.com:8443/v1")]
    [InlineData("https://user@api.example.com/v1")]
    public void Policy_RejectsTargetOutsideExactHttpsAllowlist(string target)
    {
        var request = new ProviderInvocationRequest(
            ProductProviderOperations.ModpackCatalogSearch,
            JsonSerializer.SerializeToElement(new { }),
            new Uri(target));

        Assert.Throws<ProviderPolicyException>(() =>
            ProviderInvocationPolicy.EnsureAllowed(Registration(enabled: true), request));
    }

    [Fact]
    public void Policy_RejectsUnknownOperation()
    {
        var request = new ProviderInvocationRequest(
            "provider.do-anything",
            JsonSerializer.SerializeToElement(new { }));

        var error = Assert.Throws<ProviderPolicyException>(() =>
            ProviderInvocationPolicy.EnsureAllowed(Registration(enabled: true), request));

        Assert.Contains("Unknown", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Policy_FailsClosedForNullOperationIdentifier()
    {
        var request = new ProviderInvocationRequest(
            null!,
            JsonSerializer.SerializeToElement(new { }));

        var error = Assert.Throws<ProviderPolicyException>(() =>
            ProviderInvocationPolicy.EnsureAllowed(Registration(enabled: true), request));

        Assert.Contains("identifier", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Policy_RejectsMissingCapabilityAndDisabledProvider()
    {
        var networkRequest = new ProviderInvocationRequest(
            ProductProviderOperations.RuntimeCatalogSearch,
            JsonSerializer.SerializeToElement(new { }),
            new Uri("https://api.example.com"));
        var healthRequest = new ProviderInvocationRequest(
            ProductProviderOperations.HealthGet,
            JsonSerializer.SerializeToElement(new { }));

        Assert.Throws<ProviderPolicyException>(() =>
            ProviderInvocationPolicy.EnsureAllowed(Registration(enabled: true), networkRequest));
        Assert.Throws<ProviderPolicyException>(() =>
            ProviderInvocationPolicy.EnsureAllowed(Registration(enabled: false), healthRequest));
    }

    [Fact]
    public async Task Registry_AtomicallyPersistsEnableDisableAndHealthState()
    {
        using var fixture = new RegistryFixture();
        var registry = new ProviderRegistry(fixture.Layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(Registration(enabled: false));
        await registry.SetEnabledAsync("example.catalog", true);
        await registry.ReportHealthAsync(
            "example.catalog",
            ProviderHealthStatus.Degraded,
            "line one\r\nline two");

        var reloaded = new ProviderRegistry(fixture.Layout);
        await reloaded.LoadAsync();
        var registration = Assert.Single(reloaded.GetAll());
        Assert.True(registration.IsEnabled);
        Assert.Equal(ProviderHealthStatus.Degraded, registration.Health);
        Assert.Equal(1, registration.ConsecutiveFailures);
        var lastError = Assert.IsType<string>(registration.LastError);
        Assert.DoesNotContain('\r', lastError);
        Assert.DoesNotContain('\n', lastError);
        Assert.Empty(Directory.EnumerateFiles(fixture.Layout.State, "*.tmp"));

        await reloaded.ReportHealthAsync("example.catalog", ProviderHealthStatus.Starting);
        await reloaded.ReportHealthAsync("example.catalog", ProviderHealthStatus.Failed, "failed again");
        Assert.Equal(2, Assert.Single(reloaded.GetAll()).ConsecutiveFailures);

        await reloaded.SetEnabledAsync("example.catalog", false);
        var disabled = Assert.Single(reloaded.GetAll());
        Assert.False(disabled.IsEnabled);
        Assert.Equal(ProviderHealthStatus.Disabled, disabled.Health);
        Assert.Equal(0, disabled.ConsecutiveFailures);
    }

    [Fact]
    public async Task Registry_RejectsUnknownJsonMembers()
    {
        using var fixture = new RegistryFixture();
        fixture.Layout.EnsureCreated();
        await File.WriteAllTextAsync(
            fixture.Layout.RegistryFile,
            """
            {"schemaVersion":1,"providers":[],"unknown":true}
            """);
        var registry = new ProviderRegistry(fixture.Layout);

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.LoadAsync());
    }

    [Fact]
    public async Task Registry_RejectsDuplicateKnownJsonMembers()
    {
        using var fixture = new RegistryFixture();
        fixture.Layout.EnsureCreated();
        await File.WriteAllTextAsync(
            fixture.Layout.RegistryFile,
            """
            {"schemaVersion":1,"schemaVersion":1,"providers":[]}
            """);
        var registry = new ProviderRegistry(fixture.Layout);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => registry.LoadAsync());

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessStartInfo_UsesOnlyIsolatedExecutableAndMinimalEnvironment()
    {
        using var fixture = new RegistryFixture();
        fixture.Layout.EnsureCreated();
        var install = Path.Combine(fixture.Layout.Packages, "example.catalog", "1.2.3");
        Directory.CreateDirectory(Path.Combine(install, "bin"));
        File.WriteAllText(Path.Combine(install, "bin", "provider.exe"), "binary placeholder");
        File.WriteAllText(
            Path.Combine(install, ProviderPackageInstaller.ManifestFileName),
            "signed manifest placeholder");
        var registration = Registration(enabled: true);
        var previous = Environment.GetEnvironmentVariable("MCSV_PROVIDER_TEST_SECRET");
        Environment.SetEnvironmentVariable("MCSV_PROVIDER_TEST_SECRET", "must-not-leak");
        try
        {
            var startInfo = ProviderProcessFactory.CreateStartInfo(fixture.Layout, registration);

            Assert.EndsWith("provider.exe", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(install, startInfo.WorkingDirectory);
            Assert.False(startInfo.UseShellExecute);
            Assert.True(startInfo.CreateNoWindow);
            Assert.True(startInfo.RedirectStandardInput);
            Assert.True(startInfo.RedirectStandardOutput);
            Assert.True(startInfo.RedirectStandardError);
            Assert.DoesNotContain("MCSV_PROVIDER_TEST_SECRET", startInfo.Environment.Keys);
            Assert.Equal(ProductApiProtocol.CurrentVersion.ToString(),
                startInfo.Environment["MCSV_PROVIDER_API_VERSION"]);
            Assert.Equal("--mcsv-provider-rpc", startInfo.ArgumentList[0]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCSV_PROVIDER_TEST_SECRET", previous);
        }
    }

    [Fact]
    public void ProcessStartInfo_RejectsPostInstallPayloadTamperingAndExtraFiles()
    {
        using var fixture = new RegistryFixture();
        fixture.Layout.EnsureCreated();
        var install = CreateInstalledProviderFiles(fixture);
        File.WriteAllText(Path.Combine(install, "bin", "provider.exe"), "tampered");

        Assert.Throws<CryptographicException>(() =>
            ProviderProcessFactory.CreateStartInfo(fixture.Layout, Registration(enabled: true)));

        File.WriteAllText(Path.Combine(install, "bin", "provider.exe"), "binary placeholder");
        File.WriteAllText(Path.Combine(install, "unexpected.dll"), "unexpected");
        Assert.Throws<InvalidDataException>(() =>
            ProviderProcessFactory.CreateStartInfo(fixture.Layout, Registration(enabled: true)));
    }

    [Fact]
    public void ProcessIsolationOptions_RejectUnsafeResourceLimits()
    {
        using var fixture = new RegistryFixture();

        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderProcessFactory(
            fixture.Layout,
            new ProviderProcessIsolationOptions(8 * 1024 * 1024, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderProcessFactory(
            fixture.Layout,
            new ProviderProcessIsolationOptions(128 * 1024 * 1024, 8)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderProcessFactory(
            fixture.Layout,
            new ProviderProcessIsolationOptions(128 * 1024 * 1024, 1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderProcessFactory(
            fixture.Layout,
            new ProviderProcessIsolationOptions(128 * 1024 * 1024, 1, 101)));
    }

    [Fact]
    public void RegistrationValidator_RejectsManifestWithUnknownPermission()
    {
        using var fixture = new RegistryFixture();
        var manifest = ValidManifest() with
        {
            Permissions = [ProductProviderPermissions.Http, "provider.unknown"],
        };

        Assert.Throws<InvalidDataException>(() =>
            ProviderRegistrationValidator.ValidateAndThrow(
                Registration(enabled: false) with { Manifest = manifest },
                fixture.Layout));
    }

    [Fact]
    public void RegistrationValidator_RejectsCrossProviderInstallPath()
    {
        using var fixture = new RegistryFixture();

        Assert.Throws<InvalidDataException>(() =>
            ProviderRegistrationValidator.ValidateAndThrow(
                Registration(enabled: false) with
                {
                    InstallRelativePath = "packages/different.provider/1.2.3",
                },
                fixture.Layout));
    }

    [Fact]
    public async Task InvocationHost_IsolatesStartFailureAndDurablyMarksProviderFailed()
    {
        using var fixture = new RegistryFixture();
        var registry = new ProviderRegistry(fixture.Layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(Registration(enabled: true));
        var host = new ProviderInvocationHost(registry, new ThrowingProcessFactory());

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.InvokeAsync(
            "example.catalog",
            new ProviderInvocationRequest(
                ProductProviderOperations.HealthGet,
                JsonSerializer.SerializeToElement(new { })),
            TimeSpan.FromSeconds(1)));

        var failed = Assert.Single(registry.GetAll());
        Assert.Equal(ProviderHealthStatus.Failed, failed.Health);
        Assert.Equal(1, failed.ConsecutiveFailures);
        Assert.Equal(
            "Provider invocation failed (InvalidOperationException).",
            Assert.IsType<string>(failed.LastError));
    }

    [Fact]
    public async Task InvocationHost_RejectsOperationTimeoutAbovePolicyBeforeStartingProcess()
    {
        using var fixture = new RegistryFixture();
        var registry = new ProviderRegistry(fixture.Layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(Registration(enabled: true));
        var factory = new CountingThrowingProcessFactory();
        var host = new ProviderInvocationHost(registry, factory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.InvokeAsync(
            "example.catalog",
            new ProviderInvocationRequest(
                ProductProviderOperations.HealthGet,
                JsonSerializer.SerializeToElement(new { })),
            TimeSpan.FromSeconds(11)));

        Assert.Equal(0, factory.CallCount);
        Assert.Equal(ProviderHealthStatus.Stopped, Assert.Single(registry.GetAll()).Health);
    }

    [Fact]
    public async Task InvocationHost_OpensCircuitAndDisablesProviderAfterRepeatedCrashes()
    {
        using var fixture = new RegistryFixture();
        var registry = new ProviderRegistry(fixture.Layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(Registration(enabled: true));
        var host = new ProviderInvocationHost(registry, new ThrowingProcessFactory());
        var request = new ProviderInvocationRequest(
            ProductProviderOperations.HealthGet,
            JsonSerializer.SerializeToElement(new { }));

        for (var attempt = 0; attempt < ProviderInvocationHost.CircuitBreakerFailureThreshold; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                host.InvokeAsync("example.catalog", request, TimeSpan.FromSeconds(1)));
        }

        var disabled = Assert.Single(registry.GetAll());
        Assert.False(disabled.IsEnabled);
        Assert.Equal(ProviderHealthStatus.Disabled, disabled.Health);
    }

    [Fact]
    public async Task InvocationHost_TotalTimeoutAlsoBoundsProviderStartup()
    {
        using var fixture = new RegistryFixture();
        var registry = new ProviderRegistry(fixture.Layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(Registration(enabled: true));
        var host = new ProviderInvocationHost(registry, new NeverStartingProcessFactory());

        await Assert.ThrowsAsync<ProviderRpcTimeoutException>(() => host.InvokeAsync(
            "example.catalog",
            new ProviderInvocationRequest(
                ProductProviderOperations.HealthGet,
                JsonSerializer.SerializeToElement(new { })),
            TimeSpan.FromMilliseconds(30)));

        var failed = Assert.Single(registry.GetAll());
        Assert.Equal(ProviderHealthStatus.Failed, failed.Health);
        Assert.Equal("Provider request timed out.", failed.LastError);
    }

    private static string CreateInstalledProviderFiles(RegistryFixture fixture)
    {
        var install = Path.Combine(fixture.Layout.Packages, "example.catalog", "1.2.3");
        Directory.CreateDirectory(Path.Combine(install, "bin"));
        File.WriteAllText(Path.Combine(install, "bin", "provider.exe"), "binary placeholder");
        File.WriteAllText(
            Path.Combine(install, ProviderPackageInstaller.ManifestFileName),
            "signed manifest placeholder");
        return install;
    }

    private static ProviderRegistration Registration(bool enabled)
    {
        var now = new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
        return new ProviderRegistration(
            ValidManifest(),
            "muhun.test-publisher",
            new string('a', 64),
            "packages/example.catalog/1.2.3",
            enabled,
            enabled ? ProviderHealthStatus.Stopped : ProviderHealthStatus.Disabled,
            now,
            now,
            0,
            null);
    }

    private static ProductProviderManifest ValidManifest() => new(
        ProductProviderManifestValidator.CurrentSchemaVersion,
        "example.catalog",
        "Example Catalog",
        "1.2.3",
        ProductApiProtocol.CurrentVersion,
        "bin/provider.exe",
        [ProductProviderCapabilities.ModpackCatalog],
        [ProductProviderPermissions.Http],
        ["api.example.com"],
        new Dictionary<string, string>
        {
            ["bin/provider.exe"] = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("binary placeholder"))).ToLowerInvariant(),
        });

    private sealed class RegistryFixture : IDisposable
    {
        public RegistryFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "mcsv-provider-registry-" + Guid.NewGuid().ToString("N"));
            Layout = new ProviderHostLayout(Path.Combine(Root, "host"));
        }

        public string Root { get; }
        public ProviderHostLayout Layout { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class ThrowingProcessFactory : IProviderProcessFactory
    {
        public ValueTask<IProviderProcess> StartAsync(
            ProviderRegistration registration,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("start failed for test");
    }

    private sealed class CountingThrowingProcessFactory : IProviderProcessFactory
    {
        public int CallCount { get; private set; }

        public ValueTask<IProviderProcess> StartAsync(
            ProviderRegistration registration,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("must not start");
        }
    }

    private sealed class NeverStartingProcessFactory : IProviderProcessFactory
    {
        public async ValueTask<IProviderProcess> StartAsync(
            ProviderRegistration registration,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}

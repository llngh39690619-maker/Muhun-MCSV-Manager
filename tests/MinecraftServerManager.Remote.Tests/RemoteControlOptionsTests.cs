using MinecraftServerManager.Remote;

namespace MinecraftServerManager.Remote.Tests;

public sealed class RemoteControlOptionsTests
{
    [Fact]
    public void Defaults_AreValidAndBounded()
    {
        var errors = RemoteControlOptionsValidator.Validate(TestOptions.Create());

        Assert.Empty(errors);
    }

    [Fact]
    public void QuickTunnel_RequiresNoGmailAllowlist_AndRejectsOneIfSupplied()
    {
        var valid = new RemoteControlOptions
        {
            PublicOrigin = new Uri("https://quiet-lake-abc123.trycloudflare.com/"),
            AllowedGoogleLogins = [],
            IngressMode = RemoteIngressMode.CloudflareQuickTunnel
        };
        Assert.Empty(RemoteControlOptionsValidator.Validate(valid));

        var invalid = new RemoteControlOptions
        {
            PublicOrigin = valid.PublicOrigin,
            AllowedGoogleLogins = ["owner@gmail.com"],
            IngressMode = RemoteIngressMode.CloudflareQuickTunnel
        };
        Assert.Contains(
            RemoteControlOptionsValidator.Validate(invalid),
            error => error.Contains("must not use a Gmail", StringComparison.Ordinal));
    }

    [Fact]
    public void NamedTunnel_AcceptsOnlyCanonicalFixedPublicHttpsOriginWithoutGmailAllowlist()
    {
        var valid = new RemoteControlOptions
        {
            PublicOrigin = new Uri("https://mcsv.example.com/"),
            AllowedGoogleLogins = [],
            IngressMode = RemoteIngressMode.CloudflareNamedTunnel
        };

        Assert.Empty(RemoteControlOptionsValidator.Validate(valid));
        Assert.True(valid.SupportsRememberedDevices);

        var withGmail = new RemoteControlOptions
        {
            PublicOrigin = valid.PublicOrigin,
            AllowedGoogleLogins = ["owner@gmail.com"],
            IngressMode = RemoteIngressMode.CloudflareNamedTunnel
        };
        Assert.Contains(
            RemoteControlOptionsValidator.Validate(withGmail),
            error => error.Contains("must not use a Gmail", StringComparison.Ordinal));
    }

    [Fact]
    public void TailscaleFunnel_AcceptsOnlyDefaultHttpsTsNetOriginWithoutGmailAndSupportsRememberedDevices()
    {
        var valid = new RemoteControlOptions
        {
            PublicOrigin = new Uri("https://manager-node.example.ts.net/"),
            AllowedGoogleLogins = [],
            IngressMode = RemoteIngressMode.TailscaleFunnel
        };

        Assert.Empty(RemoteControlOptionsValidator.Validate(valid));
        Assert.True(valid.IsPublicInternetIngress);
        Assert.True(valid.SupportsRememberedDevices);

        var invalid = new RemoteControlOptions
        {
            PublicOrigin = valid.PublicOrigin,
            AllowedGoogleLogins = ["owner@gmail.com"],
            IngressMode = RemoteIngressMode.TailscaleFunnel
        };
        Assert.Contains(
            RemoteControlOptionsValidator.Validate(invalid),
            error => error.Contains("must not use a Gmail", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://manager-node.example.ts.net:8443/")]
    [InlineData("https://manager-node.example.com/")]
    [InlineData("http://manager-node.example.ts.net/")]
    public void TailscaleFunnel_RejectsNonDefaultOrNonTsNetOrigin(string origin)
    {
        var options = new RemoteControlOptions
        {
            PublicOrigin = new Uri(origin),
            AllowedGoogleLogins = [],
            IngressMode = RemoteIngressMode.TailscaleFunnel
        };

        Assert.Contains(
            RemoteControlOptionsValidator.Validate(options),
            error => error.Contains("PublicOrigin", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://mcsv.example.com/")]
    [InlineData("https://mcsv.example.com:8443/")]
    [InlineData("https://mcsv.example.com/admin")]
    [InlineData("https://user@mcsv.example.com/")]
    [InlineData("https://quiet-lake-abc123.trycloudflare.com/")]
    [InlineData("https://mcsv.example.ts.net/")]
    [InlineData("https://localhost/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://single-label/")]
    [InlineData("https://bad_label.example.com/")]
    public void NamedTunnel_RejectsNonPublicOrNonCanonicalOrigins(string value)
    {
        var options = new RemoteControlOptions
        {
            PublicOrigin = new Uri(value),
            AllowedGoogleLogins = [],
            IngressMode = RemoteIngressMode.CloudflareNamedTunnel
        };

        Assert.Contains(
            RemoteControlOptionsValidator.Validate(options),
            error => error.Contains("PublicOrigin", StringComparison.Ordinal));
    }

    [Fact]
    public void NamedTunnel_RelativeOriginIsRejectedWithoutThrowing()
    {
        var options = new RemoteControlOptions
        {
            PublicOrigin = new Uri("/remote", UriKind.Relative),
            AllowedGoogleLogins = [],
            IngressMode = RemoteIngressMode.CloudflareNamedTunnel
        };

        var errors = RemoteControlOptionsValidator.Validate(options);

        Assert.Contains(
            errors,
            error => error.Contains("PublicOrigin", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://mcsv-test.example.ts.net")]
    [InlineData("https://mcsv-test.example.ts.net/path")]
    [InlineData("https://user@mcsv-test.example.ts.net")]
    [InlineData("https://mcsv-test.example.ts.net/?query=yes")]
    [InlineData("https://remote-control.example.com")]
    public void PublicOrigin_MustBeAnExactHttpsOrigin(string value)
    {
        var errors = RemoteControlOptionsValidator.Validate(
            TestOptions.Create(publicOrigin: new Uri(value)));

        Assert.Contains(errors, error => error.Contains("PublicOrigin", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("owner@example.com")]
    [InlineData("owner@gmail.com.attacker.example")]
    [InlineData(" owner@gmail.com")]
    [InlineData("owner+remote@gmail.com")]
    [InlineData("owner..admin@gmail.com")]
    public void Allowlist_AcceptsOnlyCanonicalExactGmailAddresses(string login)
    {
        var errors = RemoteControlOptionsValidator.Validate(
            TestOptions.Create(allowedGoogleLogins: [login]));

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void UnsafeCustomCookieName_IsRejected()
    {
        var options = new RemoteControlOptions
        {
            PublicOrigin = new Uri("https://mcsv-test.example.ts.net"),
            AllowedGoogleLogins = ["owner@gmail.com"],
            SessionCookieName = "remote"
        };

        Assert.Contains(
            RemoteControlOptionsValidator.Validate(options),
            error => error.Contains("cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InvalidRateAndCapacityBounds_AreRejected()
    {
        var options = new RemoteControlOptions
        {
            PublicOrigin = new Uri("https://mcsv-test.example.ts.net"),
            AllowedGoogleLogins = ["owner@gmail.com"],
            LoginAttemptsPerMinute = int.MaxValue,
            MaximumSessions = int.MaxValue
        };

        var errors = RemoteControlOptionsValidator.Validate(options);

        Assert.Contains(errors, error => error.Contains("Rate limits", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("MaximumSessions", StringComparison.Ordinal));
    }

    [Fact]
    public void IdempotencyLedger_MustRemainShortLivedAndBounded()
    {
        var options = new RemoteControlOptions
        {
            PublicOrigin = new Uri("https://mcsv-test.example.ts.net"),
            AllowedGoogleLogins = ["owner@gmail.com"],
            IdempotencyLifetime = TimeSpan.FromDays(2),
            MaximumIdempotencyEntries = int.MaxValue,
            MutationShutdownDrainTimeout = TimeSpan.FromHours(1)
        };

        var errors = RemoteControlOptionsValidator.Validate(options);

        Assert.Contains(errors, error => error.Contains("Idempotency lifetime", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("MaximumIdempotencyEntries", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("drain timeout", StringComparison.OrdinalIgnoreCase));
    }
}

namespace MinecraftServerManager.App.Tests;

public sealed class WpfStaTestHostSecurityTests
{
    [Fact]
    public void Validation_AcceptsExactPrivateDesktopThatIsNotVisible()
    {
        const string privateDesktop = "X-MCSV-Tests-1234-0123456789abcdef0123456789abcdef";

        WpfStaTestHost.ValidateIsolatedDesktopNames(
            privateDesktop,
            privateDesktop,
            "Default");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Default")]
    [InlineData("Winlogon")]
    [InlineData("Screen-saver")]
    [InlineData("Disconnect")]
    public void Validation_RejectsMissingOrInteractiveExpectedDesktop(string? expected)
    {
        Assert.Throws<InvalidOperationException>(() =>
            WpfStaTestHost.ValidateIsolatedDesktopNames(expected, "Default", "Default"));
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Winlogon")]
    [InlineData("Screen-saver")]
    [InlineData("Disconnect")]
    [InlineData("X-MCSV-Tests-Forged")]
    public void Validation_RejectsForgedEnvironmentOnInteractiveDesktop(string expected)
    {
        Assert.Throws<InvalidOperationException>(() =>
            WpfStaTestHost.ValidateIsolatedDesktopNames(expected, "Default", "Default"));
    }

    [Fact]
    public void Validation_RejectsPrivateDesktopWhenItIsTheVisibleInputDesktop()
    {
        const string privateDesktop = "X-MCSV-Tests-1234-0123456789abcdef0123456789abcdef";

        Assert.Throws<InvalidOperationException>(() =>
            WpfStaTestHost.ValidateIsolatedDesktopNames(
                privateDesktop,
                privateDesktop,
                privateDesktop));
    }

    [Fact]
    public void Validation_RejectsPrivateDesktopNameMismatch()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WpfStaTestHost.ValidateIsolatedDesktopNames(
                "X-MCSV-Tests-1234-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "X-MCSV-Tests-1234-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "Default"));
    }
}

using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Tests;

public sealed class RemoteControlSettingsTests
{
    [Fact]
    public void Defaults_AutoStartCompletedLoopbackConfiguration()
    {
        var settings = new RemoteControlSettings();

        Assert.True(settings.Enabled);
        Assert.Equal(RemoteControlSettings.DefaultLocalPort, settings.LocalPort);
        Assert.Equal(string.Empty, settings.AllowedLogin);
        Assert.Equal(string.Empty, settings.CloudflareNamedPublicOrigin);
    }

    [Fact]
    public void FunnelMode_IsAppendedWithoutChangingPersistedModeValues()
    {
        Assert.Equal(0, (int)RemoteAccessMode.Tailscale);
        Assert.Equal(1, (int)RemoteAccessMode.CloudflareQuickTunnel);
        Assert.Equal(2, (int)RemoteAccessMode.CloudflareNamedTunnel);
        Assert.Equal(3, (int)RemoteAccessMode.TailscaleFunnel);
    }

    [Fact]
    public void Copy_DoesNotAliasMutableSettingsObject()
    {
        var original = new RemoteControlSettings
        {
            Enabled = true,
            AllowedLogin = "owner@example.com",
            LocalPort = 41049,
            AccessMode = RemoteAccessMode.CloudflareNamedTunnel,
            CloudflaredExecutablePath = @"C:\Tools\cloudflared.exe",
            CloudflareNamedPublicOrigin = "https://mcsv.example.com/"
        };

        var copy = original.Copy();
        copy.AllowedLogin = "other@example.com";

        Assert.True(copy.Enabled);
        Assert.Equal(41049, copy.LocalPort);
        Assert.Equal(RemoteAccessMode.CloudflareNamedTunnel, copy.AccessMode);
        Assert.Equal(@"C:\Tools\cloudflared.exe", copy.CloudflaredExecutablePath);
        Assert.Equal("https://mcsv.example.com/", copy.CloudflareNamedPublicOrigin);
        Assert.Equal("owner@example.com", original.AllowedLogin);
    }

    [Fact]
    public void ManagerSettings_UsesNewRemoteSettingsInstance()
    {
        var first = new ManagerSettings();
        var second = new ManagerSettings();

        first.RemoteControl.Enabled = true;

        Assert.Equal(ManagerSettings.CurrentSchemaVersion, first.SchemaVersion);
        Assert.True(second.RemoteControl.Enabled);
    }
}

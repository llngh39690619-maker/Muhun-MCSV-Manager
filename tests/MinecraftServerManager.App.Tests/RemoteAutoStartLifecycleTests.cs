using System.IO;
using System.Runtime.CompilerServices;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.App.ViewModels;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.App.Tests;

public sealed class RemoteAutoStartLifecycleTests
{
    [Fact]
    public void LegacyDisabledSetting_MigratesOnceToSchemaNineAutoStart()
    {
        var settings = new ManagerSettings
        {
            SchemaVersion = 8,
            RemoteControl = new RemoteControlSettings { Enabled = false }
        };

        Assert.True(MainWindowViewModel.ApplyRemoteAutoStartMigration(settings));
        Assert.Equal(ManagerSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.True(settings.RemoteControl.Enabled);
        Assert.False(MainWindowViewModel.ApplyRemoteAutoStartMigration(settings));
    }

    [Fact]
    public void TailscaleAutoStart_RequiresCanonicalGmailAndValidPort()
    {
        var settings = new RemoteControlSettings
        {
            AccessMode = RemoteAccessMode.Tailscale,
            AllowedLogin = "owner.test@gmail.com",
            LocalPort = RemoteControlSettings.DefaultLocalPort
        };

        Assert.True(MainWindowViewModel.IsRemoteAccessConfigurationComplete(settings));

        settings.AllowedLogin = " owner.test@gmail.com ";
        Assert.False(MainWindowViewModel.IsRemoteAccessConfigurationComplete(settings));

        settings.AllowedLogin = "owner.test@gmail.com";
        settings.LocalPort = 80;
        Assert.False(MainWindowViewModel.IsRemoteAccessConfigurationComplete(settings));
    }

    [Fact]
    public void FunnelAutoStart_RequiresOnlyValidLocalPort()
    {
        var settings = new RemoteControlSettings
        {
            Enabled = true,
            AccessMode = RemoteAccessMode.TailscaleFunnel,
            AllowedLogin = string.Empty,
            CloudflaredExecutablePath = string.Empty,
            CloudflareNamedPublicOrigin = string.Empty,
            LocalPort = RemoteControlSettings.DefaultLocalPort
        };

        Assert.True(MainWindowViewModel.IsRemoteAccessConfigurationComplete(settings));

        settings.AllowedLogin = "not-a-gmail";
        Assert.True(MainWindowViewModel.IsRemoteAccessConfigurationComplete(settings));

        settings.LocalPort = 80;
        Assert.False(MainWindowViewModel.IsRemoteAccessConfigurationComplete(settings));
    }

    [Fact]
    public async Task QuickTunnelAutoStart_RequiresExistingFullyQualifiedCloudflaredExecutable()
    {
        using var temporary = new TestDirectory();
        var executable = Path.Combine(temporary.Path, "cloudflared.exe");
        await File.WriteAllBytesAsync(executable, []);
        var settings = new RemoteControlSettings
        {
            AccessMode = RemoteAccessMode.CloudflareQuickTunnel,
            CloudflaredExecutablePath = executable,
            LocalPort = RemoteControlSettings.DefaultLocalPort
        };

        Assert.True(MainWindowViewModel.IsRemoteAccessConfigurationComplete(settings));

        settings.CloudflaredExecutablePath = "cloudflared.exe";
        Assert.False(MainWindowViewModel.IsRemoteAccessConfigurationComplete(settings));

        settings.CloudflaredExecutablePath = Path.Combine(temporary.Path, "renamed.exe");
        await File.WriteAllBytesAsync(settings.CloudflaredExecutablePath, []);
        Assert.False(MainWindowViewModel.IsRemoteAccessConfigurationComplete(settings));

        settings.CloudflaredExecutablePath = Path.Combine(temporary.Path, "missing", "cloudflared.exe");
        Assert.False(MainWindowViewModel.IsRemoteAccessConfigurationComplete(settings));
    }

    [Fact]
    public async Task NamedTunnelAutoStart_RequiresExecutableAndValidFixedHttpsOrigin()
    {
        using var temporary = new TestDirectory();
        var executable = Path.Combine(temporary.Path, "cloudflared.exe");
        await File.WriteAllBytesAsync(executable, []);
        var settings = new RemoteControlSettings
        {
            Enabled = true,
            AccessMode = RemoteAccessMode.CloudflareNamedTunnel,
            CloudflaredExecutablePath = executable,
            CloudflareNamedPublicOrigin = "https://mc.example.com",
            LocalPort = RemoteControlSettings.DefaultLocalPort
        };

        Assert.True(MainWindowViewModel.IsRemoteAccessConfigurationComplete(settings));

        settings.CloudflareNamedPublicOrigin = "http://mc.example.com";
        Assert.False(MainWindowViewModel.IsRemoteAccessConfigurationComplete(settings));

        settings.CloudflareNamedPublicOrigin = "https://mc.example.com/path";
        Assert.False(MainWindowViewModel.IsRemoteAccessConfigurationComplete(settings));

        settings.CloudflareNamedPublicOrigin = "https://quiet-lake.trycloudflare.com";
        Assert.False(MainWindowViewModel.IsRemoteAccessConfigurationComplete(settings));

        settings.CloudflareNamedPublicOrigin = "https://mc.example.com";
        settings.CloudflaredExecutablePath = Path.Combine(temporary.Path, "missing", "cloudflared.exe");
        Assert.False(MainWindowViewModel.IsRemoteAccessConfigurationComplete(settings));
    }

    [Fact]
    public async Task ApplicationExitRemoteCleanup_IsIdempotentBeforeAndAfterDispose()
    {
        using var temporary = new TestDirectory();
        var viewModel = new MainWindowViewModel(new ApplicationPaths(temporary.Path));

        var first = viewModel.EnsureRemoteAccessStoppedForApplicationExitAsync();
        var second = viewModel.EnsureRemoteAccessStoppedForApplicationExitAsync();

        Assert.Same(first, second);
        await first;
        await viewModel.DisposeAsync();
        Assert.Same(first, viewModel.EnsureRemoteAccessStoppedForApplicationExitAsync());
    }

    [Fact]
    public async Task ShutdownFailure_DoesNotPoisonCachedTaskAndRetryCompletes()
    {
        using var temporary = new TestDirectory();
        var paths = new ApplicationPaths(temporary.Path);
        paths.EnsureCreated();
        // A directory at manager.json makes the first atomic settings rename fail without
        // damaging any user data. Removing it models the transient blocker being resolved.
        Directory.CreateDirectory(paths.SettingsFile);
        var viewModel = new MainWindowViewModel(paths);

        var failedAttempt = viewModel.ShutdownAsync();
        var failure = await Record.ExceptionAsync(() => failedAttempt);
        Assert.True(
            failure is IOException or UnauthorizedAccessException,
            $"Expected a settings path I/O failure, but got {failure?.GetType().FullName ?? "no exception"}.");

        Directory.Delete(paths.SettingsFile);
        var retry = viewModel.ShutdownAsync();

        Assert.NotSame(failedAttempt, retry);
        await retry.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(retry, viewModel.ShutdownAsync());
        await viewModel.DisposeAsync();
    }

    [Fact]
    public void MainLifecycleSource_KeepsAutoStartFailSoftAndConsoleStopSessionOnly()
    {
        var source = File.ReadAllText(GetAppSourcePath(Path.Combine(
            "ViewModels",
            "MainWindowViewModel.cs")));
        var initialize = ExtractMethod(source, "private async Task<string?> InitializeRemoteAccessAsync(");
        var start = ExtractMethod(source, "private async Task StartQuickWebFromConsoleAsync()");
        var stop = ExtractMethod(source, "private async Task StopQuickWebFromConsoleAsync()");
        var persist = ExtractMethod(source, "private async Task PersistRemoteAccessSettingsAsync(");

        Assert.Contains("if (!IsRemoteAccessConfigurationComplete", initialize, StringComparison.Ordinal);
        Assert.Contains("HasCloudflareNamedTunnelToken", initialize, StringComparison.Ordinal);
        Assert.Contains("coordinator.StartAsync(", initialize, StringComparison.Ordinal);
        Assert.Contains("_applicationShutdownCancellation.Token", initialize, StringComparison.Ordinal);
        Assert.Contains("catch (Exception error)", initialize, StringComparison.Ordinal);

        Assert.Contains("settings.Enabled = true", start, StringComparison.Ordinal);
        Assert.Contains("RemoteAccessMode.TailscaleFunnel", start, StringComparison.Ordinal);
        Assert.Contains("PersistRemoteAccessSettingsAsync(settings)", start, StringComparison.Ordinal);
        Assert.Contains("_applicationShutdownCancellation.Token", start, StringComparison.Ordinal);

        Assert.Contains("_remoteAccessSessionState.MarkStoppedForCurrentRun()", stop, StringComparison.Ordinal);
        Assert.Contains("coordinator.StopAsync", stop, StringComparison.Ordinal);
        Assert.DoesNotContain("PersistRemoteAccessSettingsAsync", stop, StringComparison.Ordinal);
        Assert.DoesNotContain("Enabled = false", stop, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearForExplicitReconnect", persist, StringComparison.Ordinal);
    }

    [Fact]
    public void AppLifecycleSource_CleansInitializationFailureAndUsesBoundedOnExitFallback()
    {
        var source = File.ReadAllText(GetAppSourcePath("App.xaml.cs"));
        var onExit = ExtractMethod(source, "protected override void OnExit(ExitEventArgs e)");
        var exitCleanup = ExtractMethod(source, "private void StopRemoteAccessForProcessExit()");

        Assert.Contains("StopRemoteAccessForProcessExit();", onExit, StringComparison.Ordinal);
        Assert.Contains("EnsureRemoteAccessStoppedForApplicationExitAsync", exitCleanup, StringComparison.Ordinal);
        Assert.Contains("ExitRemoteCleanupTimeout", exitCleanup, StringComparison.Ordinal);
        Assert.Contains("await failedViewModel.DisposeAsync()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteSettingsLifecycleSource_LinksMutatingOperationsToApplicationShutdown()
    {
        var source = File.ReadAllText(GetAppSourcePath(Path.Combine(
            "ViewModels",
            "RemoteAccessSettingsViewModel.cs")));
        var apply = ExtractMethod(source, "private async Task ApplyAsync()");
        var stop = ExtractMethod(source, "private async Task StopAsync()");
        var register = ExtractMethod(source, "private async Task RegisterAccountAsync()");

        Assert.Contains("_applicationStopping.ThrowIfCancellationRequested()", apply, StringComparison.Ordinal);
        Assert.Contains("StartAsync(settings, _applicationStopping)", apply, StringComparison.Ordinal);
        Assert.Contains("_applicationStopping.ThrowIfCancellationRequested()", stop, StringComparison.Ordinal);
        Assert.Contains("_applicationStopping", stop, StringComparison.Ordinal);
        Assert.Contains("RegisterLocalApprovedAccountAsync(", register, StringComparison.Ordinal);
        Assert.Contains("RegisterApprovedAccountAsync(", register, StringComparison.Ordinal);
        Assert.Contains("StartAsync(settings, _applicationStopping)", register, StringComparison.Ordinal);
        Assert.True(
            register.Split(
                    "_applicationStopping",
                    StringSplitOptions.None)
                .Length >= 5,
            "Account registration and its optional Web restart must share the application shutdown token.");
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method: {signature}");
        var openingBrace = source.IndexOf('{', start);
        Assert.True(openingBrace >= 0, $"Missing method body: {signature}");
        var depth = 0;
        for (var index = openingBrace; index < source.Length; index++)
        {
            depth += source[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0
            };
            if (depth == 0)
            {
                return source[start..(index + 1)];
            }
        }

        throw new InvalidDataException($"Unterminated method: {signature}");
    }

    private static string GetAppSourcePath(string relativePath)
        => TestRepositoryPaths.AppSource(relativePath);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mcsv-remote-lifecycle-{Guid.NewGuid():N}");
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

using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MinecraftServerManager.Updater.Tests;

public sealed class ProductExplorerGuiActivationLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MuhunMCSV-ExplorerActivationTests",
        Guid.NewGuid().ToString("N"));

    public ProductExplorerGuiActivationLauncherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void StartBrokerMode_RequiresExactlyThreeOrdinalArguments()
    {
        var valid = new[]
        {
            "--start-gui-activation-broker",
            "--install-root",
            _root,
        };

        Assert.True(ProductExplorerGuiActivationLauncher.IsCommand(valid));
        Assert.True(ProductExplorerGuiActivationLauncher.IsRequest(valid));
        Assert.False(ProductExplorerGuiActivationLauncher.IsRequest(valid[..2]));
        Assert.False(ProductExplorerGuiActivationLauncher.IsRequest(
            [.. valid, "unexpected"]));
        Assert.False(ProductExplorerGuiActivationLauncher.IsRequest(
            ["--Start-gui-activation-broker", "--install-root", _root]));
        Assert.False(ProductExplorerGuiActivationLauncher.IsRequest(
            ["--start-gui-activation-broker", "--INSTALL-ROOT", _root]));
        Assert.False(ProductExplorerGuiActivationLauncher.IsRequest(
            ["--start-gui-activation-broker", "--install-root", " "]));
        Assert.Equal(2, ProductExplorerGuiActivationLauncher.Run(valid[..2], new CapturingLauncher()));
    }

    [Fact]
    public void ManagedPolicy_AlwaysTargetsExactStableLauncherAndBrokerMode()
    {
        var installRoot = CreateManagedInstallRoot("install root with spaces");

        var command = ProductExplorerGuiActivationLauncher.CreateLaunchCommand(installRoot);

        Assert.Equal(
            Path.Combine(installRoot, "launcher", "Muhun MCSV Updater.exe"),
            command.ExecutablePath,
            ignoreCase: true);
        Assert.Equal(Path.Combine(installRoot, "launcher"), command.WorkingDirectory, ignoreCase: true);
        Assert.Equal(
            $"--gui-activation-broker --install-root \"{installRoot}\"",
            command.Arguments);
        Assert.Equal(0, command.WindowStyle);
        Assert.NotEqual(Environment.ProcessPath, command.ExecutablePath);
    }

    [Fact]
    public void ManagedPolicy_RejectsRelativeUnmarkedAndMissingStableLauncherPaths()
    {
        Assert.Throws<InvalidDataException>(() =>
            ProductExplorerGuiActivationLauncher.CreateLaunchCommand("relative-install"));
        Assert.Throws<InvalidDataException>(() =>
            ProductExplorerGuiActivationLauncher.CreateLaunchCommand(@"\\server\share\MuhunMCSV"));

        var unmarked = Path.Combine(_root, "unmarked");
        Directory.CreateDirectory(Path.Combine(unmarked, "launcher"));
        File.WriteAllText(Path.Combine(unmarked, "launcher", "Muhun MCSV Updater.exe"), "test");
        Assert.Throws<InvalidDataException>(() =>
            ProductExplorerGuiActivationLauncher.CreateLaunchCommand(unmarked));

        var missingLauncher = Path.Combine(_root, "missing-launcher");
        Directory.CreateDirectory(missingLauncher);
        File.WriteAllText(
            Path.Combine(missingLauncher, ".muhun-mcsv-install-root"),
            "muhun.mcsv.manager:1\n");
        Assert.Throws<InvalidDataException>(() =>
            ProductExplorerGuiActivationLauncher.CreateLaunchCommand(missingLauncher));
    }

    [Fact]
    public void Run_DispatchesValidatedStableCommandAndReturnsNonzeroOnOrdinaryFailure()
    {
        var installRoot = CreateManagedInstallRoot("dispatch");
        var args = new[]
        {
            "--start-gui-activation-broker",
            "--install-root",
            installRoot,
        };
        var capturing = new CapturingLauncher();

        Assert.Equal(0, ProductExplorerGuiActivationLauncher.Run(args, capturing));
        Assert.NotNull(capturing.Command);
        Assert.Equal(
            Path.Combine(installRoot, "launcher", "Muhun MCSV Updater.exe"),
            capturing.Command!.ExecutablePath,
            ignoreCase: true);

        Assert.Equal(
            4,
            ProductExplorerGuiActivationLauncher.Run(
                args,
                new ThrowingLauncher(new InvalidOperationException("Explorer unavailable"))));
    }

    [Fact]
    public void Run_DoesNotSwallowFatalRuntimeExceptions()
    {
        var installRoot = CreateManagedInstallRoot("fatal");
        var args = new[]
        {
            "--start-gui-activation-broker",
            "--install-root",
            installRoot,
        };

        Assert.Throws<OutOfMemoryException>(() =>
            ProductExplorerGuiActivationLauncher.Run(
                args,
                new ThrowingLauncher(new OutOfMemoryException("fatal"))));
        Assert.Throws<StackOverflowException>(() =>
            ProductExplorerGuiActivationLauncher.Run(
                args,
                new ThrowingLauncher(new StackOverflowException("fatal"))));
    }

    [Fact]
    public async Task ExplorerDesktopIntegration_LaunchesAsSameUnelevatedInteractiveUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var current = Process.GetCurrentProcess();
        if (current.SessionId <= 0 || !HasExplorerInSession(current.SessionId))
        {
            // Headless build agents have no desktop automation object to validate.
            return;
        }

        var pwsh = ResolvePowerShellExecutable();
        if (pwsh is null)
        {
            return;
        }

        var nonce = Guid.NewGuid().ToString("N");
        var outputPath = Path.Combine(_root, $"explorer-proof-{nonce}.json");
        var escapedOutputPath = outputPath.Replace("'", "''", StringComparison.Ordinal);
        var script = string.Concat(
            "$i=[Security.Principal.WindowsIdentity]::GetCurrent();",
            "$p=[Security.Principal.WindowsPrincipal]::new($i);",
            "$o=[ordered]@{nonce='", nonce,
            "';sid=$i.User.Value;sessionId=[Diagnostics.Process]::GetCurrentProcess().SessionId;",
            "administrator=$p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)};",
            "$j=$o|ConvertTo-Json -Compress;",
            "$f='", escapedOutputPath, "';$t=$f+'.tmp';",
            "[IO.File]::WriteAllText($t,$j,[Text.UTF8Encoding]::new($false));",
            "[IO.File]::Move($t,$f)");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var command = new ProductExplorerLaunchCommand(
            pwsh,
            $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}",
            _root,
            0);

        new ExplorerDesktopProcessLauncher().Launch(command);

        ExplorerIdentityProof? proof = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(outputPath))
            {
                proof = JsonSerializer.Deserialize<ExplorerIdentityProof>(
                    await File.ReadAllTextAsync(outputPath),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
                break;
            }

            await Task.Delay(50);
        }

        Assert.NotNull(proof);
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(nonce, proof!.Nonce);
        Assert.Equal(identity.User?.Value, proof.Sid);
        Assert.Equal(current.SessionId, proof.SessionId);
        Assert.False(proof.Administrator);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("embedded\"quote", "\"embedded\\\"quote\"")]
    public void WindowsArgumentQuoting_PreservesOneArgument(string value, string expected)
    {
        Assert.Equal(expected, ProductExplorerGuiActivationLauncher.QuoteWindowsArgument(value));
    }

    [Fact]
    public void WindowsArgumentQuoting_DoublesTrailingSlashesBeforeClosingQuote()
    {
        var value = "ends with slash " + '\\';
        var expected = "\"ends with slash " + "\\\\" + "\"";

        Assert.Equal(expected, ProductExplorerGuiActivationLauncher.QuoteWindowsArgument(value));
    }

    private string CreateManagedInstallRoot(string name)
    {
        var installRoot = Path.Combine(_root, name);
        var launcherDirectory = Path.Combine(installRoot, "launcher");
        Directory.CreateDirectory(launcherDirectory);
        File.WriteAllText(
            Path.Combine(installRoot, ".muhun-mcsv-install-root"),
            "muhun.mcsv.manager:1\n");
        File.WriteAllText(
            Path.Combine(launcherDirectory, "Muhun MCSV Updater.exe"),
            "test executable");
        return installRoot;
    }

    private static bool HasExplorerInSession(int sessionId)
    {
        var explorers = Process.GetProcessesByName("explorer");
        try
        {
            return explorers.Any(process => !process.HasExited && process.SessionId == sessionId);
        }
        finally
        {
            foreach (var process in explorers)
            {
                process.Dispose();
            }
        }
    }

    private static string? ResolvePowerShellExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell",
                "7",
                "pwsh.exe"),
            Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private sealed record ExplorerIdentityProof(
        string Nonce,
        string Sid,
        int SessionId,
        bool Administrator);

    private sealed class CapturingLauncher : IExplorerDesktopProcessLauncher
    {
        public ProductExplorerLaunchCommand? Command { get; private set; }

        public void Launch(ProductExplorerLaunchCommand command) => Command = command;
    }

    private sealed class ThrowingLauncher(Exception exception) : IExplorerDesktopProcessLauncher
    {
        public void Launch(ProductExplorerLaunchCommand command) => throw exception;
    }
}

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using MinecraftServerManager.App.Services;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class CloudflaredInstallationReceiptTests
{
    [Fact]
    public void Create_BindsCanonicalAssetTagSizeDigestAndUtcInstallTime()
    {
        var installedAt = new DateTimeOffset(
            2026,
            8,
            24,
            20,
            15,
            30,
            TimeSpan.FromHours(8));
        var result = new CloudflaredBootstrapResult(
            @"C:\MCSV\tools\cloudflared\cloudflared.exe",
            "2026.8.1",
            123_456,
            new string('A', 64));

        var receipt = CloudflaredInstallationReceipt.Create(result, installedAt);

        Assert.Equal("2026.8.1", receipt.ReleaseTag);
        Assert.Equal(
            CloudflaredInstallationReceipt.CanonicalAssetIdentity,
            receipt.AssetIdentity);
        Assert.Equal(123_456, receipt.Size);
        Assert.Equal(new string('a', 64), receipt.Sha256);
        Assert.Equal(installedAt.ToUniversalTime(), receipt.InstalledAtUtc);
        Assert.Equal(TimeSpan.Zero, receipt.InstalledAtUtc.Offset);
    }

    [Fact]
    public async Task Verify_AcceptsOnlyMatchingManagedExecutable()
    {
        using var fixture = new ManagedExecutableFixture("official cloudflared bytes"u8.ToArray());

        await using var verified = await CloudflaredNamedTunnelExecutableVerifier.VerifyAsync(
            fixture.ApplicationRoot,
            fixture.ExecutablePath,
            fixture.Receipt);

        Assert.Equal(fixture.ExecutablePath, verified.ExecutablePath);
    }

    [Fact]
    public async Task Verify_HoldsExecutableAgainstWriteOrReplacementUntilLeaseIsDisposed()
    {
        using var fixture = new ManagedExecutableFixture("official cloudflared bytes"u8.ToArray());
        var replacementPath = Path.Combine(fixture.Root, "replacement.exe");
        var replacementBytes = "attacker replacement bytes"u8.ToArray();
        File.WriteAllBytes(replacementPath, replacementBytes);
        var verified = await CloudflaredNamedTunnelExecutableVerifier.VerifyAsync(
            fixture.ApplicationRoot,
            fixture.ExecutablePath,
            fixture.Receipt);
        try
        {
            var replacementException = Record.Exception(() =>
                File.Move(replacementPath, fixture.ExecutablePath, overwrite: true));
            Assert.True(
                replacementException is IOException or UnauthorizedAccessException,
                $"Expected the verified executable lease to reject replacement, but observed {replacementException?.GetType().FullName ?? "no exception"}.");
            Assert.Equal(
                "official cloudflared bytes"u8.ToArray(),
                File.ReadAllBytes(fixture.ExecutablePath));
        }
        finally
        {
            await verified.DisposeAsync();
        }

        File.Move(replacementPath, fixture.ExecutablePath, overwrite: true);
        Assert.Equal(replacementBytes, File.ReadAllBytes(fixture.ExecutablePath));
    }

    [Fact]
    public async Task Verify_RejectsSameBytesOutsideManagedToolsPath()
    {
        using var fixture = new ManagedExecutableFixture("official cloudflared bytes"u8.ToArray());
        var externalPath = Path.Combine(fixture.Root, "external", "cloudflared.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(externalPath)!);
        File.Copy(fixture.ExecutablePath, externalPath);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            CloudflaredNamedTunnelExecutableVerifier.VerifyAsync(
                fixture.ApplicationRoot,
                externalPath,
                fixture.Receipt));
    }

    [Fact]
    public async Task Verify_RejectsSizeOrDigestMismatch()
    {
        using var fixture = new ManagedExecutableFixture("official cloudflared bytes"u8.ToArray());
        var wrongSize = fixture.Receipt with { Size = fixture.Receipt.Size + 1 };

        var sizeFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CloudflaredNamedTunnelExecutableVerifier.VerifyAsync(
                fixture.ApplicationRoot,
                fixture.ExecutablePath,
                wrongSize));
        Assert.Contains("大小", sizeFailure.Message, StringComparison.Ordinal);

        var wrongDigest = fixture.Receipt with { Sha256 = new string('0', 64) };
        var digestFailure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CloudflaredNamedTunnelExecutableVerifier.VerifyAsync(
                fixture.ApplicationRoot,
                fixture.ExecutablePath,
                wrongDigest));
        Assert.Contains("SHA-256", digestFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_RejectsNonCanonicalReceiptIdentityBeforeOpeningExecutable()
    {
        using var fixture = new ManagedExecutableFixture("official cloudflared bytes"u8.ToArray());
        var forged = fixture.Receipt with
        {
            AssetIdentity = "attacker.example/cloudflared:cloudflared-windows-amd64.exe"
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CloudflaredNamedTunnelExecutableVerifier.VerifyAsync(
                fixture.ApplicationRoot,
                fixture.ExecutablePath,
                forged));
    }

    [Fact]
    public async Task Verify_RejectsManagedDirectoryJunction()
    {
        using var directory = new TemporaryDirectory();
        var applicationRoot = Path.Combine(directory.Path, "app");
        var toolsRoot = Path.Combine(applicationRoot, "tools");
        var managedDirectory = Path.Combine(toolsRoot, "cloudflared");
        var outsideDirectory = Path.Combine(directory.Path, "outside");
        Directory.CreateDirectory(toolsRoot);
        Directory.CreateDirectory(outsideDirectory);
        var executablePath = Path.Combine(outsideDirectory, "cloudflared.exe");
        var bytes = "official cloudflared bytes"u8.ToArray();
        File.WriteAllBytes(executablePath, bytes);
        var receipt = CreateReceipt(executablePath, bytes);
        CreateDirectoryJunction(managedDirectory, outsideDirectory);
        try
        {
            var lexicalManagedPath = Path.Combine(managedDirectory, "cloudflared.exe");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                CloudflaredNamedTunnelExecutableVerifier.VerifyAsync(
                    applicationRoot,
                    lexicalManagedPath,
                    receipt));
        }
        finally
        {
            if (Directory.Exists(managedDirectory))
            {
                Directory.Delete(managedDirectory);
            }
        }
    }

    private static CloudflaredInstallationReceipt CreateReceipt(
        string executablePath,
        byte[] bytes)
        => CloudflaredInstallationReceipt.Create(
            new CloudflaredBootstrapResult(
                executablePath,
                "2026.8.1",
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not create test junction.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"Could not create test junction: {standardError}{standardOutput}");
        Assert.True(File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint));
    }

    private sealed class ManagedExecutableFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();

        public ManagedExecutableFixture(byte[] bytes)
        {
            Root = _directory.Path;
            ApplicationRoot = Path.Combine(Root, "app");
            ExecutablePath = Path.Combine(
                ApplicationRoot,
                "tools",
                "cloudflared",
                "cloudflared.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(ExecutablePath)!);
            File.WriteAllBytes(ExecutablePath, bytes);
            Receipt = CreateReceipt(ExecutablePath, bytes);
        }

        public string Root { get; }
        public string ApplicationRoot { get; }
        public string ExecutablePath { get; }
        public CloudflaredInstallationReceipt Receipt { get; }

        public void Dispose() => _directory.Dispose();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mcsv-cloudflared-receipt-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}

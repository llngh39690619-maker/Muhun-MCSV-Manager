using System.Text;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class MinecraftEulaAcceptanceServiceTests
{
    private static readonly DateTimeOffset AcceptanceTime =
        new(2026, 9, 1, 2, 3, 4, TimeSpan.Zero);

    [Fact]
    public async Task MissingDocument_WithoutConfirmationFailsWithoutCreatingFile()
    {
        using var owner = new TemporaryDirectory();
        var directory = System.IO.Path.Combine(owner.Path, "eula path with spaces");
        Directory.CreateDirectory(directory);
        var service = CreateService();

        await Assert.ThrowsAsync<MinecraftEulaAcceptanceRequiredException>(() =>
            service.EnsureAcceptedAsync(directory, userConfirmedAcceptance: false));

        Assert.False(File.Exists(System.IO.Path.Combine(directory, "eula.txt")));
    }

    [Fact]
    public async Task FalseDocument_WithoutConfirmationRemainsByteForByteUnchanged()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "eula.txt");
        var original = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(
                "# Mojang\r\neula=false\r\nEULA=true\r\ncustom=保留\r\n"))
            .ToArray();
        await File.WriteAllBytesAsync(path, original);
        var service = CreateService();

        await Assert.ThrowsAsync<MinecraftEulaAcceptanceRequiredException>(() =>
            service.EnsureAcceptedAsync(directory.Path, userConfirmedAcceptance: false));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "eula.txt.bak*"));
    }

    [Fact]
    public async Task ExplicitConfirmationAtomicallyWritesAndVerifiesServerRootDocument()
    {
        using var owner = new TemporaryDirectory();
        var directory = System.IO.Path.Combine(owner.Path, "service account world with spaces");
        Directory.CreateDirectory(directory);
        var unrelatedWorkingDirectory = System.IO.Path.Combine(directory, "unrelated");
        Directory.CreateDirectory(unrelatedWorkingDirectory);
        var path = System.IO.Path.Combine(directory, "eula.txt");
        await File.WriteAllTextAsync(path, "# Mojang\neula=false\ncustom=kept\n");
        var service = CreateService();

        var changed = await service.EnsureAcceptedAsync(
            directory,
            userConfirmedAcceptance: true);

        Assert.True(changed);
        var contents = await File.ReadAllTextAsync(path);
        Assert.True(MinecraftEulaDocumentEditor.IsAccepted(contents));
        Assert.Contains("custom=kept", contents, StringComparison.Ordinal);
        Assert.True(File.Exists(path + ".bak"));
        Assert.Empty(Directory.EnumerateFiles(directory, ".eula.txt.*.tmp"));
        Assert.False(File.Exists(System.IO.Path.Combine(unrelatedWorkingDirectory, "eula.txt")));
    }

    [Fact]
    public async Task AlreadyAcceptedDocumentIsNeverRewrittenOrBackedUp()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "eula.txt");
        var original = Encoding.Latin1.GetBytes("# exact bytes\r\neula=true\r\n");
        await File.WriteAllBytesAsync(path, original);
        var service = CreateService();

        var changed = await service.EnsureAcceptedAsync(
            directory.Path,
            userConfirmedAcceptance: false);

        Assert.False(changed);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "eula.txt.bak*"));
    }

    [Fact]
    public async Task DirectoryAtEulaPathFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "eula.txt"));
        var service = CreateService();

        await Assert.ThrowsAsync<IOException>(() => service.EnsureAcceptedAsync(
            directory.Path,
            userConfirmedAcceptance: true));
    }

    private static MinecraftEulaAcceptanceService CreateService()
        => new(
            new ServerPropertiesPortService(),
            new FixedTimeProvider(AcceptanceTime));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

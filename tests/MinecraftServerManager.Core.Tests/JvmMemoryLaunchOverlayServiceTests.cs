using System.Text;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Core.Services;

namespace MinecraftServerManager.Core.Tests;

public sealed class JvmMemoryLaunchOverlayServiceTests
{
    [Fact]
    public async Task ApplyAsync_ReplacesDuplicateAndQuotedMemoryLinesWithoutChangingSourceBytes()
    {
        using var server = new TemporaryDirectory();
        var sourcePath = Path.Combine(server.Path, "user_jvm_args.txt");
        var sourceBytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(
                "# keep this comment\r\n" +
                "-Xms1G\r\n" +
                "\"-Xmx4G\" # old maximum\r\n" +
                "-Dexample=value\n" +
                "-Xms2G\n"))
            .ToArray();
        await File.WriteAllBytesAsync(sourcePath, sourceBytes);
        var snapshot = CreateSnapshot(
            server.Path,
            ["user_jvm_args.txt", "libraries/net/minecraftforge/args.txt"]);
        var service = new JvmMemoryLaunchOverlayService();

        var generatedPath = await service.ApplyAsync(snapshot, 2048, 6144);

        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(sourcePath));
        var generated = await File.ReadAllTextAsync(generatedPath);
        Assert.StartsWith($"-Xms2048M{Environment.NewLine}-Xmx6144M{Environment.NewLine}", generated);
        Assert.Contains("# keep this comment\r\n", generated);
        Assert.Contains("-Dexample=value\n", generated);
        Assert.DoesNotContain("-Xms1G", generated);
        Assert.DoesNotContain("-Xms2G", generated);
        Assert.DoesNotContain("-Xmx4G", generated);
        Assert.Equal(
            JvmMemoryLaunchOverlayService.RuntimeArgumentFileRelativePath,
            snapshot.JavaArgumentFilePaths[0]);
        Assert.Equal("libraries/net/minecraftforge/args.txt", snapshot.JavaArgumentFilePaths[1]);
    }

    [Fact]
    public async Task ApplyAsync_RejectsRootEscapeBeforeWritingOrMutatingSnapshot()
    {
        using var server = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(outside.Path, "user_jvm_args.txt"),
            "-Xmx16G");
        var escapingPath = Path.GetRelativePath(
            server.Path,
            Path.Combine(outside.Path, "user_jvm_args.txt"));
        var snapshot = CreateSnapshot(server.Path, [escapingPath, "loader.args"]);
        var originalPaths = snapshot.JavaArgumentFilePaths.ToArray();
        var service = new JvmMemoryLaunchOverlayService();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.ApplyAsync(snapshot, 1024, 2048));

        Assert.Equal(originalPaths, snapshot.JavaArgumentFilePaths);
        Assert.False(Directory.Exists(Path.Combine(server.Path, ".mcsv-runtime")));
    }

    [Fact]
    public async Task ApplyAsync_RejectsOversizedSourceBeforeWritingOrMutatingSnapshot()
    {
        using var server = new TemporaryDirectory();
        var sourcePath = Path.Combine(server.Path, "user_jvm_args.txt");
        await using (var stream = new FileStream(
                         sourcePath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 1,
                         useAsync: true))
        {
            stream.SetLength(JvmMemoryLaunchOverlayService.MaximumSourceArgumentFileBytes + 1L);
        }

        var snapshot = CreateSnapshot(server.Path, ["user_jvm_args.txt", "loader.args"]);
        var originalPaths = snapshot.JavaArgumentFilePaths.ToArray();
        var service = new JvmMemoryLaunchOverlayService();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ApplyAsync(snapshot, 1024, 2048));

        Assert.Equal(originalPaths, snapshot.JavaArgumentFilePaths);
        Assert.False(Directory.Exists(Path.Combine(server.Path, ".mcsv-runtime")));
    }

    [Fact]
    public async Task ApplyAsync_AtomicallyReplacesExistingOverlayAndRemainsIdempotent()
    {
        using var server = new TemporaryDirectory();
        var sourcePath = Path.Combine(server.Path, "user_jvm_args.txt");
        var originalBytes = Encoding.UTF8.GetBytes("-Dkept=true\n-Xmx1G\n");
        await File.WriteAllBytesAsync(sourcePath, originalBytes);
        var snapshot = CreateSnapshot(server.Path, ["user_jvm_args.txt", "loader.args"]);
        var service = new JvmMemoryLaunchOverlayService();

        var firstPath = await service.ApplyAsync(snapshot, 1024, 2048);
        var secondPath = await service.ApplyAsync(snapshot, 3072, 5120);

        Assert.Equal(firstPath, secondPath);
        var generated = await File.ReadAllTextAsync(secondPath);
        Assert.Contains("-Xms3072M", generated);
        Assert.Contains("-Xmx5120M", generated);
        Assert.DoesNotContain("-Xms1024M", generated);
        Assert.DoesNotContain("-Xmx2048M", generated);
        Assert.Equal(1, CountOccurrences(generated, "-Xms"));
        Assert.Equal(1, CountOccurrences(generated, "-Xmx"));
        Assert.Contains("-Dkept=true", generated);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(sourcePath));
        Assert.Equal(
            [JvmMemoryLaunchOverlayService.RuntimeArgumentFileRelativePath, "loader.args"],
            snapshot.JavaArgumentFilePaths);
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(secondPath)!,
            "*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ApplyAsync_WithoutUserFileInsertsMemoryOverlayBeforeLoaderArguments()
    {
        using var server = new TemporaryDirectory();
        var snapshot = CreateSnapshot(server.Path, ["loader.args"]);
        var service = new JvmMemoryLaunchOverlayService();

        var generatedPath = await service.ApplyAsync(snapshot, 1024, 4096);

        Assert.True(File.Exists(generatedPath));
        Assert.Equal(
            [JvmMemoryLaunchOverlayService.RuntimeArgumentFileRelativePath, "loader.args"],
            snapshot.JavaArgumentFilePaths);
    }

    private static ServerInstance CreateSnapshot(string root, List<string> argumentFiles)
        => new()
        {
            DirectoryPath = root,
            LaunchKind = ServerLaunchKind.JavaArgumentFiles,
            JavaArgumentFilePaths = argumentFiles
        };

    private static int CountOccurrences(string text, string value)
        => text.Split(value, StringSplitOptions.None).Length - 1;
}

using System.Diagnostics;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class MinecraftClientJavaExecutableProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-java-probe-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(8)]
    [InlineData(17)]
    [InlineData(21)]
    [InlineData(25)]
    public async Task ProbeMajorVersionAsync_UsesExecutableOutputInsteadOfPathText(int reportedMajor)
    {
        var java = CreateJava("java-99-from-folder-name", "java.exe");
        string? probedPath = null;
        var probe = new MinecraftClientJavaExecutableProbe(
            (path, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                probedPath = path;
                return Task.FromResult(reportedMajor);
            },
            TimeSpan.FromSeconds(1));

        var actual = await probe.ProbeMajorVersionAsync(java);

        Assert.Equal(reportedMajor, actual);
        Assert.Equal(Path.GetFullPath(java), probedPath);
    }

    [Fact]
    public async Task ProbeMajorVersionAsync_TimeoutCancelsTheBoundedProbe()
    {
        var java = CreateJava("slow-runtime", "javaw.exe");
        var observedCancellation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new MinecraftClientJavaExecutableProbe(
            async (_, cancellationToken) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("unreachable");
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        observedCancellation.TrySetResult();
                    }
                }
            },
            TimeSpan.FromMilliseconds(50));
        var stopwatch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            probe.ProbeMajorVersionAsync(java));

        Assert.Contains("within 0.1 seconds", error.Message, StringComparison.Ordinal);
        await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ProbeMajorVersionAsync_InvalidOutputFailureIsNotConvertedIntoAVersionGuess()
    {
        var java = CreateJava("java-21-misleading", "java.exe");
        var probe = new MinecraftClientJavaExecutableProbe(
            (_, _) => throw new InvalidDataException("unparseable java -version output"),
            TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            probe.ProbeMajorVersionAsync(java));

        Assert.Contains("unparseable", error.Message, StringComparison.Ordinal);
    }

    private string CreateJava(string runtimeName, string executableName)
    {
        var path = Path.Combine(_root, runtimeName, "bin", executableName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x4D, 0x5A]);
        return Path.GetFullPath(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

using MinecraftServerManager.Core.Providers;

namespace MinecraftServerManager.Core.Tests;

public sealed class AdoptiumRuntimeConcurrencyTests
{
    [Fact]
    public async Task RuntimeInstallGate_SerializesTheSameDestinationAcrossCallers()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "runtimes", "temurin-jdk-25-test");
        using var first = await AdoptiumRuntimeProvider.AcquireRuntimeInstallGateAsync(destination);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAcquired = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var second = Task.Run(async () =>
        {
            secondEntered.SetResult();
            using var lease = await AdoptiumRuntimeProvider.AcquireRuntimeInstallGateAsync(destination);
            secondAcquired.SetResult();
        });

        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.False(secondAcquired.Task.IsCompleted);

        first.Dispose();
        await secondAcquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RuntimeInstallGate_DoesNotSerializeDifferentDestinations()
    {
        using var directory = new TemporaryDirectory();
        var firstDestination = Path.Combine(directory.Path, "runtimes", "temurin-jdk-21-test");
        var secondDestination = Path.Combine(directory.Path, "runtimes", "temurin-jdk-25-test");

        using var first = await AdoptiumRuntimeProvider.AcquireRuntimeInstallGateAsync(firstDestination);
        using var second = await AdoptiumRuntimeProvider.AcquireRuntimeInstallGateAsync(secondDestination)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
    }
}

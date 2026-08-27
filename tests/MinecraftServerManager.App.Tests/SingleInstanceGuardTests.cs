using System.IO;
using MinecraftServerManager.App.Services;

namespace MinecraftServerManager.App.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void SamePortableDirectory_CannotBeAcquiredByTwoProcessThreads()
    {
        var applicationDirectory = Path.Combine(
            Path.GetTempPath(),
            $"mcsv-single-instance-{Guid.NewGuid():N}");
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? ownerFailure = null;
        var owner = new Thread(() =>
        {
            try
            {
                using var guard = SingleInstanceGuard.TryAcquire(applicationDirectory)
                    ?? throw new InvalidOperationException("Initial guard acquisition failed.");
                acquired.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Test owner was not released.");
                }
            }
            catch (Exception exception)
            {
                ownerFailure = exception;
                acquired.Set();
            }
        });

        owner.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));
        Assert.Null(ownerFailure);
        Assert.Null(SingleInstanceGuard.TryAcquire(applicationDirectory + Path.DirectorySeparatorChar));

        release.Set();
        Assert.True(owner.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(ownerFailure);
        using (var reacquired = SingleInstanceGuard.TryAcquire(applicationDirectory))
        {
            Assert.NotNull(reacquired);
        }

        Assert.False(File.Exists(Path.Combine(
            applicationDirectory,
            SingleInstanceGuard.LockFileName)));
    }
}

using MinecraftServerManager.Core.Runtime;

namespace MinecraftServerManager.Core.Tests.Runtime;

public sealed class ServerDirectoryLeaseTests
{
    [Fact]
    public void Acquire_BlocksAnotherLeaseUntilDisposed()
    {
        using var temporary = new TemporaryDirectory();
        using (ServerDirectoryLease.Acquire(temporary.Path))
        {
            Assert.Throws<ServerDirectoryLockException>(
                () => ServerDirectoryLease.Acquire(temporary.Path));
        }

        using var replacement = ServerDirectoryLease.Acquire(temporary.Path);
        Assert.True(File.Exists(Path.Combine(
            temporary.Path,
            ".minecraft-server-manager.lock")));
    }
}

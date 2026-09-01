using MinecraftServerManager.Core.Services;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductServerListenerStateReaderTests
{
    [Fact]
    public void ReaderCachesOneSnapshotForAllPortsThenRefreshesAfterExpiry()
    {
        var time = new MutableTimeProvider();
        var captures = 0;
        var ports = new HashSet<int> { 25566 };
        var reader = new ProductServerListenerStateReader(
            () =>
            {
                captures++;
                return new PortOccupancySnapshot(ports, new HashSet<int>());
            },
            time);

        Assert.True(reader.TryIsListening(25566));
        Assert.False(reader.TryIsListening(25565));
        Assert.Equal(1, captures);

        ports = new HashSet<int> { 25565 };
        time.Advance(TimeSpan.FromMilliseconds(751));

        Assert.True(reader.TryIsListening(25565));
        Assert.False(reader.TryIsListening(25566));
        Assert.Equal(2, captures);
    }

    [Fact]
    public void ReaderReturnsUnknownForInvalidPortsAndExpectedCaptureFailureThenRecovers()
    {
        var time = new MutableTimeProvider();
        var fail = true;
        var captures = 0;
        var reader = new ProductServerListenerStateReader(
            () =>
            {
                captures++;
                if (fail)
                {
                    throw new InvalidOperationException("listener table unavailable");
                }

                return new PortOccupancySnapshot(new HashSet<int> { 25566 }, new HashSet<int>());
            },
            time);

        Assert.Null(reader.TryIsListening(0));
        Assert.Equal(0, captures);
        Assert.Null(reader.TryIsListening(25566));
        Assert.Null(reader.TryIsListening(25566));
        Assert.Equal(1, captures);

        fail = false;
        time.Advance(TimeSpan.FromMilliseconds(751));

        Assert.True(reader.TryIsListening(25566));
        Assert.Equal(2, captures);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}

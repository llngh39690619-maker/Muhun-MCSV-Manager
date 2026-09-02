using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MinecraftServerManager.Core.Models;
using MinecraftServerManager.Service;

namespace MinecraftServerManager.Service.Tests;

public sealed class ProductKnownPlayerRegistryReaderTests
{
    [Fact]
    public async Task BoundedSnapshot_RejectsStreamThatGrowsBeyondItsDeclaredLength()
    {
        await using var source = new DeclaredSmallGrowingStream(1_025);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProductBoundedReadSnapshot.CaptureAsync(source, 1_024, default));

        Assert.Equal(1_025, source.Position);
    }

    [Fact]
    public async Task ReadAsync_MergesKnownAndAdministrativeRegistries()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixture.ServerDirectory, "usercache.json"),
                "[{\"name\":\"Emperor_Yandi\",\"uuid\":\"f84c6a790a4e45c3b68216ba4a8c4d50\"}]");
            await File.WriteAllTextAsync(
                Path.Combine(fixture.ServerDirectory, "ops.json"),
                "[{\"name\":\"Emperor_Yandi\",\"uuid\":\"f84c6a79-0a4e-45c3-b682-16ba4a8c4d50\"}]");
            await File.WriteAllTextAsync(
                Path.Combine(fixture.ServerDirectory, "whitelist.json"),
                "[{\"name\":\"Whitelisted_1\",\"uuid\":\"156755d2-3761-4740-a9b5-e7a25a14a5ef\"}]");
            await File.WriteAllTextAsync(
                Path.Combine(fixture.ServerDirectory, "banned-players.json"),
                "[{\"name\":\"BannedPlayer\",\"uuid\":\"45f2d96b-0279-4208-8f14-3b815b97104c\"}]");

            var players = await fixture.Reader.ReadAsync(fixture.Registration.Id);

            var emperor = Assert.Single(players, player => player.Name == "Emperor_Yandi");
            Assert.Equal(Guid.Parse("f84c6a79-0a4e-45c3-b682-16ba4a8c4d50"), emperor.Uuid);
            Assert.True(emperor.Operator);
            Assert.False(emperor.Online);
            Assert.True(Assert.Single(players, player => player.Name == "Whitelisted_1").Whitelisted);
            Assert.True(Assert.Single(players, player => player.Name == "BannedPlayer").Banned);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ReadAsync_MalformedAndOverDepthFilesDoNotSuppressIndependentRegistry()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixture.ServerDirectory, "usercache.json"),
                "[{\"name\":\"unterminated\"");
            await File.WriteAllTextAsync(
                Path.Combine(fixture.ServerDirectory, "whitelist.json"),
                new string('[', 40) + "0" + new string(']', 40));
            await File.WriteAllTextAsync(
                Path.Combine(fixture.ServerDirectory, "ops.json"),
                "[{\"name\":\"SafeOperator\"}]");

            var players = await fixture.Reader.ReadAsync(fixture.Registration.Id);

            var player = Assert.Single(players);
            Assert.Equal("SafeOperator", player.Name);
            Assert.True(player.Operator);
            Assert.False(player.Whitelisted);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ReadAsync_PreCanceledRequestDoesNotReadRegistries()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixture.ServerDirectory, "usercache.json"),
                "[{\"name\":\"MustNotRead\"}]");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                fixture.Reader.ReadAsync(fixture.Registration.Id, cancellation.Token));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ReadAsync_CapsDistinctKnownPlayersAt4096()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var records = Enumerable.Range(
                    0,
                    ProductKnownPlayerRegistryReader.MaximumKnownPlayers + 1)
                .Select(static index => new { name = $"P{index:D15}" })
                .ToArray();
            await File.WriteAllTextAsync(
                Path.Combine(fixture.ServerDirectory, "usercache.json"),
                JsonSerializer.Serialize(records));

            var players = await fixture.Reader.ReadAsync(fixture.Registration.Id);

            Assert.Equal(ProductKnownPlayerRegistryReader.MaximumKnownPlayers, players.Count);
            Assert.Contains(players, player => player.Name == "P000000000000000");
            Assert.DoesNotContain(players, player => player.Name == "P000000000004096");
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ReadAsync_RejectsHardLinkedRegistryButKeepsIndependentSafeFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await CreateFixtureAsync();
        try
        {
            var outside = Path.Combine(fixture.Layout.Root, "outside.json");
            await File.WriteAllTextAsync(outside, "[{\"name\":\"MustNotLeak\"}]");
            Assert.True(CreateHardLinkW(
                Path.Combine(fixture.ServerDirectory, "usercache.json"),
                outside,
                IntPtr.Zero));
            await File.WriteAllTextAsync(
                Path.Combine(fixture.ServerDirectory, "ops.json"),
                "[{\"name\":\"SafeOperator\"}]");

            var players = await fixture.Reader.ReadAsync(fixture.Registration.Id);

            Assert.DoesNotContain(players, player => player.Name == "MustNotLeak");
            Assert.True(Assert.Single(players, player => player.Name == "SafeOperator").Operator);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ReadAsync_SkipsOversizedRegistryAndKeepsIndependentSafeFile()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using (var oversized = new FileStream(
                             Path.Combine(fixture.ServerDirectory, "usercache.json"),
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                oversized.SetLength(ProductKnownPlayerRegistryReader.MaximumRegistryFileBytes + 1);
            }
            await File.WriteAllTextAsync(
                Path.Combine(fixture.ServerDirectory, "ops.json"),
                "[{\"name\":\"SafeOperator\"}]");

            var players = await fixture.Reader.ReadAsync(fixture.Registration.Id);

            Assert.True(Assert.Single(players, player => player.Name == "SafeOperator").Operator);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ReadAsync_RejectsRedirectedManagedServerDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await CreateFixtureAsync();
        var outside = Path.Combine(
            Path.GetTempPath(),
            "muhun-player-registry-outside",
            Guid.NewGuid().ToString("N"));
        var linked = false;
        try
        {
            Directory.CreateDirectory(outside);
            await File.WriteAllTextAsync(
                Path.Combine(outside, "usercache.json"),
                "[{\"name\":\"MustNotEscape\"}]");
            Directory.Delete(fixture.ServerDirectory);
            CreateDirectoryJunction(fixture.ServerDirectory, outside);
            linked = true;

            var players = await fixture.Reader.ReadAsync(fixture.Registration.Id);

            Assert.Empty(players);
            Assert.True(File.Exists(Path.Combine(outside, "usercache.json")));
        }
        finally
        {
            if (linked && Directory.Exists(fixture.ServerDirectory))
            {
                Directory.Delete(fixture.ServerDirectory, recursive: false);
            }
            fixture.Dispose();
            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var layout = ProductServerRegistryTests.CreateLayout();
        var registration = ProductServerRegistryTests.Registration();
        var serverDirectory = Path.Combine(layout.Servers, registration.ServerDirectory);
        Directory.CreateDirectory(serverDirectory);
        var registry = new ProductServerRegistry(layout);
        await registry.LoadAsync();
        await registry.UpsertAsync(registration);
        return new Fixture(
            layout,
            registration,
            serverDirectory,
            new ProductKnownPlayerRegistryReader(layout, registry));
    }

    private sealed record Fixture(
        ProductDataLayout Layout,
        MinecraftServerManager.Contracts.ProductServerRegistration Registration,
        string ServerDirectory,
        ProductKnownPlayerRegistryReader Reader) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                Directory.Delete(Layout.Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", linkPath, targetPath },
        }) ?? throw new InvalidOperationException("Could not create test junction.");
        process.WaitForExit();
        if (process.ExitCode != 0 ||
            !File.GetAttributes(linkPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Could not create test junction.");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private sealed class DeclaredSmallGrowingStream(long actualLength) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 2;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = checked((int)Math.Min(buffer.Length, actualLength - _position));
            if (count <= 0)
            {
                return ValueTask.FromResult(0);
            }

            buffer.Span[..count].Fill((byte)'x');
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override void Flush()
        {
        }
    }
}

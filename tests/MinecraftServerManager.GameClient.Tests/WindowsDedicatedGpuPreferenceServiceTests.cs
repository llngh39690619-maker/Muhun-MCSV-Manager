using MinecraftServerManager.GameClient;

namespace MinecraftServerManager.GameClient.Tests;

public sealed class WindowsDedicatedGpuPreferenceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "x-mcsv-gpu-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void TryApply_ValidManagedJava_UsesCanonicalExecutablePath()
    {
        Directory.CreateDirectory(_root);
        var java = Path.Combine(_root, "javaw.exe");
        File.WriteAllBytes(java, [0x4d, 0x5a]);
        var store = new RecordingStore();
        var service = new WindowsDedicatedGpuPreferenceService(store);

        var applied = service.TryApply(java);

        Assert.Equal(OperatingSystem.IsWindows(), applied);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.GetFullPath(java), store.ExecutablePath);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("java.exe")]
    [InlineData("missing-java.exe")]
    public void TryApply_InvalidPath_DoesNotWritePreference(string path)
    {
        var store = new RecordingStore();
        var service = new WindowsDedicatedGpuPreferenceService(store);

        Assert.False(service.TryApply(path));
        Assert.Null(store.ExecutablePath);
    }

    [Fact]
    public void TryApply_StoreFailure_IsFailSoft()
    {
        Directory.CreateDirectory(_root);
        var java = Path.Combine(_root, "java.exe");
        File.WriteAllBytes(java, [0x4d, 0x5a]);
        var service = new WindowsDedicatedGpuPreferenceService(new ThrowingStore());

        Assert.False(service.TryApply(java));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingStore : IUserGpuPreferenceStore
    {
        public string? ExecutablePath { get; private set; }

        public void SetHighPerformance(string executablePath) => ExecutablePath = executablePath;
    }

    private sealed class ThrowingStore : IUserGpuPreferenceStore
    {
        public void SetHighPerformance(string executablePath) =>
            throw new UnauthorizedAccessException("blocked");
    }
}

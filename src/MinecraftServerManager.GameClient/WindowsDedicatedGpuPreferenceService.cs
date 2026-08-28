using Microsoft.Win32;

namespace MinecraftServerManager.GameClient;

internal interface IUserGpuPreferenceStore
{
    void SetHighPerformance(string executablePath);
}

/// <summary>
/// Applies Windows' per-executable high-performance GPU preference for the managed Java runtime.
/// The preference is stored under the current user and therefore never requires elevation.
/// </summary>
public sealed class WindowsDedicatedGpuPreferenceService
{
    private readonly IUserGpuPreferenceStore _store;

    public WindowsDedicatedGpuPreferenceService()
        : this(new WindowsUserGpuPreferenceStore())
    {
    }

    internal WindowsDedicatedGpuPreferenceService(IUserGpuPreferenceStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public bool TryApply(string? javaExecutablePath)
    {
        if (!OperatingSystem.IsWindows() ||
            string.IsNullOrWhiteSpace(javaExecutablePath) ||
            !Path.IsPathFullyQualified(javaExecutablePath))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(javaExecutablePath);
            var fileName = Path.GetFileName(fullPath);
            if ((!string.Equals(fileName, "java.exe", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(fileName, "javaw.exe", StringComparison.OrdinalIgnoreCase)) ||
                !File.Exists(fullPath))
            {
                return false;
            }

            _store.SetHighPerformance(fullPath);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // GPU preference is an optional launch enhancement. Registry policy, endpoint
            // security, or a concurrently removed runtime must not prevent Minecraft launch.
            return false;
        }
    }

    private sealed class WindowsUserGpuPreferenceStore : IUserGpuPreferenceStore
    {
        private const string RegistryPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

        public void SetHighPerformance(string executablePath)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
                            ?? throw new UnauthorizedAccessException(
                                "Windows GPU preferences are unavailable for the current user.");
            key.SetValue(executablePath, "GpuPreference=2;", RegistryValueKind.String);
        }
    }
}

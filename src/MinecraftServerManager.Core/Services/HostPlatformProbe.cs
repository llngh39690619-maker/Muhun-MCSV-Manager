using System.Runtime.InteropServices;
using MinecraftServerManager.Core.Models;

namespace MinecraftServerManager.Core.Services;

/// <summary>Small injectable boundary that makes OS-specific pack selection deterministic.</summary>
public interface IHostPlatformProbe
{
    HostOperatingSystem OperatingSystem { get; }

    Architecture OSArchitecture { get; }
}

public sealed class SystemHostPlatformProbe : IHostPlatformProbe
{
    public HostOperatingSystem OperatingSystem => OperatingSystemPlatform();

    public Architecture OSArchitecture => RuntimeInformation.OSArchitecture;

    private static HostOperatingSystem OperatingSystemPlatform()
    {
        if (System.OperatingSystem.IsWindows())
        {
            return HostOperatingSystem.Windows;
        }

        if (System.OperatingSystem.IsLinux())
        {
            return HostOperatingSystem.Linux;
        }

        return HostOperatingSystem.Unsupported;
    }
}

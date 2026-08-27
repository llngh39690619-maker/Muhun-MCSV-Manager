using System.Runtime.InteropServices;

namespace MinecraftServerManager.Core.Services;

/// <summary>A point-in-time view of physical memory available to server allocation policy.</summary>
public readonly record struct SystemMemorySnapshot(
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    bool IsFallback = false);

/// <summary>Provides physical-memory data without coupling policy code to an operating system.</summary>
public interface ISystemMemoryProbe
{
    SystemMemorySnapshot GetSnapshot();
}

/// <summary>
/// Reads Windows physical-memory counters. Probe failures never escape into the UI: a deliberately
/// conservative snapshot is returned so automatic allocation cannot reserve an unsafe amount.
/// </summary>
public sealed class WindowsSystemMemoryProbe : ISystemMemoryProbe
{
    private const long Gibibyte = 1024L * 1024L * 1024L;

    public SystemMemorySnapshot GetSnapshot()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var status = new MemoryStatusEx
                {
                    Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>())
                };

                if (GlobalMemoryStatusEx(ref status)
                    && status.TotalPhysicalBytes > 0
                    && status.AvailablePhysicalBytes <= status.TotalPhysicalBytes)
                {
                    var total = ToSignedBytes(status.TotalPhysicalBytes);
                    var available = Math.Min(
                        total,
                        ToSignedBytes(status.AvailablePhysicalBytes));
                    return new SystemMemorySnapshot(total, available);
                }
            }
            catch (Exception)
            {
                // Fall through to a bounded estimate. Memory recommendation must remain usable
                // even on unusual Windows editions or when native probing is unavailable.
            }
        }

        return CreateConservativeFallback();
    }

    private static SystemMemorySnapshot CreateConservativeFallback()
    {
        long reportedLimit;
        try
        {
            reportedLimit = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        catch
        {
            reportedLimit = 0;
        }

        // A GC/container limit is not necessarily installed physical RAM. Cap it and expose only
        // half as currently available so a failed native probe cannot encourage over-allocation.
        var total = reportedLimit is > 0 and < long.MaxValue
            ? Math.Clamp(reportedLimit, 2 * Gibibyte, 16 * Gibibyte)
            : 8 * Gibibyte;
        var available = Math.Clamp(total / 2, Gibibyte, total);
        return new SystemMemorySnapshot(total, available, IsFallback: true);
    }

    private static long ToSignedBytes(ulong value)
        => value > long.MaxValue ? long.MaxValue : (long)value;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalBytes;
        public ulong AvailablePhysicalBytes;
        public ulong TotalPageFileBytes;
        public ulong AvailablePageFileBytes;
        public ulong TotalVirtualBytes;
        public ulong AvailableVirtualBytes;
        public ulong AvailableExtendedVirtualBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

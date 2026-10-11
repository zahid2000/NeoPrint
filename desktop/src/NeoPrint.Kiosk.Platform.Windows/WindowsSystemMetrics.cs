using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Platform.Windows;

/// <summary>
/// Machine telemetry on Windows (plan sections 2.0, 7.9), from the system's
/// own counters: <c>GetSystemTimes</c> for the processor, <c>GlobalMemoryStatusEx</c>
/// for memory.
/// </summary>
/// <remarks>
/// The Wi-Fi signal is not read here: it needs the WLAN API, and a kiosk is
/// wired in the pilot. It is reported as "no data" (docs/TASKS.md).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSystemMetrics : ISystemMetrics
{
    private readonly IAppPaths _paths;
    private readonly object _gate = new();
    private (long Idle, long Total)? _previousCpu;

    public WindowsSystemMetrics(IAppPaths paths) => _paths = paths;

    public Task<SystemSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        double? cpuPercent = null;

        // Kernel time includes idle time, so busy + idle is kernel + user.
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var times = (Idle: idle, Total: kernel + user);

            lock (_gate)
            {
                cpuPercent = SystemProbes.CpuPercent(_previousCpu, times);
                _previousCpu = times;
            }
        }

        long? memoryTotal = null;
        long? memoryUsed = null;
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };

        if (GlobalMemoryStatusEx(ref status))
        {
            memoryTotal = (long)status.TotalPhysical;
            memoryUsed = (long)(status.TotalPhysical - status.AvailablePhysical);
        }

        var network = SystemProbes.ActiveNetwork();
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);

        return Task.FromResult(new SystemSnapshot(
            Os: RuntimeInformation.OSDescription,
            OsVersion: Environment.OSVersion.Version.ToString(),
            Hostname: Environment.MachineName,
            CpuPercent: cpuPercent,
            MemoryUsedBytes: memoryUsed,
            MemoryTotalBytes: memoryTotal,
            SystemDiskFreeBytes: SystemProbes.FreeBytes(string.IsNullOrEmpty(system) ? @"C:\" : system),
            SpoolDiskFreeBytes: SystemProbes.FreeBytes(_paths.SpoolDirectory),
            UptimeSeconds: Environment.TickCount64 / 1000,
            ConnectionType: network?.ConnectionType,
            WifiSignalPercent: null,
            LocalIp: network?.LocalIp));
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

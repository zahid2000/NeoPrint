using System.Globalization;
using System.Runtime.Versioning;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Platform.Linux;

/// <summary>
/// Reads the files of <c>/proc</c> and <c>/etc</c> that machine telemetry is
/// made of (plan sections 2.0 and 7.9). Text in, numbers out: nothing here
/// touches the file system, so each is tested against a real file's content.
/// </summary>
public static class ProcParsers
{
    /// <summary>
    /// The first line of <c>/proc/stat</c>: cumulative jiffies per state.
    /// Idle is <c>idle + iowait</c>; a processor waiting on a disk is not busy.
    /// </summary>
    public static (long Idle, long Total)? CpuTimes(string? stat)
    {
        var line = stat?.Split('\n').FirstOrDefault(candidate => candidate.StartsWith("cpu ", StringComparison.Ordinal));

        if (line is null)
        {
            return null;
        }

        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).ToArray();
        var values = new long[fields.Length];

        for (var i = 0; i < fields.Length; i++)
        {
            if (!long.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }

        // user nice system idle iowait irq softirq steal
        return values.Length < 4 ? null : (values[3] + (values.Length > 4 ? values[4] : 0), values.Sum());
    }

    /// <summary>
    /// <c>/proc/meminfo</c>: total, and used as the kernel itself estimates it -
    /// total minus <c>MemAvailable</c>, which counts reclaimable cache as free.
    /// </summary>
    public static (long UsedBytes, long TotalBytes)? Memory(string? meminfo)
    {
        long? total = null;
        long? available = null;

        foreach (var line in (meminfo ?? string.Empty).Split('\n'))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                total = Kilobytes(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                available = Kilobytes(line);
            }
        }

        return total is > 0 && available is >= 0 && available <= total ? ((total.Value - available.Value) * 1024, total.Value * 1024) : null;
    }

    private static long? Kilobytes(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary><c>/proc/uptime</c>: seconds since boot, the first number.</summary>
    public static long? UptimeSeconds(string? uptime)
    {
        var first = uptime?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? (long)seconds
            : null;
    }

    /// <summary><c>/etc/os-release</c>: the name people know the system by.</summary>
    public static string? OsName(string? osRelease)
    {
        foreach (var line in (osRelease ?? string.Empty).Split('\n'))
        {
            if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
            {
                var name = line["PRETTY_NAME=".Length..].Trim().Trim('"');

                return name.Length == 0 ? null : name;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>/proc/net/wireless</c>: the link quality of an interface as a
    /// percentage. The kernel reports it out of 70.
    /// </summary>
    public static int? WifiSignalPercent(string? wireless, string interfaceName)
    {
        foreach (var line in (wireless ?? string.Empty).Split('\n'))
        {
            var trimmed = line.TrimStart();

            if (!trimmed.StartsWith(interfaceName + ":", StringComparison.Ordinal))
            {
                continue;
            }

            // wlan0: 0000   54.  -56.  -256        0      0      0      0      0        0
            var fields = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            return fields.Length >= 3
                   && double.TryParse(fields[2].TrimEnd('.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var quality)
                ? (int)Math.Clamp(Math.Round(quality * 100 / 70), 0, 100)
                : null;
        }

        return null;
    }
}

/// <summary>Machine telemetry on Linux, from <c>/proc</c> and <c>/sys</c> (plan sections 2.0, 7.9).</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSystemMetrics : ISystemMetrics
{
    private readonly IAppPaths _paths;
    private readonly object _gate = new();
    private (long Idle, long Total)? _previousCpu;

    public LinuxSystemMetrics(IAppPaths paths) => _paths = paths;

    public Task<SystemSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var cpu = ProcParsers.CpuTimes(Read("/proc/stat"));
        double? cpuPercent = null;

        lock (_gate)
        {
            if (cpu is { } times)
            {
                cpuPercent = SystemProbes.CpuPercent(_previousCpu, times);
                _previousCpu = times;
            }
        }

        var memory = ProcParsers.Memory(Read("/proc/meminfo"));
        var network = SystemProbes.ActiveNetwork();

        return Task.FromResult(new SystemSnapshot(
            Os: ProcParsers.OsName(Read("/etc/os-release")) ?? "Linux",
            OsVersion: Read("/proc/sys/kernel/osrelease")?.Trim(),
            Hostname: Environment.MachineName,
            CpuPercent: cpuPercent,
            MemoryUsedBytes: memory?.UsedBytes,
            MemoryTotalBytes: memory?.TotalBytes,
            SystemDiskFreeBytes: SystemProbes.FreeBytes("/"),
            SpoolDiskFreeBytes: SystemProbes.FreeBytes(_paths.SpoolDirectory),
            UptimeSeconds: ProcParsers.UptimeSeconds(Read("/proc/uptime")),
            ConnectionType: network?.ConnectionType,
            WifiSignalPercent: network is { ConnectionType: "WiFi" }
                ? ProcParsers.WifiSignalPercent(Read("/proc/net/wireless"), network.Name)
                : null,
            LocalIp: network?.LocalIp));
    }

    private static string? Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NeoPrint.Kiosk.Platform.Abstractions;

/// <summary>The network interface the kiosk reaches the world through.</summary>
/// <param name="Name">The system's name for it (<c>eth0</c>, <c>wlan0</c>, <c>Ethernet</c>).</param>
/// <param name="ConnectionType"><c>Ethernet</c>, <c>WiFi</c> or <c>Other</c>.</param>
public sealed record ActiveNetwork(string Name, string ConnectionType, string? LocalIp);

/// <summary>
/// The parts of machine telemetry that .NET answers the same way on Windows
/// and Linux (plan section 7.9), shared by both platforms' <see cref="ISystemMetrics"/>.
/// </summary>
public static class SystemProbes
{
    /// <summary>
    /// The interface that is up, is not a loopback or a tunnel, and has a
    /// gateway - the one traffic leaves through; null when there is none.
    /// </summary>
    public static ActiveNetwork? ActiveNetwork()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                              && nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(nic => (Nic: nic, Properties: nic.GetIPProperties()))
                .Select(item => (
                    item.Nic,
                    Address: item.Properties.UnicastAddresses
                        .FirstOrDefault(address => address.Address.AddressFamily == AddressFamily.InterNetwork)?.Address,
                    HasGateway: item.Properties.GatewayAddresses.Count > 0))
                .Where(item => item.Address is not null)
                .OrderByDescending(item => item.HasGateway)
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            var (nic, address, _) = candidates[0];

            return new ActiveNetwork(nic.Name, Describe(nic.NetworkInterfaceType), address!.ToString());
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public static string Describe(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
        NetworkInterfaceType.Wireless80211 => "WiFi",
        _ => "Other",
    };

    /// <summary>Free space on the volume a path is on; null when it cannot be read.</summary>
    public static long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));

            // On Linux every path has the root "/", so the volume is found by
            // the longest mount point the path starts with.
            var drive = DriveInfo.GetDrives()
                .Where(candidate => candidate.IsReady && Path.GetFullPath(path).StartsWith(candidate.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.Name.Length)
                .FirstOrDefault() ?? (root is null ? null : new DriveInfo(root));

            return drive is { IsReady: true } ? drive.AvailableFreeSpace : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Processor use between two readings of cumulative (idle, total) time;
    /// null when no time passed or the counters went backwards.
    /// </summary>
    public static double? CpuPercent((long Idle, long Total)? previous, (long Idle, long Total) current)
    {
        if (previous is not { } before)
        {
            return null;
        }

        var total = current.Total - before.Total;
        var idle = current.Idle - before.Idle;

        return total <= 0 || idle < 0 || idle > total ? null : Math.Round(100.0 * (total - idle) / total, 1);
    }
}

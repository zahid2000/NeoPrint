using System.Globalization;
using System.Management;
using System.Runtime.Versioning;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Platform.Windows;

/// <summary>
/// A printer's details on Windows (plan section 7.10): <c>Win32_Printer</c>,
/// <c>Win32_PrinterDriver</c> and <c>Win32_PrintJob</c> through WMI. Windows
/// itself gives no toner levels; a network printer's SNMP answers, added by
/// the caller, are where they come from.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrinterInspector : IPrinterInspector
{
    private readonly IPrinterAddressSource _addresses;

    public WindowsPrinterInspector(IPrinterAddressSource addresses) => _addresses = addresses;

    public async Task<PrinterDetails> InspectAsync(string printer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(printer);

        var row = First($"SELECT * FROM Win32_Printer WHERE Name = '{Escape(printer)}'");

        if (row is null)
        {
            return new PrinterDetails(printer, State: "stopped", StateReasons: ["not-found"], Online: false);
        }

        // Win32_PrinterDriver names a driver "Name,Version,Environment".
        var driverName = row.TryGetValue("DriverName", out var name) ? name?.ToString() : null;
        var driver = driverName is null ? null : First($"SELECT * FROM Win32_PrinterDriver WHERE Name LIKE '{Escape(driverName)},%'");

        var details = WmiPrinter.Describe(printer, row, driver) with { SpoolerJobs = Jobs(printer) };

        var host = await _addresses.GetNetworkAddressAsync(printer, cancellationToken).ConfigureAwait(false);

        if (host is not null)
        {
            details = details with { Connection = PrinterConnection.Network, Address = host };
        }

        return details;
    }

    private static Dictionary<string, object?>? First(string query)
    {
        using var searcher = new ManagementObjectSearcher(query);
        using var results = searcher.Get();

        foreach (var item in results)
        {
            using (item)
            {
                return item.Properties.Cast<PropertyData>().ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal);
            }
        }

        return null;
    }

    /// <summary><c>Win32_PrintJob</c> names a job "printer, id".</summary>
    private static List<SpoolerJob> Jobs(string printer)
    {
        var jobs = new List<SpoolerJob>();

        using var searcher = new ManagementObjectSearcher("SELECT Name, JobId, Size, JobStatus, Status, TimeSubmitted FROM Win32_PrintJob");
        using var results = searcher.Get();

        foreach (var item in results)
        {
            using (item)
            {
                if (item["Name"]?.ToString() is not { } name || !name.StartsWith(printer + ",", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DateTime? submitted = null;

                if (item["TimeSubmitted"]?.ToString() is { Length: > 0 } stamp)
                {
                    try
                    {
                        submitted = ManagementDateTimeConverter.ToDateTime(stamp).ToUniversalTime();
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // A spooler that writes no usable time: the job is timed from when it was first seen.
                    }
                }

                jobs.Add(new SpoolerJob(
                    Convert.ToString(item["JobId"], CultureInfo.InvariantCulture) ?? name,
                    item["Size"] is null ? null : Convert.ToInt64(item["Size"], CultureInfo.InvariantCulture),
                    item["JobStatus"]?.ToString() is { Length: > 0 } status ? status : item["Status"]?.ToString(),
                    submitted));
            }
        }

        return jobs;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
}

using System.Net;
using System.Net.Sockets;

using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;

using Microsoft.Extensions.Logging;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core.Printing;

/// <summary>
/// Reads a network printer's Printer MIB over SNMP (plan section 7.10):
/// supplies, trays, serial number and the lifetime counter - what IPP and WMI
/// often leave out.
/// </summary>
/// <remarks>
/// Read-only, version 2c with the community every printer ships with. A
/// printer that does not answer within the time simply adds nothing: SNMP is
/// often switched off, and that is not a fault.
/// </remarks>
public sealed class SnmpPrinterReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    private static readonly string[] Tables = [PrinterMib.SuppliesTable, PrinterMib.ColorantTable, PrinterMib.InputTable];

    private static readonly string[] Scalars = [PrinterMib.SystemDescription, PrinterMib.SerialNumber, PrinterMib.LifeCount];

    private readonly string _community;
    private readonly ILogger<SnmpPrinterReader> _logger;

    public SnmpPrinterReader(ILogger<SnmpPrinterReader> logger, string community = "public")
    {
        _logger = logger;
        _community = community;
    }

    /// <returns>What the printer's MIB says, or null when it does not answer.</returns>
    public async Task<PrinterDetails?> ReadAsync(string printer, string host, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            var address = IPAddress.TryParse(host, out var parsed)
                ? parsed
                : (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, timeout.Token).ConfigureAwait(false)).FirstOrDefault();

            if (address is null)
            {
                return null;
            }

            var endpoint = new IPEndPoint(address, 161);
            var community = new OctetString(_community);
            var rows = new Dictionary<string, string>(StringComparer.Ordinal);

            var scalars = await Messenger
                .GetAsync(VersionCode.V2, endpoint, community, [.. Scalars.Select(oid => new Variable(new ObjectIdentifier(oid)))], timeout.Token)
                .ConfigureAwait(false);

            Add(rows, scalars);

            foreach (var table in Tables)
            {
                var found = new List<Variable>();

                await Messenger
                    .WalkAsync(VersionCode.V2, endpoint, community, new ObjectIdentifier(table), found, WalkMode.WithinSubtree, timeout.Token)
                    .ConfigureAwait(false);

                Add(rows, found);
            }

            return PrinterMib.Interpret(printer, rows);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("The printer {Printer} did not answer SNMP in time.", printer);
            return null;
        }
        catch (Exception exception) when (exception is SocketException or SnmpException or ErrorException)
        {
            _logger.LogDebug(exception, "The printer {Printer} could not be read over SNMP.", printer);
            return null;
        }
    }

    private static void Add(Dictionary<string, string> rows, IEnumerable<Variable> variables)
    {
        foreach (var variable in variables)
        {
            // NoSuchObject and NoSuchInstance are answers, but not values.
            if (variable.Data.TypeCode is SnmpType.NoSuchObject or SnmpType.NoSuchInstance or SnmpType.EndOfMibView or SnmpType.Null)
            {
                continue;
            }

            rows[variable.Id.ToString()] = variable.Data.ToString() ?? string.Empty;
        }
    }
}

/// <summary>
/// Asks the right source about a printer: the fake printer answers for
/// itself, the machine's inspector for everything else, and a network
/// printer's own SNMP agent fills in what the system did not know (supplies,
/// trays, serial number, lifetime counter). Never throws - a printer that
/// cannot be asked is reported by name alone.
/// </summary>
public sealed class RoutingPrinterInspector : IPrinterInspector
{
    private readonly IPrinterInspector? _machine;
    private readonly FakePrinterService _fake;
    private readonly SnmpPrinterReader? _snmp;
    private readonly ILogger<RoutingPrinterInspector> _logger;

    public RoutingPrinterInspector(
        IPrinterInspector? machine, FakePrinterService fake, ILogger<RoutingPrinterInspector> logger, SnmpPrinterReader? snmp = null)
    {
        _machine = machine;
        _fake = fake;
        _snmp = snmp;
        _logger = logger;
    }

    public async Task<PrinterDetails> InspectAsync(string printer, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_fake.Knows(printer))
            {
                return await _fake.InspectAsync(printer, cancellationToken).ConfigureAwait(false);
            }

            if (_machine is null)
            {
                return new PrinterDetails(printer);
            }

            var details = await _machine.InspectAsync(printer, cancellationToken).ConfigureAwait(false);

            return _snmp is not null && details is { Connection: PrinterConnection.Network, Address: { } host }
                ? details.FilledFrom(await _snmp.ReadAsync(printer, host, cancellationToken).ConfigureAwait(false))
                : details;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The printer {Printer} could not be inspected.", printer);
            return new PrinterDetails(printer);
        }
    }
}

/// <summary>
/// Which printers to ask about now (plan section 7.10): the selected printer
/// every time, the others once in ten.
/// </summary>
public sealed class PrinterTelemetrySchedule
{
    /// <summary>The selected printer is asked every minute.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>The other printers every ten minutes.</summary>
    public static readonly TimeSpan OthersEvery = TimeSpan.FromMinutes(10);

    private DateTime? _othersAskedAt;

    public IReadOnlyList<string> Due(string? selected, IEnumerable<string> discovered, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(discovered);

        var due = new List<string>();

        if (!string.IsNullOrWhiteSpace(selected))
        {
            due.Add(selected);
        }

        if (_othersAskedAt is null || now - _othersAskedAt >= OthersEvery)
        {
            _othersAskedAt = now;
            due.AddRange(discovered.Where(name => !string.Equals(name, selected, StringComparison.Ordinal)));
        }

        return [.. due.Distinct(StringComparer.Ordinal)];
    }
}

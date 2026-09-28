using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;

using Microsoft.Extensions.Logging;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core.Printing;

/// <summary>
/// A network printer's lifetime page count over SNMP (plan section 7.10,
/// Printer-MIB, RFC 3805): <c>prtMarkerLifeCount</c> and the unit it counts
/// in, <c>prtMarkerCounterUnit</c>.
/// </summary>
/// <remarks>
/// Read before and after every attempt, and again after a restart, it is the
/// second witness when the spooler has forgotten a job (plan section 2.5,
/// <c>print_attempts</c>). A USB printer, a printer without SNMP or one that
/// does not answer within the timeout simply has no counter.
/// </remarks>
public sealed class SnmpPageCounter : IPrinterPageCounter
{
    /// <summary>prtMarkerLifeCount.1.1: the first marker of the first device.</summary>
    private static readonly ObjectIdentifier LifeCount = new("1.3.6.1.2.1.43.10.2.1.4.1.1");

    /// <summary>prtMarkerCounterUnit.1.1.</summary>
    private static readonly ObjectIdentifier CounterUnit = new("1.3.6.1.2.1.43.10.2.1.3.1.1");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly IPrinterAddressSource? _addresses;
    private readonly string _community;
    private readonly ILogger<SnmpPageCounter> _logger;

    /// <param name="community">The read community; printers ship with "public" (not a secret).</param>
    public SnmpPageCounter(IPrinterAddressSource? addresses, ILogger<SnmpPageCounter> logger, string community = "public")
    {
        _addresses = addresses;
        _logger = logger;
        _community = community;
    }

    public async Task<PageCount?> ReadAsync(string printer, CancellationToken cancellationToken = default)
    {
        if (_addresses is null
            || await _addresses.GetNetworkAddressAsync(printer, cancellationToken).ConfigureAwait(false) is not { } host)
        {
            return null;
        }

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

            var answer = await Messenger
                .GetAsync(
                    VersionCode.V2,
                    new IPEndPoint(address, 161),
                    new OctetString(_community),
                    [new Variable(LifeCount), new Variable(CounterUnit)],
                    timeout.Token)
                .ConfigureAwait(false);

            return Interpret(
                answer.FirstOrDefault(variable => variable.Id == LifeCount)?.Data.ToString(),
                answer.FirstOrDefault(variable => variable.Id == CounterUnit)?.Data.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("The printer {Printer} did not answer SNMP in time.", printer);
            return null;
        }
        catch (Exception exception) when (exception is SocketException or SnmpException or ErrorException)
        {
            _logger.LogDebug(exception, "The printer {Printer} has no readable page counter.", printer);
            return null;
        }
    }

    /// <summary>
    /// The count and what it counts; null for a unit that is not pages
    /// (inches, hours ...) or a value that is not a number.
    /// </summary>
    /// <remarks>prtMarkerCounterUnit: 7 impressions, 8 sheets (RFC 3805).</remarks>
    public static PageCount? Interpret(string? count, string? unit)
    {
        if (!long.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            return null;
        }

        return unit switch
        {
            "7" => new PageCount(value, CountsSheets: false),
            "8" => new PageCount(value, CountsSheets: true),
            _ => null,
        };
    }
}

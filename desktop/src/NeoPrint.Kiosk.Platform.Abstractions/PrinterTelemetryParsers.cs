using System.Globalization;
using System.Text;

namespace NeoPrint.Kiosk.Platform.Abstractions;

// ---------------------------------------------------------------------------
// What printers answer, turned into PrinterDetails (plan section 7.10). Text or
// tables in, a record out: nothing here talks to a printer, so each source is
// tested against answers real devices give (plan section 11).
// ---------------------------------------------------------------------------

/// <summary>The attributes <c>ipptool -tv ... get-printer-attributes.test</c> prints.</summary>
public static class IppAttributes
{
    /// <summary>
    /// Reads lines of the form <c>name (syntax) = value</c>. Only a
    /// <c>1setOf</c> attribute is a list, split at its commas; any other is
    /// one value, commas and all - a device id (<c>CMD:PCL,PS</c>) or a
    /// sentence is not cut in two. ipptool puts a backslash before a
    /// backslash, a quote and a bracket; that is undone.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Parse(string? output)
    {
        var attributes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            var open = line.IndexOf(" (", StringComparison.Ordinal);
            var equals = line.IndexOf(") = ", StringComparison.Ordinal);

            if (open <= 0 || equals < open)
            {
                continue;
            }

            var syntax = line[(open + 2)..equals];
            var value = Unescape(line[(equals + 4)..]);

            attributes[line[..open]] = syntax.StartsWith("1setOf", StringComparison.Ordinal)
                ? [.. value.Split(',').Select(part => part.Trim())]
                : [value.Trim()];
        }

        return attributes;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var text = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
            }

            text.Append(value[i]);
        }

        return text.ToString();
    }

    public static string? First(this IReadOnlyDictionary<string, IReadOnlyList<string>> attributes, string name)
        => attributes.TryGetValue(name, out var values) && values.Count > 0 && values[0].Length > 0 ? values[0] : null;

    public static IReadOnlyList<string> All(this IReadOnlyDictionary<string, IReadOnlyList<string>> attributes, string name)
        => attributes.TryGetValue(name, out var values) ? values : [];
}

/// <summary>A printer as IPP describes it (RFC 8011, PWG 5100.13).</summary>
public static class IppPrinter
{
    public static PrinterDetails Describe(string name, IReadOnlyDictionary<string, IReadOnlyList<string>> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        var device = DeviceId(attributes.First("printer-device-id"));
        var reasons = attributes.All("printer-state-reasons").Where(reason => reason.Length > 0 && reason != "none").ToList();
        var uri = attributes.First("device-uri") ?? attributes.First("printer-uri-supported");

        return new PrinterDetails(
            name,
            Manufacturer: device.GetValueOrDefault("MFG") ?? device.GetValueOrDefault("MANUFACTURER"),
            Model: device.GetValueOrDefault("MDL") ?? device.GetValueOrDefault("MODEL") ?? attributes.First("printer-make-and-model"),
            SerialNumber: device.GetValueOrDefault("SN") ?? device.GetValueOrDefault("SERN") ?? device.GetValueOrDefault("SERIALNUMBER"),
            Firmware: attributes.First("printer-firmware-string-version"),
            DriverName: attributes.First("printer-make-and-model"),
            Connection: PrinterConnection.FromUri(uri),
            Address: PrinterConnection.HostOf(uri),
            Uri: uri,
            State: attributes.First("printer-state"),
            StateReasons: reasons,
            Online: !reasons.Any(reason => reason.StartsWith("offline", StringComparison.Ordinal)
                                           || reason.StartsWith("shutdown", StringComparison.Ordinal)
                                           || reason.StartsWith("connecting-to-device", StringComparison.Ordinal)),
            LastError: attributes.First("printer-state-message"),
            Supplies: Supplies(attributes),
            Trays: Trays(attributes),
            LifetimePages: Number(attributes.First("printer-pages-completed")) ?? Number(attributes.First("printer-impressions-completed")),
            Features: new PrinterFeatures(
                Color: attributes.First("color-supported") is { } color ? color == "true" : null,
                Duplex: attributes.TryGetValue("sides-supported", out var sides)
                    ? sides.Any(side => side.StartsWith("two-sided", StringComparison.Ordinal))
                    : null,
                PaperSizes: [.. attributes.All("media-supported").Select(PaperName).Distinct(StringComparer.Ordinal)],
                Trays: attributes.All("media-source-supported"),
                ResolutionDpi: attributes.All("printer-resolution-supported").Select(Dpi).Max(),
                NativeFormats: attributes.All("document-format-supported")));
    }

    /// <summary>IEEE 1284 device id: <c>MFG:HP;MDL:LaserJet;SN:ABC123;</c>.</summary>
    public static IReadOnlyDictionary<string, string> DeviceId(string? deviceId)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in (deviceId ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);

            if (colon > 0 && colon < part.Length - 1)
            {
                fields[part[..colon].Trim()] = part[(colon + 1)..].Trim();
            }
        }

        return fields;
    }

    /// <summary>
    /// <c>marker-names</c>, <c>-types</c>, <c>-colors</c> and <c>-levels</c>
    /// are parallel lists. A level below zero is not a level: -1 "other",
    /// -2 "unknown", -3 "some left".
    /// </summary>
    private static List<PrinterSupply> Supplies(IReadOnlyDictionary<string, IReadOnlyList<string>> attributes)
    {
        var names = attributes.All("marker-names");
        var types = attributes.All("marker-types");
        var colors = attributes.All("marker-colors");
        var levels = attributes.All("marker-levels");

        return
        [
            .. names.Select((name, index) => new PrinterSupply(
                name,
                SupplyType.FromIpp(index < types.Count ? types[index] : null),
                index < colors.Count && colors[index].Length > 0 && colors[index] != "none" ? colors[index] : null,
                index < levels.Count && Number(levels[index]) is >= 0 and <= 100 and var level ? (int)level : null)),
        ];
    }

    /// <summary><c>printer-input-tray</c>: one <c>key=value;</c> string per tray (PWG 5100.13).</summary>
    private static List<PrinterTray> Trays(IReadOnlyDictionary<string, IReadOnlyList<string>> attributes)
    {
        var trays = new List<PrinterTray>();

        foreach (var value in attributes.All("printer-input-tray"))
        {
            var fields = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(field => field.Split('=', 2))
                .Where(pair => pair.Length == 2)
                .GroupBy(pair => pair[0], StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First()[1], StringComparer.Ordinal);

            if (!fields.TryGetValue("name", out var name) || name.Length == 0)
            {
                continue;
            }

            var level = Number(fields.GetValueOrDefault("level"));
            var capacity = Number(fields.GetValueOrDefault("maxcapacity"));

            trays.Add(new PrinterTray(
                name,
                Media: null,
                LevelPercent: level is >= 0 && capacity is > 0 ? (int)Math.Clamp(level.Value * 100 / capacity.Value, 0, 100) : null));
        }

        return trays;
    }

    /// <summary><c>iso_a4_210x297mm</c> is A4; a size without an everyday name stays as the printer wrote it.</summary>
    public static string PaperName(string media) => media switch
    {
        _ when media.StartsWith("iso_a3_", StringComparison.Ordinal) => "A3",
        _ when media.StartsWith("iso_a4_", StringComparison.Ordinal) => "A4",
        _ when media.StartsWith("iso_a5_", StringComparison.Ordinal) => "A5",
        _ when media.StartsWith("iso_a6_", StringComparison.Ordinal) => "A6",
        _ when media.StartsWith("na_letter_", StringComparison.Ordinal) => "Letter",
        _ when media.StartsWith("na_legal_", StringComparison.Ordinal) => "Legal",
        _ => media,
    };

    /// <summary><c>600dpi</c> or <c>600x1200dpi</c>: the lower of the two is what both directions reach.</summary>
    private static int? Dpi(string resolution)
    {
        var numbers = resolution.Replace("dpi", string.Empty, StringComparison.Ordinal)
            .Split('x')
            .Select(part => Number(part))
            .ToList();

        return numbers.Count > 0 && numbers.All(number => number is > 0) ? (int)numbers.Min()!.Value : null;
    }

    private static long? Number(string? text)
        => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>How a printer is attached, read from where it is addressed.</summary>
public static class PrinterConnection
{
    public const string Usb = "Usb";
    public const string Network = "Network";
    public const string Other = "Other";

    /// <summary>A CUPS device URI: <c>usb://HP/LaserJet</c>, <c>ipp://10.0.0.5/ipp/print</c>, <c>socket://printer:9100</c>.</summary>
    public static string? FromUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        var scheme = uri.Split(':')[0].ToLowerInvariant();

        return scheme switch
        {
            "usb" or "hp" or "parallel" => Usb,
            "ipp" or "ipps" or "http" or "https" or "socket" or "lpd" or "dnssd" or "smb" => HostOf(uri) is "localhost" or "127.0.0.1" ? Other : Network,
            _ => Other,
        };
    }

    public static string? HostOf(string? uri)
        => System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Host.Length > 0 && parsed.Scheme is not ("usb" or "hp" or "parallel")
            ? parsed.Host
            : null;

    /// <summary>A Windows port name: <c>USB001</c>, <c>IP_10.0.0.5</c>, <c>WSD-...</c>, <c>LPT1:</c>.</summary>
    public static string? FromWindowsPort(string? port)
    {
        if (string.IsNullOrWhiteSpace(port))
        {
            return null;
        }

        return port.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ? Usb
            : port.StartsWith("IP_", StringComparison.OrdinalIgnoreCase)
              || port.StartsWith("WSD", StringComparison.OrdinalIgnoreCase)
              || port.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)
              || System.Net.IPAddress.TryParse(port, out _) ? Network
            : Other;
    }
}

/// <summary>What a consumable is, in the six words the panel knows.</summary>
public static class SupplyType
{
    public static string FromIpp(string? markerType) => markerType?.ToLowerInvariant() switch
    {
        "toner" or "toner-cartridge" => "toner",
        "ink" or "ink-cartridge" or "ink-ribbon" => "ink",
        "opc" or "drum" or "developer" => "drum",
        "fuser" or "fuser-oil" or "fuser-cleaning-pad" => "fuser",
        "waste-toner" or "waste-ink" or "waste-wax" or "waste-water" => "waste",
        _ => "other",
    };

    /// <summary><c>prtMarkerSuppliesType</c> (RFC 3805).</summary>
    public static string FromMib(int? type) => type switch
    {
        3 or 21 => "toner",
        5 or 6 or 7 => "ink",
        9 or 10 => "drum",
        11 or 15 or 17 or 19 or 22 => "fuser",
        4 or 8 or 14 => "waste",
        _ => "other",
    };
}

/// <summary>
/// A network printer's Printer MIB (RFC 3805) over SNMP: supplies, trays,
/// serial number and the lifetime counter.
/// </summary>
public static class PrinterMib
{
    public const string SuppliesTable = "1.3.6.1.2.1.43.11.1.1";
    public const string ColorantTable = "1.3.6.1.2.1.43.12.1.1";
    public const string InputTable = "1.3.6.1.2.1.43.8.2.1";
    public const string SerialNumber = "1.3.6.1.2.1.43.5.1.1.17.1";
    public const string SystemDescription = "1.3.6.1.2.1.1.1.0";
    public const string LifeCount = "1.3.6.1.2.1.43.10.2.1.4.1.1";

    /// <param name="rows">Every OID read, as text, with its value as text.</param>
    public static PrinterDetails Interpret(string name, IReadOnlyDictionary<string, string> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // Column, device 1, row: <table>.<column>.1.<row>
        IEnumerable<int> RowsOf(string table, int column) => rows.Keys
            .Where(oid => oid.StartsWith($"{table}.{column}.1.", StringComparison.Ordinal))
            .Select(oid => int.TryParse(oid[($"{table}.{column}.1.".Length)..], NumberStyles.None, CultureInfo.InvariantCulture, out var row) ? row : -1)
            .Where(row => row >= 0)
            .Order();

        string? Text(string table, int column, int row)
            => rows.TryGetValue($"{table}.{column}.1.{row}", out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        long? Number(string table, int column, int row)
            => long.TryParse(Text(table, column, row), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

        var supplies = RowsOf(SuppliesTable, 6)
            .Select(row =>
            {
                var colorant = Number(SuppliesTable, 3, row);
                var color = colorant is > 0 ? Text(ColorantTable, 4, (int)colorant.Value) : null;

                return new PrinterSupply(
                    Text(SuppliesTable, 6, row) ?? $"Supply {row}",
                    SupplyType.FromMib((int?)Number(SuppliesTable, 5, row)),
                    color is null or "other" or "unknown" ? null : color,
                    Percent(Number(SuppliesTable, 9, row), Number(SuppliesTable, 8, row)));
            })
            .ToList();

        var trays = RowsOf(InputTable, 13)
            .Select(row => new PrinterTray(
                Text(InputTable, 13, row) ?? $"Tray {row}",
                Text(InputTable, 12, row),
                Percent(Number(InputTable, 10, row), Number(InputTable, 9, row))))
            .ToList();

        return new PrinterDetails(
            name,
            Model: rows.TryGetValue(SystemDescription, out var description) && !string.IsNullOrWhiteSpace(description) ? description.Trim() : null,
            SerialNumber: rows.TryGetValue(SerialNumber, out var serial) && !string.IsNullOrWhiteSpace(serial) ? serial.Trim() : null,
            Supplies: supplies,
            Trays: trays,
            LifetimePages: rows.TryGetValue(LifeCount, out var count)
                           && long.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages) && pages >= 0
                ? pages
                : null);
    }

    /// <summary>
    /// Level out of capacity. Either below zero is not a number: -1 "no
    /// limit", -2 "unknown", -3 "some left".
    /// </summary>
    public static int? Percent(long? level, long? capacity)
        => level is >= 0 && capacity is > 0 ? (int)Math.Clamp(level.Value * 100 / capacity.Value, 0, 100) : null;
}

/// <summary>A printer as Windows describes it: <c>Win32_Printer</c> and <c>Win32_PrinterDriver</c>.</summary>
public static class WmiPrinter
{
    /// <param name="printer">The properties of the <c>Win32_Printer</c> row, by name.</param>
    /// <param name="driver">The properties of its <c>Win32_PrinterDriver</c> row, when found.</param>
    public static PrinterDetails Describe(
        string name, IReadOnlyDictionary<string, object?> printer, IReadOnlyDictionary<string, object?>? driver = null)
    {
        ArgumentNullException.ThrowIfNull(printer);

        string? Text(IReadOnlyDictionary<string, object?>? source, string key)
            => source is not null && source.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text ? text : null;

        int? Number(string key)
            => printer.TryGetValue(key, out var value) && value is not null
               && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;

        var offline = printer.TryGetValue("WorkOffline", out var workOffline) && workOffline is true;
        var status = Number("PrinterStatus");
        var error = Number("DetectedErrorState");
        var port = Text(printer, "PortName");
        var reasons = ErrorReason(error) is { } reason ? new List<string> { reason } : [];

        if (offline && !reasons.Contains("offline"))
        {
            reasons.Add("offline");
        }

        return new PrinterDetails(
            name,
            Manufacturer: Text(driver, "Manufacturer"),
            Model: Text(printer, "DriverName"),
            DriverName: Text(printer, "DriverName"),
            DriverVersion: Text(driver, "DriverVersion") ?? Text(driver, "Version"),
            Connection: PrinterConnection.FromWindowsPort(port),
            Address: port is not null && port.StartsWith("IP_", StringComparison.OrdinalIgnoreCase) ? port[3..] : null,
            Uri: port,
            // Win32_Printer.PrinterStatus: 3 idle, 4 printing, 5 warming up, 6 stopped, 7 offline.
            State: status switch { 3 => "idle", 4 or 5 => "printing", 6 or 7 => "stopped", _ => null },
            StateReasons: reasons,
            Online: !offline && status != 7 && error != 9,
            LastError: Text(printer, "ErrorDescription"),
            Features: new PrinterFeatures(ResolutionDpi: Number("HorizontalResolution") is > 0 and var dpi ? dpi : null));
    }

    /// <summary><c>Win32_Printer.DetectedErrorState</c>, in the words IPP uses for the same thing.</summary>
    public static string? ErrorReason(int? state) => state switch
    {
        3 => "media-low",
        4 => "media-empty",
        5 => "toner-low",
        6 => "toner-empty",
        7 => "door-open",
        8 => "media-jam",
        9 => "offline",
        10 => "service-requested",
        11 => "output-area-full",
        _ => null,
    };
}

/// <summary>The jobs in CUPS's queue: <c>lpstat -W not-completed -o PRINTER</c>.</summary>
public static class LpstatJobs
{
    /// <summary>Reads lines like <c>HP_LaserJet-17  kiosk  1024  Mon 28 Sep 2026 10:00:00</c>.</summary>
    public static IReadOnlyList<SpoolerJob> Parse(string? output, string state = "pending")
    {
        var jobs = new List<SpoolerJob>();

        foreach (var line in (output ?? string.Empty).Split('\n'))
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (fields.Length < 3 || !fields[0].Contains('-', StringComparison.Ordinal))
            {
                continue;
            }

            DateTime? submitted = fields.Length >= 8
                                  && DateTime.TryParse(
                                      string.Join(' ', fields[3..]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var at)
                ? at
                : null;

            jobs.Add(new SpoolerJob(
                fields[0],
                long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size) ? size : null,
                state,
                submitted));
        }

        return jobs;
    }
}

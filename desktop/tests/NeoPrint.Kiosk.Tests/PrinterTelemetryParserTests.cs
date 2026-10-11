using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// What printers answer, read into <see cref="PrinterDetails"/> (plan section
/// 7.10; the parser tests plan section 11 asks for: IPP, SNMP and WMI sample
/// answers).
/// </summary>
public sealed class PrinterTelemetryParserTests
{
    // -----------------------------------------------------------------------
    // IPP: ipptool -tv ipp://localhost/printers/NAME get-printer-attributes.test
    // -----------------------------------------------------------------------

    private const string Ipp =
        "\"get-printer-attributes.test\":\n"
        + "    Get-Printer-Attributes:\n"
        + "        attributes-charset (charset) = utf-8\n"
        + "        printer-uri-supported (1setOf uri) = ipp://localhost/printers/HP_LaserJet\n"
        + "        device-uri (uri) = ipp://10.0.0.5/ipp/print\n"
        + "        printer-make-and-model (textWithoutLanguage) = HP Color LaserJet Pro M454dn\n"
        + "        printer-device-id (textWithoutLanguage) = MFG:HP;MDL:Color LaserJet Pro M454dn;CMD:PCL,PS;SN:VNB3R12345;\n"
        + "        printer-firmware-string-version (1setOf textWithoutLanguage) = 002_2306A\n"
        + "        printer-state (enum) = stopped\n"
        + "        printer-state-reasons (1setOf keyword) = media-empty-error,toner-low-warning\n"
        + "        printer-state-message (textWithoutLanguage) = Tray 2 is empty, load \\\"A4\\\"\n"
        + "        marker-names (1setOf nameWithoutLanguage) = Black Cartridge HP 415A,Cyan Cartridge HP 415A,Imaging Drum,Waste Toner\n"
        + "        marker-types (1setOf keyword) = toner-cartridge,toner-cartridge,opc,waste-toner\n"
        + "        marker-colors (1setOf nameWithoutLanguage) = #000000,#00FFFF,none,none\n"
        + "        marker-levels (1setOf integer) = 12,87,-3,-2\n"
        + "        printer-input-tray (1setOf octetString) = type=sheetFeedAutoRemovableTray;mediafeed=29700;mediaxfeed=21000;maxcapacity=250;level=50;status=0;name=Tray 1;,type=sheetFeedAutoRemovableTray;maxcapacity=550;level=-2;status=0;name=Tray 2;\n"
        + "        printer-impressions-completed (integer) = 48213\n"
        + "        color-supported (boolean) = true\n"
        + "        sides-supported (1setOf keyword) = one-sided,two-sided-long-edge,two-sided-short-edge\n"
        + "        media-supported (1setOf keyword) = iso_a4_210x297mm,iso_a5_148x210mm,na_letter_8.5x11in,custom_min_76x127mm\n"
        + "        media-source-supported (1setOf keyword) = auto,tray-1,tray-2\n"
        + "        printer-resolution-supported (1setOf resolution) = 600dpi,600x1200dpi\n"
        + "        document-format-supported (1setOf mimeMediaType) = application/pdf,application/postscript,image/jpeg\n";

    [Fact]
    public void An_ipp_printer_tells_who_it_is_how_it_is_and_what_it_has_left()
    {
        var details = IppPrinter.Describe("HP_LaserJet", IppAttributes.Parse(Ipp));

        details.Manufacturer.Should().Be("HP");
        details.Model.Should().Be("Color LaserJet Pro M454dn");
        details.SerialNumber.Should().Be("VNB3R12345");
        details.Firmware.Should().Be("002_2306A");
        details.Connection.Should().Be(PrinterConnection.Network);
        details.Address.Should().Be("10.0.0.5");
        details.Uri.Should().Be("ipp://10.0.0.5/ipp/print");

        details.State.Should().Be("stopped");
        details.StateReasons.Should().Equal("media-empty-error", "toner-low-warning");
        details.Online.Should().BeTrue("out of paper is not offline");
        details.LastError.Should().Be(
            "Tray 2 is empty, load \"A4\"", "a sentence is one value - its comma does not cut it - and ipptool's backslashes are undone");

        details.Supplies.Should().Equal(
            new PrinterSupply("Black Cartridge HP 415A", "toner", "#000000", 12),
            new PrinterSupply("Cyan Cartridge HP 415A", "toner", "#00FFFF", 87),
            new PrinterSupply("Imaging Drum", "drum", null, null),
            new PrinterSupply("Waste Toner", "waste", null, null));

        details.Trays.Should().Equal(new PrinterTray("Tray 1", null, 20), new PrinterTray("Tray 2", null, null));
        details.LifetimePages.Should().Be(48213);
    }

    [Fact]
    public void An_ipp_printer_tells_what_it_can_do()
    {
        var features = IppPrinter.Describe("HP_LaserJet", IppAttributes.Parse(Ipp)).Features!;

        features.Color.Should().BeTrue();
        features.Duplex.Should().BeTrue();
        features.PaperSizes.Should().Equal("A4", "A5", "Letter", "custom_min_76x127mm");
        features.Trays.Should().Equal("auto", "tray-1", "tray-2");
        features.ResolutionDpi.Should().Be(600, "600x1200 reaches 600 in both directions");
        features.NativeFormats.Should().Contain("application/postscript");
    }

    [Fact]
    public void What_an_ipp_printer_does_not_say_stays_unknown()
    {
        var details = IppPrinter.Describe(
            "Plain",
            IppAttributes.Parse("        printer-state (enum) = idle\n        printer-state-reasons (1setOf keyword) = none\n        device-uri (uri) = usb://Brother/HL-1110?serial=X1\n"));

        details.State.Should().Be("idle");
        details.StateReasons.Should().BeEmpty("\"none\" is the absence of a reason");
        details.Connection.Should().Be(PrinterConnection.Usb);
        details.Address.Should().BeNull("a USB printer has no host");
        details.SerialNumber.Should().BeNull();
        details.Supplies.Should().BeEmpty();
        details.Trays.Should().BeEmpty();
        details.LifetimePages.Should().BeNull();
        details.Features!.Color.Should().BeNull();
        details.Features.Duplex.Should().BeNull();
        details.Features.ResolutionDpi.Should().BeNull();

        IppPrinter.Describe("Gone", IppAttributes.Parse(null)).State.Should().BeNull();
    }

    [Fact]
    public void A_printer_that_cups_cannot_reach_is_offline()
    {
        var details = IppPrinter.Describe(
            "Far",
            IppAttributes.Parse("        printer-state (enum) = stopped\n        printer-state-reasons (1setOf keyword) = offline-report,paused\n"));

        details.Online.Should().BeFalse();
    }

    /// <summary>
    /// The answer of a real IPP Everywhere printer, as <c>ipptool -tv</c> printed
    /// it: CUPS 2.4.2's <c>ippeveprinter</c> ("NeoPrint Test Laser 4000", duplex,
    /// PDF and JPEG), asked on 2026-10-11. Unlike a CUPS queue it has no
    /// <c>marker-*</c> attributes - its supplies are in <c>printer-supply</c> -
    /// and its single-valued attributes are not written as <c>1setOf</c>.
    /// </summary>
    private const string RealIppEverywhere =
        "        color-supported (boolean) = true\n"
        + "        media-supported (1setOf keyword) = na_letter_8.5x11in,na_legal_8.5x14in,iso_a4_210x297mm,na_number-10_4.125x9.5in,iso_dl_110x220mm,na_index-3x5_3x5in,oe_photo-l_3.5x5in,na_index-4x6_4x6in,iso_a6_105x148mm,na_5x7_5x7in,iso_a5_148x210mm\n"
        + "        media-source-supported (1setOf keyword) = auto,main,photo\n"
        + "        printer-device-id (textWithoutLanguage) = MFG:Example;MDL:NeoPrint Test Laser 4000;CMD:PDF,JPEG;\n"
        + "        printer-input-tray (1setOf octetString) = type=sheetFeedAutoRemovableTray;mediafeed=0;mediaxfeed=0;maxcapacity=-2;level=-2;status=0;name=auto,type=sheetFeedAutoRemovableTray;mediafeed=0;mediaxfeed=0;maxcapacity=250;level=-2;status=0;name=main,type=sheetFeedAutoRemovableTray;mediafeed=0;mediaxfeed=0;maxcapacity=25;level=-2;status=0;name=photo\n"
        + "        printer-make-and-model (textWithoutLanguage) = Example NeoPrint Test Laser 4000\n"
        + "        printer-resolution-supported (resolution) = 600dpi\n"
        + "        printer-supply (1setOf octetString) = index=1;class=receptacleThatIsFilled;type=wasteInk;unit=percent;maxcapacity=100;level=25;colorantname=unknown;,index=2;class=supplyThatIsConsumed;type=ink;unit=percent;maxcapacity=100;level=75;colorantname=black;,index=3;class=supplyThatIsConsumed;type=ink;unit=percent;maxcapacity=100;level=50;colorantname=cyan;,\n"
        + "        printer-supply-description (1setOf textWithoutLanguage) = Ink Waste Tank,Black Ink,Cyan Ink,Magenta Ink,Yellow Ink\n"
        + "        sides-supported (1setOf keyword) = one-sided,two-sided-long-edge,two-sided-short-edge\n"
        + "        document-format-supported (1setOf mimeMediaType) = application/octet-stream,application/pdf,image/jpeg\n"
        + "        printer-state (enum) = idle\n"
        + "        printer-state-reasons (keyword) = none\n"
        + "        printer-uri-supported (1setOf uri) = ipp://localhost:8631/ipp/print,ipps://localhost:8631/ipp/print\n";

    [Fact]
    public void A_real_ipp_everywhere_printer_answer_is_read_whole()
    {
        var details = IppPrinter.Describe("Sim Printer", IppAttributes.Parse(RealIppEverywhere));

        details.Manufacturer.Should().Be("Example");
        details.Model.Should().Be("NeoPrint Test Laser 4000", "the device id is one value: the comma in CMD:PDF,JPEG does not cut it");
        details.SerialNumber.Should().BeNull("this printer gives none");
        details.State.Should().Be("idle");
        details.StateReasons.Should().BeEmpty();
        details.Online.Should().BeTrue();

        details.Supplies.Should().Equal(
            new PrinterSupply("Ink Waste Tank", "waste", null, 25),
            new PrinterSupply("Black Ink", "ink", "black", 75),
            new PrinterSupply("Cyan Ink", "ink", "cyan", 50));

        details.Trays.Should().Equal(
            new PrinterTray("auto", null, null),
            new PrinterTray("main", null, null),
            new PrinterTray("photo", null, null));

        details.Features!.Color.Should().BeTrue();
        details.Features.Duplex.Should().BeTrue();
        details.Features.PaperSizes.Should().Contain(["A4", "A5", "A6", "Letter", "Legal"]);
        details.Features.Trays.Should().Equal("auto", "main", "photo");
        details.Features.ResolutionDpi.Should().Be(600);
        details.Features.NativeFormats.Should().Equal("application/octet-stream", "application/pdf", "image/jpeg");
    }

    [Theory]
    [InlineData("usb://HP/LaserJet%201020", "Usb")]
    [InlineData("ipp://10.0.0.5/ipp/print", "Network")]
    [InlineData("socket://printer.local:9100", "Network")]
    [InlineData("ipp://localhost/printers/X", "Other")]
    [InlineData("cups-pdf:/", "Other")]
    [InlineData("", null)]
    public void How_a_printer_is_attached_is_read_from_its_address(string uri, string? expected)
        => PrinterConnection.FromUri(uri).Should().Be(expected);

    // -----------------------------------------------------------------------
    // SNMP: the Printer MIB of RFC 3805
    // -----------------------------------------------------------------------

    private static Dictionary<string, string> Mib() => new(StringComparer.Ordinal)
    {
        [PrinterMib.SystemDescription] = "KYOCERA ECOSYS P3145dn",
        [PrinterMib.SerialNumber] = "R4P0123456",
        [PrinterMib.LifeCount] = "152340",

        // prtMarkerSupplies: colorant index (3), type (5), description (6), max capacity (8), level (9)
        [$"{PrinterMib.SuppliesTable}.3.1.1"] = "1",
        [$"{PrinterMib.SuppliesTable}.5.1.1"] = "3",
        [$"{PrinterMib.SuppliesTable}.6.1.1"] = "TK-3160",
        [$"{PrinterMib.SuppliesTable}.8.1.1"] = "12500",
        [$"{PrinterMib.SuppliesTable}.9.1.1"] = "2500",
        [$"{PrinterMib.SuppliesTable}.3.1.2"] = "0",
        [$"{PrinterMib.SuppliesTable}.5.1.2"] = "4",
        [$"{PrinterMib.SuppliesTable}.6.1.2"] = "Waste Toner Box",
        [$"{PrinterMib.SuppliesTable}.8.1.2"] = "-2",
        [$"{PrinterMib.SuppliesTable}.9.1.2"] = "-3",
        [$"{PrinterMib.ColorantTable}.4.1.1"] = "black",

        // prtInput: max capacity (9), current level (10), media name (12), name (13)
        [$"{PrinterMib.InputTable}.9.1.1"] = "500",
        [$"{PrinterMib.InputTable}.10.1.1"] = "125",
        [$"{PrinterMib.InputTable}.12.1.1"] = "iso-a4",
        [$"{PrinterMib.InputTable}.13.1.1"] = "Cassette 1",
        [$"{PrinterMib.InputTable}.9.1.2"] = "100",
        [$"{PrinterMib.InputTable}.10.1.2"] = "-3",
        [$"{PrinterMib.InputTable}.13.1.2"] = "MP Tray",
    };

    [Fact]
    public void A_network_printer_mib_gives_toner_trays_serial_and_the_lifetime_count()
    {
        var details = PrinterMib.Interpret("Kyocera", Mib());

        details.Model.Should().Be("KYOCERA ECOSYS P3145dn");
        details.SerialNumber.Should().Be("R4P0123456");
        details.LifetimePages.Should().Be(152340);

        details.Supplies.Should().Equal(
            new PrinterSupply("TK-3160", "toner", "black", 20),
            new PrinterSupply("Waste Toner Box", "waste", null, null));

        details.Trays.Should().Equal(
            new PrinterTray("Cassette 1", "iso-a4", 25),
            new PrinterTray("MP Tray", null, null));
    }

    [Theory]
    [InlineData(2500L, 12500L, 20)]
    [InlineData(0L, 100L, 0)]
    [InlineData(-3L, 100L, null)]   // "some left"
    [InlineData(-2L, 100L, null)]   // unknown
    [InlineData(50L, -2L, null)]    // capacity unknown
    [InlineData(50L, 0L, null)]
    [InlineData(300L, 100L, 100)]   // a counter past its capacity is full, not 300%
    public void A_level_is_a_percentage_only_when_both_numbers_are_real(long level, long capacity, int? expected)
        => PrinterMib.Percent(level, capacity).Should().Be(expected);

    [Fact]
    public void A_printer_that_answers_nothing_over_snmp_adds_nothing()
    {
        var details = PrinterMib.Interpret("Silent", new Dictionary<string, string>());

        details.Supplies.Should().BeEmpty();
        details.Trays.Should().BeEmpty();
        details.SerialNumber.Should().BeNull();
        details.LifetimePages.Should().BeNull();
    }

    [Fact]
    public void Snmp_fills_in_what_the_first_source_did_not_have_and_overrides_nothing()
    {
        var ipp = new PrinterDetails("P", Model: "From IPP", State: "idle", Supplies: []);
        var snmp = PrinterMib.Interpret("P", Mib());

        var merged = ipp.FilledFrom(snmp);

        merged.Model.Should().Be("From IPP");
        merged.State.Should().Be("idle");
        merged.SerialNumber.Should().Be("R4P0123456");
        merged.Supplies.Should().HaveCount(2);
        merged.LifetimePages.Should().Be(152340);
        ipp.FilledFrom(null).Should().BeSameAs(ipp);
    }

    // -----------------------------------------------------------------------
    // WMI: Win32_Printer and Win32_PrinterDriver
    // -----------------------------------------------------------------------

    [Fact]
    public void A_windows_printer_is_described_from_wmi()
    {
        var details = WmiPrinter.Describe(
            "HP LaserJet",
            new Dictionary<string, object?>
            {
                ["DriverName"] = "HP Universal Printing PCL 6",
                ["PortName"] = "IP_10.0.0.7",
                ["PrinterStatus"] = (ushort)3,
                ["DetectedErrorState"] = (ushort)5,
                ["WorkOffline"] = false,
                ["HorizontalResolution"] = 600u,
            },
            new Dictionary<string, object?> { ["Manufacturer"] = "HP", ["DriverVersion"] = "61.250.1.24832" });

        details.Manufacturer.Should().Be("HP");
        details.DriverName.Should().Be("HP Universal Printing PCL 6");
        details.DriverVersion.Should().Be("61.250.1.24832");
        details.Connection.Should().Be(PrinterConnection.Network);
        details.Address.Should().Be("10.0.0.7");
        details.State.Should().Be("idle");
        details.StateReasons.Should().Equal("toner-low");
        details.Online.Should().BeTrue();
        details.Features!.ResolutionDpi.Should().Be(600);
    }

    [Fact]
    public void A_windows_printer_set_to_work_offline_is_offline_whatever_its_status_says()
    {
        var details = WmiPrinter.Describe(
            "USB printer",
            new Dictionary<string, object?> { ["PortName"] = "USB001", ["PrinterStatus"] = 3, ["WorkOffline"] = true });

        details.Online.Should().BeFalse();
        details.StateReasons.Should().Equal("offline");
        details.Connection.Should().Be(PrinterConnection.Usb);
        details.Address.Should().BeNull();
        details.DriverVersion.Should().BeNull();
    }

    [Theory]
    [InlineData(4, "media-empty")]
    [InlineData(8, "media-jam")]
    [InlineData(7, "door-open")]
    [InlineData(2, null)]
    [InlineData(null, null)]
    public void Windows_error_states_are_named_as_ipp_names_them(int? state, string? reason)
        => WmiPrinter.ErrorReason(state).Should().Be(reason);

    // -----------------------------------------------------------------------
    // The spooler
    // -----------------------------------------------------------------------

    [Fact]
    public void Jobs_waiting_in_cups_are_listed_with_their_size_and_time()
    {
        var jobs = LpstatJobs.Parse(
            "HP_LaserJet-17          kiosk             1024   Mon 28 Sep 2026 10:00:00\n"
            + "HP_LaserJet-18          kiosk           204800   Mon 28 Sep 2026 10:05:30\n"
            + "\tStatus: waiting\n");

        jobs.Should().HaveCount(2);
        jobs[0].Id.Should().Be("HP_LaserJet-17");
        jobs[0].SizeBytes.Should().Be(1024);
        jobs[0].State.Should().Be("pending");
        jobs[0].SubmittedAt.Should().NotBeNull();
        jobs[1].SizeBytes.Should().Be(204800);

        LpstatJobs.Parse(string.Empty).Should().BeEmpty();
        LpstatJobs.Parse("lpstat: No destinations added.\n").Should().BeEmpty();
    }
}

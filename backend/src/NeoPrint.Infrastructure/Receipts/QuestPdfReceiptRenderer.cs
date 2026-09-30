using System.Globalization;
using System.Reflection;

using NeoPrint.Application.Receipts;

using QRCoder;

using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NeoPrint.Infrastructure.Receipts;

/// <summary>
/// The receipt PDF (plan section 2.12; docs/DESIGN_SYSTEM.md §5.5): what was
/// printed, what it cost, how it was paid, and the fiscal receipt with its
/// verification QR - or that it is still being prepared.
/// </summary>
public sealed class QuestPdfReceiptRenderer : IReceiptRenderer
{
    private static readonly Lazy<bool> Initialised = new(Initialise, LazyThreadSafetyMode.ExecutionAndPublication);

    public byte[] Render(ReceiptContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _ = Initialised.Value;

        var labels = content.Labels;
        string L(string key) => labels.TryGetValue(key, out var text) ? text : key;
        var language = content.Language;

        return Document.Create(document => document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(ReceiptTokens.PageMargin);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(style => style.FontFamily(ReceiptTokens.FontFamily).FontSize(ReceiptTokens.Body).FontColor(ReceiptTokens.Text));

                page.Header().Column(header =>
                {
                    header.Spacing(ReceiptTokens.Space4);
                    header.Item().Row(row =>
                    {
                        row.RelativeItem().Text("NeoPrint").FontSize(ReceiptTokens.Title).SemiBold().FontColor(ReceiptTokens.Accent);
                        row.RelativeItem().AlignRight().Text($"{L("receipt.title")} {content.Number}").FontSize(ReceiptTokens.Title).SemiBold();
                    });
                    header.Item().LineHorizontal(ReceiptTokens.DividerThickness).LineColor(ReceiptTokens.Accent);
                });

                page.Content().PaddingTop(ReceiptTokens.Space16).Column(body =>
                {
                    body.Spacing(ReceiptTokens.Space12);

                    body.Item().Column(facts =>
                    {
                        facts.Spacing(ReceiptTokens.Space4);
                        Fact(facts, L("receipt.number"), content.Number);
                        Fact(facts, L("receipt.date"), Application.Common.BakuTime.From(content.PaidAt).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));

                        if (content.KioskCode is { } kiosk)
                        {
                            Fact(facts, L("receipt.kiosk"), content.KioskAddress is { Length: > 0 } address ? $"{kiosk} — {address}" : kiosk);
                        }

                        if (content.JobCode is { } job)
                        {
                            Fact(facts, L("receipt.job"), content.PrintSummary is { } summary ? $"{job} · {summary}" : job);
                        }

                        if (content.FileName is { } file)
                        {
                            Fact(facts, L("receipt.file"), file);
                        }
                    });

                    body.Item().Column(lines =>
                    {
                        foreach (var line in content.Lines)
                        {
                            lines.Item().BorderBottom(ReceiptTokens.DividerThickness).BorderColor(ReceiptTokens.Divider)
                                .PaddingVertical(ReceiptTokens.Space8).Row(row =>
                                {
                                    row.RelativeItem().Text(line.Text);
                                    row.ConstantItem(120).AlignRight().Text(Money(line.Amount, language));
                                });
                        }

                        lines.Item().Background(ReceiptTokens.RowBackground).Padding(ReceiptTokens.Space8).Row(row =>
                        {
                            row.RelativeItem().Text(L("receipt.total")).FontSize(ReceiptTokens.Section).SemiBold();
                            row.ConstantItem(120).AlignRight().Text(Money(content.Total, language)).FontSize(ReceiptTokens.Section).SemiBold().FontColor(ReceiptTokens.Accent);
                        });
                    });

                    if (content.PaidWith.Count > 0)
                    {
                        body.Item().Column(paid =>
                        {
                            paid.Spacing(ReceiptTokens.Space4);
                            paid.Item().Text(L("receipt.paidWith")).FontSize(ReceiptTokens.Section).SemiBold();

                            foreach (var part in content.PaidWith)
                            {
                                paid.Item().Row(row =>
                                {
                                    row.RelativeItem().Text(part.Text).FontColor(ReceiptTokens.TextSecondary);
                                    row.ConstantItem(120).AlignRight().Text(Money(part.Amount, language));
                                });
                            }
                        });
                    }

                    body.Item().Element(container => Fiscal(container, content, L));
                });

                page.Footer().Column(footer =>
                {
                    footer.Spacing(ReceiptTokens.Space4);
                    footer.Item().Text(L("receipt.notFiscal")).FontSize(ReceiptTokens.Caption).FontColor(ReceiptTokens.TextSecondary);
                    footer.Item().Text(L("receipt.footer")).FontSize(ReceiptTokens.Caption).FontColor(ReceiptTokens.TextSecondary);
                });
            }))
            .GeneratePdf();
    }

    private static void Fiscal(IContainer container, ReceiptContent content, Func<string, string> label)
    {
        if (content.Fiscal is not { Issued: true } fiscal)
        {
            // Colour and words both: the status is never colour alone.
            container.Background(ReceiptTokens.WarningBackground).Padding(ReceiptTokens.Space12).Row(row =>
            {
                row.ConstantItem(ReceiptTokens.Space24).Text("!").FontSize(ReceiptTokens.Section).SemiBold().FontColor(ReceiptTokens.Warning);
                row.RelativeItem().Text(label("receipt.fiscal.pending")).FontColor(ReceiptTokens.Text);
            });
            return;
        }

        container.Border(ReceiptTokens.DividerThickness).BorderColor(ReceiptTokens.Divider).Padding(ReceiptTokens.Space12).Row(row =>
        {
            row.RelativeItem().Column(column =>
            {
                column.Spacing(ReceiptTokens.Space4);
                column.Item().Text(label("receipt.fiscal.title")).FontSize(ReceiptTokens.Section).SemiBold();
                Fact(column, label("receipt.fiscal.number"), fiscal.DocumentNumber ?? "-");
                Fact(column, label("receipt.fiscal.id"), fiscal.FiscalId ?? "-");
                Fact(column, label("receipt.fiscal.terminal"), fiscal.TerminalId ?? "-");

                if (fiscal.VerificationUrl is { } url)
                {
                    column.Item().Text(label("receipt.fiscal.check")).FontSize(ReceiptTokens.Caption).FontColor(ReceiptTokens.TextSecondary);
                    column.Item().Text(url).FontSize(ReceiptTokens.Caption).FontColor(ReceiptTokens.Accent);
                }
            });

            if (content.QrContent is { } qr)
            {
                row.ConstantItem(ReceiptTokens.QrSize).Background(ReceiptTokens.QrBackground).Image(Qr(qr));
            }
        });
    }

    private static void Fact(ColumnDescriptor column, string name, string value)
        => column.Item().Row(row =>
        {
            row.ConstantItem(140).Text(name).FontColor(ReceiptTokens.TextSecondary);
            row.RelativeItem().Text(value);
        });

    /// <summary>The QR with its own quiet zone (a white margin the scanner needs).</summary>
    private static byte[] Qr(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);

        return new PngByteQRCode(data).GetGraphic(8, drawQuietZones: true);
    }

    /// <summary>Money as docs/DESIGN_SYSTEM.md §6 writes it: <c>1 240,00 ₼</c> (az, ru), <c>1,240.00 ₼</c> (en).</summary>
    public static string Money(decimal amount, string language)
    {
        var format = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        format.NumberDecimalSeparator = language == "en" ? "." : ",";
        format.NumberGroupSeparator = language == "en" ? "," : " ";

        return $"{amount.ToString("#,0.00", format)} ₼";
    }

    private static bool Initialise()
    {
        QuestPDF.Settings.License = LicenseType.Community;

        // A missing glyph would print as an empty box; better to fail the test that renders it.
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = true;

        using var font = Assembly.GetExecutingAssembly().GetManifestResourceStream("NeoPrint.Receipts.Inter.ttf")
                         ?? throw new InvalidOperationException("The receipt font is not embedded.");
        QuestPDF.Drawing.FontManager.RegisterFontFromStream(font);

        return true;
    }
}

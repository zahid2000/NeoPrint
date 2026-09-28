using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace NeoPrint.Kiosk.Printing;

/// <summary>A page and how far to turn it: the file editor's page operations (plan section 2.2).</summary>
public sealed record PageChange(int Page, int Rotate);

/// <summary>
/// PDF structure with PDFsharp: how many pages, which of them, in what order,
/// turned how, and several to a sheet (plan sections 2.2, 2.3, 7.4).
/// </summary>
public static class PdfTools
{
    /// <summary>Paper sizes in points.</summary>
    private static readonly Dictionary<string, (double Width, double Height)> Papers = new(StringComparer.Ordinal)
    {
        ["A4"] = (595.28, 841.89),
        ["A3"] = (841.89, 1190.55),
    };

    /// <summary>Pages in the document.</summary>
    /// <exception cref="PrintingException">It is not a PDF PDFsharp can open (<c>pdf.damaged</c>).</exception>
    public static int PageCount(string pdfPath)
    {
        try
        {
            using var document = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
            return document.PageCount;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new PrintingException(IsEncrypted(exception) ? "pdf.passwordRequired" : "pdf.damaged", exception);
        }
    }

    /// <summary>
    /// A new PDF of the given pages, in the given order, each turned by its
    /// rotation on top of what it had (the file editor).
    /// </summary>
    public static void ApplyChanges(string sourcePath, string targetPath, IReadOnlyList<PageChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.Count == 0)
        {
            throw new PrintingException("files.edit.keepOnePage");
        }

        try
        {
            using var input = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
            using var result = new PdfDocument();

            foreach (var change in changes)
            {
                if (change.Page < 1 || change.Page > input.PageCount)
                {
                    throw new PrintingException("edit.pagesFailed");
                }

                var page = result.AddPage(input.Pages[change.Page - 1]);
                page.Rotate = (((page.Rotate + change.Rotate) % 360) + 360) % 360;
            }

            result.Save(targetPath);
        }
        catch (PrintingException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new PrintingException("edit.pagesFailed", exception);
        }
    }

    /// <summary>
    /// The file that goes to the printer: the chosen pages in order, laid out
    /// several to a sheet when asked (plan section 7.4: range and N-up are in
    /// the PDF; copies, sides and colour go with it).
    /// </summary>
    /// <param name="pages">1-based pages, as the range selected them.</param>
    /// <param name="pagesPerSheet">1, 2 or 4.</param>
    /// <param name="paper">A4 or A3: the sheet the N-up layout is drawn on.</param>
    public static void BuildForPrint(string sourcePath, string targetPath, IReadOnlyList<int> pages, int pagesPerSheet, string paper)
    {
        ArgumentNullException.ThrowIfNull(pages);

        if (pagesPerSheet <= 1)
        {
            ApplyChanges(sourcePath, targetPath, [.. pages.Select(page => new PageChange(page, 0))]);
            return;
        }

        var (paperWidth, paperHeight) = Papers.GetValueOrDefault(paper, Papers["A4"]);

        // Two to a sheet side by side on a landscape sheet; four in a grid on a
        // portrait one - the way every copier lays them out.
        var (columns, rows, landscape) = pagesPerSheet switch
        {
            2 => (2, 1, true),
            4 => (2, 2, false),
            _ => throw new PrintingException("printJobs.pagesPerSheetInvalid"),
        };

        var sheetWidth = landscape ? paperHeight : paperWidth;
        var sheetHeight = landscape ? paperWidth : paperHeight;
        const double Margin = 18;

        try
        {
            using var form = XPdfForm.FromFile(sourcePath);
            using var result = new PdfDocument();

            for (var start = 0; start < pages.Count; start += pagesPerSheet)
            {
                var sheet = result.AddPage();
                sheet.Width = XUnit.FromPoint(sheetWidth);
                sheet.Height = XUnit.FromPoint(sheetHeight);

                using var graphics = XGraphics.FromPdfPage(sheet);

                var cellWidth = (sheetWidth - (Margin * (columns + 1))) / columns;
                var cellHeight = (sheetHeight - (Margin * (rows + 1))) / rows;

                for (var slot = 0; slot < pagesPerSheet && start + slot < pages.Count; slot++)
                {
                    form.PageNumber = pages[start + slot];

                    var scale = Math.Min(cellWidth / form.PointWidth, cellHeight / form.PointHeight);
                    var width = form.PointWidth * scale;
                    var height = form.PointHeight * scale;
                    var column = slot % columns;
                    var row = slot / columns;
                    var x = Margin + (column * (cellWidth + Margin)) + ((cellWidth - width) / 2);
                    var y = Margin + (row * (cellHeight + Margin)) + ((cellHeight - height) / 2);

                    graphics.DrawImage(form, new XRect(x, y, width, height));
                }
            }

            result.Save(targetPath);
        }
        catch (PrintingException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new PrintingException("printer.spoolFailed", exception);
        }
    }

    private static bool IsEncrypted(Exception exception)
        => exception.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
           || exception.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Something about a document could not be done; the key says what, in the kiosk's catalog.</summary>
public sealed class PrintingException : Exception
{
    public PrintingException(string messageKey, Exception? inner = null)
        : base(messageKey, inner)
    {
        MessageKey = messageKey;
    }

    public string MessageKey { get; }
}

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace NeoPrint.Shared.Tests.Layout;

/// <summary>
/// Reads the streams of a PDF the strict way (T-198): a Flate stream has to
/// be a whole zlib stream - it inflates, and it ends in the Adler-32 of what
/// it held. Renderers forgive a stream that does not; a printer may not.
/// </summary>
internal static partial class PdfStreams
{
    /// <summary>The Flate streams that are not whole zlib streams, described; empty when all are.</summary>
    public static IReadOnlyList<string> BrokenFlate(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var broken = new List<string>();

        foreach (Match match in StreamStart().Matches(text))
        {
            var dictionary = match.Groups["dictionary"].Value;

            if (!OuterFlate().IsMatch(dictionary))
            {
                continue;
            }

            var start = match.Index + match.Length;
            var length = DirectLength().Match(dictionary) is { Success: true } direct
                ? int.Parse(direct.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                : text.IndexOf("endstream", start, StringComparison.Ordinal) - start;
            var data = pdf.AsSpan(start, length).ToArray();
            var problem = Problem(data);

            if (problem is not null)
            {
                broken.Add($"stream at byte {start} ({length} bytes): {problem}");
            }
        }

        return broken;
    }

    private static string? Problem(byte[] data)
    {
        if (data.Length < 6)
        {
            return "too short to be a zlib stream";
        }

        byte[] inflated;

        try
        {
            using var zlib = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            inflated = output.ToArray();
        }
        catch (InvalidDataException error)
        {
            return error.Message;
        }

        var written = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(data.Length - 4));

        return written == Adler32(inflated) ? null : "it does not end in the checksum of its data";
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;

        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    [GeneratedRegex(@"<<(?<dictionary>(?:[^<>]|<<[^<>]*>>|<[^<>]*>)*)>>\s*stream\r?\n", RegexOptions.Singleline)]
    private static partial Regex StreamStart();

    [GeneratedRegex(@"/Filter\s*(/FlateDecode|\[\s*/FlateDecode)")]
    private static partial Regex OuterFlate();

    [GeneratedRegex(@"/Length\s+(\d+)(?!\s+\d+\s+R)")]
    private static partial Regex DirectLength();
}

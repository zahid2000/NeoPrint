using System.IO.Compression;
using System.Text;

using NeoPrint.Crypto.Privacy;

namespace NeoPrint.Kiosk.Core.Logging;

/// <summary>
/// What an <c>UploadLogs</c> command sends (docs/KIOSK_UPDATES.md section 6,
/// T-321): the kiosk's and the agent's log files of the last days, cleaned once
/// more and packed into one gzip file no larger than the backend takes - the
/// oldest lines go first when it would be.
/// </summary>
public static class LogCollector
{
    /// <summary>How far back the files go.</summary>
    public const int Days = 3;

    private const long MaxRead = 200L * 1024 * 1024;

    public static byte[] Collect(string directory, long maxCompressedBytes, DateTime now)
    {
        var lines = new List<string>();
        long read = 0;

        if (Directory.Exists(directory))
        {
            var since = DateOnly.FromDateTime(now).AddDays(-(Days - 1));
            var files = Directory.GetFiles(directory, "*.log")
                .Select(file => (File: file, Day: DayOf(file)))
                .Where(entry => entry.Day is { } day && day >= since)
                .OrderBy(entry => entry.Day)
                .ThenBy(entry => entry.File, StringComparer.Ordinal);

            foreach (var (file, _) in files)
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    lines.Add($"# {Path.GetFileName(file)}");
                    while (reader.ReadLine() is { } line && read < MaxRead)
                    {
                        read += line.Length + 1;
                        lines.Add(LogRedactor.Redact(line));
                    }
                }
                catch (IOException)
                {
                    lines.Add($"# {Path.GetFileName(file)}: could not be read");
                }
            }
        }

        if (lines.Count == 0)
        {
            lines.Add("# no log files");
        }

        // Too large: the older half goes, until it fits.
        var start = 0;
        while (true)
        {
            var packed = Pack(lines, start);
            if (packed.Length <= maxCompressedBytes || lines.Count - start <= 1)
            {
                return packed;
            }

            start += (lines.Count - start) / 2;
        }
    }

    private static byte[] Pack(List<string> lines, int start)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
        {
            if (start > 0)
            {
                writer.WriteLine($"# {start} older lines left out to stay within the upload limit");
            }

            for (var i = start; i < lines.Count; i++)
            {
                writer.WriteLine(lines[i]);
            }
        }

        return buffer.ToArray();
    }

    private static DateOnly? DayOf(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var dash = name.LastIndexOf('-');
        return dash > 0 && DateOnly.TryParseExact(name[(dash + 1)..], "yyyyMMdd", out var day) ? day : null;
    }
}

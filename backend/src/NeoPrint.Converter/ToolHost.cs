using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeoPrint.Converter;

/// <summary>A document the tool cannot work with, as a message key for its owner.</summary>
internal sealed class ToolException : Exception
{
    public ToolException(string key, params (string Name, object? Value)[] arguments)
        : base(key)
    {
        Key = key;
        Arguments = arguments.ToDictionary(
            argument => argument.Name,
            argument => argument.Value switch
            {
                null => string.Empty,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                var value => value.ToString() ?? string.Empty,
            },
            StringComparer.Ordinal);
    }

    public string Key { get; }

    public IReadOnlyDictionary<string, string> Arguments { get; }
}

/// <summary>The request was not one the tool carries out: a caller's mistake, not a document's.</summary>
internal sealed class ToolUsageException(string message) : Exception(message);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ToolRequest))]
[JsonSerializable(typeof(ToolResponse))]
internal sealed partial class ToolJson : JsonSerializerContext;

/// <summary>
/// Carries out one operation on one user's file and exits
/// (docs/adr/ADR-0005-isolated-converter.md).
/// </summary>
/// <remarks>
/// <para>
/// A process per file: a parser bug that corrupts memory takes this process
/// down and nothing else, and no state of one user's document is left for the
/// next one. The caller put a memory and CPU limit on it before it started.
/// </para>
/// <para>
/// It reads and writes only bare file names in its working directory - the
/// operation's own directory in the spool - and writes nothing about the
/// document to stderr beyond the kind of failure.
/// </para>
/// </remarks>
internal static class ToolHost
{
    /// <summary>The response was written; whether the operation worked is in it.</summary>
    public const int Answered = 0;

    /// <summary>The call itself was wrong (sysexits EX_USAGE).</summary>
    public const int Usage = 64;

    /// <summary>The response could not be written (sysexits EX_IOERR).</summary>
    public const int NoAnswer = 74;

    public static int Run(string[] arguments, string directory)
    {
        if (arguments.Length != 1)
        {
            Console.Error.WriteLine("Usage: NeoPrint.Converter <operation>, in the operation's directory.");
            return Usage;
        }

        PreferAsOomVictim();

        var operation = arguments[0];
        ToolResponse response;

        try
        {
            var request = ReadRequest(directory);

            response = operation switch
            {
                ToolContract.PdfInfo => PdfTool.Info(directory, request),
                ToolContract.PdfRender => PdfTool.Render(directory, request),
                ToolContract.ImageToPdf => ImageTool.ToPdf(directory, request),
                ToolContract.EditImage => ImageTool.Edit(directory, request),
                ToolContract.EditPages => PagesTool.Edit(directory, request),
                ToolContract.LayoutRender => LayoutTool.Render(directory, request),
                _ => throw new ToolUsageException($"Unknown operation '{operation}'."),
            };
        }
        catch (ToolUsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return Usage;
        }
        catch (ToolException exception)
        {
            response = new ToolResponse(false, exception.Key, exception.Arguments);
        }
#pragma warning disable CA1031 // Whatever a parser throws is an answer, not a crash.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // The type only: a parser's message can quote the document.
            Console.Error.WriteLine($"{operation} failed: {exception.GetType().Name}.");
            response = new ToolResponse(false);
        }

        try
        {
            var path = Path.Combine(directory, ToolContract.ResponseFile);
            var temporary = path + ".tmp";

            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(response, ToolJson.Default.ToolResponse));
            File.Move(temporary, path, overwrite: true);

            return Answered;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"The response could not be written: {exception.GetType().Name}.");
            return NoAnswer;
        }
    }

    private static ToolRequest ReadRequest(string directory)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(directory, ToolContract.RequestFile));

            return JsonSerializer.Deserialize(stream, ToolJson.Default.ToolRequest)
                   ?? throw new ToolUsageException("The request is empty.");
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new ToolUsageException($"The request could not be read: {exception.GetType().Name}.");
        }
    }

    /// <summary>
    /// If the container runs out of memory, the kernel should end this
    /// process - the one holding a user's file - rather than the worker that
    /// serves everybody. Raising one's own score needs no privilege.
    /// </summary>
    private static void PreferAsOomVictim()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            File.WriteAllText("/proc/self/oom_score_adj", "1000");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only a preference; the limits still hold without it.
        }
    }
}

/// <summary>The files an operation names: bare names in its own directory, nothing else.</summary>
internal static class ToolFiles
{
    /// <exception cref="ToolUsageException">The name is missing or points outside the directory.</exception>
    public static string Local(string directory, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
            || name.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            throw new ToolUsageException("A file must be a bare name in the operation's directory.");
        }

        return Path.Combine(directory, name);
    }

    /// <exception cref="ToolUsageException">The file is not there.</exception>
    public static string Existing(string directory, string? name)
    {
        var path = Local(directory, name);

        return File.Exists(path) ? path : throw new ToolUsageException("The input file is missing.");
    }
}

internal static class Program
{
    public static int Main(string[] arguments) => ToolHost.Run(arguments, Environment.CurrentDirectory);
}

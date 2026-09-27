using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

using NeoPrint.Infrastructure.Conversion;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// Hosts the converter worker when the API binary is started with
/// <c>--converter-worker</c> (docs/adr/ADR-0005-isolated-converter.md).
/// </summary>
/// <remarks>
/// Deliberately no web host, no configuration providers and no secret store:
/// the container this runs in has no network and nothing to authenticate to.
/// The one input is the spool directory - the first argument, or
/// <c>NEOPRINT_CONVERTER_SPOOL</c>, or <c>/spool</c>.
/// </remarks>
public static class ConverterHost
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        var spool = arguments.FirstOrDefault()
                    ?? Environment.GetEnvironmentVariable("NEOPRINT_CONVERTER_SPOOL")
                    ?? "/spool";

        using var loggers = LoggerFactory.Create(logging => logging.AddJsonConsole(options =>
        {
            options.UseUtcTimestamp = true;
        }));

        var logger = loggers.CreateLogger("NeoPrint.Converter");

        if (!Directory.Exists(spool))
        {
            logger.LogCritical("The spool {Spool} does not exist; nothing to watch.", spool);
            return 2;
        }

        using var stop = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        var worker = new ConverterWorker(
            spool,
            new SandboxedProcess(loggers.CreateLogger<SandboxedProcess>()),
            ConverterWorker.DefaultPrograms,
            logger);

        await worker.RunAsync(stop.Token);

        logger.LogInformation("Converter worker stopped.");
        return 0;
    }
}

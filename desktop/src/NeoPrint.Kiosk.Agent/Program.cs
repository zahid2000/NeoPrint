using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NeoPrint.Kiosk.Agent;

// ---------------------------------------------------------------------------
// The kiosk agent (plan section 7.12).
//
// A system service that starts before the UI and keeps the machine working:
// it launches and watches the UI, applies updates inside the update window,
// carries out remote commands and uploads logs. Running as a service is what
// lets it restart a UI that has crashed and survive the UI being replaced
// during an update.
//
// Windows Service as LocalSystem; systemd unit as the neoprint account with a
// narrow sudoers rule. The service and the UI watchdog are here; the updater
// and the remote commands are plan step 7.
// ---------------------------------------------------------------------------

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("NEOPRINT_AGENT_");

// The same binary runs under both service managers; which one is detected at
// startup, so nothing has to be configured per platform.
builder.Services.AddWindowsService(options => options.ServiceName = "NeoPrintAgent");
builder.Services.AddSystemd();

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddHostedService<KioskUiWatchdog>();

var host = builder.Build();

host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("NeoPrint.Kiosk.Agent")
    .LogInformation("The NeoPrint agent is starting.");

await host.RunAsync();


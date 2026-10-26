using System.Runtime.InteropServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NeoPrint.Kiosk.Agent;
using NeoPrint.Kiosk.Agent.Updates;
using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Logging;
using NeoPrint.Kiosk.Core.Updates;

// ---------------------------------------------------------------------------
// The kiosk agent (plan section 7.12).
//
// A system service that starts before the UI and keeps the machine working:
// it launches and watches the UI, applies updates and carries out the remote
// commands the UI hands it (docs/KIOSK_UPDATES.md, T-321). Running as a
// service is what lets it restart a UI that has crashed and survive the UI
// being replaced during an update.
//
// It holds no credentials and never talks to the backend: the UI - the
// backend's only client - downloads, and the agent checks and applies.
//
// Windows Service as LocalSystem; systemd unit as the neoprint account with a
// narrow sudoers rule.
// ---------------------------------------------------------------------------

// The root mode (T-321): the root-owned copy of this binary, run through the
// one sudo rule, installs and runs the provisioning scripts - never the
// unprivileged agent itself.
if (args.Length > 0 && args[0] == "provisioning")
{
    return await RootProvisioning.RunAsync(args, UpdatePaths.ForThisMachine(), BuildVersion.Of(typeof(Program).Assembly), CancellationToken.None);
}

// The checker (T-327): a provisioning script asks whether a package it
// downloaded was signed with the keys compiled into this build.
if (args.Length > 0 && args[0] == "verify-package")
{
    return await PackageVerification.RunAsync(args, BuildVersion.Of(typeof(Program).Assembly), Console.Out, Console.Error, CancellationToken.None);
}

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("NEOPRINT_AGENT_");

// The same binary runs under both service managers; which one is detected at
// startup, so nothing has to be configured per platform.
builder.Services.AddWindowsService(options => options.ServiceName = "NeoPrintAgent");
builder.Services.AddSystemd();

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
var agentOptions = builder.Configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();

var platformPaths = UpdatePaths.ForThisMachine();
var paths = new UpdatePaths(
    Or(agentOptions.InstallRoot, platformPaths.InstallRoot),
    Or(agentOptions.UpdatesRoot, platformPaths.UpdatesRoot),
    Or(agentOptions.AgentRoot, platformPaths.AgentRoot));

var logDirectory = Or(
    agentOptions.LogDirectory,
    RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NeoPrint", "logs")
        : "/var/log/neoprint");
builder.Logging.AddProvider(new FileLoggerProvider(logDirectory, "agent"));

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<KioskUiWatchdog>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<KioskUiWatchdog>());
builder.Services.AddSingleton<IProvisioningRunner, ProvisioningRunner>();
builder.Services.AddSingleton<IUiHost>(provider => (Or(agentOptions.UiHost, Default(agentOptions))) switch
{
    "watchdog" => new WatchdogUiHost(provider.GetRequiredService<KioskUiWatchdog>()),
    "shell-launcher" => new ShellLauncherUiHost(provider.GetRequiredService<ILogger<ShellLauncherUiHost>>()),
    _ => new SystemdUiHost(provider.GetRequiredService<ILogger<SystemdUiHost>>()),
});
builder.Services.AddSingleton(provider =>
{
    var lifetime = provider.GetRequiredService<IHostApplicationLifetime>();
    return new UpdateApplier(
        paths,
        new ReleasePackageCheck(),
        provider.GetRequiredService<IUiHost>(),
        provider.GetRequiredService<IProvisioningRunner>(),
        provider.GetRequiredService<IClock>(),
        provider.GetRequiredService<ILogger<UpdateApplier>>(),
        BuildVersion.Of(typeof(Program).Assembly),
        // The service manager starts the agent again, from agent/current.
        () => lifetime.StopApplication());
});
builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();

host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("NeoPrint.Kiosk.Agent")
    .LogInformation("The NeoPrint agent {Version} is starting.", BuildVersion.Of(typeof(Program).Assembly));

await host.RunAsync();
return 0;

static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

static string Default(AgentOptions options)
    => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "shell-launcher"
        : string.IsNullOrWhiteSpace(options.UiExecutablePath) ? "systemd" : "watchdog";

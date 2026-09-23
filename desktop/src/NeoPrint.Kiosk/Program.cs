using System.Runtime.Versioning;

using Avalonia;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk;

/// <summary>
/// Entry point for the kiosk application.
/// </summary>
/// <remarks>
/// Plan section 7.11: one build per operating system runs on every kiosk. The
/// only thing that can be given on the command line is the API address and an
/// enrollment token - neither is a secret, and neither identifies a kiosk.
/// </remarks>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var services = BuildServices(args);

        try
        {
            return BuildAvaloniaApp(services)
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            // Plan section 7.1: a global crash handler. The agent restarts the
            // UI, so the job here is to leave a usable record behind.
            TryLogFatal(services, exception);
            return 1;
        }
    }

    /// <summary>Used by the Avalonia designer, which needs a parameterless builder.</summary>
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(BuildServices([]));

    private static AppBuilder BuildAvaloniaApp(IServiceProvider services) => AppBuilder
        .Configure(() => new App { Services = services })
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();

    private static IServiceProvider BuildServices(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables("NEOPRINT_KIOSK_")
            // --api-url and --enrollment-token, as the install script passes
            // them (plan section 7.11).
            .AddCommandLine(args, new Dictionary<string, string>
            {
                ["--api-url"] = "Kiosk:ApiBaseUrl",
                ["--enrollment-token"] = "Kiosk:EnrollmentToken",
            })
            .Build();

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.Configure<KioskSettings>(configuration.GetSection("Kiosk"));

        services.AddLogging(logging => logging
            .AddConfiguration(configuration.GetSection("Logging"))
            .AddSimpleConsole(options => options.SingleLine = true));

        RegisterPlatform(services, configuration);

        services.AddSingleton<DeviceEnrollmentService>();

        services.AddHttpClient<KioskApiClient>((provider, client) =>
        {
            var settings = provider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<KioskSettings>>().Value;

            client.BaseAddress = new Uri(settings.ApiBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Registers the implementations for the operating system this process is
    /// running on (plan section 7.6).
    /// </summary>
    /// <remarks>
    /// Detected at runtime, so the same code path serves both platforms and
    /// nothing has to be configured per machine.
    /// </remarks>
    private static void RegisterPlatform(IServiceCollection services, IConfiguration configuration)
    {
        var useFakePrinter = configuration.GetValue("Kiosk:UseFakePrinter", false);

        if (OperatingSystem.IsWindows())
        {
            RegisterWindows(services);
        }
        else if (OperatingSystem.IsLinux())
        {
            RegisterLinux(services);
        }
        else
        {
            // macOS and anything else: a developer machine. The fake platform
            // lets the UI and the enrollment flow be worked on anywhere.
            services.AddSingleton<IAppPaths>(_ => new FakeAppPaths());
            services.AddSingleton<IDeviceKeyStore, FakeDeviceKeyStore>();
            services.AddSingleton<IMachineFingerprintProvider, FakeMachineFingerprintProvider>();
            services.AddSingleton<ISystemControl, FakeSystemControl>();
            services.AddSingleton<IRemovableDriveWatcher, FakeRemovableDriveWatcher>();
            services.AddSingleton<IKioskLockdown, FakeKioskLockdown>();
            useFakePrinter = true;
        }

        if (useFakePrinter)
        {
            // Prints into a folder, so the whole flow works on a machine with
            // no printer and in CI (plan section 7.6).
            services.AddSingleton<IPrinterService>(_ => new FakePrinterService());
        }
    }

    // Annotated so the analyzer accepts the Windows-only types. The call site
    // is guarded by OperatingSystem.IsWindows(), which it does understand.
    [SupportedOSPlatform("windows")]
    private static void RegisterWindows(IServiceCollection services)
    {
#if WINDOWS
        services.AddSingleton<IAppPaths, Platform.Windows.WindowsAppPaths>();
        services.AddSingleton<IDeviceKeyStore, Platform.Windows.WindowsDeviceKeyStore>();
        services.AddSingleton<IMachineFingerprintProvider, Platform.Windows.WindowsMachineFingerprintProvider>();
        services.AddSingleton<ISystemControl, Platform.Windows.WindowsSystemControl>();
        services.AddSingleton<IRemovableDriveWatcher, Platform.Windows.WindowsRemovableDriveWatcher>();
        services.AddSingleton<IKioskLockdown, Platform.Windows.WindowsKioskLockdown>();
#else
        services.AddSingleton<IAppPaths>(_ => new FakeAppPaths());
        services.AddSingleton<IDeviceKeyStore, FakeDeviceKeyStore>();
        services.AddSingleton<IMachineFingerprintProvider, FakeMachineFingerprintProvider>();
        services.AddSingleton<ISystemControl, FakeSystemControl>();
        services.AddSingleton<IRemovableDriveWatcher, FakeRemovableDriveWatcher>();
        services.AddSingleton<IKioskLockdown, FakeKioskLockdown>();
#endif
    }

    [SupportedOSPlatform("linux")]
    private static void RegisterLinux(IServiceCollection services)
    {
#if LINUX
        services.AddSingleton<IAppPaths, Platform.Linux.LinuxAppPaths>();
        services.AddSingleton<IDeviceKeyStore, Platform.Linux.LinuxDeviceKeyStore>();
        services.AddSingleton<IMachineFingerprintProvider, Platform.Linux.LinuxMachineFingerprintProvider>();
        services.AddSingleton<ISystemControl, Platform.Linux.LinuxSystemControl>();
        services.AddSingleton<IRemovableDriveWatcher, Platform.Linux.LinuxRemovableDriveWatcher>();
        services.AddSingleton<IKioskLockdown, Platform.Linux.LinuxKioskLockdown>();
#else
        services.AddSingleton<IAppPaths>(_ => new FakeAppPaths());
        services.AddSingleton<IDeviceKeyStore, FakeDeviceKeyStore>();
        services.AddSingleton<IMachineFingerprintProvider, FakeMachineFingerprintProvider>();
        services.AddSingleton<ISystemControl, FakeSystemControl>();
        services.AddSingleton<IRemovableDriveWatcher, FakeRemovableDriveWatcher>();
        services.AddSingleton<IKioskLockdown, FakeKioskLockdown>();
#endif
    }

    private static void TryLogFatal(IServiceProvider services, Exception exception)
    {
        try
        {
            services.GetRequiredService<ILogger<App>>()
                .LogCritical(exception, "The kiosk application terminated unexpectedly.");
        }
        catch
        {
            // Logging itself failed; the console is the last resort.
            Console.Error.WriteLine(exception);
        }
    }
}

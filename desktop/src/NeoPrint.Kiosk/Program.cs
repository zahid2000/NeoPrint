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

    /// <summary>Composition root, also built by the composition test.</summary>
    internal static IServiceProvider BuildServices(string[] args)
    {
        // Production unless told otherwise: a kiosk in the field must never
        // pick up developer settings because a variable was missing.
        var environment =
            Environment.GetEnvironmentVariable("NEOPRINT_KIOSK_ENVIRONMENT") ?? "Production";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            // appsettings.Development.json existed but nothing loaded it, so on
            // a developer machine the API address and the fake printer had to be
            // passed by hand every time.
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
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

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<DeviceEnrollmentService>();
        services.AddSingleton<KioskRuntime>();

        services.AddHttpClient(KioskApiClient.HttpClientName, (provider, client) =>
            {
                var settings = provider
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<KioskSettings>>().Value;

                client.BaseAddress = new Uri(settings.ApiBaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            // Every request carries a correlation id, so what the kiosk did and
            // what the backend logged can be read together
            // (docs/OBSERVABILITY.md).
            .AddHttpMessageHandler(() => new CorrelationIdHandler())
            // And the screen's language, so a refusal reads in it (T-043).
            .AddHttpMessageHandler(() => new AcceptLanguageHandler());

        // One client, not a typed one: it holds this installation's device id
        // and its kiosk token, and AddHttpClient<T> registers T as transient.
        // With a transient client the enrollment service and the runtime would
        // each hold their own, and the runtime's - which never registered -
        // could not authenticate, so an activated kiosk would never load its
        // configuration (plan section 7.11).
        services.AddSingleton(provider => new KioskApiClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(KioskApiClient.HttpClientName),
            provider.GetRequiredService<IDeviceKeyStore>(),
            provider.GetRequiredService<ILogger<KioskApiClient>>()));

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

        // Prints into a folder, so the whole flow works on a machine with no
        // printer and in CI (plan section 7.6).
        //
        // Registered even when it was not asked for, as long as nothing else
        // provides IPrinterService: the real printer service arrives with the
        // printing work in step 3, and until then the application could not be
        // composed at all on a machine with Kiosk:UseFakePrinter left at its
        // default - a kiosk that will not start cannot be activated either.
        var hasPrinterService = services
            .Any(registration => registration.ServiceType == typeof(IPrinterService));

        if (useFakePrinter || !hasPrinterService)
        {
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

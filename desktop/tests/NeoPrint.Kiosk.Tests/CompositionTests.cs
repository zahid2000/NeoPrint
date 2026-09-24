using Microsoft.Extensions.DependencyInjection;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// What the composition root has to get right for the kiosk to work at all
/// (plan section 7.11).
/// </summary>
/// <remarks>
/// Which services are shared is behaviour rather than a detail: the API client
/// holds this installation's device identity and its kiosk token, so the
/// enrollment service and the runtime have to be talking to the same one. When
/// they are not, enrollment succeeds and the kiosk then fails to load its
/// configuration - which no unit test of either class on its own would notice,
/// because each is handed a client directly.
/// </remarks>
public sealed class CompositionTests
{
    [Fact]
    public void The_api_client_is_one_shared_instance()
    {
        using var services = Build();

        var first = services.GetRequiredService<KioskApiClient>();
        var second = services.GetRequiredService<KioskApiClient>();

        first.Should().BeSameAs(
            second,
            "the client holds the device id and the token, so a second instance would not be registered");
    }

    [Fact]
    public void The_enrollment_service_and_the_runtime_are_both_resolvable_and_shared()
    {
        using var services = Build();

        services.GetRequiredService<DeviceEnrollmentService>()
            .Should().BeSameAs(services.GetRequiredService<DeviceEnrollmentService>());

        services.GetRequiredService<KioskRuntime>()
            .Should().BeSameAs(services.GetRequiredService<KioskRuntime>());
    }

    [Fact]
    public void Every_platform_service_the_application_asks_for_is_registered()
    {
        using var services = Build();

        // Resolved, not just registered: a missing constructor dependency only
        // shows up when the object is built.
        services.GetRequiredService<IAppPaths>().Should().NotBeNull();
        services.GetRequiredService<IDeviceKeyStore>().Should().NotBeNull();
        services.GetRequiredService<IMachineFingerprintProvider>().Should().NotBeNull();
        services.GetRequiredService<ISystemControl>().Should().NotBeNull();
        services.GetRequiredService<IRemovableDriveWatcher>().Should().NotBeNull();
        services.GetRequiredService<IKioskLockdown>().Should().NotBeNull();
        services.GetRequiredService<IClock>().Should().NotBeNull();
    }

    /// <summary>
    /// The real composition root, with the fake printer so no printer is
    /// needed, exactly as CI runs it (plan section 7.6).
    /// </summary>
    private static ServiceProvider Build()
        => (ServiceProvider)Program.BuildServices(["--api-url", "http://localhost:5080"]);
}

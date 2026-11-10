using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Localization;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.ViewModels;

using Xunit;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The welcome screen's words and picture from the panel (T-387): the panel's
/// text in the language on show, the kiosk's own where it set none, and the
/// picture kept on disk once per version.
/// </summary>
public sealed class BrandingTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "neoprint-branding-" + Guid.NewGuid().ToString("N"));

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00];

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }

    private static KioskConfiguration Configuration(KioskBranding? branding) => new(
        Guid.NewGuid(), "KSK-0042", "Metro", "Online", "az", "HP", new PrinterCapabilityFlags(true, true, true), [], null, 5, 50, 200, null,
        new Dictionary<string, PaperStockLevel>(), new Dictionary<string, bool>(), 15, 24, DateTime.UtcNow, Branding: branding);

    [Fact]
    public void The_panels_words_show_in_their_language_and_the_kiosks_own_where_none_was_set()
    {
        var model = new MainWindowViewModel { Language = "az" };
        var told = new List<string?>();
        model.PropertyChanged += (_, e) => told.Add(e.PropertyName);

        model.ApplyConfiguration(Configuration(new KioskBranding(
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["heroTitle"] = new Dictionary<string, string> { ["az"] = "Metroda çap", ["en"] = "Print at the metro" },
                ["hotline"] = new Dictionary<string, string> { ["az"] = "Kömək: *1234" },
            },
            null)));

        model.HeroTitle.Should().Be("Metroda çap");
        model.Hotline.Should().Be("Kömək: *1234");
        model.UsbCardTitle.Should().Be(Strings.Get("az", "home.usbTitle"), "nothing was set for it");
        told.Should().Contain([nameof(MainWindowViewModel.HeroTitle), nameof(MainWindowViewModel.Hotline)]);

        model.Language = "ru";
        model.HeroTitle.Should().Be(Strings.Get("ru", "home.heroTitle"), "nothing was set in Russian");
        model.Hotline.Should().BeNull();

        model.Language = "en";
        model.HeroTitle.Should().Be("Print at the metro");

        // The panel took its words away: the kiosk's own come back.
        model.ApplyConfiguration(Configuration(null));
        model.HeroTitle.Should().Be(Strings.Get("en", "home.heroTitle"));
    }

    [Fact]
    public async Task A_picture_is_fetched_once_per_version_kept_while_offline_and_dropped_when_taken_away()
    {
        var cache = new BrandingImageCache(_state, NullLogger.Instance);
        var fetched = 0;

        var path = await cache.GetAsync(7, _ => { fetched++; return Task.FromResult<byte[]?>(Png); }, CancellationToken.None);
        path.Should().NotBeNull();
        File.ReadAllBytes(path!).Should().Equal(Png);

        (await cache.GetAsync(7, _ => { fetched++; return Task.FromResult<byte[]?>(Png); }, CancellationToken.None)).Should().Be(path);
        fetched.Should().Be(1, "a version on disk is not fetched again");

        // A new version while the backend is out of reach: the last one stays on screen.
        (await cache.GetAsync(8, _ => throw new HttpRequestException("offline"), CancellationToken.None)).Should().Be(path);

        // What is not a picture is never kept.
        (await cache.GetAsync(9, _ => Task.FromResult<byte[]?>("<html>"u8.ToArray()), CancellationToken.None)).Should().BeNull();

        // No picture any more: the drawing, and nothing left on disk.
        (await cache.GetAsync(null, _ => Task.FromResult<byte[]?>(null), CancellationToken.None)).Should().BeNull();
        File.Exists(path).Should().BeFalse();
    }
}

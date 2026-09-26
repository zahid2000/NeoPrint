using System.Globalization;

using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;

using NeoPrint.Kiosk.Styles;

using QRCoder;

namespace NeoPrint.Kiosk.ViewModels;

/// <summary>
/// The value converters the shell uses (plan section 7.2).
/// </summary>
/// <remarks>
/// Kept as static fields so the XAML can bind to them with
/// <c>{x:Static}</c> and no resource dictionary is needed.
/// </remarks>
public static class ScreenConverters
{
    public static readonly IValueConverter IsStarting = Is(KioskScreen.Starting);

    public static readonly IValueConverter IsActivation = Is(KioskScreen.Activation);

    public static readonly IValueConverter IsHome = Is(KioskScreen.Home);

    public static readonly IValueConverter IsNotConfigured = Is(KioskScreen.NotConfigured);

    public static readonly IValueConverter IsDegraded = Is(KioskScreen.Degraded);

    public static readonly IValueConverter IsOffline = Is(KioskScreen.Offline);

    /// <summary>Green while a job is printing, grey while it waits.</summary>
    /// <remarks>
    /// The colours come from the design tokens rather than from literals here,
    /// so the dot matches the status chips on every other screen
    /// (docs/DESIGN_SYSTEM.md section 1).
    /// </remarks>
    public static readonly IValueConverter PrintingBrush = new FuncValueConverter<bool, IBrush>(
        isPrinting => isPrinting ? KioskTokens.Success : KioskTokens.Idle);

    /// <summary>
    /// Renders a QR payload to an image.
    /// </summary>
    /// <remarks>
    /// The kiosk generates its own QR codes locally so the footer works even
    /// with no connection to the backend (plan sections 2.0, 2.9).
    /// </remarks>
    public static readonly IValueConverter QrImage = new QrImageConverter();

    private static IValueConverter Is(KioskScreen expected)
        => new FuncValueConverter<KioskScreen, bool>(screen => screen == expected);

    private sealed class QrImageConverter : IValueConverter
    {
        /// <summary>Cached by payload: the footer QR never changes once set.</summary>
        private readonly Dictionary<string, Bitmap> _cache = new(StringComparer.Ordinal);

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string payload || string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            lock (_cache)
            {
                if (_cache.TryGetValue(payload, out var cached))
                {
                    return cached;
                }

                using var generator = new QRCodeGenerator();
                using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
                var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule: 8);

                using var stream = new MemoryStream(png);
                var bitmap = new Bitmap(stream);

                _cache[payload] = bitmap;
                return bitmap;
            }
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException("A QR image cannot be converted back to its payload.");
    }
}

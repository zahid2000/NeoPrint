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
    public static readonly IValueConverter IsFlow = Is(KioskScreen.Flow);

    public static readonly IValueConverter IsOutOfService = Is(KioskScreen.OutOfService);

    public static readonly IValueConverter IsSafeMode = Is(KioskScreen.SafeMode);

    public static readonly IValueConverter IsAdmin = Is(KioskScreen.Admin);

    public static readonly IValueConverter IsAdminPin = new FuncValueConverter<AdminStage, bool>(stage => stage == AdminStage.Pin);

    public static readonly IValueConverter IsAdminMenu = new FuncValueConverter<AdminStage, bool>(stage => stage == AdminStage.Menu);

    public static readonly IValueConverter IsAdminLocked = new FuncValueConverter<AdminStage, bool>(stage => stage == AdminStage.Locked);

    public static readonly IValueConverter IsAdminNoPin = new FuncValueConverter<AdminStage, bool>(stage => stage == AdminStage.NoPin);

    public static readonly IValueConverter IsBrowse = Stage(FlowStage.Browse);

    public static readonly IValueConverter IsWorking = Stage(FlowStage.Working);

    public static readonly IValueConverter IsPreview = Stage(FlowStage.Preview);

    public static readonly IValueConverter IsEdit = Stage(FlowStage.Edit);

    public static readonly IValueConverter IsOptions = Stage(FlowStage.Options);

    public static readonly IValueConverter IsPlaced = Stage(FlowStage.Placed);

    public static readonly IValueConverter IsCancelLookup = Stage(FlowStage.CancelLookup);

    public static readonly IValueConverter IsCancelConfirm = Stage(FlowStage.CancelConfirm);

    public static readonly IValueConverter IsCancelDone = Stage(FlowStage.CancelDone);

    public static readonly IValueConverter IsA4 = new FuncValueConverter<string, bool>(paper => paper == "A4");

    /// <summary>A keypad key whose glyph is smaller than a digit ("," and "-"); it is drawn larger (T-097).</summary>
    public static readonly IValueConverter IsSymbolKey = new FuncValueConverter<string, bool>(key => key is "," or "-");

    public static readonly IValueConverter IsA3 = new FuncValueConverter<string, bool>(paper => paper == "A3");

    public static readonly IValueConverter IsOne = new FuncValueConverter<int, bool>(value => value == 1);

    public static readonly IValueConverter IsTwo = new FuncValueConverter<int, bool>(value => value == 2);

    public static readonly IValueConverter IsFour = new FuncValueConverter<int, bool>(value => value == 4);

    /// <summary>A page turned as the editor turned it.</summary>
    public static readonly IValueConverter Rotation = new FuncValueConverter<int, Avalonia.Media.ITransform>(
        degrees => new Avalonia.Media.RotateTransform(degrees));

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

    private static IValueConverter Stage(FlowStage expected)
        => new FuncValueConverter<FlowStage, bool>(stage => stage == expected);

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

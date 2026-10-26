using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.Tests;
using NeoPrint.Kiosk.ViewModels;
using NeoPrint.Kiosk.Views;

[assembly: AvaloniaTestApplication(typeof(NeoPrint.Kiosk.ScreenTests.ScreenTestApp))]

namespace NeoPrint.Kiosk.ScreenTests;

/// <summary>
/// The kiosk application without a screen: the real <see cref="App"/> - its
/// tokens, styles and the Inter font - drawn by Skia into memory, so a frame
/// can be measured and saved as a picture.
/// </summary>
public static class ScreenTestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure(() => new App())
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>
/// Every kiosk screen, drawn at 1920×1080 and at the pilot's 1280×1024
/// (T-329) in Azerbaijani, English and Russian (docs/DESIGN_SYSTEM.md 5.1, 6):
/// no text is a raw translation key, none is wider or taller than the place it
/// was given, none lies outside the window; every button is at least a finger
/// (64 × 64 on the surface) and every text stands out from its background by
/// at least 4.5 : 1, for a 250-nit screen. The pictures are kept next to the
/// test results.
/// </summary>
/// <remarks>
/// What this catches is what review does not: a key missing from the
/// catalogue, a Russian label 40% longer than the Azerbaijani one, a button
/// whose text no longer fits after a wording change.
/// </remarks>
public sealed partial class ScreenRenderTests : IDisposable
{
    /// <summary>The design system's kiosk minimum for anything a finger presses (Tokens.axaml, TouchTarget).</summary>
    private const double TouchTarget = 64;

    /// <summary>WCAG AA for text: what a dim (250 nit) screen still shows clearly.</summary>
    private const double MinimumContrast = 4.5;

    private readonly string _drive = Directory.CreateTempSubdirectory("neoprint-screens-usb-").FullName;
    private readonly List<OrderTestKit> _kits = [];

    public void Dispose()
    {
        Directory.Delete(_drive, recursive: true);

        foreach (var kit in _kits)
        {
            kit.Dispose();
        }
    }

    /// <summary>Where the pictures go: beside the test results in CI, in the build output otherwise.</summary>
    private static string Pictures
    {
        get
        {
            var folder = Path.Combine(
                Environment.GetEnvironmentVariable("NEOPRINT_SCREENS_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screens"));

            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    [GeneratedRegex(@"^[a-z][A-Za-z0-9]*(\.[A-Za-z][A-Za-z0-9]*)+$")]
    private static partial Regex RawKey();

    [AvaloniaTheory]
    [InlineData("az", 1920, 1080)]
    [InlineData("en", 1920, 1080)]
    [InlineData("ru", 1920, 1080)]
    [InlineData("az", 1280, 1024)]
    [InlineData("en", 1280, 1024)]
    [InlineData("ru", 1280, 1024)]
    public async Task Every_screen_shows_words_that_fit(string language, int width, int height)
    {
        var problems = new List<string>();
        var drawn = 0;
        // The 1920 pictures keep their names; the pilot's screen is named by its size.
        var prefix = width == 1920 ? language : $"{width}x{height}-{language}";

        foreach (var (name, model) in await ScreensAsync(language))
        {
            var window = new MainWindow { DataContext = model, Width = width, Height = height };

            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var frame = window.CaptureRenderedFrame();
                frame.Should().NotBeNull($"{name} draws");
                frame!.Save(Path.Combine(Pictures, $"{prefix}-{name}.png"));
                drawn++;

                problems.AddRange(Inspect(window, width, height).Select(problem => $"{prefix} / {name}: {problem}"));
            }
            finally
            {
                window.AllowClose = true;
                window.Close();
            }
        }

        drawn.Should().BeGreaterThan(20, "every screen and every step of the order flow is drawn");
        // All of them at once: one run says everything there is to fix.
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // -----------------------------------------------------------------------
    // What is wrong with a drawn window, without anybody looking at it
    // -----------------------------------------------------------------------

    [AvaloniaFact]
    public void A_wide_screen_is_drawn_on_1920_by_1080_and_the_pilot_s_19_inch_on_1600_by_1280()
    {
        MainWindow.SurfaceFor(new Size(1920, 1080)).Should().Be(new Size(1920, 1080));
        MainWindow.SurfaceFor(new Size(1920, 1200)).Should().Be(new Size(1920, 1080), "16:10 is wide");
        MainWindow.SurfaceFor(new Size(1280, 1024)).Should().Be(new Size(1600, 1280), "5:4 fills the screen at 0.8");
        MainWindow.SurfaceFor(new Size(1024, 768)).Should().Be(new Size(1600, 1200), "4:3");
    }

    [Fact]
    public void Nothing_on_a_kiosk_screen_shows_only_under_a_hovering_mouse()
    {
        // A finger does not hover (T-329): no tooltip, and :pointerover changes a colour, never what is there.
        var views = Path.Combine(Repository(), "desktop", "src", "NeoPrint.Kiosk");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories)
                     .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            if (text.Contains("ToolTip.Tip", StringComparison.Ordinal))
            {
                offenders.Add($"{Path.GetFileName(file)}: a tooltip");
            }

            foreach (Match style in PointerOverStyle().Matches(text))
            {
                if (style.Value.Contains("Property=\"IsVisible\"", StringComparison.Ordinal)
                    || style.Value.Contains("Property=\"Opacity\"", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: something appears only on hover");
                }
            }
        }

        offenders.Should().BeEmpty();
    }

    [GeneratedRegex(@"<Style Selector=""[^""]*:pointerover[^""]*"">.*?</Style>", RegexOptions.Singleline)]
    private static partial Regex PointerOverStyle();

    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static IEnumerable<string> Inspect(Window window, int width, int height)
    {
        // A finger, not a mouse: on the surface, before it is scaled to the screen.
        // A scroll bar's own arrows are not what a finger presses: it moves the content itself.
        foreach (var control in window.GetVisualDescendants().OfType<Control>()
                     .Where(control => control is Button or ToggleButton or RadioButton or CheckBox or ComboBox
                                       && control.IsEffectivelyVisible && control.Bounds.Width > 0
                                       && control.FindAncestorOfType<ScrollBar>() is null))
        {
            if (control.Bounds.Width + 0.5 < TouchTarget || control.Bounds.Height + 0.5 < TouchTarget)
            {
                var label = control is ContentControl { Content: string content } ? content : control.Name ?? control.GetType().Name;
                yield return $"the button \"{label}\" is {control.Bounds.Width:0}×{control.Bounds.Height:0}: smaller than a finger ({TouchTarget:0}×{TouchTarget:0})";
            }
        }

        // On the pilot's screen nothing needs scrolling: a customer does not know to (T-097, T-329).
        if (width == 1280)
        {
            foreach (var scroller in window.GetVisualDescendants().OfType<ScrollViewer>()
                         .Where(scroller => scroller.IsEffectivelyVisible && scroller.Viewport.Height > 0
                                            && scroller.Extent.Height > scroller.Viewport.Height + 1))
            {
                yield return $"a part of the screen needs scrolling: {scroller.Extent.Height:0} px of content in {scroller.Viewport.Height:0}";
            }
        }

        foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
        {
            var words = text.Text;

            if (string.IsNullOrWhiteSpace(words) || !text.IsEffectivelyVisible || text.Bounds.Width <= 0)
            {
                continue;
            }

            var shown = words.Length <= 48 ? words : words[..48] + "…";

            if (RawKey().IsMatch(words.Trim()) && !IsFileName(words))
            {
                yield return $"\"{shown}\" is a translation key, not text";
            }

            // The same words laid out with all the room they want.
            var free = new TextBlock
            {
                Text = words,
                FontFamily = text.FontFamily,
                FontSize = text.FontSize,
                FontWeight = text.FontWeight,
                FontStyle = text.FontStyle,
                LetterSpacing = text.LetterSpacing,
                LineHeight = text.LineHeight,
                TextWrapping = text.TextWrapping,
                Padding = text.Padding,
            };

            var wraps = text.TextWrapping != TextWrapping.NoWrap;
            free.Measure(new Size(wraps ? text.Bounds.Width : double.PositiveInfinity, double.PositiveInfinity));

            if (!wraps && free.DesiredSize.Width > text.Bounds.Width + 1.5)
            {
                yield return text.TextTrimming == TextTrimming.None
                    ? $"\"{shown}\" needs {free.DesiredSize.Width:0} px and has {text.Bounds.Width:0}: it is cut off or runs over its neighbour"
                    : $"\"{shown}\" does not fit ({free.DesiredSize.Width:0} px into {text.Bounds.Width:0}) and is shortened with an ellipsis";
            }

            if (wraps && text.MaxLines == 0 && free.DesiredSize.Height > text.Bounds.Height + 1.5)
            {
                yield return $"\"{shown}\" needs {free.DesiredSize.Height:0} px of height and has {text.Bounds.Height:0}: its last line is cut off";
            }

            // Scrolled content is allowed to be outside; anything else is not. Both
            // corners on the window: the surface may be scaled to fit the screen.
            if (text.FindAncestorOfType<ScrollViewer>() is null
                && text.TranslatePoint(default, window) is { } origin
                && text.TranslatePoint(new Point(text.Bounds.Width, text.Bounds.Height), window) is { } end
                && (origin.X < -1 || origin.Y < -1 || end.X > width + 1 || end.Y > height + 1))
            {
                yield return $"\"{shown}\" lies outside the window (at {origin.X:0},{origin.Y:0} to {end.X:0},{end.Y:0})";
            }

            // Readable on a 250-nit screen; a control that is switched off is exempt (WCAG 1.4.3).
            if (text.IsEffectivelyEnabled
                && text.Foreground is ISolidColorBrush { Color.A: 255 } foreground
                && Background(text) is { } background
                && Contrast(foreground.Color, background) is var contrast and < MinimumContrast)
            {
                yield return $"\"{shown}\" stands out from its background by {contrast:0.00} : 1 ({foreground.Color} on {background}); a 250-nit screen needs {MinimumContrast} : 1";
            }
        }
    }

    /// <summary>The colour a text is drawn on: the nearest opaque background under it.</summary>
    private static Color? Background(Visual text)
    {
        foreach (var ancestor in text.GetVisualAncestors())
        {
            var brush = ancestor switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                Avalonia.Controls.Primitives.TemplatedControl control => control.Background,
                ContentPresenter presenter => presenter.Background,
                _ => null,
            };

            if (brush is ISolidColorBrush { Color.A: > 0 } solid)
            {
                return solid.Color;
            }
        }

        return null;
    }

    /// <summary>WCAG 2 contrast ratio.</summary>
    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte value)
            {
                var c = value / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
        }

        var (lighter, darker) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>"report.pdf" has the shape of a key and is a file's name.</summary>
    private static bool IsFileName(string words)
        => Regex.IsMatch(words.Trim(), @"\.(pdf|docx?|xlsx?|pptx?|txt|png|jpe?g|odt|ods|odp|rtf|csv)$", RegexOptions.IgnoreCase);

    // -----------------------------------------------------------------------
    // The screens
    // -----------------------------------------------------------------------

    private static MainWindowViewModel Shell(string language, KioskScreen screen, Action<MainWindowViewModel>? more = null)
    {
        var shell = new MainWindowViewModel
        {
            Language = language,
            KioskName = "Elmlər Akademiyası metrosu, giriş zalı",
            KioskCode = "KSK-9002",
            SelectedPrinterName = "HP Color LaserJet Pro M454dn",
            CanPrintFromFlash = true,
            Screen = screen,
        };

        shell.SetQueue(
        [
            new QueueEntry(1, "482913", "Z", 12, IsPrinting: true),
            new QueueEntry(2, "775201", "A", 3, IsPrinting: false),
            new QueueEntry(3, "118340", "M", 148, IsPrinting: false),
        ]);

        more?.Invoke(shell);

        return shell;
    }

    private async Task<PrintFlowViewModel> FlowAsync(string language, bool skipPayment)
    {
        var kit = await OrderTestKit.CreateAsync(skipPayment: skipPayment);
        _kits.Add(kit);

        var folder = Directory.CreateTempSubdirectory("usb-").FullName;
        Directory.Move(folder, folder = Path.Combine(_drive, Path.GetFileName(folder)));

        Directory.CreateDirectory(Path.Combine(folder, "Dərslər və tapşırıqlar"));
        kit.Pdf(3, folder, "Kurs işi — yekun variant (düzəlişlərlə) 2026.pdf");
        await File.WriteAllBytesAsync(Path.Combine(folder, "setup.exe"), "MZ"u8.ToArray());

        var drives = new FakeRemovableDriveWatcher();
        var drive = new RemovableDrive(folder, "KINGSTON", 1, 1);
        drives.Drives.Add(drive);

        var flow = new PrintFlowViewModel(kit.Orders, drives, pageImage: _ => null)
        {
            Language = language,
            Delay = (_, token) => Task.Delay(5, token),
        };

        await flow.StartFromDriveAsync(drive);

        return flow;
    }

    private async Task<List<(string Name, MainWindowViewModel Model)>> ScreensAsync(string language)
    {
        var screens = new List<(string, MainWindowViewModel)>
        {
            ("01-starting", Shell(language, KioskScreen.Starting)),
            ("02-activation", Shell(language, KioskScreen.Activation, shell =>
            {
                shell.KioskCode = null;
                shell.Activation.PairingCode = "483920";
                shell.Activation.PairingCodeExpiresAt = DateTime.UtcNow.AddMinutes(9);
                shell.Activation.TimeUntilRefresh = TimeSpan.FromSeconds(542);
                shell.Activation.ActivationQr = "neoprint://activate/483920";
                shell.Activation.IsBackendReachable = true;
                shell.Activation.Lifecycle = KioskLifecycle.Pending;
                shell.Activation.Fingerprint = new MachineFingerprint(
                    "kiosk-elmler-akademiyasi-01", "Linux", "Ubuntu 24.04.1 LTS", "machine-1", [], "Intel Celeron N5105", 8, "1920x1080");
                shell.Activation.Printers =
                [
                    new DetectedPrinter("HP Color LaserJet Pro M454dn", "HP Universal Printing PCL 6", true, true, true, false),
                    new DetectedPrinter("Canon imageRUNNER ADVANCE C3530i III", "Canon Generic Plus UFR II", false, true, true, true),
                ];
            })),
            ("03-activation-offline", Shell(language, KioskScreen.Activation, shell =>
            {
                shell.KioskCode = null;
                shell.Activation.IsBackendReachable = false;
            })),
            ("04-home", Shell(language, KioskScreen.Home)),
            ("05-home-no-flash-empty-queue", Shell(language, KioskScreen.Home, shell =>
            {
                shell.CanPrintFromFlash = false;
                shell.Queue.Clear();
            })),
            ("06-not-configured", Shell(language, KioskScreen.NotConfigured)),
            ("07-degraded", Shell(language, KioskScreen.Degraded)),
            ("08-offline", Shell(language, KioskScreen.Offline)),
            ("09-safe-mode", Shell(language, KioskScreen.SafeMode)),
            ("10-admin-pin", Shell(language, KioskScreen.Admin, shell => shell.Admin = new AdminMenuViewModel(() => null) { Language = language })),
            ("11-admin-menu", Shell(language, KioskScreen.Admin, shell =>
                shell.Admin = new AdminMenuViewModel(() => null) { Language = language, Stage = AdminStage.Menu, IsSafeMode = true })),
        };

        foreach (var problem in new[] { "OutOfPaper", "PaperJam", "Offline", "NotFound" })
        {
            screens.Add(($"12-out-of-service-{problem}", Shell(language, KioskScreen.Home, shell => shell.SetPrinterProblem(ProblemKey(problem)))));
        }

        // An administrator paused the kiosk (plan section 7.8).
        screens.Add(("14-maintenance", Shell(language, KioskScreen.Home, shell => shell.SetMaintenance(true))));

        // An update waits for a moment with no customer, then is applied (T-321).
        screens.Add(("15-update-draining", Shell(language, KioskScreen.Home, shell => shell.SetDraining(true))));
        screens.Add(("16-update-applying", Shell(language, KioskScreen.Home, shell => shell.SetUpdating(true))));

        foreach (var destination in new[] { "Card", "Wallet", "Voucher" })
        {
            screens.Add(($"13-refund-{destination}", Shell(language, KioskScreen.Home, shell =>
                shell.ShowRefund(12.40m, destination, "https://neoprint.az/r/7KQ2MX9T"))));

            // The same notice under "the kiosk has stopped" (plan section 9.6): a narrower column,
            // where the card's text ran past its edge on the real kiosk (T-238).
            screens.Add(($"13-refund-stopped-{destination}", Shell(language, KioskScreen.Home, shell =>
            {
                shell.SetPrinterProblem("printer.paperJam");
                shell.ShowRefund(12.40m, destination, "https://neoprint.az/r/7KQ2MX9T");
            })));
        }

        // The order, walked as a customer walks it.
        var browse = await FlowAsync(language, skipPayment: true);
        screens.Add(("20-flow-browse", Shell(language, KioskScreen.Flow, shell => shell.Flow = browse)));

        var preview = await FlowAsync(language, skipPayment: true);
        await preview.OpenCommand.ExecuteAsync(preview.Entries.First(entry => entry.IsSelectable && !entry.IsFolder));
        screens.Add(("21-flow-preview", Shell(language, KioskScreen.Flow, shell => shell.Flow = preview)));

        var options = await FlowAsync(language, skipPayment: true);
        await options.OpenCommand.ExecuteAsync(options.Entries.First(entry => entry.IsSelectable && !entry.IsFolder));
        options.ContinueCommand.Execute(null);
        options.MoreCopiesCommand.Execute(null);
        screens.Add(("22-flow-options", Shell(language, KioskScreen.Flow, shell => shell.Flow = options)));

        var placed = await FlowAsync(language, skipPayment: true);
        await placed.OpenCommand.ExecuteAsync(placed.Entries.First(entry => entry.IsSelectable && !entry.IsFolder));
        placed.ContinueCommand.Execute(null);
        await placed.PlaceCommand.ExecuteAsync(null);
        screens.Add(("23-flow-placed", Shell(language, KioskScreen.Flow, shell => shell.Flow = placed)));

        var paying = await FlowAsync(language, skipPayment: false);
        await paying.OpenCommand.ExecuteAsync(paying.Entries.First(entry => entry.IsSelectable && !entry.IsFolder));
        paying.ContinueCommand.Execute(null);
        await paying.PlaceCommand.ExecuteAsync(null);
        screens.Add(("24-flow-payment-method", Shell(language, KioskScreen.Flow, shell => shell.Flow = paying)));

        var qr = await FlowAsync(language, skipPayment: false);
        await qr.OpenCommand.ExecuteAsync(qr.Entries.First(entry => entry.IsSelectable && !entry.IsFolder));
        qr.ContinueCommand.Execute(null);
        await qr.PlaceCommand.ExecuteAsync(null);
        await qr.PayWithBankCommand.ExecuteAsync(null);
        screens.Add(("25-flow-qr-payment", Shell(language, KioskScreen.Flow, shell => shell.Flow = qr)));

        var cancel = await FlowAsync(language, skipPayment: true);
        cancel.StartCancel();
        screens.Add(("26-flow-cancel-lookup", Shell(language, KioskScreen.Flow, shell => shell.Flow = cancel)));

        // The remaining steps as the view draws them when the flow is put there.
        foreach (var stage in new[]
                 {
                     FlowStage.Working, FlowStage.Edit, FlowStage.CancelConfirm, FlowStage.CancelDone,
                     FlowStage.PaymentExpired, FlowStage.UserFiles, FlowStage.Collected, FlowStage.Layout,
                 })
        {
            var flow = await FlowAsync(language, skipPayment: true);
            flow.Stage = stage;
            screens.Add(($"30-flow-{stage}", Shell(language, KioskScreen.Flow, shell => shell.Flow = flow)));
        }

        return screens;
    }

    /// <summary>The message key the session gives the shell for a printer state.</summary>
    private static string ProblemKey(string state) => PrinterErrors.For(Enum.Parse<PrinterState>(state));
}

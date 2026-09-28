using System.Collections.ObjectModel;
using System.Globalization;

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NeoPrint.Documents;
using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Localization;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.Printing;

namespace NeoPrint.Kiosk.ViewModels;

/// <summary>Where the customer is in the flash order (plan section 7.3, screens 2–5, 9, 11).</summary>
public enum FlowStage
{
    Browse = 0,
    Working = 1,
    Preview = 2,
    Edit = 3,
    Options = 4,
    Placed = 5,
    CancelLookup = 6,
    CancelConfirm = 7,
    CancelDone = 8,
}

/// <summary>A folder or a file on the flash drive.</summary>
/// <param name="Reason">Why a file cannot be chosen; null when it can (plan 7.3: "boz + səbəb").</param>
public sealed record DriveEntry(string Name, string Path, bool IsFolder, string Detail, string? Reason)
{
    public bool IsSelectable => IsFolder || Reason is null;
}

/// <summary>How far the customer is past a step of the order (docs/DESIGN_SYSTEM.md, step indicator).</summary>
public enum FlowStepState
{
    Done = 0,
    Current = 1,
    Upcoming = 2,
}

/// <summary>One step of the indicator above a flash order: a number, a name and where the customer is.</summary>
public sealed record FlowStep(int Number, string Label, FlowStepState State)
{
    public bool IsDone => State == FlowStepState.Done;

    public bool IsCurrent => State == FlowStepState.Current;
}

/// <summary>One page of the document, as the preview and the editor show it.</summary>
public sealed partial class PageTile : ObservableObject
{
    public PageTile(int page, Bitmap? image, string label)
    {
        Page = page;
        Image = image;
        Label = label;
    }

    /// <summary>1-based page in the source PDF.</summary>
    public int Page { get; }

    public Bitmap? Image { get; }

    public string Label { get; }

    public bool HasImage => Image is not null;

    [ObservableProperty]
    private int _rotate;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// The flash order and the cancel-by-code screen (plan sections 7.3, 7.5, 9.1).
/// </summary>
/// <remarks>
/// Everything that takes time - reading the drive, converting, building the
/// print file, talking to the backend - runs off the screen's thread, and the
/// screen says so. Each failure says what happened, what it means for the
/// order and what to do next (docs/DESIGN_SYSTEM.md, errors).
/// </remarks>
public sealed partial class PrintFlowViewModel : ViewModelBase
{
    private const int ThumbnailWidth = 180;
    private const int LargeWidth = 900;

    private readonly KioskOrderService _orders;
    private readonly IRemovableDriveWatcher _drives;

    private string? _driveRoot;

    // The step of the indicator the customer is on; -1 on the cancel screens,
    // which are not part of an order.
    private int _step;
    private string? _workFolder;
    private string? _sourcePath;
    private PreparedDocument? _document;
    private PrintJobInfo? _foundJob;

    // The Idempotency-Key of the order being placed: kept across a retry of the
    // same options, renewed when they change (a lost response must not make a
    // second job).
    private string? _orderKey;

    public PrintFlowViewModel(KioskOrderService orders, IRemovableDriveWatcher drives)
    {
        _orders = orders;
        _drives = drives;
    }

    /// <summary>The customer is done or gave up: back to the home screen.</summary>
    public event Action? Finished;

    [ObservableProperty]
    private FlowStage _stage = FlowStage.Browse;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string? _currentFolder;

    [ObservableProperty]
    private string? _fileName;

    [ObservableProperty]
    private Bitmap? _largePage;

    [ObservableProperty]
    private int _largePageNumber = 1;

    // ---- options (plan section 2.3)

    [ObservableProperty]
    private int _copies = 1;

    [ObservableProperty]
    private bool _allPages = true;

    [ObservableProperty]
    private string _rangeText = string.Empty;

    [ObservableProperty]
    private bool _color;

    [ObservableProperty]
    private bool _duplex;

    [ObservableProperty]
    private string _paper = "A4";

    [ObservableProperty]
    private int _pagesPerSheet = 1;

    [ObservableProperty]
    private string? _priceText;

    [ObservableProperty]
    private string? _quoteDetail;

    [ObservableProperty]
    private bool _canPlace;

    // ---- placed / cancel

    [ObservableProperty]
    private string? _placedCode;

    [ObservableProperty]
    private string? _placedPosition;

    [ObservableProperty]
    private string _codeInput = string.Empty;

    [ObservableProperty]
    private string? _foundSummary;

    // ---- editing a text file

    [ObservableProperty]
    private string _textContent = string.Empty;

    public ObservableCollection<DriveEntry> Entries { get; } = [];

    public ObservableCollection<PageTile> Pages { get; } = [];

    public bool IsFromDrive => _driveRoot is not null && Stage is FlowStage.Preview or FlowStage.Edit or FlowStage.Options or FlowStage.Browse;

    public bool IsImage => _document?.Format.Group == FormatGroup.Image;

    public bool IsText => _document?.Format.Extension == "txt";

    public bool IsPaged => !IsImage && !IsText;

    public int PageCount => _document?.PageCount ?? 0;

    public bool CanColor => _orders.Configuration?.EffectiveCapabilities.Color == true;

    public bool CanDuplex => _orders.Configuration?.EffectiveCapabilities.Duplex == true;

    public bool CanA3 => _orders.Configuration?.EffectiveCapabilities.A3 == true;

    public bool CanGoUp => CurrentFolder is not null && _driveRoot is not null
                           && !string.Equals(Path.GetFullPath(CurrentFolder), Path.GetFullPath(_driveRoot), StringComparison.OrdinalIgnoreCase);

    public bool CanFind => CodeInput.Length == 6;

    // ---- texts

    public string BrowseTitle => T("browse.title");

    public string InsertTitle => T("browse.insert");

    public string InsertBody => T("browse.insertBody");

    public string EmptyText => T("browse.empty");

    public string UpText => T("browse.up");

    public string UsbText => T("common.usb");

    public string UsbHint => T("common.usbHint");

    public string WorkingText => T("common.working");

    public string BackText => T("common.back");

    public string CancelText => T("common.cancel");

    public string SaveText => T("common.save");

    public string DoneText => T("common.done");

    public string PreviewTitle => T("preview.title");

    public string EditText => T("preview.edit");

    public string ContinueText => T("preview.continue");

    public string EditTitle => T("edit.title");

    public string RotateText => T("edit.rotate");

    public string DeleteText => T("edit.delete");

    public string MoveBackText => T("edit.moveBack");

    public string MoveForwardText => T("edit.moveForward");

    public string BrighterText => T("edit.brighter");

    public string DarkerText => T("edit.darker");

    public string OptionsTitle => T("options.title");

    public string CopiesLabel => T("options.copies");

    public string PagesLabel => T("options.pages");

    public string AllPagesText => T("options.allPages", PageCount);

    public string RangeLabel => T("options.range");

    public string RangeHint => T("options.rangeHint");

    public string ColourLabel => T("options.colour");

    public string BwText => T("options.bw");

    public string ColorText => T("options.color");

    public string SidesLabel => T("options.sides");

    public string SimplexText => T("options.simplex");

    public string DuplexText => T("options.duplex");

    public string PaperLabel => T("options.paper");

    public string PerSheetLabel => T("options.perSheet");

    public string PriceLabel => T("options.price");

    public string PlaceText => T("options.place");

    public string UnavailableText => T("options.unavailable");

    public string PlacedTitle => T("placed.title");

    public string PlacedCodeLabel => T("placed.code");

    public string KeepCodeText => T("placed.keepCode");

    public string CancelTitle => T("cancel.title");

    public string EnterCodeText => T("cancel.enterCode");

    public string FindText => T("cancel.find");

    public string ConfirmCancelText => T("cancel.confirm");

    public string ConfirmCancelBody => T("cancel.confirmBody");

    public string CancelDoneText => T("cancel.done");

    // -----------------------------------------------------------------------
    // Starting and leaving
    // -----------------------------------------------------------------------

    /// <summary>A drive was inserted (or chosen): show its top folder.</summary>
    public async Task StartFromDriveAsync(RemovableDrive drive)
    {
        ArgumentNullException.ThrowIfNull(drive);

        Reset();
        _driveRoot = drive.Path;
        await ShowFolderAsync(drive.Path).ConfigureAwait(true);
    }

    /// <summary>The cancel-by-code screen (plan section 7.3, screen 11).</summary>
    public void StartCancel()
    {
        Reset();
        Stage = FlowStage.CancelLookup;
    }

    /// <summary>The drive was pulled out: whatever was not ordered is dropped (plan 7.3).</summary>
    public void DriveRemoved()
    {
        if (_driveRoot is null || Stage is FlowStage.Placed or FlowStage.CancelLookup or FlowStage.CancelConfirm or FlowStage.CancelDone)
        {
            return;
        }

        Reset();
        Error = T("browse.removed");
        Finished?.Invoke();
    }

    /// <summary>Drops everything, deletes the working copies.</summary>
    public void Reset()
    {
        _orders.Discard(_workFolder);
        _workFolder = null;
        _document = null;
        _sourcePath = null;
        _driveRoot = null;
        _foundJob = null;
        Entries.Clear();
        Pages.Clear();
        LargePage = null;
        Error = null;
        FileName = null;
        CodeInput = string.Empty;
        FoundSummary = null;
        TextContent = string.Empty;
        ResetOptions();
        Stage = FlowStage.Browse;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void Leave()
    {
        Reset();
        Finished?.Invoke();
    }

    // -----------------------------------------------------------------------
    // Browsing (screen 2)
    // -----------------------------------------------------------------------

    [RelayCommand]
    private async Task OpenAsync(DriveEntry? entry)
    {
        if (entry is null || !entry.IsSelectable)
        {
            return;
        }

        if (entry.IsFolder)
        {
            await ShowFolderAsync(entry.Path).ConfigureAwait(true);
            return;
        }

        await PrepareAsync(entry.Path, entry.Name).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UpAsync()
    {
        if (CanGoUp && Directory.GetParent(CurrentFolder!) is { } parent)
        {
            await ShowFolderAsync(parent.FullName).ConfigureAwait(true);
        }
    }

    private async Task ShowFolderAsync(string folder)
    {
        var maxBytes = (long)(_orders.Configuration?.MaxFileSizeMb ?? 50) * 1024 * 1024;
        var allowed = _orders.Configuration?.AllowedFormats;

        var entries = await Task.Run(() => List(folder, maxBytes, allowed)).ConfigureAwait(true);

        Entries.Clear();

        foreach (var entry in entries)
        {
            Entries.Add(entry);
        }

        CurrentFolder = folder;
        Error = null;
        Stage = FlowStage.Browse;
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(IsFromDrive));
    }

    private List<DriveEntry> List(string folder, long maxBytes, IReadOnlyList<string>? allowed)
    {
        var result = new List<DriveEntry>();

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(folder).Order(StringComparer.CurrentCultureIgnoreCase).Take(200))
            {
                var name = System.IO.Path.GetFileName(directory);

                // Hidden and system folders are not what a customer put there.
                if (name.StartsWith('.') || name.StartsWith('$') || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(new DriveEntry(name, directory, true, T("browse.folder"), null));
            }

            foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.CurrentCultureIgnoreCase).Take(500))
            {
                var info = new FileInfo(file);

                if (info.Name.StartsWith('.') || (info.Attributes & FileAttributes.Hidden) != 0)
                {
                    continue;
                }

                var extension = info.Extension.TrimStart('.').ToLowerInvariant();
                var format = FileFormats.Find(extension);

                // Only the formats the kiosk takes are listed; one it takes but
                // this installation has switched off shows greyed with the reason.
                if (format is null)
                {
                    continue;
                }

                string? reason = null;

                if (allowed is { Count: > 0 } && !allowed.Contains(format.Extension, StringComparer.OrdinalIgnoreCase))
                {
                    reason = T("browse.notAllowed");
                }
                else if (info.Length > maxBytes)
                {
                    reason = T("browse.tooLarge", maxBytes / (1024 * 1024));
                }

                result.Add(new DriveEntry(
                    info.Name,
                    info.FullName,
                    false,
                    $"{format.Extension.ToUpperInvariant()} · {Size(info.Length)}",
                    reason));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The drive went away while it was being read; DriveRemoved follows.
        }

        return result;
    }

    // -----------------------------------------------------------------------
    // Preparing and previewing (screen 3)
    // -----------------------------------------------------------------------

    private async Task PrepareAsync(string path, string name)
    {
        Stage = FlowStage.Working;
        Error = null;
        _orders.Discard(_workFolder);
        _workFolder = _orders.NewWorkFolder();
        _sourcePath = path;
        FileName = name;

        try
        {
            _document = await Task.Run(() => _orders.OpenAsync(path, _workFolder, CancellationToken.None)).ConfigureAwait(true);

            if (IsText)
            {
                TextContent = await DocumentPreparer.ReadTextAsync(Directory.GetFiles(_workFolder, "source.*")[0], CancellationToken.None)
                    .ConfigureAwait(true);
            }

            await LoadPagesAsync().ConfigureAwait(true);
            ResetOptions();
            Stage = FlowStage.Preview;
        }
        catch (PrintingException exception)
        {
            Error = T(exception.MessageKey);
            Stage = FlowStage.Browse;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Error = T("browse.removed");
            Stage = FlowStage.Browse;
        }

        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(IsPaged));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(AllPagesText));
    }

    private async Task LoadPagesAsync()
    {
        Pages.Clear();

        if (_document is null)
        {
            return;
        }

        var pdf = await File.ReadAllBytesAsync(_document.PdfPath).ConfigureAwait(true);
        var count = _document.PageCount;
        var tiles = await Task.Run(() => Enumerable.Range(1, count)
                .Select(page => (page, image: Render(pdf, page - 1, ThumbnailWidth)))
                .ToList())
            .ConfigureAwait(true);

        foreach (var (page, image) in tiles)
        {
            Pages.Add(new PageTile(page, image, T("preview.page", page)));
        }

        ShowLarge(1, pdf);
    }

    [RelayCommand]
    private async Task ShowPageAsync(PageTile? tile)
    {
        if (tile is null || _document is null)
        {
            return;
        }

        ShowLarge(tile.Page, await File.ReadAllBytesAsync(_document.PdfPath).ConfigureAwait(true));
    }

    private void ShowLarge(int page, byte[] pdf)
    {
        LargePageNumber = page;
        LargePage = Render(pdf, page - 1, LargeWidth);

        foreach (var tile in Pages)
        {
            tile.IsSelected = tile.Page == page;
        }
    }

    /// <summary>A page as a bitmap; null when PDFium is not installed, and the tile shows its number.</summary>
    private static Bitmap? Render(byte[] pdf, int index, int width)
    {
        if (!PageRenderer.IsAvailable)
        {
            return null;
        }

        try
        {
            var page = PageRenderer.Render(pdf, index, width);
            var bitmap = new WriteableBitmap(new PixelSize(page.Width, page.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

            using (var buffer = bitmap.Lock())
            {
                for (var row = 0; row < page.Height; row++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        page.Pixels, row * page.Stride, buffer.Address + (row * buffer.RowBytes), Math.Min(page.Stride, buffer.RowBytes));
                }
            }

            return bitmap;
        }
        catch (Exception exception) when (exception is NeoPrint.Pdfium.PdfiumException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    [RelayCommand]
    private async Task BackToBrowseAsync()
    {
        _orders.Discard(_workFolder);
        _workFolder = null;
        _document = null;

        if (CurrentFolder is not null)
        {
            await ShowFolderAsync(CurrentFolder).ConfigureAwait(true);
        }
        else
        {
            Leave();
        }
    }

    // -----------------------------------------------------------------------
    // Editing (screen 4)
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void ShowPreview()
    {
        Error = null;
        Stage = FlowStage.Preview;
    }

    [RelayCommand]
    private void StartCancelAgain()
    {
        _foundJob = null;
        FoundSummary = null;
        Stage = FlowStage.CancelLookup;
    }

    [RelayCommand]
    private void Edit()
    {
        Error = null;
        Stage = FlowStage.Edit;
    }

    [RelayCommand]
    private void RotatePage(PageTile? tile)
    {
        if (tile is not null)
        {
            tile.Rotate = (tile.Rotate + 90) % 360;
        }
    }

    [RelayCommand]
    private void DeletePage(PageTile? tile)
    {
        if (tile is null)
        {
            return;
        }

        if (Pages.Count <= 1)
        {
            Error = T("edit.keepOnePage");
            return;
        }

        Pages.Remove(tile);
    }

    [RelayCommand]
    private void MovePageBack(PageTile? tile) => Move(tile, -1);

    [RelayCommand]
    private void MovePageForward(PageTile? tile) => Move(tile, 1);

    private void Move(PageTile? tile, int by)
    {
        if (tile is null)
        {
            return;
        }

        var index = Pages.IndexOf(tile);
        var target = index + by;

        if (index >= 0 && target >= 0 && target < Pages.Count)
        {
            Pages.Move(index, target);
        }
    }

    [RelayCommand]
    private void RotateImage()
    {
        if (Pages.FirstOrDefault() is { } tile)
        {
            tile.Rotate = (tile.Rotate + 90) % 360;
        }
    }

    [ObservableProperty]
    private int _brightness;

    [RelayCommand]
    private void Brighter() => Brightness = Math.Min(100, Brightness + 20);

    [RelayCommand]
    private void Darker() => Brightness = Math.Max(-100, Brightness - 20);

    /// <summary>Applies the edit to a new PDF (or image, or text) and shows it again.</summary>
    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (_document is null || _workFolder is null)
        {
            return;
        }

        var document = _document;
        var folder = _workFolder;
        var changes = Pages.Select(tile => new PageChange(tile.Page, tile.Rotate)).ToList();
        var imageRotate = Pages.FirstOrDefault()?.Rotate ?? 0;
        var brightness = Brightness;
        var text = TextContent;

        Stage = FlowStage.Working;

        try
        {
            var edited = Path.Combine(folder, $"edited-{Guid.NewGuid():N}");

            await Task.Run(() =>
                {
                    if (IsText)
                    {
                        SkiaDocuments.TextToPdf(text, edited + ".pdf");
                    }
                    else if (IsImage)
                    {
                        var source = Directory.GetFiles(folder, "source.*")[0];
                        SkiaDocuments.EditImage(source, edited + ".png", new ImageChange(imageRotate, null, brightness, 0));
                        SkiaDocuments.ImageToPdf(edited + ".png", edited + ".pdf");
                    }
                    else
                    {
                        PdfTools.ApplyChanges(document.PdfPath, edited + ".pdf", changes);
                    }
                })
                .ConfigureAwait(true);

            _document = document with { PdfPath = edited + ".pdf", PageCount = DocumentPreparer.PageCount(edited + ".pdf") };
            Brightness = 0;
            await LoadPagesAsync().ConfigureAwait(true);
            ResetOptions();
            Stage = FlowStage.Preview;
        }
        catch (PrintingException exception)
        {
            Error = T(exception.MessageKey);
            Stage = FlowStage.Edit;
        }

        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(AllPagesText));
    }

    [RelayCommand]
    private async Task CancelEditAsync()
    {
        // Back to the pages as they were: the tiles carry unsaved turns and removals.
        Brightness = 0;
        await LoadPagesAsync().ConfigureAwait(true);
        Stage = FlowStage.Preview;
    }

    // -----------------------------------------------------------------------
    // Options and price (screen 5)
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void Continue()
    {
        Error = null;
        Stage = FlowStage.Options;
        Requote();
    }

    [RelayCommand]
    private void MoreCopies() => Copies = Math.Min(50, Copies + 1);

    [RelayCommand]
    private void FewerCopies() => Copies = Math.Max(1, Copies - 1);

    [RelayCommand]
    private void ChooseAllPages() => AllPages = true;

    [RelayCommand]
    private void ChooseRange() => AllPages = false;

    [RelayCommand]
    private void ChooseColor(string? value) => Color = value == "true" && CanColor;

    [RelayCommand]
    private void ChooseDuplex(string? value) => Duplex = value == "true" && CanDuplex;

    [RelayCommand]
    private void ChoosePaper(string? value) => Paper = value == "A3" && CanA3 ? "A3" : "A4";

    [RelayCommand]
    private void ChoosePerSheet(string? value)
        => PagesPerSheet = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is 1 or 2 or 4 ? n : 1;

    partial void OnCopiesChanged(int value) => Requote();

    partial void OnAllPagesChanged(bool value) => Requote();

    partial void OnRangeTextChanged(string value) => Requote();

    partial void OnColorChanged(bool value) => Requote();

    partial void OnDuplexChanged(bool value) => Requote();

    partial void OnPaperChanged(string value) => Requote();

    partial void OnPagesPerSheetChanged(int value) => Requote();

    private JobOptions Options() => new(
        Copies,
        AllPages ? null : RangeText.Trim(),
        Color,
        Duplex,
        Paper,
        "Auto",
        PagesPerSheet,
        "Fit");

    private void Requote()
    {
        _orderKey = null;

        if (_document is null || Stage != FlowStage.Options)
        {
            return;
        }

        var quote = _orders.Quote(Options(), _document.PageCount);

        if (quote is null)
        {
            PriceText = null;
            QuoteDetail = null;
            CanPlace = false;
            Error = AllPages ? null : T("options.rangeInvalid", _document.PageCount);
            return;
        }

        PriceText = Formats.Money(quote.Price.FinalPrice, Language);

        var detail = T("options.summary", quote.SelectedPages, quote.Sheets);

        if (quote.Price.DiscountPercent > 0)
        {
            detail += " · " + T("options.discount", quote.Price.DiscountPercent.ToString("0.##", CultureInfo.InvariantCulture));
        }

        if (quote.Price.MinimumApplied)
        {
            detail += " · " + T("options.minimum");
        }

        QuoteDetail = detail;
        Error = quote.PaperCovers ? null : T("options.notEnoughPaper");
        CanPlace = quote.PaperCovers;
    }

    private void ResetOptions()
    {
        Copies = 1;
        AllPages = true;
        RangeText = string.Empty;
        Color = false;
        Duplex = false;
        Paper = "A4";
        PagesPerSheet = 1;
        PriceText = null;
        QuoteDetail = null;
        CanPlace = false;
    }

    [RelayCommand]
    private async Task PlaceAsync()
    {
        if (_document is null || !CanPlace)
        {
            return;
        }

        var document = _document;
        var options = Options();
        var name = FileName ?? "document";

        Stage = FlowStage.Working;
        Error = null;

        try
        {
            var key = _orderKey ??= Guid.NewGuid().ToString("N");
            var job = await Task.Run(() => _orders.PlaceAsync(document, name, options, key, CancellationToken.None)).ConfigureAwait(true);
            _orderKey = null;

            PlacedCode = job.JobCode;
            PlacedPosition = job.QueuePosition is { } position ? T("placed.position", position) : null;

            // The file is in the encrypted spool now; the working copy goes,
            // and the drive can come out.
            _orders.Discard(_workFolder);
            _workFolder = null;
            _document = null;
            Stage = FlowStage.Placed;
        }
        catch (KioskApiException exception)
        {
            // A definite answer: the next attempt is a new order.
            _orderKey = null;
            Error = exception.Message;
            Stage = FlowStage.Options;
        }
        catch (PrintingException exception)
        {
            _orderKey = null;
            Error = T(exception.MessageKey);
            Stage = FlowStage.Options;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // The answer may have been lost on the way back: the same key
            // again returns the job if it was made.
            Error = T("error.network");
            Stage = FlowStage.Options;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // The spool could not take the file; the order was cancelled.
            _orderKey = null;
            Error = T("error.unexpected");
            Stage = FlowStage.Options;
        }
    }

    // -----------------------------------------------------------------------
    // Cancel by code (screen 11)
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void Digit(string? digit)
    {
        if (digit is { Length: 1 } && char.IsAsciiDigit(digit[0]) && CodeInput.Length < 6)
        {
            CodeInput += digit;
        }
    }

    [RelayCommand]
    private void Erase()
    {
        if (CodeInput.Length > 0)
        {
            CodeInput = CodeInput[..^1];
        }
    }

    partial void OnCodeInputChanged(string value)
    {
        Error = null;
        OnPropertyChanged(nameof(CanFind));
    }

    [RelayCommand]
    private async Task FindAsync()
    {
        if (!CanFind)
        {
            return;
        }

        var code = CodeInput;
        Stage = FlowStage.Working;

        try
        {
            var job = await Task.Run(() => _orders.FindAsync(code, CancellationToken.None)).ConfigureAwait(true);
            _foundJob = job;

            FoundSummary = $"{job.JobCode} · {T("jobStatus." + job.Status)} · {Formats.Pages(job.SelectedPages, Language)} · {Formats.Money(job.Price.Total, Language)}";

            if (job.Status is "Queued" or "PendingPayment" or "Paid" or "Draft" or "Interrupted")
            {
                Stage = FlowStage.CancelConfirm;
            }
            else
            {
                Error = T("cancel.cannot", T("jobStatus." + job.Status));
                Stage = FlowStage.CancelLookup;
            }
        }
        catch (KioskApiException exception) when (exception.Status == System.Net.HttpStatusCode.NotFound)
        {
            Error = T("cancel.notFound");
            Stage = FlowStage.CancelLookup;
        }
        catch (KioskApiException exception)
        {
            Error = exception.Message;
            Stage = FlowStage.CancelLookup;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Error = T("error.network");
            Stage = FlowStage.CancelLookup;
        }
    }

    [RelayCommand]
    private async Task ConfirmCancelAsync()
    {
        if (_foundJob is null)
        {
            return;
        }

        var job = _foundJob;
        Stage = FlowStage.Working;

        try
        {
            await Task.Run(() => _orders.CancelAsync(job.Id, CancellationToken.None)).ConfigureAwait(true);
            Stage = FlowStage.CancelDone;
        }
        catch (KioskApiException exception)
        {
            Error = exception.Message;
            Stage = FlowStage.CancelLookup;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Error = T("error.network");
            Stage = FlowStage.CancelConfirm;
        }
    }

    partial void OnStageChanged(FlowStage value)
    {
        // Working keeps the step it was started from: preparing a file is still
        // choosing one, placing an order is still the options.
        _step = value switch
        {
            FlowStage.Browse => 0,
            FlowStage.Preview or FlowStage.Edit => 1,
            FlowStage.Options => 2,
            FlowStage.Placed => 3,
            FlowStage.CancelLookup or FlowStage.CancelConfirm or FlowStage.CancelDone => -1,
            _ => _step,
        };

        OnPropertyChanged(nameof(IsFromDrive));
        OnPropertyChanged(nameof(ShowSteps));
        OnPropertyChanged(nameof(Steps));
    }

    /// <summary>The indicator is part of an order, not of the cancel screen.</summary>
    public bool ShowSteps => _step >= 0;

    /// <summary>
    /// File → Preview → Options → Done. The payment step joins in plan step 4,
    /// when there is a payment; until then it is not shown (docs/DESIGN_SYSTEM.md).
    /// </summary>
    public IReadOnlyList<FlowStep> Steps
    {
        get
        {
            string[] keys = ["steps.file", "steps.preview", "steps.options", "steps.done"];

            return
            [
                .. keys.Select((key, index) => new FlowStep(
                    index + 1,
                    T(key),
                    index < _step ? FlowStepState.Done : index == _step ? FlowStepState.Current : FlowStepState.Upcoming)),
            ];
        }
    }

    // -----------------------------------------------------------------------
    // On-screen keyboards (plan section 7.1): the range keypad and the letters
    // -----------------------------------------------------------------------

    /// <summary>The keys of the page-range keypad.</summary>
    public IReadOnlyList<string> RangeKeys { get; } = ["1", "2", "3", "4", "5", "6", "7", "8", "9", ",", "0", "-"];

    [RelayCommand]
    private void RangeKey(string? key)
    {
        if (key is { Length: 1 } && RangeText.Length < 60)
        {
            AllPages = false;
            RangeText += key;
        }
    }

    [RelayCommand]
    private void RangeErase()
    {
        if (RangeText.Length > 0)
        {
            RangeText = RangeText[..^1];
        }
    }

    [ObservableProperty]
    private bool _capitals;

    /// <summary>The letter rows for the screen's language, digits on top.</summary>
    public IReadOnlyList<IReadOnlyList<string>> KeyboardRows
    {
        get
        {
            string[] rows = Language switch
            {
                "ru" => ["1234567890", "йцукенгшщзхъ", "фывапролджэ", "ячсмитьбю.,"],
                "en" => ["1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm.,"],
                _ => ["1234567890", "qüertyuiopöğ", "asdfghjklıə", "zxcvbnmçş.,"],
            };

            return
            [
                .. rows.Select(row => (IReadOnlyList<string>)[
                    .. row.Select(key => Capitals ? key.ToString().ToUpper(new CultureInfo(Language == "az" ? "tr" : Language)) : key.ToString())]),
            ];
        }
    }

    public string SpaceKeyText => T("keyboard.space");

    public string BackspaceKeyText => T("keyboard.backspace");

    public string EnterKeyText => T("keyboard.enter");

    public string ShiftKeyText => T("keyboard.shift");

    [RelayCommand]
    private void Type(string? key)
    {
        if (!string.IsNullOrEmpty(key) && TextContent.Length < 1_000_000)
        {
            TextContent += key;
        }
    }

    [RelayCommand]
    private void TypeSpace() => Type(" ");

    [RelayCommand]
    private void TypeNewLine() => Type("\n");

    [RelayCommand]
    private void TypeBackspace()
    {
        if (TextContent.Length > 0)
        {
            TextContent = TextContent[..^1];
        }
    }

    [RelayCommand]
    private void ToggleCapitals() => Capitals = !Capitals;

    partial void OnCapitalsChanged(bool value) => OnPropertyChanged(nameof(KeyboardRows));

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Requote();
    }

    private string Size(long bytes)
        => bytes >= 1024 * 1024
            ? Formats.Megabytes(bytes / 1024d / 1024d, Language)
            : Formats.Kilobytes(Math.Max(1, bytes / 1024d), Language);
}

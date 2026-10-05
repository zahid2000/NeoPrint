using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NeoPrint.Kiosk.Localization;
using NeoPrint.Kiosk.Printing;
using NeoPrint.Layout;

namespace NeoPrint.Kiosk.ViewModels;

/// <summary>
/// Screen 4a, "Səhifədə yerləşdir" (plan section 2.11; docs/DESIGN_SYSTEM.md):
/// the flash drive's picture placed on the paper at a real size. "Hazır"
/// draws it with the backend's renderer and the print is then locked to 100%
/// on that paper. Nothing is kept: the customer is anonymous.
/// </summary>
public sealed partial class PrintFlowViewModel
{
    /// <summary>The space kept clear around the paper, as in the app.</summary>
    public const double LayoutMarginMm = 10;

    private const double BiggerFactor = 1.1;
    private const double SmallerFactor = 1 / BiggerFactor;

    // The picture as read for this file, and the layout last turned into the
    // document: "Ləğv et" goes back to it, a second visit starts from it.
    private LayoutPicture? _layoutPicture;
    private LayoutProject? _appliedLayout;

    /// <summary>The layout being arranged; one page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(LayoutSizeText), nameof(LayoutEdge), nameof(LayoutOffPage), nameof(CanFinishLayout),
        nameof(CanDeleteLayoutPicture), nameof(IsLayoutA4), nameof(IsLayoutA3), nameof(LayoutSelectedElement))]
    private LayoutProject? _layout;

    /// <summary>The picture the tools act on; -1 when none is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LayoutSizeText), nameof(LayoutSelectedElement), nameof(HasLayoutSelection))]
    private int _layoutSelected = -1;

    /// <summary>The edges and centres a dragged picture has snapped to, drawn while it moves.</summary>
    [ObservableProperty]
    private IReadOnlyList<SnapLine> _layoutLines = [];

    /// <summary>The upright picture for the canvas; the view turns it into a bitmap.</summary>
    public byte[]? LayoutPreview => _layoutPicture?.Preview;

    /// <summary>The document is a layout now: the image editor would undo it, so it is not offered.</summary>
    public bool HasAppliedLayout => _appliedLayout is not null;

    public bool CanEditFile => !HasAppliedLayout;

    public bool CanStartLayout => IsImage && _remote is null;

    public LayoutElement? LayoutSelectedElement
        => Layout is { } layout && LayoutSelected >= 0 && LayoutSelected < layout.Pages[0].Elements.Count
            ? layout.Pages[0].Elements[LayoutSelected]
            : null;

    public bool HasLayoutSelection => LayoutSelectedElement is not null;

    /// <summary>"85,6 × 53,98 mm": the selected picture as it will print.</summary>
    public string? LayoutSizeText => LayoutSelectedElement is { } element
        ? T("layout.selected", Formats.Millimetres(element.WidthMm, Language), Formats.Millimetres(element.HeightMm, Language))
        : null;

    /// <summary>A warning: part of a picture is where the printer cannot reach.</summary>
    public string? LayoutEdge => Layout is { } layout && !LayoutEditing.HasOffPage(layout) && LayoutEditing.LeavesPrintableArea(layout)
        ? T("layout.edge")
        : null;

    /// <summary>An error: a picture is off the paper and "Hazır" waits until it is back.</summary>
    public string? LayoutOffPage => Layout is { } layout && LayoutEditing.HasOffPage(layout) ? T("layout.offPage") : null;

    public bool CanFinishLayout => Layout is { } layout && !LayoutEditing.HasOffPage(layout);

    /// <summary>The last picture stays: an empty page is not a layout.</summary>
    public bool CanDeleteLayoutPicture => Layout is { } layout && layout.Pages[0].Elements.Count > 1;

    public bool IsLayoutA4 => Layout?.Paper == LayoutPaper.A4;

    public bool IsLayoutA3 => Layout?.Paper == LayoutPaper.A3;

    public string LayoutStartText => T("layout.start");

    public string LayoutTitle => T("layout.title");

    public string LayoutHint => T("layout.hint");

    public string LayoutSizeGroup => T("layout.sizeGroup");

    public string LayoutSizeIdCardText => T("layout.sizeIdCard");

    public string LayoutSizePhoto3x4Text => T("layout.sizePhoto3x4");

    public string LayoutSizePhoto10x15Text => T("layout.sizePhoto10x15");

    public string LayoutSizeA6Text => T("layout.sizeA6");

    public string LayoutSizeA5Text => T("layout.sizeA5");

    public string LayoutSizeFitText => T("layout.sizeFit");

    public string LayoutTemplateGroup => T("layout.templateGroup");

    public string LayoutTemplateGridText => T("layout.templateGrid");

    public string LayoutTemplateCardsText => T("layout.templateCards");

    public string LayoutTemplatePhotosText => T("layout.templatePhotos");

    public string LayoutTemplateCentreText => T("layout.templateCentre");

    public string LayoutActionGroup => T("layout.actionGroup");

    public string LayoutRotateText => T("layout.rotate");

    public string LayoutBiggerText => T("layout.bigger");

    public string LayoutSmallerText => T("layout.smaller");

    public string LayoutCentreText => T("layout.centre");

    public string LayoutDuplicateText => T("layout.duplicate");

    public string LayoutDeleteText => T("layout.delete");

    public string LayoutPaperGroup => T("layout.paperGroup");

    public string LayoutDoneText => T("layout.done");

    [RelayCommand]
    private async Task StartLayoutAsync()
    {
        if (!CanStartLayout || _document is null || _workFolder is null)
        {
            return;
        }

        Error = null;

        if (_appliedLayout is null)
        {
            // Read again each time: an edit (turn, brightness) may have changed the picture.
            var picturePath = LayoutSource(_document, _workFolder);
            Stage = FlowStage.Working;

            try
            {
                _layoutPicture = await Task.Run(() => KioskLayouts.Load(picturePath)).ConfigureAwait(true);
            }
            catch (PrintingException exception)
            {
                Error = T(exception.MessageKey);
                Stage = FlowStage.Preview;
                return;
            }

            var empty = new LayoutProject(
                LayoutProject.CurrentVersion, LayoutPaper.A4, LayoutOrientation.Portrait, LayoutMarginMm, [new LayoutPage([])]);
            Layout = LayoutEditing.Add(empty, 0, KioskLayouts.ImageKey, _layoutPicture.Aspect);
        }
        else
        {
            Layout = _appliedLayout;
        }

        LayoutSelected = 0;
        LayoutLines = [];
        OnPropertyChanged(nameof(LayoutPreview));
        Stage = FlowStage.Layout;
    }

    // The edited picture when the editor saved one, else the file as it came.
    private static string LayoutSource(PreparedDocument document, string folder)
    {
        var edited = Path.ChangeExtension(document.PdfPath, ".png");

        return document.Edited && File.Exists(edited) ? edited : Directory.GetFiles(folder, "source.*")[0];
    }

    /// <summary>A real size for the selected picture, held the way it is now; "Fit" fills the paper inside the margins.</summary>
    [RelayCommand]
    private void LayoutSize(string? key)
    {
        if (Layout is not { } layout || LayoutSelectedElement is not { } element)
        {
            return;
        }

        LayoutElement sized;

        if (key == "Fit")
        {
            var (width, height) = layout.PageSizeMm();
            var quarter = ((int)Math.Round(element.RotationDeg / 90) % 2) != 0;
            var usableWidth = width - (2 * layout.MarginMm);
            var usableHeight = height - (2 * layout.MarginMm);
            var factor = quarter
                ? Math.Min(usableWidth / element.HeightMm, usableHeight / element.WidthMm)
                : Math.Min(usableWidth / element.WidthMm, usableHeight / element.HeightMm);

            sized = LayoutEditing.Centre(layout, LayoutEditing.Scale(element, factor));
        }
        else if (DocumentPresets.Find(key) is { } preset)
        {
            sized = LayoutEditing.Resize(element, preset.WidthMm, preset.HeightMm);
        }
        else
        {
            return;
        }

        Layout = LayoutEditing.Replace(layout, 0, LayoutSelected, sized);
    }

    /// <summary>
    /// A ready-made arrangement on A4: cards at the ID-1 size, photos at 3 × 4,
    /// the 2 × 2 grid and the centre at the picture's present size (reduced to fit).
    /// </summary>
    [RelayCommand]
    private void LayoutApplyTemplate(string? key)
    {
        if (Layout is null || (LayoutSelectedElement ?? Layout.Pages[0].Elements.FirstOrDefault()) is not { } element)
        {
            return;
        }

        LayoutElement Sized(DocumentPreset preset) => LayoutEditing.Resize(element, preset.WidthMm, preset.HeightMm);

        var (template, picture) = key switch
        {
            "Grid" => (LayoutTemplate.Grid2x2, element),
            "Cards" => (LayoutTemplate.Cards2x4, Sized(DocumentPresets.IdCard)),
            "Photos" => (LayoutTemplate.Photo3x4x6, Sized(DocumentPresets.Photo3x4)),
            "Centre" => (LayoutTemplate.PassportCentered, element),
            _ => ((LayoutTemplate?)null, element),
        };

        if (template is { } chosen)
        {
            Layout = LayoutTemplates.Apply(
                chosen, [new TemplatePicture(KioskLayouts.ImageKey, picture.WidthMm, picture.HeightMm)], LayoutMarginMm);
            LayoutSelected = 0;
        }
    }

    [RelayCommand]
    private void LayoutRotate() => ReplaceSelected(LayoutEditing.Rotate90);

    [RelayCommand]
    private void LayoutBigger() => ReplaceSelected(element => LayoutEditing.Scale(element, BiggerFactor));

    [RelayCommand]
    private void LayoutSmaller() => ReplaceSelected(element => LayoutEditing.Scale(element, SmallerFactor));

    [RelayCommand]
    private void LayoutCentre() => ReplaceSelected(element => LayoutEditing.Centre(Layout!, element));

    [RelayCommand]
    private void LayoutDuplicate()
    {
        if (Layout is { } layout && HasLayoutSelection && layout.Pages[0].Elements.Count < LayoutJson.MaxElementsPerPage)
        {
            Layout = LayoutEditing.Duplicate(layout, 0, LayoutSelected);
            LayoutSelected = Layout.Pages[0].Elements.Count - 1;
        }
    }

    [RelayCommand]
    private void LayoutDelete()
    {
        if (Layout is { } layout && HasLayoutSelection && CanDeleteLayoutPicture)
        {
            Layout = LayoutEditing.Delete(layout, 0, LayoutSelected);
            LayoutSelected = -1;
        }
    }

    /// <summary>A4, or A3 where the printer takes it; the pictures keep their places.</summary>
    [RelayCommand]
    private void LayoutPaperSize(string? paper)
    {
        if (Layout is { } layout)
        {
            Layout = layout with { Paper = paper == "A3" && CanA3 ? LayoutPaper.A3 : LayoutPaper.A4 };
        }
    }

    /// <summary>A touch on the canvas: the topmost picture under it, or none.</summary>
    public void LayoutSelectAt(double xMm, double yMm)
    {
        var elements = Layout?.Pages[0].Elements ?? [];

        for (var index = elements.Count - 1; index >= 0; index--)
        {
            var box = LayoutGeometry.Bounds(elements[index]);

            if (xMm >= box.Left && xMm <= box.Right && yMm >= box.Top && yMm <= box.Bottom)
            {
                LayoutSelected = index;
                return;
            }
        }

        LayoutSelected = -1;
    }

    /// <summary>A finger moved the selected picture from where the touch began, snapping as it goes.</summary>
    public void LayoutDrag(LayoutElement start, double dxMm, double dyMm)
    {
        if (Layout is { } layout && HasLayoutSelection)
        {
            var (moved, lines) = LayoutEditing.Move(layout, 0, LayoutSelected, start, dxMm, dyMm);
            Layout = LayoutEditing.Replace(layout, 0, LayoutSelected, moved);
            LayoutLines = lines;
        }
    }

    /// <summary>Two fingers spread or pinched the selected picture by a factor since they touched.</summary>
    public void LayoutPinch(LayoutElement start, double factor)
    {
        if (Layout is { } layout && HasLayoutSelection && factor > 0)
        {
            Layout = LayoutEditing.Replace(layout, 0, LayoutSelected, LayoutEditing.Scale(start, factor));
        }
    }

    public void LayoutGestureEnded() => LayoutLines = [];

    private void ReplaceSelected(Func<LayoutElement, LayoutElement> change)
    {
        if (Layout is { } layout && LayoutSelectedElement is { } element)
        {
            Layout = LayoutEditing.Replace(layout, 0, LayoutSelected, change(element));
        }
    }

    /// <summary>"Hazır": the layout becomes the document, printed at 100% on its paper.</summary>
    [RelayCommand]
    private async Task LayoutDoneAsync()
    {
        if (Layout is not { } project || !CanFinishLayout || _layoutPicture is not { } picture
            || _document is null || _workFolder is null)
        {
            return;
        }

        var document = _document;
        var pdf = Path.Combine(_workFolder, $"layout-{Guid.NewGuid():N}.pdf");

        Error = null;
        Stage = FlowStage.Working;

        try
        {
            await Task.Run(() => KioskLayouts.Render(project, picture.Bytes, pdf)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is PrintingException or IOException or UnauthorizedAccessException)
        {
            Error = T("layout.failed");
            Stage = FlowStage.Layout;
            return;
        }

        _document = document with { PdfPath = pdf, PageCount = 1, Edited = true };
        _appliedLayout = project;
        LayoutLines = [];

        await LoadPagesAsync().ConfigureAwait(true);
        ResetOptions();
        ApplyRealSize(KioskLayouts.RealSizePaper(project));
        Stage = FlowStage.Preview;

        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(AllPagesText));
        OnPropertyChanged(nameof(HasAppliedLayout));
        OnPropertyChanged(nameof(CanEditFile));
    }

    /// <summary>"Ləğv et": back to the preview with the document as it was.</summary>
    [RelayCommand]
    private void LayoutCancel()
    {
        Layout = _appliedLayout;
        LayoutLines = [];
        Error = null;
        Stage = FlowStage.Preview;
    }

    /// <summary>A new file, or none: the layout belonged to the last one.</summary>
    private void ResetLayout()
    {
        _layoutPicture = null;
        _appliedLayout = null;
        Layout = null;
        LayoutSelected = -1;
        LayoutLines = [];
        OnPropertyChanged(nameof(LayoutPreview));
        OnPropertyChanged(nameof(HasAppliedLayout));
        OnPropertyChanged(nameof(CanEditFile));
        OnPropertyChanged(nameof(CanStartLayout));
    }
}

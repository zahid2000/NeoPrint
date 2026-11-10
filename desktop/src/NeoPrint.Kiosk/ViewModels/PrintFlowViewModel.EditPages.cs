using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NeoPrint.Kiosk.ViewModels;

/// <summary>
/// The editor shows one page at a time with one set of tools beside it
/// (T-419): the tools no longer scroll away with the pages. "Əvvəlki səhifə"
/// and "Növbəti səhifə" - or a swipe across the page - go from one to the next.
/// </summary>
public sealed partial class PrintFlowViewModel
{
    /// <summary>The page being edited: its place in <see cref="Pages"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditPage), nameof(EditPageText), nameof(CanShowPreviousPage), nameof(CanShowNextPage))]
    [NotifyCanExecuteChangedFor(nameof(ShowPreviousPageCommand), nameof(ShowNextPageCommand))]
    private int _editIndex;

    /// <summary>The page on screen in the editor; null with no pages.</summary>
    public PageTile? EditPage => EditIndex >= 0 && EditIndex < Pages.Count ? Pages[EditIndex] : null;

    /// <summary>"Səhifə 2 / 5" - its place now, after any move or removal.</summary>
    public string EditPageText => Pages.Count == 0 ? string.Empty : T("edit.pageOf", EditIndex + 1, Pages.Count);

    public bool CanShowPreviousPage => EditIndex > 0;

    public bool CanShowNextPage => EditIndex < Pages.Count - 1;

    public string EditPreviousText => T("edit.previousPage");

    public string EditNextText => T("edit.nextPage");

    [RelayCommand(CanExecute = nameof(CanShowPreviousPage))]
    private void ShowPreviousPage() => EditIndex--;

    [RelayCommand(CanExecute = nameof(CanShowNextPage))]
    private void ShowNextPage() => EditIndex++;

    /// <summary>A sideways swipe on the page: right to left is the next one.</summary>
    public void SwipeEditPage(bool forward)
    {
        if (forward && CanShowNextPage)
        {
            EditIndex++;
        }
        else if (!forward && CanShowPreviousPage)
        {
            EditIndex--;
        }
    }

    /// <summary>The pages changed (loaded, moved, removed): the index stays on the list.</summary>
    private void OnEditPagesChanged()
    {
        var clamped = Math.Clamp(EditIndex, 0, Math.Max(0, Pages.Count - 1));
        if (clamped != EditIndex)
        {
            EditIndex = clamped;
        }

        OnPropertyChanged(nameof(EditPage));
        OnPropertyChanged(nameof(EditPageText));
        OnPropertyChanged(nameof(CanShowPreviousPage));
        OnPropertyChanged(nameof(CanShowNextPage));
        ShowPreviousPageCommand.NotifyCanExecuteChanged();
        ShowNextPageCommand.NotifyCanExecuteChanged();
    }
}

using CommunityToolkit.Mvvm.Input;

namespace NeoPrint.Kiosk.ViewModels;

/// <summary>
/// The top of every stage as the poster draws it (T-371): "← Geri" and the
/// stage's title, instead of the step indicator. "Geri" does what each stage's
/// own back button did; a stage with nothing to go back to has none.
/// </summary>
public sealed partial class PrintFlowViewModel
{
    /// <summary>The stage's title beside "Geri"; null where the stage says it in its own words.</summary>
    public string? StageTitle => Stage switch
    {
        FlowStage.Browse => BrowseTitle,
        FlowStage.UserFiles => UserFilesTitle,
        FlowStage.Preview => string.IsNullOrEmpty(FileName) ? PreviewTitle : $"{PreviewTitle} — {FileName}",
        FlowStage.Edit => EditTitle,
        FlowStage.Layout => LayoutTitle,
        FlowStage.Options => OptionsTitle,
        FlowStage.PaymentMethod => PayTitle,
        FlowStage.QrPayment => T("pay.qrTitle"),
        // Expired and the cancel screens say it in the middle, with their sign.
        _ => null,
    };

    /// <summary>Whether "Geri" shows: only where it goes somewhere.</summary>
    public bool HasBack => Stage is FlowStage.Browse or FlowStage.UserFiles or FlowStage.Preview or FlowStage.Edit
        or FlowStage.Layout or FlowStage.Options or FlowStage.QrPayment or FlowStage.CancelLookup or FlowStage.CancelConfirm;

    /// <summary>The header shows: a title, a "Geri", or both.</summary>
    public bool HasHeader => HasBack || StageTitle is not null;

    /// <summary>"Növbəni izlə" on the receipt (the poster's screen 10).</summary>
    public string FollowQueueText => T("placed.followQueue");

    /// <summary>"Ödəniş üçün qalan vaxt": the words over the countdown (T-374).</summary>
    public string CountdownLabel => T("pay.timeLeftLabel");

    /// <summary>"Geri": what the stage's own back button did.</summary>
    [RelayCommand]
    private void Back()
    {
        switch (Stage)
        {
            case FlowStage.Browse or FlowStage.UserFiles or FlowStage.CancelLookup:
                LeaveCommand.Execute(null);
                break;
            case FlowStage.Preview:
                BackToBrowseCommand.Execute(null);
                break;
            case FlowStage.Edit:
                CancelEditCommand.Execute(null);
                break;
            case FlowStage.Layout:
                LayoutCancelCommand.Execute(null);
                break;
            case FlowStage.Options:
                ShowPreviewCommand.Execute(null);
                break;
            case FlowStage.QrPayment:
                ChangeMethodCommand.Execute(null);
                break;
            case FlowStage.CancelConfirm:
                StartCancelAgainCommand.Execute(null);
                break;
        }
    }

    private void HeaderChanged()
    {
        OnPropertyChanged(nameof(StageTitle));
        OnPropertyChanged(nameof(HasBack));
        OnPropertyChanged(nameof(HasHeader));
    }
}

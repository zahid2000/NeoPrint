using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Localization;

namespace NeoPrint.Kiosk.ViewModels;

/// <summary>One card of screen 10: a file the user prepared in the app (docs/DESIGN_SYSTEM.md §5.1, UserQrFiles).</summary>
/// <param name="Detail">Pages and what was already chosen in the app, in one line.</param>
/// <param name="Amount">A paid order's amount; null for an unpaid one.</param>
/// <param name="Reason">Why it cannot be printed here; null when it can.</param>
public sealed record UserFileCard(UserQrItem Item, string Title, string Detail, string? Amount, string? PaidText, string ActionText, string? Reason)
{
    public bool IsPrintable => Reason is null;

    public bool IsPaid => Item.IsPaid;
}

/// <summary>
/// Screen 10 (plan section 7.3; 9.4): the user showed their QR and the
/// kiosk lists what they got ready in the app. An unpaid one goes to the
/// options screen with the app's choices and is paid here; one paid in the app
/// for this kiosk goes straight to the queue.
/// </summary>
public sealed partial class PrintFlowViewModel
{
    /// <summary>Cards on one page of screen 10 (docs/DESIGN_SYSTEM.md §5.1: at most 5).</summary>
    public const int UserFilesPerPage = 5;

    private UserQrFiles? _userQr;
    private UserQrItem? _remote;
    private int _userPage;

    /// <summary>The cards of the page being shown.</summary>
    public ObservableCollection<UserFileCard> UserFiles { get; } = [];

    /// <summary>Why something chosen in the app is not possible here, shown on the options screen.</summary>
    [ObservableProperty]
    private string? _presetNote;

    [ObservableProperty]
    private string? _collectedCode;

    [ObservableProperty]
    private string? _collectedPosition;

    public bool HasUserFiles => UserFiles.Count > 0;

    /// <summary>A registered user opened this order with their personal QR (plan section 9.4).</summary>
    public bool IsUserQrSession => _userQr is not null;

    public bool HasNoUserFiles => _userQr is not null && _userQr.Items.Count == 0;

    public bool HasMoreUserFiles => _userQr is not null && (_userPage + 1) * UserFilesPerPage < _userQr.Items.Count;

    public bool HasEarlierUserFiles => _userPage > 0;

    public bool IsFromApp => _remote is not null;

    public string UserFilesTitle => T("userQr.title");

    public string UserFilesBody => T("userQr.body");

    public string UserFilesEmpty => T("userQr.empty");

    public string UserFilesEmptyHint => T("userQr.emptyHint");

    public string NextPageText => T("common.next");

    public string PreviousPageText => T("common.previous");

    public string CollectedTitle => T("userQr.collectedTitle");

    public string CollectedBody => T("userQr.collectedBody");

    /// <summary>The scanner read a personal QR (<c>NPU:...</c>): the user's files for this kiosk.</summary>
    public async Task StartUserQrAsync(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        Reset();
        BeginVisit();
        Stage = FlowStage.Working;

        try
        {
            _userQr = await Task.Run(() => _orders.ScanUserQrAsync(code, CancellationToken.None)).ConfigureAwait(true);
            ShowUserPage(0);
            Track("screen_view", ("screen", nameof(FlowStage.UserFiles)));
        }
        catch (KioskApiException exception)
        {
            // Not ours, too old, or the account cannot use it: the server's words.
            Error = exception.Message;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Error = T("error.network");
        }

        Stage = FlowStage.UserFiles;
        NotifyUserFiles();
    }

    [RelayCommand]
    private void NextUserPage() => ShowUserPage(_userPage + 1);

    [RelayCommand]
    private void PreviousUserPage() => ShowUserPage(_userPage - 1);

    /// <summary>A card's own button: options for an unpaid one, the queue for one paid for this kiosk.</summary>
    [RelayCommand]
    private async Task PickUserFileAsync(UserFileCard? card)
    {
        if (card is not { IsPrintable: true } || _userQr is not { } files)
        {
            return;
        }

        Error = null;

        if (!card.IsPaid)
        {
            _remote = card.Item;
            FileName = card.Item.FileName;
            ResetOptions();
            ApplyPreset(card.Item.Preset);
            ApplyRealSize(card.Item.RealSizePaper);
            Stage = FlowStage.Options;
            OnPropertyChanged(nameof(PageCount));
            OnPropertyChanged(nameof(AllPagesText));
            OnPropertyChanged(nameof(IsFromApp));
            Requote();
            return;
        }

        Stage = FlowStage.Working;

        try
        {
            var job = await Task.Run(() => _orders.ReleaseHeldAsync(files.Grant, card.Item.Id, CancellationToken.None)).ConfigureAwait(true);

            CollectedCode = job.JobCode;
            CollectedPosition = job.QueuePosition is { } position ? T("placed.position", position) : null;
            Track("order_collected", ("jobCode", job.JobCode));
            _visitDone = true;
            Stage = FlowStage.Collected;
        }
        catch (KioskApiException exception)
        {
            Error = exception.Message;
            Stage = FlowStage.UserFiles;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Asking again is safe: an order already in the queue stays as it is.
            Error = T("error.network");
            Stage = FlowStage.UserFiles;
        }
    }

    /// <summary>
    /// What the user chose in the app goes onto the options screen; what this
    /// kiosk cannot do is said, and what it can is put instead (docs/DESIGN_SYSTEM.md §5.1).
    /// </summary>
    private void ApplyPreset(PreparedPreset? preset)
    {
        PresetNote = null;

        if (preset is null)
        {
            return;
        }

        var missing = new List<string>();

        if (preset.Copies is { } copies)
        {
            Copies = Math.Clamp(copies, 1, 50);
        }

        if (preset.Color == true && !CanColor)
        {
            missing.Add(T("userQr.missingColor"));
        }
        else if (preset.Color is { } color)
        {
            Color = color;
        }

        if (preset.Duplex == true && !CanDuplex)
        {
            missing.Add(T("userQr.missingDuplex"));
        }
        else if (preset.Duplex is { } duplex)
        {
            Duplex = duplex;
        }

        if (preset.Paper == "A3" && !CanA3)
        {
            missing.Add(T("userQr.missingA3"));
        }
        else if (preset.Paper is { } paper)
        {
            Paper = paper;
        }

        if (preset.PagesPerSheet is { } perSheet and (1 or 2 or 4))
        {
            PagesPerSheet = perSheet;
        }

        if (!string.IsNullOrWhiteSpace(preset.PageRange))
        {
            AllPages = false;
            RangeText = preset.PageRange.Trim();
        }

        PresetNote = missing.Count == 0 ? null : T("userQr.presetNotHere", string.Join(", ", missing));
    }

    private void ShowUserPage(int page)
    {
        UserFiles.Clear();

        if (_userQr is not { } files)
        {
            NotifyUserFiles();
            return;
        }

        var last = Math.Max(0, (files.Items.Count - 1) / UserFilesPerPage);
        _userPage = Math.Clamp(page, 0, last);

        foreach (var item in files.Items.Skip(_userPage * UserFilesPerPage).Take(UserFilesPerPage))
        {
            UserFiles.Add(Card(item));
        }

        NotifyUserFiles();
    }

    private UserFileCard Card(UserQrItem item)
    {
        var parts = new List<string> { T("userQr.pages", item.Pages) };

        if (item.Options is { } options)
        {
            if (options.Copies > 1)
            {
                parts.Add(T("userQr.copies", options.Copies));
            }

            parts.Add(options.Color ? T("options.color") : T("options.bw"));
            parts.Add(options.Paper);
        }
        else if (item.Preset is { } preset)
        {
            if (preset.Copies is > 1)
            {
                parts.Add(T("userQr.copies", preset.Copies.Value));
            }

            if (preset.Color is { } color)
            {
                parts.Add(color ? T("options.color") : T("options.bw"));
            }

            if (preset.Paper is { } paper)
            {
                parts.Add(paper);
            }
        }

        var detail = string.Join(" · ", parts);

        if (!item.IsPaid)
        {
            return new UserFileCard(item, item.FileName, detail, null, null, T("userQr.choose"), null);
        }

        var amount = item.Amount is { } value ? Formats.Money(value, Language) : null;
        var reason = item.Here ? null : T("userQr.paidElsewhere", item.KioskCode ?? string.Empty);

        return new UserFileCard(item, item.FileName, detail, amount, T("userQr.paid"), T("userQr.collect"), reason);
    }

    private void ResetUserQr()
    {
        _userQr = null;
        _remote = null;
        _userPage = 0;
        UserFiles.Clear();
        PresetNote = null;
        CollectedCode = null;
        CollectedPosition = null;
    }

    private void NotifyUserFiles()
    {
        OnPropertyChanged(nameof(HasUserFiles));
        OnPropertyChanged(nameof(HasNoUserFiles));
        OnPropertyChanged(nameof(HasMoreUserFiles));
        OnPropertyChanged(nameof(HasEarlierUserFiles));
        OnPropertyChanged(nameof(IsFromApp));
    }
}

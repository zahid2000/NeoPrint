using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;

namespace NeoPrint.Domain.Sharing;

/// <summary>Whether a share can still be opened and saved (docs/DECISIONS.md 2026-10-05, sharing).</summary>
public enum ShareAvailability
{
    /// <summary>The file is as it was sent.</summary>
    Available = 0,

    /// <summary>The sender deleted it: "Fayl artıq yoxdur".</summary>
    Gone = 1,

    /// <summary>The sender changed it after sending: the recipient never gets something other than what was sent.</summary>
    Changed = 2,
}

/// <summary>
/// A file one user sent another (plan sections 6 and 8.2; collection
/// <c>file_shares</c>).
/// </summary>
/// <remarks>
/// <para>
/// Nothing lands in the recipient's files until they save it: the share points
/// at the sender's file as it was when sent (its PDF), and "Fayllarıma saxla"
/// makes the recipient's own encrypted copy. Deleting or changing the file on
/// the sender's side makes an unsaved share unavailable; a saved copy is the
/// recipient's and is not touched.
/// </para>
/// <para>
/// What the lists show - name, pages, sensitivity - is copied when sending,
/// so a share still reads after the file is gone.
/// </para>
/// </remarks>
public sealed class Share : Entity
{
    /// <summary>The longest note a sender can add.</summary>
    public const int MaxMessageLength = 200;

    public Guid FileId { get; set; }

    public Guid FromUserId { get; set; }

    public Guid ToUserId { get; set; }

    /// <summary>The sender's note, plain text; null when there is none.</summary>
    public string? Message { get; set; }

    public string FileName { get; set; } = string.Empty;

    public int PageCount { get; set; }

    public bool IsSensitive { get; set; }

    /// <summary>The object key of the PDF that was sent: what "as it was sent" means.</summary>
    public string PdfKey { get; set; } = string.Empty;

    /// <summary>The copy being made; set before any byte is copied, so a retry reuses it.</summary>
    public Guid? SavingFileId { get; private set; }

    /// <summary>The recipient's copy, once saved.</summary>
    public Guid? SavedFileId { get; private set; }

    public DateTime? SavedAt { get; private set; }

    /// <summary>A share of <paramref name="file"/> as it is now.</summary>
    /// <exception cref="DomainException">The file is not ready, or it is sent to its owner.</exception>
    public static Share Send(StoredFile file, Guid toUserId, string? message, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Status != FileStatus.Ready || file.Pdf is null || file.DeletedAt is not null)
        {
            throw new DomainException(UserMessage.Of("files.notReadyNamed", ("name", file.FileName)));
        }

        if (toUserId == file.UserId)
        {
            throw new DomainException("shares.toSelf");
        }

        return new Share
        {
            CreatedAt = now,
            FileId = file.Id,
            FromUserId = file.UserId,
            ToUserId = toUserId,
            Message = CleanMessage(message),
            FileName = file.FileName,
            PageCount = file.PageCount,
            IsSensitive = file.IsSensitive,
            PdfKey = file.Pdf.Key,
        };
    }

    /// <summary>The sender's note as it is kept: trimmed, one line, at most <see cref="MaxMessageLength"/>.</summary>
    /// <exception cref="DomainException">Longer than <see cref="MaxMessageLength"/>.</exception>
    public static string? CleanMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        // Control characters (a newline, a bidi override) would let a note
        // dress itself up as something else in the recipient's list.
        var clean = new string([.. message.Trim().Select(c => char.IsControl(c) || c is '‪' or '‫' or '‭' or '‮' or '⁦' or '⁧' or '⁨' or '⁩' ? ' ' : c)]);

        return clean.Length > MaxMessageLength
            ? throw new DomainException(UserMessage.Of("shares.messageTooLong", ("max", MaxMessageLength)))
            : clean;
    }

    /// <summary>Whether <paramref name="file"/> (the sender's, or null when it is gone) is still what was sent.</summary>
    public ShareAvailability AvailabilityOf(StoredFile? file)
        => file is null || file.DeletedAt is not null || file.Id != FileId
            ? ShareAvailability.Gone
            : file.Status == FileStatus.Ready && file.Pdf?.Key == PdfKey
                ? ShareAvailability.Available
                : ShareAvailability.Changed;

    /// <summary>The recipient started saving: their copy will have <paramref name="fileId"/>.</summary>
    public void BeginSave(Guid fileId)
    {
        if (fileId == Guid.Empty)
        {
            throw new ArgumentException("A copy needs an id.", nameof(fileId));
        }

        SavingFileId = fileId;
    }

    /// <summary>The copy is in the recipient's files.</summary>
    /// <exception cref="DomainException">No save was begun.</exception>
    public void CompleteSave(DateTime now)
    {
        if (SavingFileId is not { } fileId)
        {
            throw new DomainException("shares.notSaving");
        }

        SavedFileId = fileId;
        SavedAt = now;
    }
}

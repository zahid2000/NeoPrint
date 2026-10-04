using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Files;

/// <summary>Where a file came from (plan section 8.2, the source badge).</summary>
public enum FileSource
{
    Uploaded = 0,

    /// <summary>The mobile document scanner (plan section 2.11).</summary>
    Scan = 1,

    /// <summary>Built by the page layout editor.</summary>
    Layout = 2,

    /// <summary>Read off a flash drive at a kiosk and kept after a mobile payment.</summary>
    Usb = 3,

    /// <summary>Received from another user.</summary>
    Shared = 4,
}

/// <summary>How far the pipeline has got with a file.</summary>
public enum FileStatus
{
    /// <summary>Stored, waiting for conversion.</summary>
    Pending = 0,

    /// <summary>Being converted to PDF.</summary>
    Converting = 1,

    /// <summary>Converted; the page count and previews are available.</summary>
    Ready = 2,

    /// <summary>Conversion failed. <see cref="StoredFile.FailureReason"/> says why.</summary>
    Failed = 3,
}

/// <summary>When a file deletes itself (plan section 2.13).</summary>
public enum AutoDeleteRule
{
    /// <summary>Kept until the user deletes it.</summary>
    Never = 0,

    AfterPrint = 1,
    AfterOneDay = 2,
    AfterSevenDays = 3,
    AfterThirtyDays = 4,
}

/// <summary>One object belonging to a file: the original, a render, a preview page.</summary>
/// <param name="ProviderId">The storage provider it was written to.</param>
/// <param name="Key">The object key.</param>
/// <param name="Sha256">Hash of the encrypted bytes.</param>
/// <param name="EncryptedLength">Bytes stored.</param>
/// <param name="PlaintextLength">Bytes before encryption.</param>
/// <param name="KekId">The key encryption key the data key is wrapped with.</param>
public sealed record FileObject(
    Guid ProviderId,
    string Key,
    string Sha256,
    long EncryptedLength,
    long PlaintextLength,
    string KekId);

/// <summary>One saved revision of a file (plan section 2.2, editing).</summary>
public sealed class FileVersion
{
    public int Number { get; set; }

    public FileObject Object { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>What produced it, e.g. "səhifə silindi".</summary>
    public string? Note { get; set; }

    /// <summary>
    /// The version's own format. An edit can change it - reordering the pages
    /// of a Word document produces a PDF. Null on versions saved before this
    /// was recorded, which were in the file's own format.
    /// </summary>
    public string? Extension { get; set; }
}

/// <summary>
/// A picture a layout keeps its own copy of (docs/DECISIONS.md 2026-10-05).
/// </summary>
public sealed class LayoutAsset
{
    /// <summary>The name the layout's elements use for it, as <c>asset:{Key}</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public FileObject Object { get; set; } = null!;

    /// <summary>The picture's format, for the editor that draws it again.</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>Copied from a sensitive file, which makes the layout sensitive too.</summary>
    public bool Sensitive { get; set; }
}

/// <summary>
/// A file a user owns (plan sections 2.2, 2.13).
/// </summary>
/// <remarks>
/// <para>
/// The document holds metadata only. The bytes live in object storage,
/// encrypted, and the document records which provider they went to - reads
/// follow that rather than the current primary, so changing the primary does
/// not orphan anything.
/// </para>
/// <para>
/// Anonymous kiosk files are not here at all: they stay in the kiosk's local
/// encrypted spool and are deleted when the job ends (plan section 2.2).
/// </para>
/// </remarks>
public sealed class StoredFile : Entity
{
    public Guid UserId { get; set; }

    /// <summary>The name the user sees. Never used to decide what the file is.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>The format the content proved to be, as an extension.</summary>
    public string Extension { get; set; } = string.Empty;

    public string MediaType { get; set; } = string.Empty;

    public FileSource Source { get; set; }

    public FileStatus Status { get; private set; } = FileStatus.Pending;

    /// <summary>Why conversion failed, in words a user can act on.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>The file as it was uploaded.</summary>
    public FileObject Original { get; set; } = null!;

    /// <summary>
    /// The PDF everything downstream uses: page count, previews, printing.
    /// </summary>
    /// <remarks>
    /// Null until conversion finishes. For a file that is already a PDF this
    /// still points at its own object, so nothing downstream has to special-case
    /// the format (plan section 2.2).
    /// </remarks>
    public FileObject? Pdf { get; private set; }

    /// <summary>Pages in <see cref="Pdf"/>. Zero until conversion finishes.</summary>
    public int PageCount { get; private set; }

    public List<FileVersion> Versions { get; set; } = [];

    public AutoDeleteRule AutoDelete { get; set; } = AutoDeleteRule.Never;

    /// <summary>
    /// Whether this holds a personal document - an identity card, a
    /// certificate - as the scanner marks it (plan section 2.11).
    /// </summary>
    /// <remarks>
    /// Drives the warning shown before sharing and the default auto-delete
    /// rule. Never relaxed automatically: a file marked sensitive stays so.
    /// </remarks>
    public bool IsSensitive { get; set; }

    public DateTime? DeletedAt { get; set; }

    /// <summary>The scanned document's real width, as its type or the user gave it (plan section 2.11).</summary>
    public double? PhysicalWidthMm { get; set; }

    /// <summary>The scanned document's real height.</summary>
    public double? PhysicalHeightMm { get; set; }

    /// <summary>The scanner's document type, e.g. <c>IdCard</c>, or <c>Custom</c>.</summary>
    public string? DocumentType { get; set; }

    /// <summary>
    /// A finished document that is printed, shared and deleted but never
    /// edited: a saved scan, and a copy of one (docs/DECISIONS.md 2026-10-05).
    /// </summary>
    public bool EditLocked { get; set; }

    /// <summary>
    /// The page layout a <see cref="FileSource.Layout"/> file was drawn from,
    /// with its pictures named <c>asset:{key}</c> (plan section 2.11). Null for
    /// every other file.
    /// </summary>
    public string? LayoutProjectJson { get; private set; }

    /// <summary>The layout's own copies of its pictures, so it can be edited after the sources are gone.</summary>
    public List<LayoutAsset> LayoutAssets { get; set; } = [];

    /// <summary>Records the layout a new layout file was drawn from.</summary>
    public void StartLayout(string layoutJson, IReadOnlyList<LayoutAsset> assets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layoutJson);
        ArgumentNullException.ThrowIfNull(assets);

        if (Source != FileSource.Layout || LayoutProjectJson is not null)
        {
            throw new DomainException("files.layout.notLayout");
        }

        LayoutProjectJson = layoutJson;
        LayoutAssets = [.. assets];
    }

    /// <summary>
    /// Saves a changed layout as a new version, the way an edit is saved
    /// (plan section 2.2): the PDF drawn from it becomes the newest version and
    /// the file is converted again.
    /// </summary>
    /// <param name="pdf">The new PDF, already in storage.</param>
    /// <param name="layoutJson">The layout, pictures named <c>asset:{key}</c>.</param>
    /// <param name="assets">Every picture it uses: the ones it kept and the ones newly copied.</param>
    /// <returns>Versions dropped by <paramref name="keep"/> and pictures no longer used, whose objects can be deleted.</returns>
    /// <exception cref="DomainException">Not a layout, or not ready.</exception>
    public (IReadOnlyList<FileVersion> Versions, IReadOnlyList<LayoutAsset> Assets) ApplyLayout(
        FileObject pdf, string layoutJson, IReadOnlyList<LayoutAsset> assets, int keep, ActorContext actor, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layoutJson);
        ArgumentNullException.ThrowIfNull(assets);

        if (Source != FileSource.Layout || LayoutProjectJson is null)
        {
            throw new DomainException("files.layout.notLayout");
        }

        var versions = ApplyEdit(pdf, "pdf", new UserMessage("files.note.layoutEdited").Encode(), keep, actor, now);
        var used = assets.Select(asset => asset.Key).ToHashSet(StringComparer.Ordinal);
        var unused = LayoutAssets.Where(asset => !used.Contains(asset.Key)).ToList();

        LayoutProjectJson = layoutJson;
        LayoutAssets = [.. assets];

        return (versions, unused);
    }

    /// <summary>
    /// Every object stored for the file - original, PDF, versions, layout
    /// pictures - each once. What deleting the file has to delete and what
    /// counts against the quota.
    /// </summary>
    public IEnumerable<FileObject> AllObjects()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<FileObject?> all =
        [
            Original,
            Pdf,
            .. Versions.Select(version => version.Object),
            .. LayoutAssets.Select(asset => asset.Object),
        ];

        foreach (var stored in all)
        {
            if (stored is not null && seen.Add(stored.Key))
            {
                yield return stored;
            }
        }
    }

    /// <summary>When an order made from it last printed at a kiosk; what <see cref="AutoDeleteRule.AfterPrint"/> waits for.</summary>
    public DateTime? PrintedAt { get; private set; }

    /// <summary>A kiosk printed it. The latest time is kept.</summary>
    public void MarkPrinted(DateTime at)
    {
        if (PrintedAt is null || at > PrintedAt)
        {
            PrintedAt = at;
        }
    }

    /// <summary>
    /// Whether its own rule says it should be gone by <paramref name="now"/>
    /// (plan section 2.13): after a print, or a number of days after the upload.
    /// Whether nothing still needs it - an order, a scheduled print - is the
    /// caller's to check.
    /// </summary>
    public bool AutoDeleteDue(DateTime now) => DeletedAt is null && AutoDelete switch
    {
        AutoDeleteRule.AfterPrint => PrintedAt is not null,
        AutoDeleteRule.AfterOneDay => now >= CreatedAt.AddDays(1),
        AutoDeleteRule.AfterSevenDays => now >= CreatedAt.AddDays(7),
        AutoDeleteRule.AfterThirtyDays => now >= CreatedAt.AddDays(30),
        _ => false,
    };

    private static readonly StateMachine<FileStatus> Transitions =
        new StateMachine<FileStatus>(nameof(StoredFile))
            .Allow(FileStatus.Pending, FileStatus.Converting, FileStatus.Failed)
            .Allow(FileStatus.Converting, FileStatus.Ready, FileStatus.Failed)
            // A retry after a failure converts again; so does a new version,
            // which first waits in Pending like a fresh upload (ApplyEdit).
            .Allow(FileStatus.Ready, FileStatus.Converting, FileStatus.Pending)
            .Allow(FileStatus.Failed, FileStatus.Converting);

    /// <summary>The newest saved version, or null while there is none.</summary>
    public FileVersion? LatestVersion => Versions.Count == 0 ? null : Versions.MaxBy(v => v.Number);

    /// <summary>
    /// What the PDF is made from: the newest version, or the original when
    /// nothing has been edited. The original itself is never changed.
    /// </summary>
    public FileObject CurrentContent => LatestVersion?.Object ?? Original;

    /// <summary>The format of <see cref="CurrentContent"/>.</summary>
    public string CurrentExtension => LatestVersion?.Extension ?? Extension;

    /// <summary>
    /// Saves an edit as a new version and sends the file back for conversion
    /// (plan section 2.2: "redaktə olunmuş versiya yeni fayl versiyası kimi
    /// saxlanılır, orijinal toxunulmaz qalır").
    /// </summary>
    /// <param name="stored">The edited content, already in storage.</param>
    /// <param name="extension">Its format.</param>
    /// <param name="note">What the edit was, for the version list.</param>
    /// <param name="keep"><c>Files.VersionsToKeep</c>.</param>
    /// <returns>Versions dropped to stay within <paramref name="keep"/>, whose objects can be deleted.</returns>
    /// <exception cref="DomainException">The file is not ready - there is nothing settled to edit.</exception>
    public IReadOnlyList<FileVersion> ApplyEdit(
        FileObject stored, string extension, string? note, int keep, ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        if (Status != FileStatus.Ready)
        {
            throw new DomainException(UserMessage.Of("files.notReadyNamed", ("name", FileName)));
        }

        Record(Transitions.Transition(Id, Status, FileStatus.Pending, actor, now));

        var dropped = AddVersion(stored, note, keep, now, extension);
        Status = FileStatus.Pending;

        return dropped;
    }

    /// <summary>Marks the file as being converted.</summary>
    public void BeginConversion(ActorContext actor, DateTime now)
    {
        Record(Transitions.Transition(Id, Status, FileStatus.Converting, actor, now));
        Status = FileStatus.Converting;
        FailureReason = null;
    }

    /// <summary>Records the PDF and the page count conversion produced.</summary>
    /// <exception cref="DomainException">The page count is not positive.</exception>
    public void Complete(FileObject pdf, int pageCount, ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        if (pageCount <= 0)
        {
            // A zero-page PDF would be priced at the minimum order and printed
            // as nothing, so it is a failure rather than a result.
            throw new DomainException(
                UserMessage.Of("files.emptyAfterConversion", ("name", FileName), ("count", pageCount)));
        }

        Record(Transitions.Transition(Id, Status, FileStatus.Ready, actor, now));

        Pdf = pdf;
        PageCount = pageCount;
        Status = FileStatus.Ready;
        FailureReason = null;
    }

    /// <summary>Records that conversion failed, with a reason for the user.</summary>
    /// <remarks>
    /// Kept encoded (<see cref="UserMessage.Encode"/>), so the reason reads in
    /// the language of whoever looks at the file.
    /// </remarks>
    public void Fail(UserMessage reason, ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(reason);

        Record(Transitions.Transition(Id, Status, FileStatus.Failed, actor, now));

        Status = FileStatus.Failed;
        FailureReason = reason.Encode();
    }

    /// <summary>Adds a saved revision, keeping at most <paramref name="keep"/>.</summary>
    /// <returns>The versions dropped, so their objects can be deleted.</returns>
    public IReadOnlyList<FileVersion> AddVersion(
        FileObject stored, string? note, int keep, DateTime now, string? extension = null)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);

        Versions.Add(new FileVersion
        {
            Number = Versions.Count == 0 ? 1 : Versions.Max(v => v.Number) + 1,
            Object = stored,
            CreatedAt = now,
            Note = note,
            Extension = extension,
        });

        if (Versions.Count <= keep)
        {
            return [];
        }

        // Oldest first: the original is not among the versions, so trimming
        // here never discards what the user uploaded.
        var dropped = Versions.OrderBy(v => v.Number).Take(Versions.Count - keep).ToList();

        foreach (var version in dropped)
        {
            Versions.Remove(version);
        }

        return dropped;
    }
}

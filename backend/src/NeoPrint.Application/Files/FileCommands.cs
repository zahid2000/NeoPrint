using MediatR;

using NeoPrint.Application.Behaviors;
using NeoPrint.Domain.Files;

namespace NeoPrint.Application.Files;

/// <summary>Reading and writing the file documents.</summary>
public interface IFileRepository
{
    Task<StoredFile?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>One page of a user's files, newest first, excluding deleted ones.</summary>
    Task<IReadOnlyList<StoredFile>> ListAsync(
        Guid userId, int skip, int take, CancellationToken cancellationToken = default);

    Task<long> CountAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Total plaintext bytes a user holds, for the quota check.</summary>
    Task<long> TotalBytesAsync(Guid userId, CancellationToken cancellationToken = default);

    Task AddAsync(StoredFile file, CancellationToken cancellationToken = default);

    Task UpdateAsync(StoredFile file, CancellationToken cancellationToken = default);

    /// <summary>
    /// Files not deleted whose own rule may say "now" (plan section 2.13), oldest
    /// first: printed ones set to go after a print, and ones past their days.
    /// </summary>
    /// <summary>Every file of a user not deleted yet, for the account deletion.</summary>
    Task<IReadOnlyList<Guid>> ListLiveIdsAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> ListAutoDeleteDueAsync(DateTime now, int take, CancellationToken cancellationToken = default);
}

/// <summary>What the API reports about a file.</summary>
/// <param name="EditLocked">A saved scan or a copy of one: printed, never edited (docs/DECISIONS.md 2026-10-05).</param>
/// <param name="HasLayout">A layout that "Tərtibatı redaktə et" can open again.</param>
/// <param name="DocumentType">The scanner's document type, for a scan.</param>
public sealed record UserFileResult(
    Guid Id,
    string FileName,
    string Extension,
    string MediaType,
    string Source,
    string Status,
    string? FailureReason,
    long SizeBytes,
    int PageCount,
    bool IsSensitive,
    string AutoDelete,
    int VersionCount,
    DateTime CreatedAt,
    bool EditLocked = false,
    bool HasLayout = false,
    string? DocumentType = null,
    double? PhysicalWidthMm = null,
    double? PhysicalHeightMm = null);

/// <summary>A page of a user's files.</summary>
public sealed record FileListResult(IReadOnlyList<UserFileResult> Items, long Total);

/// <summary>
/// Uploads a file (plan section 2.2).
/// </summary>
/// <param name="UserId">The owner.</param>
/// <param name="FileName">The name the uploader gave.</param>
/// <param name="Content">The bytes. Read once, streamed to storage.</param>
/// <param name="DeclaredLength">
/// What the request said the length is, for the size check before anything is
/// read. Verified against what actually arrives.
/// </param>
/// <param name="KioskId">
/// The kiosk the file is meant for, when one is chosen: its limits override
/// the defaults (plan section 2.2).
/// </param>
/// <param name="Source">Upload, scan, layout, USB or shared.</param>
/// <param name="IsSensitive">Whether the scanner marked it a personal document.</param>
public sealed record UploadFileCommand(
    Guid UserId,
    string FileName,
    Stream Content,
    long DeclaredLength,
    Guid? KioskId,
    FileSource Source,
    bool IsSensitive) : IRequest<UserFileResult>;

/// <summary>Lists a user's files, newest first.</summary>
public sealed record ListFilesQuery(Guid UserId, int Page, int PageSize) : IRequest<FileListResult>;

/// <summary>One file's metadata.</summary>
public sealed record GetFileQuery(Guid UserId, Guid FileId) : IRequest<UserFileResult>;

/// <summary>Changes when a file deletes itself (plan section 2.13).</summary>
public sealed record SetAutoDeleteCommand(Guid UserId, Guid FileId, AutoDeleteRule Rule)
    : IRequest<UserFileResult>, ITransactionalRequest;

/// <summary>
/// Deletes a file and everything stored for it.
/// </summary>
/// <remarks>
/// Transactional: the document and the objects have to go together, or a
/// deleted file leaves bytes behind on a provider with nothing pointing at
/// them (plan section 2.10).
/// </remarks>
public sealed record DeleteFileCommand(Guid UserId, Guid FileId) : IRequest, ITransactionalRequest;

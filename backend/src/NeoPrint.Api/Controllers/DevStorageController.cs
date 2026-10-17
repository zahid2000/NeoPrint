using System.Text;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Leftovers in the store, made on purpose in Development (T-215): an object
/// under <c>files/</c> that no file points at - what the file cleanup is for.
/// 404 in every other environment.
/// </summary>
[ApiController]
[Route("api/dev/storage")]
[AllowAnonymous]
[DevelopmentOnly]
public sealed class DevStorageController : ControllerBase
{
    private readonly IFileStorage _storage;

    public DevStorageController(IFileStorage storage) => _storage = storage;

    /// <summary>An encrypted dummy PDF under a user and a file that do not exist, named <c>dev-leftover.pdf</c> so a check knows its own; its key comes back.</summary>
    [HttpPost("orphan")]
    public async Task<ActionResult<DevOrphanResult>> Orphan(CancellationToken cancellationToken)
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4\n% NeoPrint development leftover\n"));
        var stored = await _storage
            .WriteAsync(StorageBucket.Files, $"files/{Guid.NewGuid():N}/{Guid.NewGuid():N}/dev-leftover.pdf", content, "application/pdf", cancellationToken);

        return Ok(new DevOrphanResult(stored.Key, stored.EncryptedLength));
    }
}

public sealed record DevOrphanResult(string Key, long Size);

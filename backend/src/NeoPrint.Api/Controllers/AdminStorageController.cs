using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Storage;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Storage providers and the sync between them (plan sections 2.10, 8A.3 -
/// the "Fayl saxlama" page).
/// </summary>
/// <remarks>
/// Everything here needs <c>storage.manage</c>, which only SuperAdmin holds by
/// default: pointing the installation at a different object store is the most
/// consequential thing the panel can do.
/// </remarks>
[ApiController]
[Route("api/admin/storage")]
[HasPermission(Permissions.StorageManage)]
public sealed class AdminStorageController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminStorageController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    // -----------------------------------------------------------------------
    // Providers
    // -----------------------------------------------------------------------

    [HttpGet("providers")]
    public async Task<ActionResult<IReadOnlyList<StorageProviderResponse>>> ListProviders(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListStorageProvidersQuery(), cancellationToken));

    /// <summary>Tests credentials that are not saved yet - the button on the add form.</summary>
    [HttpPost("providers/test")]
    public async Task<ActionResult<StorageTestResponse>> TestCredentials(
        [FromBody] StorageProviderInput input, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new TestStorageCredentialsCommand(input), cancellationToken));

    [HttpPost("providers")]
    [Idempotent]
    public async Task<ActionResult<StorageProviderResponse>> AddProvider(
        [FromBody] StorageProviderInput input, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new AddStorageProviderCommand(input, _currentUser.AdminUserId, _currentUser.Ip), cancellationToken));

    [HttpPut("providers/{id:guid}")]
    public async Task<ActionResult<StorageProviderResponse>> UpdateProvider(
        Guid id, [FromBody] StorageProviderInput input, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new UpdateStorageProviderCommand(id, input, _currentUser.AdminUserId, _currentUser.Ip), cancellationToken));

    [HttpPost("providers/{id:guid}/test")]
    public async Task<ActionResult<StorageTestResponse>> TestProvider(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new TestStorageProviderCommand(id, _currentUser.AdminUserId, _currentUser.Ip), cancellationToken));

    /// <param name="request">{ "role": "Primary" | "Secondary" | "Inactive" }</param>
    [HttpPut("providers/{id:guid}/role")]
    public async Task<ActionResult<StorageProviderResponse>> SetRole(
        Guid id, [FromBody] SetStorageRoleRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new SetStorageProviderRoleCommand(id, request.Role ?? string.Empty, _currentUser.AdminUserId, _currentUser.Ip),
            cancellationToken));

    // -----------------------------------------------------------------------
    // Sync jobs
    // -----------------------------------------------------------------------

    [HttpGet("sync-jobs")]
    public async Task<ActionResult<IReadOnlyList<StorageSyncJobResponse>>> ListJobs(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListStorageSyncJobsQuery(), cancellationToken));

    [HttpGet("sync-jobs/{id:guid}")]
    public async Task<ActionResult<StorageSyncJobResponse>> GetJob(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetStorageSyncJobQuery(id), cancellationToken));

    /// <summary>Starts a migration or a mirror from the current primary to the target.</summary>
    [HttpPost("sync-jobs")]
    [Idempotent]
    public async Task<ActionResult<StorageSyncJobResponse>> StartJob(
        [FromBody] StartStorageSyncRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new StartStorageSyncCommand(
                request.Mode ?? string.Empty,
                request.TargetId,
                request.SwitchPrimaryOnComplete,
                _currentUser.AdminUserId,
                _currentUser.Ip),
            cancellationToken));

    /// <param name="operation">pause, resume, cancel or retry-failures.</param>
    /// <remarks>
    /// Not called "action": that is a reserved route value in MVC, and using it
    /// with a constraint breaks routing for the whole application, not just
    /// this endpoint.
    /// </remarks>
    [HttpPost("sync-jobs/{id:guid}/{operation:regex(^(pause|resume|cancel|retry-failures)$)}")]
    public async Task<ActionResult<StorageSyncJobResponse>> ControlJob(
        Guid id, string operation, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new ControlStorageSyncCommand(id, operation, _currentUser.AdminUserId, _currentUser.Ip), cancellationToken));

    [HttpGet("sync-jobs/{id:guid}/failures")]
    public async Task<ActionResult<IReadOnlyList<StorageSyncFailureResponse>>> ListFailures(
        Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListStorageSyncFailuresQuery(id), cancellationToken));

    /// <summary>
    /// Rewraps every stored object onto the current key encryption key, after
    /// a rotation (plan section 2.13). Runs in the background.
    /// </summary>
    [HttpPost("rewrap")]
    [Idempotent]
    public async Task<IActionResult> StartRewrap(CancellationToken cancellationToken)
    {
        await _sender.Send(new StartKeyRewrapCommand(_currentUser.AdminUserId, _currentUser.Ip), cancellationToken);
        return Accepted();
    }
}

public sealed record SetStorageRoleRequest(string? Role);

public sealed record StartStorageSyncRequest(string? Mode, Guid TargetId, bool SwitchPrimaryOnComplete);

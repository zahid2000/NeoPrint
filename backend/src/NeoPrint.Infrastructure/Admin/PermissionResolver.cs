using System.Text.Json;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Auth;
using NeoPrint.Domain.Admin;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Admin;

/// <summary>
/// Resolves an administrator's effective permissions, cached in Redis for five
/// minutes (plan section 8A.2).
/// </summary>
/// <remarks>
/// Changing a role or an assignment clears the affected entries, so a revoked
/// permission stops working straight away rather than at the end of the cache
/// window. Because roles are also resolved into the access token, which lives
/// fifteen minutes, both the cache and the token bound how long a stale
/// permission can survive.
/// </remarks>
public sealed class CachedPermissionResolver : IPermissionResolver
{
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MongoContext _context;
    private readonly IDistributedCache _cache;
    private readonly ILogger<CachedPermissionResolver> _logger;

    public CachedPermissionResolver(
        MongoContext context, IDistributedCache cache, ILogger<CachedPermissionResolver> logger)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
    }

    public async Task<EffectivePermissions> ResolveAsync(
        Guid adminUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var cached = await _cache.GetStringAsync(CacheKey(adminUserId), cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                var deserialized = JsonSerializer.Deserialize<EffectivePermissions>(cached, JsonOptions);
                if (deserialized is not null)
                {
                    return deserialized;
                }
            }
        }
        catch (Exception exception)
        {
            // A cache problem must never widen someone's permissions, and it
            // must not take the panel down either: fall through and resolve.
            _logger.LogWarning(exception, "Reading cached permissions for {AdminId} failed.", adminUserId);
        }

        var resolved = await ResolveFromDatabaseAsync(adminUserId, cancellationToken).ConfigureAwait(false);

        try
        {
            await _cache.SetStringAsync(
                    CacheKey(adminUserId),
                    JsonSerializer.Serialize(resolved, JsonOptions),
                    CacheOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Caching permissions for {AdminId} failed.", adminUserId);
        }

        return resolved;
    }

    public Task InvalidateAsync(Guid adminUserId, CancellationToken cancellationToken = default)
        => _cache.RemoveAsync(CacheKey(adminUserId), cancellationToken);

    /// <inheritdoc />
    public async Task InvalidateRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        // Redis holds no reverse index from role to administrator, so the
        // holders are read from MongoDB. Role edits are rare, and this is
        // cheaper than maintaining that index for every permission check.
        var holders = await _context.AdminUsers
            .Find(admin => admin.RoleIds.Contains(roleId))
            .Project(admin => admin.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var adminId in holders)
        {
            await InvalidateAsync(adminId, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Cleared cached permissions for {Count} administrators after role {RoleId} changed.",
            holders.Count, roleId);
    }

    private async Task<EffectivePermissions> ResolveFromDatabaseAsync(
        Guid adminUserId, CancellationToken cancellationToken)
    {
        var admin = await _context.AdminUsers
            .Find(candidate => candidate.Id == adminUserId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // A deactivated account resolves to nothing, so a token issued before
        // it was disabled cannot be used to do anything.
        if (admin is null || !admin.IsActive)
        {
            return new EffectivePermissions([], AllKiosks: false, [], IsActive: false);
        }

        var roles = await _context.AdminRoles
            .Find(role => admin.RoleIds.Contains(role.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var permissions = roles
            .SelectMany(role => role.EffectivePermissions)
            .ToHashSet(StringComparer.Ordinal);

        // An account that looks after some kiosks has nothing of the whole
        // service, whatever its roles hold (T-311).
        if (!admin.KioskScope.All)
        {
            permissions.ExceptWith(Permissions.GlobalOnly);
        }

        return new EffectivePermissions(
            permissions,
            admin.KioskScope.All,
            admin.KioskScope.KioskIds,
            IsActive: true);
    }

    private static string CacheKey(Guid adminUserId) => $"admin:permissions:{adminUserId}";
}

/// <inheritdoc cref="IAdminUserRepository" />
public sealed class AdminUserRepository : IAdminUserRepository
{
    private readonly MongoContext _context;

    public AdminUserRepository(MongoContext context) => _context = context;

    public Task<AdminUser?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.AdminUsers.Find(admin => admin.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<AdminUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default)
        => _context.AdminUsers.Find(admin => admin.Email == normalizedEmail)
            .FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<AdminUser>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.AdminUsers
            .Find(FilterDefinition<AdminUser>.Empty)
            .SortBy(admin => admin.Email)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<long> CountActiveWithRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
        => _context.AdminUsers.CountDocumentsAsync(
            admin => admin.IsActive && admin.RoleIds.Contains(roleId),
            cancellationToken: cancellationToken);

    public Task<long> CountWithRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
        => _context.AdminUsers.CountDocumentsAsync(
            admin => admin.RoleIds.Contains(roleId),
            cancellationToken: cancellationToken);

    public Task AddAsync(AdminUser admin, CancellationToken cancellationToken = default)
        => _context.AdminUsers.InsertOneAsync(admin, cancellationToken: cancellationToken);

    public Task UpdateAsync(AdminUser admin, CancellationToken cancellationToken = default)
        => _context.AdminUsers.ReplaceOneAsync(
            existing => existing.Id == admin.Id, admin, cancellationToken: cancellationToken);
}

/// <inheritdoc cref="IAdminRoleRepository" />
public sealed class AdminRoleRepository : IAdminRoleRepository
{
    private readonly MongoContext _context;

    public AdminRoleRepository(MongoContext context) => _context = context;

    public Task<AdminRole?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.AdminRoles.Find(role => role.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<AdminRole?> FindByNameAsync(string name, CancellationToken cancellationToken = default)
        => _context.AdminRoles.Find(role => role.Name == name).FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<AdminRole>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.AdminRoles
            .Find(FilterDefinition<AdminRole>.Empty)
            .SortBy(role => role.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<AdminRole>> FindByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        return await _context.AdminRoles
            .Find(role => ids.Contains(role.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task AddAsync(AdminRole role, CancellationToken cancellationToken = default)
        => _context.AdminRoles.InsertOneAsync(role, cancellationToken: cancellationToken);

    public Task UpdateAsync(AdminRole role, CancellationToken cancellationToken = default)
        => _context.AdminRoles.ReplaceOneAsync(
            existing => existing.Id == role.Id, role, cancellationToken: cancellationToken);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.AdminRoles.DeleteOneAsync(role => role.Id == id, cancellationToken);
}

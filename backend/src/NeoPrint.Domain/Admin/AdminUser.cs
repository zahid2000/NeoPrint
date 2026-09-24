using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Admin;

/// <summary>
/// A role in the dynamic RBAC model (plan section 8A.2, <c>admin_roles</c>).
/// </summary>
/// <remarks>
/// Roles are created, edited, cloned and deleted from the admin panel. Only
/// <c>SuperAdmin</c> is a system role: it always holds every permission and can
/// be neither edited nor deleted.
/// </remarks>
public sealed class AdminRole : Entity
{
    /// <summary>Name of the one system role.</summary>
    public const string SuperAdminName = "SuperAdmin";

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public List<string> Permissions { get; set; } = [];

    /// <summary>System roles cannot be edited or deleted.</summary>
    public bool IsSystem { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsSuperAdmin => IsSystem && Name == SuperAdminName;

    /// <summary>The permissions this role actually grants.</summary>
    public IEnumerable<string> EffectivePermissions
        => IsSuperAdmin ? Admin.Permissions.All : Permissions;

    /// <summary>
    /// Replaces the permission set, rejecting anything not in the catalogue.
    /// </summary>
    /// <exception cref="DomainException">A permission is unknown, or the role is a system role.</exception>
    public void SetPermissions(IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        if (IsSystem)
        {
            throw new DomainException($"The system role '{Name}' cannot be edited.");
        }

        var requested = permissions.Distinct(StringComparer.Ordinal).ToList();
        var unknown = requested.Where(permission => !Admin.Permissions.IsKnown(permission)).ToList();

        if (unknown.Count > 0)
        {
            // Catching this here rather than at the API boundary means a role
            // can never hold a permission that no check will ever look at.
            throw new DomainException(
                "Unknown permissions: " + string.Join(", ", unknown.Order(StringComparer.Ordinal)));
        }

        Permissions = requested;
        UpdatedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Which kiosks an admin user may see and act on (plan section 8A.2).
/// </summary>
public sealed class KioskScope
{
    /// <summary>All kiosks, present and future.</summary>
    public bool All { get; set; } = true;

    /// <summary>The kiosks in scope when <see cref="All"/> is false.</summary>
    public List<Guid> KioskIds { get; set; } = [];

    public static KioskScope Everything() => new() { All = true };

    public static KioskScope Only(params Guid[] kioskIds)
        => new() { All = false, KioskIds = [.. kioskIds] };

    public bool Includes(Guid kioskId) => All || KioskIds.Contains(kioskId);
}

/// <summary>
/// An administrator or operator account (plan section 5, <c>admin_users</c>).
/// </summary>
public sealed class AdminUser : Entity
{
    /// <summary>After this many failed attempts the account locks (plan section 2.8).</summary>
    public const int MaxFailedLogins = 5;

    /// <summary>How long the lock lasts.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public string Email { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public List<Guid> RoleIds { get; set; } = [];

    public KioskScope KioskScope { get; set; } = KioskScope.Everything();

    public bool IsActive { get; set; } = true;

    /// <summary>Set for the seeded SuperAdmin and after an admin resets a password.</summary>
    public bool MustChangePassword { get; set; }

    public int FailedLoginCount { get; set; }

    public DateTime? LockedUntil { get; set; }

    public DateTime? LastLoginAt { get; set; }

    /// <summary>For Telegram alerting, when the operator has opted in.</summary>
    public string? TelegramChatId { get; set; }

    public bool IsLockedOut(DateTime utcNow) => LockedUntil is not null && LockedUntil > utcNow;

    /// <summary>
    /// Records a failed sign-in, locking the account once the limit is reached.
    /// </summary>
    public void RegisterFailedLogin(DateTime utcNow)
    {
        FailedLoginCount++;

        if (FailedLoginCount >= MaxFailedLogins)
        {
            LockedUntil = utcNow + LockoutDuration;
            // Reset the counter with the lock, so that one further failure
            // after it expires does not lock the account again immediately.
            FailedLoginCount = 0;
        }
    }

    /// <summary>Records a successful sign-in and clears any lockout state.</summary>
    public void RegisterSuccessfulLogin(DateTime utcNow)
    {
        FailedLoginCount = 0;
        LockedUntil = null;
        LastLoginAt = utcNow;
    }

    /// <summary>
    /// Clears a lockout without recording a sign-in.
    /// </summary>
    /// <remarks>
    /// What a password reset does: the point of the reset is to let somebody
    /// back in, so leaving them locked out would defeat it. The sign-in
    /// timestamp is untouched, because nobody has signed in yet.
    /// </remarks>
    public void ClearLockout()
    {
        FailedLoginCount = 0;
        LockedUntil = null;
    }
}

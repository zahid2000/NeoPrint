using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Tests.Admin;

public sealed class AdminRoleTests
{
    [Fact]
    public void SuperAdmin_holds_every_permission_in_the_catalogue()
    {
        var role = new AdminRole { Name = AdminRole.SuperAdminName, IsSystem = true, Permissions = [] };

        role.EffectivePermissions.Should().BeEquivalentTo(Permissions.All);
    }

    [Fact]
    public void A_normal_role_grants_only_what_it_was_given()
    {
        var role = new AdminRole { Name = "Maliyyə" };

        role.SetPermissions([Permissions.PaymentsView, Permissions.PaymentsRefund]);

        role.EffectivePermissions.Should().BeEquivalentTo([Permissions.PaymentsView, Permissions.PaymentsRefund]);
    }

    [Fact]
    public void A_permission_that_no_check_would_ever_look_at_is_refused()
    {
        var role = new AdminRole { Name = "Yeni rol" };

        var act = () => role.SetPermissions([Permissions.JobsView, "payments.steal"]);

        act.Should().Throw<DomainException>().WithMessage("*payments.steal*");
    }

    [Fact]
    public void A_refused_permission_set_leaves_the_role_untouched()
    {
        var role = new AdminRole { Name = "Yeni rol" };
        role.SetPermissions([Permissions.JobsView]);

        try
        {
            role.SetPermissions([Permissions.JobsCancel, "not.a.permission"]);
        }
        catch (DomainException)
        {
            // expected
        }

        role.Permissions.Should().Equal(Permissions.JobsView);
    }

    [Fact]
    public void Duplicate_permissions_are_collapsed()
    {
        var role = new AdminRole { Name = "Yeni rol" };

        role.SetPermissions([Permissions.JobsView, Permissions.JobsView]);

        role.Permissions.Should().Equal(Permissions.JobsView);
    }

    [Fact]
    public void The_system_role_cannot_be_edited()
    {
        var role = new AdminRole { Name = AdminRole.SuperAdminName, IsSystem = true };

        var act = () => role.SetPermissions([Permissions.JobsView]);

        act.Should().Throw<DomainException>().WithMessage("*cannot be edited*");
    }
}

public sealed class PermissionCatalogueTests
{
    [Fact]
    public void Every_permission_belongs_to_exactly_one_group()
    {
        var all = Permissions.Catalogue.SelectMany(group => group.Permissions).ToList();

        all.Should().OnlyHaveUniqueItems();
        all.Should().HaveCount(Permissions.All.Count);
    }

    [Fact]
    public void Permission_names_follow_the_dotted_convention()
    {
        foreach (var permission in Permissions.All)
        {
            permission.Should().MatchRegex(@"^[a-z]+(\.[a-z]+)+$", $"'{permission}' should be dotted lower case");
        }
    }

    [Fact]
    public void Group_keys_are_unique()
    {
        Permissions.Catalogue.Select(group => group.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_permissions_the_plan_names_are_all_present()
    {
        // Spot checks against plan section 8A.2, so a rename cannot silently
        // drop a permission the admin panel expects.
        Permissions.IsKnown("kiosks.activate").Should().BeTrue();
        Permissions.IsKnown("devices.revoke").Should().BeTrue();
        Permissions.IsKnown("operator.panel").Should().BeTrue();
        Permissions.IsKnown("payments.refund").Should().BeTrue();
        Permissions.IsKnown("storage.manage").Should().BeTrue();
        Permissions.IsKnown("roles.manage").Should().BeTrue();
        Permissions.IsKnown("audit.view").Should().BeTrue();
    }
}

public sealed class AdminUserLockoutTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Four_failed_attempts_do_not_lock_the_account()
    {
        var admin = new AdminUser();

        for (var i = 0; i < 4; i++)
        {
            admin.RegisterFailedLogin(Now);
        }

        admin.IsLockedOut(Now).Should().BeFalse();
    }

    [Fact]
    public void The_fifth_failed_attempt_locks_the_account_for_fifteen_minutes()
    {
        var admin = new AdminUser();

        for (var i = 0; i < 5; i++)
        {
            admin.RegisterFailedLogin(Now);
        }

        admin.IsLockedOut(Now).Should().BeTrue();
        admin.IsLockedOut(Now.AddMinutes(14)).Should().BeTrue();
        admin.IsLockedOut(Now.AddMinutes(16)).Should().BeFalse();
    }

    [Fact]
    public void After_a_lock_expires_one_more_failure_does_not_lock_again_immediately()
    {
        var admin = new AdminUser();
        for (var i = 0; i < 5; i++)
        {
            admin.RegisterFailedLogin(Now);
        }

        var later = Now.AddMinutes(16);
        admin.RegisterFailedLogin(later);

        admin.IsLockedOut(later).Should().BeFalse("the counter restarts with the lock");
    }

    [Fact]
    public void A_successful_sign_in_clears_the_failure_count_and_the_lock()
    {
        var admin = new AdminUser();
        admin.RegisterFailedLogin(Now);
        admin.RegisterFailedLogin(Now);

        admin.RegisterSuccessfulLogin(Now);

        admin.FailedLoginCount.Should().Be(0);
        admin.LockedUntil.Should().BeNull();
        admin.LastLoginAt.Should().Be(Now);
    }
}

public sealed class KioskScopeTests
{
    [Fact]
    public void An_unrestricted_scope_includes_every_kiosk_including_new_ones()
    {
        var scope = KioskScope.Everything();

        scope.Includes(Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public void A_restricted_scope_includes_only_the_listed_kiosks()
    {
        var mine = Guid.NewGuid();
        var scope = KioskScope.Only(mine);

        scope.Includes(mine).Should().BeTrue();
        scope.Includes(Guid.NewGuid()).Should().BeFalse();
    }
}

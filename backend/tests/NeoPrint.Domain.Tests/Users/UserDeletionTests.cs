using NeoPrint.Domain.Users;

namespace NeoPrint.Domain.Tests.Users;

/// <summary>"Hesabımı sil" on the account itself (plan section 2.13; docs/DECISIONS.md T-147).</summary>
public sealed class UserDeletionTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);

    private static User NewUser() => new()
    {
        Email = "Ali@Example.com",
        FullName = "Əli Əliyev",
        PasswordHash = "hash",
        DeviceIds = ["device-1"],
        FcmTokens = [new PushDevice { Token = "fcm-1", Language = "az", Platform = "android" }],
    };

    [Fact]
    public void Deleting_closes_it_and_leaves_no_way_back_to_the_person()
    {
        var user = NewUser();

        user.Delete(Now);

        user.Status.Should().Be(UserStatus.Deleted);
        user.CanSignIn.Should().BeFalse();
        user.DeletedAt.Should().Be(Now);
        user.Email.Should().StartWith("deleted-").And.NotContain("ali").And.NotContain("@");
        user.FullName.Should().BeNull();
        user.PasswordHash.Should().BeEmpty();
        user.FcmTokens.Should().BeEmpty();
        user.DeviceIds.Should().Equal(["device-1"], "they keep the referral checks honest");
    }

    [Fact]
    public void The_hash_is_the_address_s_whatever_its_case_and_differs_per_account()
    {
        var id = Guid.NewGuid();

        User.DeletedEmail("ALI@example.com ", id).Should().Be(User.DeletedEmail("ali@example.com", id));
        User.DeletedEmail("ali@example.com", Guid.NewGuid()).Should().NotBe(User.DeletedEmail("ali@example.com", id));
    }

    [Fact]
    public void A_second_delete_changes_nothing()
    {
        var user = NewUser();
        user.Delete(Now);
        var email = user.Email;

        user.Delete(Now.AddHours(1));

        user.Email.Should().Be(email);
        user.DeletedAt.Should().Be(Now);
    }
}

using NeoPrint.Domain.Common;
using NeoPrint.Domain.Notifications;
using NeoPrint.Domain.Users;

namespace NeoPrint.Domain.Tests.Notifications;

/// <summary>A notification and the push devices it goes to (plan section 8.4; docs/DECISIONS.md T-146).</summary>
public sealed class NotificationTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);

    private static Notification NewNotification(string? link = "neoprint://wallet") => Notification.Create(
        Guid.NewGuid(),
        NotificationType.RefundCompleted,
        UserMessage.Of("notifications.refundCompleted.title"),
        UserMessage.Of("notifications.refundCompleted.body", ("amount", 1.5m), ("destination", "Wallet")),
        link,
        "RefundCompleted:1",
        Now);

    [Fact]
    public void It_keeps_the_message_keys_not_the_text_and_starts_unread_with_its_push_pending()
    {
        var notification = NewNotification();

        UserMessage.Decode(notification.Title).Key.Should().Be("notifications.refundCompleted.title");
        UserMessage.Decode(notification.Body).Arguments.Should().ContainKey("amount");
        notification.IsRead.Should().BeFalse();
        notification.Push.Should().Be(PushState.Pending);
        notification.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public void It_opens_an_app_screen_or_nothing_and_belongs_to_someone()
    {
        NewNotification(link: null).Link.Should().BeNull();

        var web = () => NewNotification(link: "https://example.com/phish");
        web.Should().Throw<ArgumentException>();

        var nobody = () => Notification.Create(
            Guid.Empty, NotificationType.JobCompleted, UserMessage.Of("a.b"), UserMessage.Of("a.c"), null, "k", Now);
        nobody.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reading_it_again_keeps_the_first_time()
    {
        var notification = NewNotification();

        notification.MarkRead(Now.AddMinutes(1));
        notification.MarkRead(Now.AddMinutes(5));

        notification.ReadAt.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void A_device_is_remembered_once_and_the_push_finishes_by_who_was_reached()
    {
        var notification = NewNotification();
        var hash = PushDevice.Hash("token-a");

        notification.NotePushedTo(hash);
        notification.NotePushedTo(hash);
        notification.PushedTo.Should().ContainSingle();
        notification.WasPushedTo(hash).Should().BeTrue();
        notification.WasPushedTo(PushDevice.Hash("token-b")).Should().BeFalse();

        notification.FinishPush(anyDevice: true);
        notification.Push.Should().Be(PushState.Sent);

        var quiet = NewNotification();
        quiet.FinishPush(anyDevice: false);
        quiet.Push.Should().Be(PushState.NoDevice);
    }

    [Fact]
    public void A_token_hash_is_short_stable_and_says_nothing_of_the_token()
    {
        var hash = PushDevice.Hash("fcm-registration-token");

        hash.Should().HaveLength(16).And.Be(PushDevice.Hash("fcm-registration-token"));
        hash.Should().NotContain("fcm");
        PushDevice.Hash("another-token").Should().NotBe(hash);
    }
}

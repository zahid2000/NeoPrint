using NeoPrint.Domain.Files;

namespace NeoPrint.Domain.Tests.Files;

/// <summary>When a file's own rule says it should be gone (plan section 2.13; docs/DECISIONS.md T-147).</summary>
public sealed class AutoDeleteTests
{
    private static readonly DateTime Uploaded = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private static StoredFile File(AutoDeleteRule rule) => new() { AutoDelete = rule, CreatedAt = Uploaded };

    [Fact]
    public void After_print_waits_for_a_print()
    {
        var file = File(AutoDeleteRule.AfterPrint);
        file.AutoDeleteDue(Uploaded.AddYears(1)).Should().BeFalse("never printed");

        file.MarkPrinted(Uploaded.AddHours(2));
        file.AutoDeleteDue(Uploaded.AddHours(2)).Should().BeTrue();
    }

    [Theory]
    [InlineData(AutoDeleteRule.AfterOneDay, 1)]
    [InlineData(AutoDeleteRule.AfterSevenDays, 7)]
    [InlineData(AutoDeleteRule.AfterThirtyDays, 30)]
    public void Days_count_from_the_upload(AutoDeleteRule rule, int days)
    {
        var file = File(rule);

        file.AutoDeleteDue(Uploaded.AddDays(days).AddSeconds(-1)).Should().BeFalse();
        file.AutoDeleteDue(Uploaded.AddDays(days)).Should().BeTrue();
    }

    [Fact]
    public void Never_and_deleted_are_never_due()
    {
        var never = File(AutoDeleteRule.Never);
        never.MarkPrinted(Uploaded);
        never.AutoDeleteDue(Uploaded.AddYears(5)).Should().BeFalse();

        var gone = File(AutoDeleteRule.AfterOneDay);
        gone.DeletedAt = Uploaded.AddHours(1);
        gone.AutoDeleteDue(Uploaded.AddDays(2)).Should().BeFalse();
    }

    [Fact]
    public void The_latest_print_is_kept()
    {
        var file = File(AutoDeleteRule.AfterPrint);

        file.MarkPrinted(Uploaded.AddHours(5));
        file.MarkPrinted(Uploaded.AddHours(3));

        file.PrintedAt.Should().Be(Uploaded.AddHours(5));
    }
}

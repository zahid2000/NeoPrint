using NeoPrint.Application.Operations;

namespace NeoPrint.Application.Tests.Operations;

/// <summary>
/// What the file cleanup does with one object (T-215): a file in use, an
/// order, a refund, a planned or prepared print keep it; the grace period is a
/// boundary measured on a fixed clock; nothing outside <c>files/</c> is touched.
/// </summary>
public sealed class FileCleanupPlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(24);
    private static readonly Guid User = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid File = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static string Key(string name) => $"files/{User:N}/{File:N}/{name}";

    private static ListedObject Listed(string key, TimeSpan age) => new(Guid.NewGuid(), key, 1024, Now - age);

    private static FileReferences References(
        IEnumerable<string>? live = null, IEnumerable<Guid>? protectedFiles = null, IEnumerable<string>? protectedKeys = null)
        => new(
            (live ?? []).ToHashSet(StringComparer.Ordinal),
            (protectedFiles ?? []).ToHashSet(),
            (protectedKeys ?? []).ToHashSet(StringComparer.Ordinal));

    [Fact]
    public void An_object_the_file_points_at_stays_however_old()
        => FileCleanupPlanner.Decide(Listed(Key("print.pdf"), TimeSpan.FromDays(400)), References(live: [Key("print.pdf")]), Now, Grace)
            .Should().Be(CleanupDecision.Live);

    [Fact]
    public void An_unused_object_of_a_file_an_open_order_refund_or_planned_print_holds_is_never_deleted()
        => FileCleanupPlanner.Decide(Listed(Key("print-v2.pdf"), TimeSpan.FromDays(90)), References(protectedFiles: [File]), Now, Grace)
            .Should().Be(CleanupDecision.Protected);

    [Fact]
    public void An_object_an_order_prints_from_is_kept_even_when_the_file_moved_on()
        => FileCleanupPlanner.Decide(Listed(Key("print-v1.pdf"), TimeSpan.FromDays(90)), References(protectedKeys: [Key("print-v1.pdf")]), Now, Grace)
            .Should().Be(CleanupDecision.Protected);

    [Theory]
    [InlineData(0, CleanupDecision.Recent)]
    [InlineData(23 * 60 + 59, CleanupDecision.Recent)]
    [InlineData(24 * 60, CleanupDecision.Delete)]
    [InlineData(24 * 60 + 1, CleanupDecision.Delete)]
    public void The_grace_period_is_a_boundary_on_the_clock(int minutesOld, CleanupDecision expected)
        => FileCleanupPlanner.Decide(Listed(Key("v3.docx"), TimeSpan.FromMinutes(minutesOld)), References(), Now, Grace)
            .Should().Be(expected);

    [Theory]
    [InlineData("receipts/2026/x.pdf")]
    [InlineData("exports/aaaa/bbbb.zip")]
    [InlineData("files/not-a-guid/also-not/print.pdf")]
    [InlineData("files/only-two")]
    public void Nothing_outside_a_users_file_is_this_jobs(string key)
        => FileCleanupPlanner.Decide(Listed(key, TimeSpan.FromDays(400)), References(), Now, Grace)
            .Should().Be(CleanupDecision.Foreign);

    [Fact]
    public void The_file_is_read_from_the_key()
    {
        FileCleanupPlanner.FileOf(Key("assets/logo.png")).Should().Be(File);
        FileCleanupPlanner.FileOf("files/x/y/z").Should().BeNull();
    }

    [Fact]
    public void The_same_object_and_documents_always_give_the_same_decision()
    {
        var listed = Listed(Key("print-v4.pdf"), TimeSpan.FromDays(3));
        var references = References(live: [Key("print.pdf")]);

        Enumerable.Range(0, 3).Select(_ => FileCleanupPlanner.Decide(listed, references, Now, Grace))
            .Should().AllBeEquivalentTo(CleanupDecision.Delete);
    }
}

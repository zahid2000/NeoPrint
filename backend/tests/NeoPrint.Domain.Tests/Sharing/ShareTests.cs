using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Sharing;

namespace NeoPrint.Domain.Tests.Sharing;

/// <summary>A shared file is what was sent, or nothing (docs/DECISIONS.md 2026-10-05, sharing).</summary>
public sealed class ShareTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);
    private static readonly ActorContext Actor = ActorContext.System("test");

    private static StoredFile ReadyFile(bool sensitive = false, bool locked = false)
    {
        var file = new StoredFile
        {
            UserId = Guid.NewGuid(),
            FileName = "Vəsiqə.pdf",
            Extension = "pdf",
            MediaType = "application/pdf",
            IsSensitive = sensitive,
            EditLocked = locked,
            LayoutPaper = locked ? "A4" : null,
            Original = Object("original"),
        };

        file.BeginConversion(Actor, Now);
        file.Complete(file.Original, 2, Actor, Now);

        return file;
    }

    private static FileObject Object(string key) => new(Guid.NewGuid(), key, "abc", 10, 8, "kek-1");

    [Fact]
    public void A_share_keeps_what_was_sent_and_reads_after_the_file_is_gone()
    {
        var file = ReadyFile(sensitive: true);
        var to = Guid.NewGuid();

        var share = Share.Send(file, to, "  bax  ", Now);

        (share.FileId, share.FromUserId, share.ToUserId).Should().Be((file.Id, file.UserId, to));
        (share.FileName, share.PageCount, share.IsSensitive, share.Message).Should().Be(("Vəsiqə.pdf", 2, true, "bax"));
        share.AvailabilityOf(file).Should().Be(ShareAvailability.Available);

        file.DeletedAt = Now;
        share.AvailabilityOf(file).Should().Be(ShareAvailability.Gone);
        share.AvailabilityOf(null).Should().Be(ShareAvailability.Gone);
    }

    [Fact]
    public void A_new_version_after_sending_is_not_what_was_sent()
    {
        var file = ReadyFile();
        var share = Share.Send(file, Guid.NewGuid(), null, Now);

        file.ApplyEdit(Object("v1"), "pdf", null, 5, Actor, Now);
        share.AvailabilityOf(file).Should().Be(ShareAvailability.Changed, "converting the new version");

        file.BeginConversion(Actor, Now);
        file.Complete(Object("print-v1"), 1, Actor, Now);
        share.AvailabilityOf(file).Should().Be(ShareAvailability.Changed);
    }

    [Fact]
    public void Not_to_yourself_and_not_before_the_file_is_ready()
    {
        var file = ReadyFile();
        var pending = new StoredFile { UserId = Guid.NewGuid(), FileName = "a.pdf", Original = Object("a") };

        FluentActions.Invoking(() => Share.Send(file, file.UserId, null, Now))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("shares.toSelf");
        FluentActions.Invoking(() => Share.Send(pending, Guid.NewGuid(), null, Now))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("files.notReadyNamed");
    }

    [Fact]
    public void A_note_is_one_plain_line_of_at_most_200_characters()
    {
        Share.CleanMessage("   ").Should().BeNull();
        Share.CleanMessage("bir\nikki").Should().Be("bir ikki");
        Share.CleanMessage("pul ‮gp.exe").Should().Be("pul  gp.exe", "a bidi override would disguise the note");
        Share.CleanMessage(new string('a', 200)).Should().HaveLength(200);

        FluentActions.Invoking(() => Share.CleanMessage(new string('a', 201)))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("shares.messageTooLong");
    }

    [Fact]
    public void The_recipients_copy_is_ready_and_keeps_a_scans_lock_and_a_documents_privacy()
    {
        var sent = ReadyFile(sensitive: true, locked: true);
        var id = Guid.NewGuid();
        var to = Guid.NewGuid();

        var copy = StoredFile.ReceiveShared(id, to, sent, Object("copy"), "pdf", Object("copy"), AutoDeleteRule.AfterPrint, Now);

        (copy.Id, copy.UserId, copy.Source, copy.Status, copy.PageCount).Should().Be((id, to, FileSource.Shared, FileStatus.Ready, 2));
        (copy.IsSensitive, copy.EditLocked, copy.RealSizePaper, copy.AutoDelete).Should().Be((true, true, "A4", AutoDeleteRule.AfterPrint));
        copy.LayoutProjectJson.Should().BeNull("a layout arrives as the PDF it draws");
    }

    [Fact]
    public void A_save_completes_only_after_it_began()
    {
        var share = Share.Send(ReadyFile(), Guid.NewGuid(), null, Now);
        var copy = Guid.NewGuid();

        FluentActions.Invoking(() => share.CompleteSave(Now)).Should().Throw<DomainException>();

        share.BeginSave(copy);
        share.CompleteSave(Now);

        (share.SavedFileId, share.SavedAt).Should().Be((copy, Now));
    }
}

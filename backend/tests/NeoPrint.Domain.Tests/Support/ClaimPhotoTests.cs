using FluentAssertions;

using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Support;

namespace NeoPrint.Domain.Tests.Support;

/// <summary>Pictures on a claim (T-438): a few, while it is open, gone with the account.</summary>
public sealed class ClaimPhotoTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);

    private static FileObject Stored(int n) => new(Guid.Empty, $"claim-photos/x/{n}", "sha", 10, 10, "kek");

    private static SupportClaim Claim()
        => SupportClaim.Open(ClaimChannel.App, Guid.NewGuid(), "482913", Guid.NewGuid(), Guid.NewGuid(), "Kağız sıxışdı", null, Now);

    [Fact]
    public void Up_to_three_pictures_are_kept_and_a_fourth_is_refused()
    {
        var claim = Claim();
        for (var n = 0; n < SupportClaim.MaxPhotos; n++)
        {
            claim.AddPhoto(Stored(n), "image/jpeg", Now);
        }

        claim.Photos.Should().HaveCount(3);
        FluentActions.Invoking(() => claim.AddPhoto(Stored(9), "image/jpeg", Now)).Should().Throw<DomainException>();
        claim.Photos.Should().HaveCount(3);
    }

    [Fact]
    public void A_closed_claim_takes_no_picture()
    {
        var claim = Claim();
        claim.Close();

        FluentActions.Invoking(() => claim.AddPhoto(Stored(0), "image/png", Now)).Should().Throw<DomainException>();
    }

    [Fact]
    public void Erased_pictures_are_handed_back_for_their_objects_to_go()
    {
        var claim = Claim();
        claim.AddPhoto(Stored(0), "image/png", Now);
        claim.AddPhoto(Stored(1), "image/png", Now);

        var removed = claim.RemovePhotos();

        removed.Should().HaveCount(2);
        claim.Photos.Should().BeEmpty();
    }
}

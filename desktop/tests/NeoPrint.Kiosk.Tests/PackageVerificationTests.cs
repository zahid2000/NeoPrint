using System.Security.Cryptography;

using NeoPrint.Crypto.Releases;
using NeoPrint.Kiosk.Agent.Updates;
using NeoPrint.Kiosk.Core.Updates;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The agent on a setup stick as the checker of what a provisioning script
/// downloaded from the backend (T-327): yes only for a package signed with its
/// own keys, for this component and platform, that is exactly the file its
/// manifest names.
/// </summary>
public sealed class PackageVerificationTests : IDisposable
{
    private readonly ECDsa _team = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _dir = Directory.CreateTempSubdirectory("neoprint-verify-").FullName;

    public void Dispose()
    {
        _team.Dispose();
        _stranger.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private ReleasePackageCheck Stick() => new(new ReleaseKeySet("production", [ReleasePublicKey.FromKey(_team)]));

    private (string Package, string Manifest, string Signature) Release(ECDsa signer, ReleaseComponent component = ReleaseComponent.Agent, string keySet = "production")
    {
        var bytes = new byte[2048];
        new Random(327).NextBytes(bytes);
        var name = $"neoprint-kiosk-agent-0.9.0-linux-x64-{keySet}.tar.gz";
        var manifest = new ReleaseManifest(component, "linux-x64", keySet, ReleaseVersion.Parse("0.9.0"), ReleaseVersion.Parse("0.8.0"),
            name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "c0ffee1", DateTimeOffset.UtcNow);
        var dir = Directory.CreateDirectory(Path.Combine(_dir, Guid.NewGuid().ToString("N"))).FullName;
        var package = Path.Combine(dir, name);
        File.WriteAllBytes(package, bytes);
        File.WriteAllBytes(package + ".manifest.json", manifest.ToBytes());
        File.WriteAllBytes(package + ".manifest.json.sig", ReleaseSignature.Sign(signer, manifest.ToBytes()).ToBytes());
        return (package, package + ".manifest.json", package + ".manifest.json.sig");
    }

    private async Task<(int Code, string Output, string Error)> VerifyAsync((string Package, string Manifest, string Signature) release, ReleaseComponent component = ReleaseComponent.Agent)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await PackageVerification.RunAsync(Stick(), release.Package, release.Manifest, release.Signature, component, "linux-x64", "0.9.0", output, error, CancellationToken.None);
        return (code, output.ToString().Trim(), error.ToString());
    }

    [Fact]
    public async Task A_package_the_team_signed_passes_and_says_its_version()
    {
        var (code, output, _) = await VerifyAsync(Release(_team));

        code.Should().Be(0);
        output.Should().Be("0.9.0");
    }

    [Fact]
    public async Task A_package_signed_with_another_key_is_refused()
    {
        var (code, _, error) = await VerifyAsync(Release(_stranger));

        code.Should().Be(2);
        error.Should().Contain("UnknownKey");
    }

    [Fact]
    public async Task A_package_changed_after_signing_is_refused()
    {
        var release = Release(_team);
        await using (var package = File.OpenWrite(release.Package))
        {
            package.WriteByte(0);
        }

        (await VerifyAsync(release)).Code.Should().Be(2);
    }

    [Fact]
    public async Task A_package_of_another_component_or_key_set_is_refused()
    {
        (await VerifyAsync(Release(_team), ReleaseComponent.App)).Code.Should().Be(2, "an agent package is not the app");
        (await VerifyAsync(Release(_team, keySet: "staging"))).Code.Should().Be(2, "a staging package is not for a production stick");
    }

    [Fact]
    public async Task A_missing_file_or_a_wrong_command_is_said_plainly()
    {
        var release = Release(_team);
        File.Delete(release.Signature);
        (await VerifyAsync(release)).Code.Should().Be(2);

        using var output = new StringWriter();
        using var error = new StringWriter();
        (await PackageVerification.RunAsync(["verify-package", "a"], "0.9.0", output, error, CancellationToken.None)).Should().Be(64);
        error.ToString().Should().Contain("Usage");
    }
}

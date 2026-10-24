namespace NeoPrint.Kiosk.Core.Updates;

/// <summary>
/// The staging release keys: carried only by builds made with
/// <c>-p:ReleaseKeySet=staging</c> - the test server's and the development
/// stack's kiosks (docs/RELEASE_SIGNING.md, T-316). A production build never
/// compiles this file.
/// </summary>
/// <remarks>
/// The private halves live outside the repository, on the owner's machine
/// (<c>~/.neoprint/release-keys/staging</c>). A leak lets someone sign packages
/// that test and development kiosks install - never a pilot kiosk.
/// </remarks>
internal static class StagingReleaseKeys
{
    public const string Name = "staging";

    public const string Current = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEuZAWosXR8hg3rAORt0CNfPny1QPv
        ZobE1Um/l40z8glN/1NSXat1SoOQQ15amPuAMo4DjJR77zFJVkOj36kI/w==
        -----END PUBLIC KEY-----
        """;

    public const string Next = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAESirDBfU9iroTUmm5Yi1oiuzOIcfM
        r2zgOYZJBykNpdbLvjaRIsA7Pt3QdK0x/G5MgtNBee9furQl7m1MhE6R0Q==
        -----END PUBLIC KEY-----
        """;
}

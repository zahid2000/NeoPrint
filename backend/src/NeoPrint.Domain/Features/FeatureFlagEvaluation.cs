using System.Security.Cryptography;
using System.Text;

namespace NeoPrint.Domain.Features;

/// <summary>
/// The order in which a feature flag is evaluated (plan section 2.16).
/// </summary>
/// <remarks>
/// A domain rule, not an application concern: it depends on nothing but the
/// flag itself, so it lives here and is unit tested without any storage. The
/// order is stated in exactly one place:
/// <list type="number">
/// <item>a per-kiosk override wins outright;</item>
/// <item>an allowlisted user always gets the feature;</item>
/// <item>otherwise the default, narrowed by the rollout percentage.</item>
/// </list>
/// </remarks>
public static class FeatureFlagEvaluation
{
    public static bool Evaluate(FeatureFlag flag, Guid? kioskId, Guid? userId)
    {
        ArgumentNullException.ThrowIfNull(flag);

        // A kiosk override is the pilot switch: it decides on its own.
        if (kioskId is not null &&
            flag.KioskOverrides.TryGetValue(kioskId.Value.ToString(), out var overridden))
        {
            return overridden;
        }

        if (userId is not null && flag.UserAllowlist.Contains(userId.Value))
        {
            return true;
        }

        if (!flag.DefaultEnabled)
        {
            return false;
        }

        if (flag.RolloutPercent >= 100)
        {
            return true;
        }

        if (flag.RolloutPercent <= 0)
        {
            return false;
        }

        // A percentage rollout is defined over mobile users (plan section 2.16:
        // hash(userId + key)). There is no user to bucket on an anonymous kiosk
        // session or before sign-in, so the percentage simply does not apply
        // there and the default governs - a kiosk must not lose a feature just
        // because nobody is signed in.
        return userId is null || Bucket(userId.Value, flag.Key) < flag.RolloutPercent;
    }

    /// <summary>
    /// Puts a user in a stable bucket from 0 to 99 for one flag.
    /// </summary>
    /// <remarks>
    /// Hashing the user and the key together means the same person always gets
    /// the same answer for a given flag - the feature does not flicker between
    /// requests - while two flags at the same percentage do not pick the same
    /// people.
    /// </remarks>
    public static int Bucket(Guid userId, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:N}:{key}"));

        return (int)(BitConverter.ToUInt32(hash, 0) % 100);
    }
}

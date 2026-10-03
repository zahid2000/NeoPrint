using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.UserQr;

/// <summary>HMAC-SHA256 with the server's QR key (<c>NEOPRINT_QR_HMAC_KEY</c>, plan section 2.9).</summary>
public interface IUserQrSigner
{
    byte[] Sign(string payload);
}

/// <summary>A QR code as a PNG image (plan 2.0: the backend draws it with QRCoder).</summary>
public interface IQrImages
{
    byte[] Png(string content);
}

/// <summary>How a scanned personal QR reads.</summary>
public enum UserQrReading
{
    Valid,

    /// <summary>Not ours, or tampered with.</summary>
    Invalid,

    /// <summary>Ours, but older than <c>UserQr.RotationSeconds</c>: the app shows a newer one.</summary>
    Expired,
}

/// <summary>
/// The personal QR: <c>NPU:{userId}:{timestamp}:{hmac}</c> (plan section
/// 2.9). The user id is 32 hex digits, the timestamp Unix seconds when it was
/// issued, the HMAC base64url over <c>NPU:{userId}:{timestamp}</c>. It is good
/// for <c>UserQr.RotationSeconds</c> after it was issued; the app fetches a
/// new one before that.
/// </summary>
public sealed class UserQrCodes
{
    public const string Prefix = "NPU:";

    private readonly IUserQrSigner _signer;

    public UserQrCodes(IUserQrSigner signer) => _signer = signer;

    public string Issue(Guid userId, DateTime now)
    {
        var payload = Payload(userId, new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds());
        return $"{payload}:{Base64Url(_signer.Sign(payload))}";
    }

    public (UserQrReading Reading, Guid UserId) Read(string? code, DateTime now, int rotationSeconds)
    {
        var parts = (code ?? string.Empty).Trim().Split(':');

        if (parts is not ["NPU", var id, var stamp, var mac]
            || id.Length != 32
            || !Guid.TryParseExact(id, "N", out var userId)
            || !long.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out var issued))
        {
            return (UserQrReading.Invalid, Guid.Empty);
        }

        var expected = Encoding.ASCII.GetBytes(Base64Url(_signer.Sign(Payload(userId, issued))));

        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(mac)))
        {
            return (UserQrReading.Invalid, Guid.Empty);
        }

        var age = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds() - issued;

        // Issued in the future means a clock we did not set: not ours.
        return age < 0
            ? (UserQrReading.Invalid, Guid.Empty)
            : age > rotationSeconds
                ? (UserQrReading.Expired, userId)
                : (UserQrReading.Valid, userId);
    }

    private static string Payload(Guid userId, long issued)
        => string.Create(CultureInfo.InvariantCulture, $"{Prefix}{userId:N}:{issued}");

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The app's QR now (<c>GET users/me/qr</c>).</summary>
public sealed record GetMyUserQrQuery(Guid UserId) : IRequest<MyUserQr>;

/// <summary>The same as a picture (<c>GET users/me/qr.png</c>): the app has no QR drawing of its own.</summary>
public sealed record GetMyUserQrImageQuery(Guid UserId) : IRequest<MyUserQrImage>;

/// <param name="ExpiresAt">Show it until then, then fetch a new one.</param>
public sealed record MyUserQr(string Code, DateTime ExpiresAt, int RotationSeconds);

public sealed record MyUserQrImage(byte[] Png, DateTime ExpiresAt, int RotationSeconds);

public sealed class MyUserQrHandlers :
    IRequestHandler<GetMyUserQrQuery, MyUserQr>,
    IRequestHandler<GetMyUserQrImageQuery, MyUserQrImage>
{
    private readonly UserQrCodes _codes;
    private readonly IQrImages _images;
    private readonly ISettingsReader _settings;
    private readonly IClock _clock;

    public MyUserQrHandlers(UserQrCodes codes, IQrImages images, ISettingsReader settings, IClock clock)
    {
        _codes = codes;
        _images = images;
        _settings = settings;
        _clock = clock;
    }

    public async Task<MyUserQr> Handle(GetMyUserQrQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rotation = await UserQrRules.RotationSecondsAsync(_settings, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        return new MyUserQr(_codes.Issue(request.UserId, now), now.AddSeconds(rotation), rotation);
    }

    public async Task<MyUserQrImage> Handle(GetMyUserQrImageQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var qr = await Handle(new GetMyUserQrQuery(request.UserId), cancellationToken).ConfigureAwait(false);
        return new MyUserQrImage(_images.Png(qr.Code), qr.ExpiresAt, qr.RotationSeconds);
    }
}

internal static class UserQrRules
{
    /// <summary><c>UserQr.RotationSeconds</c>, kept between 15 seconds and 10 minutes.</summary>
    public static async Task<int> RotationSecondsAsync(ISettingsReader settings, CancellationToken cancellationToken)
        => Math.Clamp(
            await settings.GetIntAsync(SettingKeys.UserQrRotationSeconds, 60, cancellationToken).ConfigureAwait(false), 15, 600);
}

using System.Security.Cryptography;

using MediatR;

using NeoPrint.Application.Abstractions;

namespace NeoPrint.Application.UserQr;

/// <summary>
/// "Çap kodu" (T-345): six digits that stand for the personal QR when the
/// kiosk's scanner cannot read the phone - typed on the kiosk's keypad.
/// </summary>
/// <remarks>
/// Short-lived and single-use, unlike the QR's signature it is a secret held
/// by the server: the code maps to the user in Redis for
/// <see cref="Lifetime"/>, and the kiosk that types it takes it out. A kiosk
/// may get only <see cref="MaxFailures"/> wrong in <see cref="FailureWindow"/>,
/// so the million codes cannot be walked through.
/// </remarks>
public interface IUserPrintCodeStore
{
    /// <summary>The user's code while it lasts, or a new one; with when it stops being good.</summary>
    Task<(string Code, DateTime ExpiresAt)> IssueAsync(Guid userId, TimeSpan lifetime, CancellationToken cancellationToken = default);

    /// <summary>Whose code it is, taking it out so it is not good twice; null when it is no one's.</summary>
    Task<Guid?> TakeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Counts one wrong code at a kiosk; how many it has had in the window, this one included.</summary>
    Task<long> CountFailureAsync(Guid kioskId, TimeSpan window, CancellationToken cancellationToken = default);

    /// <summary>How many wrong codes the kiosk has had in the window.</summary>
    Task<long> FailuresAsync(Guid kioskId, CancellationToken cancellationToken = default);
}

public static class UserPrintCodes
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    public const int MaxFailures = 10;
    public const int Length = 6;

    /// <summary>Six digits and nothing else - the keypad's input, spaces taken out.</summary>
    public static string? Normalize(string? typed)
    {
        if (typed is null)
        {
            return null;
        }

        var digits = new string([.. typed.Where(c => !char.IsWhiteSpace(c) && c != '-')]);
        return digits.Length == Length && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    /// <summary>A new code from the system's random source, leading zeros kept.</summary>
    public static string NewCode()
        => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The app's "Çap kodu" now (<c>GET users/me/print-code</c>).</summary>
public sealed record GetMyPrintCodeQuery(Guid UserId) : IRequest<MyPrintCode>;

/// <param name="ExpiresAt">Show it until then; it also stops once a kiosk used it.</param>
public sealed record MyPrintCode(string Code, DateTime ExpiresAt);

public sealed class MyPrintCodeHandler : IRequestHandler<GetMyPrintCodeQuery, MyPrintCode>
{
    private readonly IUserPrintCodeStore _codes;

    public MyPrintCodeHandler(IUserPrintCodeStore codes) => _codes = codes;

    public async Task<MyPrintCode> Handle(GetMyPrintCodeQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (code, expiresAt) = await _codes
            .IssueAsync(request.UserId, UserPrintCodes.Lifetime, cancellationToken)
            .ConfigureAwait(false);

        return new MyPrintCode(code, expiresAt);
    }
}

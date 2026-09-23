using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

using NeoPrint.Application.Abstractions;

namespace NeoPrint.Infrastructure.Auth;

/// <summary>
/// Issues the JSON web tokens for users, administrators, kiosks and devices
/// awaiting activation (plan section 2.8).
/// </summary>
public sealed class JwtTokenService : ITokenService
{
    /// <summary>Issuer and audience; the same service serves all four clients.</summary>
    public const string Issuer = "neoprint";
    public const string Audience = "neoprint";

    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan KioskTokenLifetime = TimeSpan.FromHours(1);

    private readonly IConfiguration _configuration;
    private readonly IClock _clock;

    public JwtTokenService(IConfiguration configuration, IClock clock)
    {
        _configuration = configuration;
        _clock = clock;
    }

    /// <inheritdoc />
    public IssuedToken CreateUserToken(Guid userId, string email) => Create(
        TokenTypes.User,
        AccessTokenLifetime,
        [
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, email),
        ]);

    /// <inheritdoc />
    public IssuedToken CreateAdminToken(
        Guid adminUserId,
        string email,
        IReadOnlyCollection<string> permissions,
        bool allKiosks,
        IReadOnlyCollection<Guid> kioskIds,
        bool mustChangePassword)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, adminUserId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(NeoPrintClaims.AdminUserId, adminUserId.ToString()),
        };

        // Permissions travel in the token so that most requests need no lookup.
        // The 15 minute lifetime bounds how long a revoked permission survives;
        // the permission cache is also cleared on change (plan section 8A.2).
        claims.AddRange(permissions.Select(permission => new Claim(NeoPrintClaims.Permission, permission)));

        if (allKiosks)
        {
            claims.Add(new Claim(NeoPrintClaims.AllKiosks, "true"));
        }
        else
        {
            claims.AddRange(kioskIds.Select(id => new Claim(NeoPrintClaims.ScopedKiosk, id.ToString())));
        }

        if (mustChangePassword)
        {
            claims.Add(new Claim(NeoPrintClaims.MustChangePassword, "true"));
        }

        return Create(TokenTypes.Admin, AccessTokenLifetime, claims);
    }

    /// <inheritdoc />
    public IssuedToken CreateKioskToken(Guid deviceId, Guid kioskId) => Create(
        TokenTypes.Kiosk,
        KioskTokenLifetime,
        [
            new Claim(JwtRegisteredClaimNames.Sub, deviceId.ToString()),
            new Claim(NeoPrintClaims.DeviceId, deviceId.ToString()),
            new Claim(NeoPrintClaims.KioskId, kioskId.ToString()),
        ]);

    /// <inheritdoc />
    public IssuedToken CreateProvisioningToken(Guid deviceId) => Create(
        TokenTypes.Provisioning,
        KioskTokenLifetime,
        [
            new Claim(JwtRegisteredClaimNames.Sub, deviceId.ToString()),
            new Claim(NeoPrintClaims.DeviceId, deviceId.ToString()),
        ]);

    /// <inheritdoc />
    public string CreateRefreshToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    /// <summary>
    /// The signing key, read from OpenBao through configuration.
    /// </summary>
    /// <remarks>
    /// Read on each call rather than cached, so a rotated key takes effect
    /// within the configuration refresh interval without a restart.
    /// </remarks>
    public static SymmetricSecurityKey ReadSigningKey(IConfiguration configuration)
    {
        var value = configuration["NEOPRINT_JWT_SIGNING_KEY"];

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "NEOPRINT_JWT_SIGNING_KEY is not configured; tokens cannot be issued or validated (docs/OPENBAO.md).");
        }

        // The seeded value is base64; a key written by hand may not be.
        byte[] material;
        try
        {
            material = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            material = Encoding.UTF8.GetBytes(value);
        }

        if (material.Length < 32)
        {
            throw new InvalidOperationException(
                "NEOPRINT_JWT_SIGNING_KEY is shorter than 256 bits, which HMAC-SHA256 requires.");
        }

        return new SymmetricSecurityKey(material);
    }

    private IssuedToken Create(string tokenType, TimeSpan lifetime, IEnumerable<Claim> claims)
    {
        var now = _clock.UtcNow;
        var expiresAt = now + lifetime;

        var allClaims = new List<Claim>(claims)
        {
            new(NeoPrintClaims.TokenType, tokenType),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var credentials = new SigningCredentials(
            ReadSigningKey(_configuration), SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: allClaims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: credentials);

        return new IssuedToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}

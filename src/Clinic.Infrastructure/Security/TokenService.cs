using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Clinic.Application.Abstractions;
using Clinic.Application.Common;
using Clinic.Shared.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Clinic.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "clinic-api";
    public string Audience { get; set; } = "clinic-staff";

    /// <summary>HMAC-SHA256 key, at least 32 bytes. Set from a secret store in production.</summary>
    public string SigningKey { get; set; } = "";

    public SymmetricSecurityKey GetKey()
    {
        var bytes = Encoding.UTF8.GetBytes(SigningKey);
        if (bytes.Length < 32)
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");
        return new SymmetricSecurityKey(bytes);
    }
}

public sealed class TokenService(IOptions<JwtOptions> jwt, IOptions<AuthOptions> auth, TimeProvider clock) : ITokenService
{
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SigningCredentials _credentials = new(jwt.Value.GetKey(), SecurityAlgorithms.HmacSha256);

    public (string Token, DateTime ExpiresAt) CreateAccessToken(TokenSubject s)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(auth.Value.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(ClinicClaimTypes.UserId, s.UserId.ToString()),
            new(ClinicClaimTypes.UserName, s.UserName),
            new(ClinicClaimTypes.FullName, s.FullName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        if (s.TenantId is { } tid) claims.Add(new(ClinicClaimTypes.TenantId, tid.ToString()));
        if (s.TenantCode is { } tcode) claims.Add(new(ClinicClaimTypes.TenantCode, tcode));
        if (s.DeviceId is { } did) claims.Add(new(ClinicClaimTypes.DeviceId, did.ToString()));
        if (s.IsSuperAdmin) claims.Add(new(ClinicClaimTypes.SuperAdmin, "true"));
        claims.AddRange(s.BranchIds.Select(b => new Claim(ClinicClaimTypes.Branch, b.ToString())));
        claims.AddRange((s.Roles ?? []).Select(r => new Claim(ClinicClaimTypes.Role, r)));
        claims.AddRange(s.Permissions.Select(p => new Claim(ClinicClaimTypes.Permission, $"{p.Name}:{p.Scope}")));

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Value.Issuer,
            Audience = jwt.Value.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = _credentials,
        });

        return (token, expires);
    }

    public string CreateRefreshToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));

    public string HashRefreshToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

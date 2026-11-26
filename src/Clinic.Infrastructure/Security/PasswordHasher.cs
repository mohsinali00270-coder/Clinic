using Microsoft.AspNetCore.Identity;

namespace Clinic.Infrastructure.Security;

/// <summary>FR-SEC-04: ASP.NET Core Identity's PBKDF2 (HMAC-SHA512, 100k iterations, v3 format).</summary>
public sealed class PasswordHasher : Application.Abstractions.IPasswordHasher
{
    private static readonly object Subject = new();
    private readonly PasswordHasher<object> _inner = new();

    public string Hash(string password) => _inner.HashPassword(Subject, password);

    public bool Verify(string hash, string password)
    {
        try
        {
            return _inner.VerifyHashedPassword(Subject, hash, password) != PasswordVerificationResult.Failed;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

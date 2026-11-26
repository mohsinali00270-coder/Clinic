using System.Security.Cryptography;
using System.Text;

namespace Clinic.App.Services;

/// <summary>
/// FR-AUTH-05: unlock without the network using a PIN set after a successful online login.
/// Only a salted PBKDF2 hash is stored, in platform secure storage. Five wrong PINs remove it,
/// so the next sign-in must use the password online.
/// </summary>
public sealed class OfflinePinService
{
    public const int MaxAttempts = 5;
    private const int Iterations = 100_000;

    private static string KeyName(Guid userId) => $"pin_{userId:N}";
    private static string AttemptsName(Guid userId) => $"pin_attempts_{userId:N}";

    public bool HasPin(Guid userId) => Preferences.Default.ContainsKey(AttemptsName(userId));

    public static bool IsValidFormat(string? pin) => pin is { Length: >= 4 and <= 8 } && pin.All(char.IsAsciiDigit);

    public async Task SetPinAsync(Guid userId, string pin)
    {
        if (!IsValidFormat(pin)) throw new ArgumentException("PIN must be 4 to 8 digits.", nameof(pin));

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Derive(pin, salt);
        await SecureStorage.Default.SetAsync(KeyName(userId), Convert.ToBase64String([.. salt, .. hash]));
        Preferences.Default.Set(AttemptsName(userId), 0);
    }

    /// <summary>Returns the remaining attempts after a wrong PIN, or null when the PIN was correct.</summary>
    public async Task<int?> VerifyAsync(Guid userId, string pin)
    {
        var stored = await SecureStorage.Default.GetAsync(KeyName(userId));
        if (string.IsNullOrEmpty(stored)) return 0;

        var bytes = Convert.FromBase64String(stored);
        var salt = bytes[..16];
        var expected = bytes[16..];

        if (CryptographicOperations.FixedTimeEquals(Derive(pin, salt), expected))
        {
            Preferences.Default.Set(AttemptsName(userId), 0);
            return null;
        }

        var attempts = Preferences.Default.Get(AttemptsName(userId), 0) + 1;
        if (attempts >= MaxAttempts)
        {
            Clear(userId);
            return 0;
        }
        Preferences.Default.Set(AttemptsName(userId), attempts);
        return MaxAttempts - attempts;
    }

    public void Clear(Guid userId)
    {
        SecureStorage.Default.Remove(KeyName(userId));
        Preferences.Default.Remove(AttemptsName(userId));
    }

    private static byte[] Derive(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 32);
}

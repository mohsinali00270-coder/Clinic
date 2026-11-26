namespace Clinic.Application.Common;

internal static class Normalize
{
    public static string Key(string value) => value.Trim().ToUpperInvariant();

    public static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

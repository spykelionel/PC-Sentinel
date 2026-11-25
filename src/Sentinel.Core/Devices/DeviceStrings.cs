namespace Sentinel.Core.Devices;

/// <summary>
/// Device-supplied strings (labels, models, serials, IDs) are untrusted input.
/// </summary>
public static class DeviceStrings
{
    public const int MaxLength = 256;

    /// <summary>
    /// Removes control characters, trims, and caps the length. Returns null for null or empty results.
    /// </summary>
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var cleaned = string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        }).Trim();

        if (cleaned.Length > MaxLength)
        {
            cleaned = cleaned[..MaxLength];
        }

        return cleaned.Length == 0 ? null : cleaned;
    }
}

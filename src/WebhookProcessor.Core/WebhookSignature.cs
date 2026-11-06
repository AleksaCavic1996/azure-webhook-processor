using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WebhookProcessor.Core;

/// <summary>
/// HMAC-SHA256 over "{timestamp}.{rawBody}". Signing the timestamp stops an attacker from
/// replaying an old captured request with a fresh timestamp.
/// </summary>
public static class WebhookSignature
{
    private const string Prefix = "sha256=";

    public static string Compute(string secret, string timestamp, ReadOnlySpan<byte> body)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret));
        hmac.AppendData(Encoding.UTF8.GetBytes(timestamp + "."));
        hmac.AppendData(body);
        return Prefix + Convert.ToHexStringLower(hmac.GetHashAndReset());
    }

    public static bool IsValid(
        string secret,
        string? timestamp,
        string? signature,
        ReadOnlySpan<byte> body,
        DateTimeOffset now,
        TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(signature)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds)
            || Math.Abs(now.ToUnixTimeSeconds() - unixSeconds) > tolerance.TotalSeconds)
        {
            return false;
        }

        // Constant-time comparison, so response timing reveals nothing about the expected value.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Compute(secret, timestamp!, body)),
            Encoding.UTF8.GetBytes(signature));
    }
}

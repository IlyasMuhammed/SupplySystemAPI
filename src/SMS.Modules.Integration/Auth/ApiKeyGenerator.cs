using System.Security.Cryptography;
using System.Text;

namespace SMS.Modules.Integration.Auth;

/// <summary>
/// How API keys look, are made and are compared (plan QBI-06).
/// <para>
/// A key is <c>sqb_</c> + 40 URL-safe characters (30 random bytes, 240 bits). Its first
/// <see cref="ApiKeyDefaults.PrefixLength"/> characters are stored in clear as the lookup prefix —
/// shown on screen so a person can tell keys apart, and used to find the row before any hashing — and
/// the whole key is stored only as a SHA-256 hash. A plain hash is right here, unlike for passwords:
/// the input is 240 bits of randomness, so there is nothing for a slow hash to protect against.
/// </para>
/// </summary>
internal static class ApiKeyGenerator
{
    private const int RandomBytes = 30;
    private const int BodyLength  = 40;

    public static int KeyLength => ApiKeyDefaults.KeyPrefixMarker.Length + BodyLength;

    public static string NewKey() =>
        ApiKeyDefaults.KeyPrefixMarker + Convert.ToBase64String(RandomNumberGenerator.GetBytes(RandomBytes))
            .Replace('+', '-').Replace('/', '_');

    public static string PrefixOf(string key) => key[..ApiKeyDefaults.PrefixLength];

    /// <summary>SHA-256 of the whole key, lower-case hex.</summary>
    public static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    /// <summary>Shape only — the right marker, length and alphabet. Says nothing about whether it exists.</summary>
    public static bool IsWellFormed(string? key)
    {
        if (key is null || key.Length != KeyLength) return false;
        if (!key.StartsWith(ApiKeyDefaults.KeyPrefixMarker, StringComparison.Ordinal)) return false;

        for (var i = ApiKeyDefaults.KeyPrefixMarker.Length; i < key.Length; i++)
        {
            var c = key[i];
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
        }

        return true;
    }

    /// <summary>
    /// Compares a presented key with a stored hash in constant time, so how long a refusal takes says
    /// nothing about how much of the hash matched.
    /// </summary>
    public static bool Matches(string presentedKey, string storedHash)
    {
        var presented = Encoding.ASCII.GetBytes(Hash(presentedKey));
        var stored    = Encoding.ASCII.GetBytes(storedHash.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(presented, stored);
    }
}

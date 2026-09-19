using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace SMS.Shared.Common;

/// <summary>
/// AES encryption for stored secrets, keyed from <see cref="AppSettings.AesEncryptionKey"/>.
/// <para>
/// <b>New values are written AES-GCM; old values still decrypt as AES-CBC.</b> The original
/// implementation used CBC with no message authentication, which means a ciphertext can be altered
/// in the database without the alteration being detectable on read — the decrypt simply returns
/// different bytes. GCM authenticates, so tampering fails loudly instead.
/// </para>
/// <para>
/// The format is versioned rather than switched, so this needed no data migration: existing
/// supplier bank details keep decrypting exactly as before, and everything written from now on —
/// theirs included — is authenticated. Only rewriting a value upgrades it, which is the honest
/// trade for not touching live rows.
/// </para>
/// </summary>
public sealed class AesEncryptionService : IEncryptionService
{
    /// <summary>Marks the authenticated format. Anything without it is read as legacy CBC.</summary>
    private const string GcmPrefix = "v2:";

    private const int NonceBytes = 12;   // 96 bits, the size AES-GCM is specified for
    private const int TagBytes   = 16;   // 128-bit authentication tag

    private readonly byte[] _key;

    public AesEncryptionService(IOptions<AppSettings> settings)
    {
        // A fixed 32-byte key derived from the configured string. Kept exactly as it was, because
        // changing the derivation would make every existing ciphertext undecryptable.
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(settings.Value.AesEncryptionKey));
    }

    public string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce      = RandomNumberGenerator.GetBytes(NonceBytes);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher     = new byte[plainBytes.Length];
        var tag        = new byte[TagBytes];

        using (var aes = new AesGcm(_key, TagBytes))
            aes.Encrypt(nonce, plainBytes, cipher, tag);

        // nonce | tag | ciphertext — all fixed-size but the last, so parsing needs no lengths.
        var combined = new byte[NonceBytes + TagBytes + cipher.Length];
        nonce.CopyTo(combined, 0);
        tag.CopyTo(combined, NonceBytes);
        cipher.CopyTo(combined, NonceBytes + TagBytes);

        return GcmPrefix + Convert.ToBase64String(combined);
    }

    public string Decrypt(string ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        return ciphertext.StartsWith(GcmPrefix, StringComparison.Ordinal)
            ? DecryptGcm(ciphertext[GcmPrefix.Length..])
            : DecryptLegacyCbc(ciphertext);
    }

    private string DecryptGcm(string payload)
    {
        var combined = Convert.FromBase64String(payload);

        if (combined.Length < NonceBytes + TagBytes)
            throw new CryptographicException("The ciphertext is too short to be valid.");

        var nonce  = combined.AsSpan(0, NonceBytes);
        var tag    = combined.AsSpan(NonceBytes, TagBytes);
        var cipher = combined.AsSpan(NonceBytes + TagBytes);
        var plain  = new byte[cipher.Length];

        // Throws CryptographicException if the tag does not match — which is the point: a value
        // altered in the database fails here rather than returning plausible rubbish.
        using (var aes = new AesGcm(_key, TagBytes))
            aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>
    /// The original format: a 16-byte IV prepended to CBC ciphertext, base64-encoded. Kept only so
    /// values written before the upgrade still read.
    /// </summary>
    private string DecryptLegacyCbc(string ciphertext)
    {
        var combined = Convert.FromBase64String(ciphertext);

        using var aes = Aes.Create();
        aes.Key = _key;

        var iv         = new byte[aes.BlockSize / 8];
        var cipherBytes = new byte[combined.Length - iv.Length];
        Array.Copy(combined, 0, iv, 0, iv.Length);
        Array.Copy(combined, iv.Length, cipherBytes, 0, cipherBytes.Length);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        return Encoding.UTF8.GetString(
            decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length));
    }
}

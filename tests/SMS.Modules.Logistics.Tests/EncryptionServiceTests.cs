using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

/// <summary>
/// The shared encryption service (F29), and in particular the guarantee nothing else proves:
/// <b>values written in the original CBC format still decrypt.</b>
/// <para>
/// This matters because the format was versioned rather than switched. Supplier bank details
/// encrypted before the upgrade are live data, and there is no migration — they are only rewritten
/// when somebody edits them. If legacy decryption ever broke, the symptom would be existing
/// records failing to open, in a module far from here, with nothing pointing back at the cause.
/// </para>
/// <para>
/// Lives in this test project because it is the one that already exercises the service and there
/// is no <c>SMS.Shared.Tests</c>. It tests <c>SMS.Shared</c>, not logistics.
/// </para>
/// </summary>
public class EncryptionServiceTests
{
    private const string Key = "a-test-encryption-key-for-shared-aes";

    private static AesEncryptionService Service(string key = Key) => TestEncryption.New(key);

    /// <summary>
    /// The original implementation, reproduced exactly: SHA-256 of the configured string as the
    /// key, a random 16-byte IV prepended to CBC ciphertext, the whole thing base64. This is what
    /// rows written before the upgrade actually look like.
    /// </summary>
    private static string LegacyCbcEncrypt(string plaintext, string key = Key)
    {
        var derived = SHA256.HashData(Encoding.UTF8.GetBytes(key));

        using var aes = Aes.Create();
        aes.Key = derived;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher     = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var combined = new byte[aes.IV.Length + cipher.Length];
        aes.IV.CopyTo(combined, 0);
        cipher.CopyTo(combined, aes.IV.Length);

        return Convert.ToBase64String(combined);
    }

    // ── Backwards compatibility ───────────────────────────────────────────────

    [Fact]
    public void A_value_written_in_the_old_format_still_decrypts()
    {
        // The whole reason the format is versioned instead of switched.
        var legacy = LegacyCbcEncrypt("Account 0123456789, sort 40-11-22");

        Service().Decrypt(legacy).Should().Be("Account 0123456789, sort 40-11-22");
    }

    [Fact]
    public void Old_ciphertext_is_recognised_by_the_absence_of_a_version_marker()
    {
        // Nothing clever: a legacy value is simply base64 with no prefix, and the new one is
        // prefixed. That is what makes both readable without a flag column or a migration.
        var legacy = LegacyCbcEncrypt("secret");
        var current = Service().Encrypt("secret");

        legacy.Should().NotStartWith("v2:");
        current.Should().StartWith("v2:");
    }

    [Fact]
    public void Rewriting_an_old_value_upgrades_it()
    {
        // The only upgrade path there is, and it is deliberate: editing a record re-encrypts it,
        // and untouched records stay readable in place.
        var service = Service();
        var legacy  = LegacyCbcEncrypt("rotate me");

        var upgraded = service.Encrypt(service.Decrypt(legacy));

        upgraded.Should().StartWith("v2:");
        service.Decrypt(upgraded).Should().Be("rotate me");
    }

    // ── The current format ────────────────────────────────────────────────────

    [Theory]
    [InlineData("simple")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a-very-long-carrier-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("unicode: café · 日本語 · emoji 📦")]
    [InlineData("{\"json\":\"value\",\"nested\":{\"x\":1}}")]
    public void Anything_that_goes_in_comes_back_out(string plaintext)
    {
        var service = Service();

        service.Decrypt(service.Encrypt(plaintext)).Should().Be(plaintext);
    }

    [Fact]
    public void The_same_value_encrypts_differently_every_time()
    {
        // A fresh nonce per write. Identical ciphertexts would let anybody with read access see
        // which accounts share a password without decrypting anything.
        var service = Service();

        service.Encrypt("same").Should().NotBe(service.Encrypt("same"));
    }

    // ── What the authenticated format is for ──────────────────────────────────

    [Fact]
    public void Tampering_with_a_stored_value_is_detected()
    {
        // The reason for moving to GCM. Under the old unauthenticated CBC this would have
        // returned different bytes rather than failing, and nothing downstream could tell.
        var service    = Service();
        var ciphertext = service.Encrypt("do not alter me");

        var raw = Convert.FromBase64String(ciphertext["v2:".Length..]);
        raw[^1] ^= 0xFF;                       // flip the last byte of the payload
        var tampered = "v2:" + Convert.ToBase64String(raw);

        var act = () => service.Decrypt(tampered);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void A_different_key_cannot_read_it()
    {
        var ciphertext = Service().Encrypt("secret");

        var act = () => Service("a-completely-different-key").Decrypt(ciphertext);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void A_truncated_value_fails_rather_than_returning_rubbish()
    {
        var service = Service();

        var act = () => service.Decrypt("v2:" + Convert.ToBase64String(new byte[4]));

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Nulls_are_refused_rather_than_encrypted()
    {
        var service = Service();

        ((Action)(() => service.Encrypt(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => service.Decrypt(null!))).Should().Throw<ArgumentNullException>();
    }
}

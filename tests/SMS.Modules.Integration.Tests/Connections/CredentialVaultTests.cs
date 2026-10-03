using FluentAssertions;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Tests.Connections;

public class CredentialVaultTests
{
    private const string Access  = "eyJhbGciOi.access-token-value.sig";
    private const string Refresh = "AB11-refresh-token-value-XYZ";

    private readonly CredentialVault _vault = new(TestEncryption.Instance);

    private static TokenGrant Grant() =>
        new(Access, Refresh, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), new DateTime(2027, 1, 9, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Store_encrypts_both_tokens_and_records_expiries()
    {
        var connection = new IntegrationConnection();

        _vault.Store(connection, Grant());

        connection.EncryptedAccessToken.Should().NotBeNullOrEmpty().And.NotContain(Access);
        connection.EncryptedRefreshToken.Should().NotBeNullOrEmpty().And.NotContain(Refresh);
        connection.EncryptedAccessToken.Should().StartWith("v2:", "new values are written in the authenticated format");
        connection.AccessTokenExpiresAt.Should().Be(Grant().AccessTokenExpiresAt);
        connection.RefreshTokenExpiresAt.Should().Be(Grant().RefreshTokenExpiresAt);
    }

    [Fact]
    public void Read_returns_the_plaintext_that_was_stored()
    {
        var connection = new IntegrationConnection();
        _vault.Store(connection, Grant());

        _vault.ReadAccessToken(connection).Should().Be(Access);
        _vault.ReadRefreshToken(connection).Should().Be(Refresh);
    }

    [Fact]
    public void Each_store_produces_a_different_ciphertext()
    {
        var a = new IntegrationConnection();
        var b = new IntegrationConnection();
        _vault.Store(a, Grant());
        _vault.Store(b, Grant());

        a.EncryptedRefreshToken.Should().NotBe(b.EncryptedRefreshToken, "a random nonce per encryption");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Reading_a_missing_token_is_connection_unavailable(string? ciphertext)
    {
        var connection = new IntegrationConnection { Status = ConnectionStatus.Live, EncryptedAccessToken = ciphertext };

        var act = () => _vault.ReadAccessToken(connection);

        act.Should().Throw<ConnectionUnavailableException>()
           .Which.Status.Should().Be(ConnectionStatus.NotConnected);
    }

    [Fact]
    public void A_tampered_ciphertext_fails_loudly_without_revealing_anything()
    {
        var connection = new IntegrationConnection { Status = ConnectionStatus.Live };
        _vault.Store(connection, Grant());

        // Flip one character of the authenticated payload.
        var cipher = connection.EncryptedRefreshToken!;
        var index  = cipher.Length - 5;
        connection.EncryptedRefreshToken = cipher[..index] + (cipher[index] == 'A' ? 'B' : 'A') + cipher[(index + 1)..];

        var act = () => _vault.ReadRefreshToken(connection);

        var ex = act.Should().Throw<ConnectionUnavailableException>().Which;
        ex.Status.Should().Be(ConnectionStatus.Live);
        ex.Message.Should().Contain("refresh token cannot be decrypted").And.Contain("Reconnect");
        ex.Message.Should().NotContain(Refresh).And.NotContain(connection.EncryptedRefreshToken);
        ex.InnerException.Should().BeNull("an inner exception's message would be shown to the client by the error middleware");
    }

    [Fact]
    public void A_value_that_is_not_ciphertext_at_all_is_connection_unavailable()
    {
        var connection = new IntegrationConnection { Status = ConnectionStatus.NeedsSetup, EncryptedAccessToken = "not-base64-%%%" };

        var act = () => _vault.ReadAccessToken(connection);

        act.Should().Throw<ConnectionUnavailableException>().WithMessage("*access token cannot be decrypted*");
    }

    [Theory]
    [InlineData("", "RT")]
    [InlineData("AT", "")]
    [InlineData(" ", "RT")]
    public void An_empty_token_from_the_provider_is_refused(string access, string refresh)
    {
        var connection = new IntegrationConnection();

        var act = () => _vault.Store(connection, new TokenGrant(access, refresh, DateTime.UtcNow, DateTime.UtcNow));

        act.Should().Throw<AccountingAuthException>();
        connection.EncryptedAccessToken.Should().BeNull();
    }

    [Fact]
    public void Clear_removes_tokens_and_expiries()
    {
        var connection = new IntegrationConnection();
        _vault.Store(connection, Grant());

        _vault.Clear(connection);

        connection.EncryptedAccessToken.Should().BeNull();
        connection.EncryptedRefreshToken.Should().BeNull();
        connection.AccessTokenExpiresAt.Should().BeNull();
        connection.RefreshTokenExpiresAt.Should().BeNull();
    }
}

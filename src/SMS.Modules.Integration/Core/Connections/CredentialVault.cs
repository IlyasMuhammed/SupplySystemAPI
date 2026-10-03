using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Core.Connections;

/// <summary>
/// The only code that encrypts or decrypts a connection's OAuth tokens (plan QBI-05).
/// <para>
/// Works on the connection entity rather than on the database, so the caller decides what else goes
/// into the same <c>SaveChanges</c> — which is the whole point for a token refresh: the rotated refresh
/// token and both expiries must land together, under the connection's row version, or not at all.
/// </para>
/// </summary>
internal interface ICredentialVault
{
    /// <summary>Encrypts both tokens onto the connection and records their expiries. Does not save.</summary>
    void Store(IntegrationConnection connection, TokenGrant grant);

    /// <exception cref="ConnectionUnavailableException">Nothing stored, or the ciphertext cannot be decrypted.</exception>
    string ReadAccessToken(IntegrationConnection connection);

    /// <exception cref="ConnectionUnavailableException">Nothing stored, or the ciphertext cannot be decrypted.</exception>
    string ReadRefreshToken(IntegrationConnection connection);

    /// <summary>Removes both tokens and their expiries. Does not save.</summary>
    void Clear(IntegrationConnection connection);
}

/// <summary>
/// Tokens are encrypted on the way in and decrypted only on the way out to Intuit, so plaintext exists
/// in exactly two places: the OAuth response that produced it and the call that uses it.
/// <para>
/// <b>Nothing here ever puts a token into an exception, a log line or a model.</b> A decryption failure
/// says which token and why (the exception type), never what the stored value was — the same rule
/// Logistics' <c>CarrierCredentialVault</c> follows for carrier secrets.
/// </para>
/// </summary>
internal sealed class CredentialVault : ICredentialVault
{
    private readonly IEncryptionService _encryption;

    public CredentialVault(IEncryptionService encryption) => _encryption = encryption;

    public void Store(IntegrationConnection connection, TokenGrant grant)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(grant);

        // An empty token would be stored "successfully" and fail at Intuit on the next call, a long
        // way from the cause. The OAuth response was wrong; say so here.
        if (string.IsNullOrWhiteSpace(grant.AccessToken) || string.IsNullOrWhiteSpace(grant.RefreshToken))
            throw new AccountingAuthException("QuickBooks returned an empty access or refresh token.");

        connection.EncryptedAccessToken  = _encryption.Encrypt(grant.AccessToken);
        connection.EncryptedRefreshToken = _encryption.Encrypt(grant.RefreshToken);
        connection.AccessTokenExpiresAt  = grant.AccessTokenExpiresAt;
        connection.RefreshTokenExpiresAt = grant.RefreshTokenExpiresAt;
    }

    public string ReadAccessToken(IntegrationConnection connection) =>
        Decrypt(connection, connection.EncryptedAccessToken, "access");

    public string ReadRefreshToken(IntegrationConnection connection) =>
        Decrypt(connection, connection.EncryptedRefreshToken, "refresh");

    public void Clear(IntegrationConnection connection)
    {
        connection.EncryptedAccessToken  = null;
        connection.EncryptedRefreshToken = null;
        connection.AccessTokenExpiresAt  = null;
        connection.RefreshTokenExpiresAt = null;
    }

    private string Decrypt(IntegrationConnection connection, string? ciphertext, string which)
    {
        if (string.IsNullOrEmpty(ciphertext))
            throw new ConnectionUnavailableException(
                ConnectionStatus.NotConnected,
                $"No QuickBooks {which} token is stored for this organization. Connect QuickBooks.");

        try
        {
            return _encryption.Decrypt(ciphertext);
        }
        catch (Exception ex)
        {
            // Either the encryption key changed under the data, or the ciphertext was altered — which
            // the authenticated format is there to detect. Using a garbled token would fail at Intuit
            // with a confusing 401, so stop here and name the cause. The value itself never appears.
            throw new ConnectionUnavailableException(
                connection.Status,
                $"The stored QuickBooks {which} token cannot be decrypted ({ex.GetType().Name}). Either the "
              + "encryption key has changed since it was saved, or the value has been altered. Reconnect QuickBooks.");
        }
    }
}

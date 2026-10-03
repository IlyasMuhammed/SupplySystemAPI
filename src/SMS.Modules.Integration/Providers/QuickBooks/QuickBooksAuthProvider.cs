using Intuit.Ipp.OAuth2PlatformClient;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Logging;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Providers.QuickBooks;

/// <summary>One answer from Intuit's token endpoint, with the SDK's types stripped off.</summary>
/// <param name="HttpStatus">Set when the SDK reported an HTTP-level failure rather than an OAuth error.</param>
/// <param name="ExceptionType">Set when the call threw inside the SDK (network, discovery); the type name only.</param>
internal sealed record IntuitTokenResponse(
    bool    IsError,
    string? Error,
    string? ErrorDescription,
    int?    HttpStatus,
    string? AccessToken,
    string? RefreshToken,
    long    AccessTokenExpiresIn,
    long    RefreshTokenExpiresIn,
    long    RefreshTokenHardExpiresIn,
    string? ExceptionType = null);

internal sealed record IntuitRevokeResponse(bool IsError, string? Error, int? HttpStatus);

/// <summary>
/// The seam between us and the SDK's <see cref="OAuth2Client"/>, so the OAuth logic can be tested
/// without Intuit. Nothing but <see cref="QuickBooksAuthProvider"/> uses it.
/// </summary>
internal interface IIntuitOAuthClient
{
    string GetAuthorizationUrl(string state);
    Task<IntuitTokenResponse> ExchangeCodeAsync(string code, CancellationToken ct);
    Task<IntuitTokenResponse> RefreshAsync(string refreshToken, CancellationToken ct);
    Task<IntuitRevokeResponse> RevokeAsync(string token, CancellationToken ct);
}

/// <summary>
/// <see cref="IIntuitOAuthClient"/> over the SDK (IppDotNetSdkForQuickBooksApiV3 14.7.1.6).
/// <para>
/// Verified against the installed assembly: <c>new OAuth2Client(clientID, clientSecret, redirectURI,
/// environment)</c> parses <c>environment</c> as an <c>AppEnvironment</c> name ("Sandbox" / "Production",
/// exact case) and <b>fetches Intuit's discovery document inside the constructor</b>. So a client is made
/// per operation — these are rare (connect, callback, a refresh an hour) — and never on a hot path.
/// </para>
/// <para>
/// The SDK's own request/response logging is switched off on every instance
/// (<c>EnableSerilogRequestResponseLoggingFor{Debug,Trace,Console,File}</c>, no <c>CustomLogger</c>):
/// what it would log is token responses.
/// </para>
/// </summary>
internal sealed class SdkIntuitOAuthClient : IIntuitOAuthClient
{
    private readonly IOptions<QuickBooksOptions> _options;

    public SdkIntuitOAuthClient(IOptions<QuickBooksOptions> options) => _options = options;

    public string GetAuthorizationUrl(string state) =>
        // The overload taking the CSRF token uses it as the OAuth "state" parameter verbatim.
        CreateClient().GetAuthorizationURL(new List<OidcScopes> { OidcScopes.Accounting }, state);

    public async Task<IntuitTokenResponse> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        try
        {
            var client   = await Task.Run(CreateClient, ct);
            var response = await client.GetBearerTokenAsync(code, ct);
            return Map(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ex);
        }
    }

    public async Task<IntuitTokenResponse> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        try
        {
            var client   = await Task.Run(CreateClient, ct);
            var response = await client.RefreshTokenAsync(refreshToken, null, ct);
            return Map(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ex);
        }
    }

    public async Task<IntuitRevokeResponse> RevokeAsync(string token, CancellationToken ct)
    {
        try
        {
            var client   = await Task.Run(CreateClient, ct);
            var response = await client.RevokeTokenAsync(token, ct);
            return new IntuitRevokeResponse(response.IsError, response.Error, (int)response.HttpStatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new IntuitRevokeResponse(true, ex.GetType().Name, null);
        }
    }

    private OAuth2Client CreateClient()
    {
        var options = _options.Value;
        var client  = new OAuth2Client(
            options.ClientId, options.ClientSecret, options.RedirectUri, options.IsProduction ? "Production" : "Sandbox");

        client.EnableSerilogRequestResponseLoggingForDebug   = false;
        client.EnableSerilogRequestResponseLoggingForTrace   = false;
        client.EnableSerilogRequestResponseLoggingForConsole = false;
        client.EnableSerilogRequestResponseLoggingForFile    = false;
        client.EnableAdvancedLoggerInfoMode                  = false;
        client.CustomLogger                                  = null;

        return client;
    }

    private static IntuitTokenResponse Map(TokenResponse r)
    {
        var status = r.HttpStatusCode == 0 ? (int?)null : (int)r.HttpStatusCode;

        // The expiry getters read the parsed JSON body, which an HTTP- or exception-level failure does
        // not have — so on an error only the error fields are touched.
        if (r.IsError)
            return new IntuitTokenResponse(
                IsError: true, Error: r.Error, ErrorDescription: SafeTryGet(r, "error_description"), HttpStatus: status,
                AccessToken: null, RefreshToken: null, AccessTokenExpiresIn: 0, RefreshTokenExpiresIn: 0,
                RefreshTokenHardExpiresIn: 0, ExceptionType: r.Exception?.GetType().Name);

        return new IntuitTokenResponse(
            IsError:                   false,
            Error:                     null,
            ErrorDescription:          null,
            HttpStatus:                status,
            AccessToken:               r.AccessToken,
            RefreshToken:              r.RefreshToken,
            AccessTokenExpiresIn:      r.AccessTokenExpiresIn,
            RefreshTokenExpiresIn:     r.RefreshTokenExpiresIn,
            RefreshTokenHardExpiresIn: r.RefreshTokenHardExpiresIn);
    }

    private static string? SafeTryGet(TokenResponse r, string name)
    {
        try { return r.TryGet(name); }
        catch { return null; }
    }

    private static IntuitTokenResponse Failed(Exception ex) =>
        new(true, null, null, null, null, null, 0, 0, 0, ex.GetType().Name);
}

/// <summary>
/// QuickBooks' <see cref="IAccountingAuthProvider"/> (plan QBI-07): consent URL, code exchange,
/// refresh and revoke, translated into our terms.
/// <para>
/// <b>The one translation that matters:</b> a refresh refused with <c>invalid_grant</c> means the grant is
/// gone — the app was disconnected inside QuickBooks, or the refresh token outlived its lifetime — and
/// becomes <see cref="AuthorizationRevokedException"/>, which the token manager turns into a Revoked or
/// Expired connection. Anything else (bad app keys, Intuit unreachable) is an
/// <see cref="AccountingAuthException"/> and leaves the connection alone.
/// </para>
/// </summary>
internal sealed class QuickBooksAuthProvider : IAccountingAuthProvider
{
    /// <summary>Intuit documents one hour; used only if a response ever omits <c>expires_in</c>.</summary>
    private const long FallbackAccessSeconds = 3600;
    /// <summary>Intuit documents 100 days; used only if a response ever omits <c>x_refresh_token_expires_in</c>.</summary>
    private const long FallbackRefreshSeconds = 100L * 24 * 3600;

    private readonly IIntuitOAuthClient          _client;
    private readonly IOptions<QuickBooksOptions> _options;

    public QuickBooksAuthProvider(IIntuitOAuthClient client, IOptions<QuickBooksOptions> options)
    {
        _client  = client;
        _options = options;
    }

    public string ProviderKey => ProviderKeys.QuickBooksOnline;

    public string BuildConsentUrl(string state)
    {
        EnsureConfigured();

        try
        {
            return _client.GetAuthorizationUrl(state);
        }
        catch (Exception ex)
        {
            throw new AccountingAuthException(
                $"Could not reach Intuit to start the connection ({ex.GetType().Name}). Try again in a moment.", inner: ex);
        }
    }

    public async Task<TokenGrant> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        EnsureConfigured();

        var response = await _client.ExchangeCodeAsync(code, ct);
        if (response.IsError || string.IsNullOrEmpty(response.AccessToken) || string.IsNullOrEmpty(response.RefreshToken))
            throw new AccountingAuthException($"Intuit did not exchange the authorization code: {Describe(response)}.", response.Error);

        return ToGrant(response, DateTime.UtcNow);
    }

    public async Task<TokenGrant> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        EnsureConfigured();

        var response = await _client.RefreshAsync(refreshToken, ct);

        if (IsGrantGone(response))
            throw new AuthorizationRevokedException($"Intuit refused the refresh token: {Describe(response)}.");

        if (response.IsError || string.IsNullOrEmpty(response.AccessToken) || string.IsNullOrEmpty(response.RefreshToken))
            throw new AccountingAuthException($"Intuit did not refresh the connection: {Describe(response)}.", response.Error);

        return ToGrant(response, DateTime.UtcNow);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct = default)
    {
        EnsureConfigured();

        var response = await _client.RevokeAsync(refreshToken, ct);
        if (response.IsError)
            throw new AccountingAuthException(
                $"Intuit did not confirm the revocation ({Redactor.Redact(response.Error) ?? "no reason"}{(response.HttpStatus is { } s ? $", HTTP {s}" : "")}).",
                response.Error);
    }

    /// <summary>
    /// <c>invalid_grant</c> is Intuit's word for "this refresh token is no longer valid" — expired,
    /// revoked, or already rotated away. It can arrive as the OAuth error or inside its description.
    /// </summary>
    internal static bool IsGrantGone(IntuitTokenResponse response) =>
        response.IsError
        && (string.Equals(response.Error, "invalid_grant", StringComparison.OrdinalIgnoreCase)
            || (response.ErrorDescription?.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) ?? false));

    internal static TokenGrant ToGrant(IntuitTokenResponse response, DateTime now)
    {
        var accessSeconds  = response.AccessTokenExpiresIn > 0 ? response.AccessTokenExpiresIn : FallbackAccessSeconds;
        var refreshSeconds = response.RefreshTokenExpiresIn > 0 ? response.RefreshTokenExpiresIn : FallbackRefreshSeconds;

        // Where Intuit also states a hard maximum lifetime, the grant ends at whichever comes first.
        if (response.RefreshTokenHardExpiresIn > 0)
            refreshSeconds = Math.Min(refreshSeconds, response.RefreshTokenHardExpiresIn);

        return new TokenGrant(
            response.AccessToken!,
            response.RefreshToken!,
            now.AddSeconds(accessSeconds),
            now.AddSeconds(refreshSeconds));
    }

    /// <summary>
    /// Error code, description and status — never the raw body, which is where tokens live. Redacted
    /// anyway: an SDK exception message is the one part whose content we do not control.
    /// </summary>
    private static string Describe(IntuitTokenResponse r)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Error))            parts.Add(Redactor.Redact(r.Error)!);
        if (!string.IsNullOrWhiteSpace(r.ErrorDescription)) parts.Add(Redactor.Redact(r.ErrorDescription)!);
        if (r.HttpStatus is { } status and >= 400)          parts.Add($"HTTP {status}");
        if (!string.IsNullOrWhiteSpace(r.ExceptionType))    parts.Add(r.ExceptionType!);
        return parts.Count == 0 ? "no reason given" : string.Join(", ", parts);
    }

    private void EnsureConfigured()
    {
        if (!_options.Value.IsConfigured)
            throw new AccountingAuthException(
                "QuickBooks is not configured on this server: QuickBooks:ClientId, ClientSecret and RedirectUri must be set.");
    }
}

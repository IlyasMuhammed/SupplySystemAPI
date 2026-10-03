namespace SMS.Modules.Integration.Core.Connections;

/// <summary>
/// The accounting provider's OAuth endpoint refused or failed a request for a reason other than a
/// revoked grant: bad app keys, an unknown authorization code, Intuit unreachable.
/// <para>
/// The message is always composed by us from the provider's error <em>code</em> and description —
/// never from a raw response body, which is where a token would be if there was one to leak.
/// </para>
/// </summary>
internal sealed class AccountingAuthException : Exception
{
    /// <summary>The provider's OAuth error code (e.g. <c>invalid_client</c>), when it sent one.</summary>
    public string? ErrorCode { get; }

    public AccountingAuthException(string message, string? errorCode = null, Exception? inner = null)
        : base(message, inner) => ErrorCode = errorCode;
}

/// <summary>
/// A token refresh failed for a reason that says nothing about the grant — Intuit unreachable, a 5xx,
/// a timeout — and the current access token has already run out, so there is no token to hand out.
/// <para>
/// Deliberately not a <see cref="ConnectionUnavailableException"/>: the connection is fine and the next
/// attempt will very likely work, so callers treat this as transient (retry later) rather than
/// suspending everything queued and asking a person to reconnect.
/// </para>
/// </summary>
internal sealed class TokenRefreshFailedException : Exception
{
    public TokenRefreshFailedException(string message, Exception? inner = null) : base(message, inner) { }
}

using System.Text.RegularExpressions;

namespace SMS.Modules.Integration.Core.Logging;

/// <summary>
/// Scrubs secrets from anything written to the sync log or a log line — applied at the boundary,
/// before storage, never filtered afterwards. Tokens, client secrets, API keys and Authorization
/// credentials are replaced; oversized bodies are truncated.
/// <para>
/// Deliberately narrow: business data (names, error codes, a customer called "Basic Supplies") must
/// survive, or the log stops being useful for support. So JSON keys are matched by exact secret name,
/// Authorization credentials only when they look like a credential (16+ token characters), and the
/// OAuth authorization <c>code</c> only in its form/query shape — a JSON <c>"code"</c> key is an
/// Intuit error code or a currency code, never the OAuth code.
/// </para>
/// </summary>
internal static partial class Redactor
{
    public const int MaxLength = 16_000;
    private const string Mask = "***REDACTED***";

    // Also matches the escaped form (\"access_token\":\"…\") that appears when JSON is logged inside a JSON string.
    [GeneratedRegex("(\\\\?\"(?:access_token|refresh_token|id_token|client_secret|clientSecret|accessToken|refreshToken|apiKey|api_key|password)\\\\?\"\\s*:\\s*\\\\?\")(?:[^\"\\\\]|\\\\(?!\"))*(\\\\?\")", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecret();

    [GeneratedRegex("((?:access_token|refresh_token|client_secret|code)=)[^&\\s\"]+", RegexOptions.IgnoreCase)]
    private static partial Regex FormSecret();

    // A credential after Bearer/Basic: anything shaped like a JWT or base64 (contains '.' or '='), or any
    // 16+ character token. "Basic Supplies" and "Bearer Logistics" are names, not credentials.
    [GeneratedRegex("\\b(Bearer|Basic)\\s+(?:[A-Za-z0-9\\-_~+/]*[.=][A-Za-z0-9\\-._~+/=]*|[A-Za-z0-9\\-._~+/]{16,}=*)", RegexOptions.IgnoreCase)]
    private static partial Regex AuthCredential();

    [GeneratedRegex("sqb_[A-Za-z0-9_\\-]{8,}")]
    private static partial Regex ApiKey();

    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = JsonSecret().Replace(text, m => m.Groups[1].Value + Mask + m.Groups[2].Value);
        result = FormSecret().Replace(result, m => m.Groups[1].Value + Mask);
        result = AuthCredential().Replace(result, m => m.Groups[1].Value + " " + Mask);
        result = ApiKey().Replace(result, Mask);

        return result.Length > MaxLength ? result[..MaxLength] + "…[truncated]" : result;
    }
}

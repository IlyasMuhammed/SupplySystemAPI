using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Intuit.Ipp.Exception;
using Newtonsoft.Json.Linq;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Logging;
using SMS.Modules.Integration.Core.Providers;

namespace SMS.Modules.Integration.Providers.QuickBooks;

/// <summary>What the provider was doing when the call failed — changes how a few codes read.</summary>
internal enum QboOperation
{
    Other,
    Create,
    Update,
    Read,
    Query,
    Void
}

/// <summary>One error QuickBooks sent in a Fault (code / element / message / detail).</summary>
internal sealed record QboFaultError(string? Code, string? Element, string? Message, string? Detail);

/// <summary>The classified failure, before it becomes a <see cref="ProviderResult{T}"/>.</summary>
internal sealed record QboFailure(
    ProviderOutcomeKind          Outcome,
    string?                      Code,
    string                       Message,
    string?                      Field,
    string?                      IntuitTid,
    int?                         HttpStatus,
    IReadOnlyList<QboFaultError> Errors,
    string                       ExceptionType);

/// <summary>
/// Turns anything the SDK (or the network under it) throws into a <see cref="ProviderOutcomeKind"/>.
/// The rule that matters: <see cref="ProviderOutcomeKind.Refused"/> is final and never retried, so it is
/// used only when QuickBooks clearly said no; anything unrecognised is
/// <see cref="ProviderOutcomeKind.Transient"/> (outcome unknown — look it up before retrying).
/// <para>How SDK 14.7.1.6 surfaces an HTTP error (verified by decompiling <c>FaultHandler</c>):</para>
/// <list type="bullet">
/// <item>400 → <c>IdsException("BadRequest", ErrorCode "400")</c> whose inner exception is the parsed
/// Fault: <c>ValidationException</c> (ValidationFault — its IdsErrors are also copied onto the outer
/// <c>InnerExceptions</c>), <c>ServiceException</c> (ServiceFault), <c>SecurityException</c>
/// (Authentication/AuthorizationFault) or a plain <c>IdsException</c> (other fault types, e.g. SystemFault).</item>
/// <item>401 → <c>InvalidTokenException("Unauthorized-401")</c> wrapping the parsed <c>SecurityException</c> (code 3200).</item>
/// <item>403 / 404 / 500 / 503 → <c>IdsException</c> with ErrorCode = the status, wrapping an
/// <c>EndpointNotFoundException</c>; the body is not parsed.</item>
/// <item>429 → <c>IdsException</c> ErrorCode "429" wrapping a <c>ThrottleExceededException</c>; body not parsed.</item>
/// <item>Other statuses → bare <c>IdsException</c> with ErrorCode = the status.</item>
/// <item>A Fault inside a 200 body → the parsed exception thrown directly.</item>
/// <item>No response at all (timeout, DNS, reset) → the SDK swallows the WebException and then throws
/// <c>IdsException("Communication error…")</c> wrapping a <c>CommunicationException</c>.</item>
/// </list>
/// <c>Intuit_Tid</c> is set on the outer exception from the response header when there was a response.
/// </summary>
internal sealed partial class QboErrorTranslator
{
    public const int MaxCodeLength    = 100;
    public const int MaxMessageLength = 2000;

    // Fault codes (QuickBooks sends them as strings, sometimes zero-padded: "003200").
    public const string AuthenticationFailed     = "3200";
    public const string GeneralAuthentication    = "100";
    public const string ApplicationAuthFailed    = "3100";
    public const string InvalidCompanyStatus     = "6190";
    public const string ThrottleExceeded         = "3001";
    public const string DuplicateName            = "6240";
    public const string DuplicateDocumentNumber  = "6140";
    public const string StaleObject              = "5010";
    public const string ObjectNotFound           = "610";

    private static readonly HashSet<string> AuthCodes = [GeneralAuthentication, AuthenticationFailed, ApplicationAuthFailed, InvalidCompanyStatus];

    [GeneratedRegex(@"Unrecognized field\s+\\?""(?<f>[^""\\]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UnrecognizedField();

    [GeneratedRegex(@"Required param(?:eter)?\s+(?<f>[A-Za-z][\w.\[\]]*)\s+is missing", RegexOptions.IgnoreCase)]
    private static partial Regex RequiredParameter();

    [GeneratedRegex(@"\b(?:Element name|Element|Property|Field)\s*[:=]\s*(?<f>[A-Za-z][\w.\[\]]*)", RegexOptions.IgnoreCase)]
    private static partial Regex NamedField();

    /// <summary>The failure as a result: outcome, code, message, field, IntuitTid and a redacted fault summary in ResponseJson.</summary>
    public ProviderResult<T> Translate<T>(Exception exception, QboOperation operation = QboOperation.Other)
    {
        var failure = Classify(exception, operation);
        return new ProviderResult<T>
        {
            Outcome      = failure.Outcome,
            ErrorCode    = failure.Code,
            Message      = failure.Message,
            ErrorField   = failure.Field,
            IntuitTid    = failure.IntuitTid,
            ResponseJson = Redactor.Redact(Describe(failure))
        };
    }

    public QboFailure Classify(Exception exception, QboOperation operation = QboOperation.Other)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var chain  = Flatten(exception);
        var errors = CollectErrors(chain);
        var tid    = chain.OfType<IdsException>().Select(e => e.Intuit_Tid).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        var http   = chain.OfType<IdsException>().Select(e => HttpStatusOf(e.ErrorCode)).FirstOrDefault(s => s is not null);
        var type   = exception.GetType().Name;

        QboFailure Build(ProviderOutcomeKind outcome, string? code, QboFaultError? primary = null) =>
            new(outcome,
                Cap(code, MaxCodeLength),
                Cap(Redactor.Redact(ComposeMessage(primary, chain, http)), MaxMessageLength) ?? type,
                ExtractField(primary),
                Cap(tid, MaxCodeLength),
                http,
                errors,
                type);

        // ── Connection-level: the token / grant / company cannot be used. Suspend, do not retry. ──
        if (chain.Any(e => e is ConnectionUnavailableException))
        {
            var unavailable = chain.OfType<ConnectionUnavailableException>().First();
            return Build(ProviderOutcomeKind.AuthRevoked, $"ConnectionUnavailable:{unavailable.Status}");
        }
        if (chain.Any(e => e is AuthorizationRevokedException))
            return Build(ProviderOutcomeKind.AuthRevoked, "AuthorizationRevoked");

        var authError = errors.FirstOrDefault(e => e.Code is not null && AuthCodes.Contains(e.Code));
        if (authError is not null)
            return Build(ProviderOutcomeKind.AuthRevoked, authError.Code, authError);
        if (chain.Any(e => e is InvalidTokenException or SecurityException or InvalidRealmException) || http is 401 or 403)
            return Build(ProviderOutcomeKind.AuthRevoked, http?.ToString(CultureInfo.InvariantCulture) ?? InnermostName(chain), errors.FirstOrDefault());

        // ── Throttled (HTTP 429 / ThrottleExceeded 3001). ──
        var throttleError = errors.FirstOrDefault(e => e.Code == ThrottleExceeded);
        if (throttleError is not null)
            return Build(ProviderOutcomeKind.Throttled, throttleError.Code, throttleError);
        if (http == 429 || chain.Any(e => e is ThrottleExceededException))
            return Build(ProviderOutcomeKind.Throttled, "429");

        // ── Specific fault codes, whatever the fault type. ──
        foreach (var error in errors)
        {
            switch (error.Code)
            {
                case DuplicateName:
                case DuplicateDocumentNumber:
                    return Build(ProviderOutcomeKind.Duplicate, error.Code, error);
                case StaleObject:
                    return Build(ProviderOutcomeKind.StaleObject, error.Code, error);
                case ObjectNotFound:
                    // On a create there is no target record to be missing — 610 then means a record it
                    // references is inactive or gone, which is a refusal of this payload.
                    return Build(operation == QboOperation.Create ? ProviderOutcomeKind.Refused : ProviderOutcomeKind.NotFound,
                        error.Code, error);
            }
        }

        // ── Any other ValidationFault: QuickBooks looked at the data and said no. Final. ──
        if (chain.Any(e => e is ValidationException))
        {
            var primary = errors.FirstOrDefault();
            return Build(ProviderOutcomeKind.Refused, primary?.Code ?? http?.ToString(CultureInfo.InvariantCulture) ?? "Validation", primary);
        }

        // ── Service / system faults, 5xx, 404, timeouts, dropped connections, unreadable responses:
        //    the outcome is unknown. ──
        if (chain.Any(IsTransientType) || http is >= 500 or 404 || errors.Count > 0)
        {
            var primary = errors.FirstOrDefault();
            return Build(ProviderOutcomeKind.Transient, primary?.Code ?? http?.ToString(CultureInfo.InvariantCulture) ?? InnermostName(chain), primary);
        }

        // ── A 400 with no readable Fault: QuickBooks refused the request as sent. ──
        if (http == 400)
            return Build(ProviderOutcomeKind.Refused, "400");

        // ── Anything else we did not anticipate. Never Refused. ──
        return Build(ProviderOutcomeKind.Transient, http?.ToString(CultureInfo.InvariantCulture) ?? InnermostName(chain));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    private static bool IsTransientType(Exception e) => e is
        ServiceException                        // ServiceFault, EndpointNotFound, Communication, Throttle, ServerTooBusy, Retry, ChannelTerminated…
        or RetryExceededException
        or Intuit.Ipp.Exception.SerializationException  // response could not be read: the call may well have succeeded
        or ServiceReturnedNoInformationException
        or WebException
        or HttpRequestException
        or SocketException
        or IOException
        or TimeoutException
        or OperationCanceledException;          // includes TaskCanceledException (HTTP timeout)

    /// <summary>Outer first. Follows both the BCL inner exception and the SDK's own (it hides the base property).</summary>
    private static List<Exception> Flatten(Exception exception)
    {
        var result = new List<Exception>();
        var queue  = new Queue<Exception>();
        queue.Enqueue(exception);
        while (queue.Count > 0 && result.Count < 32)
        {
            var current = queue.Dequeue();
            if (result.Any(e => ReferenceEquals(e, current))) continue;
            result.Add(current);

            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) queue.Enqueue(inner);
            if (current.InnerException is { } baseInner) queue.Enqueue(baseInner);
            if (current is IdsException { InnerException: { } idsInner }) queue.Enqueue(idsInner);
        }
        return result;
    }

    private static List<QboFaultError> CollectErrors(IEnumerable<Exception> chain)
    {
        var seen   = new List<IdsError>();
        var result = new List<QboFaultError>();
        foreach (var ids in chain.OfType<IdsException>())
        {
            if (ids.InnerExceptions is null) continue;
            foreach (var error in ids.InnerExceptions)
            {
                if (error is null || seen.Any(s => ReferenceEquals(s, error))) continue;
                seen.Add(error);
                result.Add(new QboFaultError(NormalizeCode(error.ErrorCode), Blank(error.Element), Blank(error.Message), Blank(error.Detail)));
            }
        }
        return result;
    }

    /// <summary>"003200" → "3200"; non-numeric codes are kept trimmed.</summary>
    public static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var trimmed = code.Trim();
        return trimmed.All(char.IsAsciiDigit) ? (trimmed.TrimStart('0') is { Length: > 0 } digits ? digits : "0") : trimmed;
    }

    /// <summary>The SDK puts the HTTP status in <c>IdsException.ErrorCode</c> when it had a response.</summary>
    private static int? HttpStatusOf(string? errorCode) =>
        int.TryParse(errorCode, NumberStyles.None, CultureInfo.InvariantCulture, out var status) && status is >= 100 and <= 599
            ? status
            : null;

    private static string ComposeMessage(QboFaultError? primary, IReadOnlyList<Exception> chain, int? http)
    {
        if (primary is not null)
        {
            var message = primary.Message;
            var detail  = primary.Detail;
            if (message is not null && detail is not null)
                return detail.StartsWith(message, StringComparison.OrdinalIgnoreCase) ? detail : $"{message}: {detail}";
            var single = message ?? detail;
            if (single is not null) return single;
        }

        var messages = chain
            .Where(e => e is not IdsError)
            .Select(e => Blank(e.Message))
            .Where(m => m is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var text = messages.Count > 0 ? string.Join(" | ", messages) : chain[0].GetType().Name;
        return http is { } status && !text.Contains(status.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            ? $"HTTP {status}: {text}"
            : text;
    }

    /// <summary>The fault's <c>element</c> when QuickBooks named it, otherwise a field named in the detail text.</summary>
    public static string? ExtractField(QboFaultError? error)
    {
        if (error is null) return null;
        if (error.Element is { } element) return Cap(element, MaxCodeLength);

        foreach (var text in new[] { error.Detail, error.Message })
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (var regex in new[] { UnrecognizedField(), RequiredParameter(), NamedField() })
            {
                var match = regex.Match(text);
                if (match.Success) return Cap(match.Groups["f"].Value, MaxCodeLength);
            }
        }
        return null;
    }

    private static string InnermostName(IReadOnlyList<Exception> chain) =>
        chain.LastOrDefault(e => e is not IdsError)?.GetType().Name ?? chain[0].GetType().Name;

    /// <remarks>
    /// Keys are "errorCode", never "code": <see cref="Redactor"/> masks any JSON property named "code"
    /// (the OAuth authorization code), which would blank out QuickBooks' fault codes.
    /// </remarks>
    private static string Describe(QboFailure failure)
    {
        var json = new JObject
        {
            ["outcome"]    = failure.Outcome.ToString(),
            ["exception"]  = failure.ExceptionType,
            ["httpStatus"] = failure.HttpStatus,
            ["errorCode"]  = failure.Code,
            ["field"]      = failure.Field,
            ["intuitTid"]  = failure.IntuitTid,
            ["message"]    = failure.Message
        };
        if (failure.Errors.Count > 0)
        {
            json["errors"] = new JArray(failure.Errors.Select(e => new JObject
            {
                ["errorCode"] = e.Code,
                ["element"]   = e.Element,
                ["message"]   = e.Message,
                ["detail"]    = e.Detail
            }));
        }
        return QboJson.Describe(json);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Cap(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}

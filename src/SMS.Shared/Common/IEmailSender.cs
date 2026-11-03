namespace SMS.Shared.Common;

public sealed record EmailAttachment(string FileName, byte[] Content, string ContentType);

/// <summary>
/// A48 REV-1 — what became of one send: <see cref="Success"/>, or the reason it did not go out (<see cref="Error"/>, safe to
/// store and show: no credentials, no stack trace).
/// </summary>
public sealed record EmailSendResult(bool Success, string? Error)
{
    public static readonly EmailSendResult Sent = new(true, null);
    public static EmailSendResult Failed(string error) => new(false, string.IsNullOrWhiteSpace(error) ? "The email could not be sent." : error);
}

// Public (not internal) so other modules — e.g. SMS.Modules.Demand emailing a generated PO
// document — can send email via DI without depending on SMS.Modules.Auth directly. Implemented
// in SMS.Modules.Auth (which already owns the SMTP/SendGrid routing logic), same cross-module
// pattern as IUserQueryService/IOrgChartService.
public interface IEmailSender
{
    /// <summary>Fire-and-forget: a failure is logged by the sender and never reaches the caller (unchanged since before A48).</summary>
    Task SendAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null);

    /// <summary>
    /// A48 REV-1 — the same send, telling the caller whether it went out: an SMTP/SendGrid exception, a 4xx/5xx response, a
    /// missing API key, an invalid address or a subject with a line break come back as <see cref="EmailSendResult.Failed"/>
    /// (never thrown). Callers that record "sent" (statements, dunning) use this one.
    /// <para>
    /// The default implementation (for senders that predate it) reports whatever <see cref="SendAsync"/> did: success unless it
    /// threw. The real sender (SMS.Modules.Auth EmailSender) overrides it. A Moq mock of this interface answers null for it
    /// unless the mock has <c>CallBase = true</c> or a setup — set one up in tests.
    /// </para>
    /// </summary>
    async Task<EmailSendResult> SendWithResultAsync(
        string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(to, subject, htmlBody, attachments);
            return EmailSendResult.Sent;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return EmailSendResult.Failed(ex.Message);
        }
    }
}

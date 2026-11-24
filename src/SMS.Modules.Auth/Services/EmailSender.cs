using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;
using SMS.Shared.Common;
using System.Net;
using System.Net.Mail;

namespace SMS.Modules.Auth.Services;

// Public counterpart to the internal IEmailService — reuses the same SMTP/SendGrid routing
// (Environment 0 -> Gmail SMTP, else SendGrid) but with attachment support, for cross-module
// callers like the PO document email-on-approval flow.
//
// A48 REV-1 — one send path with an outcome (SendWithResultAsync). SendAsync keeps its old contract exactly: it runs the same
// path, logs, and swallows the outcome; only the result-returning callers (statements, dunning) learn about a failure.
internal sealed class EmailSender : IEmailSender
{
    private readonly AppSettings _settings;
    private readonly ILogger<EmailSender> _logger;

    /// <summary>Test seams: replace the network hop. Null = the real SMTP / SendGrid clients.</summary>
    internal Func<MailMessage, Task>? SmtpTransport { get; init; }
    internal Func<SendGridMessage, Task<Response>>? SendGridTransport { get; init; }

    private readonly EmailSendTracker? _tracker;

    public EmailSender(IOptions<AppSettings> settings, ILogger<EmailSender> logger, EmailSendTracker? tracker = null)
    {
        _settings = settings.Value;
        _logger = logger;
        _tracker = tracker;
    }

    public async Task SendAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null) =>
        _ = await SendCoreAsync(to, subject, htmlBody, attachments, validate: false);

    public Task<EmailSendResult> SendWithResultAsync(
        string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken ct = default) =>
        SendCoreAsync(to, subject, htmlBody, attachments, validate: true);

    private async Task<EmailSendResult> SendCoreAsync(
        string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments, bool validate)
    {
        if (validate)
        {
            if (string.IsNullOrWhiteSpace(to) || !MailAddress.TryCreate(to.Trim(), out _))
                return Fail($"'{to}' is not a valid email address.", to);
            if (subject is not null && (subject.Contains('\r') || subject.Contains('\n')))
                return Fail("The email subject contains a line break.", to);
        }

        var result = _settings.Environment == 0
            ? await SendViaSmtpAsync(to, subject ?? string.Empty, htmlBody, attachments)
            : await SendViaSendGridAsync(to, subject ?? string.Empty, htmlBody, attachments);
        _tracker?.Record(result.Success, result.Error);
        return result;
    }

    private EmailSendResult Fail(string error, string to)
    {
        _logger.LogError("Email to {Email} not sent: {Error}", to, error);
        return EmailSendResult.Failed(error);
    }

    private async Task<EmailSendResult> SendViaSmtpAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments)
    {
        try
        {
            var from = string.IsNullOrWhiteSpace(_settings.SmtpFromEmail)
                ? _settings.AppSupportEmail
                : _settings.SmtpFromEmail;

            using var message = new MailMessage(from, to, subject, htmlBody) { IsBodyHtml = true };

            foreach (var att in attachments ?? Array.Empty<EmailAttachment>())
                message.Attachments.Add(new System.Net.Mail.Attachment(new MemoryStream(att.Content), att.FileName, att.ContentType));

            if (SmtpTransport is not null)
                await SmtpTransport(message);
            else
            {
                using var client = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
                {
                    Credentials = new NetworkCredential(_settings.SmtpUser, _settings.SmtpPass),
                    EnableSsl = true,
                    Timeout = 8_000 // A51 P7/P8: a dead SMTP host must not hold anything for long (the default is 100 s)
                };
                client.Send(message);
            }
            _logger.LogInformation("Email sent via Gmail SMTP to {Email}", to);
            return EmailSendResult.Sent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gmail SMTP failed sending to {Email}", to);
            return EmailSendResult.Failed($"SMTP send failed: {ex.Message}");
        }
    }

    private async Task<EmailSendResult> SendViaSendGridAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.SendGridApiKey) && SendGridTransport is null)
            {
                _logger.LogError("SendGrid API key is not configured");
                return EmailSendResult.Failed("Email is not configured (no SendGrid API key).");
            }

            var from = new EmailAddress(
                string.IsNullOrWhiteSpace(_settings.SmtpFromEmail)
                    ? _settings.AppSupportEmail
                    : _settings.SmtpFromEmail);

            var msg = MailHelper.CreateSingleEmail(from, new EmailAddress(to), subject, null, htmlBody);

            foreach (var att in attachments ?? Array.Empty<EmailAttachment>())
                msg.AddAttachment(att.FileName, Convert.ToBase64String(att.Content), att.ContentType);

            var response = SendGridTransport is not null
                ? await SendGridTransport(msg)
                : await new SendGridClient(_settings.SendGridApiKey).SendEmailAsync(msg);

            if ((int)response.StatusCode >= 400)
            {
                _logger.LogError("SendGrid returned {Status} sending to {Email}", response.StatusCode, to);
                return EmailSendResult.Failed($"SendGrid refused the email ({(int)response.StatusCode} {response.StatusCode}).");
            }

            _logger.LogInformation("Email sent via SendGrid to {Email}", to);
            return EmailSendResult.Sent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendGrid failed sending to {Email}", to);
            return EmailSendResult.Failed($"SendGrid send failed: {ex.Message}");
        }
    }
}

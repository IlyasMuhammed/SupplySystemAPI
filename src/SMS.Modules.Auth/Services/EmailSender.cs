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
internal sealed class EmailSender : IEmailSender
{
    private readonly AppSettings _settings;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(IOptions<AppSettings> settings, ILogger<EmailSender> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null)
    {
        if (_settings.Environment == 0)
            SendViaSmtp(to, subject, htmlBody, attachments);
        else
            await SendViaSendGrid(to, subject, htmlBody, attachments);
    }

    private void SendViaSmtp(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments)
    {
        try
        {
            using var client = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
            {
                Credentials = new NetworkCredential(_settings.SmtpUser, _settings.SmtpPass),
                EnableSsl = true
            };
            var from = string.IsNullOrWhiteSpace(_settings.SmtpFromEmail)
                ? _settings.AppSupportEmail
                : _settings.SmtpFromEmail;

            using var message = new MailMessage(from, to, subject, htmlBody) { IsBodyHtml = true };

            foreach (var att in attachments ?? Array.Empty<EmailAttachment>())
                message.Attachments.Add(new System.Net.Mail.Attachment(new MemoryStream(att.Content), att.FileName, att.ContentType));

            client.Send(message);
            _logger.LogInformation("Email sent via Gmail SMTP to {Email}", to);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gmail SMTP failed sending to {Email}", to);
        }
    }

    private async Task SendViaSendGrid(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_settings.SendGridApiKey))
            {
                _logger.LogError("SendGrid API key is not configured");
                return;
            }

            var client = new SendGridClient(_settings.SendGridApiKey);
            var from = new EmailAddress(
                string.IsNullOrWhiteSpace(_settings.SmtpFromEmail)
                    ? _settings.AppSupportEmail
                    : _settings.SmtpFromEmail);

            var msg = MailHelper.CreateSingleEmail(from, new EmailAddress(to), subject, null, htmlBody);

            foreach (var att in attachments ?? Array.Empty<EmailAttachment>())
                msg.AddAttachment(att.FileName, Convert.ToBase64String(att.Content), att.ContentType);

            var response = await client.SendEmailAsync(msg);

            if ((int)response.StatusCode >= 400)
                _logger.LogError("SendGrid returned {Status} sending to {Email}", response.StatusCode, to);
            else
                _logger.LogInformation("Email sent via SendGrid to {Email}", to);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendGrid failed sending to {Email}", to);
        }
    }
}

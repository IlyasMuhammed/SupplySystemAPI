namespace SMS.Shared.Common;

public sealed record EmailAttachment(string FileName, byte[] Content, string ContentType);

// Public (not internal) so other modules — e.g. SMS.Modules.Demand emailing a generated PO
// document — can send email via DI without depending on SMS.Modules.Auth directly. Implemented
// in SMS.Modules.Auth (which already owns the SMTP/SendGrid routing logic), same cross-module
// pattern as IUserQueryService/IOrgChartService.
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null);
}

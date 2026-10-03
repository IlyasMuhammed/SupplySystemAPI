using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;
using SMS.Modules.Notifications.Domain;
using SMS.Modules.Notifications.Hubs;
using SMS.Modules.Notifications.Models;
using SMS.Modules.Notifications.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using System.Net;
using System.Net.Mail;

namespace SMS.Modules.Notifications.Services;

public sealed class NotificationService : INotificationService
{
    private readonly INotificationRepository       _repo;
    private readonly IHubContext<NotificationHub>  _hub;
    private readonly AppSettings                   _settings;
    private readonly ILogger<NotificationService>  _logger;

    public NotificationService(
        INotificationRepository      repo,
        IHubContext<NotificationHub> hub,
        IOptions<AppSettings>        settings,
        ILogger<NotificationService> logger)
    {
        _repo     = repo;
        _hub      = hub;
        _settings = settings.Value;
        _logger   = logger;
    }

    // ── INotificationService ──────────────────────────────────────────────────

    public async Task CreateAsync(NotificationRequest req)
    {
        var entity = Map(req);
        var uuid   = await _repo.CreateAsync(entity);

        var dto = ToDto(entity, uuid);
        await PushToUserAsync(req.UserId, dto);

        if (req.SendEmail)
        {
            var email = req.Email ?? await _repo.GetUserEmailAsync(req.UserId);
            if (!string.IsNullOrWhiteSpace(email))
                SendEmailFireAndForget(email, req.Title, req.Message, req.NavigationUrl);
        }
    }

    public async Task TryCreateAsync(NotificationRequest req)
    {
        try   { await CreateAsync(req); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Non-critical: notification creation failed for user {UserId} type {Type}",
                req.UserId, req.Type);
        }
    }

    public async Task BroadcastAsync(string type, string category, string title, string message, int createdBy = 0)
    {
        var users = await _repo.GetAllActiveUsersAsync();
        if (users.Count == 0) return;

        var entities = users.Select(u => new Notification
        {
            UserId    = u.UserId,
            Type      = type,
            Category  = category,
            Title     = title,
            Message   = message,
            CreatedBy = createdBy
        });

        await _repo.CreateManyAsync(entities);

        // Push in parallel to all user groups
        var tasks = users.Select(u =>
            _hub.Clients.Group($"user_{u.UserId}")
                .SendAsync("ReceiveNotification", new { title, message, type, category }));
        await Task.WhenAll(tasks);
    }

    // ── Internal service methods (used by controllers / other services) ────────

    /// <summary>Create notifications for every user that belongs to a role.</summary>
    public async Task CreateForRoleAsync(int roleId, NotificationRequest template)
    {
        var users = await _repo.GetUsersByRoleAsync(roleId);
        if (users.Count == 0) return;

        var entities = users.Select(u => Map(template with { UserId = u.UserId })).ToList();
        await _repo.CreateManyAsync(entities);

        foreach (var u in users)
        {
            var dto = ToDto(entities.First(e => e.UserId == u.UserId), Guid.Empty);
            await PushToUserAsync(u.UserId, dto);

            if (template.SendEmail && !string.IsNullOrWhiteSpace(u.Email))
                SendEmailFireAndForget(u.Email, template.Title, template.Message, template.NavigationUrl);
        }
    }

    // ── Repository passthroughs used by NotificationsController ──────────────

    public Task<PaginatedResponse<NotificationDto>> GetForUserAsync(int userId, NotificationListRequest req) =>
        _repo.GetForUserAsync(userId, req);

    public Task<int> GetUnreadCountAsync(int userId) =>
        _repo.GetUnreadCountAsync(userId);

    public Task<bool> MarkReadAsync(int userId, Guid[]? uuids) =>
        _repo.MarkReadAsync(userId, uuids);

    public Task<bool> DeleteAsync(int userId, Guid uuid) =>
        _repo.DeleteAsync(userId, uuid);

    public async Task SendEmailAsync(string to, string subject, string htmlBody)
    {
        try
        {
            if (_settings.Environment == 0)
                await SendViaSmtpAsync(to, subject, htmlBody);
            else
                await SendViaSendGridAsync(to, subject, htmlBody);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transactional email to {Email} failed", to);
            throw; // let Hangfire retry
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static Notification Map(NotificationRequest r) => new()
    {
        UserId        = r.UserId,
        Type          = r.Type,
        Category      = r.Category,
        Title         = r.Title,
        Message       = r.Message,
        EntityType    = r.EntityType,
        EntityUuid    = r.EntityUuid,
        NavigationUrl = r.NavigationUrl,
        CreatedBy     = r.CreatedBy
    };

    private static NotificationDto ToDto(Notification n, Guid uuid) => new(
        uuid == Guid.Empty ? n.UUID : uuid,
        n.UserId, n.Type, n.Category, n.Title, n.Message,
        n.EntityType, n.EntityUuid, n.NavigationUrl,
        n.IsRead, n.ReadAt, n.CreatedBy, n.CreatedAt);

    private Task PushToUserAsync(int userId, NotificationDto dto) =>
        _hub.Clients.Group($"user_{userId}").SendAsync("ReceiveNotification", dto);

    private void SendEmailFireAndForget(string to, string subject, string body, string? actionUrl)
    {
        // actionUrl (NavigationUrl) is stored as a bare frontend-relative path (e.g.
        // "/portal/pages/workflow-inbox/{uuid}") — correct for in-app use (Angular router), but
        // embedded as-is into an email's <a href>, it has no scheme or host for the email client
        // to resolve it against (an email has no "current page" to be relative to, unlike a
        // browser tab). Several webmail clients mangle a bare "/path" href into "http:///path"
        // trying to render it. Resolve it to a genuine absolute URL against AppSettings:BaseUrl
        // before it ever reaches the HTML.
        var absoluteActionUrl = actionUrl is null ? null : $"{_settings.BaseUrl.TrimEnd('/')}{actionUrl}";

        _ = Task.Run(async () =>
        {
            try
            {
                var htmlBody = BuildEmailHtml(subject, body, absoluteActionUrl);
                if (_settings.Environment == 0)
                    await SendViaSmtpAsync(to, subject, htmlBody);
                else
                    await SendViaSendGridAsync(to, subject, htmlBody);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notification email to {Email} failed", to);
            }
        });
    }

    /// <summary>
    /// The notification e-mail. Title and body are plain text (they are shown as text in the app too) and can
    /// carry user input — a rejection or reversal reason, a supplier's name — so they are HTML-encoded here, as
    /// is the link; otherwise anyone who can type such a reason could put markup and links into every
    /// recipient's mail (security audit F3).
    /// </summary>
    internal static string BuildEmailHtml(string title, string body, string? actionUrl)
    {
        var safeTitle = System.Net.WebUtility.HtmlEncode(title ?? string.Empty);
        var safeBody  = System.Net.WebUtility.HtmlEncode(body ?? string.Empty);
        var safeUrl   = actionUrl is null ? null : System.Net.WebUtility.HtmlEncode(actionUrl);
        return BuildEncodedEmailHtml(safeTitle, safeBody, safeUrl);
    }

    private static string BuildEncodedEmailHtml(string title, string body, string? actionUrl) => $"""
        <!DOCTYPE html>
        <html>
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
        <body style="margin:0;padding:0;background:#f1f5f9;font-family:Arial,sans-serif;">
          <table width="100%" cellpadding="0" cellspacing="0" style="background:#f1f5f9;padding:32px 0;">
            <tr><td align="center">
              <table width="560" cellpadding="0" cellspacing="0" style="background:#ffffff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,.08);">
                <tr><td style="background:linear-gradient(135deg,#0f172a 0%,#1e293b 100%);padding:24px 32px;">
                  <table width="100%"><tr>
                    <td><span style="display:inline-block;background:#10b981;color:#fff;font-weight:700;font-size:13px;padding:6px 14px;border-radius:4px;letter-spacing:.5px;">SMS ALERT</span></td>
                    <td align="right"><span style="color:#94a3b8;font-size:11px;">Supply Management System</span></td>
                  </tr></table>
                </td></tr>
                <tr><td style="padding:32px;">
                  <h2 style="margin:0 0 12px;color:#0f172a;font-size:20px;">{title}</h2>
                  <p style="margin:0 0 24px;color:#475569;font-size:15px;line-height:1.6;">{body}</p>
                  {(actionUrl is not null ? $"""<a href="{actionUrl}" style="display:inline-block;background:#10b981;color:#fff;text-decoration:none;padding:10px 24px;border-radius:6px;font-weight:600;font-size:14px;">View Details</a>""" : "")}
                </td></tr>
                <tr><td style="background:#f8fafc;padding:16px 32px;border-top:1px solid #e2e8f0;">
                  <p style="margin:0;color:#94a3b8;font-size:12px;">This is an automated notification from the Supply Management System. Please do not reply to this email.</p>
                </td></tr>
              </table>
            </td></tr>
          </table>
        </body></html>
        """;

    private async Task SendViaSmtpAsync(string to, string subject, string htmlBody)
    {
        using var client = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
        {
            Credentials = new NetworkCredential(_settings.SmtpUser, _settings.SmtpPass),
            EnableSsl   = true
        };
        var from = string.IsNullOrWhiteSpace(_settings.SmtpFromEmail) ? _settings.AppSupportEmail : _settings.SmtpFromEmail;
        using var msg = new MailMessage(from, to, subject, htmlBody) { IsBodyHtml = true };
        await client.SendMailAsync(msg);
        _logger.LogInformation("Notification email sent via SMTP to {Email}", to);
    }

    private async Task SendViaSendGridAsync(string to, string subject, string htmlBody)
    {
        var sgClient = new SendGridClient(_settings.SendGridApiKey);
        var fromAddr = string.IsNullOrWhiteSpace(_settings.SmtpFromEmail) ? _settings.AppSupportEmail : _settings.SmtpFromEmail;
        var msg      = MailHelper.CreateSingleEmail(new EmailAddress(fromAddr), new EmailAddress(to), subject, null, htmlBody);
        var response = await sgClient.SendEmailAsync(msg);
        if ((int)response.StatusCode >= 400)
            _logger.LogError("SendGrid {Status} sending notification email to {Email}", response.StatusCode, to);
        else
            _logger.LogInformation("Notification email sent via SendGrid to {Email}", to);
    }
}

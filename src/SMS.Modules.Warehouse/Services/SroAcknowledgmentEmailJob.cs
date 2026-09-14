using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Warehouse.Services;

// REQ-3.x — unlike SMS.Modules.Demand's RfqEmailDispatchJob (misleadingly named — it's actually
// called synchronously in-request), this one is a genuine Hangfire background job, enqueued via
// IBackgroundJobClient from SroRepository.DispatchAsync, matching how this module already uses
// _jobs for ISroEscalationJob.
internal sealed class SroAcknowledgmentEmailJob : ISroAcknowledgmentEmailJob
{
    private readonly WarehouseDbContext _wh;
    private readonly INotificationService _email;
    private readonly ILogger<SroAcknowledgmentEmailJob> _logger;

    public SroAcknowledgmentEmailJob(WarehouseDbContext wh, INotificationService email, ILogger<SroAcknowledgmentEmailJob> logger)
    {
        _wh     = wh;
        _email  = email;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 3)]
    public async Task SendAsync(int linkId)
    {
        var link = await _wh.SroAcknowledgmentLinks
            .Include(l => l.ReturnOrder)
            .FirstOrDefaultAsync(l => l.Id == linkId);

        if (link is null)
        {
            _logger.LogWarning("SroAcknowledgmentEmail: link {Id} not found — skipping", linkId);
            return;
        }

        if (string.IsNullOrWhiteSpace(link.SupplierEmail) || string.IsNullOrWhiteSpace(link.PortalLinkUrl))
        {
            _logger.LogWarning("SroAcknowledgmentEmail: link {Id} has no email or portal URL — skipping", linkId);
            return;
        }

        if (link.EmailSentAt.HasValue)
        {
            _logger.LogInformation("SroAcknowledgmentEmail: link {Id} already dispatched at {SentAt} — skipping", linkId, link.EmailSentAt);
            return;
        }

        var sro         = link.ReturnOrder;
        var expiryDate  = link.ExpiresAt.ToString("dd MMM yyyy");
        var subject     = $"Return Order Dispatched — {sro.ReturnNumber} — Please Confirm Receipt";
        var html        = BuildHtml(sro.ReturnNumber, sro.RmaNumber, sro.DispatchCarrier,
            sro.DispatchTrackingRef, sro.DispatchDate, expiryDate, link.PortalLinkUrl);

        await _email.SendEmailAsync(link.SupplierEmail, subject, html);

        link.EmailSentAt = DateTime.UtcNow;
        await _wh.SaveChangesAsync();

        _logger.LogInformation(
            "SRO acknowledgment email dispatched to {Email} for {Number} (link {Id})",
            link.SupplierEmail, sro.ReturnNumber, linkId);
    }

    private static string BuildHtml(
        string sroNumber, string? rmaNumber, string? carrier, string? trackingRef,
        DateTime? dispatchDate, string expiryDate, string portalUrl) => $"""
        <!DOCTYPE html>
        <html>
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
        <body style="margin:0;padding:0;background:#f1f5f9;font-family:Arial,sans-serif;">
          <table width="100%" cellpadding="0" cellspacing="0" style="background:#f1f5f9;padding:32px 0;">
            <tr><td align="center">
              <table width="560" cellpadding="0" cellspacing="0" style="background:#ffffff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,.08);">
                <tr><td style="background:linear-gradient(135deg,#0f172a 0%,#1e293b 100%);padding:24px 32px;">
                  <table width="100%"><tr>
                    <td><span style="display:inline-block;background:#f59e0b;color:#fff;font-weight:700;font-size:13px;padding:6px 14px;border-radius:4px;letter-spacing:.5px;">RETURN ORDER DISPATCHED</span></td>
                    <td align="right"><span style="color:#94a3b8;font-size:11px;">Supply Management System</span></td>
                  </tr></table>
                </td></tr>
                <tr><td style="padding:32px;">
                  <h2 style="margin:0 0 8px;color:#0f172a;font-size:20px;">A return has been dispatched to you</h2>
                  <p style="margin:0 0 24px;color:#475569;font-size:15px;line-height:1.6;">
                    Return order <strong>{sroNumber}</strong> has been dispatched. Please confirm receipt using the link below once the items arrive.
                  </p>
                  <table cellpadding="0" cellspacing="0" style="margin-bottom:24px;background:#f8fafc;border:1px solid #e2e8f0;border-radius:6px;width:100%;">
                    <tr>
                      <td style="color:#64748b;font-size:13px;padding:10px 16px;">Return Number</td>
                      <td style="color:#0f172a;font-weight:600;font-size:13px;padding:10px 16px;">{sroNumber}</td>
                    </tr>
                    <tr style="border-top:1px solid #e2e8f0;">
                      <td style="color:#64748b;font-size:13px;padding:10px 16px;">RMA Number</td>
                      <td style="color:#0f172a;font-weight:600;font-size:13px;padding:10px 16px;">{rmaNumber ?? "—"}</td>
                    </tr>
                    <tr style="border-top:1px solid #e2e8f0;">
                      <td style="color:#64748b;font-size:13px;padding:10px 16px;">Carrier</td>
                      <td style="color:#0f172a;font-weight:600;font-size:13px;padding:10px 16px;">{carrier ?? "—"}</td>
                    </tr>
                    <tr style="border-top:1px solid #e2e8f0;">
                      <td style="color:#64748b;font-size:13px;padding:10px 16px;">Tracking Reference</td>
                      <td style="color:#0f172a;font-weight:600;font-size:13px;padding:10px 16px;">{trackingRef ?? "—"}</td>
                    </tr>
                    <tr style="border-top:1px solid #e2e8f0;">
                      <td style="color:#64748b;font-size:13px;padding:10px 16px;">Dispatch Date</td>
                      <td style="color:#0f172a;font-weight:600;font-size:13px;padding:10px 16px;">{dispatchDate?.ToString("dd MMM yyyy") ?? "—"}</td>
                    </tr>
                  </table>
                  <a href="{portalUrl}" style="display:inline-block;background:#f59e0b;color:#fff;text-decoration:none;padding:12px 28px;border-radius:6px;font-weight:600;font-size:14px;">Confirm Receipt</a>
                </td></tr>
                <tr><td style="background:#f8fafc;padding:16px 32px;border-top:1px solid #e2e8f0;">
                  <p style="margin:0;color:#94a3b8;font-size:12px;">This link is unique to this return and can only be used once. It expires on {expiryDate}.</p>
                </td></tr>
              </table>
            </td></tr>
          </table>
        </body></html>
        """;
}

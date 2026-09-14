using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Modules.Warehouse.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Warehouse.Services;

internal sealed class SroAckSubmissionService : ISroAckSubmissionService
{
    private readonly ISroAckValidationService _validation;
    private readonly WarehouseDbContext _wh;
    private readonly IBackgroundJobClient _jobs;
    private readonly IAuditService _audit;
    private readonly INotificationService _notif;

    public SroAckSubmissionService(
        ISroAckValidationService validation,
        WarehouseDbContext wh,
        IBackgroundJobClient jobs,
        IAuditService audit,
        INotificationService notif)
    {
        _validation = validation;
        _wh         = wh;
        _jobs       = jobs;
        _audit      = audit;
        _notif      = notif;
    }

    public async Task<SroAcknowledgeResult> SubmitAsync(
        string rawToken, SroAcknowledgeRequest request, string clientIp)
    {
        // Re-validate — defends against the two-tab race condition, same as the RFQ portal.
        var validationResult = await _validation.ValidateAsync(rawToken);

        if (validationResult is not SroAckValidationResult.Valid)
        {
            return validationResult switch
            {
                SroAckValidationResult.Consumed => new SroAcknowledgeResult { Status = "CONSUMED" },
                SroAckValidationResult.Expired  => new SroAcknowledgeResult { Status = "EXPIRED"  },
                _                                => new SroAcknowledgeResult { Status = "INVALID"  }
            };
        }

        // Retrieve the already-tracked link entity — ValidateAsync loaded and saved it above.
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        var tokenHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        var link      = _wh.SroAcknowledgmentLinks.Local.First(l => l.TokenHash == tokenHash);
        var sro       = link.ReturnOrder;
        var now       = DateTime.UtcNow;

        // Whichever path — this portal, or the internal "Confirm Supplier Receipt" button —
        // reaches DISPATCHED→SUPPLIER_RECEIVED first wins; the other is a safe no-op. The
        // acknowledgment itself is still recorded on the link either way, so T3.9's display
        // always reflects what the supplier actually said.
        var advancedStatus = sro.Status == "DISPATCHED";

        var strategy = _wh.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _wh.Database.BeginTransactionAsync();
            try
            {
                if (advancedStatus)
                {
                    sro.Status       = "SUPPLIER_RECEIVED";
                    sro.ModifiedDate = now;
                }

                link.Status          = "CONSUMED";
                link.ConsumedAt      = now;
                link.ConsumedIp      = clientIp;
                link.AckRemarks      = request.Remarks;
                link.AckReceivedDate = request.ReceivedDate;

                await _wh.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });

        await _audit.LogAsync(null, "Supplier (portal)", "WAREHOUSE", "ACKNOWLEDGE", "SRO", sro.UUID,
            ipAddress: clientIp,
            fieldChanged: advancedStatus ? "Status" : null,
            oldValue: advancedStatus ? "DISPATCHED" : null,
            newValue: advancedStatus ? "SUPPLIER_RECEIVED" : null,
            notes: request.Remarks);

        if (advancedStatus)
        {
            // Same follow-up escalation window ConfirmReceiptAsync schedules for the internal path.
            _jobs.Schedule<ISroEscalationJob>(j => j.EscalateIfOverdue(sro.UUID), TimeSpan.FromDays(14));

            try
            {
                await _notif.TryCreateAsync(new NotificationRequest(
                    UserId: sro.CreatedBy, Type: "SRO_RECEIVED", Title: "Return Received by Supplier",
                    Message: $"Supplier has acknowledged receipt of returned goods for {sro.ReturnNumber}.",
                    Category: "Warehouse", EntityType: "SRO", EntityUuid: sro.UUID.ToString(),
                    NavigationUrl: $"/portal/pages/warehouse/sro/{sro.UUID}",
                    CreatedBy: 0));
            }
            catch
            {
                // Best-effort — the acknowledgment itself already committed successfully above.
            }
        }

        return new SroAcknowledgeResult
        {
            Status         = "ACKNOWLEDGED",
            SroNumber      = sro.ReturnNumber,
            AcknowledgedAt = now
        };
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;

namespace SMS.Modules.Demand.Services;

internal sealed class RfqSubmissionService : IRfqSubmissionService
{
    private readonly IRfqLinkValidationService _validation;
    private readonly DemandDbContext _db;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<RfqSubmissionService> _logger;

    public RfqSubmissionService(
        IRfqLinkValidationService validation,
        DemandDbContext db,
        IBackgroundJobClient jobs,
        ILogger<RfqSubmissionService> logger)
    {
        _validation = validation;
        _db         = db;
        _jobs       = jobs;
        _logger     = logger;
    }

    public async Task<RfqSubmitResult> SubmitAsync(
        string rawToken, RfqSubmitRequest request, string clientIp)
    {
        // Re-validate — defends against second-tab race condition (FSD §5.3)
        var validationResult = await _validation.ValidateAsync(rawToken);

        if (validationResult is not ValidationResult.Valid v)
        {
            return validationResult switch
            {
                ValidationResult.Consumed => new RfqSubmitResult { Status = "CONSUMED" },
                ValidationResult.Expired  => new RfqSubmitResult { Status = "EXPIRED"  },
                _                         => new RfqSubmitResult { Status = "INVALID"  }
            };
        }

        // Retrieve the already-tracked link entity from the change tracker
        // ValidateAsync loaded and saved it; it remains tracked in _db.
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        var tokenHash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        var link      = _db.RfqAccessLinks.Local.First(l => l.TokenHash == tokenHash);

        return await HandleSubmissionAsync(link, v.Payload, request, clientIp);
    }

    private async Task<RfqSubmitResult> HandleSubmissionAsync(
        RfqAccessLink link, RfqPublicPayload payload,
        RfqSubmitRequest request, string clientIp)
    {
        var now              = DateTime.UtcNow;
        var quotationLines   = link.Quotation.Lines.ToList();
        var submittedByUuid  = request.Lines.ToDictionary(l => l.LineUuid);

        // Validate every line the vendor is actually offering to supply — a line they've marked
        // CanSupply=false (or omitted) is a deliberate decline, not something to validate.
        var errors = new List<string>();
        foreach (var ql in quotationLines)
        {
            if (!submittedByUuid.TryGetValue(ql.UUID, out var sub) || !sub.CanSupply)
                continue;

            var hasValidPrice = decimal.TryParse(sub.UnitPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) && price > 0;
            var hasValidDays  = int.TryParse(sub.DeliveryDays, out var days) && days > 0;

            if (!hasValidPrice)
                errors.Add($"Line {ql.LineNo} ({ql.ItemDescription}): unit price must be a positive number.");
            if (!hasValidDays)
                errors.Add($"Line {ql.LineNo} ({ql.ItemDescription}): delivery days must be a positive whole number.");
        }

        if (errors.Count > 0)
            return new RfqSubmitResult { Status = "VALIDATION_ERROR", ValidationErrors = errors };

        var responseLines = quotationLines.Select(ql =>
        {
            submittedByUuid.TryGetValue(ql.UUID, out var sub);

            // A missing line is treated the same as an explicit CanSupply=false decline.
            var canSupply = sub?.CanSupply ?? false;

            decimal.TryParse(sub?.UnitPrice, NumberStyles.Any,
                CultureInfo.InvariantCulture, out var unitPrice);
            int.TryParse(sub?.DeliveryDays, out var leadDays);

            if (!canSupply) { unitPrice = 0; leadDays = 0; }

            return new VendorResponseLine
            {
                UUID            = Guid.NewGuid(),
                QuotationLineId = ql.Id,
                NetUnitPrice    = unitPrice,
                Quantity        = ql.Quantity,
                LineTotal       = unitPrice * ql.Quantity,
                LeadTimeDays    = leadDays > 0 ? leadDays : null,
                Notes           = sub?.Remarks,
                CanSupply       = canSupply
            };
        }).ToList();

        var vendorResponse = new VendorResponse
        {
            UUID         = request.ResponseUuid is { } rid && rid != Guid.Empty ? rid : Guid.NewGuid(),
            QuotationId  = link.QuotationId,
            SupplierId   = link.SupplierId,
            SupplierName = payload.SupplierName ?? string.Empty,
            Status       = "PENDING",
            TotalAmount  = responseLines.Sum(l => l.LineTotal),
            ResponseDate = now,
            Notes        = request.Notes,
            CreatedBy    = 0,    // portal submission — no authenticated user
            CreatedDate  = now,
            Lines        = responseLines
        };

        // DemandDbContext is configured with EnableRetryOnFailure, so a manually-opened transaction
        // (BeginTransactionAsync outside an execution strategy) throws — EF Core requires the whole
        // retriable unit to run through CreateExecutionStrategy().ExecuteAsync(). Same pattern already
        // used by EfGrnInventoryPoster / InventoryRepository / MaterialReturnService / MivService.
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                _db.VendorResponses.Add(vendorResponse);
                await _db.SaveChangesAsync();

                link.Status     = "CONSUMED";
                link.ConsumedAt = now;
                link.ConsumedIp = clientIp;
                link.ResponseId = vendorResponse.Id;
                await _db.SaveChangesAsync();

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });

        // The submission itself already committed above — a notification failure here must never
        // make the vendor see "submission failed" for a response that actually saved successfully.
        try
        {
            _jobs.Enqueue<RfqResponseNotificationJob>(j =>
                j.RunAsync(link.CreatedBy, link.Quotation.UUID, link.Quotation.QuotationNumber, vendorResponse.SupplierName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to enqueue RfqResponseNotificationJob for vendor response {ResponseUuid} (quotation {QuotationNumber}) — submission itself already committed successfully.",
                vendorResponse.UUID, link.Quotation.QuotationNumber);
        }

        return new RfqSubmitResult
        {
            Status          = "SUBMITTED",
            ResponseUuid    = vendorResponse.UUID,
            QuotationNumber = link.Quotation.QuotationNumber,
            SupplierName    = vendorResponse.SupplierName,
            SubmittedAt     = now
        };
    }
}

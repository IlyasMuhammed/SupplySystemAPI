using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Modules.Warehouse.Models;

namespace SMS.Modules.Warehouse.Services;

internal sealed class SroAckValidationService : ISroAckValidationService
{
    private readonly WarehouseDbContext _wh;

    public SroAckValidationService(WarehouseDbContext wh) => _wh = wh;

    public async Task<SroAckValidationResult> ValidateAsync(string rawToken)
    {
        // Hash the incoming token exactly as SroAckTokenService did during generation
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        var tokenHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // (1) Look up by hash — load the return order + lines in one round-trip
        var link = await _wh.SroAcknowledgmentLinks
            .Include(l => l.ReturnOrder)
                .ThenInclude(o => o.Lines.OrderBy(x => x.LineNo))
            .FirstOrDefaultAsync(l => l.TokenHash == tokenHash);

        if (link is null) return new SroAckValidationResult.Invalid();

        // (2) Consumed takes precedence — check before expiry
        if (link.ConsumedAt is not null) return new SroAckValidationResult.Consumed();

        // (3) Expired
        if (DateTime.UtcNow > link.ExpiresAt)
        {
            if (link.Status != "EXPIRED")
            {
                link.Status = "EXPIRED";
                await _wh.SaveChangesAsync();
            }
            return new SroAckValidationResult.Expired();
        }

        // (4) Valid — set FirstOpenedAt once; increment AccessCount every visit
        if (link.FirstOpenedAt is null)
        {
            link.FirstOpenedAt = DateTime.UtcNow;
            link.Status        = "ACCESSED";
        }
        link.AccessCount++;
        await _wh.SaveChangesAsync();

        return new SroAckValidationResult.Valid(BuildPayload(link));
    }

    private static SroAckPublicPayload BuildPayload(SroAcknowledgmentLink link)
    {
        var sro = link.ReturnOrder;

        return new SroAckPublicPayload
        {
            SroNumber           = sro.ReturnNumber,
            SupplierName        = sro.SupplierName,
            RmaNumber           = sro.RmaNumber,
            DispatchDate        = sro.DispatchDate,
            DispatchCarrier     = sro.DispatchCarrier,
            DispatchTrackingRef = sro.DispatchTrackingRef,
            ExpiresAt           = link.ExpiresAt,
            Lines = sro.Lines.Select(l => new SroAckLineModel
            {
                Uuid            = l.UUID,
                LineNo          = l.LineNo,
                ItemDescription = l.ItemDescription,
                UnitOfMeasure   = l.UnitOfMeasure,
                QtyToReturn     = l.QtyToReturn,
                Condition       = l.Condition
            }).ToList()
        };
    }
}

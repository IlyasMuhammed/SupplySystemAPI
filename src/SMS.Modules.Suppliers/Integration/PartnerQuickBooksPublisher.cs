using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Suppliers.Data;

namespace SMS.Modules.Suppliers.Integration;

/// <summary>
/// What the partner services call once their own save has committed: re-reads the partner as it now
/// stands and hands it to the QuickBooks gateway in every role it holds.
/// <para>
/// <b>Never throws.</b> The partner is saved whatever QuickBooks makes of it; a failure here is logged with
/// the partner's id and the gateway's hourly reconciliation offers the partner again.
/// </para>
/// </summary>
internal sealed class PartnerQuickBooksPublisher
{
    private readonly SuppliersDbContext                  _db;
    private readonly PartnerQuickBooksSource             _source;
    private readonly ILogger<PartnerQuickBooksPublisher> _log;

    public PartnerQuickBooksPublisher(
        SuppliersDbContext db, PartnerQuickBooksSource source, ILogger<PartnerQuickBooksPublisher> log)
    {
        _db     = db;
        _source = source;
        _log    = log;
    }

    public async Task PublishAsync(Guid partnerUuid)
    {
        try
        {
            // Deleted rows included on purpose: a delete is a deactivation QuickBooks should hear about.
            var partner = await _db.BusinessPartners.AsNoTracking()
                .FirstOrDefaultAsync(p => p.UUID == partnerUuid);

            if (partner is null) return;

            await _source.SendRolesAsync(partner);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Business partner {PartnerUuid} was saved, but could not be handed to the QuickBooks gateway; " +
                "the hourly reconciliation will offer it again.",
                partnerUuid);
        }
    }
}

using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

// Receives Twilio's async delivery-status callback (relayed by Notifications' webhook
// controller) and applies it to whichever RfqAccessLink the original send was for. A no-op if
// the message ID isn't one of ours (e.g. it belongs to a PO WhatsApp send instead).
internal sealed class DemandWhatsAppStatusHandler : IWhatsAppStatusUpdateHandler
{
    private readonly DemandDbContext _db;

    public DemandWhatsAppStatusHandler(DemandDbContext db) => _db = db;

    public async Task HandleStatusUpdateAsync(string providerMessageId, string status, string? errorCode, string? errorMessage)
    {
        var link = await _db.RfqAccessLinks
            .FirstOrDefaultAsync(l => l.WhatsAppProviderMessageId == providerMessageId);

        if (link is null) return;

        link.WhatsAppStatus = status;
        link.WhatsAppStatusUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }
}

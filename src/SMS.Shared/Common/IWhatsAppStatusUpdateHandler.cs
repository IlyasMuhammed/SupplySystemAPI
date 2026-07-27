namespace SMS.Shared.Common;

// Implemented by whichever module owns the domain entity a WhatsApp send was made on behalf
// of (e.g. Demand owns RfqAccessLink) — lets the Notifications module's Twilio status webhook
// propagate a real delivery outcome back onto that entity without Notifications depending on
// Demand directly. Multiple handlers may be registered; each should no-op on message IDs it
// doesn't recognize (i.e. look up by ProviderMessageId and return quietly if not found).
public interface IWhatsAppStatusUpdateHandler
{
    Task HandleStatusUpdateAsync(string providerMessageId, string status, string? errorCode, string? errorMessage);
}

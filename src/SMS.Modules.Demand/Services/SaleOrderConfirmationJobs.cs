using Microsoft.Extensions.Logging;

namespace SMS.Modules.Demand.Services;

// A29-P4-03/P4-04 §4.3/§4.5 — what ConfirmAsync and CancelAsync "delegate to" rather than do
// themselves. Genuine Hangfire jobs (registered in DI, safe to actually enqueue and run today).
// IAutoPoCreationJob's implementation moved to AutoPoCreationJob.cs once Section 6's pieces existed.
//
// SaleOrderEmailJob originally also carried a SendConfirmationEmailAsync placeholder; A29-P4-07
// built the real ISaleOrderEmailService.SendConfirmationAsync and SaleOrderService.ConfirmAsync
// now calls that instead (see SaleOrderService.cs). SendCancellationEmailAsync stays a
// placeholder here — it still has no home in §5.1's own event table (exactly seven events:
// SO_CONFIRMED, PO_CREATED, DROP_SHIP, RESERVED, PO_APPROVED, GRN_RECEIVED, EXPIRING; cancellation
// is not one of them, even though P4-04's own task description asks for a cancellation email) —
// built anyway since a real cancellation notice is clearly useful, and can fold into
// demand.SaleOrderIntimations' event_type enum whenever a task actually extends it for that.

public interface IAutoPoCreationJob
{
    Task CreateForDeficitAsync(Guid saleOrderUuid, Guid saleOrderLineUuid, int userId);
}

public interface ISaleOrderEmailJob
{
    Task SendCancellationEmailAsync(Guid saleOrderUuid, string? reason, int userId);
}

internal sealed class SaleOrderEmailJob : ISaleOrderEmailJob
{
    private readonly ILogger<SaleOrderEmailJob> _logger;
    public SaleOrderEmailJob(ILogger<SaleOrderEmailJob> logger) => _logger = logger;

    public Task SendCancellationEmailAsync(Guid saleOrderUuid, string? reason, int userId)
    {
        _logger.LogInformation(
            "Cancellation email for sale order {SaleOrderUuid} is not yet implemented (no §5.1 event " +
            "type covers cancellation yet). No email was sent. Reason given: {Reason}",
            saleOrderUuid, reason ?? "(none)");
        return Task.CompletedTask;
    }
}

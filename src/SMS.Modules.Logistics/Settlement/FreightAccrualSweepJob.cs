using Microsoft.Extensions.Logging;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Settlement;

/// <summary>
/// Accrues what has moved, and writes back what was cancelled.
/// <para>
/// Nightly rather than on the status change itself. A consignment reaches PICKED_UP through a
/// carrier webhook, a poll, or somebody pressing a button, and hanging an accrual off every one of
/// those paths is three places for it to be missed. One sweep that asks "what has moved and is not
/// accrued" catches all of them, including anything that moved while this was switched off.
/// </para>
/// </summary>
internal sealed class FreightAccrualSweepJob
{
    internal const string RecurringJobId = "logistics-freight-accrual-sweep";

    /// <summary>
    /// Accruals are recorded against the system rather than a person: nobody presses a button to
    /// make one, and attributing them to whoever happened to be logged in would be a lie.
    /// </summary>
    internal const int SystemUserId = 0;

    private readonly IFreightAccrualService          _accruals;
    private readonly ICodReconciliationService       _cod;
    private readonly ITenantContext                  _tenant;
    private readonly ILogger<FreightAccrualSweepJob> _log;

    public FreightAccrualSweepJob(
        IFreightAccrualService accruals, ICodReconciliationService cod,
        ITenantContext tenant, ILogger<FreightAccrualSweepJob> log)
    {
        _accruals = accruals;
        _cod      = cod;
        _tenant   = tenant;
        _log      = log;
    }

    public async Task RunAsync()
    {
        var result = await _accruals.SweepAsync(SystemUserId);

        // The other side of the same ledger, on the same schedule: cash a carrier has taken on our
        // behalf and not yet passed on. Opened here for the same reason accruals are — a
        // consignment reaches a collectable state by several routes, and one sweep catches them all.
        var codOpened = await _cod.OpenOutstandingAsync(SystemUserId);

        if (codOpened > 0)
            _log.LogInformation(
                "Opened {Count} cash-on-delivery record(s) for {Organization}.",
                codOpened, _tenant.OrganizationId);

        if (result.Accrued > 0 || result.Reversed > 0)
            _log.LogInformation(
                "Freight accrual sweep for {Organization}: accrued {Accrued}, reversed {Reversed}.",
                _tenant.OrganizationId, result.Accrued, result.Reversed);

        // Logged at warning because it is money missing from a balance, not a housekeeping note.
        if (result.CouldNotAccrue.Count > 0)
            _log.LogWarning(
                "{Count} consignment(s) have moved without ever being priced and cannot be accrued: {Numbers}",
                result.CouldNotAccrue.Count,
                string.Join(", ", result.CouldNotAccrue.Take(20).Select(c => c.ConsignmentNumber)));
    }
}

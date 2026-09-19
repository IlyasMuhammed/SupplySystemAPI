using Microsoft.Extensions.Logging;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Visibility;

/// <summary>
/// Turns what carriers report into things somebody can act on: work, where a scan says something
/// went wrong, and evidence, where one says the goods arrived.
/// <para>
/// A sweep rather than a hook on the recorder, for the reason <see cref="Settlement.FreightAccrualSweepJob"/>
/// gives: a tracking event arrives through a webhook, a poll, or a backfill, and hanging this off
/// every path is three places for it to be missed. One question — "which of these events has nothing
/// against it" — catches all of them, including events that landed while this was off.
/// </para>
/// </summary>
internal sealed class DeliveryExceptionSweepJob
{
    internal const string RecurringJobId = "logistics-delivery-exception-sweep";

    /// <summary>
    /// Raised against the system, not a person. Nobody pressed a button, and attributing it to
    /// whoever happened to be logged in would be a lie the queue then shows to their colleagues.
    /// </summary>
    internal const int SystemUserId = 0;

    private readonly IDeliveryExceptionService          _exceptions;
    private readonly IDeliveryProofService              _proofs;
    private readonly ITenantContext                     _tenant;
    private readonly ILogger<DeliveryExceptionSweepJob> _log;

    public DeliveryExceptionSweepJob(
        IDeliveryExceptionService exceptions, IDeliveryProofService proofs,
        ITenantContext tenant, ILogger<DeliveryExceptionSweepJob> log)
    {
        _exceptions = exceptions;
        _proofs     = proofs;
        _tenant     = tenant;
        _log        = log;
    }

    public async Task RunAsync()
    {
        var fromCarrier = await _exceptions.SweepFromTrackingAsync(SystemUserId);

        // What T-40 flagged as gone quiet. Until this ran, StuckSince was a column two screens could
        // show and nothing could act on.
        var fromSilence = await _exceptions.SweepStuckAsync(SystemUserId);

        // The good news, on the same schedule (T-61): a DELIVERED scan becomes the proof, so an
        // API-carrier delivery is evidenced without anybody typing anything.
        var proofs = await _proofs.SweepFromTrackingAsync(SystemUserId);

        if (fromCarrier > 0 || fromSilence > 0 || proofs > 0)
            _log.LogInformation(
                "Raised {FromCarrier} delivery exception(s) from carrier events and {FromSilence} "
              + "from consignments gone quiet, and recorded {Proofs} proof(s) of delivery, for "
              + "{Organization}.",
                fromCarrier, fromSilence, proofs, _tenant.OrganizationId);
    }
}

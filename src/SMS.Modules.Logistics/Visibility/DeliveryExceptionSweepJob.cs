using Microsoft.Extensions.Logging;
using SMS.Modules.Logistics.Services;
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
    private readonly IDeliveryProgressService           _progress;
    private readonly ITenantContext                     _tenant;
    private readonly ILogger<DeliveryExceptionSweepJob> _log;

    public DeliveryExceptionSweepJob(
        IDeliveryExceptionService exceptions, IDeliveryProofService proofs,
        IDeliveryProgressService progress, ITenantContext tenant, ILogger<DeliveryExceptionSweepJob> log)
    {
        _exceptions = exceptions;
        _proofs     = proofs;
        _progress   = progress;
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

        // And the delivery itself: what the carrier says has happened to the parcel happens to the
        // order it was raised for, which is what lets the sale order move and the customer be invoiced.
        var advanced = await _progress.SweepAsync(SystemUserId);

        if (fromCarrier > 0 || fromSilence > 0 || proofs > 0 || advanced > 0)
            _log.LogInformation(
                "Raised {FromCarrier} delivery exception(s) from carrier events and {FromSilence} "
              + "from consignments gone quiet, recorded {Proofs} proof(s) of delivery, and moved "
              + "{Advanced} delivery order(s) along with their consignments, for {Organization}.",
                fromCarrier, fromSilence, proofs, advanced, _tenant.OrganizationId);
    }
}

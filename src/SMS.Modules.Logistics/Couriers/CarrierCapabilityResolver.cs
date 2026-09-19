using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Couriers;

/// <summary>
/// Narrows what an adapter <em>can</em> do by what an account is <em>configured</em> to do.
/// </summary>
internal static class CarrierCapabilityResolver
{
    /// <summary>
    /// The capabilities that actually apply to a booking on this account.
    /// <para>
    /// <b>Strictly narrowing.</b> An account may switch something off — no cash-on-delivery
    /// contract, labels not purchased — but it can never switch something on that the adapter
    /// cannot do. Allowing that would let configuration promise a capability no code implements,
    /// and the failure would surface as a carrier refusal that nobody can explain. So each flag is
    /// <c>adapter AND (account ?? true)</c>, and a null on the account means "whatever the adapter
    /// says" rather than "yes".
    /// </para>
    /// </summary>
    internal static CourierCapabilities Resolve(
        CourierCapabilities adapter, CarrierAccount? account)
    {
        if (account is null) return adapter;

        return adapter with
        {
            SupportsCod           = adapter.SupportsCod           && (account.CodEnabled           ?? true),
            SupportsLabels        = adapter.SupportsLabels        && (account.LabelsEnabled        ?? true),
            SupportsTracking      = adapter.SupportsTracking      && (account.TrackingEnabled      ?? true),
            SupportsCancellation  = adapter.SupportsCancellation  && (account.CancellationEnabled  ?? true),
            SupportsPickupBooking = adapter.SupportsPickupBooking && (account.PickupBookingEnabled ?? true)

            // SupportsBooking, SupportsMultiPiece and HonoursIdempotencyKey are not negotiable per
            // account. The first is what an adapter is for; the other two are statements about how
            // the carrier behaves, and configuration cannot change how a carrier behaves.
        };
    }
}

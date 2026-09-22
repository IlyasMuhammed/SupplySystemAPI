using SMS.Modules.Logistics.Domain;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Couriers.Booking;

/// <summary>
/// Turns a consignment into what a carrier is asked for.
/// <para>
/// Everything that can make a booking impossible is checked here, <em>before</em> anything is sent
/// and before the consignment is moved to BOOKING — so a missing address or a COD request to a
/// carrier that does not collect cash is a message on the screen, not a carrier refusal somebody
/// has to interpret.
/// </para>
/// <para>
/// Called twice with the same consignment: once when the booking is requested (to refuse early)
/// and again by the job (to build what is sent). It reads nothing but the consignment graph, so
/// both produce the same request — which is what keeps the ledger's fingerprint stable across a
/// retry.
/// </para>
/// </summary>
/// <remarks>
/// Expects the consignment loaded with its addresses, and its deliveries with their addresses and
/// packages.
/// </remarks>
internal static class CourierBookingRequestFactory
{
    internal static CourierBookingRequest Build(
        Consignment consignment, ResolvedCarrierAccount account, string idempotencyKey)
    {
        var deliveries = consignment.Deliveries
            .OrderBy(d => d.Sequence)
            .Select(d => d.DeliveryOrder)
            .Where(d => !d.IsDelete)
            .ToList();

        if (deliveries.Count == 0)
            throw new BadRequestException(
                $"Consignment {consignment.ConsignmentNumber} carries no deliveries, so there is nothing to book.");

        var shipTo = consignment.ShipToAddress
            ?? SingleAddress(deliveries.Select(d => d.ShipToAddress), "ship-to", consignment.ConsignmentNumber)
            ?? throw new BadRequestException(
                "A carrier cannot book a consignment with nowhere to deliver it. Set the ship-to address " +
                "on the consignment or its delivery.");

        var shipFrom = consignment.ShipFromAddress
            ?? SingleAddress(deliveries.Select(d => d.ShipFromAddress), "ship-from", consignment.ConsignmentNumber)
            ?? throw new BadRequestException(
                "A carrier needs to know where to collect from, and this consignment has no ship-from " +
                "address. Give the delivery's ship-from warehouse an address, city and country under " +
                "Inventory → Warehouses; or, if the deliveries leave from more than one warehouse, split " +
                "them across consignments.");

        // Top-level handling units only. A carton inside a pallet travels inside the pallet, and
        // declaring both would have the carrier expect — and bill for — one more piece than arrives.
        var packages = deliveries
            .SelectMany(d => d.Packages)
            .Where(p => !p.IsVoided && !p.IsDelete && p.ParentPackageId == null)
            .OrderBy(p => p.PackageBarcode, StringComparer.Ordinal)
            .Select(p => new CourierPackage(
                p.PackageBarcode, p.PackageType, p.LengthCm, p.WidthCm, p.HeightCm, p.GrossWeightKg, p.DeclaredValue))
            .ToList();

        if (packages.Count == 0)
            throw new BadRequestException(
                "Nothing on this consignment has been packed. A carrier books handling units, so pack the " +
                "deliveries before booking.");

        var capabilities = account.Capabilities;

        if (!capabilities.SupportsBooking)
            throw new ConflictException(
                $"{account.Provider.DisplayName} cannot book consignments on account '{account.AccountName}'.");

        if (packages.Count > 1 && !capabilities.SupportsMultiPiece)
            throw new BadRequestException(
                $"{account.Provider.DisplayName} books one piece per consignment, and this one has " +
                $"{packages.Count}. Split it, or book it with another carrier.");

        if (consignment.CodAmount is > 0)
        {
            if (!capabilities.SupportsCod)
                throw new BadRequestException(
                    $"This consignment collects cash on delivery, and account '{account.AccountName}' does not " +
                    "offer COD. Remove the COD amount, or book it on an account that collects cash.");

            if (string.IsNullOrWhiteSpace(consignment.CodCurrency))
                throw new BadRequestException("A cash-on-delivery amount needs a currency.");
        }

        var references = deliveries
            .SelectMany(d => new[] { d.DeliveryNumber, d.SourceNumber })
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CourierBookingRequest(
            IdempotencyKey:    idempotencyKey,
            ConsignmentNumber: consignment.ConsignmentNumber,
            ServiceCode:       consignment.CarrierServiceCode ?? account.DefaultServiceCode,
            ShipFrom:          Map(shipFrom),
            ShipTo:            Map(shipTo),
            Packages:          packages,
            FreightTerms:      consignment.FreightTerms,
            CodAmount:         consignment.CodAmount is > 0 ? consignment.CodAmount : null,
            CodCurrency:       consignment.CodAmount is > 0 ? consignment.CodCurrency : null,
            PickupWindowStart: consignment.PickupWindowStart,
            PickupWindowEnd:   consignment.PickupWindowEnd,
            ReferenceNumbers:  references,
            Credentials:       account.Credentials);
    }

    /// <summary>
    /// The one address every delivery agrees on, or null when none has one.
    /// <para>
    /// A courier consignment has one airway bill and so one destination. Deliveries going to
    /// different places cannot share one, and picking the first would put a parcel on a van to the
    /// wrong site.
    /// </para>
    /// </summary>
    private static Address? SingleAddress(IEnumerable<Address?> candidates, string which, string consignmentNumber)
    {
        var distinct = candidates
            .Where(a => a is not null)
            .GroupBy(a => (a!.Line1.Trim().ToUpperInvariant(), a.CityName.Trim().ToUpperInvariant(),
                           (a.PostalCode ?? "").Trim().ToUpperInvariant()))
            .Select(g => g.First())
            .ToList();

        return distinct.Count switch
        {
            0 => null,
            1 => distinct[0],
            _ => throw new BadRequestException(
                $"The deliveries on consignment {consignmentNumber} have {distinct.Count} different {which} " +
                "addresses, and one airway bill can only go to one. Set the consignment's own address, or " +
                "split the deliveries across consignments.")
        };
    }

    private static CourierAddress Map(Address a) => new(
        ContactName:    a.ContactName,
        ContactPhone:   a.ContactPhoneE164 ?? a.ContactPhone,
        ContactEmail:   a.ContactEmail,
        Line1:          a.Line1,
        Line2:          a.Line2,
        City:           a.CityName,
        State:          a.State,
        PostalCode:     a.PostalCode,
        CountryIsoCode: a.CountryIsoCode);
}

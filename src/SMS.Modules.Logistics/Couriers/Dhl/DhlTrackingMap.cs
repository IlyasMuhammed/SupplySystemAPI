using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Couriers.Dhl;

/// <summary>
/// DHL Express's tracking event codes, onto the milestones this system understands.
/// <para>
/// <b>Only what DHL's published sample documents is mapped by code:</b> PU, AF, PL, DF, RR, CR, AR, WC
/// and OK. DHL does not publish the full list, and guessing a code's meaning from memory is how a parcel
/// gets marked delivered that was not — and DELIVERED is what lets an order be invoiced. So that one
/// milestone comes from the code <c>OK</c> and from nothing else.
/// </para>
/// <para>
/// Anything else is kept on the timeline with its own code and words, and given the most cautious
/// milestone its description supports. It never invents a terminal one.
/// </para>
/// </summary>
internal static class DhlTrackingMap
{
    internal static TrackingMilestone MilestoneFor(string? typeCode, string? description)
    {
        switch (typeCode?.Trim().ToUpperInvariant())
        {
            case "PU": return TrackingMilestone.PickedUp;
            case "AF": return TrackingMilestone.ArrivedAtHub;
            case "AR": return TrackingMilestone.ArrivedAtHub;
            case "PL": return TrackingMilestone.InTransit;
            case "DF": return TrackingMilestone.DepartedHub;
            case "WC": return TrackingMilestone.OutForDelivery;
            case "OK": return TrackingMilestone.Delivered;

            // Customs paperwork moving. Real progress, but nothing about where the parcel is.
            case "RR":
            case "CR": return TrackingMilestone.InfoReceived;
        }

        return FromWords(description);
    }

    /// <summary>
    /// For a code DHL has not documented. Deliberately never DELIVERED or RETURNED — those two end a
    /// consignment's life, and a wrong guess cannot be taken back.
    /// </summary>
    private static TrackingMilestone FromWords(string? description)
    {
        var text = description?.ToLowerInvariant() ?? string.Empty;

        if (text.Contains("customs") && (text.Contains("hold") || text.Contains("delay")))
            return TrackingMilestone.CustomsHold;

        if (text.Contains("attempt") || text.Contains("not home") || text.Contains("unsuccessful")
         || text.Contains("recipient not available"))
            return TrackingMilestone.DeliveryAttempted;

        if (text.Contains("return"))
            return TrackingMilestone.ReturnInitiated;

        if (text.Contains("exception") || text.Contains("on hold") || text.Contains("delay")
         || text.Contains("refused") || text.Contains("damaged") || text.Contains("held"))
            return TrackingMilestone.Exception;

        return TrackingMilestone.InfoReceived;
    }
}

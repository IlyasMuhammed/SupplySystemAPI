using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Couriers.Simulator;

/// <summary>
/// What the simulator should pretend happened, selected by the service code on the booking.
/// <para>
/// One mechanism, documented in one place. Encoding scenarios in addresses or reference numbers
/// as well would mean two things to check when a demo does something unexpected.
/// </para>
/// </summary>
internal enum SimulatorScenario
{
    /// <summary>Books, then travels the whole way and arrives.</summary>
    Delivered,

    /// <summary>Books, and is still moving. The common case while a demo is running.</summary>
    InTransit,

    /// <summary>Books, hits a customs hold, then recovers and arrives.</summary>
    Exception,

    /// <summary>Books, fails delivery twice and goes home.</summary>
    Returned,

    /// <summary>The carrier answers, and the answer is no. Final.</summary>
    Refuse,

    /// <summary>The call comes apart. Whether a parcel exists is unknown — the T-36 case.</summary>
    Fail
}

internal static class SimulatorScenarios
{
    /// <summary>
    /// Service codes that select a scenario. Anything else — including null — books normally and
    /// takes a journey derived from the idempotency key, so a demo with no special setup still
    /// shows a mix of parcels at different stages.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, SimulatorScenario> ByServiceCode =
        new Dictionary<string, SimulatorScenario>(StringComparer.OrdinalIgnoreCase)
        {
            ["SIM-DELIVERED"]  = SimulatorScenario.Delivered,
            ["SIM-IN-TRANSIT"] = SimulatorScenario.InTransit,
            ["SIM-EXCEPTION"]  = SimulatorScenario.Exception,
            ["SIM-RETURNED"]   = SimulatorScenario.Returned,
            ["SIM-REFUSE"]     = SimulatorScenario.Refuse,
            ["SIM-FAIL"]       = SimulatorScenario.Fail
        };

    /// <summary>The service codes a UI can offer. Ordered so the list does not move about.</summary>
    internal static IReadOnlyList<string> ServiceCodes =>
        [.. ByServiceCode.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)];

    internal static bool TryParse(string? serviceCode, out SimulatorScenario scenario)
    {
        scenario = default;
        return !string.IsNullOrWhiteSpace(serviceCode)
            && ByServiceCode.TryGetValue(serviceCode.Trim(), out scenario);
    }

    /// <summary>
    /// The journey scenarios, in the order their single-letter codes are assigned. Only these
    /// four can be carried in an airway bill — a refused or failed booking has no airway bill to
    /// carry anything.
    /// </summary>
    private static readonly SimulatorScenario[] Journeys =
    [
        SimulatorScenario.Delivered,
        SimulatorScenario.InTransit,
        SimulatorScenario.Exception,
        SimulatorScenario.Returned
    ];

    /// <summary>The letter written into the airway bill, so tracking needs no stored state.</summary>
    internal static char CodeOf(SimulatorScenario scenario) =>
        (char)('A' + Array.IndexOf(Journeys, scenario));

    internal static bool TryFromCode(char code, out SimulatorScenario scenario)
    {
        var index = code - 'A';

        if (index < 0 || index >= Journeys.Length)
        {
            scenario = default;
            return false;
        }

        scenario = Journeys[index];
        return true;
    }

    /// <summary>A journey picked from the key, so unspecified bookings are still varied.</summary>
    internal static SimulatorScenario FromHash(uint hash) => Journeys[hash % Journeys.Length];

    /// <summary>
    /// The milestones a journey passes through, oldest first.
    /// <para>
    /// Every one is a real <see cref="TrackingMilestone"/>. An adapter inventing its own vocabulary
    /// is the thing T-31's contract suite checks for, and the simulator is the reference other
    /// adapters get compared against — so it has to be exemplary rather than merely passing.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<(TrackingMilestone Milestone, string Description, string Location)>
        JourneyOf(SimulatorScenario scenario) => scenario switch
    {
        SimulatorScenario.InTransit =>
        [
            (TrackingMilestone.InfoReceived,  "Booking received",        "Karachi"),
            (TrackingMilestone.PickedUp,      "Collected from shipper",  "Karachi"),
            (TrackingMilestone.InTransit,     "In transit",              "Sukkur")
        ],

        SimulatorScenario.Exception =>
        [
            (TrackingMilestone.InfoReceived,  "Booking received",        "Karachi"),
            (TrackingMilestone.PickedUp,      "Collected from shipper",  "Karachi"),
            (TrackingMilestone.ArrivedAtHub,  "Arrived at sort facility","Lahore"),
            (TrackingMilestone.CustomsHold,   "Held for documentation",  "Lahore"),
            (TrackingMilestone.InTransit,     "Released, in transit",    "Lahore"),
            (TrackingMilestone.OutForDelivery,"Out for delivery",        "Lahore"),
            (TrackingMilestone.Delivered,     "Delivered",               "Lahore")
        ],

        SimulatorScenario.Returned =>
        [
            (TrackingMilestone.InfoReceived,      "Booking received",       "Karachi"),
            (TrackingMilestone.PickedUp,          "Collected from shipper", "Karachi"),
            (TrackingMilestone.OutForDelivery,    "Out for delivery",       "Lahore"),
            (TrackingMilestone.DeliveryAttempted, "Consignee unavailable",  "Lahore"),
            (TrackingMilestone.DeliveryAttempted, "Second attempt failed",  "Lahore"),
            (TrackingMilestone.ReturnInitiated,   "Returning to shipper",   "Lahore"),
            (TrackingMilestone.Returned,          "Returned to shipper",    "Karachi")
        ],

        // Delivered, and anything unexpected — a straightforward successful journey is the
        // safest thing to fall back on.
        _ =>
        [
            (TrackingMilestone.InfoReceived,   "Booking received",         "Karachi"),
            (TrackingMilestone.PickedUp,       "Collected from shipper",   "Karachi"),
            (TrackingMilestone.ArrivedAtHub,   "Arrived at sort facility", "Multan"),
            (TrackingMilestone.DepartedHub,    "Departed sort facility",   "Multan"),
            (TrackingMilestone.InTransit,      "In transit",               "Lahore"),
            (TrackingMilestone.OutForDelivery, "Out for delivery",         "Lahore"),
            (TrackingMilestone.Delivered,      "Delivered",                "Lahore")
        ]
    };
}

using System.Text.RegularExpressions;
using SMS.Modules.Demand.Models;

namespace SMS.Modules.Demand.Services;

// What a saved sale order policy has to satisfy, and how its copy list is read. The Sale Order
// Settings screen applies the same rules before it sends anything, but the API is the authority: it
// is also reachable without the screen.
internal static class SaleOrderConfigRules
{
    public const int MinReservationHours = 1;
    public const int MaxReservationHours = 8760;
    public const int MaxCopyEmailsLength = 500;

    private static readonly string[] SupplierSelection = ["DEFAULT_SUPPLIER", "BEST_MATCH", "MANUAL"];
    private static readonly string[] ApprovalModes     = ["REQUIRE_WORKFLOW", "AUTO_SEND", "DRAFT_ONLY"];
    private static readonly string[] FulfilmentModes   = ["IN_STOCK", "BACK_TO_BACK", "DROP_SHIP"];

    private static readonly Regex Email = new(@"^[^\s@,;]+@[^\s@,;]+\.[^\s@,;]+$", RegexOptions.Compiled);

    /// <summary>The addresses in a typed list: split on commas, semicolons and spaces, without repeats.</summary>
    public static IReadOnlyList<string> ParseEmails(string? text)
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var part in (text ?? string.Empty).Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            if (seen.Add(part.Trim())) result.Add(part.Trim());
        return result;
    }

    /// <summary>How the list is stored: one comma separated line, or null when there are none.</summary>
    public static string? NormalizeEmails(string? text)
    {
        var emails = ParseEmails(text);
        return emails.Count == 0 ? null : string.Join(", ", emails);
    }

    /// <summary>The first thing wrong with a policy, in words, or null when it can be saved.</summary>
    public static string? Problem(UpdateSaleOrderConfigRequest req)
    {
        if (!SupplierSelection.Contains(req.SupplierSelectionMode))
            return $"'{req.SupplierSelectionMode}' is not a supplier selection mode. Use {string.Join(", ", SupplierSelection)}.";
        if (!ApprovalModes.Contains(req.AutoPoApprovalMode))
            return $"'{req.AutoPoApprovalMode}' is not a purchase order approval mode. Use {string.Join(", ", ApprovalModes)}.";
        if (!FulfilmentModes.Contains(req.DefaultFulfillmentMode))
            return $"'{req.DefaultFulfillmentMode}' is not a fulfilment mode. Use {string.Join(", ", FulfilmentModes)}.";

        if (req.ReservationTtlHours is < MinReservationHours or > MaxReservationHours)
            return $"The reservation hold has to be a whole number of hours from {MinReservationHours} to {MaxReservationHours}.";

        var invalid = ParseEmails(req.IntimationCcEmails).Where(e => !Email.IsMatch(e)).ToList();
        if (invalid.Count > 0)
            return $"These copy addresses are not valid emails: {string.Join(", ", invalid)}.";
        if ((NormalizeEmails(req.IntimationCcEmails)?.Length ?? 0) > MaxCopyEmailsLength)
            return $"The copy addresses are too long. Keep them within {MaxCopyEmailsLength} characters.";

        if (req.DefaultFulfillmentMode == "DROP_SHIP" && !req.DropShipEnabled)
            return "Drop ship cannot be the default fulfilment while drop shipping is switched off.";
        if (!req.ShipmentRequiredDefault && !req.SelfPickupEnabled)
            return "New orders cannot start as customer pickup while customer pickup is switched off.";

        return null;
    }
}

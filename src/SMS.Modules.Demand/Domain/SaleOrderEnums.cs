using System.Reflection;

namespace SMS.Modules.Demand.Domain;

// A29-P3-05 §4.1/§4.2 — mirrors Suppliers' PartnerType/PartnerCode (itself a local copy of
// Logistics' LogisticsCode/[Code] pattern, internal to its own assembly and so not shareable).
// SaleOrder.Status/DeliveryMode and SaleOrderLine.FulfillmentMode/Status persist as plain strings —
// matching every entity in this codebase, PurchaseOrder.Status included — these enums exist purely
// so service code can reason in types instead of magic strings, converting at the boundary via
// EnumCode<T>. One generic converter shared by all four enums here, rather than four hand-written
// copies of the same reflection.

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
internal sealed class CodeAttribute : Attribute
{
    internal string Value { get; }
    internal CodeAttribute(string value) => Value = value;
}

/// <summary>Converts between a [Code]-attributed enum and the string it persists as. Parsing is
/// strict: an unknown code is always a failure, never a guess.</summary>
internal static class EnumCode<TEnum> where TEnum : struct, Enum
{
    private static readonly IReadOnlyDictionary<TEnum, string> ToCode;
    private static readonly IReadOnlyDictionary<string, TEnum> FromCode;

    static EnumCode()
    {
        var toCode   = new Dictionary<TEnum, string>();
        var fromCode = new Dictionary<string, TEnum>(StringComparer.Ordinal);

        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attribute = field.GetCustomAttribute<CodeAttribute>()
                ?? throw new InvalidOperationException(
                    $"{typeof(TEnum).Name}.{field.Name} has no [Code]. Every member of a persisted " +
                    "enum must declare the string it is stored as.");

            var member = (TEnum)field.GetValue(null)!;
            fromCode[attribute.Value] = member;
            toCode[member] = attribute.Value;
        }

        ToCode   = toCode;
        FromCode = fromCode;
    }

    internal static string Of(TEnum value) =>
        ToCode.TryGetValue(value, out var code)
            ? code
            : throw new InvalidOperationException($"{typeof(TEnum).Name} has no code for '{value}'.");

    internal static bool TryParse(string? code, out TEnum value)
    {
        if (!string.IsNullOrWhiteSpace(code) && FromCode.TryGetValue(code, out value))
            return true;

        value = default;
        return false;
    }
}

internal enum SaleOrderStatus
{
    [Code("DRAFT")]               Draft,
    [Code("CONFIRMED")]           Confirmed,
    [Code("PARTIALLY_FULFILLED")] PartiallyFulfilled,
    [Code("FULFILLED")]           Fulfilled,
    [Code("INVOICED")]            Invoiced,
    [Code("CLOSED")]              Closed,
    [Code("CANCELLED")]           Cancelled
}

internal enum DeliveryMode
{
    [Code("SHIP")]        Ship,
    [Code("SELF_PICKUP")] SelfPickup
}

// Distinct from Demand.SupplierSelectionMode/FulfillmentModes (SaleOrderConfig's 3-value default,
// A29-P3-02) — a line's actual outcome has a fourth value, SPLIT, that isn't a valid config default
// (nobody configures "always split"; it only ever happens per-line at confirm time, §4.3).
internal enum SaleOrderLineFulfillmentMode
{
    [Code("IN_STOCK")]     InStock,
    [Code("BACK_TO_BACK")] BackToBack,
    [Code("DROP_SHIP")]    DropShip,
    [Code("SPLIT")]        Split
}

internal enum SaleOrderLineStatus
{
    [Code("OPEN")]                Open,
    [Code("RESERVED")]            Reserved,
    [Code("PARTIALLY_FULFILLED")] PartiallyFulfilled,
    [Code("FULFILLED")]           Fulfilled,
    [Code("INVOICED")]            Invoiced,
    [Code("CANCELLED")]           Cancelled
}

// A29-P4-06 §5.3 — demand.SaleOrderIntimations' own two persisted-as-string columns.
internal enum SaleOrderIntimationEventType
{
    [Code("SO_CONFIRMED")] SoConfirmed,
    [Code("PO_CREATED")]   PoCreated,
    [Code("DROP_SHIP")]    DropShip,
    [Code("RESERVED")]     Reserved,
    [Code("PO_APPROVED")]  PoApproved,
    [Code("GRN_RECEIVED")] GrnReceived,
    [Code("EXPIRING")]     Expiring,
    // A29-P6-06 §7.6 — the order's last delivery has reached the customer.
    [Code("SO_FULFILLED")] SoFulfilled
}

internal enum SaleOrderIntimationStatus
{
    [Code("QUEUED")] Queued,
    [Code("SENT")]   Sent,
    [Code("FAILED")] Failed
}

using System.Text.Json.Serialization;

namespace SMS.Shared.Integration.QuickBooks;

/// <summary>What kind of record is being sent to the accounting system.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SyncKind
{
    Customer,
    Vendor,
    Item,
    SalesInvoice,
    Bill
}

/// <summary>Where one record stands, as the gateway sees it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SyncState
{
    /// <summary>The gateway holds the payload but has not been asked to send it (e.g. scope says "only when referenced").</summary>
    NotSynced,
    Pending,
    InProgress,
    Synced,
    /// <summary>Dry-run mode: built and validated, nothing sent.</summary>
    DryRunOk,
    /// <summary>QuickBooks refused it, or retries ran out. Needs a changed payload or a manual retry.</summary>
    Failed,
    /// <summary>Our own validation refused it before anything was sent.</summary>
    Blocked,
    /// <summary>Waiting for a customer, vendor or item it references to be synced first.</summary>
    WaitingOnDependency,
    /// <summary>The outcome of a call is unknown and could not be settled automatically. A person must check.</summary>
    NeedsResolution,
    Voided
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ItemPayloadKind
{
    Goods,
    Service
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SalesInvoicePayloadStatus
{
    Issued,
    Cancelled,
    /// <summary>Not supported yet — the gateway refuses it with a clear reason (plan D-7).</summary>
    CreditNote
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BillLineCategory
{
    /// <summary>Goods bought — sent against an item when <c>ItemExternalId</c> is given.</summary>
    Goods,
    /// <summary>Carriage — posted to the freight expense account.</summary>
    Freight,
    /// <summary>Anything else — posted to the default expense account.</summary>
    Other
}

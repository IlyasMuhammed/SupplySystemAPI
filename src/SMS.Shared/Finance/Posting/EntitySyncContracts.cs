using System.Text.Json.Serialization;

namespace SMS.Shared.Finance.Posting;

// A44 C11 — master data (customers, vendors, products) to external accounting systems. Callers call
// IEntitySyncGateway after their own commit; it forwards to every active IEntitySyncTarget (QuickBooks today).

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SyncEntityType { Customer, Vendor, Product }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SyncAction { Create, Update, Deactivate, Delete }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EntitySyncOutcome
{
    Queued, NoTargetConfigured, ModuleNotLicensed, DuplicateSkipped,
    /// <summary>The payload was refused (missing document, bad data) — the caller's data needs fixing.</summary>
    ValidationFailed,
    Held,
    /// <summary>D-48 — an exception / unexpected error at the router or a target (log at Warning or above).</summary>
    Failed,
}

/// <summary>FSD §15.3 snapshot — informational (logged); targets build from <see cref="EntitySyncRequest.Document"/>.</summary>
public sealed class EntitySnapshot
{
    public string?  DisplayName  { get; init; }
    public string?  Email        { get; init; }
    public string?  Phone        { get; init; }
    public bool     IsActive     { get; init; } = true;
    public string?  CompanyName  { get; init; }
    public string?  TaxIdentificationNumber { get; init; }
    public string?  CurrencyCode { get; init; }
    public string?  Sku          { get; init; }
    public decimal? UnitPrice    { get; init; }
    public decimal? PurchaseCost { get; init; }
}

public sealed class EntitySyncRequest
{
    public SyncEntityType EntityType { get; init; }
    public SyncAction     Action     { get; init; }
    /// <summary>Partner Uuid or variant Uuid.</summary>
    public Guid           EntityId   { get; init; }
    /// <summary>Logged only — targets dedupe by payload fingerprint (D-13).</summary>
    public string?        IdempotencyKey { get; init; }
    public EntitySnapshot? Snapshot  { get; init; }
    /// <summary>CustomerPayload | VendorPayload | ItemPayload (SMS.Shared.Integration.QuickBooks).</summary>
    public IAccountingDocument Document { get; init; } = null!;
    public string InitiatedBy { get; init; } = "system";
}

public sealed class EntitySyncResult
{
    public bool              Success       { get; init; }
    public EntitySyncOutcome Outcome       { get; init; }
    public int               TargetsQueued { get; init; }
    public string?           ErrorMessage  { get; init; }
}

public sealed class BatchEntitySyncResult
{
    public int          TotalRequests     { get; init; }
    public int          Queued            { get; init; }
    public int          DuplicatesSkipped { get; init; }
    public List<string> Errors            { get; init; } = new();
}

/// <summary>Implemented by SMS.Modules.Accounting (router). Never throws.</summary>
public interface IEntitySyncGateway
{
    Task<EntitySyncResult> PushAsync(EntitySyncRequest request, CancellationToken ct = default);
    Task<BatchEntitySyncResult> PushBatchAsync(IReadOnlyList<EntitySyncRequest> requests, CancellationToken ct = default);
}

/// <summary>An external system that takes master data. Registered by the module that owns it.</summary>
public interface IEntitySyncTarget
{
    string TargetCode { get; }
    Task<bool> IsActiveAsync(CancellationToken ct = default);
    Task<EntitySyncResult> PushAsync(EntitySyncRequest request, CancellationToken ct = default);
}

public sealed class NullEntitySyncGateway : IEntitySyncGateway
{
    public Task<EntitySyncResult> PushAsync(EntitySyncRequest request, CancellationToken ct = default) =>
        Task.FromResult(new EntitySyncResult { Success = true, Outcome = EntitySyncOutcome.NoTargetConfigured });

    public Task<BatchEntitySyncResult> PushBatchAsync(IReadOnlyList<EntitySyncRequest> requests, CancellationToken ct = default) =>
        Task.FromResult(new BatchEntitySyncResult { TotalRequests = requests.Count });
}

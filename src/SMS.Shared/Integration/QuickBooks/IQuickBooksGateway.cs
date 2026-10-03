namespace SMS.Shared.Integration.QuickBooks;

/// <summary>
/// The QuickBooks gateway (SMS.Modules.Integration), as the rest of SCM sees it. Lives here so a
/// module can call it without referencing SMS.Modules.Integration — which references nothing of
/// theirs, so there is no cycle.
/// <para>
/// Every method only validates, stores and queues. Nothing here calls Intuit, so it is safe to call
/// inside a user's request. The organization comes from the ambient <see cref="Common.ITenantContext"/>.
/// </para>
/// <para>
/// <b>A gateway failure must never fail the caller's own operation.</b> Callers wrap each call and log;
/// the gateway's hourly reconciliation (<see cref="IQuickBooksSource.PushAllAsync"/>) catches anything missed.
/// </para>
/// </summary>
public interface IQuickBooksGateway
{
    Task<GatewayResult> UpsertCustomerAsync(CustomerPayload payload, CancellationToken ct = default);
    Task<GatewayResult> UpsertVendorAsync(VendorPayload payload, CancellationToken ct = default);
    Task<GatewayResult> UpsertItemAsync(ItemPayload payload, CancellationToken ct = default);
    Task<GatewayResult> UpsertSalesInvoiceAsync(SalesInvoicePayload payload, CancellationToken ct = default);
    Task<GatewayResult> VoidSalesInvoiceAsync(string externalId, CancellationToken ct = default);
    Task<GatewayResult> UpsertBillAsync(BillPayload payload, CancellationToken ct = default);

    Task<IReadOnlyList<SyncStatus>> GetStatusAsync(
        SyncKind kind, IReadOnlyCollection<string> externalIds, CancellationToken ct = default);

    /// <summary>
    /// Flags a record for the accountant without sending anything — e.g. a supplier invoice reversed in SCM after
    /// its bill reached QuickBooks, which must be voided or deleted there by hand (SAP alignment S-7). Whatever is
    /// still queued for the record is dropped. If it is (or may be) in QuickBooks, the sync dashboard shows it as
    /// NeedsResolution with <paramref name="reason"/>, and later payloads for it are not sent, until a person resolves
    /// it there; if it never reached QuickBooks, it is closed (Voided) and nothing more is sent for it.
    /// <para>The default does nothing: hosts and test fakes without the Integration module.</para>
    /// </summary>
    Task<GatewayResult> FlagForAccountantAsync(SyncKind kind, string externalId, string reason, CancellationToken ct = default) =>
        Task.FromResult(GatewayResult.Disabled());
}

/// <summary>
/// Implemented by an SCM module that owns records of one or more kinds (Suppliers: Customer + Vendor,
/// Inventory: Item, Finance: SalesInvoice + Bill), and called <b>by the gateway</b> when it needs SCM
/// to send something: a dependency an invoice is waiting on, the admin's "Push now" / "Sync all", and
/// the hourly reconciliation.
/// <para>
/// Implementations build payloads from their own data and call <see cref="IQuickBooksGateway"/>
/// back. Records requested through <see cref="PushAsync"/> are treated by the gateway as explicitly
/// wanted, whatever the partner/item scope setting says. The gateway calls these from background
/// jobs with the tenant set through <c>HangfireTenantScope</c>, so no HttpContext is available.
/// </para>
/// </summary>
public interface IQuickBooksSource
{
    IReadOnlyCollection<SyncKind> Kinds { get; }

    /// <summary>Send these records now. Unknown or deleted ids are skipped.</summary>
    Task PushAsync(SyncKind kind, IReadOnlyCollection<string> externalIds, CancellationToken ct = default);

    /// <summary>
    /// Send every record of this kind that could belong in QuickBooks and changed since
    /// <paramref name="changedSince"/> (all of them when null). Returns how many were sent.
    /// </summary>
    Task<int> PushAllAsync(SyncKind kind, DateTime? changedSince, CancellationToken ct = default);
}

/// <summary>The source system in-process SCM calls are recorded under.</summary>
public static class QuickBooksSourceSystems
{
    public const string Scm = "SCM";
}

/// <summary>
/// Registered by TryAdd in the modules that call the gateway, so they work (and their tests run)
/// in a host without SMS.Modules.Integration. The Integration module replaces it.
/// </summary>
public sealed class NullQuickBooksGateway : IQuickBooksGateway
{
    public Task<GatewayResult> UpsertCustomerAsync(CustomerPayload payload, CancellationToken ct = default)         => Disabled;
    public Task<GatewayResult> UpsertVendorAsync(VendorPayload payload, CancellationToken ct = default)             => Disabled;
    public Task<GatewayResult> UpsertItemAsync(ItemPayload payload, CancellationToken ct = default)                 => Disabled;
    public Task<GatewayResult> UpsertSalesInvoiceAsync(SalesInvoicePayload payload, CancellationToken ct = default) => Disabled;
    public Task<GatewayResult> VoidSalesInvoiceAsync(string externalId, CancellationToken ct = default)             => Disabled;
    public Task<GatewayResult> UpsertBillAsync(BillPayload payload, CancellationToken ct = default)                 => Disabled;

    public Task<IReadOnlyList<SyncStatus>> GetStatusAsync(
        SyncKind kind, IReadOnlyCollection<string> externalIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SyncStatus>>([]);

    private static Task<GatewayResult> Disabled => Task.FromResult(GatewayResult.Disabled());
}

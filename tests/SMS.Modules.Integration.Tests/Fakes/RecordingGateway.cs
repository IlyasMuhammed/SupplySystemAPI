using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Fakes;

/// <summary>An IQuickBooksGateway that answers with a set result and records what it was sent.</summary>
internal sealed class RecordingGateway : IQuickBooksGateway
{
    public GatewayResult Next { get; set; } = GatewayResult.Accepted(SyncState.Pending);
    public List<SyncStatus> Statuses { get; } = new();
    public List<(string Method, object? Payload)> Received { get; } = new();

    public Task<GatewayResult> UpsertCustomerAsync(CustomerPayload payload, CancellationToken ct = default)         => Answer(nameof(UpsertCustomerAsync), payload);
    public Task<GatewayResult> UpsertVendorAsync(VendorPayload payload, CancellationToken ct = default)             => Answer(nameof(UpsertVendorAsync), payload);
    public Task<GatewayResult> UpsertItemAsync(ItemPayload payload, CancellationToken ct = default)                 => Answer(nameof(UpsertItemAsync), payload);
    public Task<GatewayResult> UpsertSalesInvoiceAsync(SalesInvoicePayload payload, CancellationToken ct = default) => Answer(nameof(UpsertSalesInvoiceAsync), payload);
    public Task<GatewayResult> VoidSalesInvoiceAsync(string externalId, CancellationToken ct = default)             => Answer(nameof(VoidSalesInvoiceAsync), externalId);
    public Task<GatewayResult> UpsertBillAsync(BillPayload payload, CancellationToken ct = default)                 => Answer(nameof(UpsertBillAsync), payload);

    public Task<IReadOnlyList<SyncStatus>> GetStatusAsync(SyncKind kind, IReadOnlyCollection<string> externalIds, CancellationToken ct = default)
    {
        Received.Add((nameof(GetStatusAsync), externalIds.ToList()));
        return Task.FromResult<IReadOnlyList<SyncStatus>>(Statuses.Where(s => s.Kind == kind && externalIds.Contains(s.ExternalId)).ToList());
    }

    private Task<GatewayResult> Answer(string method, object? payload)
    {
        Received.Add((method, payload));
        return Task.FromResult(Next);
    }
}

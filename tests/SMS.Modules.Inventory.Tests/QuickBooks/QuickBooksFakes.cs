using Microsoft.Extensions.Logging;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Inventory.Tests.QuickBooks;

/// <summary>
/// Stands in for the QuickBooks gateway (SMS.Modules.Integration): records every call, and can be told to
/// answer something else, to throw, or to fail as a faulted task.
/// </summary>
internal sealed class RecordingQuickBooksGateway : IQuickBooksGateway
{
    public List<CustomerPayload>     Customers     { get; } = [];
    public List<VendorPayload>       Vendors       { get; } = [];
    public List<ItemPayload>         Items         { get; } = [];
    public List<SalesInvoicePayload> SalesInvoices { get; } = [];
    public List<string>              Voids         { get; } = [];
    public List<BillPayload>         Bills         { get; } = [];

    /// <summary>Every call, in order, as "Kind:ExternalId".</summary>
    public List<string> Calls { get; } = [];

    /// <summary>What every call answers, unless <see cref="Throw"/> is set.</summary>
    public GatewayResult Result { get; set; } = GatewayResult.Accepted(SyncState.Pending);

    /// <summary>Thrown by every call (synchronously) when set.</summary>
    public Exception? Throw { get; set; }

    /// <summary>When true, <see cref="Throw"/> comes back as a faulted task instead of a synchronous throw.</summary>
    public bool FaultTask { get; set; }

    /// <summary>Only these external ids fail; the rest answer <see cref="Result"/>.</summary>
    public Func<string, bool>? FailWhen { get; set; }

    /// <summary>Called on every call before it answers — lets a test observe interleaving.</summary>
    public Action<string>? OnCall { get; set; }

    public Task<GatewayResult> UpsertCustomerAsync(CustomerPayload payload, CancellationToken ct = default) =>
        Record(Customers, payload, SyncKind.Customer, payload.ExternalId);

    public Task<GatewayResult> UpsertVendorAsync(VendorPayload payload, CancellationToken ct = default) =>
        Record(Vendors, payload, SyncKind.Vendor, payload.ExternalId);

    public Task<GatewayResult> UpsertItemAsync(ItemPayload payload, CancellationToken ct = default) =>
        Record(Items, payload, SyncKind.Item, payload.ExternalId);

    public Task<GatewayResult> UpsertSalesInvoiceAsync(SalesInvoicePayload payload, CancellationToken ct = default) =>
        Record(SalesInvoices, payload, SyncKind.SalesInvoice, payload.ExternalId);

    public Task<GatewayResult> VoidSalesInvoiceAsync(string externalId, CancellationToken ct = default) =>
        Record(Voids, externalId, SyncKind.SalesInvoice, externalId, "Void");

    public Task<GatewayResult> UpsertBillAsync(BillPayload payload, CancellationToken ct = default) =>
        Record(Bills, payload, SyncKind.Bill, payload.ExternalId);

    public Task<IReadOnlyList<SyncStatus>> GetStatusAsync(
        SyncKind kind, IReadOnlyCollection<string> externalIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SyncStatus>>([]);

    private Task<GatewayResult> Record<T>(List<T> list, T payload, SyncKind kind, string externalId, string? verb = null)
    {
        list.Add(payload);
        var call = $"{verb ?? kind.ToString()}:{externalId}";
        Calls.Add(call);
        OnCall?.Invoke(call);

        var fail = FailWhen is null ? Throw is not null : FailWhen(externalId);
        if (fail)
        {
            var ex = Throw ?? new InvalidOperationException($"Gateway failure for {externalId}.");
            if (FaultTask) return Task.FromException<GatewayResult>(ex);
            throw ex;
        }

        return Task.FromResult(Result);
    }
}

/// <summary>A logger that keeps what it is told, so a test can say what was logged and at what level.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception), exception));

    public IEnumerable<(LogLevel Level, string Message, Exception? Exception)> At(LogLevel level) =>
        Entries.Where(e => e.Level == level);
}

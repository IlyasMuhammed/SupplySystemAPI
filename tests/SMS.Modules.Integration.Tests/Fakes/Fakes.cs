using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Fakes;

internal sealed class FakeReferenceDataReader : IReferenceDataReader
{
    public RemoteReferenceData? Data { get; set; }
    public Task<RemoteReferenceData?> GetCachedAsync(int connectionId, CancellationToken ct = default) => Task.FromResult(Data);
}

/// <summary>
/// Finance's exchange-rate lookup as the SAP-alignment plan defines it (S-4): the latest rate for the pair
/// on or before the date; else the reciprocal of the latest opposite rate (Inverted); same currency = 1;
/// no triangulation. Records every question.
/// </summary>
internal sealed class FakeExchangeRates : IExchangeRateProvider
{
    private readonly List<(string From, string To, decimal Rate, DateTime Date)> _rates = new();

    public List<(string From, string To, DateTime AsOf)> Asked { get; } = new();

    public FakeExchangeRates Add(string from, string to, decimal rate, DateTime date)
    {
        _rates.Add((from.ToUpperInvariant(), to.ToUpperInvariant(), rate, date.Date));
        return this;
    }

    public void Clear() => _rates.Clear();

    public Task<ExchangeRateQuote?> GetRateAsync(string fromCurrencyCode, string toCurrencyCode, DateTime asOf, CancellationToken ct = default)
    {
        var from = fromCurrencyCode.Trim().ToUpperInvariant();
        var to   = toCurrencyCode.Trim().ToUpperInvariant();
        Asked.Add((from, to, asOf));

        if (from == to) return Task.FromResult<ExchangeRateQuote?>(new ExchangeRateQuote(from, to, 1m, asOf.Date, false));

        var direct = Latest(from, to, asOf);
        if (direct is { } d) return Task.FromResult<ExchangeRateQuote?>(new ExchangeRateQuote(from, to, d.Rate, d.Date, false));

        var opposite = Latest(to, from, asOf);
        return Task.FromResult<ExchangeRateQuote?>(opposite is { } o
            ? new ExchangeRateQuote(from, to, 1m / o.Rate, o.Date, true)
            : null);
    }

    private (decimal Rate, DateTime Date)? Latest(string from, string to, DateTime asOf) =>
        _rates.Where(r => r.From == from && r.To == to && r.Date <= asOf.Date)
              .OrderByDescending(r => r.Date)
              .Select(r => ((decimal Rate, DateTime Date)?)(r.Rate, r.Date))
              .FirstOrDefault();
}

/// <summary>Records every call; optionally acts like the real one (marks the connection unusable).</summary>
internal sealed class FakeConnectionHealth : IConnectionHealth
{
    public List<(int ConnectionId, ConnectionStatus Status, string Reason)> Calls { get; } = new();
    public Func<int, ConnectionStatus, Task>? OnMarked { get; set; }

    public async Task MarkUnavailableAsync(int connectionId, ConnectionStatus status, string reason, CancellationToken ct = default)
    {
        Calls.Add((connectionId, status, reason));
        if (OnMarked is not null) await OnMarked(connectionId, status);
    }
}

internal sealed class FakeCallerContext : IGatewayCallerContext
{
    public string  SourceSystem     { get; set; } = QuickBooksSourceSystems.Scm;
    public bool    IsExternalClient { get; set; }
    public string? ApiClientId      { get; set; }
}

internal sealed class MutableTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public MutableTimeProvider(DateTimeOffset start) => Now = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now = Now.Add(by);
}

/// <summary>
/// Like SMS.Shared's TenantContext without an HttpContext: a job's <see cref="HangfireTenantScope"/>
/// wins; otherwise the "request" organization; with neither, no tenant (a bypass, as for a job
/// that never set one).
/// </summary>
internal sealed class HarnessTenant : ITenantContext
{
    public Guid? RequestOrganizationId { get; set; }

    /// <summary>
    /// The "request" is a super admin's: like the real TenantContext, the organization is still their own (the JWT
    /// claim wins over HangfireTenantScope), but the tenant query filter is bypassed.
    /// </summary>
    public bool RequestIsSuperAdmin { get; set; }

    public Guid OrganizationId =>
        RequestIsSuperAdmin && RequestOrganizationId is { } own ? own
        : HangfireTenantScope.OrganizationId ?? RequestOrganizationId ?? Guid.Empty;

    public bool IsSuperAdmin =>
        (RequestIsSuperAdmin && RequestOrganizationId is not null)
        || (HangfireTenantScope.OrganizationId is null && RequestOrganizationId is null);
}

/// <summary>
/// Finance's exchange rates as the real provider scopes them: per organization, read from the CURRENT tenant
/// (<see cref="ITenantContext.OrganizationId"/>) — so a lookup made under the wrong tenant gets the wrong
/// organization's rates, which is what tenant-isolation tests must be able to see.
/// </summary>
internal sealed class TenantExchangeRates : IExchangeRateProvider
{
    private readonly ITenantContext _tenant;
    private readonly Dictionary<Guid, FakeExchangeRates> _byOrganization;

    public TenantExchangeRates(ITenantContext tenant, Dictionary<Guid, FakeExchangeRates> byOrganization)
    {
        _tenant         = tenant;
        _byOrganization = byOrganization;
    }

    public Task<ExchangeRateQuote?> GetRateAsync(string fromCurrencyCode, string toCurrencyCode, DateTime asOf, CancellationToken ct = default) =>
        _byOrganization.TryGetValue(_tenant.OrganizationId, out var rates)
            ? rates.GetRateAsync(fromCurrencyCode, toCurrencyCode, asOf, ct)
            : Task.FromResult<ExchangeRateQuote?>(null);
}

/// <summary>Finance's tax codes as the real lookup scopes them: per organization, from the current tenant.</summary>
internal sealed class TenantTaxCodes : ITaxCodeLookup
{
    private readonly ITenantContext _tenant;
    private readonly Dictionary<Guid, List<TaxCodeInfo>> _byOrganization;

    public TenantTaxCodes(ITenantContext tenant, Dictionary<Guid, List<TaxCodeInfo>> byOrganization)
    {
        _tenant         = tenant;
        _byOrganization = byOrganization;
    }

    public static TaxCodeInfo Code(string code, string name, decimal rate, string usage, bool active = true, bool isDefault = false) =>
        new(Guid.NewGuid(), code, name, rate, usage, isDefault, active);

    private IEnumerable<TaxCodeInfo> Own => _byOrganization.TryGetValue(_tenant.OrganizationId, out var list) ? list : [];

    public Task<TaxCodeInfo?> GetAsync(Guid uuid, CancellationToken ct = default) =>
        Task.FromResult(Own.FirstOrDefault(c => c.Uuid == uuid));

    public Task<IReadOnlyList<TaxCodeInfo>> ListActiveAsync(string side, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TaxCodeInfo>>(Own
            .Where(c => c.IsActive && TaxCodeUsage.Allows(c.Usage, side))
            .OrderByDescending(c => c.IsDefault).ThenBy(c => c.Code, StringComparer.Ordinal)
            .ToList());

    public Task<TaxCodeInfo?> GetDefaultAsync(string side, CancellationToken ct = default) =>
        Task.FromResult(Own.FirstOrDefault(c => c.IsActive && c.IsDefault && TaxCodeUsage.Allows(c.Usage, side)));
}

/// <summary>What an SCM module "has": payloads it sends when the gateway asks.</summary>
internal sealed class FakeSourceState
{
    public List<SyncKind> Kinds { get; } = [SyncKind.Customer, SyncKind.Vendor, SyncKind.Item, SyncKind.SalesInvoice, SyncKind.Bill];
    public Dictionary<(SyncKind, string), object> Records { get; } = new();
    public List<(SyncKind Kind, string[] Ids)> PushCalls { get; } = new();
    public List<(SyncKind Kind, DateTime? Since)> PushAllCalls { get; } = new();
    public HashSet<SyncKind> ThrowOnPushAll { get; } = new();

    public void Has(CustomerPayload p)     => Records[(SyncKind.Customer, p.ExternalId)] = p;
    public void Has(VendorPayload p)       => Records[(SyncKind.Vendor, p.ExternalId)] = p;
    public void Has(ItemPayload p)         => Records[(SyncKind.Item, p.ExternalId)] = p;
    public void Has(SalesInvoicePayload p) => Records[(SyncKind.SalesInvoice, p.ExternalId)] = p;
    public void Has(BillPayload p)         => Records[(SyncKind.Bill, p.ExternalId)] = p;
}

/// <summary>An SCM source: answers the gateway's requests by calling the gateway back, as the real ones do.</summary>
internal sealed class FakeQuickBooksSource : IQuickBooksSource
{
    private readonly FakeSourceState    _state;
    private readonly IQuickBooksGateway _gateway;

    public FakeQuickBooksSource(FakeSourceState state, IQuickBooksGateway gateway)
    {
        _state   = state;
        _gateway = gateway;
    }

    public IReadOnlyCollection<SyncKind> Kinds => _state.Kinds;

    public async Task PushAsync(SyncKind kind, IReadOnlyCollection<string> externalIds, CancellationToken ct = default)
    {
        _state.PushCalls.Add((kind, externalIds.ToArray()));
        foreach (var id in externalIds)
            if (_state.Records.TryGetValue((kind, id), out var payload))
                await SendAsync(payload, ct);
    }

    public async Task<int> PushAllAsync(SyncKind kind, DateTime? changedSince, CancellationToken ct = default)
    {
        _state.PushAllCalls.Add((kind, changedSince));
        if (_state.ThrowOnPushAll.Contains(kind)) throw new InvalidOperationException($"Source broke on {kind}.");

        var sent = 0;
        foreach (var ((k, _), payload) in _state.Records.ToList())
        {
            if (k != kind) continue;
            await SendAsync(payload, ct);
            sent++;
        }
        return sent;
    }

    private Task<GatewayResult> SendAsync(object payload, CancellationToken ct) => payload switch
    {
        CustomerPayload c     => _gateway.UpsertCustomerAsync(Clone(c), ct),
        VendorPayload v       => _gateway.UpsertVendorAsync(Clone(v), ct),
        ItemPayload i         => _gateway.UpsertItemAsync(Clone(i), ct),
        SalesInvoicePayload s => _gateway.UpsertSalesInvoiceAsync(Clone(s), ct),
        BillPayload b         => _gateway.UpsertBillAsync(Clone(b), ct),
        _ => throw new ArgumentException(payload.GetType().Name)
    };

    private static T Clone<T>(T payload) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(payload))!;
}

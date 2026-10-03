using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Integration.Tests.QuickBooks;

/// <summary>
/// The Intuit boundary, faked for the end-to-end tests: an in-memory QuickBooks company that behaves
/// like the real one where the integration depends on it — ids and SyncTokens, duplicate names and
/// document numbers refused, stale SyncTokens refused, sparse updates — plus scripted failures.
/// Everything above this (gateway, outbox, ledger, executor, controllers, SCM triggers) is real.
/// </summary>
internal sealed class FakeQuickBooksCompany : IAccountingProvider
{
    internal sealed class Record
    {
        public required SyncKind     Kind      { get; init; }
        public required string       Id        { get; init; }
        public          string       Name      { get; set; } = string.Empty;
        public          int          SyncToken { get; set; }
        public          bool         Active    { get; set; } = true;
        public          RemoteEntity? Entity   { get; set; }
    }

    internal sealed record Call(string Operation, SyncKind Kind, string? Name, RemoteEntity? Entity, string? SyncToken);

    private readonly object _gate = new();
    private readonly List<Record> _records = [];
    private readonly List<Call> _calls = [];
    private int _nextId = 100;

    public string ProviderKey => ProviderKeys.QuickBooksOnline;

    /// <summary>The next create of this kind really creates the record, then reports a timeout.</summary>
    public SyncKind? TimeOutAfterNextCreateOf { get; set; }

    /// <summary>The next write call of any kind is refused as an auth failure (grant revoked in QuickBooks).</summary>
    public bool RevokeOnNextWrite { get; set; }

    /// <summary>
    /// SAP alignment (S-10): QuickBooks' multicurrency preference as the reference data reports it. Off by
    /// default — what every QuickBooks E2E test before it relied on.
    /// </summary>
    public bool MultiCurrencyEnabled { get; set; }

    /// <summary>Active currencies besides the home currency — reported only while <see cref="MultiCurrencyEnabled"/> is on, as QuickBooks does.</summary>
    public List<RemoteCurrency> ExtraCurrencies { get; } = [];

    /// <summary>Tax codes the company has besides NON and STD (SAP alignment S-11 mapping by code vs by rate).</summary>
    public List<RemoteTaxCode> ExtraTaxCodes { get; } = [];

    public IReadOnlyList<Call> Calls { get { lock (_gate) return _calls.ToList(); } }

    public IReadOnlyList<Call> Writes => Calls.Where(c => c.Operation is "Create" or "Update" or "Void").ToList();

    public IReadOnlyList<Record> Records(SyncKind kind) { lock (_gate) return _records.Where(r => r.Kind == kind).ToList(); }

    /// <summary>Something the accountant already entered in QuickBooks before SCM connected.</summary>
    public Record Seed(SyncKind kind, string name)
    {
        lock (_gate)
        {
            var record = new Record { Kind = kind, Id = (_nextId++).ToString(), Name = name };
            _records.Add(record);
            return record;
        }
    }

    public void ResetCalls() { lock (_gate) _calls.Clear(); }

    // ── IAccountingProvider ──────────────────────────────────────────────────────────

    public Task<ProviderResult<RemoteCompanyInfo>> GetCompanyInfoAsync(ProviderContext ctx, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult<RemoteCompanyInfo>.Ok(CompanyInfo));

    public Task<ProviderResult<RemoteReferenceData>> GetReferenceDataAsync(ProviderContext ctx, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult<RemoteReferenceData>.Ok(new RemoteReferenceData
        {
            Accounts =
            [
                new RemoteAccount("1", "Sales",           "Income",  "SalesOfProductIncome",  "Revenue", null, true),
                new RemoteAccount("2", "Discounts given", "Income",  "DiscountsRefundsGiven", "Revenue", null, true),
                new RemoteAccount("3", "Purchases",       "Expense", "SuppliesMaterials",     "Expense", null, true),
                new RemoteAccount("4", "Freight",         "Expense", "ShippingFreightDelivery", "Expense", null, true)
            ],
            TaxCodes =
            [
                new RemoteTaxCode("NON", "Exempt",  null, false, 0m,  true),
                new RemoteTaxCode("STD", "GST 17%", null, true,  17m, true),
                .. ExtraTaxCodes
            ],
            Terms       = [new RemoteTerm("T30", "Net 30", 30, true)],
            Currencies  = [new RemoteCurrency("PKR", "Pakistani Rupee"), .. (MultiCurrencyEnabled ? ExtraCurrencies : [])],
            Preferences = new RemotePreferences("PKR", MultiCurrencyEnabled, true, false) { DiscountsEnabled = true },
            CompanyInfo = CompanyInfo
        }));

    public Task<ProviderResult<RemoteRecord>> CreateAsync(ProviderContext ctx, RemoteEntity entity, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var name = NameOf(entity);
            _calls.Add(new Call("Create", entity.Kind, name, entity, null));

            if (TakeRevoke()) return Task.FromResult(Fail<RemoteRecord>(ProviderOutcomeKind.AuthRevoked, "3200", "AuthenticationFailed"));

            if (_records.Any(r => SameKindFamily(r.Kind, entity.Kind) && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(Fail<RemoteRecord>(ProviderOutcomeKind.Duplicate,
                    entity.Kind is SyncKind.SalesInvoice or SyncKind.Bill ? "6140" : "6240", "Duplicate Name Exists Error"));

            var record = new Record { Kind = entity.Kind, Id = (_nextId++).ToString(), Name = name, Entity = entity };
            _records.Add(record);

            if (TimeOutAfterNextCreateOf == entity.Kind)
            {
                TimeOutAfterNextCreateOf = null;
                return Task.FromResult(Fail<RemoteRecord>(ProviderOutcomeKind.Transient, "Timeout", "The operation has timed out."));
            }

            return Task.FromResult(ProviderResult<RemoteRecord>.Ok(ToRemote(record)));
        }
    }

    public Task<ProviderResult<RemoteRecord>> UpdateAsync(
        ProviderContext ctx, RemoteEntity entity, string remoteId, string syncToken, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _calls.Add(new Call("Update", entity.Kind, NameOf(entity), entity, syncToken));

            if (TakeRevoke()) return Task.FromResult(Fail<RemoteRecord>(ProviderOutcomeKind.AuthRevoked, "3200", "AuthenticationFailed"));

            var record = _records.SingleOrDefault(r => r.Kind == entity.Kind && r.Id == remoteId);
            if (record is null) return Task.FromResult(Fail<RemoteRecord>(ProviderOutcomeKind.NotFound, "610", "Object Not Found"));
            if (record.SyncToken.ToString() != syncToken)
                return Task.FromResult(Fail<RemoteRecord>(ProviderOutcomeKind.StaleObject, "5010", "Stale Object Error"));

            record.Name = NameOf(entity) ?? record.Name;
            record.Entity = entity;
            record.SyncToken++;
            return Task.FromResult(ProviderResult<RemoteRecord>.Ok(ToRemote(record)));
        }
    }

    public Task<ProviderResult<RemoteRecord>> GetByIdAsync(ProviderContext ctx, SyncKind kind, string remoteId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _calls.Add(new Call("Get", kind, remoteId, null, null));
            var record = _records.SingleOrDefault(r => r.Kind == kind && r.Id == remoteId);
            return Task.FromResult(record is null
                ? Fail<RemoteRecord>(ProviderOutcomeKind.NotFound, "610", "Object Not Found")
                : ProviderResult<RemoteRecord>.Ok(ToRemote(record)));
        }
    }

    public Task<ProviderResult<RemoteRecord?>> FindAsync(ProviderContext ctx, SyncKind kind, RemoteLookup lookup, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = lookup.DocNumber ?? lookup.Name ?? lookup.Sku;
            _calls.Add(new Call("Find", kind, key, null, null));
            var record = _records.FirstOrDefault(r => r.Kind == kind && string.Equals(r.Name, key, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(ProviderResult<RemoteRecord?>.Ok(record is null ? null : ToRemote(record)));
        }
    }

    public Task<ProviderResult<RemoteRecord>> VoidInvoiceAsync(ProviderContext ctx, string remoteId, string syncToken, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _calls.Add(new Call("Void", SyncKind.SalesInvoice, remoteId, null, syncToken));
            var record = _records.SingleOrDefault(r => r.Kind == SyncKind.SalesInvoice && r.Id == remoteId);
            if (record is null) return Task.FromResult(Fail<RemoteRecord>(ProviderOutcomeKind.NotFound, "610", "Object Not Found"));
            record.Active = false;
            record.SyncToken++;
            return Task.FromResult(ProviderResult<RemoteRecord>.Ok(ToRemote(record)));
        }
    }

    public Task<ProviderResult<IReadOnlyList<RemoteListEntry>>> ListAsync(
        ProviderContext ctx, SyncKind kind, int startPosition, int maxResults, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _calls.Add(new Call("List", kind, null, null, null));
            IReadOnlyList<RemoteListEntry> page = _records
                .Where(r => r.Kind == kind)
                .OrderBy(r => int.Parse(r.Id))
                .Skip(Math.Max(0, startPosition - 1))
                .Take(maxResults)
                .Select(r => new RemoteListEntry(r.Id, r.Name, r.Name, null, null, null, null, "PKR", r.Active))
                .ToList();
            return Task.FromResult(ProviderResult<IReadOnlyList<RemoteListEntry>>.Ok(page));
        }
    }

    public string? BuildDeepLink(IntegrationEnvironment environment, SyncKind kind, string remoteId) =>
        $"https://app.sandbox.qbo.intuit.com/app/{kind.ToString().ToLowerInvariant()}?id={remoteId}";

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private static readonly RemoteCompanyInfo CompanyInfo = new("E2E Sandbox Co", "E2E Sandbox Co Ltd", "PK", "books@e2e.test");

    private bool TakeRevoke()
    {
        if (!RevokeOnNextWrite) return false;
        RevokeOnNextWrite = false;
        return true;
    }

    /// <summary>QuickBooks keeps display names unique across customers and vendors (and employees).</summary>
    private static bool SameKindFamily(SyncKind a, SyncKind b) =>
        a == b || (a is SyncKind.Customer or SyncKind.Vendor && b is SyncKind.Customer or SyncKind.Vendor);

    private static string? NameOf(RemoteEntity entity) => entity switch
    {
        RemoteParty   p => p.DisplayName,
        RemoteItem    i => i.Name,
        RemoteInvoice v => v.DocNumber,
        RemoteBill    b => b.DocNumber,
        _               => null
    };

    private static RemoteRecord ToRemote(Record r)
    {
        var doc   = r.Kind is SyncKind.SalesInvoice or SyncKind.Bill ? r.Name : null;
        var total = r.Entity switch
        {
            RemoteInvoice inv => inv.Lines.Sum(l => l.Amount) - inv.DiscountAmount,
            RemoteBill    bill => bill.Lines.Sum(l => l.Amount),
            _                  => (decimal?)null
        };
        return new RemoteRecord(r.Id, r.SyncToken.ToString(), r.Name, doc, total, r.Entity is RemoteInvoice or RemoteBill ? 0m : null, r.Active);
    }

    private static ProviderResult<T> Fail<T>(ProviderOutcomeKind kind, string code, string message) =>
        ProviderResult<T>.Fail(kind, code, message);
}

/// <summary>Intuit's OAuth endpoints, faked: consent URL carries our state; codes become tokens.</summary>
internal sealed class FakeQuickBooksAuth : IAccountingAuthProvider
{
    private int _refreshes;

    public string ProviderKey => ProviderKeys.QuickBooksOnline;

    public List<string> ExchangedCodes { get; } = [];

    public string BuildConsentUrl(string state) =>
        $"https://appcenter.intuit.test/connect/oauth2?client_id=e2e&state={Uri.EscapeDataString(state)}";

    public Task<TokenGrant> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        lock (ExchangedCodes) ExchangedCodes.Add(code);
        return Task.FromResult(new TokenGrant($"AT-{code}", $"RT-{code}", DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddDays(100)));
    }

    public Task<TokenGrant> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var n = Interlocked.Increment(ref _refreshes);
        return Task.FromResult(new TokenGrant($"AT-refresh-{n}", $"RT-refresh-{n}", DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddDays(100)));
    }

    public Task RevokeAsync(string refreshToken, CancellationToken ct = default) => Task.CompletedTask;
}

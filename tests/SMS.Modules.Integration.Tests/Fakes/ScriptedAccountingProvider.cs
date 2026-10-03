using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Fakes;

/// <summary>
/// A scripted QuickBooks. Keeps an in-memory "company" so behaviour is realistic by default — a
/// create stores a record, a clashing name is a Duplicate, a stale SyncToken is a StaleObject, a
/// lookup finds what a "timed-out" create actually created — and lets a test script the next
/// outcome of any operation.
/// </summary>
internal sealed class ScriptedAccountingProvider : IAccountingProvider
{
    public string ProviderKey => ProviderKeys.QuickBooksOnline;

    public sealed record Call(string Operation, SyncKind Kind, RemoteEntity? Entity, string? RemoteId, string? SyncToken, RemoteLookup? Lookup, string RealmId);

    /// <param name="ApplyAnyway">For a failure: perform the write anyway (a timeout after the record was created).</param>
    public sealed record Script(ProviderOutcomeKind Outcome, bool ApplyAnyway = false, string? Code = null, string? Message = null, string? Field = null);

    public sealed class Record
    {
        public required string   Id        { get; init; }
        public required SyncKind Kind      { get; init; }
        public int               Token     { get; set; }
        public string?           Name      { get; set; }
        public string?           DocNumber { get; set; }
        public string?           VendorId  { get; set; }
        public string?           Email     { get; set; }
        public string?           TaxId     { get; set; }
        public string?           AccountNumber { get; set; }
        public string?           Sku       { get; set; }
        public bool              Active    { get; set; } = true;
        public bool              Voided    { get; set; }
        public decimal?          TotalTax  { get; set; }
        public RemoteEntity?     Entity    { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<Script>> _scripts = new(StringComparer.Ordinal);
    private int _nextId = 100;

    public List<Call>   Calls   { get; } = new();
    public List<Record> Company { get; } = new();

    /// <summary>Tax QuickBooks "calculates" for an invoice; null = not reported.</summary>
    public Func<RemoteInvoice, decimal?>? InvoiceTax { get; set; }

    /// <summary>Realms whose calls throw (a broken company, for job isolation tests).</summary>
    public HashSet<string> ThrowForRealms { get; } = new();

    public int WriteCalls => Calls.Count(c => c.Operation is "Create" or "Update" or "Void");
    public int CallsOf(string operation) => Calls.Count(c => c.Operation == operation);
    public IReadOnlyList<Record> Of(SyncKind kind) => Company.Where(r => r.Kind == kind).ToList();

    public ScriptedAccountingProvider Enqueue(string operation, params Script[] scripts)
    {
        if (!_scripts.TryGetValue(operation, out var queue)) _scripts[operation] = queue = new Queue<Script>();
        foreach (var s in scripts) queue.Enqueue(s);
        return this;
    }

    public Record Seed(SyncKind kind, string name, string? email = null, string? taxId = null, string? accountNumber = null,
                       string? sku = null, string? docNumber = null, bool active = true)
    {
        var record = new Record
        {
            Id = NextId(), Kind = kind, Name = name, Email = email, TaxId = taxId, AccountNumber = accountNumber,
            Sku = sku, DocNumber = docNumber, Active = active
        };
        Company.Add(record);
        return record;
    }

    /// <summary>The accountant edits the record in QuickBooks: its SyncToken moves on.</summary>
    public void TouchInQuickBooks(string remoteId) => Company.Single(r => r.Id == remoteId).Token++;

    // ── IAccountingProvider ──────────────────────────────────────────────────

    public Task<ProviderResult<RemoteCompanyInfo>> GetCompanyInfoAsync(ProviderContext ctx, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult<RemoteCompanyInfo>.Ok(new RemoteCompanyInfo("Sandbox Company", null, "PK", null)));

    /// <summary>What QuickBooks answers a reference-data refresh with.</summary>
    public RemoteReferenceData ReferenceData { get; set; } = new();

    public Task<ProviderResult<RemoteReferenceData>> GetReferenceDataAsync(ProviderContext ctx, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult<RemoteReferenceData>.Ok(ReferenceData));

    /// <summary>Runs while a create is "on the wire" — e.g. the caller sends a newer payload meanwhile.</summary>
    public Func<RemoteEntity, Task>? DuringCreate { get; set; }

    public async Task<ProviderResult<RemoteRecord>> CreateAsync(ProviderContext ctx, RemoteEntity entity, CancellationToken ct = default)
    {
        if (DuringCreate is { } hook)
        {
            DuringCreate = null;   // once
            await hook(entity);
        }
        return await CreateLocked(ctx, entity);
    }

    private Task<ProviderResult<RemoteRecord>> CreateLocked(ProviderContext ctx, RemoteEntity entity)
    {
        lock (_lock)
        {
            Track(ctx, "Create", entity.Kind, entity, null, null, null);
            var script = Next("Create");

            if (script is { Outcome: not ProviderOutcomeKind.Succeeded })
            {
                if (script.ApplyAnyway) Apply(entity, null);
                return Done(Fail<RemoteRecord>(script));
            }

            if (Clash(entity, exceptId: null) is { } clash)
                return Done(ProviderResult<RemoteRecord>.Fail(ProviderOutcomeKind.Duplicate, "6240",
                    $"Duplicate Name Exists Error: The name supplied already exists ({clash.Id})."));

            var created = Apply(entity, null);
            return Done(ProviderResult<RemoteRecord>.Ok(ToRemote(created)));
        }
    }

    public Task<ProviderResult<RemoteRecord>> UpdateAsync(ProviderContext ctx, RemoteEntity entity, string remoteId, string syncToken, CancellationToken ct = default)
    {
        lock (_lock)
        {
            Track(ctx, "Update", entity.Kind, entity, remoteId, syncToken, null);
            var script = Next("Update");
            if (script is { Outcome: not ProviderOutcomeKind.Succeeded })
            {
                if (script.ApplyAnyway && Find(remoteId) is { } r) Apply(entity, r);
                return Done(Fail<RemoteRecord>(script));
            }

            var record = Find(remoteId);
            if (record is null) return Done(ProviderResult<RemoteRecord>.Fail(ProviderOutcomeKind.NotFound, "610", "Object Not Found"));
            if (record.Token.ToString() != syncToken)
                return Done(ProviderResult<RemoteRecord>.Fail(ProviderOutcomeKind.StaleObject, "5010", "Stale Object Error"));
            if (Clash(entity, exceptId: remoteId) is not null)
                return Done(ProviderResult<RemoteRecord>.Fail(ProviderOutcomeKind.Duplicate, "6240", "Duplicate Name Exists Error"));

            Apply(entity, record);
            return Done(ProviderResult<RemoteRecord>.Ok(ToRemote(record)));
        }
    }

    public Task<ProviderResult<RemoteRecord>> GetByIdAsync(ProviderContext ctx, SyncKind kind, string remoteId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            Track(ctx, "Get", kind, null, remoteId, null, null);
            var script = Next("Get");
            if (script is { Outcome: not ProviderOutcomeKind.Succeeded }) return Done(Fail<RemoteRecord>(script));

            var record = Find(remoteId);
            return Done(record is null
                ? ProviderResult<RemoteRecord>.Fail(ProviderOutcomeKind.NotFound, "610", "Object Not Found")
                : ProviderResult<RemoteRecord>.Ok(ToRemote(record)));
        }
    }

    public Task<ProviderResult<RemoteRecord?>> FindAsync(ProviderContext ctx, SyncKind kind, RemoteLookup lookup, CancellationToken ct = default)
    {
        lock (_lock)
        {
            Track(ctx, "Find", kind, null, null, null, lookup);
            var script = Next("Find");
            if (script is { Outcome: not ProviderOutcomeKind.Succeeded }) return Done(Fail<RemoteRecord?>(script));

            var found = Company.FirstOrDefault(r => r.Kind == kind && (
                lookup.Name is not null && string.Equals(r.Name?.Trim(), lookup.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                || lookup.DocNumber is not null && r.DocNumber == lookup.DocNumber
                   && (lookup.VendorRemoteId is null || r.VendorId == lookup.VendorRemoteId)));

            return Done(ProviderResult<RemoteRecord?>.Ok(found is null ? null : ToRemote(found)));
        }
    }

    public Task<ProviderResult<RemoteRecord>> VoidInvoiceAsync(ProviderContext ctx, string remoteId, string syncToken, CancellationToken ct = default)
    {
        lock (_lock)
        {
            Track(ctx, "Void", SyncKind.SalesInvoice, null, remoteId, syncToken, null);
            var script = Next("Void");
            if (script is { Outcome: not ProviderOutcomeKind.Succeeded }) return Done(Fail<RemoteRecord>(script));

            var record = Find(remoteId);
            if (record is null) return Done(ProviderResult<RemoteRecord>.Fail(ProviderOutcomeKind.NotFound, "610", "Object Not Found"));
            if (record.Token.ToString() != syncToken)
                return Done(ProviderResult<RemoteRecord>.Fail(ProviderOutcomeKind.StaleObject, "5010", "Stale Object Error"));

            record.Voided = true;
            record.Token++;
            return Done(ProviderResult<RemoteRecord>.Ok(ToRemote(record)));
        }
    }

    public Task<ProviderResult<IReadOnlyList<RemoteListEntry>>> ListAsync(
        ProviderContext ctx, SyncKind kind, int startPosition, int maxResults, CancellationToken ct = default)
    {
        lock (_lock)
        {
            Track(ctx, "List", kind, null, null, null, null);
            var script = Next("List");
            if (script is { Outcome: not ProviderOutcomeKind.Succeeded }) return Done(Fail<IReadOnlyList<RemoteListEntry>>(script));

            IReadOnlyList<RemoteListEntry> page = Company
                .Where(r => r.Kind == kind)
                .OrderBy(r => int.Parse(r.Id))
                .Skip(startPosition - 1)
                .Take(maxResults)
                .Select(r => new RemoteListEntry(r.Id, r.Name ?? string.Empty, r.Name, r.Email, r.TaxId, r.AccountNumber, r.Sku, "PKR", r.Active))
                .ToList();
            return Done(ProviderResult<IReadOnlyList<RemoteListEntry>>.Ok(page));
        }
    }

    public string? BuildDeepLink(IntegrationEnvironment environment, SyncKind kind, string remoteId) =>
        $"https://app.sandbox.qbo.intuit.com/app/{kind.ToString().ToLowerInvariant()}?id={remoteId}";

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private void Track(ProviderContext ctx, string op, SyncKind kind, RemoteEntity? entity, string? remoteId, string? token, RemoteLookup? lookup)
    {
        if (ThrowForRealms.Contains(ctx.RealmId)) throw new InvalidOperationException($"Company {ctx.RealmId} is broken.");
        Calls.Add(new Call(op, kind, entity, remoteId, token, lookup, ctx.RealmId));
    }

    private Script? Next(string operation) =>
        _scripts.TryGetValue(operation, out var queue) && queue.Count > 0 ? queue.Dequeue() : null;

    private static ProviderResult<T> Fail<T>(Script s) => new()
    {
        Outcome     = s.Outcome,
        ErrorCode   = s.Code ?? s.Outcome.ToString().ToUpperInvariant(),
        Message     = s.Message ?? $"Scripted {s.Outcome}",
        ErrorField  = s.Field,
        IntuitTid   = "tid-" + Guid.NewGuid().ToString("N")[..8],
        RequestJson = "{\"access_token\":\"SECRET-TOKEN\",\"scripted\":true}",
        DurationMs  = 5
    };

    private static Task<ProviderResult<T>> Done<T>(ProviderResult<T> result) => Task.FromResult(result);

    private string NextId() => (_nextId++).ToString();

    private Record? Find(string remoteId) => Company.FirstOrDefault(r => r.Id == remoteId);

    private Record? Clash(RemoteEntity entity, string? exceptId)
    {
        var (name, doc, vendor) = Identity(entity);
        return entity.Kind switch
        {
            SyncKind.Customer or SyncKind.Vendor => Company.FirstOrDefault(r => r.Id != exceptId
                && r.Kind is SyncKind.Customer or SyncKind.Vendor
                && string.Equals(r.Name?.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase)),
            SyncKind.Item => Company.FirstOrDefault(r => r.Id != exceptId && r.Kind == SyncKind.Item
                && string.Equals(r.Name?.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase)),
            SyncKind.SalesInvoice => Company.FirstOrDefault(r => r.Id != exceptId && r.Kind == SyncKind.SalesInvoice && r.DocNumber == doc),
            SyncKind.Bill => Company.FirstOrDefault(r => r.Id != exceptId && r.Kind == SyncKind.Bill && r.DocNumber == doc && r.VendorId == vendor),
            _ => null
        };
    }

    private static (string? name, string? doc, string? vendor) Identity(RemoteEntity entity) => entity switch
    {
        RemoteParty p   => (p.DisplayName, null, null),
        RemoteItem i    => (i.Name, null, null),
        RemoteInvoice s => (null, s.DocNumber, null),
        RemoteBill b    => (null, b.DocNumber, b.VendorId),
        _               => (null, null, null)
    };

    private Record Apply(RemoteEntity entity, Record? existing)
    {
        var record = existing ?? new Record { Id = NextId(), Kind = entity.Kind };
        if (existing is null) Company.Add(record);
        else record.Token++;

        var (name, doc, vendor) = Identity(entity);
        record.Name      = name ?? record.Name;
        record.DocNumber = doc ?? record.DocNumber;
        record.VendorId  = vendor ?? record.VendorId;
        record.Entity    = entity;

        switch (entity)
        {
            case RemoteParty p:
                record.Email = p.Email; record.TaxId = p.TaxId; record.Active = p.Active;
                if (p is RemoteVendor v) record.AccountNumber = v.AccountNumber;
                break;
            case RemoteItem i:
                record.Sku = i.Sku; record.Active = i.Active;
                break;
            case RemoteInvoice inv:
                record.TotalTax = InvoiceTax?.Invoke(inv);
                break;
        }

        return record;
    }

    private static RemoteRecord ToRemote(Record r) =>
        new(r.Id, r.Token.ToString(), r.Name, r.DocNumber, null, r.TotalTax, r.Active);
}

internal sealed class FakeAccountingProviderRegistry : IAccountingProviderRegistry
{
    private readonly IAccountingProvider _provider;
    public FakeAccountingProviderRegistry(IAccountingProvider provider) => _provider = provider;

    public IAccountingProvider Get(string providerKey) =>
        string.Equals(providerKey, _provider.ProviderKey, StringComparison.OrdinalIgnoreCase)
            ? _provider
            : throw new KeyNotFoundException($"No provider '{providerKey}'.");
}

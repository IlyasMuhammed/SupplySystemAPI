using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Hubs;
using SMS.Modules.Pos.Models;
using SMS.Modules.Pos.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Pos.Tests;

internal sealed class FakeClock : TimeProvider
{
    public DateTime UtcNow { get; set; } = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);
    public override DateTimeOffset GetUtcNow() => new(UtcNow);
    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>In-memory IPosPinService (XMOD's PosPinService semantics: FindByPin = null on ambiguity, inactive users never match).</summary>
internal sealed class FakePins : IPosPinService
{
    public Dictionary<int, PosUserInfo> Users { get; } = [];
    public Dictionary<int, string> PinsByUser { get; } = [];
    public List<int> TokensIssuedFor { get; } = [];

    public void AddUser(int id, string name, params string[] permissions) =>
        Users[id] = new PosUserInfo(id, name, $"{name.ToLowerInvariant()}@x.test", "Role", true, false, permissions);

    public Task SetPinAsync(Guid organizationId, int userId, string? pin, CancellationToken ct = default)
    {
        if (pin is null) PinsByUser.Remove(userId); else PinsByUser[userId] = pin;
        Users[userId] = Users[userId] with { HasPin = pin is not null };
        return Task.CompletedTask;
    }

    public Task<bool> VerifyAsync(Guid organizationId, int userId, string pin, CancellationToken ct = default) =>
        Task.FromResult(Users.GetValueOrDefault(userId)?.IsActive == true && PinsByUser.GetValueOrDefault(userId) == pin);

    public Task<int?> FindByPinAsync(Guid organizationId, IReadOnlyCollection<int> candidateUserIds, string pin, CancellationToken ct = default)
    {
        var hits = candidateUserIds.Where(id => Users.GetValueOrDefault(id)?.IsActive == true && PinsByUser.GetValueOrDefault(id) == pin).ToList();
        return Task.FromResult<int?>(hits.Count == 1 ? hits[0] : null);
    }

    public Task<IReadOnlyList<PosPinHash>> GetPinHashesAsync(Guid organizationId, IReadOnlyCollection<int> userIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PosPinHash>>(userIds.Where(PinsByUser.ContainsKey).Select(id => new PosPinHash(id, "hash:" + id)).ToList());

    public Task<IReadOnlyList<PosUserInfo>> GetUsersAsync(Guid organizationId, IReadOnlyCollection<int> userIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PosUserInfo>>(userIds.Where(Users.ContainsKey).Select(id => Users[id]).ToList());

    public Task<IReadOnlyList<PosUserInfo>> SearchUsersAsync(Guid organizationId, string? search, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PosUserInfo>>(Users.Values
            .Where(u => string.IsNullOrEmpty(search) || u.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList());

    public Task<PosTokenPair> IssueTokensAsync(Guid organizationId, int userId, CancellationToken ct = default)
    {
        TokensIssuedFor.Add(userId);
        return Task.FromResult(new PosTokenPair($"access-{userId}", $"refresh-{userId}"));
    }
}

internal sealed class FakeHub : IPosHubNotifier
{
    public List<(string Event, Guid Target, string Detail)> Sent { get; } = [];
    private Task Add(string e, Guid t, string d) { Sent.Add((e, t, d)); return Task.CompletedTask; }
    public Task PriceChangedAsync(Guid org, Guid variantUuid, decimal? oldPrice, decimal? newPrice) => Add(PosHubEvents.PriceChanged, variantUuid, "");
    public Task StockUpdatedAsync(Guid org, Guid storeUuid, Guid variantUuid, decimal available) => Add(PosHubEvents.StockUpdated, storeUuid, "");
    public Task ProductUpdatedAsync(Guid org, Guid variantUuid) => Add(PosHubEvents.ProductUpdated, variantUuid, "");
    public Task ProductDeactivatedAsync(Guid org, Guid variantUuid) => Add(PosHubEvents.ProductDeactivated, variantUuid, "");
    public Task CustomerBalanceChangedAsync(Guid org, Guid customerUuid, decimal balance, decimal creditLimit) => Add(PosHubEvents.CustomerBalanceChanged, customerUuid, "");
    public Task ConfigChangedAsync(Guid org, Guid storeUuid, string configType) => Add(PosHubEvents.ConfigChanged, storeUuid, configType);
    public Task ForceSyncAsync(Guid org, Guid terminalUuid, string syncType) => Add(PosHubEvents.ForceSync, terminalUuid, syncType);
    public Task ShiftAlertAsync(Guid org, Guid terminalUuid, string message, string severity) => Add(PosHubEvents.ShiftAlert, terminalUuid, message);
    public Task ForceLogoutAsync(Guid org, Guid terminalUuid, string reason) => Add(PosHubEvents.ForceLogout, terminalUuid, reason);
    public List<(string Event, Guid Store, object Payload)> StoreEvents { get; } = [];
    public Task StoreEventAsync(Guid org, Guid storeUuid, string eventName, object payload)
    {
        StoreEvents.Add((eventName, storeUuid, payload));
        return Add(eventName, storeUuid, "");
    }
}

internal sealed class FakeGate : IModuleGate
{
    public HashSet<string> Off { get; } = [];
    public Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default) => Task.FromResult(!Off.Contains(featureCode));
}

internal sealed class FakeWarehouses : IWarehouseDirectory
{
    public Dictionary<Guid, WarehouseContact> Items { get; } = [];
    public Guid Add(string code, bool active = true)
    {
        var id = Guid.NewGuid();
        Items[id] = new WarehouseContact(id, code, $"Warehouse {code}", null, null, null, null, null, active);
        return id;
    }
    public Task<WarehouseContact?> FindAsync(Guid warehouseUuid, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(warehouseUuid));
}

internal sealed class FakeTaxCodes : ITaxCodeLookup
{
    public List<TaxCodeInfo> Codes { get; } = [];
    public Task<TaxCodeInfo?> GetAsync(Guid uuid, CancellationToken ct = default) => Task.FromResult(Codes.FirstOrDefault(c => c.Uuid == uuid));
    public Task<IReadOnlyList<TaxCodeInfo>> ListActiveAsync(string side, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TaxCodeInfo>>(Codes.Where(c => c.IsActive && TaxCodeUsage.Allows(c.Usage, side)).ToList());
    public Task<TaxCodeInfo?> GetDefaultAsync(string side, CancellationToken ct = default) =>
        Task.FromResult(Codes.FirstOrDefault(c => c.IsDefault && c.IsActive && TaxCodeUsage.Allows(c.Usage, side)));
}

internal sealed class FakeNotifications : INotificationService
{
    public List<NotificationRequest> Sent { get; } = [];
    public Task CreateAsync(NotificationRequest request) { Sent.Add(request); return Task.CompletedTask; }
    public Task TryCreateAsync(NotificationRequest request) { Sent.Add(request); return Task.CompletedTask; }
    public Task BroadcastAsync(string type, string category, string title, string message, int createdBy = 0) =>
        throw new InvalidOperationException("POS must never broadcast (R-1).");
    public Task SendEmailAsync(string to, string subject, string htmlBody) => Task.CompletedTask;
}

/// <summary>Inventory's catalog read side (D-26 semantics: strictly after since, oldest first, ties extend a full page).</summary>
internal sealed class FakeCatalog : IPosCatalogSource
{
    public Dictionary<Guid, PosCatalogVariant> Variants { get; } = [];
    public List<(int Id, string Name)> Categories { get; } = [(1, "Drinks"), (2, "Snacks")];

    public PosCatalogVariant Add(string sku, decimal price, bool active = true, bool saleable = true, bool pos = true, Guid? product = null,
        DateTime? modifiedAt = null, string? name = null)
    {
        var v = new PosCatalogVariant(Guid.NewGuid(), product ?? Guid.NewGuid(), sku, name ?? $"Product {sku}", null, $"BC{sku}", 1, "Drinks", "EA",
            price, null, active, saleable, pos, modifiedAt ?? new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        Variants[v.VariantUuid] = v;
        return v;
    }

    public void Set(PosCatalogVariant v) => Variants[v.VariantUuid] = v;

    public Task<IReadOnlyList<PosCatalogVariant>> GetChangedAsync(Guid organizationId, DateTime? since, int limit, CancellationToken ct = default)
    {
        var all = Variants.Values.Where(v => since == null || v.ModifiedAt > since).OrderBy(v => v.ModifiedAt).ThenBy(v => v.Sku).ToList();
        var page = all.Take(limit).ToList();
        if (page.Count == limit && page.Count > 0) page.AddRange(all.Skip(limit).TakeWhile(v => v.ModifiedAt == page[^1].ModifiedAt));
        return Task.FromResult<IReadOnlyList<PosCatalogVariant>>(page);
    }

    public Task<IReadOnlyList<PosCatalogVariant>> GetByVariantUuidsAsync(Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PosCatalogVariant>>(variantUuids.Where(Variants.ContainsKey).Select(id => Variants[id]).ToList());

    public Task<IReadOnlyList<PosCatalogVariant>> GetByProductUuidsAsync(Guid organizationId, IReadOnlyCollection<Guid> productUuids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PosCatalogVariant>>(Variants.Values.Where(v => productUuids.Contains(v.ProductUuid)).ToList());

    public Task<IReadOnlyList<(int Id, string Name)>> GetCategoriesAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(int Id, string Name)>>(Categories);
}

/// <summary>IPosStockPoster: records postings, idempotent per (source, type), can fail or report negatives.</summary>
internal sealed class FakeStock : IPosStockPoster
{
    public List<PosStockPosting> Posted { get; } = [];
    public bool Fail { get; set; }
    public List<Guid> Negatives { get; } = [];
    public Dictionary<Guid, decimal> Levels { get; } = [];
    public List<Guid> WarehousesAsked { get; } = [];

    public Task<PosStockResult> PostAsync(PosStockPosting posting, CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("Inventory is down.");
        if (!Posted.Any(p => p.SourceUuid == posting.SourceUuid && p.TransactionType == posting.TransactionType))
        {
            Posted.Add(posting);
            foreach (var l in posting.Lines) Levels[l.VariantUuid] = Levels.GetValueOrDefault(l.VariantUuid) + (posting.IsOutbound ? -l.Quantity : l.Quantity);
        }
        return Task.FromResult(new PosStockResult(Negatives.ToList()));
    }

    public Task<IReadOnlyList<PosStockLevel>> GetLevelsAsync(Guid organizationId, Guid warehouseUuid, IReadOnlyCollection<Guid>? variantUuids = null, CancellationToken ct = default)
    {
        WarehousesAsked.Add(warehouseUuid);
        return Task.FromResult<IReadOnlyList<PosStockLevel>>(Levels.Where(l => variantUuids == null || variantUuids.Contains(l.Key))
            .Select(l => new PosStockLevel(l.Key, l.Value)).ToList());
    }
}

internal sealed class FakeCustomers : IPosCustomerSource
{
    public Dictionary<Guid, PosCustomer> Items { get; } = [];
    public PosCustomer WalkIn { get; }

    public FakeCustomers()
    {
        WalkIn = new PosCustomer(Guid.NewGuid(), "C-WALKIN", "Walk-In Customer", "INDIVIDUAL", null, null, null, 0m, true, true,
            new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Items[WalkIn.Uuid] = WalkIn;
    }

    public PosCustomer Add(string code, decimal creditLimit = 0m, bool active = true, DateTime? modifiedAt = null)
    {
        var c = new PosCustomer(Guid.NewGuid(), code, $"Customer {code}", "COMPANY", null, null, null, creditLimit, active, false,
            modifiedAt ?? new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        Items[c.Uuid] = c;
        return c;
    }

    public Task<IReadOnlyList<PosCustomer>> GetChangedAsync(Guid organizationId, DateTime? since, int limit, CancellationToken ct = default)
    {
        var all = Items.Values.Where(c => since == null || c.ModifiedAt > since).OrderBy(c => c.ModifiedAt).ThenBy(c => c.Code).ToList();
        var page = all.Take(limit).ToList();
        if (page.Count == limit && page.Count > 0) page.AddRange(all.Skip(limit).TakeWhile(c => c.ModifiedAt == page[^1].ModifiedAt));
        return Task.FromResult<IReadOnlyList<PosCustomer>>(page);
    }

    public Task<PosCustomer?> GetAsync(Guid organizationId, Guid customerUuid, CancellationToken ct = default) => Task.FromResult(Items.GetValueOrDefault(customerUuid));
    public Task<IReadOnlyList<PosCustomer>> GetManyAsync(Guid organizationId, IReadOnlyCollection<Guid> customerUuids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PosCustomer>>(customerUuids.Where(Items.ContainsKey).Select(id => Items[id]).ToList());
    public Task<PosCustomer> GetWalkInAsync(Guid organizationId, CancellationToken ct = default) => Task.FromResult(WalkIn);
}

internal sealed class FakeBalances : ICustomerBalanceLookup
{
    public Dictionary<Guid, decimal> Balances { get; } = [];
    public Task<IReadOnlyDictionary<Guid, CustomerBalanceInfo>> GetAsync(Guid organizationId, IReadOnlyCollection<Guid> partnerIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, CustomerBalanceInfo>>(partnerIds.Where(Balances.ContainsKey)
            .ToDictionary(id => id, id => new CustomerBalanceInfo(Balances[id], 0m, "USD")));
    public Task<string?> GetBaseCurrencyCodeAsync(Guid organizationId, CancellationToken ct = default) => Task.FromResult<string?>("USD");
}

/// <summary>ICounterSaleService (FIN semantics: idempotent on SourceUuid, exact tender sums; ON_ACCOUNT raises the balance).</summary>
internal sealed class FakeFinance : ICounterSaleService
{
    private readonly FakeBalances _balances;
    public FakeFinance(FakeBalances balances) => _balances = balances;

    public bool Fail { get; set; }
    public string? RefuseVoid { get; set; }
    public Dictionary<Guid, CounterSaleRequest> Sales { get; } = [];
    public Dictionary<Guid, CounterReturnRequest> Returns { get; } = [];
    public Dictionary<Guid, Guid> Voids { get; } = [];
    public List<Guid> VoidSourceUuids { get; } = [];
    private int _n;

    /// <summary>HangfireTenantScope at each sale posting (R-6: a job must post inside the organization's scope).</summary>
    public List<Guid?> ScopesSeen { get; } = [];

    public Task<CounterSaleResult> PostSaleAsync(CounterSaleRequest r, CancellationToken ct = default)
    {
        ScopesSeen.Add(HangfireTenantScope.OrganizationId);
        if (Fail) throw new InvalidOperationException("Finance is down.");
        var total = r.Lines.Sum(l => l.LineTotal);
        if (r.Lines.Any(l => l.LineTotal != l.NetAmount + l.TaxAmount)) throw new SMS.Shared.Exceptions.BadRequestException("line arithmetic");
        if (r.Tenders.Sum(t => t.Amount) != total) throw new SMS.Shared.Exceptions.BadRequestException($"Tenders {r.Tenders.Sum(t => t.Amount)} ≠ {total}");
        if (!Sales.ContainsKey(r.SourceUuid))
        {
            Sales[r.SourceUuid] = r;
            _balances.Balances[r.CustomerPartnerUuid] = _balances.Balances.GetValueOrDefault(r.CustomerPartnerUuid)
                + r.Tenders.Where(t => t.Method == PosPaymentMethod.OnAccount).Sum(t => t.Amount);
        }
        var invoice = new Guid(r.SourceUuid.ToByteArray().Reverse().ToArray());
        return Task.FromResult(new CounterSaleResult(invoice, $"SI-{++_n}", total,
            r.Tenders.Where(t => t.Method != PosPaymentMethod.OnAccount).Select(_ => Guid.NewGuid()).ToList()));
    }

    public Task<CounterReturnResult> PostReturnAsync(CounterReturnRequest r, CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("Finance is down.");
        if (r.Refunds.Sum(t => t.Amount) != r.Lines.Sum(l => l.LineTotal)) throw new SMS.Shared.Exceptions.BadRequestException("refunds ≠ total");
        Returns.TryAdd(r.SourceUuid, r);
        return Task.FromResult(new CounterReturnResult(Guid.NewGuid(), $"CN-{++_n}", r.Lines.Sum(l => l.LineTotal),
            r.Refunds.Where(t => t.Method != PosPaymentMethod.OnAccount).Select(_ => Guid.NewGuid()).ToList()));
    }

    public Task<CounterReturnResult> VoidSaleAsync(Guid organizationId, Guid salesInvoiceUuid, Guid sourceUuid, string reason, int userId, CancellationToken ct = default)
    {
        if (RefuseVoid is not null) throw new SMS.Shared.Exceptions.BadRequestException(RefuseVoid);
        if (Fail) throw new InvalidOperationException("Finance is down.");
        VoidSourceUuids.Add(sourceUuid);
        if (!Voids.ContainsKey(sourceUuid)) Voids[sourceUuid] = Guid.NewGuid();
        return Task.FromResult(new CounterReturnResult(Voids[sourceUuid], $"CN-V{_n}", 0m, []));
    }
}

/// <summary>One organization, real POS services over EF InMemory, fakes for every seam.</summary>
internal sealed class PosHarness
{
    public const int Admin = 1, Cashier = 10, Cashier2 = 11, Supervisor = 20, NoPos = 30;

    public static readonly PosCaller AdminCaller = new(Admin, PosPermissions.All.ToHashSet());

    public Guid Org { get; }
    public string DbName { get; } = Guid.NewGuid().ToString();
    public StaticTenantContext Tenant { get; }
    public PosDbContext Db { get; }
    public FakeClock Clock { get; } = new();
    public FakePins Pins { get; } = new();
    public FakeHub Hub { get; } = new();
    public FakeGate Gate { get; } = new();
    public FakeWarehouses Warehouses { get; } = new();
    public FakeTaxCodes TaxCodes { get; } = new();
    public FakeNotifications Notifications { get; } = new();
    public PosReadModel Read { get; }
    public PosStoreService Stores { get; }
    public PosSessionService Sessions { get; }
    public PosShiftService Shifts { get; }
    public PosShiftTotals Totals { get; }
    public PosNumberSequenceService Numbers { get; }
    public Guid VatUuid { get; } = Guid.NewGuid();
    public FakeCatalog Catalog { get; } = new();
    public FakeStock Stock { get; } = new();
    public FakeCustomers Customers { get; } = new();
    public FakeBalances Balances { get; } = new();
    public FakeFinance Finance { get; }
    public PosPostingService Posting { get; }
    public PosChangeListener Changes { get; }
    public PosOrderService Orders { get; }
    public PosSettingsService Settings { get; }
    public PosSyncService Sync { get; }
    public PosReturnPolicyService Policies { get; }
    public PosStoreCreditService Credit { get; }

    public PosHarness(Guid? org = null, string? dbName = null)
    {
        Org    = org ?? Guid.NewGuid();
        DbName = dbName ?? DbName;
        Tenant = new StaticTenantContext { OrganizationId = Org };
        Db     = NewDb(Tenant);
        TaxCodes.Codes.Add(new TaxCodeInfo(VatUuid, "VAT17", "Sales tax 17%", 17m, TaxCodeUsage.Sales, true, true));
        Pins.AddUser(Admin, "Admin", [.. PosPermissions.All]);
        Pins.AddUser(Cashier, "Cathy", PosPermissions.OrderCreate);
        Pins.AddUser(Cashier2, "Carl", PosPermissions.OrderCreate);
        Pins.AddUser(Supervisor, "Sue", PosPermissions.OrderCreate, PosPermissions.ShiftOpen, PosPermissions.ShiftClose, PosPermissions.CashMovement);
        Pins.AddUser(NoPos, "Nora");

        Read     = new PosReadModel(Db, Tenant, Pins, TaxCodes, clock: Clock);
        Numbers  = new PosNumberSequenceService(Db);
        Totals   = new PosShiftTotals(Db);
        Stores   = new PosStoreService(Db, Tenant, Read, Hub, Pins, Warehouses, TaxCodes, Gate);
        Sessions = new PosSessionService(Db, Tenant, Read, Pins);
        Shifts   = new PosShiftService(Db, Tenant, Read, Numbers, Totals);

        Finance  = new FakeFinance(Balances);
        Posting  = new PosPostingService(Db, Stock, Finance, Notifications, Clock);
        Changes  = new PosChangeListener(Db, Hub, Catalog, Stock, Customers, Balances);
        Orders   = new PosOrderService(Db, Tenant, Read, Numbers, Totals, Posting, Catalog, Customers, Balances, Pins, Gate, Notifications, Finance, Changes,
            hub: Hub);
        Settings = new PosSettingsService(Db, Tenant, Catalog, Customers, Changes, Clock);
        Sync     = new PosSyncService(Db, Tenant, Read, Catalog, Stock, Customers, Balances, Pins, Gate);
        Policies = new PosReturnPolicyService(Db, Tenant, Read, Hub);
        Credit   = new PosStoreCreditService(Db, Tenant, Read, Numbers, Hub, Customers, Gate);
    }

    // ── POS-B helpers ──

    public static readonly PosCaller CashierCaller = new(Cashier, new HashSet<string> { PosPermissions.OrderCreate });
    public static readonly PosCaller DiscountCaller = new(Cashier, new HashSet<string> { PosPermissions.OrderCreate, PosPermissions.OrderDiscount });

    /// <summary>A sale of the given lines paid in cash (exact amount unless payments are given).</summary>
    public static PosSyncOrderRequest Sale(PosTerminalModel terminal, PosShiftModel shift, IEnumerable<(PosCatalogVariant V, decimal Qty)> lines,
        decimal? cash = null, string? localId = null, bool offline = false, Guid? customer = null, params PosSyncPaymentRequest[] payments)
    {
        var req = new PosSyncOrderRequest
        {
            LocalId = localId ?? Guid.NewGuid().ToString("N"), TerminalUuid = terminal.Uuid, ShiftUuid = shift.Uuid, IsOffline = offline,
            CustomerUuid = customer, CreatedAtLocal = new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.Zero),
            Lines = lines.Select((l, i) => new PosSyncLineRequest
            {
                LocalLineId = $"L{i}", VariantUuid = l.V.VariantUuid, Quantity = l.Qty, UnitPrice = l.V.Price
            }).ToList()
        };
        req.Payments = payments.Length > 0 ? payments.ToList()
            : [new PosSyncPaymentRequest { LocalId = Guid.NewGuid().ToString("N"), Method = PosPaymentMethod.Cash, Amount = cash ?? 0m }];
        return req;
    }

    public static PosSyncPaymentRequest Pay(string method, decimal amount, decimal change = 0m) =>
        new() { LocalId = Guid.NewGuid().ToString("N"), Method = method, Amount = amount, ChangeGiven = change };

    /// <summary>Exclusive 17%: 2 × 10.00 → 23.40.</summary>
    public async Task<(PosStoreModel Store, PosTerminalModel Terminal, PosShiftModel Shift, PosCatalogVariant Item)> SellingSetupAsync()
    {
        var (store, terminal, shift) = await OpenShiftAsync();
        var item = Catalog.Add("A", 10m);
        return (store, terminal, shift, item);
    }

    public PosDbContext NewDb(ITenantContext tenant) =>
        new(new DbContextOptionsBuilder<PosDbContext>().UseInMemoryDatabase(DbName).Options, tenant);

    public static PosCaller Caller(int userId, params string[] permissions) => new(userId, permissions.ToHashSet());

    public PosStoreUpsertRequest StoreRequest(string code, Guid? warehouse = null, string? timezone = null) => new()
    {
        Code = code, Name = $"Store {code}", WarehouseUuid = warehouse ?? Warehouses.Add("W" + code), Timezone = timezone, VarianceThreshold = 5m
    };

    public async Task<PosStoreModel> StoreAsync(string code = "S1", string? timezone = null) =>
        await Stores.CreateStoreAsync(StoreRequest(code, timezone: timezone), Admin);

    public async Task<PosTerminalModel> TerminalAsync(Guid storeUuid, string code = "T1") =>
        await Stores.CreateTerminalAsync(storeUuid, new PosTerminalCreateRequest { Code = code, Name = $"Till {code}" }, Admin);

    public async Task StaffAsync(Guid storeUuid, int userId, string role = PosRole.Cashier, string? pin = null) =>
        await Stores.UpsertStaffAsync(storeUuid, userId, new PosStaffUpsertRequest { PosRole = role, Pin = pin }, Admin);

    /// <summary>Store S1 with terminal T1, cashier (PIN 1111), supervisor (PIN 2222) and an open shift (float 100).</summary>
    public async Task<(PosStoreModel Store, PosTerminalModel Terminal, PosShiftModel Shift)> OpenShiftAsync(decimal openingFloat = 100m)
    {
        var store = await StoreAsync();
        var terminal = await TerminalAsync(store.Uuid);
        await StaffAsync(store.Uuid, Cashier, pin: "1111");
        await StaffAsync(store.Uuid, Supervisor, PosRole.Supervisor, "2222");
        var shift = await Shifts.OpenAsync(terminal.Uuid, new PosOpenShiftRequest { OpeningFloat = openingFloat }, Caller(Supervisor, PosPermissions.ShiftOpen));
        return (store, terminal, shift);
    }

    /// <summary>Seeds an order with payments straight into the tables (POS-B's job), for the totals.</summary>
    public async Task<PosOrder> OrderAsync(Guid shiftUuid, string type, decimal grandTotal, string status = PosOrderStatus.Completed,
        int? sessionId = null, params (string Method, decimal Amount, decimal Change)[] tenders)
    {
        var shift = await Db.Shifts.FirstAsync(s => s.Uuid == shiftUuid);
        var order = new PosOrder
        {
            OrganizationId = Org, OrderNumber = $"POS-{Guid.NewGuid():N}"[..20], LocalId = Guid.NewGuid().ToString(), OrderType = type,
            Status = status, StoreId = shift.StoreId, TerminalId = shift.TerminalId, ShiftId = shift.Id, SessionId = sessionId,
            CashierUserId = Cashier, CustomerUuid = Guid.NewGuid(), CustomerCode = "C-WALKIN", CustomerName = "Walk-in", GrandTotal = grandTotal
        };
        foreach (var (method, amount, change) in tenders)
            order.Payments.Add(new PosPayment
            {
                OrganizationId = Org, ShiftId = shift.Id, Method = method, Amount = amount, ChangeGiven = change, IsRefund = type == PosOrderType.Return
            });
        Db.Orders.Add(order);
        await Db.SaveChangesAsync();
        return order;
    }
}

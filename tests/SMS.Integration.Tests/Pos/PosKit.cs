using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.RouteClassification;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.ServiceOrders;

namespace SMS.Integration.Tests.Pos;

/// <summary>A user of the POS organization: the user id, a real login (password) and the email.</summary>
internal sealed record PosUser(int Id, string Email, HttpClient Client);

/// <summary>
/// One A38 world in its own ENTERPRISE organization created through the real create path (MODULE_POS licensed from the plan
/// template, D-20): PKR sale base, GST17 (default sales, 17 %), a stocked warehouse, a vendor, POS products
/// (<see cref="A"/> 10.00 and <see cref="B"/> 5.99 with 100 on hand at 4.00 / 2.00, <see cref="D"/> 3.33 with 100 at 1.00,
/// <see cref="Zero"/> 7.00 never stocked, <see cref="Hidden"/> 8.00 not available for POS), a store on the warehouse, a
/// terminal, a POS_CASHIER (PIN 1234) and a POS_SUPERVISOR (PIN 5678) as store staff. The org admin (<see cref="K"/>) holds
/// every POS permission (POS_STORE_MANAGE: may act without being staff).
/// </summary>
internal sealed record PosWorld(
    ModOrg O, Warehouse Wh, Partner Vendor, Product A, Product B, Product D, Product Zero, Product Hidden,
    Guid Store, string StoreCode, Guid Terminal, PosUser Cashier, PosUser Supervisor)
{
    public SapKit K => O.K;
    public SapWebApplicationFactory F => O.K.F;
}

/// <summary>A38 (QA, G rows) — what <see cref="PosE2ETests"/> needs on top of SapKit / Mods / PreOrder.</summary>
internal static class PosKit
{
    public const string Module = "MODULE_POS";
    public const string CashierPin = "1234";
    public const string SupervisorPin = "5678";

    // ── World ────────────────────────────────────────────────────────────────────

    public static async Task<PosWorld> PosWorldAsync(this SapKit root, string prefix, bool inclusive = false, decimal variance = 5m,
        decimal tolerance = 10m, bool stock = true)
    {
        var o = await root.OrgAsync(prefix);
        var k = o.K;
        await k.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        await k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await k.CreateApproverPlaceholdersAsync();
        await k.EnsureNotificationsTableAsync();

        var wh = await k.CreateWarehouseAsync();
        var vendor = await k.CreateVendorAsync($"{prefix} Vendor");
        var a = await k.PosProductAsync($"{prefix} A", 4m, 10.00m);
        var b = await k.PosProductAsync($"{prefix} B", 2m, 5.99m);
        var d = await k.PosProductAsync($"{prefix} D", 1m, 3.33m);
        var zero = await k.PosProductAsync($"{prefix} Zero", 3m, 7.00m);
        var hidden = await k.PosProductAsync($"{prefix} Hidden", 3m, 8.00m, pos: false);
        if (stock) await k.StockUpAsync(vendor, wh, (a, 100m, 4m), (b, 100m, 2m), (d, 100m, 1m));

        var code = $"S{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var store = await k.Ok(k.CreateStore(code, wh.Uuid, inclusive, variance, tolerance), "create store");
        var terminal = await k.Ok(k.Post($"/api/pos/stores/{store.G("uuid")}/terminals", new { code = "T1", name = "Till 1" }), "create terminal");

        var cashier = await k.PosUserAsync("POS_CASHIER", "cashier");
        var supervisor = await k.PosUserAsync("POS_SUPERVISOR", "super");
        await k.Ok(k.Staff(store.G("uuid"), cashier.Id, "CASHIER", CashierPin), "cashier as staff");
        await k.Ok(k.Staff(store.G("uuid"), supervisor.Id, "SUPERVISOR", SupervisorPin), "supervisor as staff");

        return new PosWorld(o, wh, vendor, a, b, d, zero, hidden, store.G("uuid"), code, terminal.G("uuid"), cashier, supervisor);
    }

    public static Task<Api> CreateStore(this SapKit k, string code, Guid warehouse, bool inclusive = false, decimal variance = 5m, decimal tolerance = 10m) =>
        k.Post("/api/pos/stores", new
        {
            code, name = k.Next("Store"), warehouseUuid = warehouse, timezone = "UTC", taxInclusivePricing = inclusive,
            varianceThreshold = variance, offlineCreditTolerancePct = tolerance, autoCloseShiftHours = 12
        });

    public static Task<Api> Staff(this SapKit k, Guid store, int userId, string role, string? pin, HttpClient? client = null) =>
        k.Put($"/api/pos/stores/{store}/staff/{userId}", new { posRole = role, pin }, client);

    /// <summary>A product whose one variant is (or is not) available on POS.</summary>
    public static async Task<Product> PosProductAsync(this SapKit k, string label, decimal purchase, decimal selling, bool pos = true)
    {
        var name = k.Next(label);
        var created = await k.Ok(k.Post("/api/products", new
        {
            Name = name, UomCode = "PCS",
            Variants = new[]
            {
                new
                {
                    VariantName = "Default", PurchasePrice = purchase, SellingPrice = (decimal?)selling, IsDefault = true,
                    IsAvailableForRetail = true, IsAvailableForPos = pos, IsAvailableForMirMiv = true
                }
            }
        }), $"create product {name}");
        var id = created.I("id");
        var detail = await k.Ok(k.Get($"/api/products/{id}"), "read product");
        return new Product(id, detail.G("uuid"), detail.A("variants").Single().G("uuid"), name);
    }

    /// <summary>A user in a global built-in role (by RoleCode), created by the org admin, with a real login.</summary>
    public static async Task<PosUser> PosUserAsync(this SapKit k, string roleCode, string tag)
    {
        var roleId = Convert.ToInt32((await k.F.QueryAsync("SELECT RoleID FROM auth.Roles WHERE RoleCode = @c", ("@c", roleCode))).Single()["RoleID"]);
        var email = $"{tag}-{Guid.NewGuid():N}@a38-qa.test";
        await k.CreateUserAsync(roleId, email);
        await k.F.SetPasswordAsync(email, "A38Qa@12345!");
        var client = k.F.CreateBearerClient(await k.F.LoginAsync(email, "A38Qa@12345!"));
        return new PosUser(ServiceKit.AdminUserId(client), email, client);
    }

    public static int UserIdOf(string token) =>
        int.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.First(c => c.Type is "sub" or "nameid" or "userId").Value);

    // ── PIN, shifts, sessions ────────────────────────────────────────────────────

    public static Task<Api> PinSignIn(this PosWorld w, string pin, HttpClient device, Guid? terminal = null, int? userId = null) =>
        w.K.Post("/api/pos/auth/pin", new { terminalUuid = terminal ?? w.Terminal, userId, pin }, device);

    /// <summary>PIN sign-in with the supervisor's (device) token; a client carrying the cashier's own access token.</summary>
    public static async Task<HttpClient> CashierClientAsync(this PosWorld w, string pin = CashierPin)
    {
        var signIn = await w.K.Ok(w.PinSignIn(pin, w.Supervisor.Client), "PIN sign-in");
        return w.F.CreateBearerClient(signIn.S("accessToken")!);
    }

    public static Task<Api> OpenShift(this PosWorld w, decimal openingFloat, HttpClient? client = null, Guid? terminal = null) =>
        w.K.Post($"/api/pos/terminals/{terminal ?? w.Terminal}/shifts", new { openingFloat }, client ?? w.Supervisor.Client);

    public static async Task<Guid> OpenShiftAsync(this PosWorld w, decimal openingFloat = 200m, Guid? terminal = null) =>
        (await w.K.Ok(w.OpenShift(openingFloat, terminal: terminal), "open shift")).G("uuid");

    public static Task<JsonElement> ShiftAsync(this PosWorld w, Guid shift) => w.K.Ok(w.K.Get($"/api/pos/shifts/{shift}"), "read shift");

    public static Task<Api> CloseShift(this PosWorld w, Guid shift, decimal counted, string? note = null) =>
        w.K.Patch($"/api/pos/shifts/{shift}/close", new { actualCashClosing = counted, varianceNote = note }, w.Supervisor.Client);

    // ── Orders ───────────────────────────────────────────────────────────────────

    public static object Line(Product p, decimal qty, decimal price, string discountType = "NONE", decimal discountValue = 0m) =>
        new { localLineId = Guid.NewGuid().ToString("N"), variantUuid = p.VariantUuid, quantity = qty, unitPrice = price, discountType, discountValue };

    public static object Pay(string method, decimal amount, decimal change = 0m, string? reference = null) =>
        new { localId = Guid.NewGuid().ToString("N"), method, amount, changeGiven = change, reference };

    public static object Order(this PosWorld w, Guid shift, object[] lines, object[] payments, bool offline = false, Guid? customer = null,
        string orderDiscountType = "NONE", decimal orderDiscountValue = 0m, string? localId = null, DateTimeOffset? at = null,
        Guid? session = null, int? cashierUserId = null, object? clientTotals = null, bool offlineCredit = false) => new
    {
        localId = localId ?? Guid.NewGuid().ToString("N"), terminalUuid = w.Terminal, shiftUuid = shift, sessionUuid = session,
        cashierUserId, createdAtLocal = at ?? DateTimeOffset.UtcNow, isOffline = offline, customerUuid = customer,
        lines, orderDiscountType, orderDiscountValue, payments, offlineCredit, clientTotals,
        offlineNumber = offline ? $"POS-{w.StoreCode}-OFF-{Guid.NewGuid():N}"[..(w.StoreCode.Length + 17)].ToUpperInvariant() : null
    };

    public static Task<Api> Sync(this PosWorld w, object order, HttpClient? client = null) => w.K.Post("/api/pos/orders/sync", order, client);

    /// <summary>Synced and successful; returns the SyncResult.</summary>
    public static async Task<JsonElement> SellAsync(this PosWorld w, object order, HttpClient? client = null, string what = "sync order")
    {
        var r = await w.K.Ok(w.Sync(order, client), what);
        r.B("success").Should().BeTrue($"{what} — {J.Short(r)}");
        return r;
    }

    public static Task<JsonElement> OrderAsync(this PosWorld w, Guid order) => w.K.Ok(w.K.Get($"/api/pos/orders/{order}"), "read POS order");

    public static List<string> Flags(this JsonElement e) => e.A("flags").Select(f => f.GetString()!).ToList();

    // ── What only SQL shows ──────────────────────────────────────────────────────

    public static async Task<decimal> OnHandAsync(this PosWorld w, Product p)
    {
        var rows = await w.F.QueryAsync(
            "SELECT ISNULL(SUM(i.QtyOnHand), 0) AS Q FROM inventory.InventoryItems i JOIN inventory.ProductVariants v ON v.Id = i.VariantId " +
            "JOIN inventory.Warehouses wh ON wh.Id = i.WarehouseId WHERE v.Uuid = @v AND wh.Uuid = @w", ("@v", p.VariantUuid), ("@w", w.Wh.Uuid));
        return Convert.ToDecimal(rows[0]["Q"]);
    }

    /// <summary>Inventory ledger rows of a POS document: (variant, type, in, out).</summary>
    public static async Task<List<(Guid Variant, string Type, decimal In, decimal Out)>> StockLedgerAsync(this PosWorld w, Guid reference) =>
        (await w.F.QueryAsync(
            "SELECT v.Uuid AS V, l.TransactionType AS T, ISNULL(l.QuantityIn, 0) AS QI, ISNULL(l.QuantityOut, 0) AS QO " +
            "FROM inventory.InventoryLedgerEntries l JOIN inventory.ProductVariants v ON v.Id = l.VariantId WHERE l.ReferenceId = @r", ("@r", reference)))
        .Select(r => ((Guid)r["V"]!, (string)r["T"]!, Convert.ToDecimal(r["QI"]), Convert.ToDecimal(r["QO"]))).ToList();

    /// <summary>Master product ledger rows of a POS document: (type, in, out, source, destination).</summary>
    public static async Task<List<(string Type, decimal In, decimal Out, string Source, string Destination)>> MasterLedgerAsync(this PosWorld w, Guid reference) =>
        (await w.F.QueryAsync(
            "SELECT TransactionType AS T, ISNULL(QuantityIn, 0) AS QI, ISNULL(QuantityOut, 0) AS QO, SourceType AS S, DestinationType AS D " +
            "FROM finance.master_product_ledger WHERE ReferenceId = @r", ("@r", reference)))
        .Select(r => ((string)r["T"]!, Convert.ToDecimal(r["QI"]), Convert.ToDecimal(r["QO"]), (string)r["S"]!, (string)r["D"]!)).ToList();

    public static async Task<Dictionary<string, object?>> InvoiceRowAsync(this PosWorld w, Guid invoice) =>
        (await w.F.QueryAsync("SELECT * FROM finance.sales_invoices WHERE UUID = @u", ("@u", invoice))).Single();

    public static async Task<int> InvoiceCountForSourceAsync(this PosWorld w, Guid source) =>
        Convert.ToInt32((await w.F.QueryAsync("SELECT COUNT(*) AS N FROM finance.sales_invoices WHERE SourceUuid = @s", ("@s", source)))[0]["N"]);

    /// <summary>Customer payments booked for POS tenders: (tender uuid → (payment uuid, amount, method, status)).</summary>
    public static async Task<Dictionary<Guid, (Guid Uuid, decimal Amount, string Method, string Status)>> CustomerPaymentsAsync(this PosWorld w, IEnumerable<Guid> tenders)
    {
        var result = new Dictionary<Guid, (Guid, decimal, string, string)>();
        foreach (var t in tenders)
        {
            var rows = await w.F.QueryAsync("SELECT UUID, Amount, PaymentMethod, Status FROM finance.customer_payments WHERE SourceUuid = @s", ("@s", t));
            if (rows.Count == 1)
                result[t] = ((Guid)rows[0]["UUID"]!, Convert.ToDecimal(rows[0]["Amount"]), (string)rows[0]["PaymentMethod"]!, (string)rows[0]["Status"]!);
        }
        return result;
    }

    /// <summary>Customer ledger rows of the customer: (type, reference, debit, credit).</summary>
    public static async Task<List<(string Type, Guid Reference, decimal Debit, decimal Credit)>> CustomerLedgerAsync(this PosWorld w, Guid customer) =>
        (await w.F.QueryAsync(
            "SELECT EntryType AS T, ReferenceId AS R, DebitAmount AS D, CreditAmount AS C FROM finance.customer_ledger " +
            "WHERE PartnerId = @p AND OrganizationId = @o ORDER BY SequenceNo", ("@p", customer), ("@o", w.O.OrgId)))
        .Select(r => ((string)r["T"]!, (Guid)r["R"]!, Convert.ToDecimal(r["D"]), Convert.ToDecimal(r["C"]))).ToList();

    /// <summary>Finance product ledger rows of a variant: (type, reference, qty, running qty).</summary>
    public static async Task<List<(string Type, Guid Reference, decimal Qty, decimal Running)>> ProductLedgerAsync(this PosWorld w, Product p) =>
        (await w.F.QueryAsync(
            "SELECT EntryType AS T, ReferenceId AS R, Quantity AS Q, RunningQty AS RQ FROM finance.product_ledger WHERE VariantUuid = @v ORDER BY SequenceNo",
            ("@v", p.VariantUuid)))
        .Select(r => ((string)r["T"]!, (Guid)r["R"]!, Convert.ToDecimal(r["Q"]), Convert.ToDecimal(r["RQ"]))).ToList();

    public static async Task<List<(int User, string Message)>> NotificationsAsync(this PosWorld w, string type, Guid entity) =>
        (await w.F.QueryAsync("SELECT UserId, Message FROM notifications.notifications WHERE Type = @t AND EntityUuid = @e",
            ("@t", type), ("@e", entity.ToString())))
        .Select(r => (Convert.ToInt32(r["UserId"]), (string)r["Message"]!)).ToList();

    public static async Task<int> NotificationCountForUserAsync(this PosWorld w, string type, int user) =>
        Convert.ToInt32((await w.F.QueryAsync("SELECT COUNT(*) AS N FROM notifications.notifications WHERE Type = @t AND UserId = @u",
            ("@t", type), ("@u", user)))[0]["N"]);

    /// <summary>An internal POS job (StaleShiftAlertJob, PosPostingRetryJob, …) from a fresh scope, as Hangfire would run it.</summary>
    public static async Task<int> RunPosJobAsync(this SapWebApplicationFactory f, string typeName)
    {
        var type = typeof(SMS.Modules.Pos.IPosModule).Assembly.GetType($"SMS.Modules.Pos.Services.{typeName}", throwOnError: true)!;
        await using var scope = f.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService(type);
        return await (Task<int>)type.GetMethod("RunAsync")!.Invoke(job, null)!;
    }
}

/// <summary>
/// A minimal SignalR client (JSON protocol over the test server's WebSocket, negotiation skipped) — the test project has no
/// SignalR client package. Enough to connect with a JWT (access_token query, as the PWA does), invoke hub methods and see
/// server pushes and an aborted connection.
/// </summary>
internal sealed class HubProbe : IAsyncDisposable
{
    private const char Rs = '\u001e';
    private readonly WebSocket _ws;
    private readonly ConcurrentQueue<JsonElement> _messages = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reader;
    private int _id;

    public bool Closed { get; private set; }

    private HubProbe(WebSocket ws)
    {
        _ws = ws;
        _reader = Task.Run(ReadLoopAsync);
    }

    public static async Task<HubProbe> ConnectAsync(SapWebApplicationFactory f, string token, string path = "/hubs/pos")
    {
        var client = f.Server.CreateWebSocketClient();
        var ws = await client.ConnectAsync(new Uri($"ws://localhost{path}?access_token={Uri.EscapeDataString(token)}"), CancellationToken.None);
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}" + Rs), WebSocketMessageType.Text, true, CancellationToken.None);
        var probe = new HubProbe(ws);
        // the handshake answer is "{}" (an error names it); give OnConnectedAsync a moment to accept or abort
        await SapKit.WaitForAsync(() => Task.FromResult(probe._messages.Any(m => m.ValueKind == JsonValueKind.Object && !m.Has("type")) || probe.Closed),
            x => x, "SignalR handshake", 10);
        await Task.Delay(300);
        return probe;
    }

    public static string TokenOf(HttpClient client) => client.DefaultRequestHeaders.Authorization!.Parameter!;

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        var text = new StringBuilder();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(buffer, _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) { Closed = true; return; }
                text.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                var all = text.ToString();
                var last = all.LastIndexOf(Rs);
                if (last < 0) continue;
                foreach (var part in all[..last].Split(Rs, StringSplitOptions.RemoveEmptyEntries))
                {
                    var msg = JsonDocument.Parse(part).RootElement.Clone();
                    if (msg.Has("type") && msg.I("type") == 7) Closed = true; // Close
                    _messages.Enqueue(msg);
                }
                text.Clear().Append(all[(last + 1)..]);
            }
        }
        catch
        {
            Closed = true;
        }
    }

    /// <summary>Invokes a hub method; returns the completion message (with "error" when the hub threw).</summary>
    public async Task<JsonElement> InvokeAsync(string target, params object[] args)
    {
        var id = Interlocked.Increment(ref _id).ToString();
        var payload = JsonSerializer.Serialize(new { type = 1, invocationId = id, target, arguments = args }) + Rs;
        await _ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, CancellationToken.None);
        return await SapKit.WaitForAsync(
            () => Task.FromResult(_messages.FirstOrDefault(m => m.Has("type") && m.I("type") == 3 && m.S("invocationId") == id)),
            m => m.ValueKind == JsonValueKind.Object || Closed, $"hub completion of {target}", 15);
    }

    /// <summary>The first server push of <paramref name="target"/> (its first argument), or null when none arrived in time.</summary>
    public async Task<JsonElement?> WaitForPushAsync(string target, int seconds = 10, Func<JsonElement, bool>? where = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var m = _messages.Where(x => x.Has("type") && x.I("type") == 1 && x.S("target") == target)
                .Select(x => x.A("arguments").FirstOrDefault())
                .FirstOrDefault(a => a.ValueKind == JsonValueKind.Object && (where is null || where(a)));
            if (m.ValueKind == JsonValueKind.Object) return m;
            await Task.Delay(100);
        }
        return null;
    }

    /// <summary>Every push of <paramref name="target"/> received so far (first arguments).</summary>
    public List<JsonElement> Pushes(string target) =>
        _messages.Where(x => x.Has("type") && x.I("type") == 1 && x.S("target") == target).Select(x => x.A("arguments").FirstOrDefault()).ToList();

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch { /* best effort */ }
        try { await _reader; } catch { /* cancelled */ }
        _ws.Dispose();
    }
}

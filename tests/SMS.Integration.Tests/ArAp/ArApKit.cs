using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.FinancialPosting;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.JournalEngine;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;

namespace SMS.Integration.Tests.ArAp;

/// <summary>An A48 test organization: the A44 org (PKR base, GST17/PGST17, approver placeholders) and a stocked world.</summary>
internal sealed record ArOrg(FpOrg Fp, Warehouse Wh, Partner Vendor, Partner Customer, Product Item)
{
    public SapKit K => Fp.K;
    public Guid OrgId => Fp.OrgId;
    public SapWebApplicationFactory F => Fp.F;
    public Guid Pkr => Fp.O.Pkr;
}

/// <summary>
/// A48 (QA) — what the ArAp E2E classes share on top of <see cref="SapKit"/>, <see cref="Fp"/> and <see cref="Je"/>.
/// Document set-up goes through the existing SCM endpoints; only what time alone would do (an invoice 45 days overdue,
/// an expired temporary limit) is moved in SQL afterwards. A48 routes, bodies and messages follow
/// docs/ar-ap-subledgers/API-CONTRACT.md (see the second half of this file); JSON is read case-insensitively.
/// </summary>
internal static partial class Ar
{
    public static DateTime Today => DateTime.UtcNow.Date;
    public static string Iso(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ── organization ─────────────────────────────────────────────────────────────

    /// <summary>A new org (Finance on) with a warehouse, vendor, customer and an item (price 100, cost 60) with 200 in stock.</summary>
    public static async Task<ArOrg> NewArOrgAsync(this SapKit root, string prefix, bool gl = false, decimal stock = 200m, bool qbo = false)
    {
        FpOrg o;
        if (gl) o = (await root.NewJeOrgAsync(prefix, qbo: qbo)).Fp;
        else
        {
            o = await root.NewOrgAsync(prefix);
            if (qbo) o = await o.ConnectLiveAsync();
        }
        var (wh, vendor, customer, item) = await o.K.StockedWorldAsync(prefix, price: 100m, cost: 60m, qty: stock);
        // The stock-up GRN raises its supplier invoice in the background; wait for it, so a bill the test creates next does
        // not race it for the same count-based invoice number (QA-2).
        try
        {
            await SapKit.WaitForAsync(() => o.F.RowCountAsync("SELECT COUNT(*) FROM finance.invoices WHERE OrganizationId = @o", ("@o", o.OrgId)),
                n => n > 0, "the stock-up GRN's automatic invoice", seconds: 60);
        }
        catch (TimeoutException) { /* none raised: nothing to race */ }
        return new ArOrg(o, wh, vendor, customer, item);
    }

    /// <summary>A further customer of the org.</summary>
    public static Task<Partner> CustomerAsync(this ArOrg o, string label) => o.K.CreateCustomerAsync(label);

    /// <summary>A further vendor of the org.</summary>
    public static Task<Partner> VendorAsync(this ArOrg o, string label) => o.K.CreateVendorAsync(label);

    // ── partner master (D-4: the permanent limit stays on BusinessPartner.CreditLimit) ──

    /// <summary>
    /// The permanent credit limit on the partner master. The customer API applies it only under FEATURE_CREDIT_MANAGEMENT,
    /// so the test sets the column directly (it is plain master data; nothing A48 caches it beyond a request).
    /// </summary>
    public static async Task SetCreditLimitAsync(this ArOrg o, Partner p, decimal? limit)
    {
        var n = await o.F.ExecuteAsync("UPDATE suppliers.BusinessPartners SET CreditLimit = @l WHERE UUID = @u",
            ("@l", (object?)limit ?? DBNull.Value), ("@u", p.Uuid));
        n.Should().Be(1, $"partner {p.Name} exists");
    }

    /// <summary>The partner's e-mail on the master (statements and dunning letters go there).</summary>
    public static async Task<string> SetEmailAsync(this ArOrg o, Partner p)
    {
        var email = $"ar-{Guid.NewGuid():N}"[..14] + "@a48-qa.test";
        (await o.F.ExecuteAsync("UPDATE suppliers.BusinessPartners SET Email = @e WHERE UUID = @u", ("@e", email), ("@u", p.Uuid))).Should().Be(1);
        return email;
    }

    // ── selling ──────────────────────────────────────────────────────────────────

    /// <summary>A sale order of <paramref name="qty"/> × item (no tax) — DRAFT, not confirmed.</summary>
    public static Task<Guid> DraftSoAsync(this ArOrg o, Partner c, decimal qty) =>
        o.K.CreateSaleOrderAsync(c, o.Pkr, SapKit.Line(o.Item, qty));

    public static Task<Api> TryConfirm(this ArOrg o, Guid so, HttpClient? client = null) =>
        o.K.Post($"/api/sale-orders/{so}/confirm", null, client);

    public static async Task<string> SoStatusAsync(this ArOrg o, Guid so) => (await o.K.GetSaleOrderAsync(so)).S("status")!;

    /// <summary>SO → confirm → deliver → invoice → issue, no tax: an open receivable of qty × 100. Returns the invoice uuid.</summary>
    public static async Task<Guid> IssuedInvoiceAsync(this ArOrg o, Partner c, decimal qty)
    {
        var (_, _, inv) = await o.K.SellAsync(c, o.Pkr, SapKit.Line(o.Item, qty));
        return inv;
    }

    /// <summary>Moves an issued sales invoice in time: invoice date and due date <paramref name="dueDaysAgo"/> (+ terms) back.</summary>
    public static async Task BackdateSalesInvoiceAsync(this ArOrg o, Guid invoice, int dueDaysAgo, int termDays = 30)
    {
        var due = Today.AddDays(-dueDaysAgo);
        var n = await o.F.ExecuteAsync(
            "UPDATE finance.sales_invoices SET DueDate = @d, InvoiceDate = @i WHERE UUID = @u",
            ("@d", due), ("@i", due.AddDays(-termDays)), ("@u", invoice));
        n.Should().Be(1);
        // The customer ledger row of the issue carries the document date; keep it consistent for statements.
        await o.F.ExecuteAsync("UPDATE finance.customer_ledger SET EntryDate = @i WHERE ReferenceId = @u AND EntryDate > @i",
            ("@i", due.AddDays(-termDays)), ("@u", invoice));
    }

    public static async Task<JsonElement> PayCustomerAsync(this ArOrg o, Partner c, decimal amount, params (Guid Invoice, decimal Amount)[] alloc) =>
        await o.K.RecordPaymentAsync(c, amount, "CASH", alloc);

    public static async Task<decimal> SalesBalanceAsync(this ArOrg o, Guid invoice) => (await o.K.GetInvoiceAsync(invoice)).D("balanceDue");

    // ── buying ───────────────────────────────────────────────────────────────────

    public const string SupplierInvoices = "/api/finance/invoices";

    /// <summary>PO of qty × price → approved → sent; returns the PO and its single line uuid.</summary>
    public static async Task<(Guid Po, Guid PoLine)> OrderedAsync(this ArOrg o, Partner vendor, decimal qty, decimal price)
    {
        var po = await o.K.CreatePurchaseOrderAsync(vendor, o.Wh, (o.Item, qty, price));
        await o.K.SubmitApproveAndSendPoAsync(po);
        var line = (await o.K.Ok(o.K.Get($"/api/purchase-orders/{po}"), "read PO")).A("lines").Single().G("uuid");
        return (po, line);
    }

    /// <summary>A GRN of <paramref name="qty"/> on the PO; the invoice its approval raised in the background is rejected
    /// (so the test's own invoice is the only one billing the receipt). Returns the GRN and its single line.</summary>
    public static async Task<(Guid Grn, Guid GrnLine)> ReceivedAsync(this ArOrg o, Guid po, decimal qty, bool rejectAutoInvoice = true)
    {
        var grn = await o.K.ReceiveAllAsync(po, o.Wh, qty);
        if (rejectAutoInvoice)
        {
            List<Dictionary<string, object?>> rows;
            try
            {
                rows = await SapKit.WaitForAsync(
                    () => o.F.QueryAsync("SELECT UUID FROM finance.invoices WHERE GrnUuid = @g AND IsDelete = 0", ("@g", grn)),
                    r => r.Count > 0, $"the invoice auto-created from GRN {grn}", seconds: 60);
            }
            catch (TimeoutException)
            {
                rows = []; // nothing raised by itself (yet) — then nothing else bills the receipt either
            }
            foreach (var row in rows)
                await o.K.Ok(o.K.Post($"{SupplierInvoices}/{(Guid)row["UUID"]!}/reject", new { Reason = "set aside by A48 QA" }), "reject auto invoice");
        }
        var line = (await o.K.Ok(o.K.Get($"/api/grns/{grn}"), "read GRN")).A("lines").Single().G("uuid");
        return (grn, line);
    }

    /// <summary>A supplier invoice (draft / pending match) on a PO (+GRN), one line.</summary>
    public static Task<Api> TrySupplierInvoice(this ArOrg o, Partner vendor, string number, Guid? po, Guid? poLine, Guid? grn, Guid? grnLine,
        decimal qty, decimal price, DateTime? invoiceDate = null, DateTime? dueDate = null, string currency = "PKR") =>
        o.K.Post(SupplierInvoices, new
        {
            SupplierInvoiceNo = number, SupplierId = vendor.Uuid, PoUuid = po, GrnUuid = grn,
            InvoiceDate = invoiceDate ?? Today, ReceivedDate = invoiceDate ?? Today, DueDate = dueDate ?? Today.AddDays(30), Currency = currency,
            Subtotal = qty * price, TaxAmount = 0m,
            Lines = new[] { new { PoLineUuid = poLine, GrnLineUuid = grnLine, ItemDescription = o.Item.Name, QtyInvoiced = qty, UnitPrice = price } }
        });

    public static async Task<Guid> SupplierInvoiceAsync(this ArOrg o, Partner vendor, string number, Guid? po, Guid? poLine, Guid? grn, Guid? grnLine,
        decimal qty, decimal price, DateTime? invoiceDate = null, DateTime? dueDate = null) =>
        (await o.K.Ok(o.TrySupplierInvoice(vendor, number, po, poLine, grn, grnLine, qty, price, invoiceDate, dueDate), "create supplier invoice")).GetGuid();

    /// <summary>A direct (no PO) supplier invoice, approved through the existing path. Due in <paramref name="dueInDays"/>.</summary>
    public static async Task<Guid> ApprovedBillAsync(this ArOrg o, Partner vendor, decimal amount, int dueInDays = 30, DateTime? invoiceDate = null)
    {
        var uuid = (await o.K.Ok(o.K.Post(SupplierInvoices, new
        {
            SupplierInvoiceNo = $"{o.K.Marker}-{Guid.NewGuid():N}"[..20], SupplierId = vendor.Uuid, InvoiceDate = invoiceDate ?? Today,
            ReceivedDate = invoiceDate ?? Today, DueDate = Today.AddDays(dueInDays), Currency = "PKR", Subtotal = amount, TaxAmount = 0m
        }), "supplier invoice")).GetGuid();
        await o.K.Ok(o.K.Post($"{SupplierInvoices}/{uuid}/approve", new { Notes = "a48 qa" }), "approve supplier invoice");
        return uuid;
    }

    public static Task<JsonElement> SupplierInvoiceDetailAsync(this ArOrg o, Guid uuid) =>
        o.K.Ok(o.K.Get($"{SupplierInvoices}/{uuid}"), "read supplier invoice");

    public static async Task BackdateSupplierInvoiceAsync(this ArOrg o, Guid invoice, int dueDaysAgo, int termDays = 30)
    {
        var due = Today.AddDays(-dueDaysAgo);
        (await o.F.ExecuteAsync("UPDATE finance.invoices SET DueDate = @d, InvoiceDate = @i WHERE UUID = @u",
            ("@d", due), ("@i", due.AddDays(-termDays)), ("@u", invoice))).Should().Be(1);
    }

    /// <summary>Live supplier-payment lines (not cancelled/bounced) naming the invoice.</summary>
    public static async Task<int> LivePaymentLinesAsync(this ArOrg o, Guid invoice) =>
        Convert.ToInt32((await o.F.QueryAsync(
            "SELECT COUNT(*) AS N FROM finance.supplier_payment_lines l JOIN finance.supplier_payments p ON p.Id = l.SupplierPaymentId " +
            "WHERE l.InvoiceUuid = @i AND p.Status NOT IN ('CANCELLED', 'BOUNCED')", ("@i", invoice))).Single()["N"]);

    // ── users / timing ───────────────────────────────────────────────────────────

    public static Task<HttpClient> UserWithAsync(this ArOrg o, string tag, params string[] codes) => o.K.LoginWithPermissionsAsync(tag, codes);

    public static string ArtifactsDir
    {
        get
        {
            var d = Path.Combine(Path.GetTempPath(), "a48qa");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static void Log(string file, string line)
    {
        try { File.AppendAllText(Path.Combine(ArtifactsDir, file), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); } catch { /* best effort */ }
    }

    public static async Task<(T Value, TimeSpan Time)> TimeAsync<T>(Func<Task<T>> call, bool warm = true)
    {
        if (warm) await call();
        var sw = Stopwatch.StartNew();
        var v = await call();
        sw.Stop();
        return (v, sw.Elapsed);
    }

    public static void ShouldBeForbidden(this Api api, string because) =>
        api.Status.Should().Be(HttpStatusCode.Forbidden, $"{because} — {api}");

    public static List<JsonElement> Rows(JsonElement r) => Je.Rows(r);
}

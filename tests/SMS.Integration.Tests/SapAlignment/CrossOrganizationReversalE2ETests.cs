using System.Net;
using FluentAssertions;
using Xunit;

namespace SMS.Integration.Tests.SapAlignment;

/// <summary>
/// SAP alignment S-2 / S-7 across organizations, end to end (reported by the security &amp; tenancy auditor):
/// the seeded platform super admin bypasses the tenant query filter, so it can reach another organization's
/// documents. Reversing or cancelling one must not book the opposite entries into the super admin's own
/// organization — it must be refused (404), or at the very least every new row must belong to the document's
/// organization. And an organization's own admin must never reach another organization's documents.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~CrossOrganizationReversalE2ETests</c>.</para>
/// </summary>
public sealed class CrossOrganizationReversalE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public CrossOrganizationReversalE2ETests(SapWebApplicationFactory factory)
    {
        _f    = factory;
        _root = new SapKit(factory, "XO");
    }

    [Fact]
    public async Task The_super_admin_cannot_reverse_another_organizations_supplier_invoice_into_its_own_books()
    {
        await _root.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        var (org2, other) = await SecondOrganizationAsync();

        var vendor = await other.CreateVendorAsync("Org2 Vendor");
        var invoice = (await other.Ok(other.Post("/api/finance/invoices", new
        {
            SupplierInvoiceNo = $"{other.Marker}-1", SupplierId = vendor.Uuid, InvoiceDate = DateTime.UtcNow.Date,
            ReceivedDate = DateTime.UtcNow.Date, DueDate = DateTime.UtcNow.Date.AddDays(30), Currency = "PKR", Subtotal = 1000m, TaxAmount = 0m
        }), "org 2 supplier invoice")).GetGuid();
        await other.Ok(other.Post($"/api/finance/invoices/{invoice}/approve", new { Notes = "ok" }), "org 2 approves");

        // Org 1's admin of an ordinary kind would not even see it; the super admin does.
        var reverse = await _root.Post($"/api/finance/invoices/{invoice}/reverse", new { Reason = "reversed from the platform org" });

        var rows = await _f.QueryAsync(
            "SELECT TransactionType, OrganizationId FROM finance.supplier_ledger_entries WHERE ReferenceId = @i ORDER BY SequenceNo", ("@i", invoice));
        var master = await _f.QueryAsync(
            "SELECT TransactionType, OrganizationId FROM finance.master_financial_ledger WHERE ReferenceId = @i ORDER BY SequenceNo", ("@i", invoice));
        Console.WriteLine($"PROBE super-admin reverse: {reverse}; supplier ledger: " +
                          string.Join(", ", rows.Select(r => $"{r["TransactionType"]}@{r["OrganizationId"]}")) + "; master: " +
                          string.Join(", ", master.Select(r => $"{r["TransactionType"]}@{r["OrganizationId"]}")));

        rows.Concat(master).Should().OnlyContain(r => (Guid)r["OrganizationId"]! == org2,
            "every ledger row of org 2's invoice belongs to org 2 — never to the super admin's organization");
        reverse.Status.Should().Be(HttpStatusCode.NotFound, $"another organization's invoice is not the super admin's to reverse — {reverse}");
        (await other.Ok(other.Get($"/api/finance/invoices/{invoice}"), "org 2 reads it")).S("matchStatus").Should().Be("Approved");
    }

    [Fact]
    public async Task The_super_admin_cannot_cancel_another_organizations_sales_invoice_into_its_own_books()
    {
        await _root.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        var pkr = await _root.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        var (org2, other) = await SecondOrganizationAsync();

        await other.CreateApproverPlaceholdersAsync();
        var wh       = await other.CreateWarehouseAsync();
        var vendor   = await other.CreateVendorAsync("Org2 Stock Vendor");
        var customer = await other.CreateCustomerAsync("Org2 Customer");
        var item     = await other.CreateProductAsync("Org2 Widget", purchasePrice: 60m, sellingPrice: 100m);
        await other.StockUpAsync(vendor, wh, (item, 10m, 60m));
        var (_, _, invoice) = await other.SellAsync(customer, pkr, SapKit.Line(item, 1m, taxPercent: 17m));

        // An organization's own admin never reaches another's documents.
        var otherAdminFromOrg1 = await _root.LoginAsNewUserAsync((int)SMS.Shared.Common.EnumRole.FinanceManager, "org1fin");
        (await _root.Post($"/api/sales-invoices/{invoice}/cancel", new { reason = "not mine" }, otherAdminFromOrg1)).Status
            .Should().Be(HttpStatusCode.NotFound, "a Finance Manager of org 1 cannot see org 2's invoice");

        var cancel = await _root.Post($"/api/sales-invoices/{invoice}/cancel", new { reason = "cancelled from the platform org" });

        var ledger = await _f.QueryAsync(
            "SELECT EntryType, OrganizationId FROM finance.customer_ledger WHERE ReferenceId = @i ORDER BY SequenceNo", ("@i", invoice));
        var stock = await _f.QueryAsync(
            "SELECT EntryType, OrganizationId FROM finance.product_ledger WHERE ReferenceId = @i ORDER BY Id", ("@i", invoice));
        Console.WriteLine($"PROBE super-admin cancel: {cancel}; customer ledger: " +
                          string.Join(", ", ledger.Select(r => $"{r["EntryType"]}@{r["OrganizationId"]}")) + "; product ledger: " +
                          string.Join(", ", stock.Select(r => $"{r["EntryType"]}@{r["OrganizationId"]}")));

        ledger.Concat(stock).Should().OnlyContain(r => (Guid)r["OrganizationId"]! == org2,
            "every ledger row of org 2's invoice belongs to org 2 — never to the super admin's organization");
        cancel.Status.Should().Be(HttpStatusCode.NotFound, $"another organization's invoice is not the super admin's to cancel — {cancel}");
        (await other.GetInvoiceAsync(invoice)).S("status").Should().Be("ISSUED");
    }

    [Fact]
    public async Task An_invoice_cannot_be_raised_against_another_organizations_purchase_order_or_grn()
    {
        await _root.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        await _root.CreateApproverPlaceholdersAsync();
        var wh     = await _root.CreateWarehouseAsync();
        var vendor = await _root.CreateVendorAsync("Org1 Vendor");
        var item   = await _root.CreateProductAsync("Org1 Item", purchasePrice: 300m, sellingPrice: 400m);
        var (po, grn) = await _root.StockUpAsync(vendor, wh, (item, 10m, 300m));
        var poQtyBefore = await PoQtyInvoicedAsync(po);

        var (_, other) = await SecondOrganizationAsync();
        var vendor2 = await other.CreateVendorAsync("Org2 Vendor");

        var viaPo = await other.Post("/api/finance/invoices", new
        {
            SupplierInvoiceNo = $"{other.Marker}-PO", SupplierId = vendor2.Uuid, PoUuid = po, GrnUuid = grn,
            InvoiceDate = DateTime.UtcNow.Date, ReceivedDate = DateTime.UtcNow.Date, DueDate = DateTime.UtcNow.Date.AddDays(30),
            Currency = "PKR", Subtotal = 3000m, TaxAmount = 0m
        });
        viaPo.Status.Should().Be(HttpStatusCode.NotFound, $"org 1's purchase order does not exist for org 2 — {viaPo}");

        var viaGrn = await other.Ok(other.Post("/api/finance/invoices", new
        {
            SupplierInvoiceNo = $"{other.Marker}-GRN", SupplierId = vendor2.Uuid, GrnUuid = grn,
            InvoiceDate = DateTime.UtcNow.Date, ReceivedDate = DateTime.UtcNow.Date, DueDate = DateTime.UtcNow.Date.AddDays(30),
            Currency = "PKR", Subtotal = 3000m, TaxAmount = 0m
        }), "org 2 invoice naming org 1's GRN");
        var inv = await other.Ok(other.Get($"/api/finance/invoices/{viaGrn.GetGuid()}"), "read org 2 invoice");
        inv.D("matchedGrnValue").Should().Be(0m, "org 1's GRN is never read for org 2's match");
        inv.S("grnNumber").Should().BeNull("nor is its number copied onto org 2's invoice");

        await other.Ok(other.Post($"/api/finance/invoices/{viaGrn.GetGuid()}/approve", new { Notes = "ok" }), "org 2 approves");
        (await PoQtyInvoicedAsync(po)).Should().Be(poQtyBefore, "org 2's approval never moves org 1's purchase order");
    }

    private async Task<decimal> PoQtyInvoicedAsync(Guid po) =>
        (decimal)(await _f.QueryAsync(
            "SELECT SUM(l.QtyInvoiced) AS Q FROM demand.purchase_order_lines l JOIN demand.purchase_orders p ON p.Id = l.PurchaseOrderId WHERE p.UUID = @p",
            ("@p", po))).Single()["Q"]!;

    /// <summary>A second organization (ENTERPRISE plan) whose admin has accepted the invitation, and a kit acting as that admin.</summary>
    /// <summary>
    /// A35 P1-14 — an organization created by the super admin (no base currency) gets its own currency setup in the same
    /// request: org currencies and the PKR SYSTEM rate row under ITS id; the super admin's organization is untouched.
    /// (Regression: the org-create transaction stayed attached to TenancyDbContext, so every provisioning handler that
    /// read Tenancy afterwards failed silently.)
    /// </summary>
    [Fact]
    public async Task A35_a_new_organization_gets_its_own_currencies_and_rate_currency_row()
    {
        async Task<int> CountAsync(string sql, Guid org) =>
            Convert.ToInt32((await _f.QueryAsync(sql, ("@o", org)))[0].Values.First());
        const string currencies = "SELECT COUNT(*) AS n FROM finance.org_currencies WHERE OrganizationId = @o";
        const string systemRow  = "SELECT COUNT(*) AS n FROM finance.currency_rates WHERE OrganizationId = @o AND Source = 'SYSTEM' AND CurrencyCode = 'PKR' AND Rate = 1";

        var mineBefore = await CountAsync(currencies, _f.OrganizationId);
        var (org2, kit2) = await SecondOrganizationAsync();

        (await CountAsync(currencies, org2)).Should().BeGreaterThan(0, "the new organization's currencies are seeded on creation");
        (await CountAsync(systemRow, org2)).Should().Be(1, "its rate currency (PKR fallback) has the permanent 1.0 row");
        (await CountAsync(currencies, _f.OrganizationId)).Should().Be(mineBefore, "the super admin's organization is untouched");

        var list = await kit2.Ok(kit2.Get("/api/currencies"), "org 2 currencies");
        list.Items().Should().Contain(c => c.S("code") == "PKR" && c.B("isRateCurrency"));
    }

    private async Task<(Guid OrgId, SapKit Kit)> SecondOrganizationAsync()
    {
        var email = $"org2-{Guid.NewGuid():N}@sap-e2e.test";
        var created = await _root.Ok(_root.Post("/api/system/organizations", new
        {
            OrgCode = $"X{Guid.NewGuid():N}"[..10].ToUpperInvariant(), OrgName = $"{_root.Marker} Org {Guid.NewGuid():N}"[..30], Plan = "ENTERPRISE",
            AdminFirstName = "Other", AdminLastName = "Admin", AdminEmail = email
        }), "create a second organization");
        var orgId = created.G("organizationId");
        orgId.Should().NotBe(_f.OrganizationId);

        await _f.SetPasswordAsync(email, "Org2@12345!");
        await _f.ExecuteAsync("UPDATE auth.UserAccounts SET IsActive = 1 WHERE Email = @e", ("@e", email));
        var client = _f.CreateBearerClient(await _f.LoginAsync(email, "Org2@12345!"));
        return (orgId, new SapKit(_f, "O2", client));
    }
}

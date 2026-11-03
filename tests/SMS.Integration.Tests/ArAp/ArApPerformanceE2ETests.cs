using System.Diagnostics;
using System.Net;
using System.Text;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.JournalEngine;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using Xunit.Abstractions;
using static SMS.Integration.Tests.ArAp.Ar;

namespace SMS.Integration.Tests.ArAp;

/// <summary>
/// A48 (QA) — performance on LocalDB (tasks 594–595). Bulk documents are cloned in SQL from one document made through the API
/// (column lists from sys.columns; every column of a unique index gets a fresh value), then the API is timed after a warm-up.
/// Timings go to the test output and %TEMP%\a48qa\perf.txt. Targets: AR aging over 50 000 open invoices &lt; 5 s;
/// a payment run of 200 vendors / 500 items proposed and executed (timed; fails only when wrong or grossly slow).
/// Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~ArAp.ArApPerformanceE2ETests</c>.
/// </summary>
public sealed class ArApPerformanceE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private static readonly TimeSpan AgingTarget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Gross = TimeSpan.FromMinutes(5);
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;
    private readonly ITestOutputHelper _out;

    public ArApPerformanceE2ETests(SapWebApplicationFactory factory, ITestOutputHelper output)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A48PF");
        _out = output;
    }

    private void Record(string task, string what, TimeSpan t)
    {
        var line = $"A48 perf {task} {what}: {t.TotalMilliseconds:0} ms";
        _out.WriteLine(line);
        Log("perf.txt", line);
    }

    /// <summary>
    /// <paramref name="count"/> copies of row <paramref name="templateId"/> of <paramref name="table"/>. <paramref name="over"/>
    /// sets columns (SQL over alias t and the row number n.n, 1-based); every other column of a unique index (except Id) gets
    /// NEWID() / CONCAT(value, '-', n) so the copies never collide.
    /// </summary>
    private async Task CloneAsync(string table, int templateId, int count, Dictionary<string, string> over)
    {
        var cols = (await _f.QueryAsync(
            "SELECT c.name, t.name AS type FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id " +
            "WHERE c.object_id = OBJECT_ID(@t) AND c.is_identity = 0 AND c.is_computed = 0 AND t.name NOT IN ('timestamp','rowversion') ORDER BY c.column_id",
            ("@t", table))).Select(r => ((string)r["name"]!, (string)r["type"]!)).ToList();
        var unique = (await _f.QueryAsync(
            "SELECT DISTINCT c.name FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
            "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE i.object_id = OBJECT_ID(@t) AND i.is_unique = 1 AND i.is_primary_key = 0",
            ("@t", table))).Select(r => (string)r["name"]!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var all = new Dictionary<string, string>(over, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, type) in cols)
        {
            if (all.ContainsKey(name) || !unique.Contains(name) || name is "OrganizationId") continue;
            all[name] = type == "uniqueidentifier" ? "NEWID()" : type is "nvarchar" or "varchar" ? $"LEFT(CONCAT(t.[{name}], '-', n.n), 50)" : $"t.[{name}]";
        }
        var sql = new StringBuilder();
        sql.Append($"INSERT INTO {table} ({string.Join(",", cols.Select(c => $"[{c.Item1}]"))}) ");
        sql.Append($"SELECT {string.Join(",", cols.Select(c => all.TryGetValue(c.Item1, out var v) ? v : $"t.[{c.Item1}]"))} ");
        sql.Append($"FROM {table} t CROSS JOIN (SELECT TOP (@n) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) n WHERE t.Id = @tpl;");
        await _f.ExecuteLongAsync(sql.ToString(), ("@n", count), ("@tpl", templateId));
    }

    [Fact]
    public async Task Perf_594_ar_aging_50k_open_invoices_under_5_seconds()
    {
        var o = await _root.NewArOrgAsync("PF594", stock: 5m);
        var customers = new List<Guid> { o.Customer.Uuid };
        for (var i = 0; i < 19; i++) customers.Add((await o.CustomerAsync($"Bulk {i}")).Uuid);
        var tpl = await o.IssuedInvoiceAsync(o.Customer, 1m); // 100 open
        var tplId = Convert.ToInt32((await _f.QueryAsync("SELECT Id FROM finance.sales_invoices WHERE UUID = @u", ("@u", tpl))).Single()["Id"]);
        var partnerCase = "CASE n.n % 20 " + string.Join(" ", customers.Select((c, i) => $"WHEN {i} THEN '{c}'")) + " END";

        var sw = Stopwatch.StartNew();
        await CloneAsync("finance.sales_invoices", tplId, 50_000, new()
        {
            ["UUID"] = "NEWID()", ["TraceId"] = "NEWID()", ["InvoiceNumber"] = "CONCAT('PF-', n.n)",
            ["PartnerId"] = partnerCase, ["DueDate"] = "DATEADD(day, -(n.n % 200), t.DueDate)", ["InvoiceDate"] = "DATEADD(day, -(n.n % 200) - 30, t.DueDate)",
        });
        // RPT counts an invoice once its customer-ledger INVOICE debit is booked (D-RPT): one ledger row per clone, copied from
        // the template's, numbered after it per customer.
        var lcols = (await _f.QueryAsync(
            "SELECT c.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('finance.customer_ledger') " +
            "AND c.is_identity = 0 AND c.is_computed = 0 AND t.name NOT IN ('timestamp','rowversion') ORDER BY c.column_id")).Select(r => (string)r["name"]!).ToList();
        var lover = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UUID"] = "NEWID()", ["PartnerId"] = "i.PartnerId", ["ReferenceId"] = "i.UUID", ["ReferenceNumber"] = "i.InvoiceNumber",
            ["EntryDate"] = "i.InvoiceDate", ["SequenceNo"] = "1000 + ROW_NUMBER() OVER (PARTITION BY i.PartnerId ORDER BY i.Id)",
            ["RunningBalance"] = "l.DebitAmount",
        };
        await _f.ExecuteLongAsync(
            $"INSERT INTO finance.customer_ledger ({string.Join(",", lcols.Select(c => $"[{c}]"))}) " +
            $"SELECT {string.Join(",", lcols.Select(c => lover.TryGetValue(c, out var v) ? v : $"l.[{c}]"))} " +
            "FROM finance.sales_invoices i JOIN finance.customer_ledger l ON l.ReferenceId = @tpl AND l.EntryType = 'INVOICE' " +
            "WHERE i.OrganizationId = @o AND i.InvoiceNumber LIKE 'PF-%';", ("@tpl", tpl), ("@o", o.OrgId));
        sw.Stop();
        Record("594", "clone 50 000 sales invoices + their ledger debits", sw.Elapsed);
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.sales_invoices WHERE OrganizationId = @o AND Status = 'ISSUED'", ("@o", o.OrgId)))
            .Should().Be(50_001);

        var (page, t) = await TimeAsync(() => o.ArAging("page=1&pageSize=25"));
        Record("594", "AR aging, 50 001 open invoices / 20 customers, page 1", t);
        page.Status.Should().Be(HttpStatusCode.OK, page.ToString());
        page.Result.P("totals").D("grandTotal").Should().Be(100m * 50_001);
        page.Result.P("totals").I("totalOpenItems").Should().Be(50_001);
        t.Should().BeLessThan(AgingTarget, "task 594: aging with 50 000 lines under 5 s");

        var (one, t1) = await TimeAsync(() => o.ArAging($"customerUuid={o.Customer.Uuid}&includeDetails=true&pageSize=25"));
        Record("594", "AR aging one customer with details (2 501 invoices)", t1);
        one.Status.Should().Be(HttpStatusCode.OK);
        t1.Should().BeLessThan(AgingTarget);

        var (sum, ts) = await TimeAsync(() => o.ArSummary());
        Record("594", "AR summary, 50 001 open invoices", ts);
        sum.Result.D("totalOutstanding").Should().Be(100m * 50_001);
        ts.Should().BeLessThan(AgingTarget);
    }

    [Fact]
    public async Task Perf_595_payment_run_200_vendors_500_items()
    {
        var o = await _root.NewArOrgAsync("PF595", stock: 1m);
        var vendors = new List<Partner>();
        for (var i = 0; i < 200; i++) vendors.Add(await o.VendorAsync($"Payee {i:D3}"));
        var tpl = await o.ApprovedBillAsync(vendors[0], 100m, dueInDays: 3);
        var tplId = Convert.ToInt32((await _f.QueryAsync("SELECT Id FROM finance.invoices WHERE UUID = @u", ("@u", tpl))).Single()["Id"]);

        // 499 more approved bills: vendor = n % 200 (every vendor gets 2–3), the name snapshot taken from the master.
        var values = string.Join(",", vendors.Select((v, i) => $"({i}, '{v.Uuid}', N'{v.Name.Replace("'", "''")}')"));
        var supplierNameCol = (await _f.RowCountAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('finance.invoices') AND name = 'SupplierName'")) == 1;
        var over = new Dictionary<string, string>
        {
            ["UUID"] = "NEWID()", ["InvoiceNumber"] = "CONCAT('PFB-', n.n)", ["SupplierInvoiceNo"] = "CONCAT('PFV-', n.n)",
            ["SupplierId"] = $"(SELECT u FROM (VALUES {values}) v(i, u, nm) WHERE v.i = n.n % 200)",
        };
        if (supplierNameCol) over["SupplierName"] = $"(SELECT nm FROM (VALUES {values}) v(i, u, nm) WHERE v.i = n.n % 200)";
        var sw = Stopwatch.StartNew();
        await CloneAsync("finance.invoices", tplId, 499, over);
        sw.Stop();
        Record("595", "clone 499 approved supplier invoices", sw.Elapsed);

        var (proposal, tp) = await TimeAsync(() => o.Propose(Today.AddDays(10)), warm: false);
        Record("595", "propose, 200 vendors / 500 items (schedule sync included)", tp);
        proposal.Status.Should().Be(HttpStatusCode.OK, proposal.ToString());
        var run = proposal.Result;
        run.RunBatches().Should().HaveCount(200);
        run.RunItems().Should().HaveCount(500);
        run.Head().D("totalAmount").Should().Be(50_000m);

        await o.K.Ok(o.ApproveRun(run.RunUuid()), "approve");
        var ex = Stopwatch.StartNew();
        var started = await o.ExecuteRun(run.RunUuid());
        started.Status.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.Accepted], started.ToString());
        Record("595", $"execute answered HTTP {(int)started.Status} ({started.Result.RunStatus()})", ex.Elapsed);
        // D-AP-13: a large run answers Executing and finishes in the background; poll it.
        var done = await SapKit.WaitForAsync(() => o.RunAsync(run.RunUuid()), r => r.RunStatus() != "Executing", "the run to finish", seconds: 600);
        ex.Stop();
        Record("595", "execute, 200 supplier payments / 500 allocations", ex.Elapsed);
        done.RunStatus().Should().Be("Completed");
        done.Head().I("failedBatchCount").Should().Be(0, J.Short(done.RunBatches().FirstOrDefault(b => b.S("status") == "Failed")));
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.supplier_payments WHERE OrganizationId = @o AND Status NOT IN ('CANCELLED','BOUNCED')", ("@o", o.OrgId)))
            .Should().Be(200, "one payment per vendor");
        (await _f.RowCountAsync(
            "SELECT COUNT(*) FROM (SELECT l.InvoiceUuid FROM finance.supplier_payment_lines l JOIN finance.supplier_payments p ON p.Id = l.SupplierPaymentId " +
            "WHERE p.OrganizationId = @o GROUP BY l.InvoiceUuid HAVING COUNT(*) = 1) x", ("@o", o.OrgId))).Should().Be(500, "every invoice paid exactly once");
        (tp + ex.Elapsed).Should().BeLessThan(Gross);
    }
}

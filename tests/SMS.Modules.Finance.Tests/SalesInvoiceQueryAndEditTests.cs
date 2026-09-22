using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P7-08 §9.1 — reading, listing, editing and deleting sales invoices: the CRUD half of
/// <see cref="ISalesInvoiceService"/> that the endpoints sit on.
/// </summary>
public class SalesInvoiceQueryAndEditTests
{
    private const int User = Receivables.User;
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly DateTime Sep1 = new(2026, 9, 1);

    private sealed record H(FinanceDbContext Db, SalesInvoiceService Service, TestClock Clock, Guid Org, string DbName);

    private static H New(Guid? org = null, string? dbName = null, IInterceptor? interceptor = null)
    {
        org    ??= Guid.NewGuid();
        dbName ??= Guid.NewGuid().ToString();
        var clock  = new TestClock();
        var db     = Receivables.Db(org.Value, dbName, interceptor);
        var demand = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = org.Value });

        var service = new SalesInvoiceService(
            db, demand, Mock.Of<IDeliveryFulfillmentReader>(), new CustomerLedgerService(db, clock), new NoProductLedger(),
            Receivables.Names().Object, Receivables.Lookups().Object, Mock.Of<IBackgroundJobClient>(),
            NullLogger<SalesInvoiceService>.Instance, clock);

        return new H(db, service, clock, org.Value, dbName);
    }

    private static SalesInvoice Inv(H h, string number, DateTime date, decimal grand = 1000m, string status = "DRAFT",
        Guid? partner = null, string so = "SO-2026-00042", string? delivery = null, string name = "Acme Ltd",
        decimal paid = 0m, bool deleted = false) =>
        Receivables.Invoice(h.Org, partner ?? Customer, number, date, grand, status, paid, deleted: deleted,
            saleOrderNumber: so, deliveryNumber: delivery, partnerName: name);

    private static Task<SalesInvoice> Stored(H h, Guid uuid) =>
        Receivables.Auditor(h.DbName).SalesInvoices.AsNoTracking().SingleAsync(i => i.UUID == uuid);

    // ── Get ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_invoice_is_returned_with_its_lines_in_order_and_the_payments_applied_to_it()
    {
        var h = New();
        var invoice = Inv(h, "SINV-20260901-0001", Sep1, 4000m, "PARTIALLY_PAID", delivery: "DLV-2026-00007", paid: 1500m);
        invoice.Notes = "Raised from delivery DLV-2026-00007 against sale order SO-2026-00042.";
        invoice.TaxAmount = 200m; invoice.DiscountAmount = 100m; invoice.Subtotal = 3900m;
        invoice.Lines.Add(new SalesInvoiceLine { UUID = Guid.NewGuid(), LineNo = 2, SoLineUuid = Guid.NewGuid(), VariantUuid = Guid.NewGuid(), Description = "Junction box", Quantity = 3, UnitPrice = 50, LineTotal = 150 });
        invoice.Lines.Add(new SalesInvoiceLine { UUID = Guid.NewGuid(), LineNo = 1, SoLineUuid = Guid.NewGuid(), VariantUuid = Guid.NewGuid(), Description = "4mm cable", Quantity = 100, UnitPrice = 40, DiscountPercent = 10, TaxPercent = 5, LineTotal = 3780 });

        var later   = new CustomerPayment { UUID = Guid.NewGuid(), OrganizationId = h.Org, PartnerId = Customer, PartnerName = "Acme Ltd", PaymentNumber = "CPAY-20260910-0001", PaymentDate = new DateTime(2026, 9, 10), Amount = 1000m, PaymentMethod = "CASH", Status = "RECEIVED", CreatedBy = 1, CreatedDate = Sep1 };
        var earlier = new CustomerPayment { UUID = Guid.NewGuid(), OrganizationId = h.Org, PartnerId = Customer, PartnerName = "Acme Ltd", PaymentNumber = "CPAY-20260905-0001", PaymentDate = new DateTime(2026, 9, 5), Amount = 500m, PaymentMethod = "CHEQUE", Status = "BOUNCED", CreatedBy = 1, CreatedDate = Sep1 };
        var a1 = new PaymentAllocation { UUID = Guid.NewGuid(), OrganizationId = h.Org, CustomerPayment = later,   SalesInvoice = invoice, AllocatedAmount = 1000m, AllocatedAt = new DateTime(2026, 9, 10, 9, 0, 0), AllocatedBy = 7 };
        var a2 = new PaymentAllocation { UUID = Guid.NewGuid(), OrganizationId = h.Org, CustomerPayment = earlier, SalesInvoice = invoice, AllocatedAmount = 500m,  AllocatedAt = new DateTime(2026, 9, 5, 9, 0, 0),  AllocatedBy = 8 };
        await Receivables.Seed(h.Db, invoice, later, earlier, a1, a2);

        var detail = await h.Service.GetAsync(invoice.UUID);

        detail.Should().NotBeNull();
        detail!.Uuid.Should().Be(invoice.UUID);
        detail.InvoiceNumber.Should().Be("SINV-20260901-0001");
        detail.SaleOrderNumber.Should().Be("SO-2026-00042");
        detail.DeliveryNumber.Should().Be("DLV-2026-00007");
        detail.PartnerId.Should().Be(Customer);
        detail.PartnerName.Should().Be("Acme Ltd");
        (detail.Subtotal, detail.DiscountAmount, detail.TaxAmount, detail.GrandTotal).Should().Be((3900m, 100m, 200m, 4000m));
        (detail.AmountPaid, detail.BalanceDue).Should().Be((1500m, 2500m));
        detail.Status.Should().Be("PARTIALLY_PAID");
        detail.CurrencyCode.Should().Be("PKR");
        detail.DueDate.Should().Be(Sep1.AddDays(30));
        detail.Notes.Should().Contain("DLV-2026-00007");
        detail.TraceId.Should().Be(invoice.TraceId);

        detail.Lines.Select(l => (l.LineNo, l.Description, l.Quantity, l.LineTotal)).Should().Equal(
            (1, "4mm cable", 100m, 3780m), (2, "Junction box", 3m, 150m));
        detail.Lines[0].Should().Match<SalesInvoiceLineModel>(l => l.UnitPrice == 40m && l.DiscountPercent == 10m && l.TaxPercent == 5m);

        detail.Payments.Select(p => (p.PaymentNumber, p.PaymentMethod, p.PaymentStatus, p.AllocatedAmount, p.AllocatedBy)).Should().Equal(
            ("CPAY-20260905-0001", "CHEQUE", "BOUNCED", 500m, 8),
            ("CPAY-20260910-0001", "CASH", "RECEIVED", 1000m, 7));
    }

    [Fact]
    public async Task An_unknown_deleted_or_another_organizations_invoice_is_not_returned()
    {
        var h = New();
        var mine    = Inv(h, "SINV-1", Sep1);
        var deleted = Inv(h, "SINV-2", Sep1, deleted: true);
        await Receivables.Seed(h.Db, mine, deleted);

        var stranger = New(Guid.NewGuid(), h.DbName);

        (await h.Service.GetAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Service.GetAsync(deleted.UUID)).Should().BeNull();
        (await stranger.Service.GetAsync(mine.UUID)).Should().BeNull("the tenant filter makes it not exist for them");
    }

    [Fact]
    public async Task Reading_an_invoice_tracks_nothing()
    {
        var h = New();
        var invoice = Inv(h, "SINV-1", Sep1);
        await Receivables.Seed(h.Db, invoice);

        await h.Service.GetAsync(invoice.UUID);

        h.Db.ChangeTracker.Entries().Should().BeEmpty();
    }

    // ── List ─────────────────────────────────────────────────────────────────

    private static async Task<H> ListFixture()
    {
        var h = New();
        var other = Guid.NewGuid();
        await Receivables.Seed(h.Db,
            Inv(h, "SINV-20260901-0001", new DateTime(2026, 9, 1), 100m, "ISSUED", so: "SO-2026-00001", delivery: "DLV-2026-00011"),
            Inv(h, "SINV-20260905-0001", new DateTime(2026, 9, 5), 200m, "PAID", paid: 200m, so: "SO-2026-00002"),
            Inv(h, "SINV-20260905-0002", new DateTime(2026, 9, 5), 300m, "DRAFT", so: "SO-2026-00002"),
            Inv(h, "SINV-20260910-0001", new DateTime(2026, 9, 10), 400m, "OVERDUE", partner: other, name: "Zenith Traders", so: "SO-2026-00003"),
            Inv(h, "SINV-20260912-0001", new DateTime(2026, 9, 12), 500m, "ISSUED", deleted: true));
        return h;
    }

    [Fact]
    public async Task Invoices_are_listed_newest_first_and_a_deleted_one_never_appears()
    {
        var h = await ListFixture();

        var page = await h.Service.ListAsync(new SalesInvoiceFilter());

        page.Data.Select(i => i.InvoiceNumber).Should().Equal(
            "SINV-20260910-0001", "SINV-20260905-0002", "SINV-20260905-0001", "SINV-20260901-0001");
        page.TotalRecords.Should().Be(4);
        var first = page.Data.Last();
        (first.SaleOrderNumber, first.DeliveryNumber, first.GrandTotal, first.BalanceDue, first.Status, first.CurrencyCode)
            .Should().Be(("SO-2026-00001", "DLV-2026-00011", 100m, 100m, "ISSUED", "PKR"));
    }

    [Fact]
    public async Task The_list_filters_by_customer_order_and_status()
    {
        var h = await ListFixture();

        (await h.Service.ListAsync(new SalesInvoiceFilter { PartnerId = Customer })).TotalRecords.Should().Be(3);
        (await h.Service.ListAsync(new SalesInvoiceFilter { PartnerId = Guid.NewGuid() })).TotalRecords.Should().Be(0);

        var page = await h.Service.ListAsync(new SalesInvoiceFilter { Status = "draft" });
        page.Data.Should().ContainSingle().Which.InvoiceNumber.Should().Be("SINV-20260905-0002", "the status is not case-sensitive");

        var order = await h.Service.ListAsync(new SalesInvoiceFilter { SaleOrderUuid = null, Search = "SO-2026-00002" });
        order.Data.Select(i => i.InvoiceNumber).Should().BeEquivalentTo(["SINV-20260905-0001", "SINV-20260905-0002"]);
    }

    [Fact]
    public async Task An_order_can_be_filtered_by_its_uuid_to_see_all_its_invoices()
    {
        var h = New();
        var a = Inv(h, "SINV-1", Sep1, 100m);
        var b = Inv(h, "SINV-2", Sep1.AddDays(1), 200m);
        b.SaleOrderUuid = a.SaleOrderUuid;
        await Receivables.Seed(h.Db, a, b, Inv(h, "SINV-3", Sep1.AddDays(2), 300m));

        var page = await h.Service.ListAsync(new SalesInvoiceFilter { SaleOrderUuid = a.SaleOrderUuid });

        page.Data.Select(i => i.InvoiceNumber).Should().Equal("SINV-2", "SINV-1");
    }

    [Fact]
    public async Task An_unknown_status_is_refused_and_named()
    {
        var h = New();

        var act = async () => await h.Service.ListAsync(new SalesInvoiceFilter { Status = "SETTLED" });

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*SETTLED*").WithMessage("*PARTIALLY_PAID*");
    }

    [Fact]
    public async Task The_invoice_date_range_is_whole_days_inclusive_at_both_ends()
    {
        var h = New();
        await Receivables.Seed(h.Db,
            Inv(h, "BEFORE", new DateTime(2026, 9, 9)),
            Inv(h, "FIRST", new DateTime(2026, 9, 10)),
            Inv(h, "LAST", new DateTime(2026, 9, 20)),
            Inv(h, "AFTER", new DateTime(2026, 9, 21)));

        var page = await h.Service.ListAsync(new SalesInvoiceFilter
        {
            DateFrom = new DateTime(2026, 9, 10, 18, 0, 0), DateTo = new DateTime(2026, 9, 20, 3, 0, 0)
        });

        page.Data.Select(i => i.InvoiceNumber).Should().Equal("LAST", "FIRST");
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_refused()
    {
        var h = New();

        var act = async () => await h.Service.ListAsync(new SalesInvoiceFilter { DateFrom = new DateTime(2026, 9, 20), DateTo = new DateTime(2026, 9, 10) });

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*invoice list*start date is after*");
    }

    [Theory]
    [InlineData("sinv-20260910", 1)]       // invoice number, any case
    [InlineData("ZENITH", 1)]              // customer name
    [InlineData("so-2026-00002", 2)]       // sale order number
    [InlineData("dlv-2026-00011", 1)]      // delivery number
    [InlineData("  acme  ", 3)]            // trimmed
    [InlineData("no such thing", 0)]
    public async Task Search_matches_the_number_the_order_the_delivery_or_the_customer(string search, int expected)
    {
        var h = await ListFixture();

        (await h.Service.ListAsync(new SalesInvoiceFilter { Search = search })).TotalRecords.Should().Be(expected);
    }

    [Fact]
    public async Task The_list_pages_and_clamps_and_describes_the_filtered_total()
    {
        var h = New();
        await Receivables.Seed(h.Db, Enumerable.Range(1, 25).Select(i => Inv(h, $"SINV-{i:D3}", Sep1.AddDays(i))).ToArray());

        var p2 = await h.Service.ListAsync(new SalesInvoiceFilter { Page = 2, PageSize = 10 });
        p2.Data.Should().HaveCount(10);
        (p2.TotalRecords, p2.TotalPages, p2.Page, p2.HasNext, p2.HasPrevious).Should().Be((25, 3, 2, true, true));

        var clamped = await h.Service.ListAsync(new SalesInvoiceFilter { Page = 0, PageSize = 1000 });
        (clamped.Page, clamped.PageSize).Should().Be((1, 100));

        var beyond = await h.Service.ListAsync(new SalesInvoiceFilter { Page = 9, PageSize = 10 });
        beyond.Data.Should().BeEmpty();
        beyond.TotalRecords.Should().Be(25);
    }

    [Fact]
    public async Task Another_organizations_invoices_are_never_listed()
    {
        var h = await ListFixture();
        var stranger = New(Guid.NewGuid(), h.DbName);

        (await stranger.Service.ListAsync(new SalesInvoiceFilter())).TotalRecords.Should().Be(0);
    }

    // ── Update ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_draft_can_have_its_due_date_and_notes_changed_and_nothing_else()
    {
        var h = New();
        var original = Inv(h, "SINV-1", Sep1, 4000m, "DRAFT", delivery: "DLV-2026-00007");
        original.Lines.Add(new SalesInvoiceLine { UUID = Guid.NewGuid(), LineNo = 1, SoLineUuid = Guid.NewGuid(), VariantUuid = Guid.NewGuid(), Description = "Cable", Quantity = 100, UnitPrice = 40, LineTotal = 4000 });
        await Receivables.Seed(h.Db, original);

        await h.Service.UpdateAsync(original.UUID, new UpdateSalesInvoiceRequest
        {
            DueDate = new DateTime(2026, 10, 15, 17, 45, 0), Notes = "  Net 45 as agreed  "
        }, User);

        var stored = await Stored(h, original.UUID);
        stored.DueDate.Should().Be(new DateTime(2026, 10, 15), "the time of day is dropped");
        stored.Notes.Should().Be("Net 45 as agreed");
        stored.ModifiedBy.Should().Be(User);
        stored.ModifiedDate.Should().Be(TestClock.Start);
        stored.Should().BeEquivalentTo(original, o => o
            .Excluding(i => i.DueDate).Excluding(i => i.Notes).Excluding(i => i.ModifiedBy).Excluding(i => i.ModifiedDate)
            .Excluding(i => i.Lines).Excluding(i => i.Allocations).Excluding(i => i.Id));
        stored.Status.Should().Be("DRAFT");
    }

    [Fact]
    public async Task A_due_date_on_the_invoice_date_is_allowed_and_one_before_it_is_not()
    {
        var h = New();
        var invoice = Inv(h, "SINV-1", new DateTime(2026, 9, 10));
        await Receivables.Seed(h.Db, invoice);

        await h.Service.UpdateAsync(invoice.UUID, new UpdateSalesInvoiceRequest { DueDate = new DateTime(2026, 9, 10, 23, 0, 0) }, User);
        (await Stored(h, invoice.UUID)).DueDate.Should().Be(new DateTime(2026, 9, 10));

        var act = async () => await h.Service.UpdateAsync(invoice.UUID, new UpdateSalesInvoiceRequest { DueDate = new DateTime(2026, 9, 9) }, User);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*before the invoice date*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_notes_clear_the_notes(string? notes)
    {
        var h = New();
        var invoice = Inv(h, "SINV-1", Sep1);
        invoice.Notes = "old";
        await Receivables.Seed(h.Db, invoice);

        await h.Service.UpdateAsync(invoice.UUID, new UpdateSalesInvoiceRequest { DueDate = Sep1.AddDays(30), Notes = notes }, User);

        (await Stored(h, invoice.UUID)).Notes.Should().BeNull();
    }

    [Fact]
    public async Task Over_long_notes_are_refused_before_they_reach_the_database()
    {
        var h = New();
        var invoice = Inv(h, "SINV-1", Sep1);
        await Receivables.Seed(h.Db, invoice);

        var act = async () => await h.Service.UpdateAsync(invoice.UUID, new UpdateSalesInvoiceRequest { DueDate = Sep1.AddDays(30), Notes = new string('n', 501) }, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*500 characters*");
    }

    [Theory]
    [InlineData("ISSUED")]
    [InlineData("PARTIALLY_PAID")]
    [InlineData("PAID")]
    [InlineData("OVERDUE")]
    [InlineData("CANCELLED")]
    [InlineData("CREDIT_NOTE")]
    public async Task Only_a_draft_can_be_edited(string status)
    {
        var h = New();
        var invoice = Inv(h, "SINV-1", Sep1, status: status);
        await Receivables.Seed(h.Db, invoice);

        var act = async () => await h.Service.UpdateAsync(invoice.UUID, new UpdateSalesInvoiceRequest { DueDate = Sep1.AddDays(60) }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage($"*{status}*").WithMessage("*Only a DRAFT can be edited*");
        (await Stored(h, invoice.UUID)).DueDate.Should().Be(Sep1.AddDays(30));
    }

    [Fact]
    public async Task Editing_an_unknown_deleted_or_another_organizations_invoice_is_not_found()
    {
        var h = New();
        var gone = Inv(h, "SINV-GONE", Sep1, deleted: true);
        var mine = Inv(h, "SINV-MINE", Sep1);
        await Receivables.Seed(h.Db, gone, mine);
        var stranger = New(Guid.NewGuid(), h.DbName);
        var request = new UpdateSalesInvoiceRequest { DueDate = Sep1.AddDays(40) };

        foreach (var act in new Func<Task>[]
                 {
                     () => h.Service.UpdateAsync(Guid.NewGuid(), request, User),
                     () => h.Service.UpdateAsync(gone.UUID, request, User),
                     () => stranger.Service.UpdateAsync(mine.UUID, request, User)
                 })
            await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task An_edit_made_from_a_stale_read_is_reported_as_a_conflict_not_applied_over_somebody_elses_change()
    {
        // Between this request reading the draft and saving it, somebody issues it. Without the
        // concurrency token the edit would simply land on top of the issued invoice.
        var dbName = Guid.NewGuid().ToString();
        var org    = Guid.NewGuid();
        var rival  = New(org, dbName);
        var invoice = Inv(rival, "SINV-1", Sep1);
        await Receivables.Seed(rival.Db, invoice);

        var race = new RunOnceBeforeSave(db => db.ChangeTracker.Entries<SalesInvoice>().Any(e => e.State == EntityState.Modified), async () =>
        {
            var other = Receivables.Db(org, dbName);
            var row = await other.SalesInvoices.SingleAsync(i => i.UUID == invoice.UUID);
            row.Status = "ISSUED";
            row.ModifiedBy = 99;
            row.ModifiedDate = TestClock.Start.AddSeconds(-1);
            await other.SaveChangesAsync();
        });
        var h = New(org, dbName, race);

        var act = async () => await h.Service.UpdateAsync(invoice.UUID, new UpdateSalesInvoiceRequest { DueDate = Sep1.AddDays(60) }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*changed by someone else*");
        race.Fired.Should().BeTrue();
        var stored = await Stored(h, invoice.UUID);
        (stored.Status, stored.DueDate, stored.ModifiedBy).Should().Be(("ISSUED", Sep1.AddDays(30), 99), "the other request's change stands");
        h.Db.ChangeTracker.Entries<SalesInvoice>().Should().BeEmpty("the stale copy is let go");
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deleting_a_draft_hides_it_but_keeps_the_row_and_its_number()
    {
        var h = New();
        var invoice = Inv(h, "SINV-1", Sep1);
        await Receivables.Seed(h.Db, invoice);

        await h.Service.DeleteAsync(invoice.UUID, User);

        var stored = await Stored(h, invoice.UUID);
        stored.IsDelete.Should().BeTrue();
        stored.IsActive.Should().BeFalse();
        (stored.ModifiedBy, stored.ModifiedDate).Should().Be((User, TestClock.Start));
        stored.InvoiceNumber.Should().Be("SINV-1", "kept, and never reused");

        (await h.Service.GetAsync(invoice.UUID)).Should().BeNull();
        (await h.Service.ListAsync(new SalesInvoiceFilter())).TotalRecords.Should().Be(0);
    }

    [Theory]
    [InlineData("ISSUED")]
    [InlineData("PARTIALLY_PAID")]
    [InlineData("PAID")]
    [InlineData("OVERDUE")]
    [InlineData("CANCELLED")]
    [InlineData("CREDIT_NOTE")]
    public async Task Only_a_draft_can_be_deleted_because_an_issued_one_has_a_receivable_against_it(string status)
    {
        var h = New();
        var invoice = Inv(h, "SINV-1", Sep1, status: status);
        await Receivables.Seed(h.Db, invoice);

        var act = async () => await h.Service.DeleteAsync(invoice.UUID, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage($"*{status}*").WithMessage("*Only a DRAFT can be deleted*");
        (await Stored(h, invoice.UUID)).IsDelete.Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_an_unknown_or_already_deleted_invoice_is_not_found()
    {
        var h = New();
        var gone = Inv(h, "SINV-GONE", Sep1, deleted: true);
        await Receivables.Seed(h.Db, gone);

        await ((Func<Task>)(() => h.Service.DeleteAsync(Guid.NewGuid(), User))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => h.Service.DeleteAsync(gone.UUID, User))).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Another_organization_cannot_delete_a_draft()
    {
        var h = New();
        var invoice = Inv(h, "SINV-1", Sep1);
        await Receivables.Seed(h.Db, invoice);
        var stranger = New(Guid.NewGuid(), h.DbName);

        await ((Func<Task>)(() => stranger.Service.DeleteAsync(invoice.UUID, User))).Should().ThrowAsync<NotFoundException>();
        (await Stored(h, invoice.UUID)).IsDelete.Should().BeFalse();
    }

    [Fact]
    public async Task A_delete_made_from_a_stale_read_is_reported_as_a_conflict()
    {
        var dbName = Guid.NewGuid().ToString();
        var org    = Guid.NewGuid();
        var rival  = New(org, dbName);
        var invoice = Inv(rival, "SINV-1", Sep1);
        await Receivables.Seed(rival.Db, invoice);

        var race = new RunOnceBeforeSave(db => db.ChangeTracker.Entries<SalesInvoice>().Any(e => e.State == EntityState.Modified), async () =>
        {
            var other = Receivables.Db(org, dbName);
            var row = await other.SalesInvoices.SingleAsync(i => i.UUID == invoice.UUID);
            row.Status = "ISSUED";
            row.ModifiedDate = TestClock.Start.AddSeconds(-1);
            await other.SaveChangesAsync();
        });
        var h = New(org, dbName, race);

        var act = async () => await h.Service.DeleteAsync(invoice.UUID, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*changed by someone else*");
        (await Stored(h, invoice.UUID)).IsDelete.Should().BeFalse("an invoice somebody has just issued must not be deleted under them");
    }
}

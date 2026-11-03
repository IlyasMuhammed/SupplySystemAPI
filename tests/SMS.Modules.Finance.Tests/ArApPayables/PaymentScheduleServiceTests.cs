using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.ArAp.Payables;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.ArApPayables;

/// <summary>A48 C10 / C15 — schedules (tasks 360–364, 381–382), terms (BR-EP-004), discounts (TS-EP-001/002/004).</summary>
public class PaymentScheduleServiceTests
{
    private static readonly ApActor Actor = ApRig.Actor;

    [Fact]
    public async Task Task_360_approval_creates_the_schedule_with_the_vendor_profile_and_its_early_payment_term()
    {
        var rig = ApRig.New();
        var vendor = Guid.NewGuid();
        var term = await rig.TermAsync();
        await rig.VendorProfileAsync(vendor, "High", "CHEQUE", term.Id);

        var invoice = await rig.ApprovedInvoiceAsync(vendor, 1000m, due: new DateOnly(2026, 10, 31), invoiceDate: new DateOnly(2026, 10, 1));

        var s = await rig.ScheduleAsync(invoice);
        (s.Status, s.VendorUuid, s.OriginalAmount, s.AmountRemaining, s.DueDate).Should().Be(("Open", vendor, 1000m, 1000m, new DateOnly(2026, 10, 31)));
        (s.PaymentPriority, s.PaymentMethod).Should().Be(("High", "CHEQUE"));
        (s.EarlyPaymentTermId, s.DiscountPercent, s.DiscountDate, s.DiscountAmount).Should().Be((term.Id, 2m, new DateOnly(2026, 10, 11), 20m));
    }

    [Fact]
    public async Task The_schedule_follows_payments_and_a_reversal()
    {
        var rig = ApRig.New();
        var vendor = Guid.NewGuid();
        var invoice = await rig.ApprovedInvoiceAsync(vendor, 1000m, new DateOnly(2026, 10, 31));

        var payment = await rig.Payments.CreateAsync(new CreateSupplierPaymentRequest
        {
            SupplierId = vendor, SupplierName = "Test Supplier", PaymentDate = new DateTime(2026, 10, 5), PaymentMethod = "CASH", TotalAmount = 400m,
            Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = invoice, AllocatedAmount = 400m }]
        }, 7);
        rig.Forget();
        (await rig.ScheduleAsync(invoice)).Should().Match<PaymentSchedule>(s => s.Status == "PartiallyPaid" && s.AmountRemaining == 600m,
            "a drafted payment already counts against what is owed");

        await rig.Payments.CancelAsync(payment, 7);
        rig.Forget();
        (await rig.ScheduleAsync(invoice)).Should().Match<PaymentSchedule>(s => s.Status == "Open" && s.AmountRemaining == 1000m);

        (await rig.Invoices.ReverseAsync(invoice, "entered twice", 7)).Should().BeTrue();
        rig.Forget();
        (await rig.ScheduleAsync(invoice)).Status.Should().Be("Cancelled");
    }

    [Fact]
    public async Task A_missed_hook_is_repaired_by_the_next_read_and_paid_invoices_get_none()
    {
        var rig = ApRig.New();
        var vendor = Guid.NewGuid();
        var invoice = await rig.ApprovedInvoiceAsync(vendor, 500m, new DateOnly(2026, 10, 20));
        rig.Fin.Set<PaymentSchedule>().RemoveRange(rig.Fin.Set<PaymentSchedule>());
        await rig.Fin.SaveChangesAsync();
        rig.Forget();

        var list = await rig.Schedules.ListAsync(null, null, null, null, 1, 25);

        list.Data.Should().ContainSingle(s => s.InvoiceUuid == invoice && s.Status == "Open");
    }

    [Fact]
    public async Task Task_361_upcoming_payments_are_filtered_by_the_date_range()
    {
        var rig = ApRig.New();
        var vendor = Guid.NewGuid();
        await rig.ApprovedInvoiceAsync(vendor, 100m, new DateOnly(2026, 10, 5));
        var inRange = await rig.ApprovedInvoiceAsync(vendor, 200m, new DateOnly(2026, 10, 15));
        await rig.ApprovedInvoiceAsync(vendor, 300m, new DateOnly(2026, 11, 15));

        var upcoming = await rig.Schedules.UpcomingAsync(new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 31));

        upcoming.Should().ContainSingle().Which.InvoiceUuid.Should().Be(inRange);
        upcoming[0].DaysUntilDue.Should().Be(14);
    }

    [Fact]
    public async Task Task_362_block_and_unblock()
    {
        var rig = ApRig.New();
        var invoice = await rig.ApprovedInvoiceAsync(Guid.NewGuid(), 100m, new DateOnly(2026, 10, 5));
        var uuid = (await rig.ScheduleAsync(invoice)).Uuid;

        await FluentActions.Awaiting(() => rig.Schedules.BlockAsync(uuid, " ", Actor)).Should().ThrowAsync<BadRequestException>();
        var blocked = await rig.Schedules.BlockAsync(uuid, "Quality dispute", Actor);
        (blocked.Status, blocked.PaymentBlock, blocked.BlockReason, blocked.BlockedByName).Should().Be(("Blocked", true, "Quality dispute", "Tess Tester"));
        rig.Forget();
        await FluentActions.Awaiting(() => rig.Schedules.BlockAsync(uuid, "again", Actor)).Should().ThrowAsync<ConflictException>().WithMessage("*already blocked.");

        var open = await rig.Schedules.UnblockAsync(uuid, Actor);
        (open.Status, open.PaymentBlock, open.BlockReason).Should().Be(("Open", false, null));
        rig.Forget();
        await FluentActions.Awaiting(() => rig.Schedules.UnblockAsync(uuid, Actor)).Should().ThrowAsync<ConflictException>().WithMessage("*is not blocked.");
    }

    [Fact]
    public async Task A_schedule_held_by_a_payment_run_cannot_be_blocked()
    {
        var rig = ApRig.New();
        var invoice = await rig.ApprovedInvoiceAsync(Guid.NewGuid(), 100m, new DateOnly(2026, 10, 5));
        var run = await rig.Runs.ProposeAsync(rig.Proposal(), Actor);
        var uuid = (await rig.ScheduleAsync(invoice)).Uuid;

        await FluentActions.Awaiting(() => rig.Schedules.BlockAsync(uuid, "dispute", Actor)).Should().ThrowAsync<ConflictException>()
            .WithMessage($"*is in payment run {run.Run.RunNumber}; remove it from the run first.");
    }

    [Fact]
    public async Task Priority_method_and_term_can_be_changed_and_a_paid_schedule_cannot()
    {
        var rig = ApRig.New();
        var vendor = Guid.NewGuid();
        var invoice = await rig.ApprovedInvoiceAsync(vendor, 1000m, new DateOnly(2026, 10, 31), invoiceDate: new DateOnly(2026, 10, 1));
        var uuid = (await rig.ScheduleAsync(invoice)).Uuid;
        var term = await rig.TermAsync(pct: 3m, days: 15, net: 45);

        var updated = await rig.Schedules.UpdateAsync(uuid, new UpdateScheduleRequest { PaymentPriority = "low", PaymentMethod = "online_wire" }, Actor);
        (updated.PaymentPriority, updated.PaymentMethod).Should().Be(("Low", "ONLINE_WIRE"));
        rig.Forget();
        await FluentActions.Awaiting(() => rig.Schedules.UpdateAsync(uuid, new UpdateScheduleRequest { PaymentPriority = "Urgent" }, Actor))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Unknown paymentPriority 'Urgent'. Allowed: High, Normal, Low.");

        var withTerm = await rig.Schedules.SetEarlyPaymentTermAsync(uuid, term.Uuid, Actor);
        (withTerm.DiscountPercent, withTerm.DiscountDate, withTerm.DiscountAmount, withTerm.EarlyPaymentTermCode).Should().Be((3m, new DateOnly(2026, 10, 16), 30m, term.Code));
        rig.Forget();
        (await rig.Schedules.SetEarlyPaymentTermAsync(uuid, null, Actor)).DiscountDate.Should().BeNull();

        rig.Forget();
        await rig.Payments.PostAsync(await PaidInFullAsync(rig, vendor, invoice), 7);
        rig.Forget();
        await FluentActions.Awaiting(() => rig.Schedules.UpdateAsync(uuid, new UpdateScheduleRequest { PaymentPriority = "High" }, Actor))
            .Should().ThrowAsync<ConflictException>().WithMessage("*is Paid; its schedule can no longer change.");
    }

    private static async Task<Guid> PaidInFullAsync(ApRig rig, Guid vendor, Guid invoice)
    {
        var p = await rig.Payments.CreateAsync(new CreateSupplierPaymentRequest
        {
            SupplierId = vendor, SupplierName = "Test Supplier", PaymentDate = new DateTime(2026, 10, 5), PaymentMethod = "CASH", TotalAmount = 1000m,
            Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = invoice, AllocatedAmount = 1000m }]
        }, 7);
        rig.Forget();
        await rig.Payments.ApproveAsync(p, 7);
        rig.Forget();
        return p;
    }

    [Fact]
    public async Task TS_EP_001_002_the_discount_of_a_schedule_depends_on_the_payment_date()
    {
        var rig = ApRig.New();
        var vendor = Guid.NewGuid();
        var term = await rig.TermAsync();
        await rig.VendorProfileAsync(vendor, termId: term.Id);
        var invoice = await rig.ApprovedInvoiceAsync(vendor, 1000m, new DateOnly(2026, 10, 31), invoiceDate: new DateOnly(2026, 10, 1));
        var uuid = (await rig.ScheduleAsync(invoice)).Uuid;

        var within = await rig.Schedules.CalculateDiscountAsync(uuid, new DateOnly(2026, 10, 9));
        (within.IsEligible, within.DiscountAmount, within.NetPaymentAmount, within.DaysUntilDeadline).Should().Be((true, 20m, 980m, 2));

        var after = await rig.Schedules.CalculateDiscountAsync(uuid, new DateOnly(2026, 10, 12));
        (after.IsEligible, after.DiscountAmount, after.NetPaymentAmount).Should().Be((false, 0m, 1000m));
    }

    [Fact]
    public async Task TS_EP_004_task_363_discount_opportunities_within_the_look_ahead_window()
    {
        var rig = ApRig.New(new DateOnly(2026, 10, 5));
        var vendor = Guid.NewGuid();
        var term = await rig.TermAsync();
        await rig.VendorProfileAsync(vendor, termId: term.Id);
        var soon = await rig.ApprovedInvoiceAsync(vendor, 1000m, new DateOnly(2026, 11, 1), invoiceDate: new DateOnly(2026, 10, 2));   // deadline 10-12
        await rig.ApprovedInvoiceAsync(vendor, 1000m, new DateOnly(2026, 11, 20), invoiceDate: new DateOnly(2026, 10, 20));           // deadline 10-30
        await rig.ApprovedInvoiceAsync(vendor, 1000m, new DateOnly(2026, 10, 25), invoiceDate: new DateOnly(2026, 9, 20));            // deadline 09-30 (gone)

        var list = await rig.Schedules.DiscountOpportunitiesAsync(new DateOnly(2026, 10, 5), 7);

        list.Should().ContainSingle().Which.InvoiceUuid.Should().Be(soon);
        (await rig.Schedules.DiscountOpportunitiesAsync(new DateOnly(2026, 10, 5), 30)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Task_364_381_382_cash_flow_groups_by_Monday_weeks_and_shows_blocked_amounts_separately()
    {
        var rig = ApRig.New(new DateOnly(2026, 10, 7)); // a Wednesday
        var vendor = Guid.NewGuid();
        await rig.ApprovedInvoiceAsync(vendor, 100m, new DateOnly(2026, 10, 2));   // overdue (before Monday 10-05)
        await rig.ApprovedInvoiceAsync(vendor, 200m, new DateOnly(2026, 10, 5));   // week 1 (Monday)
        await rig.ApprovedInvoiceAsync(vendor, 300m, new DateOnly(2026, 10, 11));  // week 1 (Sunday)
        await rig.ApprovedInvoiceAsync(vendor, 400m, new DateOnly(2026, 10, 12));  // week 2 (next Monday)
        var blockedInvoice = await rig.ApprovedInvoiceAsync(vendor, 500m, new DateOnly(2026, 10, 13)); // week 2, blocked
        await rig.ApprovedInvoiceAsync(vendor, 600m, new DateOnly(2027, 1, 30));   // after the horizon
        await rig.Schedules.BlockAsync((await rig.ScheduleAsync(blockedInvoice)).Uuid, "dispute", Actor);
        rig.Forget();

        var f = await rig.Schedules.CashFlowAsync(new DateOnly(2026, 10, 7), 8);

        f.WeekRows.Should().HaveCount(8);
        f.WeekRows[0].WeekStart.Should().Be(new DateOnly(2026, 10, 5));
        f.WeekRows[0].WeekEnd.Should().Be(new DateOnly(2026, 10, 11));
        (f.WeekRows[0].DueAmount, f.WeekRows[0].ItemCount).Should().Be((500m, 2));
        (f.WeekRows[1].DueAmount, f.WeekRows[1].BlockedAmount).Should().Be((400m, 500m));
        f.OverdueAmount.Should().Be(100m);
        f.LaterAmount.Should().Be(600m);
        (f.TotalDue, f.TotalBlocked).Should().Be((1000m, 500m));
        await FluentActions.Awaiting(() => rig.Schedules.CashFlowAsync(null, 0)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Early_payment_terms_validate_BR_EP_004_and_unique_codes()
    {
        var rig = ApRig.New();
        await FluentActions.Awaiting(() => rig.Terms.CreateAsync(new EarlyPaymentTermRequest { Code = "X", Name = "X", DiscountPercent = 2, DiscountDays = 30, NetDays = 30 }, Actor))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Discount days must be fewer than net days.");
        await FluentActions.Awaiting(() => rig.Terms.CreateAsync(new EarlyPaymentTermRequest { Code = "X", Name = "X", DiscountPercent = 0, DiscountDays = 10, NetDays = 30 }, Actor))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The discount percent must be greater than 0 and less than 100.");

        var created = await rig.Terms.CreateAsync(new EarlyPaymentTermRequest { Code = "2/10n30", Name = "2/10 Net 30", DiscountPercent = 2, DiscountDays = 10, NetDays = 30 }, Actor);
        created.Code.Should().Be("2/10N30");
        await FluentActions.Awaiting(() => rig.Terms.CreateAsync(new EarlyPaymentTermRequest { Code = "2/10N30", Name = "dup", DiscountPercent = 1, DiscountDays = 5, NetDays = 30 }, Actor))
            .Should().ThrowAsync<ConflictException>().WithMessage("An early payment term with code 2/10N30 already exists.");

        rig.Forget();
        var updated = await rig.Terms.UpdateAsync(created.Uuid, new EarlyPaymentTermRequest { NetDays = 45, IsActive = false }, Actor);
        (updated.NetDays, updated.IsActive).Should().Be((45, false));
        (await rig.Terms.ListAsync()).Should().ContainSingle();
    }

    [Fact]
    public void Schedule_status_derivation_order()
    {
        PaymentScheduleService.DeriveStatus(true, 100, 0, true, true).Should().Be("Cancelled");
        PaymentScheduleService.DeriveStatus(false, 0, 100, true, true).Should().Be("Paid");
        PaymentScheduleService.DeriveStatus(false, 100, 0, true, true).Should().Be("Blocked");
        PaymentScheduleService.DeriveStatus(false, 100, 0, false, true).Should().Be("Scheduled");
        PaymentScheduleService.DeriveStatus(false, 100, 50, false, false).Should().Be("PartiallyPaid");
        PaymentScheduleService.DeriveStatus(false, 100, 0, false, false).Should().Be("Open");
    }
}

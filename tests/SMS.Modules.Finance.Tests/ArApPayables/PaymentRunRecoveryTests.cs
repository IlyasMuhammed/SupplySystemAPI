using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Finance.ArAp.Payables;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using Xunit;

namespace SMS.Modules.Finance.Tests.ArApPayables;

/// <summary>A48 D-AP-12/13 — resumable execution, cancelling a stuck run, background execution, the discount-account gate.</summary>
public class PaymentRunRecoveryTests
{
    private static readonly ApActor Actor = ApRig.Actor;

    private sealed class MissingRoles(params string[] missing) : IPostingRoleReadiness
    {
        public Task<IReadOnlyList<string>> MissingRolesAsync(IReadOnlyCollection<string> roles, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(roles.Where(missing.Contains).ToList());
    }

    private static async Task<(ApRig Rig, Guid Run)> ApprovedRunAsync(int vendors, Mock<IBackgroundJobClient>? jobs = null)
    {
        var rig = ApRig.New(runJobs: jobs?.Object);
        for (var v = 0; v < vendors; v++)
            await rig.ApprovedInvoiceAsync(Guid.NewGuid(), 100m, rig.Today.AddDays(5));
        var run = await rig.Runs.ProposeAsync(rig.Proposal(), Actor);
        await rig.Runs.ApproveAsync(run.Run.Uuid, Actor);
        rig.Forget();
        return (rig, run.Run.Uuid);
    }

    /// <summary>As a process that died after Approved → Executing (and, optionally, after some batches).</summary>
    private static async Task StuckAsync(ApRig rig, Guid run, int paidBatches = 0)
    {
        var r = await rig.Fin.Set<PaymentRun>().Include(x => x.Batches).SingleAsync(x => x.Uuid == run);
        r.Status = ArApEnums.PaymentRunStatus.Executing;
        foreach (var b in r.Batches.OrderBy(b => b.VendorName).Take(paidBatches)) b.Status = ArApEnums.PaymentRunBatchStatus.Paid;
        await rig.Fin.SaveChangesAsync();
        rig.Forget();
    }

    [Fact]
    public async Task A_stuck_run_resumes_skipping_the_batches_already_paid()
    {
        var (rig, run) = await ApprovedRunAsync(5);
        await StuckAsync(rig, run, paidBatches: 2);

        var done = await rig.Runs.ResumeAsync(run, Actor);

        done.Run.Status.Should().Be("Completed");
        (await rig.Fin.SupplierPayments.CountAsync()).Should().Be(3, "the two batches already Paid are not paid again");
        await FluentActions.Awaiting(() => rig.Runs.ResumeAsync(run, Actor)).Should().ThrowAsync<ConflictException>()
            .WithMessage("Only an executing payment run can be resumed.");
    }

    [Fact]
    public async Task Only_an_executing_run_can_be_resumed()
    {
        var (rig, run) = await ApprovedRunAsync(2);
        await FluentActions.Awaiting(() => rig.Runs.ResumeAsync(run, Actor)).Should().ThrowAsync<ConflictException>();
        await FluentActions.Awaiting(() => rig.Runs.ResumeAsync(Guid.NewGuid(), Actor)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_stuck_run_with_no_paid_batch_can_be_cancelled_and_its_schedules_released()
    {
        var (rig, run) = await ApprovedRunAsync(3);
        await StuckAsync(rig, run);

        var cancelled = await rig.Runs.CancelAsync(run, "Execution died; paying by hand", Actor);

        cancelled.Run.Status.Should().Be("Cancelled");
        cancelled.VendorGroups.Should().OnlyContain(g => g.Status == "Failed" && g.ErrorMessage == "Payment run cancelled before this batch was paid.");
        (await rig.Fin.Set<PaymentSchedule>().AsNoTracking().ToListAsync()).Should().OnlyContain(s => s.Status == "Open" && s.ReservedByRunId == null);
        (await rig.Fin.SupplierPayments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_stuck_run_with_a_paid_batch_must_be_resumed_not_cancelled()
    {
        var (rig, run) = await ApprovedRunAsync(3);
        await StuckAsync(rig, run, paidBatches: 1);
        await FluentActions.Awaiting(() => rig.Runs.CancelAsync(run, "stop", Actor)).Should().ThrowAsync<ConflictException>()
            .WithMessage("*has paid batches; resume it instead of cancelling it.");
    }

    [Fact]
    public async Task A_big_run_executes_in_the_background_and_the_job_completes_it()
    {
        var jobs = new Mock<IBackgroundJobClient>();
        var (rig, run) = await ApprovedRunAsync(PaymentRunService.InlineBatchLimit + 1, jobs);

        var started = await rig.Runs.ExecuteAsync(run, Actor);

        started.Run.Status.Should().Be("Executing");
        jobs.Verify(j => j.Create(It.Is<Job>(x => x.Type == typeof(PaymentRunExecutionJob) && x.Method.Name == nameof(PaymentRunExecutionJob.RunAsync)),
            It.IsAny<IState>()), Times.Once);
        (await rig.Fin.SupplierPayments.CountAsync()).Should().Be(0, "nothing is paid in the request");
        await FluentActions.Awaiting(() => rig.Runs.ExecuteAsync(run, Actor)).Should().ThrowAsync<ConflictException>();

        rig.Forget();
        try { await new PaymentRunExecutionJob(rig.Runs).RunAsync(rig.Org, run, 7, "Tess Tester"); }
        finally { HangfireTenantScope.OrganizationId = null; }

        rig.Forget();
        (await rig.Runs.GetAsync(run)).Run.Status.Should().Be("Completed");
        (await rig.Fin.SupplierPayments.CountAsync()).Should().Be(PaymentRunService.InlineBatchLimit + 1);
    }

    [Fact]
    public async Task A_small_run_still_executes_in_the_request()
    {
        var jobs = new Mock<IBackgroundJobClient>();
        var (rig, run) = await ApprovedRunAsync(3, jobs);
        (await rig.Runs.ExecuteAsync(run, Actor)).Run.Status.Should().Be("Completed");
        jobs.Verify(j => j.Create(It.Is<Job>(x => x.Type == typeof(PaymentRunExecutionJob)), It.IsAny<IState>()), Times.Never,
            "only the timeline jobs are enqueued");
    }

    [Fact]
    public async Task REV_4_a_run_cannot_be_proposed_for_a_past_date()
    {
        var rig = ApRig.New();
        await rig.ApprovedInvoiceAsync(Guid.NewGuid(), 100m, rig.Today.AddDays(5));
        await FluentActions.Awaiting(() => rig.Runs.ProposeAsync(rig.Proposal(paymentDate: rig.Today.AddDays(-1)), Actor))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The payment date cannot be before today.");
    }

    [Fact]
    public async Task REV_4_a_run_executed_after_its_date_pays_today_and_loses_an_expired_discount()
    {
        var rig = ApRig.New(new DateOnly(2026, 10, 8));
        var vendor = Guid.NewGuid();
        var term = await rig.TermAsync();
        await rig.VendorProfileAsync(vendor, termId: term.Id);
        await rig.ApprovedInvoiceAsync(vendor, 1000m, new DateOnly(2026, 10, 31), invoiceDate: new DateOnly(2026, 10, 1)); // discount until 10-11
        var run = await rig.Runs.ProposeAsync(rig.Proposal(cutoff: new DateOnly(2026, 10, 15)), Actor);
        run.Run.TotalDiscount.Should().Be(20m);
        await rig.Runs.ApproveAsync(run.Run.Uuid, Actor);

        rig.Clock.Day = new DateOnly(2026, 10, 15);
        var done = await rig.Runs.ExecuteAsync(run.Run.Uuid, Actor);

        var payment = await rig.Fin.SupplierPayments.AsNoTracking().Include(p => p.Lines).SingleAsync();
        payment.PaymentDate.Date.Should().Be(new DateTime(2026, 10, 15), "the run's date had passed: it is paid today, not backdated");
        (payment.TotalAmount, payment.Lines.Single().DiscountAmount).Should().Be((1000m, 0m), "the discount ended on 10-11");
        (done.Run.TotalDiscount, done.Run.NetPaymentAmount).Should().Be((0m, 1000m));
    }

    [Fact]
    public async Task REV_21_an_invoice_paid_in_part_after_approval_is_re_priced_not_failed()
    {
        var (rig, run) = await ApprovedRunAsync(1);
        var s = await rig.Fin.Set<PaymentSchedule>().AsNoTracking().SingleAsync();
        await rig.Payments.CreateAsync(new SMS.Modules.Finance.Models.CreateSupplierPaymentRequest
        {
            SupplierId = s.VendorUuid, SupplierName = "Test Supplier", PaymentDate = rig.Today.ToDateTime(TimeOnly.MinValue), PaymentMethod = "CASH", TotalAmount = 40m,
            Lines = [new SMS.Modules.Finance.Models.CreateSupplierPaymentLineRequest { InvoiceUuid = s.InvoiceUuid, AllocatedAmount = 40m }]
        }, 7);
        rig.Forget();

        var done = await rig.Runs.ExecuteAsync(run, Actor);

        done.Run.Status.Should().Be("Completed");
        done.VendorGroups.Single().Status.Should().Be("Paid");
        var runPayment = await rig.Fin.SupplierPayments.AsNoTracking().Include(p => p.Lines).SingleAsync(p => p.Notes!.StartsWith("Payment run"));
        runPayment.Lines.Single().AllocatedAmount.Should().Be(60m, "100 owed, 40 already on another payment");
    }

    [Fact]
    public async Task REV_7_a_publish_lost_mid_run_is_made_good_by_the_resume()
    {
        using var cts = new CancellationTokenSource();
        var jobs = new Mock<IBackgroundJobClient>();
        // The first batch's timeline enqueue happens right after its commit: stop there — "the worker died before publishing".
        jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Callback(() => cts.Cancel()).Returns("1");
        var (rig, run) = await ApprovedRunAsync(3, jobs);
        // The first batch's publish is lost (the gateway is unreachable), then the worker stops.
        rig.Gateway.Throw = new InvalidOperationException("posting gateway unreachable");

        await FluentActions.Awaiting(() => rig.Runs.ExecuteAsync(run, Actor, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
        rig.Forget();
        (await rig.Fin.SupplierPayments.CountAsync(p => p.Status == "POSTED")).Should().Be(1);
        rig.Gateway.Posts.Should().BeEmpty("the one publish attempted was lost");
        (await rig.Runs.GetAsync(run)).Run.Status.Should().Be("Executing");

        rig.Gateway.Throw = null;
        var done = await rig.Runs.ResumeAsync(run, Actor);

        done.Run.Status.Should().Be("Completed");
        rig.Gateway.Posts.Where(t => t.DocumentType == FinDocType.BillPayment).Select(t => t.SourceDocumentId).Distinct()
            .Should().HaveCount(3, "the batch paid before the stop is posted too");
    }

    [Fact]
    public async Task QA_6_a_bill_paid_by_hand_before_execution_leaves_the_run_and_the_vendors_other_bill_is_paid()
    {
        var rig = ApRig.New();
        var vendor = Guid.NewGuid();
        var a = await rig.ApprovedInvoiceAsync(vendor, 300m, rig.Today.AddDays(5));
        var b = await rig.ApprovedInvoiceAsync(vendor, 400m, rig.Today.AddDays(6));
        var run = await rig.Runs.ProposeAsync(rig.Proposal(), Actor);
        await rig.Runs.ApproveAsync(run.Run.Uuid, Actor);
        rig.Forget();

        var manual = await rig.Payments.CreateAsync(new SMS.Modules.Finance.Models.CreateSupplierPaymentRequest
        {
            SupplierId = vendor, SupplierName = "Test Supplier", PaymentDate = rig.Today.ToDateTime(TimeOnly.MinValue), PaymentMethod = "CASH", TotalAmount = 300m,
            Lines = [new SMS.Modules.Finance.Models.CreateSupplierPaymentLineRequest { InvoiceUuid = a, AllocatedAmount = 300m }]
        }, 7);
        rig.Forget(); await rig.Payments.ApproveAsync(manual, 7); rig.Forget(); await rig.Payments.PostAsync(manual, 7); rig.Forget();
        (await rig.ScheduleAsync(a)).ReservedByRunId.Should().BeNull("the schedule sync released the paid bill");

        var done = await rig.Runs.ExecuteAsync(run.Run.Uuid, Actor);

        var batch = done.VendorGroups.Single();
        batch.Status.Should().Be("Paid");
        batch.Items.Should().ContainSingle(i => i.InvoiceUuid == b && i.Status == "Paid");
        done.RemovedItems.Should().ContainSingle(i => i.InvoiceUuid == a);
        (await rig.P.LoadAsync(b)).PaidAmount.Should().Be(400m);
        (await rig.P.LoadAsync(a)).PaidAmount.Should().Be(300m, "paid once, by hand");
    }

    [Fact]
    public async Task QA_6_a_hold_lost_to_another_run_still_fails_the_batch()
    {
        var (rig, run) = await ApprovedRunAsync(1);
        var s = await rig.Fin.Set<PaymentSchedule>().SingleAsync();
        s.ReservedByRunId = null; // as if released and claimed elsewhere, still owing
        await rig.Fin.SaveChangesAsync();
        rig.Forget();

        var done = await rig.Runs.ExecuteAsync(run, Actor);

        done.VendorGroups.Single().Status.Should().Be("Failed");
        done.VendorGroups.Single().ErrorMessage.Should().Contain("is no longer held by payment run");
    }

    [Fact]
    public async Task REV_9_the_timeline_of_a_run_payment_names_its_organization()
    {
        var jobs = new Mock<IBackgroundJobClient>();
        var (rig, run) = await ApprovedRunAsync(1, jobs);
        await rig.Runs.ExecuteAsync(run, Actor);
        jobs.Verify(j => j.Create(It.Is<Job>(x => x.Type == typeof(SMS.WorkflowEngine.Jobs.ITimelineAppendJob)
            && x.Args.Count == 5 && (Guid)x.Args[4]! == rig.Org), It.IsAny<IState>()), Times.Exactly(3));
    }

    [Fact]
    public async Task REV_22_export_references_number_batches_by_their_place_in_the_whole_run_as_cheques_do()
    {
        var rig = ApRig.New();
        for (var v = 0; v < 3; v++) await rig.ApprovedInvoiceAsync(Guid.NewGuid(), 100m, rig.Today.AddDays(5));
        var request = rig.Proposal(); request.PaymentMethod = "CHEQUE"; request.BankAccount = null;
        var run = await rig.Runs.ProposeAsync(request, Actor);
        await rig.Runs.ApproveAsync(run.Run.Uuid, Actor);
        rig.Forget();
        // As after a crash in which the first batch failed: Executing, batch 1 Failed, the others Pending.
        var stored = await rig.Fin.Set<PaymentRun>().Include(r => r.Batches).SingleAsync();
        stored.Status = ArApEnums.PaymentRunStatus.Executing;
        var first = stored.Batches.OrderBy(b => b.VendorName).ThenBy(b => b.Id).First();
        first.Status = ArApEnums.PaymentRunBatchStatus.Failed;
        await rig.Fin.SaveChangesAsync();
        rig.Forget();

        var refs = System.Text.Encoding.UTF8.GetString((await rig.Runs.ExportAsync(run.Run.Uuid, "csv", Actor)).Content).TrimStart('﻿')
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(',')[11]).Order().ToList();
        await rig.Runs.ResumeAsync(run.Run.Uuid, Actor);
        var cheques = await rig.Fin.SupplierPayments.AsNoTracking().Select(p => p.ChequeNo!).ToListAsync();

        refs.Should().Equal($"{run.Run.RunNumber}-002", $"{run.Run.RunNumber}-003");
        cheques.Order().Should().Equal(refs);
    }

    private static async Task<ApRig> DiscountRigAsync(IPostingRoleReadiness roles)
    {
        var rig = ApRig.New(new DateOnly(2026, 10, 8), roles: roles);
        var vendor = Guid.NewGuid();
        var term = await rig.TermAsync();
        await rig.VendorProfileAsync(vendor, termId: term.Id);
        await rig.ApprovedInvoiceAsync(vendor, 1000m, new DateOnly(2026, 10, 31), invoiceDate: new DateOnly(2026, 10, 1));
        return rig;
    }

    [Fact]
    public async Task Without_the_discount_account_a_discount_run_warns_at_proposal_and_cannot_be_approved()
    {
        var rig = await DiscountRigAsync(new MissingRoles("DEFAULT_DISCOUNT_RECEIVED"));

        var run = await rig.Runs.ProposeAsync(rig.Proposal(cutoff: new DateOnly(2026, 10, 15)), Actor);

        run.Run.TotalDiscount.Should().Be(20m);
        run.Warnings.Should().ContainSingle().Which.Should().Contain("DEFAULT_DISCOUNT_RECEIVED");
        await FluentActions.Awaiting(() => rig.Runs.ApproveAsync(run.Run.Uuid, Actor)).Should().ThrowAsync<ConflictException>()
            .WithMessage("This run takes early-payment discounts, but the general ledger has no account for DEFAULT_DISCOUNT_RECEIVED*");
        (await rig.Runs.GetAsync(run.Run.Uuid)).Run.Status.Should().Be("Proposed");
    }

    [Fact]
    public async Task Without_discounts_the_missing_account_does_not_matter()
    {
        var rig = await DiscountRigAsync(new MissingRoles("DEFAULT_DISCOUNT_RECEIVED"));
        var request = rig.Proposal(cutoff: new DateOnly(2026, 10, 31)); request.IncludeDiscountItems = false;

        var run = await rig.Runs.ProposeAsync(request, Actor);

        run.Warnings.Should().BeEmpty();
        (await rig.Runs.ApproveAsync(run.Run.Uuid, Actor)).Run.Status.Should().Be("Approved");
    }

    [Fact]
    public async Task With_the_account_mapped_or_no_GL_active_a_discount_run_is_approved()
    {
        var rig = await DiscountRigAsync(new NullPostingRoleReadiness());
        var run = await rig.Runs.ProposeAsync(rig.Proposal(cutoff: new DateOnly(2026, 10, 15)), Actor);
        run.Warnings.Should().BeEmpty();
        (await rig.Runs.ApproveAsync(run.Run.Uuid, Actor)).Run.Status.Should().Be("Approved");
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration.Posting;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Finance.ArAp.Payables;

public interface IPaymentRunService
{
    Task<PaymentRunDetailModel> ProposeAsync(ProposePaymentRunRequest request, ApActor actor, CancellationToken ct = default);
    Task<PaginatedResponse<PaymentRunSummaryModel>> ListAsync(string? status, int? page, int? pageSize, CancellationToken ct = default);
    Task<PaymentRunDetailModel> GetAsync(Guid runUuid, CancellationToken ct = default);
    Task<PaymentRunDetailModel> RemoveItemAsync(Guid runUuid, Guid itemUuid, string? reason, ApActor actor, CancellationToken ct = default);
    Task<PaymentRunDetailModel> RestoreItemAsync(Guid runUuid, Guid itemUuid, ApActor actor, CancellationToken ct = default);
    Task<PaymentRunDetailModel> UpdateItemMethodAsync(Guid runUuid, Guid itemUuid, string? paymentMethod, ApActor actor, CancellationToken ct = default);
    Task<PaymentRunDetailModel> ReviewAsync(Guid runUuid, ApActor actor, CancellationToken ct = default);
    Task<PaymentRunDetailModel> ApproveAsync(Guid runUuid, ApActor actor, CancellationToken ct = default);
    Task<PaymentRunDetailModel> ExecuteAsync(Guid runUuid, ApActor actor, CancellationToken ct = default);
    Task<PaymentRunDetailModel> ResumeAsync(Guid runUuid, ApActor actor, CancellationToken ct = default);
    Task<PaymentRunDetailModel> CancelAsync(Guid runUuid, string? reason, ApActor actor, CancellationToken ct = default);
    Task<PaymentFile> ExportAsync(Guid runUuid, string? format, ApActor actor, CancellationToken ct = default);
}

/// <summary>
/// A48 C11 (tasks 415–475; D-9, D-15) — the automatic payment run.
/// <para>
/// <b>Proposal</b> (BR-PR-001..004): the live payment schedules due by the cutoff — or, with discounts included, whose
/// discount date the payment date still meets — that are not blocked (those are listed separately) and not held by
/// another run; filtered by vendor, currency, method and priority; the discount taken when the payment date is on or before
/// the discount date. The chosen schedules are <b>claimed</b> for the run in the same transaction (a conditional update
/// <c>WHERE ReservedByRunId IS NULL</c>): two runs proposed at once never hold the same schedule (TS-PR-012). Items are
/// grouped into batches — vendor × currency × method (BR-PR-003; a SupplierPayment has one currency).
/// </para>
/// <para>
/// <b>Execution</b> (A-9, D-9): per batch, one transaction: one <see cref="SupplierPayment"/> through the existing
/// supplier-payment repository — create (allocated to the batch's invoices, gross, with each line's discount), approve,
/// post (PaidAmount, supplier ledger, exchange differences) — then the items, schedules and batch are marked paid. After
/// the commit the A44 BillPayment posting is published (GL: Dr PAYABLE gross / Cr BANK cash / Cr DISCOUNT_RECEIVED,
/// BR-PR-005). A batch that fails rolls back alone: it is marked Failed with the reason, its schedules are released, and
/// the other batches go on. Nothing is paid twice: a schedule is held by one run, and the invoice locks the payment takes
/// refuse an allocation above what is still owed.
/// </para>
/// </summary>
internal sealed class PaymentRunService : IPaymentRunService
{
    internal const string RunNumberPrefix = "PR";
    internal const int RemovedReasonMaxLength = 200;

    private static readonly string[] EditableStatuses =
        [ArApEnums.PaymentRunStatus.Draft, ArApEnums.PaymentRunStatus.Proposed, ArApEnums.PaymentRunStatus.Reviewing];
    private static readonly string[] CancellableStatuses =
        [ArApEnums.PaymentRunStatus.Draft, ArApEnums.PaymentRunStatus.Proposed, ArApEnums.PaymentRunStatus.Reviewing, ArApEnums.PaymentRunStatus.Approved];
    private static readonly string[] ActiveItemStatuses =
        [ArApEnums.PaymentRunItemStatus.Proposed, ArApEnums.PaymentRunItemStatus.Approved];
    private static readonly string[] ClaimableScheduleStatuses =
        [ArApEnums.ScheduleStatus.Open, ArApEnums.ScheduleStatus.PartiallyPaid];
    private static readonly string[] BankMethods = ["BANK_TRANSFER", "ONLINE_WIRE"];

    private readonly FinanceDbContext            _fin;
    private readonly PaymentScheduleService      _schedules;
    private readonly ISupplierPaymentRepository  _payments;
    private readonly SupplierPaymentPostings?    _postings;
    private readonly IDocumentNumberGenerator?   _numbers;
    private readonly IPartnerLookupService?      _partners;
    private readonly ICurrencyService?           _currency;
    private readonly ILookupsService?            _lookups;
    private readonly IBackgroundJobClient?       _jobs;
    private readonly ILogger<PaymentRunService>? _log;
    private readonly TimeProvider?               _clock;
    private readonly IPostingRoleReadiness?      _roles;

    /// <summary>
    /// D-AP-12 — the refusal text when an active posting target (the internal GL) cannot resolve DEFAULT_DISCOUNT_RECEIVED,
    /// which a run that takes discounts posts to; null when it can, or nothing needs it (QuickBooks posts the payload).
    /// </summary>
    internal async Task<string?> DiscountAccountProblemAsync(CancellationToken ct)
    {
        if (_roles is null) return null;
        var missing = await _roles.MissingRolesAsync([PostingRoles.DiscountReceived], ct);
        return missing.Count == 0 ? null
            : "This run takes early-payment discounts, but the general ledger has no account for DEFAULT_DISCOUNT_RECEIVED (Discount Received). "
            + "Assign it in Accounting → Defaults, or remove the discounted items (or propose without discounts), before approving.";
    }

    /// <summary>At execution too (the default may have been unassigned since approval): 409, nothing claimed.</summary>
    private async Task EnsureDiscountAccountAsync(Guid runUuid, CancellationToken ct)
    {
        var discounted = await _fin.Set<PaymentRunItem>().AnyAsync(i => i.PaymentRun.Uuid == runUuid && i.PaymentRun.OrganizationId == Org
            && i.Status == ArApEnums.PaymentRunItemStatus.Approved && i.DiscountAmount > 0m, ct);
        if (discounted && await DiscountAccountProblemAsync(ct) is { } problem) throw new ConflictException(problem);
    }

    public PaymentRunService(
        FinanceDbContext fin, PaymentScheduleService schedules, ISupplierPaymentRepository payments,
        SupplierPaymentPostings? postings = null, IDocumentNumberGenerator? numbers = null, IPartnerLookupService? partners = null,
        ICurrencyService? currency = null, ILookupsService? lookups = null, IBackgroundJobClient? jobs = null,
        ILogger<PaymentRunService>? log = null, TimeProvider? clock = null, IPostingRoleReadiness? roles = null)
    {
        _roles     = roles;
        _fin       = fin;
        _schedules = schedules;
        _payments  = payments;
        _postings  = postings;
        _numbers   = numbers;
        _partners  = partners;
        _currency  = currency;
        _lookups   = lookups;
        _jobs      = jobs;
        _log       = log;
        _clock     = clock;
    }

    private Guid Org => _fin.TenantContext.OrganizationId;
    private DateOnly Today => ApSupport.Today(_clock);

    private IQueryable<PaymentRun> OwnRuns() => _fin.Set<PaymentRun>().Where(r => r.OrganizationId == Org);
    private IQueryable<PaymentSchedule> OwnSchedules() => _fin.Set<PaymentSchedule>().Where(s => s.OrganizationId == Org);

    // ── Proposal ─────────────────────────────────────────────────────────────

    public async Task<PaymentRunDetailModel> ProposeAsync(ProposePaymentRunRequest request, ApActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var paymentDate = request.PaymentDate ?? Today;
        // REV-4 (D-AP-16): a run is never backdated (a payment made earlier is entered as a manual supplier payment).
        if (paymentDate < Today) throw new BadRequestException("The payment date cannot be before today.");
        var cutoff      = request.DueDateCutoff ?? paymentDate;
        var method      = ApSupport.ApMethod(request.PaymentMethod, "paymentMethod");
        var currency    = string.IsNullOrWhiteSpace(request.CurrencyCode) ? null : request.CurrencyCode.Trim().ToUpperInvariant();
        var minRank     = request.MinPriority is null ? 3 : ArApEnums.PaymentPriority.Rank(ApSupport.Priority(request.MinPriority, "minPriority"));
        var bank        = request.BankAccount?.Trim();
        if (bank is { Length: > 100 }) throw new BadRequestException("BankAccount can be at most 100 characters.");
        var include     = request.IncludeDiscountItems;

        // D-CORE-8: bring every schedule in step with its invoice before choosing.
        await _schedules.EnsureAllAsync(ct);
        _fin.ChangeTracker.Clear();

        var candidates = OwnSchedules().AsNoTracking()
            .Where(s => ClaimableScheduleStatuses.Contains(s.Status) && !s.PaymentBlock && s.ReservedByRunId == null && s.AmountRemaining > 0m
                     && (s.DueDate <= cutoff
                         || (include && s.DiscountDate != null && s.DiscountDate >= paymentDate && s.DiscountPercent > 0m)));
        if (request.VendorUuid is { } vendor) candidates = candidates.Where(s => s.VendorUuid == vendor);
        if (currency is not null) candidates = candidates.Where(s => s.CurrencyCode == currency);
        var rows = await candidates.OrderBy(s => s.VendorName).ThenBy(s => s.DueDate).ThenBy(s => s.Id).ToListAsync(ct);

        var profiles = await VendorProfilesAsync(rows.Select(r => r.VendorUuid), ct);
        var chosen = new List<(PaymentSchedule Schedule, string Method, DiscountCalculation Discount)>();
        foreach (var s in rows)
        {
            if (ArApEnums.PaymentPriority.Rank(s.PaymentPriority) > minRank) continue;
            var itemMethod = s.PaymentMethod ?? profiles.GetValueOrDefault(s.VendorUuid)?.PreferredPaymentMethod ?? method ?? "BANK_TRANSFER";
            if (method is not null && itemMethod != method) continue;
            var discount = include
                ? PayablesMath.Discount(s.AmountRemaining, s.DiscountPercent, s.DiscountDate, paymentDate)
                : PayablesMath.Discount(s.AmountRemaining, null, null, paymentDate);
            chosen.Add((s, itemMethod, discount));
        }

        if (chosen.Count == 0)
            throw new BadRequestException($"Nothing is due by {cutoff:yyyy-MM-dd}.");

        var criteria = JsonSerializer.Serialize(new
        {
            paymentDate, dueDateCutoff = cutoff, paymentMethod = method, vendorUuid = request.VendorUuid, currencyCode = currency,
            includeDiscountItems = include, minPriority = request.MinPriority
        });
        var baseCode  = await ApSupport.PurchaseBaseCodeAsync(_currency, _lookups, Org, ct);
        var masks     = await BankMasksAsync(chosen.Select(c => c.Schedule.VendorUuid), ct);
        var now       = DateTime.UtcNow;

        var runUuid = await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            // In the transaction: without the app-wide counter the local numbering is serialized by an app lock to the commit.
            var runNumber = await NextRunNumberAsync(ct);
            var run = new PaymentRun
            {
                OrganizationId    = Org,
                RunNumber         = runNumber,
                RunDate           = Today,
                PaymentDate       = paymentDate,
                DueDateCutoff     = cutoff,
                Status            = ArApEnums.PaymentRunStatus.Proposed,
                SelectionCriteria = criteria,
                PaymentMethod     = method,
                BankAccount       = string.IsNullOrEmpty(bank) ? null : bank,
                IncludeDiscount   = include,
                BaseCurrencyCode  = baseCode,
                CreatedAt         = now,
                ProposedBy        = actor.UserId,
                ProposedByName    = actor.UserName,
                ProposedAt        = now
            };
            _fin.Set<PaymentRun>().Add(run);
            await _fin.SaveChangesAsync(ct);

            var claimed = await ClaimAsync(chosen.Select(c => c.Schedule.Id).ToList(), run.Id, ct);
            if (claimed.Count == 0)
            {
                // Another run took them all meanwhile: nothing to propose (and on the in-memory provider, no run either).
                if (!_fin.Database.IsRelational()) { _fin.Set<PaymentRun>().Remove(run); await _fin.SaveChangesAsync(ct); }
                throw new BadRequestException($"Nothing is due by {cutoff:yyyy-MM-dd}.");
            }

            var batches = new Dictionary<(Guid, string, string), PaymentRunBatch>();
            foreach (var (s, itemMethod, discount) in chosen.Where(c => claimed.Contains(c.Schedule.Id)))
            {
                var key = (s.VendorUuid, s.CurrencyCode, itemMethod);
                if (!batches.TryGetValue(key, out var batch))
                {
                    batch = new PaymentRunBatch
                    {
                        OrganizationId = Org, PaymentRun = run, VendorUuid = s.VendorUuid, VendorName = s.VendorName,
                        CurrencyCode = s.CurrencyCode, PaymentMethod = itemMethod, BankDetailsMasked = masks.GetValueOrDefault(s.VendorUuid),
                        Status = ArApEnums.PaymentRunBatchStatus.Pending
                    };
                    batches[key] = batch;
                    run.Batches.Add(batch);
                }

                var item = new PaymentRunItem
                {
                    OrganizationId    = Org,
                    PaymentRun        = run,
                    Batch             = batch,
                    PaymentScheduleId = s.Id,
                    InvoiceUuid       = s.InvoiceUuid,
                    InvoiceNumber     = s.InvoiceNumber,
                    VendorUuid        = s.VendorUuid,
                    VendorName        = s.VendorName,
                    CurrencyCode      = s.CurrencyCode,
                    DueDate           = s.DueDate,
                    OriginalAmount    = s.AmountRemaining,
                    DiscountAmount    = discount.DiscountAmount,
                    NetPaymentAmount  = discount.NetPaymentAmount,
                    PaymentMethod     = itemMethod,
                    Status            = ArApEnums.PaymentRunItemStatus.Proposed
                };
                run.Items.Add(item);
                batch.Items.Add(item);
            }

            await RecalculateAsync(run, ct);
            await _fin.SaveChangesAsync(ct);
            return run.Uuid;
        });

        _fin.ChangeTracker.Clear();
        return await GetAsync(runUuid, ct);
    }

    /// <summary>
    /// TS-PR-012 — claims the schedules for the run: one conditional UPDATE (… WHERE ReservedByRunId IS NULL AND not blocked
    /// AND still open), then reads back which ones this run now holds. On the in-memory provider: tracked changes.
    /// </summary>
    private async Task<HashSet<int>> ClaimAsync(IReadOnlyList<int> scheduleIds, int runId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_fin.Database.IsRelational())
        {
            foreach (var chunk in scheduleIds.Chunk(1000))
            {
                var ids = chunk.ToList();
                await OwnSchedules()
                    .Where(s => ids.Contains(s.Id) && s.ReservedByRunId == null && !s.PaymentBlock && ClaimableScheduleStatuses.Contains(s.Status))
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(s => s.ReservedByRunId, runId)
                        .SetProperty(s => s.Status, ArApEnums.ScheduleStatus.Scheduled)
                        .SetProperty(s => s.ModifiedAt, now), ct);
            }
            return (await OwnSchedules().AsNoTracking().Where(s => s.ReservedByRunId == runId).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        }

        var rows = await OwnSchedules().Where(s => scheduleIds.Contains(s.Id)).ToListAsync(ct);
        var claimed = new HashSet<int>();
        foreach (var s in rows.Where(s => s.ReservedByRunId == null && !s.PaymentBlock && ClaimableScheduleStatuses.Contains(s.Status)))
        {
            s.ReservedByRunId = runId;
            s.Status          = ArApEnums.ScheduleStatus.Scheduled;
            s.ModifiedAt      = now;
            claimed.Add(s.Id);
        }
        await _fin.SaveChangesAsync(ct);
        return claimed;
    }

    /// <summary>Lets the schedules of these items go (removed item, cancelled run, failed batch): no run holds them, status from the invoice.</summary>
    private async Task ReleaseAsync(IReadOnlyCollection<int> scheduleIds, int runId, CancellationToken ct)
    {
        if (scheduleIds.Count == 0) return;
        var rows = await OwnSchedules().Where(s => scheduleIds.Contains(s.Id) && s.ReservedByRunId == runId).ToListAsync(ct);
        if (rows.Count == 0) return;
        var state = await _schedules.InvoiceStateAsync(rows.Select(r => r.InvoiceId).ToList(), ct);
        foreach (var s in rows)
        {
            s.ReservedByRunId = null;
            PaymentScheduleService.Apply(s, state.GetValueOrDefault(s.InvoiceId));
            s.ModifiedAt = DateTime.UtcNow;
        }
    }

    // ── Review & edit ────────────────────────────────────────────────────────

    public async Task<PaymentRunDetailModel> RemoveItemAsync(Guid runUuid, Guid itemUuid, string? reason, ApActor actor, CancellationToken ct = default)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why)) throw new BadRequestException("A removal needs a reason.");
        if (why.Length > RemovedReasonMaxLength) throw new BadRequestException($"The reason can be at most {RemovedReasonMaxLength} characters.");

        await EditAsync(runUuid, async run =>
        {
            var item = run.Items.FirstOrDefault(i => i.Uuid == itemUuid) ?? throw new NotFoundException("Payment run item", itemUuid);
            if (!ActiveItemStatuses.Contains(item.Status))
                throw new ConflictException($"Invoice {item.InvoiceNumber} is already {item.Status.ToLowerInvariant()} in payment run {run.RunNumber}.");

            item.Status        = ArApEnums.PaymentRunItemStatus.Removed;
            item.RemovedReason = why;
            item.RemovedBy     = actor.UserId;
            item.RemovedByName = actor.UserName;
            item.RemovedAt     = DateTime.UtcNow;
            await ReleaseAsync([item.PaymentScheduleId], run.Id, ct);
        }, ct);
        return await GetAsync(runUuid, ct);
    }

    public async Task<PaymentRunDetailModel> RestoreItemAsync(Guid runUuid, Guid itemUuid, ApActor actor, CancellationToken ct = default)
    {
        await EditAsync(runUuid, async run =>
        {
            var item = run.Items.FirstOrDefault(i => i.Uuid == itemUuid) ?? throw new NotFoundException("Payment run item", itemUuid);
            if (item.Status != ArApEnums.PaymentRunItemStatus.Removed)
                throw new ConflictException($"Invoice {item.InvoiceNumber} is not removed from payment run {run.RunNumber}.");

            var claimed = await ClaimAsync([item.PaymentScheduleId], run.Id, ct);
            if (!claimed.Contains(item.PaymentScheduleId))
                throw new ConflictException($"The payment of invoice {item.InvoiceNumber} is blocked, paid or in another payment run; it cannot be restored.");

            var s = await OwnSchedules().AsNoTracking().FirstAsync(x => x.Id == item.PaymentScheduleId, ct);
            var discount = run.IncludeDiscount
                ? PayablesMath.Discount(s.AmountRemaining, s.DiscountPercent, s.DiscountDate, run.PaymentDate)
                : PayablesMath.Discount(s.AmountRemaining, null, null, run.PaymentDate);
            item.OriginalAmount   = s.AmountRemaining;
            item.DiscountAmount   = discount.DiscountAmount;
            item.NetPaymentAmount = discount.NetPaymentAmount;
            item.Status           = run.Status == ArApEnums.PaymentRunStatus.Approved ? ArApEnums.PaymentRunItemStatus.Approved : ArApEnums.PaymentRunItemStatus.Proposed;
            item.RemovedReason    = null;
            item.RemovedBy        = null;
            item.RemovedByName    = null;
            item.RemovedAt        = null;
        }, ct);
        return await GetAsync(runUuid, ct);
    }

    public async Task<PaymentRunDetailModel> UpdateItemMethodAsync(Guid runUuid, Guid itemUuid, string? paymentMethod, ApActor actor, CancellationToken ct = default)
    {
        var method = ApSupport.ApMethod(paymentMethod, "paymentMethod") ?? throw new BadRequestException("paymentMethod is required.");
        await EditAsync(runUuid, async run =>
        {
            var item = run.Items.FirstOrDefault(i => i.Uuid == itemUuid) ?? throw new NotFoundException("Payment run item", itemUuid);
            if (!ActiveItemStatuses.Contains(item.Status))
                throw new ConflictException($"Invoice {item.InvoiceNumber} is {item.Status.ToLowerInvariant()} in payment run {run.RunNumber}.");
            if (item.PaymentMethod == method) return;

            var batch = run.Batches.FirstOrDefault(b => b.VendorUuid == item.VendorUuid && b.CurrencyCode == item.CurrencyCode && b.PaymentMethod == method);
            if (batch is null)
            {
                var masks = await BankMasksAsync([item.VendorUuid], ct);
                batch = new PaymentRunBatch
                {
                    OrganizationId = Org, PaymentRun = run, VendorUuid = item.VendorUuid, VendorName = item.VendorName,
                    CurrencyCode = item.CurrencyCode, PaymentMethod = method, BankDetailsMasked = masks.GetValueOrDefault(item.VendorUuid),
                    Status = ArApEnums.PaymentRunBatchStatus.Pending
                };
                run.Batches.Add(batch);
            }
            item.Batch         = batch;
            item.PaymentMethod = method;
        }, ct);
        return await GetAsync(runUuid, ct);
    }

    public async Task<PaymentRunDetailModel> ReviewAsync(Guid runUuid, ApActor actor, CancellationToken ct = default)
    {
        await TransitionAsync(runUuid, [ArApEnums.PaymentRunStatus.Proposed], run => run.Status = ArApEnums.PaymentRunStatus.Reviewing,
            run => $"Only a proposed payment run can be sent to review; {run.RunNumber} is {run.Status}.", ct);
        return await GetAsync(runUuid, ct);
    }

    /// <summary>BR-PR-006 — Proposed / Reviewing → Approved; the active items become Approved.</summary>
    public async Task<PaymentRunDetailModel> ApproveAsync(Guid runUuid, ApActor actor, CancellationToken ct = default)
    {
        var discountProblem = await DiscountAccountProblemAsync(ct);
        await TransitionAsync(runUuid, [ArApEnums.PaymentRunStatus.Proposed, ArApEnums.PaymentRunStatus.Reviewing], run =>
        {
            var active = run.Items.Where(i => ActiveItemStatuses.Contains(i.Status)).ToList();
            if (active.Count == 0) throw new ConflictException($"Payment run {run.RunNumber} has nothing left to pay.");
            if (string.IsNullOrWhiteSpace(run.BankAccount) && active.FirstOrDefault(i => BankMethods.Contains(i.PaymentMethod)) is { } bankItem)
                throw new BadRequestException($"BankAccount is required for {bankItem.PaymentMethod} payments; propose the run with the bank account to pay from.");
            if (discountProblem is not null && active.Any(i => i.DiscountAmount > 0m)) throw new ConflictException(discountProblem);

            foreach (var item in active) item.Status = ArApEnums.PaymentRunItemStatus.Approved;
            run.Status         = ArApEnums.PaymentRunStatus.Approved;
            run.ApprovedBy     = actor.UserId;
            run.ApprovedByName = actor.UserName;
            run.ApprovedAt     = DateTime.UtcNow;
        }, run => $"Payment run {run.RunNumber} is {run.Status}; it can no longer change.", ct);
        return await GetAsync(runUuid, ct);
    }

    /// <summary>BR-PR-010 — Draft / Proposed / Reviewing / Approved → Cancelled; every schedule it held is released; nothing is paid.</summary>
    public async Task<PaymentRunDetailModel> CancelAsync(Guid runUuid, string? reason, ApActor actor, CancellationToken ct = default)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why)) throw new BadRequestException("A reason is required.");
        if (why.Length > 500) throw new BadRequestException("The reason can be at most 500 characters.");

        await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            var run = await LockRunAsync(runUuid, ct);
            // D-AP-13: a run stuck Executing may be cancelled while none of its batches has been paid (under the run lock,
            // which every batch transaction takes first, so no batch can be paid meanwhile).
            var stuck = run.Status == ArApEnums.PaymentRunStatus.Executing;
            if (stuck && run.Batches.Any(b => b.Status == ArApEnums.PaymentRunBatchStatus.Paid))
                throw new ConflictException($"Payment run {run.RunNumber} has paid batches; resume it instead of cancelling it.");
            if (!stuck && !CancellableStatuses.Contains(run.Status))
                throw new ConflictException("Only a draft, proposed, reviewing or approved payment run can be cancelled.");
            if (stuck)
                foreach (var batch in run.Batches.Where(b => b.Status == ArApEnums.PaymentRunBatchStatus.Pending).ToList())
                {
                    // A payment an interrupted attempt created (DRAFT / APPROVED, never posted) is cancelled with the run.
                    if (batch.SupplierPaymentUuid is { } draft
                        && await _fin.SupplierPayments.AsNoTracking().Where(p => p.UUID == draft).Select(p => p.Status).FirstOrDefaultAsync(ct)
                           is "DRAFT" or "APPROVED")
                        await _payments.CancelAsync(draft, actor.UserId ?? 0);
                    batch.Status        = ArApEnums.PaymentRunBatchStatus.Failed;
                    batch.FailureReason = "Payment run cancelled before this batch was paid.";
                }

            await ReleaseAsync(run.Items.Where(i => ActiveItemStatuses.Contains(i.Status)).Select(i => i.PaymentScheduleId).ToList(), run.Id, ct);
            run.Status          = ArApEnums.PaymentRunStatus.Cancelled;
            run.CancelReason    = why;
            run.CancelledBy     = actor.UserId;
            run.CancelledByName = actor.UserName;
            run.CancelledAt     = DateTime.UtcNow;
            await ApSupport.SaveAsync(_fin, $"Payment run {run.RunNumber}", ct);
            return true;
        });
        _fin.ChangeTracker.Clear();
        return await GetAsync(runUuid, ct);
    }

    private async Task EditAsync(Guid runUuid, Func<PaymentRun, Task> change, CancellationToken ct)
    {
        await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            var run = await LockRunAsync(runUuid, ct);
            if (!EditableStatuses.Contains(run.Status))
                throw new ConflictException($"Payment run {run.RunNumber} is {run.Status}; it can no longer change.");
            await change(run);
            await RecalculateAsync(run, ct);
            await ApSupport.SaveAsync(_fin, $"Payment run {run.RunNumber}", ct);
            return true;
        });
        _fin.ChangeTracker.Clear();
    }

    private async Task TransitionAsync(Guid runUuid, string[] from, Action<PaymentRun> change, Func<PaymentRun, string> refused, CancellationToken ct)
    {
        await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            var run = await LockRunAsync(runUuid, ct);
            if (!from.Contains(run.Status)) throw new ConflictException(refused(run));
            change(run);
            await ApSupport.SaveAsync(_fin, $"Payment run {run.RunNumber}", ct);
            return true;
        });
        _fin.ChangeTracker.Clear();
    }

    /// <summary>The run's row locked (a no-op UPDATE, inside the caller's transaction), then read with its batches and items.</summary>
    private async Task<PaymentRun> LockRunAsync(Guid runUuid, CancellationToken ct)
    {
        if (_fin.Database.IsRelational())
        {
            var rows = await OwnRuns().Where(r => r.Uuid == runUuid).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, r => r.Status), ct);
            if (rows == 0) throw new NotFoundException("Payment run", runUuid);
        }
        foreach (var e in _fin.ChangeTracker.Entries().Where(e => e.Entity is PaymentRun or PaymentRunBatch or PaymentRunItem).ToList())
            e.State = EntityState.Detached;

        return await OwnRuns().Include(r => r.Batches).Include(r => r.Items).AsSplitQuery()
                   .FirstOrDefaultAsync(r => r.Uuid == runUuid, ct)
               ?? throw new NotFoundException("Payment run", runUuid);
    }

    /// <summary>Batch and run totals from the active items (task 427/435); empty batches of an unexecuted run are dropped.</summary>
    private async Task RecalculateAsync(PaymentRun run, CancellationToken ct)
    {
        var counted = run.Status is ArApEnums.PaymentRunStatus.Completed
            ? run.Items.Where(i => i.Status == ArApEnums.PaymentRunItemStatus.Paid).ToList()
            : run.Items.Where(i => ActiveItemStatuses.Contains(i.Status)).ToList();

        foreach (var batch in run.Batches.ToList())
        {
            var items = counted.Where(i => ReferenceEquals(i.Batch, batch)).ToList();
            batch.ItemCount     = items.Count;
            batch.TotalAmount   = items.Sum(i => i.OriginalAmount);
            batch.TotalDiscount = items.Sum(i => i.DiscountAmount);
            batch.NetAmount     = items.Sum(i => i.NetPaymentAmount);

            // Items reach their batch through the navigation (EF fixes it up on load; a moved item points at its new batch).
            var everything = run.Items.Any(i => ReferenceEquals(i.Batch, batch));
            if (!everything && EditableStatuses.Contains(run.Status))
            {
                run.Batches.Remove(batch);
                if (batch.Id != 0) _fin.Set<PaymentRunBatch>().Remove(batch);
            }
        }

        var rates = await RatesAsync(counted.Select(i => i.InvoiceUuid), ct);
        run.TotalItems       = counted.Count;
        run.TotalVendors     = counted.Select(i => i.VendorUuid).Distinct().Count();
        run.TotalAmount      = counted.Sum(i => ApSupport.ToBase(i.OriginalAmount, i.CurrencyCode, rates.GetValueOrDefault(i.InvoiceUuid), run.BaseCurrencyCode));
        run.TotalDiscount    = counted.Sum(i => ApSupport.ToBase(i.DiscountAmount, i.CurrencyCode, rates.GetValueOrDefault(i.InvoiceUuid), run.BaseCurrencyCode));
        run.NetPaymentAmount = counted.Sum(i => ApSupport.ToBase(i.NetPaymentAmount, i.CurrencyCode, rates.GetValueOrDefault(i.InvoiceUuid), run.BaseCurrencyCode));
    }

    private async Task<Dictionary<Guid, decimal?>> RatesAsync(IEnumerable<Guid> invoiceUuids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, decimal?>();
        foreach (var chunk in invoiceUuids.Distinct().Chunk(1000))
        {
            var ids = chunk.ToList();
            foreach (var r in await InvoiceRowLocks.Own(_fin).AsNoTracking().Where(i => ids.Contains(i.UUID))
                         .Select(i => new { i.UUID, i.ExchangeRate }).ToListAsync(ct))
                result[r.UUID] = r.ExchangeRate;
        }
        return result;
    }

    // ── Execution ────────────────────────────────────────────────────────────

    /// <summary>A run of at most this many batches executes in the request; a bigger one in the background (202).</summary>
    internal const int InlineBatchLimit = 20;

    /// <summary>
    /// Approved → Executing once (a second execute or a double click is 409, TS-PR-011), then the batches: in this request
    /// when there are at most <see cref="InlineBatchLimit"/> (or no job client), else as a background job — the run is
    /// returned Executing and the caller polls it (D-AP-13).
    /// </summary>
    public async Task<PaymentRunDetailModel> ExecuteAsync(Guid runUuid, ApActor actor, CancellationToken ct = default)
    {
        await EnsureDiscountAccountAsync(runUuid, ct);

        if (_fin.Database.IsRelational())
        {
            var rows = await OwnRuns().Where(r => r.Uuid == runUuid && r.Status == ArApEnums.PaymentRunStatus.Approved)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.Status, ArApEnums.PaymentRunStatus.Executing)
                    .SetProperty(r => r.ExecutedBy, actor.UserId)
                    .SetProperty(r => r.ExecutedByName, actor.UserName)
                    .SetProperty(r => r.ExecutedAt, DateTime.UtcNow), ct);
            if (rows == 0)
            {
                if (!await OwnRuns().AnyAsync(r => r.Uuid == runUuid, ct)) throw new NotFoundException("Payment run", runUuid);
                throw new ConflictException("Only an approved payment run can be executed.");
            }
        }
        else
        {
            var run = await OwnRuns().FirstOrDefaultAsync(r => r.Uuid == runUuid, ct) ?? throw new NotFoundException("Payment run", runUuid);
            if (run.Status != ArApEnums.PaymentRunStatus.Approved) throw new ConflictException("Only an approved payment run can be executed.");
            run.Status = ArApEnums.PaymentRunStatus.Executing;
            run.ExecutedBy = actor.UserId; run.ExecutedByName = actor.UserName; run.ExecutedAt = DateTime.UtcNow;
            await _fin.SaveChangesAsync(ct);
        }
        _fin.ChangeTracker.Clear();
        return await RunOrQueueAsync(runUuid, actor, ct);
    }

    /// <summary>
    /// A run left Executing (a crash, a stopped job) carries on: batches already Paid or Failed are skipped, the Pending
    /// ones are executed — each claimed under its own row lock, so a resume racing a still-running execution never pays a
    /// batch twice (D-AP-13).
    /// </summary>
    public async Task<PaymentRunDetailModel> ResumeAsync(Guid runUuid, ApActor actor, CancellationToken ct = default)
    {
        var status = await OwnRuns().AsNoTracking().Where(r => r.Uuid == runUuid).Select(r => r.Status).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Payment run", runUuid);
        if (status != ArApEnums.PaymentRunStatus.Executing)
            throw new ConflictException("Only an executing payment run can be resumed.");
        await EnsureDiscountAccountAsync(runUuid, ct); // REV-20: as execute does
        return await RunOrQueueAsync(runUuid, actor, ct);
    }

    private async Task<PaymentRunDetailModel> RunOrQueueAsync(Guid runUuid, ApActor actor, CancellationToken ct)
    {
        var runId = await OwnRuns().Where(r => r.Uuid == runUuid).Select(r => r.Id).FirstAsync(ct);
        var pending = await _fin.Set<PaymentRunBatch>().CountAsync(b => b.PaymentRunId == runId && b.Status == ArApEnums.PaymentRunBatchStatus.Pending, ct);
        if (_jobs is not null && pending > InlineBatchLimit)
        {
            var org = Org;
            _jobs.Enqueue<PaymentRunExecutionJob>(j => j.RunAsync(org, runUuid, actor.UserId, actor.UserName));
            return await GetAsync(runUuid, ct);
        }
        return await ContinueAsync(runUuid, actor, ct);
    }

    /// <summary>
    /// The batches still Pending, then — before Completed — the A44 BillPayment of every Paid batch (REV-7, D-AP-17).
    /// Each batch is two short transactions: <see cref="PrepareBatchAsync"/> re-prices it to today and creates its
    /// SupplierPayment (DRAFT, recorded on the batch), <see cref="SettleBatchAsync"/> approves and posts it and marks the batch
    /// Paid. Safe to call again at any point: a batch whose payment already exists is settled, a Paid one is skipped, and
    /// the postings are idempotent (the gateway dedupes by the payment's key), so a lost publish is made good by the next
    /// resume / job retry — the run stays Executing until they have been handed over.
    /// </summary>
    internal async Task<PaymentRunDetailModel> ContinueAsync(Guid runUuid, ApActor actor, CancellationToken ct = default)
    {
        var runId = await OwnRuns().Where(r => r.Uuid == runUuid).Select(r => r.Id).FirstOrDefaultAsync(ct);
        if (runId == 0) throw new NotFoundException("Payment run", runUuid);

        var sequence = await SequenceAsync(runId, ct);
        var pending  = await _fin.Set<PaymentRunBatch>().AsNoTracking()
            .Where(b => b.PaymentRunId == runId && b.Status == ArApEnums.PaymentRunBatchStatus.Pending)
            .OrderBy(b => b.VendorName).ThenBy(b => b.Id).Select(b => b.Id).ToListAsync(ct);

        var stopped = false;
        var published = new HashSet<Guid>();
        var timing = new System.Diagnostics.Stopwatch[3] { new(), new(), new() }; // prepare, settle, publish + timeline
        foreach (var batchId in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                timing[0].Start();
                var (prepared, after) = await PrepareBatchAsync(runId, batchId, sequence[batchId], actor, ct);
                timing[0].Stop();
                _fin.ChangeTracker.Clear();
                if (prepared == BatchStep.Stopped) { stopped = true; break; }
                if (prepared == BatchStep.Done) continue;

                timing[1].Start();
                var settled = await SettleBatchAsync(runId, batchId, actor, after, ct);
                timing[1].Stop();
                _fin.ChangeTracker.Clear();
                if (settled is { Stopped: true }) { stopped = true; break; }
                if (settled?.Paid is { } p)
                {
                    // Published right after its own commit; the end-of-pass sweep re-sends only what this pass did not.
                    timing[2].Start();
                    if (_postings is not null) await _postings.PublishAsync(p.PaymentUuid, ct);
                    published.Add(p.PaymentUuid);
                    await TimelineAsync(p.PaymentUuid, actor, ct);
                    timing[2].Stop();
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _fin.ChangeTracker.Clear();
                _log?.LogWarning(ex, "Payment run batch {BatchId} of run {RunId} failed; the other batches go on.", batchId, runId);
                await FailBatchAsync(runId, batchId, ex, actor, ct);
            }
        }

        foreach (var sw in timing) sw.Stop();
        _log?.LogInformation("Payment run {RunId}: {Batches} batches — prepare {Prepare} ms, settle {Settle} ms, publish {Publish} ms.",
            runId, pending.Count, timing[0].ElapsedMilliseconds, timing[1].ElapsedMilliseconds, timing[2].ElapsedMilliseconds);

        if (!stopped) await PublishPaidAsync(runId, published, ct);

        // Completed (BR-PR-009: immutable from now on) once nothing is Pending, with the totals of what was paid.
        await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            await LockRunRowAsync(runId, ct);
            var run = await OwnRuns().Include(r => r.Batches).Include(r => r.Items).AsSplitQuery().FirstAsync(r => r.Id == runId, ct);
            if (run.Status != ArApEnums.PaymentRunStatus.Executing || run.Batches.Any(b => b.Status == ArApEnums.PaymentRunBatchStatus.Pending))
                return false;
            run.Status           = ArApEnums.PaymentRunStatus.Completed;
            run.CompletedAt      = DateTime.UtcNow;
            run.FailedBatchCount = run.Batches.Count(b => b.Status == ArApEnums.PaymentRunBatchStatus.Failed);
            await RecalculateAsync(run, ct);
            await _fin.SaveChangesAsync(ct);
            return true;
        });
        _fin.ChangeTracker.Clear();
        return await GetAsync(runUuid, ct);
    }

    /// <summary>A batch's place in the whole run (vendor name, id): cheque numbers and export references use it (REV-22).</summary>
    private async Task<Dictionary<int, int>> SequenceAsync(int runId, CancellationToken ct) =>
        (await _fin.Set<PaymentRunBatch>().AsNoTracking().Where(b => b.PaymentRunId == runId)
            .OrderBy(b => b.VendorName).ThenBy(b => b.Id).Select(b => b.Id).ToListAsync(ct))
        .Select((id, i) => (id, i + 1)).ToDictionary(x => x.id, x => x.Item2);

    internal static string BatchReference(string runNumber, int sequence) => $"{runNumber}-{sequence:D3}";

    /// <summary>The run's row lock (no-op UPDATE) inside the caller's transaction — taken before any invoice lock.</summary>
    private async Task LockRunRowAsync(int runId, CancellationToken ct)
    {
        if (!_fin.Database.IsRelational()) return;
        await OwnRuns().Where(r => r.Id == runId).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, r => r.Status), ct);
        foreach (var e in _fin.ChangeTracker.Entries<PaymentRun>().Where(e => e.Entity.Id == runId).ToList()) e.State = EntityState.Detached;
    }

    /// <summary>The batch's row, locked and read afresh only while still Pending; null when another attempt has handled it.</summary>
    private async Task<PaymentRunBatch?> ClaimBatchAsync(int runId, int batchId, CancellationToken ct)
    {
        if (_fin.Database.IsRelational()
            && await _fin.Set<PaymentRunBatch>().Where(b => b.Id == batchId && b.Status == ArApEnums.PaymentRunBatchStatus.Pending)
                   .ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, b => b.Status), ct) == 0)
            return null;
        foreach (var e in _fin.ChangeTracker.Entries<PaymentRunBatch>().Where(e => e.Entity.Id == batchId).ToList()) e.State = EntityState.Detached;
        var batch = await _fin.Set<PaymentRunBatch>().Include(b => b.Items).FirstAsync(b => b.Id == batchId && b.PaymentRunId == runId, ct);
        return batch.Status == ArApEnums.PaymentRunBatchStatus.Pending ? batch : null;
    }

    private enum BatchStep { Ready, Done, Stopped }

    private sealed record PaidBatch(Guid PaymentUuid);

    private sealed record SettleOutcome(PaidBatch? Paid, bool Stopped);

    /// <summary>
    /// Transaction 1 of a batch: the run row, then the batch row, then (inside the payment repository) the invoices. A batch
    /// that already has its SupplierPayment (an earlier attempt) is Ready. Otherwise it is re-priced to the day it is paid
    /// (REV-4/REV-21, D-AP-16): the payment date is the run's date, or today when that has passed; each item pays what is
    /// still owed (at most what was approved; an invoice settled meanwhile leaves the run); the discount is recomputed for
    /// that date and amount. Then the DRAFT payment is created and recorded on the batch. Short, so the org's payment
    /// numbering lock the create takes is held only for this (REV-19).
    /// </summary>
    private async Task<(BatchStep Step, Dictionary<int, PaymentScheduleService.InvoiceState>? After)> PrepareBatchAsync(int runId, int batchId, int sequence, ApActor actor, CancellationToken ct) =>
        await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            await LockRunRowAsync(runId, ct);
            var run = await OwnRuns().AsNoTracking().FirstAsync(r => r.Id == runId, ct);
            if (run.Status != ArApEnums.PaymentRunStatus.Executing) return (BatchStep.Stopped, null);

            var batch = await ClaimBatchAsync(runId, batchId, ct);
            if (batch is null) return (BatchStep.Done, null);
            if (batch.SupplierPaymentUuid is not null) return (BatchStep.Ready, null);

            var items = batch.Items.Where(i => i.Status == ArApEnums.PaymentRunItemStatus.Approved).OrderBy(i => i.DueDate).ThenBy(i => i.Id).ToList();
            var scheduleIds = items.Select(i => i.PaymentScheduleId).ToList();
            var schedules = await OwnSchedules().Where(s => scheduleIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);

            var payDate = run.PaymentDate < Today ? Today : run.PaymentDate;
            var state   = await _schedules.InvoiceStateAsync(schedules.Values.Select(s => s.InvoiceId).ToList(), ct);
            var now     = DateTime.UtcNow;
            var settledElsewhere = new List<int>();
            foreach (var item in items.ToList())
            {
                var s = schedules[item.PaymentScheduleId];
                // An invoice that stopped being a payable (rejected, reversed, deleted) fails the batch, as the payment would;
                // one merely settled meanwhile leaves the run (REV-21).
                if (!state.TryGetValue(s.InvoiceId, out var st) || !InvoiceMatchStatus.Is(st.MatchStatus, InvoiceMatchStatus.Approved))
                    throw new ConflictException($"Invoice {item.InvoiceNumber} has been {(st.Exists ? st.MatchStatus.ToLowerInvariant() : "deleted")}, so nothing is owed on it.");
                var owed = st.Remaining;
                // QA-6: the schedule sync clears the run's hold when the invoice gets settled by hand; that item has left the
                // run. A hold lost for any other reason (the schedule taken by another run) still fails the batch.
                if (s.ReservedByRunId != runId && owed > 0m)
                    throw new ConflictException($"A schedule of {batch.VendorName} is no longer held by payment run {run.RunNumber}.");
                if (owed <= 0m)
                {
                    item.Status        = ArApEnums.PaymentRunItemStatus.Removed;
                    item.RemovedReason = "Settled or no longer payable before the run was executed.";
                    item.RemovedAt     = now;
                    settledElsewhere.Add(s.Id);
                    items.Remove(item);
                    continue;
                }
                item.OriginalAmount = Math.Min(item.OriginalAmount, owed);
                var discount = run.IncludeDiscount
                    ? PayablesMath.Discount(item.OriginalAmount, s.DiscountPercent, s.DiscountDate, payDate)
                    : PayablesMath.Discount(item.OriginalAmount, null, null, payDate);
                item.DiscountAmount   = discount.DiscountAmount;
                item.NetPaymentAmount = discount.NetPaymentAmount;
            }
            await ReleaseAsync(settledElsewhere, runId, ct);

            batch.ItemCount     = items.Count;
            batch.TotalAmount   = items.Sum(i => i.OriginalAmount);
            batch.TotalDiscount = items.Sum(i => i.DiscountAmount);
            batch.NetAmount     = items.Sum(i => i.NetPaymentAmount);
            if (items.Count == 0)
            {
                batch.Status        = ArApEnums.PaymentRunBatchStatus.Paid;
                batch.FailureReason = "Nothing left to pay: every invoice was settled before the run was executed.";
                batch.ExecutedAt    = now;
                await _fin.SaveChangesAsync(ct);
                return (BatchStep.Done, null);
            }

            var cheque  = batch.PaymentMethod == "CHEQUE";
            var payDay  = payDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var request = new CreateSupplierPaymentRequest
            {
                SupplierId    = batch.VendorUuid,
                SupplierName  = batch.VendorName,
                PaymentDate   = payDay,
                PaymentMethod = batch.PaymentMethod,
                TotalAmount   = batch.NetAmount,
                BankAccount   = run.BankAccount,
                ChequeNo      = cheque ? BatchReference(run.RunNumber, sequence) : null,
                ChequeDate    = cheque ? payDay : null,
                Notes         = $"Payment run {run.RunNumber}",
                PaymentType   = "STANDARD",
                CurrencyCode  = batch.CurrencyCode,
                Lines         = items.Select(i => new CreateSupplierPaymentLineRequest
                {
                    InvoiceUuid     = i.InvoiceUuid,
                    AllocatedAmount = i.OriginalAmount,
                    DiscountAmount  = i.DiscountAmount,
                    Notes           = i.DiscountAmount > 0m ? $"Payment run {run.RunNumber}, early-payment discount {i.DiscountAmount:0.00}" : $"Payment run {run.RunNumber}"
                }).ToList()
            };

            var paymentUuid = await _payments.CreateAsync(request, actor.UserId ?? 0);
            var number = await _fin.SupplierPayments.AsNoTracking().Where(p => p.UUID == paymentUuid).Select(p => p.PaymentNumber).FirstAsync(ct);

            // The repository's save may have detached what this context tracked: the batch's rows afresh.
            var fresh = await _fin.Set<PaymentRunBatch>().FirstAsync(b => b.Id == batchId, ct);
            fresh.SupplierPaymentUuid   = paymentUuid;
            fresh.SupplierPaymentNumber = number;
            fresh.PaymentReference      = cheque ? request.ChequeNo : number;
            await _fin.SaveChangesAsync(ct);

            // What the invoices will look like once this payment posts (its lines already count as settled): the settle step
            // brings the schedules in step from this instead of reading the invoices again (QA-6 perf).
            var after = new Dictionary<int, PaymentScheduleService.InvoiceState>();
            foreach (var item in items)
            {
                var invoiceId = schedules[item.PaymentScheduleId].InvoiceId;
                var st = after.TryGetValue(invoiceId, out var seen) ? seen : state[invoiceId];
                after[invoiceId] = st with { Settled = st.Settled + item.OriginalAmount };
            }
            return (BatchStep.Ready, after);
        });

    /// <summary>Transaction 2 of a batch: its payment approved (if DRAFT) and posted (if APPROVED); items, schedules and batch Paid.</summary>
    /// <param name="after">From this pass's prepare: the payment is still the DRAFT it created, and the invoices' state after posting.</param>
    private async Task<SettleOutcome?> SettleBatchAsync(int runId, int batchId, ApActor actor, Dictionary<int, PaymentScheduleService.InvoiceState>? after, CancellationToken ct) =>
        await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            await LockRunRowAsync(runId, ct);
            var run = await OwnRuns().AsNoTracking().FirstAsync(r => r.Id == runId, ct);
            if (run.Status != ArApEnums.PaymentRunStatus.Executing) return new SettleOutcome(null, true);

            var batch = await ClaimBatchAsync(runId, batchId, ct);
            if (batch?.SupplierPaymentUuid is not { } paymentUuid) return null;

            var user = actor.UserId ?? 0;
            // Claimed right after this pass created it, the payment is still its DRAFT (a racer that settled it first made the
            // batch not Pending, so the claim above returned nothing); a batch resumed from an earlier attempt is read.
            var status = after is not null ? "DRAFT"
                : await _fin.SupplierPayments.AsNoTracking().Where(p => p.UUID == paymentUuid).Select(p => p.Status).FirstAsync(ct);
            if (status == "DRAFT") { await _payments.ApproveAsync(paymentUuid, user); status = "APPROVED"; }
            if (status == "APPROVED") { await _payments.PostAsync(paymentUuid, user); status = "POSTED"; }
            if (status != "POSTED") throw new ConflictException($"Supplier payment {batch.SupplierPaymentNumber} of the batch is {status}.");

            batch = await _fin.Set<PaymentRunBatch>().Include(b => b.Items).FirstAsync(b => b.Id == batchId, ct);
            var now = DateTime.UtcNow;
            var items = batch.Items.Where(i => i.Status == ArApEnums.PaymentRunItemStatus.Approved).ToList();
            foreach (var item in items)
            {
                item.Status = ArApEnums.PaymentRunItemStatus.Paid;
                item.PaidAt = now;
            }
            batch.Status        = ArApEnums.PaymentRunBatchStatus.Paid;
            batch.ExecutedAt    = now;
            batch.FailureReason = null;

            var scheduleIds = items.Select(i => i.PaymentScheduleId).ToList();
            var schedules = await OwnSchedules().Where(s => scheduleIds.Contains(s.Id)).ToListAsync(ct);
            var state = after ?? await _schedules.InvoiceStateAsync(schedules.Select(s => s.InvoiceId).ToList(), ct);
            foreach (var s in schedules)
            {
                s.ReservedByRunId = null;
                s.PaidInRunId     = runId;
                PaymentScheduleService.Apply(s, state.GetValueOrDefault(s.InvoiceId));
                s.ModifiedAt = now;
            }

            await _fin.SaveChangesAsync(ct);
            return new SettleOutcome(new PaidBatch(paymentUuid), false);
        });

    /// <summary>
    /// A failed batch: Failed with the reason, its items Failed, its schedules released, its unposted payment (if one was
    /// created) cancelled — in a transaction of its own; a batch a racing attempt has meanwhile handled is left alone.
    /// </summary>
    private async Task FailBatchAsync(int runId, int batchId, Exception ex, ApActor actor, CancellationToken ct)
    {
        try
        {
            await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
            {
                await LockRunRowAsync(runId, ct);
                var batch = await ClaimBatchAsync(runId, batchId, ct);
                if (batch is null) return false;

                if (batch.SupplierPaymentUuid is { } paymentUuid
                    && await _fin.SupplierPayments.AsNoTracking().Where(p => p.UUID == paymentUuid).Select(p => p.Status).FirstOrDefaultAsync(ct)
                       is "DRAFT" or "APPROVED")
                    await _payments.CancelAsync(paymentUuid, actor.UserId ?? 0);

                batch = await _fin.Set<PaymentRunBatch>().Include(b => b.Items).FirstAsync(b => b.Id == batchId, ct);
                var items = batch.Items.Where(i => i.Status == ArApEnums.PaymentRunItemStatus.Approved).ToList();
                foreach (var item in items) item.Status = ArApEnums.PaymentRunItemStatus.Failed;
                batch.Status        = ArApEnums.PaymentRunBatchStatus.Failed;
                batch.FailureReason = Trim(ex.Message, 1000);
                batch.ExecutedAt    = DateTime.UtcNow;
                await ReleaseAsync(items.Select(i => i.PaymentScheduleId).ToList(), runId, ct);
                await _fin.SaveChangesAsync(ct);
                return true;
            });
        }
        catch (Exception inner) when (!ct.IsCancellationRequested)
        {
            _fin.ChangeTracker.Clear();
            _log?.LogError(inner, "Payment run batch {BatchId} failed and could not be marked Failed.", batchId);
        }
        _fin.ChangeTracker.Clear();
    }

    /// <summary>
    /// REV-7 (D-AP-17, QA-6) — before a pass completes the run, hands to the posting gateway the BillPayment of every Paid
    /// batch this pass did <b>not</b> publish itself: batches paid by an earlier, interrupted attempt (crash or cancel between
    /// commit and publish, a commit whose acknowledgement was lost) or by a racing resume. (A gateway that answers with a failure
    /// keeps its own retry, as for every other document.) A clean first
    /// run re-sends nothing. The gateway dedupes by the payment's idempotency key, so a re-send of one already posted posts
    /// nothing twice. The run stays Executing until this has run, so a resume always gets here. Never throws.
    /// </summary>
    private async Task PublishPaidAsync(int runId, IReadOnlySet<Guid> alreadyPublished, CancellationToken ct)
    {
        if (_postings is null) return;
        var payments = (await _fin.Set<PaymentRunBatch>().AsNoTracking()
            .Where(b => b.PaymentRunId == runId && b.Status == ArApEnums.PaymentRunBatchStatus.Paid && b.SupplierPaymentUuid != null)
            .Select(b => b.SupplierPaymentUuid!.Value).ToListAsync(ct)).Where(p => !alreadyPublished.Contains(p));
        foreach (var payment in payments)
        {
            ct.ThrowIfCancellationRequested();
            await _postings.PublishAsync(payment, ct);
        }
    }

    /// <summary>
    /// The document timeline of a payment a batch just posted. REV-9: the organization is passed explicitly (the 5-argument
    /// overload) — in the background job there is no HttpContext, so the enqueue filter would store none and the append
    /// would land in the default organization's timeline.
    /// </summary>
    private async Task TimelineAsync(Guid paymentUuid, ApActor actor, CancellationToken ct)
    {
        if (_jobs is null) return;
        try
        {
            var p = await _fin.SupplierPayments.AsNoTracking().Where(x => x.UUID == paymentUuid)
                .Select(x => new { x.TraceId, x.PaymentNumber, x.TotalAmount, x.PaymentMethod, x.Status }).FirstOrDefaultAsync(ct);
            if (p is null) return;
            var org   = Org;
            var notes = $"Amount: {p.TotalAmount:F2}, Method: {p.PaymentMethod}, Status: {p.Status}.";
            foreach (var type in new[] { "PAYMENT_CREATED", "PAYMENT_APPROVED", "PAYMENT_POSTED" })
                _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                    p.TraceId,
                    new TimelineEvent(type, "SUPPLIER_PAYMENT", paymentUuid, p.PaymentNumber, DateTime.UtcNow, actor.UserId ?? 0, notes),
                    "SUPPLIER_PAYMENT", p.PaymentNumber, org));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log?.LogDebug(ex, "The timeline of supplier payment {PaymentUuid} could not be appended.", paymentUuid);
        }
    }

    // ── Export (D-15) ────────────────────────────────────────────────────────

    public async Task<PaymentFile> ExportAsync(Guid runUuid, string? format, ApActor actor, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(format) && !string.Equals(format.Trim(), "csv", StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException(ArApEnums.UnknownText("format", format, ["csv"]));

        var run = await OwnRuns().Include(r => r.Batches).ThenInclude(b => b.Items).AsSplitQuery()
                      .FirstOrDefaultAsync(r => r.Uuid == runUuid, ct) ?? throw new NotFoundException("Payment run", runUuid);
        if (run.Status is not (ArApEnums.PaymentRunStatus.Approved or ArApEnums.PaymentRunStatus.Executing or ArApEnums.PaymentRunStatus.Completed))
            throw new ConflictException($"Payment run {run.RunNumber} is {run.Status}; only an approved or executed run has a payment file.");

        var batches = run.Batches
            .Where(b => b.Status != ArApEnums.PaymentRunBatchStatus.Failed
                     && (run.Status != ArApEnums.PaymentRunStatus.Completed || b.Status == ArApEnums.PaymentRunBatchStatus.Paid)
                     && b.Items.Any(i => i.Status is ArApEnums.PaymentRunItemStatus.Approved or ArApEnums.PaymentRunItemStatus.Paid))
            .OrderBy(b => b.VendorName).ThenBy(b => b.Id).ToList();

        var vendors = batches.Select(b => b.VendorUuid).Distinct().ToList();
        IReadOnlyDictionary<Guid, PartnerInfo> partners = new Dictionary<Guid, PartnerInfo>();
        if (_partners is not null && vendors.Count > 0)
        {
            try { partners = await _partners.GetManyAsync(Org, vendors, null, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log?.LogWarning(ex, "Vendor codes could not be read for payment run {Run}.", run.RunNumber); }
        }

        var csv = new StringBuilder();
        csv.AppendLine("RunNumber,PaymentDate,VendorCode,VendorName,BankName,AccountNumber,IBAN,SWIFT,Amount,Currency,PaymentMethod,Reference,Invoices");
        // REV-22: the batch's place in the whole run — the same number its cheque gets at execution.
        var sequence = await SequenceAsync(run.Id, ct);
        foreach (var b in batches)
        {
            PartnerBankInfo? bank = null;
            if (_partners is not null)
            {
                // Read now, for this file only — never stored or logged (D-15, §10.1).
                try { bank = await _partners.GetBankDetailsAsync(Org, b.VendorUuid, ct); }
                catch (Exception) when (!ct.IsCancellationRequested) { bank = null; }
            }
            var items = b.Items.Where(i => i.Status is ArApEnums.PaymentRunItemStatus.Approved or ArApEnums.PaymentRunItemStatus.Paid).ToList();
            var amount = run.Status == ArApEnums.PaymentRunStatus.Completed ? b.NetAmount : items.Sum(i => i.NetPaymentAmount);
            csv.AppendLine(string.Join(",",
                Csv(run.RunNumber), run.PaymentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Csv(partners.GetValueOrDefault(b.VendorUuid)?.Code), Csv(partners.GetValueOrDefault(b.VendorUuid)?.Name ?? b.VendorName),
                Csv(bank?.BankName), Csv(bank?.AccountNumber), Csv(bank?.Iban), Csv(bank?.Swift),
                amount.ToString("0.00", CultureInfo.InvariantCulture), Csv(b.CurrencyCode), Csv(b.PaymentMethod),
                Csv(b.PaymentReference ?? b.SupplierPaymentNumber ?? BatchReference(run.RunNumber, sequence[b.Id])),
                Csv(string.Join("; ", items.Select(i => i.InvoiceNumber)))));
        }

        var fileName = $"{run.RunNumber}.csv";
        run.PaymentFileName = fileName;
        run.ExportedAt      = DateTime.UtcNow;
        try { await _fin.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { _fin.ChangeTracker.Clear(); /* the file is still right; only the stamp is lost */ }

        return new PaymentFile(fileName, "text/csv", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray());
    }

    /// <summary>RFC 4180 field; a leading =, +, - or @ is prefixed with ' so a spreadsheet never runs it as a formula.</summary>
    internal static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var v = value;
        if (v[0] is '=' or '+' or '-' or '@') v = "'" + v;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    // ── Reads ────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<PaymentRunSummaryModel>> ListAsync(string? status, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (p, size) = ApSupport.Paging(page, pageSize);
        var q = OwnRuns().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = ArApEnums.Canonical(status, ArApEnums.PaymentRunStatus.All)
                ?? throw new BadRequestException(ArApEnums.UnknownText("status", status, ArApEnums.PaymentRunStatus.All));
            q = q.Where(r => r.Status == s);
        }
        var total = await q.CountAsync(ct);
        var rows  = await q.OrderByDescending(r => r.RunDate).ThenByDescending(r => r.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return ApSupport.Page(rows.Select(Summary).ToList(), total, p, size);
    }

    public async Task<PaymentRunDetailModel> GetAsync(Guid runUuid, CancellationToken ct = default)
    {
        var run = await OwnRuns().AsNoTracking().Include(r => r.Batches).Include(r => r.Items).ThenInclude(i => i.PaymentSchedule)
                      .AsSplitQuery().FirstOrDefaultAsync(r => r.Uuid == runUuid, ct)
                  ?? throw new NotFoundException("Payment run", runUuid);

        var today = Today;
        PaymentRunItemModel Item(PaymentRunItem i) => new(
            i.Uuid, i.PaymentSchedule.Uuid, i.InvoiceUuid, i.InvoiceNumber, i.CurrencyCode, i.DueDate,
            i.OriginalAmount, i.DiscountAmount, i.NetPaymentAmount, i.DueDate.DayNumber - today.DayNumber, i.DiscountAmount > 0m,
            i.PaymentSchedule.DiscountDate, i.PaymentMethod, i.Status, i.RemovedReason);

        var groups = run.Batches.OrderBy(b => b.VendorName).ThenBy(b => b.CurrencyCode).ThenBy(b => b.PaymentMethod).Select(b => new PaymentRunBatchModel(
            b.Uuid, b.VendorUuid, b.VendorName, b.CurrencyCode, b.PaymentMethod, b.BankDetailsMasked,
            b.ItemCount, b.TotalAmount, b.TotalDiscount, b.NetAmount, b.SupplierPaymentUuid, b.SupplierPaymentNumber, b.Status, b.FailureReason,
            run.Items.Where(i => i.BatchId == b.Id && i.Status != ArApEnums.PaymentRunItemStatus.Removed)
                .OrderBy(i => i.DueDate).ThenBy(i => i.Id).Select(Item).ToList())).ToList();

        var removed = run.Items.Where(i => i.Status == ArApEnums.PaymentRunItemStatus.Removed).OrderBy(i => i.InvoiceNumber).Select(Item).ToList();

        // BR-PR-002: what the cutoff would have taken but a block holds back — listed, never paid.
        var blockedQuery = OwnSchedules().AsNoTracking().Where(s => s.PaymentBlock && s.Status == ArApEnums.ScheduleStatus.Blocked && s.DueDate <= run.DueDateCutoff);
        var criteria = Criteria(run.SelectionCriteria);
        if (criteria.Vendor is { } v) blockedQuery = blockedQuery.Where(s => s.VendorUuid == v);
        if (criteria.Currency is { } c) blockedQuery = blockedQuery.Where(s => s.CurrencyCode == c);
        var blocked = run.Status is ArApEnums.PaymentRunStatus.Completed or ArApEnums.PaymentRunStatus.Cancelled
            ? []
            : (await blockedQuery.OrderBy(s => s.VendorName).ThenBy(s => s.DueDate).Take(500).ToListAsync(ct))
                .Select(s => new BlockedScheduleModel(s.Uuid, s.InvoiceUuid, s.InvoiceNumber, s.VendorUuid, s.VendorName, s.CurrencyCode,
                    s.DueDate, s.AmountRemaining, s.BlockReason)).ToList();

        var warnings = new List<string>();
        if (EditableStatuses.Contains(run.Status) || run.Status == ArApEnums.PaymentRunStatus.Approved)
            if (run.Items.Any(i => ActiveItemStatuses.Contains(i.Status) && i.DiscountAmount > 0m) && await DiscountAccountProblemAsync(ct) is { } problem)
                warnings.Add(problem);

        return new PaymentRunDetailModel(Summary(run), run.BankAccount, run.IncludeDiscount, run.SelectionCriteria,
            run.ExecutedByName, run.ExecutedAt, run.CancelledByName, run.CancelledAt, run.CancelReason,
            run.PaymentFileName, run.ExportedAt, ApSupport.RowVersionText(run.RowVersion), groups, removed, blocked) { Warnings = warnings };
    }

    private static (Guid? Vendor, string? Currency) Criteria(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            Guid? vendor = doc.RootElement.TryGetProperty("vendorUuid", out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g : null;
            string? currency = doc.RootElement.TryGetProperty("currencyCode", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            return (vendor, currency);
        }
        catch (JsonException) { return (null, null); }
    }

    private static PaymentRunSummaryModel Summary(PaymentRun r) => new(
        r.Uuid, r.RunNumber, r.RunDate, r.PaymentDate, r.DueDateCutoff, r.Status, r.PaymentMethod, r.BaseCurrencyCode,
        r.TotalVendors, r.TotalItems, r.TotalAmount, r.TotalDiscount, r.NetPaymentAmount, r.FailedBatchCount,
        r.ProposedByName, r.ProposedAt, r.ApprovedByName, r.ApprovedAt, r.CompletedAt);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<Dictionary<Guid, PartnerFinancialProfile>> VendorProfilesAsync(IEnumerable<Guid> vendors, CancellationToken ct)
    {
        var ids = vendors.Distinct().ToList();
        if (ids.Count == 0) return [];
        return await _fin.Set<PartnerFinancialProfile>().AsNoTracking()
            .Where(p => p.OrganizationId == Org && p.PartnerRole == ArApEnums.PartnerRole.Vendor && ids.Contains(p.PartnerUuid))
            .ToDictionaryAsync(p => p.PartnerUuid, ct);
    }

    /// <summary>"HBL ••••1234" per vendor — the only bank detail ever stored (§8).</summary>
    private async Task<Dictionary<Guid, string?>> BankMasksAsync(IEnumerable<Guid> vendors, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string?>();
        if (_partners is null) return result;
        foreach (var vendor in vendors.Distinct())
        {
            try
            {
                var bank = await _partners.GetBankDetailsAsync(Org, vendor, ct);
                result[vendor] = Mask(bank);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { result[vendor] = null; }
        }
        return result;
    }

    internal static string? Mask(PartnerBankInfo? bank)
    {
        if (bank is null) return null;
        var account = (bank.Iban ?? bank.AccountNumber)?.Replace(" ", "");
        if (string.IsNullOrEmpty(account) && string.IsNullOrWhiteSpace(bank.BankName)) return null;
        var tail = string.IsNullOrEmpty(account) ? "" : "••••" + account[Math.Max(0, account.Length - 4)..];
        var text = $"{bank.BankName?.Trim()} {tail}".Trim();
        return text.Length > 100 ? text[..100] : text;
    }

    private async Task<string> NextRunNumberAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            string number;
            if (_numbers is not null) number = await _numbers.NextAsync(RunNumberPrefix, DateTime.UtcNow, Org, ct);
            else
            {
                await DocumentNumbers.SerializeAsync(_fin, "finance.payment-run-number", Org);
                var start = $"{RunNumberPrefix}-{DateTime.UtcNow.Year}-";
                var existing = await _fin.Set<PaymentRun>().IgnoreQueryFilters()
                    .Where(r => r.OrganizationId == Org && r.RunNumber.StartsWith(start)).Select(r => r.RunNumber).ToListAsync(ct);
                var max = existing.Select(n => int.TryParse(n[start.Length..], out var x) ? x : 0).DefaultIfEmpty(0).Max();
                number = $"{start}{max + 1 + attempt:D5}";
            }
            if (!await _fin.Set<PaymentRun>().IgnoreQueryFilters().AnyAsync(r => r.OrganizationId == Org && r.RunNumber == number, ct))
                return number;
        }
        throw new InvalidOperationException("Could not find a free payment run number.");
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}

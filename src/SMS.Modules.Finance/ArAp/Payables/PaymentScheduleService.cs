using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.ArAp.Payables;

public interface IPaymentScheduleService
{
    Task<PaginatedResponse<PaymentScheduleModel>> ListAsync(string? status, Guid? vendorUuid, DateOnly? dueFrom, DateOnly? dueTo, int? page, int? pageSize, CancellationToken ct = default);
    Task<IReadOnlyList<PaymentScheduleModel>> UpcomingAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<IReadOnlyList<PaymentScheduleModel>> DiscountOpportunitiesAsync(DateOnly? asOfDate, int? lookAheadDays, CancellationToken ct = default);
    Task<CashFlowForecastModel> CashFlowAsync(DateOnly? from, int? weeks, CancellationToken ct = default);
    Task<DiscountCalculation> CalculateDiscountAsync(Guid scheduleUuid, DateOnly? paymentDate, CancellationToken ct = default);
    Task<PaymentScheduleModel> BlockAsync(Guid scheduleUuid, string? reason, ApActor actor, CancellationToken ct = default);
    Task<PaymentScheduleModel> UnblockAsync(Guid scheduleUuid, ApActor actor, CancellationToken ct = default);
    Task<PaymentScheduleModel> UpdateAsync(Guid scheduleUuid, UpdateScheduleRequest request, ApActor actor, CancellationToken ct = default);
    Task<PaymentScheduleModel> SetEarlyPaymentTermAsync(Guid scheduleUuid, Guid? termUuid, ApActor actor, CancellationToken ct = default);
}

/// <summary>
/// A48 C10 (tasks 351–364, 377–382; D-8, D-CORE-8) — payment schedules: one row per approved supplier invoice, kept in
/// step with the invoice (amount still owed = <see cref="Repositories.InvoiceSettlement"/>'s definition: total − paid −
/// on unposted supplier payments − legacy payments; status Open / PartiallyPaid / Paid / Cancelled, Blocked while
/// blocked, Scheduled while a payment run holds it). Created on approval (the existing invoice service calls
/// <see cref="OnInvoiceApprovedAsync"/> after its commit), re-synced after payments/notes, and — whatever a hook missed —
/// on every list read and every payment-run proposal (<see cref="EnsureAllAsync"/>).
/// </summary>
internal sealed class PaymentScheduleService : IPaymentScheduleService, IPaymentScheduleSync
{
    private static readonly string[] LiveStatuses =
        [ArApEnums.ScheduleStatus.Open, ArApEnums.ScheduleStatus.PartiallyPaid, ArApEnums.ScheduleStatus.Scheduled, ArApEnums.ScheduleStatus.Blocked];

    private readonly FinanceDbContext _fin;
    private readonly ILogger<PaymentScheduleService>? _log;
    private readonly ICurrencyService? _currency;
    private readonly ILookupsService?  _lookups;
    private readonly TimeProvider?     _clock;

    public PaymentScheduleService(
        FinanceDbContext fin, ILogger<PaymentScheduleService>? log = null,
        ICurrencyService? currency = null, ILookupsService? lookups = null, TimeProvider? clock = null)
    {
        _fin      = fin;
        _log      = log;
        _currency = currency;
        _lookups  = lookups;
        _clock    = clock;
    }

    private Guid Org => _fin.TenantContext.OrganizationId;

    internal IQueryable<PaymentSchedule> Own() => _fin.Set<PaymentSchedule>().Where(s => s.OrganizationId == Org);

    // ── Sync (IPaymentScheduleSync) ──────────────────────────────────────────

    public async Task OnInvoiceApprovedAsync(Guid invoiceUuid, CancellationToken ct = default)
    {
        try
        {
            await CreateMissingAsync(ApSupport.Payables(_fin).Where(i => i.UUID == invoiceUuid), ct);
            await RefreshAsync(Own().Where(s => s.InvoiceUuid == invoiceUuid), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _fin.ChangeTracker.Clear();
            _log?.LogWarning(ex, "The payment schedule of approved invoice {InvoiceUuid} could not be created now; the next schedule read or proposal creates it.", invoiceUuid);
        }
    }

    public async Task SyncAsync(IReadOnlyCollection<Guid> invoiceUuids, CancellationToken ct = default)
    {
        if (invoiceUuids.Count == 0) return;
        try
        {
            var uuids = invoiceUuids.Distinct().ToList();
            await CreateMissingAsync(ApSupport.Payables(_fin).Where(i => uuids.Contains(i.UUID)), ct);
            await RefreshAsync(Own().Where(s => uuids.Contains(s.InvoiceUuid)), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _fin.ChangeTracker.Clear();
            _log?.LogWarning(ex, "Payment schedules of {Count} invoice(s) could not be re-synced now; the next schedule read or proposal does it.", invoiceUuids.Count);
        }
    }

    /// <summary>
    /// D-CORE-8 — creates the schedule of every approved invoice still owing that has none, and refreshes every live one.
    /// Run before a proposal and a list read. Throws (the caller decides); a lost race on a row is skipped.
    /// </summary>
    internal async Task EnsureAllAsync(CancellationToken ct = default)
    {
        var schedules = _fin.Set<PaymentSchedule>();
        await CreateMissingAsync(ApSupport.Payables(_fin)
            .Where(i => i.TotalAmount - i.PaidAmount > 0m
                     && i.PaymentStatus != "Paid" && i.PaymentStatus != InvoicePaymentStatus.FullyPaid && i.PaymentStatus != InvoicePaymentStatus.Overpaid
                     && !schedules.Any(s => s.InvoiceId == i.Id)), ct);
        await RefreshAsync(Own().Where(s => LiveStatuses.Contains(s.Status)), ct);
    }

    private async Task CreateMissingAsync(IQueryable<Invoice> candidates, CancellationToken ct)
    {
        var schedules = _fin.Set<PaymentSchedule>();
        var invoices = await candidates.Where(i => !schedules.Any(s => s.InvoiceId == i.Id)).AsNoTracking()
            .Select(i => new { i.Id, i.UUID, i.InvoiceNumber, i.SupplierInvoiceNo, i.SupplierId, i.SupplierName, i.Currency, i.InvoiceDate, i.DueDate, i.TotalAmount })
            .ToListAsync(ct);
        if (invoices.Count == 0) return;

        var vendors  = invoices.Select(i => i.SupplierId).Distinct().ToList();
        var profiles = await _fin.Set<PartnerFinancialProfile>().AsNoTracking().Include(p => p.EarlyPaymentTerm)
            .Where(p => p.OrganizationId == Org && p.PartnerRole == ArApEnums.PartnerRole.Vendor && vendors.Contains(p.PartnerUuid))
            .ToDictionaryAsync(p => p.PartnerUuid, ct);

        var now = DateTime.UtcNow;
        foreach (var i in invoices)
        {
            profiles.TryGetValue(i.SupplierId, out var profile);
            var term = profile?.EarlyPaymentTerm is { IsActive: true } t ? t : null;
            var invoiceDate = DateOnly.FromDateTime(i.InvoiceDate);
            var schedule = new PaymentSchedule
            {
                OrganizationId    = Org,
                InvoiceId         = i.Id,
                InvoiceUuid       = i.UUID,
                InvoiceNumber     = i.InvoiceNumber,
                SupplierInvoiceNo = i.SupplierInvoiceNo,
                VendorUuid        = i.SupplierId,
                VendorName        = i.SupplierName,
                CurrencyCode      = (i.Currency ?? "PKR").Trim().ToUpperInvariant(),
                InvoiceDate       = invoiceDate,
                DueDate           = DateOnly.FromDateTime(i.DueDate),
                OriginalAmount    = i.TotalAmount,
                AmountRemaining   = i.TotalAmount,
                PaymentPriority   = profile?.PaymentPriority ?? ArApEnums.PaymentPriority.Normal,
                PaymentMethod     = profile?.PreferredPaymentMethod,
                Status            = ArApEnums.ScheduleStatus.Open,
                CreatedAt         = now
            };
            if (term is not null) ApplyTerm(schedule, term);
            schedules.Add(schedule);
        }

        try { await _fin.SaveChangesAsync(ct); }
        catch (DbUpdateException ex)
        {
            // Another request created some of them first (unique InvoiceId): theirs stand.
            _fin.ChangeTracker.Clear();
            _log?.LogDebug(ex, "Payment schedules raced with another request; the rows it created stand.");
        }
    }

    /// <summary>Recomputes AmountRemaining, OriginalAmount, DueDate, DiscountAmount and Status of these schedules from their invoices.</summary>
    internal async Task RefreshAsync(IQueryable<PaymentSchedule> query, CancellationToken ct)
    {
        var rows = await query.ToListAsync(ct);
        if (rows.Count == 0) return;

        var state = await InvoiceStateAsync(rows.Select(r => r.InvoiceId).ToList(), ct);
        var changed = false;
        foreach (var s in rows)
        {
            var st = state.GetValueOrDefault(s.InvoiceId);
            var before = (s.AmountRemaining, s.OriginalAmount, s.Status, s.DueDate, s.DiscountAmount);
            Apply(s, st);
            if (before != (s.AmountRemaining, s.OriginalAmount, s.Status, s.DueDate, s.DiscountAmount))
            {
                s.ModifiedAt = DateTime.UtcNow;
                changed = true;
            }
        }
        if (!changed) return;

        try { await _fin.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException ex)
        {
            // Someone changed one of them meanwhile; the next sync recomputes it.
            foreach (var e in _fin.ChangeTracker.Entries<PaymentSchedule>().ToList()) e.State = EntityState.Detached;
            _log?.LogDebug(ex, "A payment schedule changed while it was being re-synced; it is re-synced next time.");
        }
    }

    /// <summary>One invoice's state for its schedule. <c>Exists = false</c>: deleted (or another organization's).</summary>
    internal readonly record struct InvoiceState(bool Exists, string MatchStatus, decimal TotalAmount, decimal Settled, DateOnly DueDate)
    {
        public decimal Remaining => Math.Max(0m, TotalAmount - Settled);
    }

    /// <summary>Total, and what is paid or promised (posted + unposted supplier payments + legacy payments), per invoice id — in three queries.</summary>
    internal async Task<Dictionary<int, InvoiceState>> InvoiceStateAsync(IReadOnlyCollection<int> invoiceIds, CancellationToken ct)
    {
        var result = new Dictionary<int, InvoiceState>(invoiceIds.Count);
        foreach (var chunk in invoiceIds.Distinct().Chunk(1000))
        {
            var ids = chunk.ToList();
            var invoices = await Repositories.InvoiceRowLocks.Own(_fin).AsNoTracking().Where(i => ids.Contains(i.Id))
                .Select(i => new { i.Id, i.UUID, i.MatchStatus, i.TotalAmount, i.PaidAmount, i.DueDate }).ToListAsync(ct);
            var uuids = invoices.Select(i => i.UUID).ToList();

            var unposted = await _fin.SupplierPaymentLines.AsNoTracking()
                .Where(l => uuids.Contains(l.InvoiceUuid) && (l.SupplierPayment.Status == "DRAFT" || l.SupplierPayment.Status == "APPROVED"))
                .GroupBy(l => l.InvoiceUuid).Select(g => new { g.Key, Sum = g.Sum(l => l.AllocatedAmount) })
                .ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
            var legacy = await _fin.Payments.AsNoTracking()
                .Where(p => ids.Contains(p.InvoiceId) && !p.IsDelete && p.Status != "Reversed")
                .GroupBy(p => p.InvoiceId).Select(g => new { g.Key, Sum = g.Sum(p => p.AmountPaid) })
                .ToDictionaryAsync(x => x.Key, x => x.Sum, ct);

            foreach (var i in invoices)
                result[i.Id] = new InvoiceState(true, i.MatchStatus, i.TotalAmount,
                    i.PaidAmount + unposted.GetValueOrDefault(i.UUID) + legacy.GetValueOrDefault(i.Id), DateOnly.FromDateTime(i.DueDate));
        }
        return result;
    }

    /// <summary>The schedule follows its invoice (D-8).</summary>
    internal static void Apply(PaymentSchedule s, InvoiceState st)
    {
        var cancelled = !st.Exists || !InvoiceMatchStatus.Is(st.MatchStatus, InvoiceMatchStatus.Approved);
        if (st.Exists)
        {
            s.OriginalAmount  = st.TotalAmount;
            s.AmountRemaining = st.Remaining;
            s.DueDate         = st.DueDate;
        }
        else s.AmountRemaining = 0m;

        if (s.DiscountPercent is { } pct and > 0m)
            s.DiscountAmount = ApSupport.Money(s.AmountRemaining * pct / 100m);

        s.Status = DeriveStatus(cancelled, s.AmountRemaining, st.Exists ? st.Settled : 0m, s.PaymentBlock, s.ReservedByRunId is not null);
        // A cancelled or paid schedule is never held by a run any more.
        if (s.Status is ArApEnums.ScheduleStatus.Cancelled or ArApEnums.ScheduleStatus.Paid && s.ReservedByRunId is not null
            && s.PaidInRunId is null) s.ReservedByRunId = null;
    }

    /// <summary>Cancelled → Paid → Blocked → Scheduled (held by a run) → PartiallyPaid → Open.</summary>
    internal static string DeriveStatus(bool cancelled, decimal remaining, decimal settled, bool blocked, bool reserved) =>
        cancelled       ? ArApEnums.ScheduleStatus.Cancelled
      : remaining <= 0m ? ArApEnums.ScheduleStatus.Paid
      : blocked         ? ArApEnums.ScheduleStatus.Blocked
      : reserved        ? ArApEnums.ScheduleStatus.Scheduled
      : settled > 0m    ? ArApEnums.ScheduleStatus.PartiallyPaid
      :                   ArApEnums.ScheduleStatus.Open;

    /// <summary>BR-EP-001/002 — the term's discount on this schedule: deadline = invoice date + discount days.</summary>
    internal static void ApplyTerm(PaymentSchedule s, EarlyPaymentTerm term)
    {
        // The id only: the term may be a no-tracking copy, and the navigation would make EF insert it again.
        s.EarlyPaymentTermId = term.Id;
        s.DiscountPercent    = term.DiscountPercent;
        s.DiscountDate       = PayablesMath.DiscountDate(s.InvoiceDate, term.DiscountDays);
        s.DiscountAmount     = ApSupport.Money(s.AmountRemaining * term.DiscountPercent / 100m);
    }

    internal static void ClearTerm(PaymentSchedule s)
    {
        s.EarlyPaymentTermId = null;
        s.EarlyPaymentTerm   = null;
        s.DiscountPercent    = null;
        s.DiscountDate       = null;
        s.DiscountAmount     = null;
    }

    // ── Reads ────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<PaymentScheduleModel>> ListAsync(
        string? status, Guid? vendorUuid, DateOnly? dueFrom, DateOnly? dueTo, int? page, int? pageSize, CancellationToken ct = default)
    {
        await EnsureAllSafeAsync(ct);
        var (p, size) = ApSupport.Paging(page, pageSize);

        var q = Own();
        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = ArApEnums.Canonical(status, ArApEnums.ScheduleStatus.All)
                ?? throw new BadRequestException(ArApEnums.UnknownText("status", status, ArApEnums.ScheduleStatus.All));
            q = q.Where(x => x.Status == s);
        }
        if (vendorUuid is { } v) q = q.Where(x => x.VendorUuid == v);
        if (dueFrom is { } f) q = q.Where(x => x.DueDate >= f);
        if (dueTo is { } t) q = q.Where(x => x.DueDate <= t);

        var total = await q.CountAsync(ct);
        var rows  = await Shaped(q.OrderBy(x => x.DueDate).ThenBy(x => x.Id).Skip((p - 1) * size).Take(size)).ToListAsync(ct);
        return ApSupport.Page(rows.Select(ToModel).ToList(), total, p, size);
    }

    public async Task<IReadOnlyList<PaymentScheduleModel>> UpcomingAsync(DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        await EnsureAllSafeAsync(ct);
        var start = from ?? Today;
        var end   = to ?? start.AddDays(30);
        if (end < start) throw new BadRequestException("'to' must be on or after 'from'.");

        var rows = await Shaped(Own().Where(s => s.Status != ArApEnums.ScheduleStatus.Paid && s.Status != ArApEnums.ScheduleStatus.Cancelled
                                               && s.DueDate >= start && s.DueDate <= end)
            .OrderBy(s => s.DueDate).ThenBy(s => s.Id)).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<PaymentScheduleModel>> DiscountOpportunitiesAsync(DateOnly? asOfDate, int? lookAheadDays, CancellationToken ct = default)
    {
        await EnsureAllSafeAsync(ct);
        var asOf  = asOfDate ?? Today;
        var ahead = lookAheadDays ?? 7;
        if (ahead is < 0 or > 366) throw new BadRequestException("lookAheadDays must be between 0 and 366.");
        var until = asOf.AddDays(ahead);

        var rows = await Shaped(Own().Where(s => (s.Status == ArApEnums.ScheduleStatus.Open || s.Status == ArApEnums.ScheduleStatus.PartiallyPaid
                                                  || s.Status == ArApEnums.ScheduleStatus.Scheduled)
                                               && !s.PaymentBlock && s.DiscountPercent > 0m && s.DiscountDate != null
                                               && s.DiscountDate >= asOf && s.DiscountDate <= until)
            .OrderBy(s => s.DiscountDate).ThenBy(s => s.Id)).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<CashFlowForecastModel> CashFlowAsync(DateOnly? from, int? weeks, CancellationToken ct = default)
    {
        await EnsureAllSafeAsync(ct);
        var start = from ?? Today;
        var n     = weeks ?? 8;
        if (n is < 1 or > 52) throw new BadRequestException("weeks must be between 1 and 52.");

        var baseCode = await ApSupport.PurchaseBaseCodeAsync(_currency, _lookups, Org, ct);
        var invoices = Repositories.InvoiceRowLocks.Own(_fin);
        var rows = await Own().Where(s => LiveStatuses.Contains(s.Status) && s.AmountRemaining > 0m)
            .Join(invoices, s => s.InvoiceId, i => i.Id, (s, i) => new
            {
                s.DueDate, s.AmountRemaining, s.CurrencyCode, s.PaymentBlock, s.DiscountDate, s.DiscountAmount, i.ExchangeRate
            })
            .AsNoTracking().ToListAsync(ct);

        var weekStart = PayablesMath.WeekStart(start);
        var buckets = Enumerable.Range(0, n).Select(_ => new Bucket()).ToArray();
        var overdue = new Bucket();
        var later   = new Bucket();

        foreach (var r in rows)
        {
            var amount   = ApSupport.ToBase(r.AmountRemaining, r.CurrencyCode, r.ExchangeRate, baseCode);
            var discount = r.DiscountDate is { } d && d >= start && r.DiscountAmount is > 0m && !r.PaymentBlock
                ? ApSupport.ToBase(r.DiscountAmount.Value, r.CurrencyCode, r.ExchangeRate, baseCode) : 0m;
            var index = PayablesMath.WeekIndex(r.DueDate, start, n);
            var bucket = index < 0 ? overdue : index >= n ? later : buckets[index];
            bucket.Add(amount, r.PaymentBlock, discount);
        }

        var weekRows = buckets.Select((b, i) => new CashFlowWeekModel(
            i + 1, weekStart.AddDays(7 * i), weekStart.AddDays(7 * i + 6), b.Count, b.Due, b.Blocked, b.Discount, b.Due - b.Discount)).ToList();

        return new CashFlowForecastModel(start, n, baseCode, overdue.Due, overdue.Count, overdue.Blocked, weekRows, later.Due + later.Blocked,
            TotalDue: overdue.Due + buckets.Sum(b => b.Due),
            TotalBlocked: overdue.Blocked + buckets.Sum(b => b.Blocked),
            TotalDiscountAvailable: overdue.Discount + buckets.Sum(b => b.Discount));
    }

    /// <summary>Unblocked amounts are "due"; blocked ones are a separate category (task 378).</summary>
    private sealed class Bucket
    {
        public int Count; public decimal Due, Blocked, Discount;
        public void Add(decimal amount, bool blocked, decimal discount)
        {
            Count++;
            if (blocked) Blocked += amount; else Due += amount;
            Discount += discount;
        }
    }

    public async Task<DiscountCalculation> CalculateDiscountAsync(Guid scheduleUuid, DateOnly? paymentDate, CancellationToken ct = default)
    {
        var s = await Own().AsNoTracking().FirstOrDefaultAsync(x => x.Uuid == scheduleUuid, ct)
            ?? throw new NotFoundException("Payment schedule", scheduleUuid);
        return PayablesMath.Discount(s.AmountRemaining, s.DiscountPercent, s.DiscountDate, paymentDate ?? Today);
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    public async Task<PaymentScheduleModel> BlockAsync(Guid scheduleUuid, string? reason, ApActor actor, CancellationToken ct = default)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why)) throw new BadRequestException("A reason is required.");
        if (why.Length > 200) throw new BadRequestException("The reason can be at most 200 characters.");

        var s = await ChangeableAsync(scheduleUuid, ct);
        if (s.PaymentBlock) throw new ConflictException($"The payment of invoice {s.InvoiceNumber} is already blocked.");
        await NotInRunAsync(s, ct);

        s.PaymentBlock  = true;
        s.BlockReason   = why;
        s.BlockedBy     = actor.UserId;
        s.BlockedByName = actor.UserName;
        s.BlockedAt     = DateTime.UtcNow;
        s.Status        = ArApEnums.ScheduleStatus.Blocked;
        s.ModifiedAt    = DateTime.UtcNow;
        await ApSupport.SaveAsync(_fin, "The payment schedule", ct);
        return await ModelAsync(s.Uuid, ct);
    }

    public async Task<PaymentScheduleModel> UnblockAsync(Guid scheduleUuid, ApActor actor, CancellationToken ct = default)
    {
        var s = await ChangeableAsync(scheduleUuid, ct);
        if (!s.PaymentBlock) throw new ConflictException($"The payment of invoice {s.InvoiceNumber} is not blocked.");

        s.PaymentBlock  = false;
        s.BlockReason   = null;
        s.BlockedBy     = null;
        s.BlockedByName = null;
        s.BlockedAt     = null;
        s.ModifiedAt    = DateTime.UtcNow;
        var state = await InvoiceStateAsync([s.InvoiceId], ct);
        Apply(s, state.GetValueOrDefault(s.InvoiceId));
        await ApSupport.SaveAsync(_fin, "The payment schedule", ct);
        return await ModelAsync(s.Uuid, ct);
    }

    public async Task<PaymentScheduleModel> UpdateAsync(Guid scheduleUuid, UpdateScheduleRequest request, ApActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var priority = request.PaymentPriority is null ? null : ApSupport.Priority(request.PaymentPriority);
        var method   = ApSupport.ApMethod(request.PaymentMethod, "paymentMethod");

        var s = await ChangeableAsync(scheduleUuid, ct);
        ApSupport.ExpectRowVersion(_fin, s, request.RowVersion);
        if (priority is not null) s.PaymentPriority = priority;
        if (request.PaymentMethod is not null) s.PaymentMethod = method;
        s.ModifiedAt = DateTime.UtcNow;
        await ApSupport.SaveAsync(_fin, "The payment schedule", ct);
        return await ModelAsync(s.Uuid, ct);
    }

    public async Task<PaymentScheduleModel> SetEarlyPaymentTermAsync(Guid scheduleUuid, Guid? termUuid, ApActor actor, CancellationToken ct = default)
    {
        var s = await ChangeableAsync(scheduleUuid, ct);
        await NotInRunAsync(s, ct);

        if (termUuid is { } t && t != Guid.Empty)
        {
            var term = await _fin.Set<EarlyPaymentTerm>().FirstOrDefaultAsync(x => x.Uuid == t && x.OrganizationId == Org, ct)
                ?? throw new BadRequestException($"Early payment term with key '{t}' was not found.");
            if (!term.IsActive) throw new BadRequestException($"Early payment term {term.Code} is inactive.");
            ApplyTerm(s, term);
        }
        else ClearTerm(s);

        s.ModifiedAt = DateTime.UtcNow;
        await ApSupport.SaveAsync(_fin, "The payment schedule", ct);
        return await ModelAsync(s.Uuid, ct);
    }

    private async Task<PaymentSchedule> ChangeableAsync(Guid uuid, CancellationToken ct)
    {
        var s = await Own().FirstOrDefaultAsync(x => x.Uuid == uuid, ct) ?? throw new NotFoundException("Payment schedule", uuid);
        if (s.Status is ArApEnums.ScheduleStatus.Paid or ArApEnums.ScheduleStatus.Cancelled)
            throw new ConflictException($"Invoice {s.InvoiceNumber} is {s.Status}; its schedule can no longer change.");
        return s;
    }

    private async Task NotInRunAsync(PaymentSchedule s, CancellationToken ct)
    {
        if (s.ReservedByRunId is not { } runId) return;
        var number = await _fin.Set<PaymentRun>().Where(r => r.Id == runId).Select(r => r.RunNumber).FirstOrDefaultAsync(ct);
        throw new ConflictException($"The payment of invoice {s.InvoiceNumber} is in payment run {number}; remove it from the run first.");
    }

    // ── Shaping ──────────────────────────────────────────────────────────────

    private DateOnly Today => ApSupport.Today(_clock);

    private async Task EnsureAllSafeAsync(CancellationToken ct)
    {
        try { await EnsureAllAsync(ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _fin.ChangeTracker.Clear();
            _log?.LogWarning(ex, "Payment schedules could not be brought in step before a read; showing them as stored.");
        }
    }

    internal sealed record Row(
        PaymentSchedule S, Guid? TermUuid, string? TermCode, Guid? ReservedUuid, string? ReservedNumber, Guid? PaidUuid, string? PaidNumber);

    private IQueryable<Row> Shaped(IQueryable<PaymentSchedule> q) => q.AsNoTracking().Select(s => new Row(
        s,
        s.EarlyPaymentTerm != null ? s.EarlyPaymentTerm.Uuid : null, s.EarlyPaymentTerm != null ? s.EarlyPaymentTerm.Code : null,
        s.ReservedByRun != null ? s.ReservedByRun.Uuid : null, s.ReservedByRun != null ? s.ReservedByRun.RunNumber : null,
        s.PaidInRun != null ? s.PaidInRun.Uuid : null, s.PaidInRun != null ? s.PaidInRun.RunNumber : null));

    private PaymentScheduleModel ToModel(Row r)
    {
        var s = r.S;
        return new PaymentScheduleModel(
            s.Uuid, s.InvoiceUuid, s.InvoiceNumber, s.SupplierInvoiceNo, s.VendorUuid, s.VendorName, s.CurrencyCode,
            s.InvoiceDate, s.DueDate, s.DueDate.DayNumber - Today.DayNumber,
            s.OriginalAmount, s.AmountRemaining, s.PaymentPriority, s.PaymentMethod,
            s.PaymentBlock, s.BlockReason, s.BlockedByName, s.BlockedAt,
            r.TermUuid, r.TermCode, s.DiscountDate, s.DiscountPercent, s.DiscountAmount,
            r.ReservedUuid, r.ReservedNumber, r.PaidUuid, r.PaidNumber, s.Status, ApSupport.RowVersionText(s.RowVersion));
    }

    internal async Task<PaymentScheduleModel> ModelAsync(Guid uuid, CancellationToken ct)
    {
        var row = await Shaped(Own().Where(s => s.Uuid == uuid)).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Payment schedule", uuid);
        return ToModel(row);
    }
}

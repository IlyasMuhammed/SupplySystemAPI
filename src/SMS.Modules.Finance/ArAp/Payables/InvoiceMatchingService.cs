using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.ArAp.Payables;

public interface IInvoiceMatchingService
{
    Task<MatchDetailModel> MatchAsync(Guid invoiceUuid, string? matchType, ApActor actor, CancellationToken ct = default);
    Task<MatchDetailModel> GetAsync(Guid invoiceUuid, CancellationToken ct = default);
    Task<MatchDetailModel> OverrideAsync(Guid invoiceUuid, string? reason, ApActor actor, CancellationToken ct = default);
    Task<MatchDetailModel> ApproveAsync(Guid invoiceUuid, ApActor actor, CancellationToken ct = default);
    Task<MatchDetailModel> RejectAsync(Guid invoiceUuid, string? reason, ApActor actor, CancellationToken ct = default);
    Task<PaginatedResponse<MatchSummaryModel>> ListPendingAsync(Guid? vendorUuid, string? result, int? page, int? pageSize, CancellationToken ct = default, string? search = null);
    Task<IReadOnlyList<MatchSummaryModel>> ByPurchaseOrderAsync(Guid poUuid, CancellationToken ct = default);
}

/// <summary>
/// A48 C9 (tasks 291–332; D-7, D-CORE-5) — the per-line three-way / two-way match of an existing supplier invoice.
/// <list type="bullet">
/// <item>The invoice is the header: the result goes onto its A48 columns (MatchType, LineMatchResult, …) and its lines
/// to <see cref="InvoiceMatchLine"/>; re-matching replaces them and clears an earlier override. The existing
/// <c>MatchStatus</c> follows: Matched when every line is, otherwise Variance (both approvable, as before).</item>
/// <item>Tolerance: the vendor's active rule, else the organization's active default (seeded DEFAULT, D-16), else exact.</item>
/// <item>Duplicate vendor invoice number (BR-MT-007): refused (409) — also on invoice create/update.</item>
/// <item>Auto-approve (BR-MT-005): when the rule says so and every line is within tolerance, the invoice is approved
/// through the existing approval path right after the match commits (ledger, A44 posting, payment schedule).</item>
/// <item>Approve (BR-MT-008): the existing approval path, refused while a variance is not overridden. The existing
/// invoice approval enforces the same gate once an A48 match has judged the invoice (D-AP-4).</item>
/// </list>
/// One change at a time per invoice: the invoice row lock (<see cref="InvoiceRowLocks"/>) is taken first.
/// </summary>
internal sealed class InvoiceMatchingService : IInvoiceMatchingService
{
    internal const int OverrideReasonMaxLength = 500;

    private readonly FinanceDbContext   _fin;
    private readonly MatchSourceReader  _source;
    private readonly IInvoiceService    _invoices;
    private readonly ILogger<InvoiceMatchingService>? _log;
    private readonly ArApDefaultsSeeder? _seeder;

    public InvoiceMatchingService(FinanceDbContext fin, MatchSourceReader source, IInvoiceService invoices,
        ILogger<InvoiceMatchingService>? log = null, ArApDefaultsSeeder? seeder = null)
    {
        _fin      = fin;
        _source   = source;
        _invoices = invoices;
        _log      = log;
        _seeder   = seeder;
    }

    /// <summary>The DEFAULT tolerance rule (D-16) for an organization created before the seeds ran — before the match's transaction.</summary>
    private async Task EnsureDefaultsAsync(CancellationToken ct)
    {
        if (_seeder is null) return;
        if (await _fin.Set<MatchToleranceRule>().AnyAsync(r => r.OrganizationId == Org && r.IsDefault, ct)) return;
        try { await _seeder.EnsureAsync(Org, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _fin.ChangeTracker.Clear();
            _log?.LogWarning(ex, "The default tolerance rule could not be seeded; the match runs exact.");
        }
    }

    private Guid Org => _fin.TenantContext.OrganizationId;

    // ── Match ────────────────────────────────────────────────────────────────

    public async Task<MatchDetailModel> MatchAsync(Guid invoiceUuid, string? matchType, ApActor actor, CancellationToken ct = default)
    {
        var requested = MatchSourceReader.NormalizeType(matchType);
        await EnsureDefaultsAsync(ct);

        var (autoApprove, ruleName) = await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            if (!await InvoiceRowLocks.LockOneAsync(_fin, invoiceUuid)) throw new NotFoundException("Invoice", invoiceUuid);
            var invoice = await InvoiceRowLocks.Own(_fin).Include(i => i.Lines).Include(i => i.MatchLines)
                .FirstAsync(i => i.UUID == invoiceUuid, ct);

            if (!InvoiceMatchStatus.IsApprovable(invoice.MatchStatus))
                throw new ConflictException(
                    $"Invoice {invoice.InvoiceNumber} is {invoice.MatchStatus}; only a pending, matched or variance invoice can be matched.");

            await EnsureNotDuplicateAsync(invoice, ct);

            var source    = await _source.ReadAsync(invoice, requested, ct);
            var (rule, tolerance) = await ToleranceForAsync(invoice.SupplierId, ct);
            var twoWay    = source.MatchType == MatchStatuses.TwoWay;
            var eval      = MatchEvaluator.Evaluate(source.Lines, tolerance, twoWay);
            var now       = DateTime.UtcNow;

            _fin.Set<InvoiceMatchLine>().RemoveRange(invoice.MatchLines);
            invoice.MatchLines.Clear();
            foreach (var r in eval.Lines)
            {
                var noPo = r.Input.ReferenceQuantity is null && r.Input.PoLineUuid is null;
                invoice.MatchLines.Add(new InvoiceMatchLine
                {
                    OrganizationId   = Org,
                    InvoiceLineUuid  = r.Input.InvoiceLineUuid,
                    PoLineUuid       = r.Input.PoLineUuid,
                    GrnLineUuid      = r.Input.GrnLineUuid,
                    LineNo           = r.Input.LineNumber,
                    ItemDescription  = Trim(r.Input.ItemDescription, 500),
                    PoQuantity       = noPo ? null : r.Input.PoQuantity,
                    PoUnitPrice      = noPo ? null : r.Input.PoUnitPrice,
                    PoLineTotal      = noPo ? null : r.PoLineTotal,
                    GrnQuantity      = r.Input.GrnQuantity,
                    InvoiceQuantity  = r.Input.InvoiceQuantity,
                    InvoiceUnitPrice = r.Input.InvoiceUnitPrice,
                    InvoiceLineTotal = r.InvoiceLineTotal,
                    QuantityVariance = r.Input.ReferenceQuantity is null ? null : r.QuantityVariance,
                    PriceVariance    = noPo ? null : r.PriceVariance,
                    PriceVariancePercent = !noPo && r.Input.PoUnitPrice > 0m ? Math.Round(r.PriceVariance / r.Input.PoUnitPrice * 100m, 4) : null,
                    TotalVariance    = noPo ? null : r.TotalVariance,
                    LineStatus       = r.Status,
                    CreatedAt        = now
                });
            }

            invoice.MatchType             = source.MatchType;
            invoice.LineMatchResult       = eval.Status;
            invoice.WithinTolerance       = eval.WithinTolerance;
            invoice.MatchVariancePercent  = eval.VariancePercent;
            invoice.ToleranceRuleId       = rule?.Id;
            invoice.MatchedAt             = now;
            invoice.MatchedBy             = actor.UserId;
            invoice.MatchedByName         = actor.UserName;
            invoice.MatchOverrideReason   = null;
            invoice.MatchOverriddenAt     = null;
            invoice.MatchOverriddenBy     = null;
            invoice.MatchOverriddenByName = null;
            invoice.AutoApproved          = false;
            // The existing whole-invoice figures follow the per-line match (it refines them).
            invoice.MatchedPoValue  = eval.TotalExpected;
            if (eval.TotalGrn is { } grn) invoice.MatchedGrnValue = grn;
            invoice.VarianceAmount  = eval.VarianceAmount;
            invoice.MatchStatus     = eval.Status == MatchStatuses.Matched ? InvoiceMatchStatus.Matched : InvoiceMatchStatus.Variance;
            invoice.ModifiedBy      = actor.UserId ?? invoice.ModifiedBy;
            invoice.ModifiedDate    = now;

            await _fin.SaveChangesAsync(ct);
            return (rule?.AutoApproveWithin == true && eval.Status == MatchStatuses.Matched, rule?.Name);
        });

        string? autoMessage = null;
        if (autoApprove)
        {
            try
            {
                if (await _invoices.ApproveAsync(invoiceUuid, null, actor.UserId ?? 0))
                {
                    await _fin.Invoices.Where(i => i.UUID == invoiceUuid && i.OrganizationId == Org)
                        .SetAndSaveAsync(_fin, i => i.AutoApproved = true, ct);
                }
            }
            catch (Exception ex) when (ex is BadRequestException or ConflictException or UnprocessableEntityException)
            {
                // The match stands; approval needs a person (e.g. a foreign invoice with no rate).
                autoMessage = $"Matched within tolerance rule {ruleName}, but it could not be approved automatically: {ex.Message}";
                _log?.LogInformation("Auto-approval of invoice {InvoiceUuid} was refused: {Message}", invoiceUuid, ex.Message);
            }
        }

        _fin.ChangeTracker.Clear();
        var detail = await GetAsync(invoiceUuid, ct);
        return detail with { AutoApproveMessage = autoMessage };
    }

    // ── Override / approve ───────────────────────────────────────────────────

    public async Task<MatchDetailModel> OverrideAsync(Guid invoiceUuid, string? reason, ApActor actor, CancellationToken ct = default)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why)) throw new BadRequestException("An override needs a reason.");
        if (why.Length > OverrideReasonMaxLength) throw new BadRequestException($"The reason can be at most {OverrideReasonMaxLength} characters.");

        await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            if (!await InvoiceRowLocks.LockOneAsync(_fin, invoiceUuid)) throw new NotFoundException("Invoice", invoiceUuid);
            var invoice = await InvoiceRowLocks.Own(_fin).Include(i => i.MatchLines).FirstAsync(i => i.UUID == invoiceUuid, ct);

            if (!InvoiceMatchStatus.IsApprovable(invoice.MatchStatus)
                || invoice.LineMatchResult is not (MatchStatuses.PartialMatch or MatchStatuses.Mismatch)
                || invoice.MatchOverrideReason is not null)
                throw new ConflictException("Only an invoice with a match variance can be overridden.");

            var now = DateTime.UtcNow;
            invoice.MatchOverrideReason   = why;
            invoice.MatchOverriddenAt     = now;
            invoice.MatchOverriddenBy     = actor.UserId;
            invoice.MatchOverriddenByName = actor.UserName;
            invoice.ModifiedBy            = actor.UserId ?? invoice.ModifiedBy;
            invoice.ModifiedDate          = now;
            // Task 324: every line that did not match is marked Overridden.
            foreach (var line in invoice.MatchLines.Where(l => l.LineStatus != MatchStatuses.Matched))
            {
                line.Note       = Trim($"{line.LineStatus}: {why}", 500);
                line.LineStatus = MatchStatuses.Overridden;
            }
            await _fin.SaveChangesAsync(ct);
            return true;
        });

        _fin.ChangeTracker.Clear();
        return await GetAsync(invoiceUuid, ct);
    }

    public async Task<MatchDetailModel> ApproveAsync(Guid invoiceUuid, ApActor actor, CancellationToken ct = default)
    {
        var invoice = await InvoiceRowLocks.Own(_fin).AsNoTracking().FirstOrDefaultAsync(i => i.UUID == invoiceUuid, ct)
            ?? throw new NotFoundException("Invoice", invoiceUuid);

        if (InvoiceMatchStatus.IsApprovable(invoice.MatchStatus))
        {
            if (invoice.LineMatchResult is null)
                throw new ConflictException($"Invoice {invoice.InvoiceNumber} has not been matched yet; run the match before approving it here.");
            if (MatchGate.Blocks(invoice.LineMatchResult, invoice.MatchOverrideReason))
                throw new ConflictException(MatchGate.Text(invoice.InvoiceNumber));
        }

        // The existing approval path: ledger, A44 posting, PO quantities, payment schedule (D-7). Its own checks
        // (already approved, rejected, reversed, exchange rate) answer as they always have; it re-checks the gate under
        // the invoice lock.
        if (!await _invoices.ApproveAsync(invoiceUuid, null, actor.UserId ?? 0)) throw new NotFoundException("Invoice", invoiceUuid);

        _fin.ChangeTracker.Clear();
        return await GetAsync(invoiceUuid, ct);
    }

    // ── Reads ────────────────────────────────────────────────────────────────

    public async Task<MatchDetailModel> GetAsync(Guid invoiceUuid, CancellationToken ct = default)
    {
        var i = await InvoiceRowLocks.Own(_fin).AsNoTracking().Include(x => x.MatchLines).Include(x => x.ToleranceRule)
            .FirstOrDefaultAsync(x => x.UUID == invoiceUuid, ct) ?? throw new NotFoundException("Invoice", invoiceUuid);

        var lines = i.MatchLines.OrderBy(l => l.LineNo).ThenBy(l => l.Id).Select(l => new MatchLineModel(
            l.LineNo, l.ItemDescription, l.PoLineUuid, l.GrnLineUuid, l.PoQuantity, l.PoUnitPrice, l.PoLineTotal, l.GrnQuantity,
            l.InvoiceQuantity, l.InvoiceUnitPrice, l.InvoiceLineTotal, l.QuantityVariance, l.PriceVariance, l.PriceVariancePercent,
            l.TotalVariance, l.LineStatus)).ToList();

        return new MatchDetailModel(
            i.UUID, i.InvoiceNumber, i.SupplierInvoiceNo, i.SupplierId, i.SupplierName, i.PoUuid, i.PoNumber, i.GrnUuid, i.GrnNumber,
            i.MatchStatus, i.MatchType, i.LineMatchResult, i.WithinTolerance, i.MatchVariancePercent, i.VarianceAmount,
            i.ToleranceRule is null ? null : new ToleranceRuleRef(i.ToleranceRule.Uuid, i.ToleranceRule.Name), i.AutoApproved,
            i.MatchedAt, i.MatchedByName, i.MatchOverrideReason, i.MatchOverriddenAt, i.MatchOverriddenByName,
            i.Currency, i.Subtotal, i.TotalAmount, lines);
    }

    /// <summary>
    /// Invoices waiting for the match or its outcome: not approved, rejected or reversed, raised against a purchase order.
    /// <paramref name="result"/>: Matched / PartialMatch / Mismatch / Overridden / NotMatched.
    /// </summary>
    /// <summary>FE-6 — reject from the matching page: the existing supplier-invoice rejection (reason kept in Notes, ≤ 300).</summary>
    public async Task<MatchDetailModel> RejectAsync(Guid invoiceUuid, string? reason, ApActor actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new BadRequestException("Say why the invoice is being rejected.");
        if (!await _invoices.RejectAsync(invoiceUuid, reason, actor.UserId ?? 0)) throw new NotFoundException("Invoice", invoiceUuid);
        _fin.ChangeTracker.Clear();
        return await GetAsync(invoiceUuid, ct);
    }

    public async Task<PaginatedResponse<MatchSummaryModel>> ListPendingAsync(Guid? vendorUuid, string? result, int? page, int? pageSize, CancellationToken ct = default, string? search = null)
    {
        var (p, size) = ApSupport.Paging(page, pageSize);
        var q = InvoiceRowLocks.Own(_fin).AsNoTracking()
            .Where(i => i.PoUuid != null
                     && (i.MatchStatus == InvoiceMatchStatus.Pending || i.MatchStatus == InvoiceMatchStatus.Matched || i.MatchStatus == InvoiceMatchStatus.Variance));
        if (vendorUuid is { } v) q = q.Where(i => i.SupplierId == v);
        // FE-7 — our number, the supplier's number, the PO number or the supplier's name (contains, case-insensitive).
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            q = q.Where(i => i.InvoiceNumber.ToLower().Contains(s)
                          || (i.SupplierInvoiceNo != null && i.SupplierInvoiceNo.ToLower().Contains(s))
                          || (i.PoNumber != null && i.PoNumber.ToLower().Contains(s))
                          || i.SupplierName.ToLower().Contains(s));
        }

        if (!string.IsNullOrWhiteSpace(result))
        {
            string[] allowed = [MatchStatuses.Matched, MatchStatuses.PartialMatch, MatchStatuses.Mismatch, MatchStatuses.Overridden, "NotMatched"];
            var r = ArApEnums.Canonical(result, allowed) ?? throw new BadRequestException(ArApEnums.UnknownText("result", result, allowed));
            q = r switch
            {
                "NotMatched"              => q.Where(i => i.LineMatchResult == null),
                MatchStatuses.Overridden  => q.Where(i => i.MatchOverrideReason != null),
                MatchStatuses.Matched     => q.Where(i => i.LineMatchResult == MatchStatuses.Matched),
                _                         => q.Where(i => i.LineMatchResult == r && i.MatchOverrideReason == null)
            };
        }

        var total = await q.CountAsync(ct);
        var rows  = await q.OrderBy(i => i.InvoiceDate).ThenBy(i => i.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return ApSupport.Page(rows.Select(Summary).ToList(), total, p, size);
    }

    public async Task<IReadOnlyList<MatchSummaryModel>> ByPurchaseOrderAsync(Guid poUuid, CancellationToken ct = default)
    {
        var rows = await InvoiceRowLocks.Own(_fin).AsNoTracking().Where(i => i.PoUuid == poUuid)
            .OrderBy(i => i.InvoiceDate).ThenBy(i => i.Id).ToListAsync(ct);
        return rows.Select(Summary).ToList();
    }

    private static MatchSummaryModel Summary(Invoice i) => new(
        i.UUID, i.InvoiceNumber, i.SupplierInvoiceNo, i.SupplierId, i.SupplierName, i.PoUuid, i.PoNumber, i.MatchStatus,
        i.MatchType, i.LineMatchResult, i.WithinTolerance, i.MatchVariancePercent, i.VarianceAmount, i.MatchOverrideReason is not null,
        i.Currency, i.TotalAmount, DateOnly.FromDateTime(i.InvoiceDate), i.MatchedAt);

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>The vendor's active rule, else the organization's active default; none → exact match.</summary>
    internal Task<(MatchToleranceRule? Rule, MatchTolerance Tolerance)> ToleranceForAsync(Guid vendorUuid, CancellationToken ct) =>
        MatchRecheck.ToleranceForAsync(_fin, Org, vendorUuid, ct);

    /// <summary>BR-MT-007 at match time too: another live invoice of this vendor under the same number.</summary>
    private async Task EnsureNotDuplicateAsync(Invoice invoice, CancellationToken ct)
    {
        var no = invoice.SupplierInvoiceNo?.Trim();
        if (string.IsNullOrEmpty(no)) return;
        var upper = no.ToUpperInvariant();
        var clash = await InvoiceRowLocks.Own(_fin).AsNoTracking()
            .Where(i => i.SupplierId == invoice.SupplierId && i.Id != invoice.Id && i.SupplierInvoiceNo != null
                     && i.SupplierInvoiceNo.Trim().ToUpper() == upper
                     && i.MatchStatus != InvoiceMatchStatus.Rejected && i.MatchStatus != InvoiceMatchStatus.Reversed)
            .Select(i => i.InvoiceNumber).FirstOrDefaultAsync(ct);
        if (clash is not null)
            throw new ConflictException($"Supplier invoice number {no} is already recorded for {invoice.SupplierName} (invoice {clash}).");
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}

/// <summary>BR-MT-008 (D-AP-4) — the approval gate an A48 match puts on an invoice. Shared with the existing approval.</summary>
internal static class MatchGate
{
    /// <summary>A judged variance (PartialMatch / Mismatch) nobody has overridden. Never-matched invoices pass (the old path).</summary>
    public static bool Blocks(string? lineMatchResult, string? overrideReason) =>
        lineMatchResult is MatchStatuses.PartialMatch or MatchStatuses.Mismatch && overrideReason is null;

    public static string Text(string invoiceNumber) => $"Invoice {invoiceNumber} has a match variance; override it before approving.";
}

internal static class InvoiceQueryExtensions
{
    /// <summary>Loads the rows, applies the change, saves.</summary>
    internal static async Task SetAndSaveAsync(this IQueryable<Invoice> query, FinanceDbContext db, Action<Invoice> change, CancellationToken ct)
    {
        foreach (var invoice in await query.ToListAsync(ct)) change(invoice);
        await db.SaveChangesAsync(ct);
    }
}

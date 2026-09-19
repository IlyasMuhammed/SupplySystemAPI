using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Settlement;

/// <summary>
/// Comparing what a carrier billed against what it was expected to bill.
/// <para>
/// The three figures have been gathered deliberately: the quote in T-47, what the carrier agreed at
/// booking in T-52, and the bill in T-53, tied to movements in T-54. This is what they were for.
/// </para>
/// <para>
/// <b>It names the reason, not just the number.</b> "The bill is 140 out" is not something anybody
/// can act on; "the carrier billed 14 kg where we made it 12.5" is, and it is the single commonest
/// cause of a freight bill being wrong.
/// </para>
/// </summary>
public interface IThreeWayMatchService
{
    /// <summary>
    /// Compares a bill against what was expected, records the result on each accrual, and moves the
    /// bill to MATCHED or DISPUTED.
    /// </summary>
    Task<ThreeWayMatchModel?> MatchAsync(Guid invoiceUuid, int userId, CancellationToken ct = default);

    /// <summary>The same comparison without recording anything.</summary>
    Task<ThreeWayMatchModel?> PreviewAsync(Guid invoiceUuid, CancellationToken ct = default);
}

internal sealed class ThreeWayMatchService : IThreeWayMatchService
{
    private const string WithinTolerance = "WITHIN_TOLERANCE";
    private const string Overcharged     = "OVERCHARGED";
    private const string Undercharged    = "UNDERCHARGED";
    private const string NotAccrued      = "NOT_ACCRUED";

    /// <summary>
    /// A percentage by default and no absolute figure, because a percentage is the only tolerance
    /// that means the same thing in every currency. An absolute one can be configured on top where
    /// a deployment wants small bills waved through.
    /// </summary>
    private const decimal DefaultTolerancePercent = 2m;

    private readonly LogisticsDbContext _db;
    private readonly IConfiguration     _config;

    public ThreeWayMatchService(LogisticsDbContext db, IConfiguration config)
    {
        _db     = db;
        _config = config;
    }

    private decimal TolerancePercent =>
        _config.GetValue("Logistics:Settlement:TolerancePercent", DefaultTolerancePercent);

    private decimal ToleranceAmount =>
        _config.GetValue("Logistics:Settlement:ToleranceAmount", 0m);

    public Task<ThreeWayMatchModel?> PreviewAsync(Guid invoiceUuid, CancellationToken ct = default) =>
        BuildAsync(invoiceUuid, recordAs: null, ct);

    public Task<ThreeWayMatchModel?> MatchAsync(Guid invoiceUuid, int userId, CancellationToken ct = default) =>
        BuildAsync(invoiceUuid, recordAs: userId, ct);

    private async Task<ThreeWayMatchModel?> BuildAsync(Guid invoiceUuid, int? recordAs, CancellationToken ct)
    {
        var query = _db.CarrierInvoices
            .Include(i => i.Carrier)
            .Include(i => i.Lines);

        var invoice = recordAs is null
            ? await query.AsNoTracking().FirstOrDefaultAsync(i => i.UUID == invoiceUuid && !i.IsDelete, ct)
            : await query.FirstOrDefaultAsync(i => i.UUID == invoiceUuid && !i.IsDelete, ct);

        if (invoice is null) return null;

        if (invoice.Status == LogisticsCode.Of(CarrierInvoiceStatus.Cancelled))
            throw new ConflictException(
                $"Invoice '{invoice.InvoiceNumber}' was withdrawn. There is nothing to compare.");

        var model = new ThreeWayMatchModel
        {
            InvoiceUuid      = invoice.UUID,
            InvoiceNumber    = invoice.InvoiceNumber,
            CarrierName      = invoice.CarrierName ?? invoice.Carrier?.Name ?? string.Empty,
            Currency         = invoice.Currency,
            InvoiceTotal     = invoice.TotalAmount,
            TolerancePercent = TolerancePercent,
            ToleranceAmount  = ToleranceAmount
        };

        var matchedLines = invoice.Lines.Where(l => l.MatchedConsignmentId is not null).ToList();

        var loose = invoice.Lines
            .Where(l => l.MatchStatus == LogisticsCode.Of(InvoiceLineMatchStatus.Unmatched)
                     || l.MatchStatus == LogisticsCode.Of(InvoiceLineMatchStatus.Ambiguous))
            .ToList();

        model.UnmatchedLines  = loose.Count;
        model.UnmatchedAmount = loose.Sum(l => l.Amount);

        if (model.UnmatchedLines > 0)
            model.Warnings.Add(
                $"{model.UnmatchedLines} line(s) worth {invoice.Currency} {model.UnmatchedAmount:N2} "
              + "are not tied to a movement, so they are not compared against anything at all. "
              + "Match them before settling this bill.");

        var consignmentIds = matchedLines.Select(l => l.MatchedConsignmentId!.Value).Distinct().ToList();

        var accruals = await AccrualsAsync(consignmentIds, recordAs is not null, ct);
        var quotes   = await QuoteChargeCodesAsync(consignmentIds, ct);

        var consignments = await _db.Consignments.AsNoTracking()
            .Where(c => consignmentIds.Contains(c.Id))
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        foreach (var consignment in consignments.OrderBy(c => c.ConsignmentNumber, StringComparer.Ordinal))
        {
            var lines   = matchedLines.Where(l => l.MatchedConsignmentId == consignment.Id).ToList();
            var accrual = accruals.GetValueOrDefault(consignment.Id);

            var compared = Compare(consignment, lines, accrual, invoice.Currency,
                                   quotes.GetValueOrDefault(consignment.Id) ?? []);

            model.Consignments.Add(compared);

            if (recordAs is { } userId && accrual is not null)
                Record(accrual, invoice, compared, now, userId);
        }

        Summarize(model);

        if (recordAs is { } who)
        {
            invoice.Status       = model.IsClean
                ? LogisticsCode.Of(CarrierInvoiceStatus.Matched)
                : LogisticsCode.Of(CarrierInvoiceStatus.Disputed);
            invoice.ModifiedBy   = who;
            invoice.ModifiedDate = now;

            await _db.SaveChangesAsync(ct);
        }

        model.Status = invoice.Status;

        return model;
    }

    // ── The comparison ────────────────────────────────────────────────────────

    /// <summary>
    /// What is expected is what the carrier agreed at booking where it said anything, and the quote
    /// otherwise — the same precedence T-47 used for pricing, and for the same reason: the carrier's
    /// own word beats our estimate of it.
    /// </summary>
    private ConsignmentMatchModel Compare(
        Consignment consignment, List<CarrierInvoiceLine> lines, FreightAccrual? accrual,
        string currency, IReadOnlyCollection<string> quotedCodes)
    {
        var invoiced = lines.Sum(l => l.Amount);

        var model = new ConsignmentMatchModel
        {
            ConsignmentUuid   = consignment.UUID,
            ConsignmentNumber = consignment.ConsignmentNumber,
            MasterAwb         = consignment.MasterAwb,
            InvoicedAmount    = invoiced,
            Currency          = currency,
            QuotedAmount      = accrual?.AccruedAmount ?? consignment.FreightCost,
            BookedAmount      = accrual?.BookedAmount
        };

        if (accrual is null)
        {
            // Nothing was ever accrued for this movement, so there is nothing to compare the bill
            // against. Said plainly rather than treated as agreement — an unaccrued charge passing
            // silently is exactly how an unexpected bill gets paid.
            model.Outcome      = NotAccrued;
            model.VarianceNote = "Nothing was accrued for this movement, so the bill cannot be "
                               + "checked against anything. Price and accrue it, then match again.";
            return model;
        }

        // A bill in one currency against an accrual in another compares nothing. There is no
        // exchange rate in this module (F42), so it is refused rather than mis-stated.
        if (!string.Equals(accrual.Currency, currency, StringComparison.OrdinalIgnoreCase))
        {
            model.Outcome      = NotAccrued;
            model.VarianceNote = $"This movement was accrued in {accrual.Currency} and billed in "
                               + $"{currency}. There is no exchange rate here, so the two cannot be compared.";
            return model;
        }

        var expected = accrual.BookedAmount is { } booked
                    && string.Equals(accrual.BookedCurrency, currency, StringComparison.OrdinalIgnoreCase)
            ? booked
            : accrual.AccruedAmount;

        model.ExpectedAmount = expected;
        model.ExpectedBasis  = expected == accrual.BookedAmount ? "BOOKED" : "QUOTED";

        var variance = invoiced - expected;

        model.VarianceAmount  = variance;
        model.VariancePercent = expected == 0m ? null : Math.Round(variance / Math.Abs(expected) * 100m, 2);

        // Whichever tolerance is the more generous applies — the percentage on a large bill, the
        // absolute on a small one where a percentage would be pennies.
        var allowed = Math.Max(Math.Abs(expected) * TolerancePercent / 100m, ToleranceAmount);

        if (Math.Abs(variance) <= allowed)
        {
            model.Outcome = WithinTolerance;
            return model;
        }

        model.Outcome = variance > 0 ? Overcharged : Undercharged;

        var (reason, note) = Explain(consignment, lines, quotedCodes, variance, currency);

        model.VarianceReason = LogisticsCode.Of(reason);
        model.VarianceNote   = note;

        return model;
    }

    /// <summary>
    /// Why the carrier billed something else. Weight first: it is the commonest cause by a wide
    /// margin, and T-44 stored our figure precisely so it could be put beside theirs.
    /// </summary>
    private static (VarianceReason Reason, string Note) Explain(
        Consignment consignment, List<CarrierInvoiceLine> lines,
        IReadOnlyCollection<string> quotedCodes, decimal variance, string currency)
    {
        var billedWeight = lines.Where(l => l.ChargeableWeightKg is not null)
                                .Select(l => l.ChargeableWeightKg!.Value)
                                .DefaultIfEmpty(0m)
                                .Max();

        if (billedWeight > 0m && consignment.RatedChargeableWeightKg is { } ours && billedWeight != ours)
            return (VarianceReason.Weight,
                $"The carrier billed on {billedWeight:0.###} kg where we made it {ours:0.###} kg. "
              + $"That accounts for a {currency} {Math.Abs(variance):N2} difference on this movement.");

        var extraCodes = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.ChargeCode))
            .Select(l => l.ChargeCode!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(code => !quotedCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        if (extraCodes.Count > 0)
            return (VarianceReason.Surcharge,
                $"The bill carries {string.Join(", ", extraCodes)}, which the quote did not.");

        var billedService = lines.Select(l => l.ServiceCode)
                                 .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

        if (!string.IsNullOrWhiteSpace(billedService)
         && !string.IsNullOrWhiteSpace(consignment.RatedServiceCode)
         && !string.Equals(billedService, consignment.RatedServiceCode, StringComparison.OrdinalIgnoreCase))
            return (VarianceReason.Service,
                $"The bill is for '{billedService}' and the quote was for "
              + $"'{consignment.RatedServiceCode}'.");

        // Nothing on the bill explains it. Saying so is more use than a guessed reason, which would
        // be argued with the carrier and lost.
        return (VarianceReason.Unexplained,
            $"{currency} {Math.Abs(variance):N2} {(variance > 0 ? "more" : "less")} than expected, and "
          + "nothing on the bill says why. Query it with the carrier.");
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    private static void Record(
        FreightAccrual accrual, CarrierInvoice invoice, ConsignmentMatchModel compared,
        DateTime now, int userId)
    {
        accrual.InvoicedAmount   = compared.InvoicedAmount;
        accrual.VarianceAmount   = compared.VarianceAmount;
        accrual.VarianceReason   = compared.VarianceReason;
        accrual.VarianceNote     = compared.VarianceNote;
        accrual.CarrierInvoiceId = invoice.Id;
        accrual.MatchedAt        = now;
        accrual.ModifiedBy       = userId;
        accrual.ModifiedDate     = now;

        // MATCHED, not CLOSED. The liability stays on the books until the bill is approved, which
        // is T-56 — a disputed charge is still owed until somebody decides it is not.
        if (accrual.Status == LogisticsCode.Of(FreightAccrualStatus.Accrued))
            accrual.Status = LogisticsCode.Of(FreightAccrualStatus.Matched);
    }

    private static void Summarize(ThreeWayMatchModel model)
    {
        model.ConsignmentCount = model.Consignments.Count;
        model.WithinTolerance  = model.Consignments.Count(c => c.Outcome == WithinTolerance);
        model.Overcharged      = model.Consignments.Count(c => c.Outcome == Overcharged);
        model.Undercharged     = model.Consignments.Count(c => c.Outcome == Undercharged);
        model.NotAccrued       = model.Consignments.Count(c => c.Outcome == NotAccrued);

        model.ExpectedTotal = model.Consignments.Sum(c => c.ExpectedAmount ?? 0m);
        model.VarianceTotal = model.Consignments.Sum(c => c.VarianceAmount ?? 0m);

        model.IsClean = model.UnmatchedLines == 0
                     && model.Overcharged  == 0
                     && model.Undercharged == 0
                     && model.NotAccrued   == 0
                     && model.ConsignmentCount > 0;

        if (model.Undercharged > 0)
            model.Warnings.Add(
                $"{model.Undercharged} movement(s) were billed for less than expected. That is "
              + "usually a second bill still to come rather than a saving.");

        if (model.NotAccrued > 0)
            model.Warnings.Add(
                $"{model.NotAccrued} movement(s) on this bill were never accrued, so nothing checks "
              + "what is being charged for them.");

        if (model.ConsignmentCount == 0)
            model.Warnings.Add("No line on this bill is tied to a movement, so there is nothing to compare.");
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private async Task<Dictionary<int, FreightAccrual>> AccrualsAsync(
        List<int> consignmentIds, bool tracked, CancellationToken ct)
    {
        if (consignmentIds.Count == 0) return [];

        var q = _db.FreightAccruals.Where(a => !a.IsDelete && consignmentIds.Contains(a.ConsignmentId));

        if (!tracked) q = q.AsNoTracking();

        return await q.ToDictionaryAsync(a => a.ConsignmentId, ct);
    }

    /// <summary>The charge codes our own quote carried, so the bill's extras can be named.</summary>
    private async Task<Dictionary<int, List<string>>> QuoteChargeCodesAsync(
        List<int> consignmentIds, CancellationToken ct)
    {
        if (consignmentIds.Count == 0) return [];

        var charges = await _db.ConsignmentCharges.AsNoTracking()
            .Where(c => consignmentIds.Contains(c.ConsignmentId))
            .Select(c => new { c.ConsignmentId, c.Code })
            .ToListAsync(ct);

        return charges
            .GroupBy(c => c.ConsignmentId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Code).Distinct().ToList());
    }
}

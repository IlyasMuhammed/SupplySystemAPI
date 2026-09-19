using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Visibility;

/// <summary>
/// How each carrier has actually performed, from figures this module already stores.
/// <para>
/// <b>Nothing here is new data.</b> Every number comes from something an earlier task recorded
/// because it had to: ETAs and arrivals from tracking (T-39), exceptions from T-60, proofs from
/// T-61, and variance from the three-way match (T-55), which already decided what counts as within
/// tolerance so this does not decide it a second time and drift.
/// </para>
/// </summary>
public interface ICarrierScorecardService
{
    Task<CarrierScorecardModel> GetAsync(ScorecardFilter filter, CancellationToken ct = default);
}

internal sealed class CarrierScorecardService : ICarrierScorecardService
{
    /// <summary>
    /// Below this, a percentage is noise. It is still shown — hiding it would be its own kind of
    /// lie — but it is shown with the count that produced it and a warning.
    /// </summary>
    internal const int MinimumForRate = 10;

    internal const int DefaultWindowDays = 90;

    internal static readonly IReadOnlySet<string> Sorts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "VOLUME", "ON_TIME", "EXCEPTIONS", "BILLING"
        };

    private static readonly string Delivered  = LogisticsCode.Of(ShipmentStatus.Delivered);
    private static readonly string Returned   = LogisticsCode.Of(ShipmentStatus.ReturnedToOrigin);
    private static readonly string Lost       = LogisticsCode.Of(ShipmentStatus.Lost);
    private static readonly string Cancelled  = LogisticsCode.Of(ShipmentStatus.Cancelled);
    private static readonly string Critical   = LogisticsCode.Of(ExceptionSeverity.Critical);
    private static readonly string Withdrawn  = LogisticsCode.Of(ExceptionStatus.Withdrawn);
    private static readonly string Open       = LogisticsCode.Of(ExceptionStatus.Open);
    private static readonly string Waiting    = LogisticsCode.Of(ExceptionStatus.Waiting);

    private readonly LogisticsDbContext _db;
    public CarrierScorecardService(LogisticsDbContext db) => _db = db;

    public async Task<CarrierScorecardModel> GetAsync(
        ScorecardFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var to   = filter.To   ?? DateTime.UtcNow;
        var from = filter.From ?? to.AddDays(-DefaultWindowDays);

        if (from >= to)
            throw new BadRequestException("The window starts after it ends.");

        var sort = (filter.SortBy ?? "VOLUME").Trim().ToUpperInvariant();

        if (!Sorts.Contains(sort))
            throw new BadRequestException(
                $"'{filter.SortBy}' is not something to sort by. Valid values: {string.Join(", ", Sorts)}.");

        // The cohort is what the carrier collected in the window, not what it delivered in it. A
        // consignment must not move between periods as it progresses, or last month's figures change
        // every time somebody looks at them.
        var consignments = await _db.Consignments.AsNoTracking()
            .Include(c => c.Carrier)
            .Where(c => !c.IsDelete
                     && c.CarrierId != null
                     && c.Status != Cancelled
                     && c.ActualDispatchAt != null
                     && c.ActualDispatchAt >= from
                     && c.ActualDispatchAt <= to)
            .ToListAsync(ct);

        if (filter.CarrierUuid is { } wanted)
            consignments = [.. consignments.Where(c => c.Carrier?.UUID == wanted)];

        var scorecard = new CarrierScorecardModel { From = from, To = to, SortedBy = sort };

        if (consignments.Count == 0)
        {
            scorecard.Warnings.Add(
                "Nothing was collected by a carrier in this window, so there is nothing to compare.");
            return scorecard;
        }

        var ids = consignments.Select(c => c.Id).ToList();

        var exceptions = await _db.DeliveryExceptions.AsNoTracking()
            .Where(e => !e.IsDelete && e.Status != Withdrawn && ids.Contains(e.ConsignmentId))
            .ToListAsync(ct);

        var proofs = await _db.DeliveryProofs.AsNoTracking()
            .Include(p => p.Files.Where(f => !f.IsDelete))
            .Where(p => !p.IsDelete && ids.Contains(p.ConsignmentId))
            .ToListAsync(ct);

        var accruals = filter.IncludeBilling
            ? await _db.FreightAccruals.AsNoTracking()
                .Where(a => !a.IsDelete && ids.Contains(a.ConsignmentId))
                .ToListAsync(ct)
            : [];

        var byException = exceptions.ToLookup(e => e.ConsignmentId);
        var byProof     = proofs.ToLookup(p => p.ConsignmentId);
        var byAccrual   = accruals.ToLookup(a => a.ConsignmentId);

        scorecard.Carriers =
        [
            .. consignments
                .GroupBy(c => c.CarrierId!.Value)
                .Select(g => Score(g, byException, byProof, byAccrual, filter.IncludeBilling))
        ];

        scorecard.Carriers = [.. Sorted(scorecard.Carriers, sort)];

        if (!filter.IncludeBilling)
            scorecard.Warnings.Add(
                "Billing accuracy is not shown. It needs FREIGHT_INVOICE_VIEW — what the company "
              + "pays to move goods is a separate question from how well they moved.");

        if (scorecard.Carriers.Count == 1)
            scorecard.Warnings.Add(
                "One carrier. These are its figures, not a comparison — there is nothing to compare "
              + "them against.");

        return scorecard;
    }

    // ── One carrier ───────────────────────────────────────────────────────────

    private static CarrierScoreModel Score(
        IGrouping<int, Consignment> group,
        ILookup<int, DeliveryException> byException,
        ILookup<int, DeliveryProof> byProof,
        ILookup<int, FreightAccrual> byAccrual,
        bool includeBilling)
    {
        var all       = group.ToList();
        var first     = all[0];
        var delivered = all.Where(c => c.Status == Delivered).ToList();

        var score = new CarrierScoreModel
        {
            CarrierUuid      = first.Carrier?.UUID,
            CarrierName      = first.CarrierName ?? first.Carrier?.Name ?? "Unnamed carrier",
            Consignments     = all.Count,
            Delivered        = delivered.Count,
            ReturnedToOrigin = all.Count(c => c.Status == Returned),
            Lost             = all.Count(c => c.Status == Lost),
            StuckNow         = all.Count(c => c.StuckSince != null)
        };

        score.StillMoving = all.Count - score.Delivered - score.ReturnedToOrigin - score.Lost;

        ScoreTimeliness(score, delivered);
        ScoreExceptions(score, all, byException);
        ScoreEvidence(score, delivered, byProof);

        if (includeBilling) score.Billing = Bill(all, byAccrual);

        AddWarnings(score);

        return score;
    }

    private static void ScoreTimeliness(CarrierScoreModel score, List<Consignment> delivered)
    {
        // Only what can actually be judged. A consignment with no ETA is neither on time nor late,
        // and counting it either way would be inventing the promise it was measured against.
        var judgeable = delivered
            .Where(c => c.Eta is not null && c.ActualArrivalAt is not null)
            .ToList();

        score.Judgeable    = judgeable.Count;
        score.NotJudgeable = delivered.Count - judgeable.Count;

        if (judgeable.Count == 0) return;

        var late = judgeable.Where(c => c.ActualArrivalAt > c.Eta).ToList();

        score.Late          = late.Count;
        score.OnTime        = judgeable.Count - late.Count;
        score.OnTimePercent = Percent(score.OnTime, judgeable.Count);

        if (late.Count > 0)
            score.AverageDaysLate = Math.Round(
                late.Average(c => (c.ActualArrivalAt!.Value - c.Eta!.Value).TotalDays), 1);
    }

    private static void ScoreExceptions(
        CarrierScoreModel score, List<Consignment> all, ILookup<int, DeliveryException> byException)
    {
        var raised = all.SelectMany(c => byException[c.Id]).ToList();

        score.Exceptions         = raised.Count;
        score.CriticalExceptions = raised.Count(e => e.Severity == Critical);
        score.StillOpen          = raised.Count(e => e.Status == Open || e.Status == Waiting);
        score.ExceptionsPer100   = Math.Round(raised.Count * 100d / all.Count, 1);

        var resolved = raised.Where(e => e.ResolvedAt is not null).ToList();

        if (resolved.Count > 0)
            score.AverageHoursToResolve = Math.Round(
                resolved.Average(e => (e.ResolvedAt!.Value - e.OccurredAt).TotalHours), 1);
    }

    private static void ScoreEvidence(
        CarrierScoreModel score, List<Consignment> delivered, ILookup<int, DeliveryProof> byProof)
    {
        // The same bar T-61 sets: a name and an artefact. A proof with one of the two is recorded
        // and is not evidence, so it does not count here either.
        score.Defensible = delivered.Count(c =>
        {
            var forThis = byProof[c.Id].ToList();

            return forThis.Count > 0
                && forThis.All(p => !string.IsNullOrWhiteSpace(p.ReceivedBy)
                                 && p.Files.Any(f => !f.IsDelete));
        });

        score.ProofCoveragePercent = Percent(score.Defensible, delivered.Count);
    }

    private static CarrierBillingModel Bill(List<Consignment> all, ILookup<int, FreightAccrual> byAccrual)
    {
        var accruals = all.SelectMany(c => byAccrual[c.Id]).ToList();
        var invoiced = accruals.Where(a => a.InvoicedAmount is not null).ToList();

        var billing = new CarrierBillingModel
        {
            Invoiced       = invoiced.Count,
            NotYetInvoiced = all.Count - invoiced.Count,
            // VarianceReason is null within tolerance, which T-55 already worked out. Re-deciding
            // what counts as close enough here would give two answers to one question.
            Overcharged    = invoiced.Count(a => a.VarianceReason is not null && a.VarianceAmount > 0),
            Undercharged   = invoiced.Count(a => a.VarianceReason is not null && a.VarianceAmount < 0)
        };

        var withinTolerance = invoiced.Count(a => a.VarianceReason is null);

        billing.AccuracyPercent = Percent(withinTolerance, invoiced.Count);

        // Never summed across currencies. Adding rupees to dirhams produces a number that looks
        // like money and is not.
        billing.Variance =
        [
            .. invoiced
                .Where(a => a.VarianceAmount is not null)
                .GroupBy(a => a.Currency)
                .Select(g => new VarianceByCurrencyModel
                {
                    Currency     = g.Key,
                    Variance     = g.Sum(a => a.VarianceAmount!.Value),
                    Consignments = g.Count()
                })
                .OrderByDescending(v => Math.Abs(v.Variance))
        ];

        return billing;
    }

    // ── What the figures do not say for themselves ────────────────────────────

    private static void AddWarnings(CarrierScoreModel score)
    {
        if (score.Consignments < MinimumForRate)
            score.Warnings.Add(
                $"Worked out from {score.Consignments} consignment(s). Too few to compare this "
              + "carrier against another on.");

        if (score.NotJudgeable > 0)
            score.Warnings.Add(
                $"{score.NotJudgeable} delivered consignment(s) had no ETA, so on-time is worked out "
              + $"from the {score.Judgeable} that did.");

        if (score.Judgeable == 0 && score.Delivered > 0)
            score.Warnings.Add(
                "Nothing this carrier delivered had an ETA to be judged against, so there is no "
              + "on-time figure at all.");

        if (score.StuckNow > 0)
            score.Warnings.Add(
                $"{score.StuckNow} consignment(s) are stuck right now. That is a live figure, not a "
              + "count for the window.");

        if (score.Delivered > 0 && score.Defensible < score.Delivered)
            score.Warnings.Add(
                $"{score.Delivered - score.Defensible} of {score.Delivered} deliveries could not be "
              + "demonstrated if they were denied.");

        if (score.Billing?.NotYetInvoiced > 0)
            score.Warnings.Add(
                $"{score.Billing.NotYetInvoiced} consignment(s) have not been billed. Some of that is "
              + "a carrier that has not invoiced yet, and some is a bill nobody keyed in.");
    }

    // ── Ordering ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Sorted, never scored. A carrier with no figure for the chosen measure goes last rather than
    /// being treated as zero — absent is not bad.
    /// </summary>
    private static IEnumerable<CarrierScoreModel> Sorted(List<CarrierScoreModel> carriers, string sort) =>
        sort switch
        {
            "ON_TIME"    => carriers.OrderByDescending(c => c.OnTimePercent ?? double.MinValue)
                                    .ThenByDescending(c => c.Consignments),
            "EXCEPTIONS" => carriers.OrderByDescending(c => c.ExceptionsPer100 ?? double.MinValue)
                                    .ThenByDescending(c => c.CriticalExceptions),
            "BILLING"    => carriers.OrderBy(c => c.Billing?.AccuracyPercent ?? double.MaxValue)
                                    .ThenByDescending(c => c.Consignments),
            _            => carriers.OrderByDescending(c => c.Consignments)
                                    .ThenBy(c => c.CarrierName)
        };

    private static double? Percent(int part, int whole) =>
        whole == 0 ? null : Math.Round(part * 100d / whole, 1);
}

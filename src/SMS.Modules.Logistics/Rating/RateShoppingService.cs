using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Rating;

/// <summary>
/// Comparing what every carrier would charge for the same consignment — and saying <b>why</b> one
/// won, not just which.
/// </summary>
/// <remarks>
/// A ranked list with no reasoning on it is a list somebody has to re-derive before they can defend
/// the choice, and the option that misses a deadline by a day is the one they most need to see.
/// </remarks>
public interface IRateShoppingService
{
    Task<RateShopResultModel?> ShopAsync(
        Guid consignmentUuid, RateShopRequest req, CancellationToken ct = default);

    /// <summary>
    /// Takes one of the shopped options: points the consignment at that carrier, account and
    /// service, and stores the quote through the same path a direct rating uses.
    /// </summary>
    Task<ConsignmentRateModel?> AcceptAsync(
        Guid consignmentUuid, AcceptRateRequest req, int userId, CancellationToken ct = default);
}

internal sealed class RateShoppingService : IRateShoppingService
{
    private const string Cheapest = "CHEAPEST";
    private const string Fastest  = "FASTEST";

    private readonly LogisticsDbContext        _db;
    private readonly IChargeableWeightService  _weights;
    private readonly ICarrierAccountResolver   _accounts;
    private readonly ICarrierServiceRepository _services;
    private readonly IRateCardService          _cards;
    private readonly IConsignmentQuoteStore    _store;

    public RateShoppingService(
        LogisticsDbContext db, IChargeableWeightService weights, ICarrierAccountResolver accounts,
        ICarrierServiceRepository services, IRateCardService cards, IConsignmentQuoteStore store)
    {
        _db       = db;
        _weights  = weights;
        _accounts = accounts;
        _services = services;
        _cards    = cards;
        _store    = store;
    }

    /// <summary>One quote, before it has been ranked or ruled out.</summary>
    private sealed record Candidate(
        Carrier            Carrier,
        Guid?              AccountUuid,
        string?            AccountName,
        RateSource         Source,
        CourierRateOption  Option,
        string?            Note);

    // ── Shopping ──────────────────────────────────────────────────────────────

    public async Task<RateShopResultModel?> ShopAsync(
        Guid consignmentUuid, RateShopRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        // Read-only: shopping compares prices, it does not choose one, so it must not write the
        // weights onto the packages the way rating does.
        var weight = await _weights.GetAsync(consignmentUuid, ct);
        if (weight is null) return null;

        var consignment = await _db.Consignments.AsNoTracking()
            .Include(c => c.ShipFromAddress)
            .Include(c => c.ShipToAddress)
            .Include(c => c.Deliveries).ThenInclude(cd => cd.DeliveryOrder).ThenInclude(d => d.Packages)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.UUID == consignmentUuid && !c.IsDelete, ct);

        if (consignment is null) return null;

        var strategy = Normalize(req.Strategy);
        var shipDate = req.ShipDate ?? DateTime.UtcNow;
        var carriers = await CandidateCarriersAsync(req, ct);

        var result = new RateShopResultModel
        {
            ConsignmentUuid    = consignment.UUID,
            ConsignmentNumber  = consignment.ConsignmentNumber,
            ChargeableWeightKg = weight.TotalChargeableKg,
            ShipDate           = shipDate,
            RequiredBy         = req.RequiredBy,
            Strategy           = strategy,
            Warnings           = [.. weight.Warnings]
        };

        if (!weight.IsComplete)
            result.Warnings.Insert(0,
                "Not every package on this consignment could be weighed, so every price below is a "
              + "floor. Comparing floors is still fair — they are all understated by the same packages.");

        if (carriers.Count == 0)
        {
            result.Warnings.Add("There are no active carriers to compare.");
            return result;
        }

        var candidates = new List<Candidate>();

        foreach (var carrier in carriers)
        {
            var quoted = await QuoteCarrierAsync(
                consignment, carrier, req, weight.TotalChargeableKg, shipDate, result.Excluded, ct);

            candidates.AddRange(quoted);
        }

        Rank(result, candidates, strategy, req.RequiredBy);

        return result;
    }

    /// <summary>
    /// Everything one carrier would charge. Its own adapter first, exactly as T-47 does — a carrier
    /// quoted twice, once by its API and once from a card, would show two prices for one carrier
    /// with no way to tell which is real.
    /// </summary>
    private async Task<List<Candidate>> QuoteCarrierAsync(
        Consignment consignment, Carrier carrier, RateShopRequest req,
        decimal chargeableKg, DateTime shipDate, List<RateShopExclusionModel> excluded,
        CancellationToken ct)
    {
        void Exclude(string reason, string? serviceCode = null) =>
            excluded.Add(new RateShopExclusionModel
            {
                CarrierUuid = carrier.UUID, CarrierName = carrier.Name,
                ServiceCode = serviceCode, Reason = reason
            });

        if (!req.RateCardOnly)
        {
            var fromCarrier = await AskCarrierAsync(consignment, carrier, req, shipDate, ct);
            if (fromCarrier.Count > 0) return fromCarrier;
        }

        // The card, once per service the carrier is configured to sell — which is what makes a
        // carrier with no API comparable on speed as well as price.
        var services = await _services.GetForCarrierAsync(carrier.UUID);

        var wanted = Trim(req.ServiceCode);

        var toQuote = services
            .Where(s => s.IsActive)
            .Where(s => wanted is null || string.Equals(s.ServiceCode, wanted, StringComparison.OrdinalIgnoreCase))
            .Select(s => (Code: (string?)s.ServiceCode, Name: s.ServiceName, s.TransitDays))
            .ToList();

        if (toQuote.Count == 0)
        {
            if (wanted is not null && services.Count > 0)
            {
                Exclude($"{carrier.Name} does not sell '{wanted}'.", wanted);
                return [];
            }

            // No services configured at all: quote the card once, on whatever the consignment names.
            toQuote = [(Trim(consignment.CarrierServiceCode), null, null)];
        }

        var candidates = new List<Candidate>();

        foreach (var (code, name, transitDays) in toQuote)
        {
            var quote = await _cards.QuoteAsync(new RateCardQuoteRequest(
                CarrierUuid:           carrier.UUID,
                ServiceCode:           code,
                ChargeableWeightKg:    chargeableKg,
                OriginCountryIso:      consignment.ShipFromAddress?.CountryIsoCode,
                OriginPostcode:        consignment.ShipFromAddress?.PostalCode,
                DestinationCountryIso: consignment.ShipToAddress?.CountryIsoCode,
                DestinationPostcode:   consignment.ShipToAddress?.PostalCode,
                CodAmount:             consignment.CodAmount,
                TransitDays:           transitDays,
                ShipDate:              shipDate), ct);

            if (!quote.Quoted)
            {
                Exclude(quote.Explanation ?? "No rate card covers this consignment.", code);
                continue;
            }

            candidates.Add(new Candidate(
                carrier, null, null, RateSource.RateCard,
                quote.Option! with { ServiceCode = code ?? quote.Option!.ServiceCode, ServiceName = name ?? quote.Option!.ServiceName },
                quote.Explanation));
        }

        return candidates;
    }

    /// <summary>
    /// The carrier's own rates, when it has an adapter that gives them. Every way this comes to
    /// nothing returns an empty list, and the card is tried next — a shop must not stop because one
    /// carrier's gateway is down.
    /// </summary>
    private async Task<List<Candidate>> AskCarrierAsync(
        Consignment consignment, Carrier carrier, RateShopRequest req, DateTime shipDate, CancellationToken ct)
    {
        ResolvedCarrierAccount account;

        try
        {
            account = await _accounts.ResolveAsync(carrier.UUID, null, ct);
        }
        catch (Exception ex) when (ex is NotFoundException or ConflictException or BadRequestException)
        {
            return [];
        }

        if (!account.Capabilities.SupportsRating) return [];

        var packages = TopLevelPackages(consignment)
            .Select(p => new CourierPackage(
                p.PackageBarcode, p.PackageType, p.LengthCm, p.WidthCm, p.HeightCm, p.GrossWeightKg,
                p.DeclaredValue))
            .ToList();

        if (packages.Count == 0) return [];

        CourierRateResult result;

        try
        {
            result = await account.Provider.RateAsync(new CourierRateRequest(
                ConsignmentNumber: consignment.ConsignmentNumber,
                ServiceCode:       Trim(req.ServiceCode),
                ShipFrom:          Map(consignment.ShipFromAddress),
                ShipTo:            Map(consignment.ShipToAddress),
                Packages:          packages,
                FreightTerms:      consignment.FreightTerms,
                CodAmount:         consignment.CodAmount,
                CodCurrency:       consignment.CodCurrency,
                ShipDate:          shipDate,
                Credentials:       account.Credentials), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }

        if (!result.Succeeded) return [];

        return
        [
            .. result.Options.Select(o => new Candidate(
                carrier, account.AccountUuid == Guid.Empty ? null : account.AccountUuid,
                account.AccountName, RateSource.Carrier, o,
                $"Quoted by {account.Provider.DisplayName}."))
        ];
    }

    // ── Ranking, and saying why ───────────────────────────────────────────────

    private static void Rank(
        RateShopResultModel result, List<Candidate> candidates, string strategy, DateTime? requiredBy)
    {
        if (candidates.Count == 0) return;

        // Prices in different currencies cannot be compared without a rate, and this module holds
        // no FX table. Rather than pretend, the largest currency group is ranked and the rest are
        // returned unranked and flagged — a silently mis-ranked quote would be far worse.
        var byCurrency = candidates
            .GroupBy(c => c.Option.Currency, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var comparable = byCurrency[0].ToList();
        var others     = byCurrency.Skip(1).SelectMany(g => g).ToList();

        if (others.Count > 0)
            result.Warnings.Add(
                $"{others.Count} quote(s) came back in "
              + $"{string.Join(", ", others.Select(o => o.Option.Currency).Distinct().Order())} "
              + $"and cannot be ranked against {comparable[0].Option.Currency} without an exchange "
              + "rate. They are listed unranked.");

        // The deadline rules an option out, but never hides it: the cheapest option that misses by a
        // day is exactly what somebody needs to see before accepting the one that does not.
        var missed = requiredBy is null
            ? []
            : comparable.Where(c => c.Option.EstimatedDelivery is { } eta && eta.Date > requiredBy.Value.Date)
                        .ToList();

        foreach (var late in missed)
            result.Excluded.Add(new RateShopExclusionModel
            {
                CarrierUuid = late.Carrier.UUID,
                CarrierName = late.Carrier.Name,
                ServiceCode = late.Option.ServiceCode,
                TotalAmount = late.Option.TotalAmount,
                Currency    = late.Option.Currency,
                Reason      = $"Arrives {late.Option.EstimatedDelivery:yyyy-MM-dd}, after the "
                            + $"{requiredBy:yyyy-MM-dd} the goods are needed by."
            });

        var eligible = comparable.Except(missed).ToList();

        if (eligible.Count == 0)
        {
            result.Warnings.Add(requiredBy is null
                ? "No carrier quoted a comparable price."
                : $"Nothing quoted arrives by {requiredBy:yyyy-MM-dd}. Every option is listed under "
                + "excluded, cheapest first, so the shortfall can be weighed against the cost.");

            result.Options = [.. others.Select(c => ToModel(c, null, null))];
            return;
        }

        var ordered = strategy == Fastest
            // Unknown transit sorts last: a carrier that will not say how long it takes cannot be
            // recommended for speed.
            ? eligible.OrderBy(c => c.Option.TransitDays ?? int.MaxValue)
                      .ThenBy(c => c.Option.TotalAmount)
                      .ThenBy(c => c.Carrier.Name, StringComparer.Ordinal).ToList()
            : eligible.OrderBy(c => c.Option.TotalAmount)
                      .ThenBy(c => c.Option.TransitDays ?? int.MaxValue)
                      .ThenBy(c => c.Carrier.Name, StringComparer.Ordinal).ToList();

        var best = ordered[0];

        var models = new List<RateShopOptionModel>();

        for (var i = 0; i < ordered.Count; i++)
        {
            var candidate = ordered[i];
            var more      = candidate.Option.TotalAmount - best.Option.TotalAmount;

            var model = ToModel(candidate, i + 1, more);

            model.MoreThanBestPercent = best.Option.TotalAmount > 0m
                ? Math.Round(more / best.Option.TotalAmount * 100m, 1)
                : null;

            model.Note = i == 0
                ? WhyItWon(ordered, strategy, requiredBy)
                : Compare(candidate, best, strategy);

            models.Add(model);
        }

        models.AddRange(others.Select(c => ToModel(c, null, null)));

        foreach (var unranked in models.Where(m => m.Rank is null))
            unranked.Note = $"Quoted in {unranked.Currency}; not ranked against {best.Option.Currency}.";

        result.Options        = models;
        result.Recommended    = models[0];
        result.Recommendation = models[0].Note;
    }

    private static string WhyItWon(List<Candidate> ordered, string strategy, DateTime? requiredBy)
    {
        var best = ordered[0];
        var next = ordered.Count > 1 ? ordered[1] : null;

        var opening = strategy == Fastest
            ? best.Option.TransitDays is { } days
                ? $"Fastest of {ordered.Count}: {best.Carrier.Name} {best.Option.ServiceCode} in {days} day(s)"
                : $"First of {ordered.Count}: {best.Carrier.Name} {best.Option.ServiceCode}"
            : $"Cheapest of {ordered.Count}: {best.Carrier.Name} {best.Option.ServiceCode} at "
            + $"{best.Option.Currency} {best.Option.TotalAmount:N2}";

        // The deadline is stated whether or not there was anything to beat — when one option is all
        // that survived the constraint, saying it made the cut is the most useful thing to say.
        var deadline = requiredBy is null
            ? ""
            : $" It arrives {best.Option.EstimatedDelivery:yyyy-MM-dd}, inside the {requiredBy:yyyy-MM-dd} deadline.";

        if (next is null) return $"{opening}, and the only comparable quote.{deadline}";

        var margin = strategy == Fastest
            ? next.Option.TransitDays is { } nextDays && best.Option.TransitDays is { } bestDays
                ? $"{nextDays - bestDays} day(s) ahead of {next.Carrier.Name} {next.Option.ServiceCode}"
                : $"ahead of {next.Carrier.Name} {next.Option.ServiceCode}"
            : $"{best.Option.Currency} {next.Option.TotalAmount - best.Option.TotalAmount:N2} below "
            + $"{next.Carrier.Name} {next.Option.ServiceCode}";

        return $"{opening} — {margin}.{deadline}";
    }

    private static string Compare(Candidate candidate, Candidate best, string strategy)
    {
        var more = candidate.Option.TotalAmount - best.Option.TotalAmount;

        if (strategy == Fastest)
            return candidate.Option.TransitDays is { } days && best.Option.TransitDays is { } bestDays
                ? $"{days - bestDays} day(s) slower than the recommendation."
                : "No transit time quoted, so it cannot be compared on speed.";

        var faster = candidate.Option.TransitDays is { } t && best.Option.TransitDays is { } b && t < b
            ? $" It does arrive {b - t} day(s) sooner."
            : "";

        return more <= 0m
            ? "Same price as the recommendation."
            : $"{candidate.Option.Currency} {more:N2} more than the recommendation.{faster}";
    }

    private static RateShopOptionModel ToModel(Candidate c, int? rank, decimal? moreThanBest) => new()
    {
        CarrierUuid        = c.Carrier.UUID,
        CarrierName        = c.Carrier.Name,
        CarrierAccountUuid = c.AccountUuid,
        CarrierAccountName = c.AccountName,
        ServiceCode        = c.Option.ServiceCode,
        ServiceName        = c.Option.ServiceName,
        Source             = LogisticsCode.Of(c.Source),
        TotalAmount        = c.Option.TotalAmount,
        Currency           = c.Option.Currency,
        BaseAmount         = c.Option.BaseAmount,
        Surcharges         = [.. (c.Option.Surcharges ?? []).Select(s => new ConsignmentChargeModel
        {
            Code = s.Code, Description = s.Description, Amount = s.Amount
        })],
        TransitDays        = c.Option.TransitDays,
        EstimatedDelivery  = c.Option.EstimatedDelivery,
        IsGuaranteed       = c.Option.IsGuaranteed,
        Rank               = rank,
        MoreThanBest       = moreThanBest
    };

    // ── Accepting ─────────────────────────────────────────────────────────────

    public async Task<ConsignmentRateModel?> AcceptAsync(
        Guid consignmentUuid, AcceptRateRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        // A blank service code is a real answer, not a missing one: a carrier priced from a general
        // rate card with no services configured produces exactly one option, and it has no code.
        // Consignment.CarrierServiceCode has always been nullable for the same reason. Where every
        // option *is* named, a blank simply matches none and the refusal below says so.
        var serviceCode = Trim(req.ServiceCode);

        // Re-quoted rather than trusted from the request body. A price that arrived at a browser and
        // came back changed is not a price the carrier ever gave, and this is the moment it would
        // become the figure an invoice gets reconciled against.
        var shop = await ShopAsync(consignmentUuid, new RateShopRequest
        {
            CarrierUuids = [req.CarrierUuid],
            ServiceCode  = serviceCode,
            ShipDate     = req.ShipDate,
            RateCardOnly = string.Equals(req.Source, LogisticsCode.Of(RateSource.RateCard),
                                         StringComparison.OrdinalIgnoreCase)
        }, ct);

        if (shop is null) return null;

        var option = shop.Options.FirstOrDefault(o =>
            o.CarrierUuid == req.CarrierUuid
         && string.Equals(o.ServiceCode ?? string.Empty, serviceCode ?? string.Empty,
                          StringComparison.OrdinalIgnoreCase));

        if (option is null)
            throw new ConflictException(
                $"'{serviceCode ?? "(no service)"}' could not be quoted for that carrier now. "
              + string.Join(" ", shop.Excluded.Select(e => e.Reason))
              + " Shop again and pick from what comes back.");

        var quote = new CourierRateOption(
            ServiceCode:        option.ServiceCode,
            ServiceName:        option.ServiceName,
            TotalAmount:        option.TotalAmount,
            Currency:           option.Currency,
            BaseAmount:         option.BaseAmount,
            Surcharges:         [.. option.Surcharges.Select(s => new CourierSurcharge(s.Code, s.Description, s.Amount))],
            ChargeableWeightKg: shop.ChargeableWeightKg,
            EstimatedDelivery:  option.EstimatedDelivery,
            TransitDays:        option.TransitDays,
            IsGuaranteed:       option.IsGuaranteed);

        return await _store.AcceptQuoteAsync(consignmentUuid, new AcceptedQuote(
            Option:             quote,
            Source:             LogisticsCode.Parse<RateSource>(option.Source),
            Note:               $"Chosen by rate shopping: {option.CarrierName} {option.ServiceCode}."
                              + (option.Note is null ? "" : $" {option.Note}"),
            CarrierUuid:        option.CarrierUuid,
            CarrierAccountUuid: option.CarrierAccountUuid,
            ChargeableWeightKg: shop.ChargeableWeightKg), userId, ct);
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private async Task<List<Carrier>> CandidateCarriersAsync(RateShopRequest req, CancellationToken ct)
    {
        var query = _db.Carriers.AsNoTracking().Where(c => !c.IsDelete && c.IsActive);

        if (req.CarrierUuids is { Count: > 0 } wanted)
            query = query.Where(c => wanted.Contains(c.UUID));

        return await query.OrderBy(c => c.Name).ToListAsync(ct);
    }

    private static string Normalize(string? strategy) =>
        string.Equals(strategy?.Trim(), Fastest, StringComparison.OrdinalIgnoreCase) ? Fastest : Cheapest;

    private static IEnumerable<ShipmentPackage> TopLevelPackages(Consignment consignment) =>
        consignment.Deliveries
            .Select(cd => cd.DeliveryOrder)
            .Where(d => d is not null && !d.IsDelete)
            .SelectMany(d => d.Packages)
            .Where(p => !p.IsVoided && !p.IsDelete && p.ParentPackageId == null)
            .DistinctBy(p => p.Id)
            .OrderBy(p => p.PackageBarcode, StringComparer.Ordinal);

    private static CourierAddress Map(Address? a) => new(
        ContactName:    a?.ContactName,
        ContactPhone:   a?.ContactPhoneE164 ?? a?.ContactPhone,
        ContactEmail:   a?.ContactEmail,
        Line1:          a?.Line1,
        Line2:          a?.Line2,
        City:           a?.CityName,
        State:          a?.State,
        PostalCode:     a?.PostalCode,
        CountryIsoCode: a?.CountryIsoCode);

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

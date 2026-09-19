using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Rating;

internal interface IRateCardRepository
{
    Task<Guid> CreateAsync(CreateRateCardRequest req, int userId);
    Task<IReadOnlyList<RateCardModel>> GetForCarrierAsync(Guid carrierUuid, DateTime? on = null);
    Task<RateCardModel?> GetByUuidAsync(Guid uuid, DateTime? on = null);
    Task<bool> PatchAsync(Guid uuid, PatchRateCardRequest req, int userId);
    Task<bool> DeleteAsync(Guid uuid, int userId);

    /// <summary>
    /// The card that prices this carrier and service on this date, loaded with its lanes and
    /// breaks. A service-specific card beats a carrier-wide one; null when neither exists.
    /// </summary>
    Task<RateCard?> ResolveAsync(
        int carrierId, string? serviceCode, DateTime on, CancellationToken ct = default);

    /// <summary>
    /// The internal id behind a carrier UUID. <see cref="ResolveAsync"/> takes the id because the
    /// consignment path (T-47) already has it; callers holding only a UUID come through here.
    /// </summary>
    Task<int?> FindCarrierIdAsync(Guid carrierUuid, CancellationToken ct = default);
}

internal sealed class RateCardRepository : IRateCardRepository
{
    private static readonly string[] Clearable =
        ["EFFECTIVE_TO", "MINIMUM_CHARGE", "FUEL_SURCHARGE", "COD_FEE"];

    private readonly LogisticsDbContext _db;
    public RateCardRepository(LogisticsDbContext db) => _db = db;

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateRateCardRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var carrier = await _db.Carriers.FirstOrDefaultAsync(c => c.UUID == req.CarrierUuid && !c.IsDelete)
            ?? throw new NotFoundException("Carrier", req.CarrierUuid);

        var name        = Require(req.Name, "A rate card needs a name — somebody has to be able to tell two tariffs apart.");
        var currency    = Currency(req.Currency);
        var serviceCode = Trim(req.ServiceCode);

        ValidateDates(req.EffectiveFrom, req.EffectiveTo);
        ValidateTerms(req.MinimumCharge, req.FuelSurchargePercent, req.CodFeePercent, req.CodFeeMinimum);

        await RefuseOverlapAsync(carrier, serviceCode, req.EffectiveFrom, req.EffectiveTo, excludingId: null);

        var now = DateTime.UtcNow;

        var card = new RateCard
        {
            UUID                 = Guid.NewGuid(),
            OrganizationId       = carrier.OrganizationId,
            CarrierId            = carrier.Id,
            ServiceCode          = serviceCode,
            Name                 = name,
            Currency             = currency,
            EffectiveFrom        = req.EffectiveFrom.Date,
            EffectiveTo          = req.EffectiveTo?.Date,
            MinimumCharge        = req.MinimumCharge,
            FuelSurchargePercent = req.FuelSurchargePercent,
            CodFeePercent        = req.CodFeePercent,
            CodFeeMinimum        = req.CodFeeMinimum,
            IsActive             = true,
            CreatedBy            = userId,
            CreatedDate          = now
        };

        BuildLanes(card, req.Lanes, userId, now);

        _db.RateCards.Add(card);
        await _db.SaveChangesAsync();

        return card.UUID;
    }

    /// <summary>
    /// Replaces a card's lanes, validating the whole tariff before any of it is attached.
    /// </summary>
    private static void BuildLanes(RateCard card, List<RateCardLaneRequest>? lanes, int userId, DateTime now)
    {
        if (lanes is null || lanes.Count == 0)
            throw new BadRequestException(
                "A rate card with no lanes prices nothing. Add at least one lane — leave its "
              + "criteria empty for a tariff that applies everywhere.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        card.Lanes.Clear();

        foreach (var request in lanes)
        {
            var lane = new RateCardLane
            {
                UUID                      = Guid.NewGuid(),
                OrganizationId            = card.OrganizationId,
                Name                      = Trim(request.Name),
                OriginCountryIso          = Upper(request.OriginCountryIso),
                DestinationCountryIso     = Upper(request.DestinationCountryIso),
                OriginPostcodePrefix      = Upper(request.OriginPostcodePrefix),
                DestinationPostcodePrefix = Upper(request.DestinationPostcodePrefix),
                CreatedBy                 = userId,
                CreatedDate               = now
            };

            var key = string.Join('|',
                lane.OriginCountryIso, lane.OriginPostcodePrefix,
                lane.DestinationCountryIso, lane.DestinationPostcodePrefix);

            if (!seen.Add(key))
                throw new ConflictException(
                    "Two lanes on this card match on exactly the same criteria, which would make "
                  + "the price depend on row order. Merge them.");

            BuildBreaks(lane, request.Breaks, card.OrganizationId, userId, now);

            card.Lanes.Add(lane);
        }
    }

    private static void BuildBreaks(
        RateCardLane lane, List<RateCardBreakRequest>? breaks, Guid organizationId, int userId, DateTime now)
    {
        if (breaks is null || breaks.Count == 0)
            throw new BadRequestException(
                $"Lane '{lane.Name ?? "unnamed"}' has no weight breaks, so it prices nothing.");

        var ordered = breaks.OrderBy(b => b.FromWeightKg).ToList();

        // A lane whose lowest break is above zero has a hole under it, and a consignment falling
        // into that hole prices at nothing at all. Every other card on the system would look fine.
        if (ordered[0].FromWeightKg != 0m)
            throw new BadRequestException(
                $"Lane '{lane.Name ?? "unnamed"}' starts at {ordered[0].FromWeightKg:0.###} kg. "
              + "Weight breaks must start at 0, or anything lighter falls through the card.");

        decimal? previous = null;

        foreach (var request in ordered)
        {
            if (request.FromWeightKg < 0m)
                throw new BadRequestException("A weight break cannot start below zero.");

            if (previous == request.FromWeightKg)
                throw new BadRequestException(
                    $"Lane '{lane.Name ?? "unnamed"}' has two breaks starting at "
                  + $"{request.FromWeightKg:0.###} kg. One weight, one rate.");

            if (request.Amount <= 0m)
                throw new BadRequestException(
                    $"A rate of {request.Amount} is not a rate. Every weight break needs a positive amount.");

            if (!LogisticsCode.TryParse<RateBasis>(request.Basis, out var basis))
                throw new BadRequestException(
                    $"'{request.Basis}' is not a rate basis. Valid values: "
                  + $"{string.Join(", ", LogisticsCode.Codes<RateBasis>())}.");

            lane.Breaks.Add(new RateCardBreak
            {
                UUID           = Guid.NewGuid(),
                OrganizationId = organizationId,
                FromWeightKg   = request.FromWeightKg,
                Basis          = LogisticsCode.Of(basis),
                Amount         = request.Amount,
                CreatedBy      = userId,
                CreatedDate    = now
            });

            previous = request.FromWeightKg;
        }
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RateCardModel>> GetForCarrierAsync(Guid carrierUuid, DateTime? on = null)
    {
        var carrier = await _db.Carriers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.UUID == carrierUuid && !c.IsDelete);

        if (carrier is null) return [];

        var cards = await WithTariff(_db.RateCards.AsNoTracking())
            .Where(c => c.CarrierId == carrier.Id && !c.IsDelete)
            // Newest period first: the current tariff is what anybody opening the screen wants.
            .OrderByDescending(c => c.EffectiveFrom)
            .ThenBy(c => c.Name)
            .ToListAsync();

        return [.. cards.Select(c => ToModel(c, carrier, on ?? DateTime.UtcNow))];
    }

    public async Task<RateCardModel?> GetByUuidAsync(Guid uuid, DateTime? on = null)
    {
        var card = await WithTariff(_db.RateCards.AsNoTracking())
            .Include(c => c.Carrier)
            .FirstOrDefaultAsync(c => c.UUID == uuid && !c.IsDelete);

        return card is null ? null : ToModel(card, card.Carrier, on ?? DateTime.UtcNow);
    }

    public async Task<RateCard?> ResolveAsync(
        int carrierId, string? serviceCode, DateTime on, CancellationToken ct = default)
    {
        var date = on.Date;

        var candidates = await WithTariff(_db.RateCards.AsNoTracking())
            .Where(c => c.CarrierId == carrierId && !c.IsDelete && c.IsActive
                     && c.EffectiveFrom <= date
                     && (c.EffectiveTo == null || c.EffectiveTo >= date))
            .ToListAsync(ct);

        if (candidates.Count == 0) return null;

        var code = Trim(serviceCode);

        // A card written for this service beats the carrier-wide one — the general card is a
        // fallback, not a competitor.
        return candidates.FirstOrDefault(c =>
                   code is not null
                && string.Equals(c.ServiceCode, code, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.ServiceCode));
    }

    public async Task<int?> FindCarrierIdAsync(Guid carrierUuid, CancellationToken ct = default)
    {
        var carrier = await _db.Carriers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.UUID == carrierUuid && !c.IsDelete, ct);

        return carrier?.Id;
    }

    private static IQueryable<RateCard> WithTariff(IQueryable<RateCard> query) => query
        .Include(c => c.Lanes.Where(l => !l.IsDelete))
            .ThenInclude(l => l.Breaks.Where(b => !b.IsDelete))
        .AsSplitQuery();

    private static RateCardModel ToModel(RateCard card, Carrier carrier, DateTime on) => new()
    {
        UUID                 = card.UUID,
        CarrierUuid          = carrier.UUID,
        CarrierName          = carrier.Name,
        ServiceCode          = card.ServiceCode,
        Name                 = card.Name,
        Currency             = card.Currency,
        EffectiveFrom        = card.EffectiveFrom,
        EffectiveTo          = card.EffectiveTo,
        MinimumCharge        = card.MinimumCharge,
        FuelSurchargePercent = card.FuelSurchargePercent,
        CodFeePercent        = card.CodFeePercent,
        CodFeeMinimum        = card.CodFeeMinimum,
        IsActive             = card.IsActive,
        IsInEffect           = card.IsActive && Covers(card, on),
        CreatedDate          = card.CreatedDate,
        Lanes = [.. card.Lanes.Where(l => !l.IsDelete)
            .OrderBy(l => l.Name ?? string.Empty)
            .ThenBy(l => l.Id)
            .Select(l => new RateCardLaneModel
            {
                UUID                      = l.UUID,
                Name                      = l.Name,
                OriginCountryIso          = l.OriginCountryIso,
                OriginPostcodePrefix      = l.OriginPostcodePrefix,
                DestinationCountryIso     = l.DestinationCountryIso,
                DestinationPostcodePrefix = l.DestinationPostcodePrefix,
                Breaks = [.. l.Breaks.Where(b => !b.IsDelete)
                    .OrderBy(b => b.FromWeightKg)
                    .Select(b => new RateCardBreakModel
                    {
                        UUID = b.UUID, FromWeightKg = b.FromWeightKg, Basis = b.Basis, Amount = b.Amount
                    })]
            })]
    };

    private static bool Covers(RateCard card, DateTime on) =>
        card.EffectiveFrom <= on.Date && (card.EffectiveTo is null || card.EffectiveTo >= on.Date);

    // ── Update ────────────────────────────────────────────────────────────────

    public async Task<bool> PatchAsync(Guid uuid, PatchRateCardRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var card = await WithTariff(_db.RateCards)
            .Include(c => c.Carrier)
            .FirstOrDefaultAsync(c => c.UUID == uuid && !c.IsDelete);

        if (card is null) return false;

        if (req.Name is not null) card.Name = Require(req.Name, "A rate card name cannot be blank.");

        if (req.EffectiveFrom is { } from) card.EffectiveFrom = from.Date;
        if (req.EffectiveTo   is { } to)   card.EffectiveTo   = to.Date;

        if (req.MinimumCharge        is { } minimum) card.MinimumCharge        = minimum;
        if (req.FuelSurchargePercent is { } fuel)    card.FuelSurchargePercent = fuel;
        if (req.CodFeePercent        is { } percent) card.CodFeePercent        = percent;
        if (req.CodFeeMinimum        is { } codMin)  card.CodFeeMinimum        = codMin;
        if (req.IsActive             is { } active)  card.IsActive             = active;

        ApplyClears(card, req.ClearTerms);

        ValidateDates(card.EffectiveFrom, card.EffectiveTo);
        ValidateTerms(card.MinimumCharge, card.FuelSurchargePercent, card.CodFeePercent, card.CodFeeMinimum);

        await RefuseOverlapAsync(
            card.Carrier, card.ServiceCode, card.EffectiveFrom, card.EffectiveTo, excludingId: card.Id);

        var now = DateTime.UtcNow;

        if (req.Lanes is not null)
        {
            // Replaced wholesale. Cascade delete removes the old lanes and their breaks.
            _db.RateCardBreaks.RemoveRange(card.Lanes.SelectMany(l => l.Breaks));
            _db.RateCardLanes.RemoveRange(card.Lanes);

            BuildLanes(card, req.Lanes, userId, now);
        }

        card.ModifiedBy   = userId;
        card.ModifiedDate = now;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(Guid uuid, int userId)
    {
        var card = await _db.RateCards.FirstOrDefaultAsync(c => c.UUID == uuid && !c.IsDelete);
        if (card is null) return false;

        // Soft: a consignment priced from this card months ago is only explainable while the card
        // it was priced from still exists.
        card.IsDelete     = true;
        card.IsActive     = false;
        card.ModifiedBy   = userId;
        card.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    // ── Rules ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two cards that could both price the same carriage on the same day would make the price
    /// depend on row order. Refused here, which is what lets <see cref="ResolveAsync"/> pick one
    /// without arbitrating.
    /// </summary>
    private async Task RefuseOverlapAsync(
        Carrier carrier, string? serviceCode, DateTime from, DateTime? to, int? excludingId)
    {
        var existing = await _db.RateCards
            .Where(c => c.CarrierId == carrier.Id && !c.IsDelete
                     && (excludingId == null || c.Id != excludingId))
            .ToListAsync();

        var clash = existing.FirstOrDefault(c =>
            SameScope(c.ServiceCode, serviceCode) && Overlaps(c.EffectiveFrom, c.EffectiveTo, from.Date, to?.Date));

        if (clash is not null)
            throw new ConflictException(
                $"'{clash.Name}' already prices {Scope(serviceCode)} on {carrier.Name} between "
              + $"{clash.EffectiveFrom:yyyy-MM-dd} and {clash.EffectiveTo?.ToString("yyyy-MM-dd") ?? "further notice"}. "
              + "Close that card off before starting this one, or the price would depend on which row was read first.");
    }

    private static string Scope(string? serviceCode) =>
        string.IsNullOrWhiteSpace(serviceCode) ? "every service" : $"service '{serviceCode}'";

    private static bool SameScope(string? left, string? right) =>
        string.Equals(Trim(left) ?? string.Empty, Trim(right) ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>Inclusive on both ends; a null end date runs forever.</summary>
    private static bool Overlaps(DateTime aFrom, DateTime? aTo, DateTime bFrom, DateTime? bTo) =>
        (aTo is null || aTo >= bFrom) && (bTo is null || bTo >= aFrom);

    private static void ValidateDates(DateTime from, DateTime? to)
    {
        if (from == default)
            throw new BadRequestException("A rate card needs a date it takes effect from.");

        if (to is { } end && end < from)
            throw new BadRequestException("A rate card cannot stop applying before it starts.");
    }

    private static void ValidateTerms(
        decimal? minimumCharge, decimal? fuelPercent, decimal? codPercent, decimal? codMinimum)
    {
        if (minimumCharge is < 0) throw new BadRequestException("A minimum charge cannot be negative.");
        if (codMinimum    is < 0) throw new BadRequestException("A cash-on-delivery fee cannot be negative.");

        Percent(fuelPercent, "Fuel surcharge");
        Percent(codPercent,  "Cash-on-delivery fee");
    }

    private static void Percent(decimal? value, string what)
    {
        if (value is null) return;

        // Expressed as a percentage, not a fraction. 0.12 here means twelve hundredths of one
        // percent, and a card meaning 12% that was typed as 0.12 would under-charge by a hundred
        // times without ever looking wrong — so the upper bound is what catches the other direction.
        if (value < 0 || value > 100)
            throw new BadRequestException($"{what} must be a percentage between 0 and 100.");
    }

    private static void ApplyClears(RateCard card, List<string>? clears)
    {
        if (clears is null || clears.Count == 0) return;

        foreach (var raw in clears)
        {
            switch (raw?.Trim().ToUpperInvariant())
            {
                case "EFFECTIVE_TO":   card.EffectiveTo          = null; break;
                case "MINIMUM_CHARGE": card.MinimumCharge        = null; break;
                case "FUEL_SURCHARGE": card.FuelSurchargePercent = null; break;
                case "COD_FEE":        card.CodFeePercent        = null;
                                       card.CodFeeMinimum        = null; break;
                default:
                    throw new BadRequestException(
                        $"'{raw}' is not a clearable term. Valid values: {string.Join(", ", Clearable)}.");
            }
        }
    }

    private static string Currency(string? value)
    {
        var currency = Upper(value);

        if (currency is null || currency.Length != 3 || !currency.All(char.IsLetter))
            throw new BadRequestException(
                "A rate card needs a three-letter ISO currency — a tariff with no currency is a list of numbers.");

        return currency;
    }

    private static string Require(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new BadRequestException(message) : value.Trim();

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Upper(string? value) => Trim(value)?.ToUpperInvariant();
}

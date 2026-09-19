using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Rating;

/// <summary>
/// What a consignment costs to move, and where that figure came from.
/// <para>
/// <b>This is where decision G9's two halves meet.</b> The carrier is asked first, because its own
/// quote is the only number that is actually true — surcharges included, whether or not anybody has
/// modelled them. The rate card answers when the carrier will not, cannot, or does not reply. Both
/// arrive as the same <c>CourierRateOption</c>, so nothing below this line knows which it got.
/// </para>
/// </summary>
public interface IConsignmentRatingService
{
    /// <summary>The stored quote, or an unrated consignment's empty shape. Null if it does not exist.</summary>
    Task<ConsignmentRateModel?> GetAsync(Guid consignmentUuid, CancellationToken ct = default);

    /// <summary>Prices it, stores the result and moves it to RATED.</summary>
    Task<ConsignmentRateModel?> RateAsync(
        Guid consignmentUuid, RateConsignmentRequest req, int userId, CancellationToken ct = default);

    /// <summary>Records a price obtained outside the system — emailed, or quoted by telephone.</summary>
    Task<ConsignmentRateModel?> SetManualRateAsync(
        Guid consignmentUuid, ManualRateRequest req, int userId, CancellationToken ct = default);

    /// <summary>Discards the quote and drops back to DRAFT.</summary>
    Task<bool> ClearAsync(Guid consignmentUuid, int userId, CancellationToken ct = default);
}

/// <summary>A quote obtained elsewhere — by rate shopping (T-48) — and the carrier it came from.</summary>
internal sealed record AcceptedQuote(
    CourierRateOption Option,
    RateSource        Source,
    string?           Note,
    Guid              CarrierUuid,
    Guid?             CarrierAccountUuid,
    decimal?          ChargeableWeightKg);

/// <summary>
/// The one place a quote is written onto a consignment.
/// <para>
/// Separate from <see cref="IConsignmentRatingService"/> and implemented by the same object, so rate
/// shopping stores its winner through exactly the code that stores a direct rating — including the
/// state transition and the charge lines. Two ways to record a price would eventually disagree.
/// </para>
/// </summary>
internal interface IConsignmentQuoteStore
{
    Task<ConsignmentRateModel?> AcceptQuoteAsync(
        Guid consignmentUuid, AcceptedQuote quote, int userId, CancellationToken ct = default);
}

internal sealed class ConsignmentRatingService : IConsignmentRatingService, IConsignmentQuoteStore
{
    private static readonly ShipmentStateMachine Machine = ShipmentStateMachine.Instance;

    /// <summary>The line the base carriage is stored under. Surcharges keep the carrier's own codes.</summary>
    internal const string BaseChargeCode = "BASE";

    private readonly LogisticsDbContext        _db;
    private readonly IChargeableWeightService  _weights;
    private readonly ICarrierAccountResolver   _accounts;
    private readonly IRateCardService          _cards;

    public ConsignmentRatingService(
        LogisticsDbContext db, IChargeableWeightService weights,
        ICarrierAccountResolver accounts, IRateCardService cards)
    {
        _db       = db;
        _weights  = weights;
        _accounts = accounts;
        _cards    = cards;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<ConsignmentRateModel?> GetAsync(Guid consignmentUuid, CancellationToken ct = default)
    {
        var consignment = await _db.Consignments.AsNoTracking()
            .Include(c => c.Charges)
            .FirstOrDefaultAsync(c => c.UUID == consignmentUuid && !c.IsDelete, ct);

        return consignment is null ? null : ToModel(consignment);
    }

    // ── Rate ──────────────────────────────────────────────────────────────────

    public async Task<ConsignmentRateModel?> RateAsync(
        Guid consignmentUuid, RateConsignmentRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        // The weights first, and persisted: a quote that cannot be reproduced from stored figures
        // is a quote nobody can dispute. This also gives us the warnings to pass on.
        var weight = await _weights.RecalculateAsync(consignmentUuid, userId, ct);
        if (weight is null) return null;

        var consignment = await LoadForRatingAsync(consignmentUuid, ct);
        if (consignment is null) return null;

        EnsureRateable(consignment);

        var shipDate    = req.ShipDate ?? DateTime.UtcNow;
        var serviceCode = Trim(req.ServiceCode) ?? consignment.CarrierServiceCode;
        var attempts    = new List<RateAttemptModel>();

        if (consignment.CarrierId is null)
            throw new BadRequestException(
                "This consignment has no carrier, so there is nobody to price it. Set a carrier first.");

        CourierRateOption? option = null;
        RateSource         source = RateSource.RateCard;
        string?            note   = null;

        // ── The carrier first ─────────────────────────────────────────────────

        if (!req.RateCardOnly)
        {
            var (carrierOption, carrierService, attempt) =
                await AskTheCarrierAsync(consignment, req, serviceCode, shipDate, ct);

            attempts.Add(attempt);

            if (carrierOption is not null)
            {
                option      = carrierOption;
                source      = RateSource.Carrier;
                serviceCode = carrierOption.ServiceCode;
                note        = carrierService;
            }
        }

        // ── Then the card ─────────────────────────────────────────────────────

        if (option is null)
        {
            var quote = await _cards.QuoteAsync(new RateCardQuoteRequest(
                CarrierUuid:           consignment.Carrier!.UUID,
                ServiceCode:           serviceCode,
                ChargeableWeightKg:    weight.TotalChargeableKg,
                OriginCountryIso:      consignment.ShipFromAddress?.CountryIsoCode,
                OriginPostcode:        consignment.ShipFromAddress?.PostalCode,
                DestinationCountryIso: consignment.ShipToAddress?.CountryIsoCode,
                DestinationPostcode:   consignment.ShipToAddress?.PostalCode,
                CodAmount:             consignment.CodAmount,
                TransitDays:           null,
                ShipDate:              shipDate), ct);

            attempts.Add(new RateAttemptModel
            {
                Source    = LogisticsCode.Of(RateSource.RateCard),
                Succeeded = quote.Quoted,
                Message   = quote.Explanation
            });

            if (quote.Quoted)
            {
                option = quote.Option;
                source = RateSource.RateCard;
                note   = quote.Explanation;
            }
        }

        if (option is null)
            throw new ConflictException(
                "Nothing could price this consignment. "
              + string.Join(" ", attempts.Where(a => !a.Succeeded).Select(a => a.Message))
              + " Configure a rate card for this carrier, or record the price by hand.");

        Apply(consignment, option, source, note, serviceCode, weight.TotalChargeableKg, userId);

        await _db.SaveChangesAsync(ct);

        var model = ToModel(consignment);
        model.Attempts = attempts;
        model.Warnings = [.. weight.Warnings, .. weight.Packages.SelectMany(p => p.Warnings).Distinct()];

        if (!weight.IsComplete)
            model.Warnings.Insert(0,
                "Not every package on this consignment could be weighed, so this price is a floor "
              + "rather than a quote.");

        return model;
    }

    /// <summary>
    /// Asks the carrier's adapter, when there is one that rates. Every way this can come to nothing
    /// — no account, an adapter that does not rate, a refusal, a call that came apart — is reported
    /// rather than swallowed, because "the carrier quoted this" and "the carrier would not answer,
    /// so the card was used" are different facts about the same number.
    /// </summary>
    private async Task<(CourierRateOption? Option, string? Note, RateAttemptModel Attempt)> AskTheCarrierAsync(
        Consignment consignment, RateConsignmentRequest req, string? serviceCode,
        DateTime shipDate, CancellationToken ct)
    {
        RateAttemptModel Failed(string message) => new()
        {
            Source = LogisticsCode.Of(RateSource.Carrier), Succeeded = false, Message = message
        };

        ResolvedCarrierAccount account;

        try
        {
            account = await _accounts.ResolveAsync(
                consignment.Carrier!.UUID, req.CarrierAccountUuid ?? consignment.CarrierAccount?.UUID, ct);
        }
        catch (Exception ex) when (ex is NotFoundException or ConflictException or BadRequestException)
        {
            return (null, null, Failed($"No carrier account could be resolved: {ex.Message}"));
        }

        if (!account.Capabilities.SupportsRating)
            return (null, null, Failed(
                $"{account.Provider.DisplayName} does not quote — pricing from the rate card instead."));

        var packages = TopLevelPackages(consignment)
            .Select(p => new CourierPackage(
                p.PackageBarcode, p.PackageType, p.LengthCm, p.WidthCm, p.HeightCm, p.GrossWeightKg,
                p.DeclaredValue))
            .ToList();

        if (packages.Count == 0)
            return (null, null, Failed("Nothing on this consignment has been packed, so the carrier has nothing to price."));

        CourierRateResult result;

        try
        {
            result = await account.Provider.RateAsync(new CourierRateRequest(
                ConsignmentNumber: consignment.ConsignmentNumber,
                ServiceCode:       serviceCode,
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
            // The caller gave up, not the carrier. Nothing to fall back to and nothing to report.
            throw;
        }
        catch (Exception ex)
        {
            // A rate call is a read, so an adapter coming apart costs nothing and the card takes
            // over. This is exactly the case a booking must never treat this way.
            return (null, null, Failed($"The carrier's rate call failed: {ex.Message}"));
        }

        if (!result.Succeeded || result.Options.Count == 0)
            return (null, null, Failed(
                result.Message ?? $"{account.Provider.DisplayName} returned no rates."));

        var chosen = Choose(result.Options, serviceCode);

        return (chosen,
                $"Quoted by {account.Provider.DisplayName} on account '{account.AccountName}'"
              + (chosen.ServiceName is null ? "" : $", {chosen.ServiceName}") + ".",
                new RateAttemptModel
                {
                    Source    = LogisticsCode.Of(RateSource.Carrier),
                    Succeeded = true,
                    Message   = $"{result.Options.Count} rate(s) returned."
                });
    }

    /// <summary>
    /// The named service if the carrier quoted it, otherwise the cheapest. Rate <em>shopping</em> —
    /// comparing across carriers and explaining which won — is T-48; this only picks one option
    /// from one carrier, and picking the dearest by accident is the failure worth avoiding here.
    /// </summary>
    private static CourierRateOption Choose(IReadOnlyList<CourierRateOption> options, string? serviceCode) =>
        (string.IsNullOrWhiteSpace(serviceCode)
            ? null
            : options.FirstOrDefault(o => string.Equals(
                o.ServiceCode, serviceCode.Trim(), StringComparison.OrdinalIgnoreCase)))
        ?? options.OrderBy(o => o.TotalAmount).ThenBy(o => o.ServiceCode, StringComparer.Ordinal).First();

    // ── Manual ────────────────────────────────────────────────────────────────

    public async Task<ConsignmentRateModel?> SetManualRateAsync(
        Guid consignmentUuid, ManualRateRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var consignment = await LoadForRatingAsync(consignmentUuid, ct);
        if (consignment is null) return null;

        EnsureRateable(consignment);

        if (req.Amount <= 0m)
            throw new BadRequestException("A freight cost of zero is not a price anybody agreed.");

        var currency = Trim(req.Currency)?.ToUpperInvariant();

        if (currency is null || currency.Length != 3 || !currency.All(char.IsLetter))
            throw new BadRequestException("A freight cost needs a three-letter ISO currency.");

        // Provenance, not decoration: a keyed-in price with no source behind it cannot be defended
        // when the invoice disagrees with it.
        var note = Trim(req.Note)
            ?? throw new BadRequestException(
                "Say where this price came from — the quotation reference, or who gave it. A figure "
              + "with no provenance cannot be checked against an invoice.");

        var option = new CourierRateOption(
            ServiceCode:  Trim(req.ServiceCode) ?? consignment.CarrierServiceCode ?? string.Empty,
            ServiceName:  null,
            TotalAmount:  req.Amount,
            Currency:     currency,
            BaseAmount:   req.Amount,
            Surcharges:   []);

        Apply(consignment, option, RateSource.Manual, note,
              option.ServiceCode, consignment.RatedChargeableWeightKg, userId);

        await _db.SaveChangesAsync(ct);

        return ToModel(consignment);
    }

    // ── Accepting a shopped quote (T-48) ──────────────────────────────────────

    public async Task<ConsignmentRateModel?> AcceptQuoteAsync(
        Guid consignmentUuid, AcceptedQuote quote, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var consignment = await LoadForRatingAsync(consignmentUuid, ct);
        if (consignment is null) return null;

        EnsureRateable(consignment);

        var carrier = await _db.Carriers.FirstOrDefaultAsync(
                c => c.UUID == quote.CarrierUuid && !c.IsDelete, ct)
            ?? throw new NotFoundException("Carrier", quote.CarrierUuid);

        // Accepting a quote is choosing a carrier. Leaving the consignment pointing at whoever it
        // named before would price it with one carrier and book it with another.
        consignment.CarrierId          = carrier.Id;
        consignment.CarrierName        = carrier.Name;
        consignment.CarrierServiceCode = Trim(quote.Option.ServiceCode);

        if (quote.CarrierAccountUuid is { } accountUuid)
        {
            var account = await _db.CarrierAccounts.FirstOrDefaultAsync(
                a => a.UUID == accountUuid && !a.IsDelete, ct);

            consignment.CarrierAccountId = account?.Id;
        }

        Apply(consignment, quote.Option, quote.Source, quote.Note,
              quote.Option.ServiceCode, quote.ChargeableWeightKg, userId);

        await _db.SaveChangesAsync(ct);

        return ToModel(consignment);
    }

    public async Task<bool> ClearAsync(Guid consignmentUuid, int userId, CancellationToken ct = default)
    {
        var consignment = await _db.Consignments
            .Include(c => c.Charges)
            .FirstOrDefaultAsync(c => c.UUID == consignmentUuid && !c.IsDelete, ct);

        if (consignment is null) return false;

        var current = LogisticsCode.Parse<ShipmentStatus>(consignment.Status);

        if (current != ShipmentStatus.Rated)
            throw new ConflictException(
                $"This consignment is {consignment.Status}, not RATED. Only a quote that has not been "
              + "acted on can be discarded.");

        Machine.EnsureCanTransition(current, ShipmentStatus.Draft);

        _db.ConsignmentCharges.RemoveRange(consignment.Charges);

        consignment.FreightCost             = null;
        consignment.FreightCurrency         = null;
        consignment.FreightRatedAt          = null;
        consignment.FreightRateSource       = null;
        consignment.FreightRateNote         = null;
        consignment.RatedChargeableWeightKg = null;
        consignment.RatedServiceCode        = null;
        consignment.Status                  = LogisticsCode.Of(ShipmentStatus.Draft);
        consignment.ModifiedBy              = userId;
        consignment.ModifiedDate            = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ── Storing ───────────────────────────────────────────────────────────────

    private void Apply(
        Consignment consignment, CourierRateOption option, RateSource source, string? note,
        string? serviceCode, decimal? chargeableKg, int userId)
    {
        var now = DateTime.UtcNow;

        _db.ConsignmentCharges.RemoveRange(consignment.Charges);
        consignment.Charges.Clear();

        var sequence = 0;

        void AddCharge(string code, string? description, decimal amount) =>
            consignment.Charges.Add(new ConsignmentCharge
            {
                UUID           = Guid.NewGuid(),
                OrganizationId = consignment.OrganizationId,
                Code           = code,
                Description    = description,
                Amount         = amount,
                Sequence       = sequence++,
                CreatedBy      = userId,
                CreatedDate    = now
            });

        AddCharge(BaseChargeCode, "Base carriage", option.BaseAmount ?? option.TotalAmount);

        foreach (var surcharge in option.Surcharges ?? [])
            AddCharge(surcharge.Code, surcharge.Description, surcharge.Amount);

        consignment.FreightCost             = option.TotalAmount;
        consignment.FreightCurrency         = option.Currency;
        consignment.FreightRatedAt          = now;
        consignment.FreightRateSource       = LogisticsCode.Of(source);
        consignment.FreightRateNote         = note;
        consignment.RatedChargeableWeightKg = option.ChargeableWeightKg ?? chargeableKg;
        consignment.RatedServiceCode        = Trim(serviceCode);
        consignment.ModifiedBy              = userId;
        consignment.ModifiedDate            = now;

        // Re-rating leaves the status alone: nothing about the consignment's stage has changed, and
        // a self-transition would claim otherwise.
        var current = LogisticsCode.Parse<ShipmentStatus>(consignment.Status);

        if (current != ShipmentStatus.Rated)
        {
            Machine.EnsureCanTransition(current, ShipmentStatus.Rated);
            consignment.Status = LogisticsCode.Of(ShipmentStatus.Rated);
        }
    }

    /// <summary>
    /// Refused once the consignment has been handed to a carrier. After that the real cost is what
    /// was booked, and overwriting it with a fresh quote would replace a fact with an estimate.
    /// </summary>
    private static void EnsureRateable(Consignment consignment)
    {
        var current = LogisticsCode.Parse<ShipmentStatus>(consignment.Status);

        if (current == ShipmentStatus.Rated) return;

        if (!Machine.CanTransition(current, ShipmentStatus.Rated))
            throw new ConflictException(
                $"This consignment is {consignment.Status} and cannot be re-priced. Once it is with "
              + "the carrier, what it costs is what was booked — not what a quote says today.");
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private Task<Consignment?> LoadForRatingAsync(Guid uuid, CancellationToken ct) =>
        _db.Consignments
            .Include(c => c.Carrier)
            .Include(c => c.CarrierAccount)
            .Include(c => c.ShipFromAddress)
            .Include(c => c.ShipToAddress)
            .Include(c => c.Charges)
            .Include(c => c.Deliveries).ThenInclude(cd => cd.DeliveryOrder).ThenInclude(d => d.Packages)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.UUID == uuid && !c.IsDelete, ct);

    /// <summary>
    /// Top-level handling units only — the same set declared to a carrier at booking. A carton
    /// inside a pallet travels inside the pallet, and pricing both bills the same goods twice.
    /// </summary>
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

    private static ConsignmentRateModel ToModel(Consignment c) => new()
    {
        ConsignmentUuid    = c.UUID,
        ConsignmentNumber  = c.ConsignmentNumber,
        Status             = c.Status,
        IsRated            = c.FreightCost is not null,
        FreightCost        = c.FreightCost,
        FreightCurrency    = c.FreightCurrency,
        RatedAt            = c.FreightRatedAt,
        Source             = c.FreightRateSource,
        Note               = c.FreightRateNote,
        ServiceCode        = c.RatedServiceCode,
        ChargeableWeightKg = c.RatedChargeableWeightKg,
        Charges = [.. c.Charges.OrderBy(x => x.Sequence).Select(x => new ConsignmentChargeModel
        {
            Code = x.Code, Description = x.Description, Amount = x.Amount
        })]
    };

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

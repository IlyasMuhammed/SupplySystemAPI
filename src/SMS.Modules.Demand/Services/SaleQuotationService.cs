using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A32 C2 — the seller-side Sale Quotation (spec §4, BR-C2-01..11, API-CONTRACT §5). Unrelated to the buyer-side RFQ
/// <see cref="QuotationService"/>.
/// <para>
/// <b>Own organization.</b> Every lookup filters explicitly on the caller's organization (<see cref="OwnQuotations"/>):
/// the EF tenant filter is bypassed for super admins, and another organization's quotation must be "not found" for
/// them too.
/// </para>
/// <para>
/// <b>Numbering (PC-03).</b> <see cref="IDocumentNumberGenerator"/> with prefix SQ → <c>SQ-YYYY-NNNNN</c> per
/// organization per year: its counter row carries a concurrency token and retries, and
/// <c>(OrganizationId, QuotationNumber)</c> is uniquely indexed. A number is drawn only once every rule has passed,
/// so a refused create does not burn one.
/// </para>
/// <para>
/// <b>State machine (PC-04).</b> DRAFT → SENT → ACCEPTED → CONVERTED; SENT → REJECTED; SENT → EXPIRED
/// (<see cref="QuotationExpiryJob"/>). Only DRAFT is editable; responses are recorded only while SENT. Status is an
/// EF concurrency token, so every transition is a tracked load → mutate → one SaveChangesAsync and a lost race is
/// a 409.
/// </para>
/// <para>
/// <b>Lines and totals (PC-05).</b> The SaleOrderLine rules: <c>qty × price × (1 − disc%) × (1 + tax%)</c> rounded
/// to 2, a Finance tax code's rate snapshotted when one is picked, and the header summed from the same three parts.
/// REJECTED lines carry no price and count for nothing; NORMAL and ALTERNATIVE lines are summed — the header is
/// what was offered.
/// </para>
/// </summary>
internal sealed class SaleQuotationService : ISaleQuotationService
{
    private static readonly string Draft     = EnumCode<SaleQuotationStatus>.Of(SaleQuotationStatus.Draft);
    private static readonly string Sent      = EnumCode<SaleQuotationStatus>.Of(SaleQuotationStatus.Sent);
    private static readonly string Accepted  = EnumCode<SaleQuotationStatus>.Of(SaleQuotationStatus.Accepted);
    private static readonly string Rejected  = EnumCode<SaleQuotationStatus>.Of(SaleQuotationStatus.Rejected);
    private static readonly string Converted = EnumCode<SaleQuotationStatus>.Of(SaleQuotationStatus.Converted);

    private static readonly string NormalLine      = EnumCode<SaleQuotationLineType>.Of(SaleQuotationLineType.Normal);
    private static readonly string AlternativeLine = EnumCode<SaleQuotationLineType>.Of(SaleQuotationLineType.Alternative);
    private static readonly string RejectedLine    = EnumCode<SaleQuotationLineType>.Of(SaleQuotationLineType.Rejected);

    private static readonly string Pending          = EnumCode<SaleQuotationCustomerResponse>.Of(SaleQuotationCustomerResponse.Pending);
    private static readonly string CustomerAccepted = EnumCode<SaleQuotationCustomerResponse>.Of(SaleQuotationCustomerResponse.Accepted);
    private static readonly string CustomerRejected = EnumCode<SaleQuotationCustomerResponse>.Of(SaleQuotationCustomerResponse.Rejected);
    private static readonly string Counter          = EnumCode<SaleQuotationCustomerResponse>.Of(SaleQuotationCustomerResponse.Counter);

    private static readonly string InquiryReviewComplete = EnumCode<SaleInquiryStatus>.Of(SaleInquiryStatus.ReviewComplete);
    private static readonly string InquiryQuoted         = EnumCode<SaleInquiryStatus>.Of(SaleInquiryStatus.Quoted);

    private const string NumberPrefix = "SQ";

    private readonly DemandDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IDocumentNumberGenerator _numbers;
    private readonly IPartnerRoleLookup _partners;
    private readonly IOrganizationCurrencyService _orgCurrency;
    private readonly IPricingService _pricing;
    private readonly ISaleInquiryService _inquiries;
    private readonly ISaleOrderService _saleOrders;
    private readonly ITaxCodeLookup? _taxCodes;
    private readonly IProductVariantResolver? _variants;
    private readonly IVariantAvailabilityService? _availability;
    private readonly IExchangeRateProvider? _exchangeRates;
    private readonly ICurrencyCodeLookup? _currencyCodes;
    private readonly ISupplierNameLookupService? _partnerNames;
    private readonly ICurrencyService? _currency;
    private readonly IPartnerCurrencyDefaults? _partnerCurrencies;
    private readonly IOrgCurrencyLookup? _orgCurrencies;

    // The optional ones are optional for the reason SaleOrderService gives: production DI always supplies them
    // (Finance, Inventory, Lookups, Suppliers register them); without one, the behaviour from before it existed.
    public SaleQuotationService(
        DemandDbContext db, ITenantContext tenant, IDocumentNumberGenerator numbers, IPartnerRoleLookup partners,
        IOrganizationCurrencyService orgCurrency, IPricingService pricing,
        ISaleInquiryService inquiries, ISaleOrderService saleOrders,
        ITaxCodeLookup? taxCodes = null, IProductVariantResolver? variants = null,
        IVariantAvailabilityService? availability = null, IExchangeRateProvider? exchangeRates = null,
        ICurrencyCodeLookup? currencyCodes = null, ISupplierNameLookupService? partnerNames = null,
        ICurrencyService? currency = null, IPartnerCurrencyDefaults? partnerCurrencies = null,
        IOrgCurrencyLookup? orgCurrencies = null)
    {
        // A35 — rate lock at SENT (Finance), the customer's default currency (Suppliers), org currencies (Finance).
        _currency          = currency;
        _partnerCurrencies = partnerCurrencies;
        _orgCurrencies     = orgCurrencies;
        _db            = db;
        _tenant        = tenant;
        _numbers       = numbers;
        _partners      = partners;
        _orgCurrency   = orgCurrency;
        _pricing       = pricing;
        _inquiries     = inquiries;
        _saleOrders    = saleOrders;
        _taxCodes      = taxCodes;
        _variants      = variants;
        _availability  = availability;
        _exchangeRates = exchangeRates;
        _currencyCodes = currencyCodes;
        _partnerNames  = partnerNames;
    }

    // ── queries scoped to the caller's own organization ───────────────────────

    private IQueryable<SaleQuotation> OwnQuotations()
    {
        var org = _tenant.OrganizationId;
        return _db.SaleQuotations.Where(q => q.OrganizationId == org);
    }

    private Task<SaleQuotation?> LoadTrackedAsync(Guid uuid) =>
        OwnQuotations().Include(q => q.Lines).FirstOrDefaultAsync(q => q.UUID == uuid);

    // ── create ────────────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateSaleQuotationRequest req, int userId)
    {
        await RequireCustomerAsync(req.PartnerId);
        var (validFrom, validTo) = Validity(req.ValidFrom, req.ValidTo);
        var currencyId = await CurrencyAsync(req.CurrencyId, req.PartnerId);

        var quotation = new SaleQuotation
        {
            TraceId               = Guid.NewGuid(),
            PartnerId             = req.PartnerId,
            CustomerReference     = Text(req.CustomerReference, 50, "customer reference"),
            CustomerReferenceDate = req.CustomerReferenceDate?.Date,
            CurrencyId            = currencyId,
            ValidFrom             = validFrom,
            ValidTo               = validTo,
            Status                = Draft,
            PaymentTerms          = Text(req.PaymentTerms, 200, "payment terms"),
            DeliveryTerms         = Text(req.DeliveryTerms, 200, "delivery terms"),
            Notes                 = Text(req.Notes, 2000, "notes"),
            InternalNotes         = Text(req.InternalNotes, 2000, "internal notes"),
            CreatedBy             = userId,
            CreatedDate           = DateTime.UtcNow
        };

        var context = await NewContextAsync(quotation, req.Lines.Select(l => l.VariantUuid));
        var built = new List<(SaleQuotationLine Line, SaleQuotationLineRequest Req)>(req.Lines.Count);
        foreach (var lineReq in req.Lines)
        {
            var line = new SaleQuotationLine { LineNumber = built.Count + 1, CreatedDate = DateTime.UtcNow };
            await ApplyLineAsync(line, lineReq, context);
            quotation.Lines.Add(line);
            built.Add((line, lineReq));
        }
        foreach (var (line, lineReq) in built)
            LinkAlternative(line, lineReq, quotation);

        ApplyTotals(quotation, await AmountDecimalsAsync(quotation.CurrencyId));
        quotation.QuotationNumber = await _numbers.NextAsync(NumberPrefix, DateTime.UtcNow);

        _db.SaleQuotations.Add(quotation);
        await _db.SaveChangesAsync();
        return quotation.UUID;
    }

    /// <summary>PC-08 — API-CONTRACT §4.1's mapping; the inquiry's QUOTED status commits in the same save.</summary>
    public async Task<Guid> CreateFromInquiryAsync(Guid inquiryUuid, CreateSaleQuotationFromInquiryRequest req, int userId)
    {
        var org = _tenant.OrganizationId;
        var inquiry = await _db.SaleInquiries.Include(i => i.Lines)
                          .FirstOrDefaultAsync(i => i.UUID == inquiryUuid && i.OrganizationId == org)
                      ?? throw new NotFoundException("Sale inquiry", inquiryUuid);

        if (inquiry.Status == InquiryQuoted)
            throw new ConflictException($"Inquiry {inquiry.InquiryNumber} has already been quoted.");
        if (inquiry.Status != InquiryReviewComplete)
            throw new BadRequestException(
                $"Inquiry {inquiry.InquiryNumber} is {inquiry.Status}: only a REVIEW_COMPLETE inquiry can be turned into a quotation.");
        if (inquiry.Lines.Count == 0)
            throw new BadRequestException($"Inquiry {inquiry.InquiryNumber} has no lines to quote.");

        await RequireCustomerAsync(inquiry.PartnerId);
        var (validFrom, validTo) = Validity(req.ValidFrom, req.ValidTo);
        var currencyId = await CurrencyAsync(req.CurrencyId, inquiry.PartnerId, inherited: inquiry.CurrencyId);

        var quotation = new SaleQuotation
        {
            TraceId               = Guid.NewGuid(),
            PartnerId             = inquiry.PartnerId,
            CustomerReference     = inquiry.CustomerReference,
            CustomerReferenceDate = inquiry.CustomerReferenceDate,
            SourceInquiryId       = inquiry.Id,
            CurrencyId            = currencyId,
            ValidFrom             = validFrom,
            ValidTo               = validTo,
            Status                = Draft,
            PaymentTerms          = Text(req.PaymentTerms, 200, "payment terms"),
            DeliveryTerms         = Text(req.DeliveryTerms, 200, "delivery terms"),
            Notes                 = Text(req.Notes, 2000, "notes"),
            InternalNotes         = Text(req.InternalNotes, 2000, "internal notes"),
            CreatedBy             = userId,
            CreatedDate           = DateTime.UtcNow
        };

        var context = await NewContextAsync(quotation,
            inquiry.Lines.SelectMany(l => new[] { l.VariantUuid, l.AlternativeVariantUuid }));

        // A generated line has no tax code of its own: the organization's default sales code, when it has one,
        // snapshotted like any other — the seller reviews every line in DRAFT before sending.
        var defaultTax = _taxCodes is null ? null : await _taxCodes.GetDefaultAsync(TaxCodeUsage.Sales);

        var number = 0;
        foreach (var src in inquiry.Lines.OrderBy(l => l.LineNumber))
        {
            var label = $"Inquiry line {src.LineNumber} ({src.ProductDescription})";

            if (src.LineStatus is "CAN_SUPPLY" or "PARTIAL")
            {
                if (src.VariantUuid is not { } variant || variant == Guid.Empty)
                    throw new BadRequestException(
                        $"{label} can be supplied but names no catalog item — identify the catalog item first.");
                var qty = src.LineStatus == "PARTIAL" ? src.CanSupplyQuantity ?? 0m : src.RequestedQuantity;

                var line = await GeneratedLineAsync(++number, src, label, context, new SaleQuotationLineRequest
                {
                    LineType = NormalLine, VariantUuid = variant, Quantity = qty, ProductDescription = src.ProductDescription,
                    UomCode = src.RequestedUomCode, PromisedDeliveryDate = src.EstimatedDeliveryDate,
                    TaxCodeUuid = defaultTax?.Uuid, Notes = src.Notes
                });
                // A34 D-15 — the inquiry line's calculation travels with the offered item (same variant).
                line.CalculatedLeadTimeDays = src.CalculatedLeadTimeDays;
                line.CalculatedDeliveryDate = src.CalculatedDeliveryDate;
                line.LeadTimeCalculatedAt   = src.LeadTimeCalculatedAt;
                quotation.Lines.Add(line);
            }
            else if (src.LineStatus == "CANNOT_SUPPLY")
            {
                // Copied as evaluated — the reason was active when the inquiry was reviewed, and a reason
                // deactivated since stays on historical data (BR-C5-03) rather than blocking the quotation.
                var rejected = new SaleQuotationLine
                {
                    LineNumber          = ++number,
                    SourceInquiryLineId = src.Id,
                    VariantUuid         = src.VariantUuid,
                    ProductDescription  = Text(src.ProductDescription, 500, "product description") ?? string.Empty,
                    Quantity            = src.RequestedQuantity,
                    UomCode             = Text(src.RequestedUomCode, 20, "unit of measure"),
                    LineType            = RejectedLine,
                    RejectionReasonId   = src.RejectionReasonId
                                          ?? throw new BadRequestException($"{label} cannot be supplied but has no rejection reason."),
                    RejectionNotes      = src.RejectionNotes,
                    CreatedDate         = DateTime.UtcNow
                };
                quotation.Lines.Add(rejected);

                if (src.AlternativeVariantUuid is { } alternative && alternative != Guid.Empty)
                {
                    var alt = await GeneratedLineAsync(++number, src, label, context, new SaleQuotationLineRequest
                    {
                        LineType = AlternativeLine, VariantUuid = alternative, Quantity = src.RequestedQuantity,
                        UomCode = src.RequestedUomCode, AlternativeNotes = src.AlternativeNotes, TaxCodeUuid = defaultTax?.Uuid
                    });
                    alt.AlternativeForLine = rejected;
                    quotation.Lines.Add(alt);
                }
            }
            else
            {
                throw new BadRequestException($"{label} is {src.LineStatus}: every line must be evaluated before quoting.");
            }
        }

        ApplyTotals(quotation, await AmountDecimalsAsync(quotation.CurrencyId));
        quotation.QuotationNumber = await _numbers.NextAsync(NumberPrefix, DateTime.UtcNow);

        // BR-C1-07 — QUOTED on the same tracked inquiry; the one save below commits both (a concurrent
        // create-quotation loses on the inquiry's Status concurrency token → 409).
        await _inquiries.MarkQuotedAsync(inquiryUuid, userId);

        _db.SaleQuotations.Add(quotation);
        await _db.SaveChangesAsync();
        return quotation.UUID;
    }

    private async Task<SaleQuotationLine> GeneratedLineAsync(
        int number, SaleInquiryLine src, string label, LineContext context, SaleQuotationLineRequest req)
    {
        var line = new SaleQuotationLine { LineNumber = number, SourceInquiryLineId = src.Id, CreatedDate = DateTime.UtcNow };
        try
        {
            await ApplyLineAsync(line, req, context);
        }
        catch (BadRequestException ex)
        {
            throw new BadRequestException($"{label}: {ex.Message}");
        }
        return line;
    }

    // ── header ────────────────────────────────────────────────────────────────

    public async Task<bool> UpdateAsync(Guid uuid, UpdateSaleQuotationRequest req, int userId)
    {
        var quotation = await LoadTrackedAsync(uuid);
        if (quotation is null) return false;
        RequireEditable(quotation);

        var (validFrom, validTo) = Validity(req.ValidFrom, req.ValidTo);
        var currencyId = await CurrencyAsync(req.CurrencyId, quotation.PartnerId, current: quotation.CurrencyId);
        // Prices were quoted in the old currency; silently relabelling them would misprice every line.
        if (currencyId != quotation.CurrencyId && quotation.Lines.Any(l => l.LineType != RejectedLine))
            throw new BadRequestException(
                "The currency cannot change while the quotation has priced lines — they were quoted in the current currency. " +
                "Remove the lines first, or make a new quotation.");

        quotation.CustomerReference     = Text(req.CustomerReference, 50, "customer reference");
        quotation.CustomerReferenceDate = req.CustomerReferenceDate?.Date;
        quotation.CurrencyId            = currencyId;
        quotation.ValidFrom             = validFrom;
        quotation.ValidTo               = validTo;
        quotation.PaymentTerms          = Text(req.PaymentTerms, 200, "payment terms");
        quotation.DeliveryTerms         = Text(req.DeliveryTerms, 200, "delivery terms");
        quotation.Notes                 = Text(req.Notes, 2000, "notes");
        quotation.InternalNotes         = Text(req.InternalNotes, 2000, "internal notes");
        Touch(quotation, userId);

        await _db.SaveChangesAsync();
        return true;
    }

    // ── lines ─────────────────────────────────────────────────────────────────

    public async Task<Guid?> AddLineAsync(Guid uuid, SaleQuotationLineRequest req, int userId)
    {
        var quotation = await LoadTrackedAsync(uuid);
        if (quotation is null) return null;
        RequireEditable(quotation);

        var context = await NewContextAsync(quotation, [req.VariantUuid]);
        var line = new SaleQuotationLine
        {
            LineNumber  = quotation.Lines.Count == 0 ? 1 : quotation.Lines.Max(l => l.LineNumber) + 1,
            CreatedDate = DateTime.UtcNow
        };
        await ApplyLineAsync(line, req, context);
        LinkAlternative(line, req, quotation);
        quotation.Lines.Add(line);

        ApplyTotals(quotation, await AmountDecimalsAsync(quotation.CurrencyId));
        Touch(quotation, userId);
        await _db.SaveChangesAsync();
        return line.UUID;
    }

    public async Task<bool> UpdateLineAsync(Guid uuid, Guid lineUuid, SaleQuotationLineRequest req, int userId)
    {
        var quotation = await LoadTrackedAsync(uuid);
        var line = quotation?.Lines.FirstOrDefault(l => l.UUID == lineUuid);
        if (quotation is null || line is null) return false;
        RequireEditable(quotation);

        if (line.LineType == RejectedLine
            && !string.Equals(req.LineType?.Trim(), RejectedLine, StringComparison.OrdinalIgnoreCase)
            && quotation.Lines.Any(l => l.AlternativeForLineId == line.Id && l.Id != 0))
            throw new BadRequestException(
                $"Line {line.LineNumber} has alternatives offered for it, so it must stay REJECTED. Remove its alternatives first.");

        var context = await NewContextAsync(quotation, [req.VariantUuid]);
        await ApplyLineAsync(line, req, context);
        LinkAlternative(line, req, quotation);
        line.ModifiedDate = DateTime.UtcNow;

        ApplyTotals(quotation, await AmountDecimalsAsync(quotation.CurrencyId));
        Touch(quotation, userId);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteLineAsync(Guid uuid, Guid lineUuid, int userId)
    {
        var quotation = await LoadTrackedAsync(uuid);
        var line = quotation?.Lines.FirstOrDefault(l => l.UUID == lineUuid);
        if (quotation is null || line is null) return false;
        RequireEditable(quotation);

        if (quotation.Lines.Any(l => l.AlternativeForLineId == line.Id))
            throw new BadRequestException(
                $"Line {line.LineNumber} has alternatives offered for it. Remove its alternatives first.");

        quotation.Lines.Remove(line);
        _db.SaleQuotationLines.Remove(line);

        ApplyTotals(quotation, await AmountDecimalsAsync(quotation.CurrencyId));
        Touch(quotation, userId);
        await _db.SaveChangesAsync();
        return true;
    }

    // ── transitions ───────────────────────────────────────────────────────────

    public async Task<bool> SendAsync(Guid uuid, int userId)
    {
        var quotation = await LoadTrackedAsync(uuid);
        if (quotation is null) return false;

        if (quotation.Status != Draft)
            throw new BadRequestException($"Only a DRAFT quotation can be sent; {quotation.QuotationNumber} is {quotation.Status}.");
        if (!quotation.Lines.Any(l => l.LineType == NormalLine || l.LineType == AlternativeLine))
            throw new BadRequestException(
                "A quotation needs at least one NORMAL or ALTERNATIVE line before it can be sent — there is nothing to offer.");

        // A35 P3-11 (D-5, D-12) — the rate of the sent date, against the sale base; refused (400) before anything changes
        // when the quotation is in another currency and no rate is on file. Never recalculated afterwards (BR-C5-06).
        var sentAt = DateTime.UtcNow;
        var rateLock = await DemandCurrency.LockAsync(_currency, _orgCurrency, _tenant.OrganizationId, quotation.CurrencyId,
            DemandCurrency.Today(sentAt), TransactionDomain.Sale);
        if (rateLock is not null)
            DemandCurrency.Apply(quotation, rateLock, sentAt);

        quotation.Status       = Sent;
        quotation.SentAt       = sentAt;
        quotation.SentByUserId = userId;
        Touch(quotation, userId);

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RecordCustomerResponseAsync(Guid uuid, Guid lineUuid, RecordCustomerResponseRequest req, int userId)
    {
        var quotation = await LoadTrackedAsync(uuid);
        var line = quotation?.Lines.FirstOrDefault(l => l.UUID == lineUuid);
        if (quotation is null || line is null) return false;

        if (quotation.Status != Sent)
            throw new BadRequestException(
                $"Customer responses can only be recorded while the quotation is SENT; {quotation.QuotationNumber} is {quotation.Status}.");
        if (line.LineType == RejectedLine)
            throw new BadRequestException(
                $"Line {line.LineNumber} was rejected by the seller — there is nothing for the customer to respond to.");
        if (!EnumCode<SaleQuotationCustomerResponse>.TryParse(req.Response?.Trim().ToUpperInvariant(), out var response))
            throw new BadRequestException($"'{req.Response}' is not a customer response. Use PENDING, ACCEPTED, REJECTED or COUNTER.");
        if (req.AcceptCounterPrice && response != SaleQuotationCustomerResponse.Accepted)
            throw new BadRequestException("acceptCounterPrice only goes with an ACCEPTED response.");

        var code = EnumCode<SaleQuotationCustomerResponse>.Of(response);
        switch (response)
        {
            case SaleQuotationCustomerResponse.Counter:
                if (req.CounterPrice is not { } counter || counter <= 0m)
                    throw new BadRequestException("A COUNTER response needs the customer's counter price, greater than zero.");
                RequirePrice(counter, "The counter price");
                line.CustomerCounterPrice = counter;
                break;

            case SaleQuotationCustomerResponse.Accepted when req.AcceptCounterPrice:
                // §4.5 — the seller takes the customer's counter: the only price change after DRAFT.
                if (line.CustomerResponse != Counter || line.CustomerCounterPrice is not { } agreed)
                    throw new BadRequestException(
                        $"Line {line.LineNumber} has no counter price to accept — record the customer's COUNTER first.");
                line.UnitPrice = agreed;
                ComputeLine(line, await AmountDecimalsAsync(quotation.CurrencyId));
                // A35 — the line's base amounts follow its new price at the rate locked on SENT (never re-looked up, BR-C5-06).
                if (quotation.ExchangeRate is { } locked && quotation.BaseCurrencyId is { } baseId)
                    await RebaseLineAsync(quotation, line, locked, baseId);
                break;

            default:
                line.CustomerCounterPrice = null;
                break;
        }

        line.CustomerResponse      = code;
        line.CustomerResponseDate  = response == SaleQuotationCustomerResponse.Pending ? null : (req.ResponseDate ?? DateTime.UtcNow).Date;
        line.CustomerResponseNotes = Text(req.Notes, 500, "response notes");
        line.ModifiedDate          = DateTime.UtcNow;

        ApplyTotals(quotation, await AmountDecimalsAsync(quotation.CurrencyId));
        Touch(quotation, userId);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> AcceptAsync(Guid uuid, int userId)
    {
        var quotation = await LoadTrackedAsync(uuid);
        if (quotation is null) return false;

        if (quotation.Status != Sent)
            throw new BadRequestException($"Only a SENT quotation can be accepted; {quotation.QuotationNumber} is {quotation.Status}.");
        if (!quotation.Lines.Any(l => l.LineType != RejectedLine && l.CustomerResponse == CustomerAccepted))
            throw new BadRequestException("Record at least one line the customer ACCEPTED before accepting the quotation.");

        quotation.Status = Accepted;
        Touch(quotation, userId);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RejectAsync(Guid uuid, string? reason, int userId)
    {
        var quotation = await LoadTrackedAsync(uuid);
        if (quotation is null) return false;

        if (quotation.Status != Sent)
            throw new BadRequestException($"Only a SENT quotation can be rejected; {quotation.QuotationNumber} is {quotation.Status}.");
        var open = quotation.Lines.Where(l => l.LineType != RejectedLine && l.CustomerResponse != CustomerRejected)
                                  .Select(l => l.LineNumber).OrderBy(n => n).ToList();
        if (open.Count > 0)
            throw new BadRequestException(
                $"The customer has not rejected every offered line (still open: line {string.Join(", ", open)}). " +
                "Record their response on each line first.");

        quotation.Status = Rejected;
        // The quotation has no rejection-reason column; the reason is kept with the internal notes.
        if (!string.IsNullOrWhiteSpace(reason))
        {
            var entry = $"Rejected by the customer on {DateTime.UtcNow:yyyy-MM-dd}: {reason.Trim()}";
            var notes = string.IsNullOrWhiteSpace(quotation.InternalNotes) ? entry : $"{quotation.InternalNotes}\n{entry}";
            quotation.InternalNotes = notes.Length <= 2000 ? notes : notes[..2000];
        }
        Touch(quotation, userId);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// BR-C2-11 / PD-04 — sets the tracked quotation CONVERTED and hands the ACCEPTED lines (at their quoted price) to
    /// <see cref="ISaleOrderService.CreateFromQuotationAsync"/>, whose single SaveChangesAsync commits both. A second
    /// conversion is a 409: here when it is already CONVERTED, and in a race on the Status concurrency token and the
    /// unique SaleOrders.SourceQuotationId.
    /// </summary>
    public async Task<Guid?> ConvertToOrderAsync(Guid uuid, ConvertSaleQuotationToOrderRequest req, int userId)
    {
        var quotation = await LoadTrackedAsync(uuid);
        if (quotation is null) return null;

        if (quotation.Status == Converted)
            throw new ConflictException($"Quotation {quotation.QuotationNumber} has already been converted to a sale order.");
        if (quotation.Status != Accepted)
            throw new BadRequestException(
                $"Only an ACCEPTED quotation can be converted to a sale order; {quotation.QuotationNumber} is {quotation.Status}.");

        // T-C2-13 — a COUNTER stays out until the seller accepts its price (the line is then ACCEPTED).
        var accepted = quotation.Lines
            .Where(l => l.LineType != RejectedLine && l.CustomerResponse == CustomerAccepted && l.VariantUuid is not null)
            .OrderBy(l => l.LineNumber)
            .ToList();
        if (accepted.Count == 0)
            throw new BadRequestException("No accepted lines to convert.");

        var (previousBy, previousDate) = (quotation.ModifiedBy, quotation.ModifiedDate);
        quotation.Status = Converted;
        Touch(quotation, userId);

        var command = new CreateSaleOrderFromQuotationCommand
        {
            SourceQuotationUuid    = quotation.UUID,
            OrderDate              = req.OrderDate?.Date,
            ExpectedDeliveryDate   = req.ExpectedDeliveryDate?.Date,
            DeliveryMode           = req.DeliveryMode,
            ShippingAddressId      = req.ShippingAddressId,
            IntimationDepartmentId = req.IntimationDepartmentId,
            Notes                  = req.Notes,
            CustomerPoReference    = req.CustomerPoReference,
            CustomerPoDate         = req.CustomerPoDate?.Date,
            Lines = accepted.Select(l => new QuotedSaleOrderLine
            {
                VariantUuid     = l.VariantUuid!.Value,
                Quantity        = l.Quantity,
                UnitPrice       = l.UnitPrice,
                DiscountPercent = l.DiscountPercent,
                TaxPercent      = l.TaxPercent,
                TaxCodeUuid     = l.TaxCodeUuid,
                // A34 D-15 — promised → the order line's manual date; the calculation is copied.
                ManualDeliveryDate     = l.PromisedDeliveryDate,
                CalculatedLeadTimeDays = l.CalculatedLeadTimeDays,
                CalculatedDeliveryDate = l.CalculatedDeliveryDate,
                LeadTimeCalculatedAt   = l.LeadTimeCalculatedAt
            }).ToList()
        };

        try
        {
            return await _saleOrders.CreateFromQuotationAsync(command, userId);
        }
        catch
        {
            // Nothing was saved; leave the tracked quotation as it was so nothing later in this scope commits it.
            quotation.Status       = Accepted;
            quotation.ModifiedBy   = previousBy;
            quotation.ModifiedDate = previousDate;
            throw;
        }
    }

    /// <summary>§4.4 — "create a new quotation, optionally copying lines": a new DRAFT, prices as quoted, responses cleared.</summary>
    public async Task<Guid?> CopyAsync(Guid uuid, int userId)
    {
        var source = await OwnQuotations().AsNoTracking().Include(q => q.Lines).FirstOrDefaultAsync(q => q.UUID == uuid);
        if (source is null) return null;

        await RequireCustomerAsync(source.PartnerId);

        var today = DateTime.UtcNow.Date;
        var copy = new SaleQuotation
        {
            TraceId               = Guid.NewGuid(),
            PartnerId             = source.PartnerId,
            CustomerReference     = source.CustomerReference,
            CustomerReferenceDate = source.CustomerReferenceDate,
            SourceInquiryId       = source.SourceInquiryId,
            CurrencyId            = source.CurrencyId,
            // The same validity length, starting today — the original's window may well have passed.
            ValidFrom             = today,
            ValidTo               = today + (source.ValidTo.Date - source.ValidFrom.Date),
            Status                = Draft,
            PaymentTerms          = source.PaymentTerms,
            DeliveryTerms         = source.DeliveryTerms,
            Notes                 = source.Notes,
            InternalNotes         = source.InternalNotes,
            CreatedBy             = userId,
            CreatedDate           = DateTime.UtcNow
        };

        var copyDecimals = await AmountDecimalsAsync(copy.CurrencyId);
        var map = new Dictionary<int, SaleQuotationLine>();
        foreach (var l in source.Lines.OrderBy(l => l.LineNumber))
        {
            var line = new SaleQuotationLine
            {
                LineNumber           = l.LineNumber,
                SourceInquiryLineId  = l.SourceInquiryLineId,
                VariantUuid          = l.VariantUuid,
                ProductDescription   = l.ProductDescription,
                Quantity             = l.Quantity,
                UomCode              = l.UomCode,
                UnitPrice            = l.UnitPrice,
                DiscountPercent      = l.DiscountPercent,
                TaxPercent           = l.TaxPercent,
                TaxCodeUuid          = l.TaxCodeUuid,
                TaxCode              = l.TaxCode,
                PromisedDeliveryDate = l.PromisedDeliveryDate,
                CalculatedLeadTimeDays = l.CalculatedLeadTimeDays,
                CalculatedDeliveryDate = l.CalculatedDeliveryDate,
                LeadTimeCalculatedAt   = l.LeadTimeCalculatedAt,
                LineType             = l.LineType,
                RejectionReasonId    = l.RejectionReasonId,
                RejectionNotes       = l.RejectionNotes,
                AlternativeNotes     = l.AlternativeNotes,
                Notes                = l.Notes,
                CreatedDate          = DateTime.UtcNow
            };
            ComputeLine(line, copyDecimals);
            map[l.Id] = line;
            copy.Lines.Add(line);
        }
        foreach (var l in source.Lines.Where(l => l.AlternativeForLineId is not null))
            if (map.TryGetValue(l.AlternativeForLineId!.Value, out var target))
                map[l.Id].AlternativeForLine = target;

        ApplyTotals(copy, copyDecimals);
        copy.QuotationNumber = await _numbers.NextAsync(NumberPrefix, DateTime.UtcNow);

        _db.SaleQuotations.Add(copy);
        await _db.SaveChangesAsync();
        return copy.UUID;
    }

    // ── reads ─────────────────────────────────────────────────────────────────

    public async Task<SaleQuotationModel?> GetByIdAsync(Guid uuid)
    {
        var q = await OwnQuotations().AsNoTracking()
            .Include(x => x.Lines).ThenInclude(l => l.RejectionReason)
            .Include(x => x.SourceInquiry)
            .FirstOrDefaultAsync(x => x.UUID == uuid);
        if (q is null) return null;

        var org = _tenant.OrganizationId;
        var order = await _db.SaleOrders.AsNoTracking()
            .Where(o => o.SourceQuotationId == q.Id && o.OrganizationId == org)
            .Select(o => new SalesDocumentLinkModel { Uuid = o.UUID, Number = o.SoNumber, Status = o.Status })
            .FirstOrDefaultAsync();

        var inquiryLineIds = q.Lines.Where(l => l.SourceInquiryLineId is not null).Select(l => l.SourceInquiryLineId!.Value).Distinct().ToList();
        var inquiryLineUuids = inquiryLineIds.Count == 0
            ? new Dictionary<int, Guid>()
            : await _db.SaleInquiryLines.AsNoTracking().Where(l => inquiryLineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.UUID);

        var variants = await DescribeAsync(q.Lines.Select(l => l.VariantUuid));
        var linesById = q.Lines.ToDictionary(l => l.Id);
        var partner = await _partners.GetAsync(q.PartnerId);

        var lines = q.Lines.OrderBy(l => l.LineNumber).Select(l =>
        {
            var v = l.VariantUuid is { } vu ? variants.GetValueOrDefault(vu) : null;
            var altFor = l.AlternativeForLineId is { } a ? linesById.GetValueOrDefault(a) : null;
            return new SaleQuotationLineModel
            {
                Uuid                       = l.UUID,
                LineNumber                 = l.LineNumber,
                SourceInquiryLineUuid      = l.SourceInquiryLineId is { } s && inquiryLineUuids.TryGetValue(s, out var su) ? su : null,
                VariantUuid                = l.VariantUuid,
                VariantSku                 = v?.Sku,
                VariantName                = v?.DisplayName,
                ProductDescription         = l.ProductDescription,
                Quantity                   = l.Quantity,
                UomCode                    = l.UomCode,
                UnitPrice                  = l.UnitPrice,
                DiscountPercent            = l.DiscountPercent,
                TaxPercent                 = l.TaxPercent,
                TaxCodeUuid                = l.TaxCodeUuid,
                TaxCode                    = l.TaxCode,
                TaxAmount                  = l.TaxAmount,
                LineTotal                  = l.LineTotal,
                UnitPriceBase              = l.UnitPriceBase,
                DiscountAmountBase         = l.DiscountAmountBase,
                TaxAmountBase              = l.TaxAmountBase,
                LineTotalBase              = l.LineTotalBase,
                PromisedDeliveryDate       = l.PromisedDeliveryDate,
                LineType                   = l.LineType,
                RejectionReasonUuid        = l.RejectionReason?.UUID,
                RejectionReasonCode        = l.RejectionReason?.Code,
                RejectionReasonDescription = l.RejectionReason?.Description,
                RejectionNotes             = l.RejectionNotes,
                AlternativeForLineUuid     = altFor?.UUID,
                AlternativeForLineNumber   = altFor?.LineNumber,
                AlternativeNotes           = l.AlternativeNotes,
                CustomerResponse           = l.CustomerResponse,
                CustomerResponseDate       = l.CustomerResponseDate,
                CustomerResponseNotes      = l.CustomerResponseNotes,
                CustomerCounterPrice       = l.CustomerCounterPrice,
                Notes                      = l.Notes,
                CalculatedLeadTimeDays     = l.CalculatedLeadTimeDays,
                CalculatedDeliveryDate     = l.CalculatedDeliveryDate,
                LeadTimeCalculatedAt       = l.LeadTimeCalculatedAt,
                EffectiveDeliveryDate      = DeliveryDateSources.Effective(l.PromisedDeliveryDate, l.CalculatedDeliveryDate),
                DeliveryDateSource         = DeliveryDateSources.Of(l.PromisedDeliveryDate, l.CalculatedDeliveryDate)
            };
        }).ToList();

        return new SaleQuotationModel
        {
            Uuid                  = q.UUID,
            TraceId               = q.TraceId,
            QuotationNumber       = q.QuotationNumber,
            PartnerId             = q.PartnerId,
            PartnerName           = partner?.Name,
            CustomerReference     = q.CustomerReference,
            CustomerReferenceDate = q.CustomerReferenceDate,
            SourceInquiry         = q.SourceInquiry is { } si
                ? new SalesDocumentLinkModel { Uuid = si.UUID, Number = si.InquiryNumber, Status = si.Status }
                : null,
            SaleOrder             = order,
            CurrencyId            = q.CurrencyId,
            CurrencyCode          = await CurrencyCodeAsync(q.CurrencyId),
            ValidFrom             = q.ValidFrom,
            ValidTo               = q.ValidTo,
            Status                = q.Status,
            PaymentTerms          = q.PaymentTerms,
            DeliveryTerms         = q.DeliveryTerms,
            Subtotal              = q.Subtotal,
            TaxAmount             = q.TaxAmount,
            DiscountAmount        = q.DiscountAmount,
            GrandTotal            = q.GrandTotal,
            ExchangeRate          = q.ExchangeRate,
            BaseCurrencyId        = q.BaseCurrencyId,
            BaseCurrencyCode      = q.BaseCurrencyId is { } baseId ? await CurrencyCodeAsync(baseId) : null,
            RateLockedAt          = q.RateLockedAt,
            Notes                 = q.Notes,
            InternalNotes         = q.InternalNotes,
            SentAt                = q.SentAt,
            SentByUserId          = q.SentByUserId,
            CreatedBy             = q.CreatedBy,
            CreatedDate           = q.CreatedDate,
            ModifiedDate          = q.ModifiedDate,
            IsEditable            = q.Status == Draft,
            AllowedActions        = AllowedActions(q),
            Lines                 = lines
        };
    }

    public async Task<PaginatedResponse<SaleQuotationListItemModel>> GetListAsync(SaleQuotationListFilter filter)
    {
        var query = OwnQuotations().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var status = filter.Status.Trim().ToUpperInvariant();
            query = query.Where(q => q.Status == status);
        }
        if (filter.PartnerId is { } partnerId)
            query = query.Where(q => q.PartnerId == partnerId);
        if (filter.SourceInquiryUuid is { } inquiryUuid)
            query = query.Where(q => q.SourceInquiry != null && q.SourceInquiry.UUID == inquiryUuid);
        if (filter.ValidToFrom is { } from)
            query = query.Where(q => q.ValidTo >= from.Date);
        if (filter.ValidToTo is { } to)
            query = query.Where(q => q.ValidTo <= to.Date);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(q => q.QuotationNumber.Contains(term) || (q.CustomerReference != null && q.CustomerReference.Contains(term)));
        }

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var rows = await query
            .OrderByDescending(q => q.CreatedDate).ThenByDescending(q => q.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(q => new SaleQuotationListItemModel
            {
                Uuid                = q.UUID,
                QuotationNumber     = q.QuotationNumber,
                PartnerId           = q.PartnerId,
                CustomerReference   = q.CustomerReference,
                SourceInquiryUuid   = q.SourceInquiry != null ? q.SourceInquiry.UUID : null,
                SourceInquiryNumber = q.SourceInquiry != null ? q.SourceInquiry.InquiryNumber : null,
                CurrencyId          = q.CurrencyId,
                ValidFrom           = q.ValidFrom,
                ValidTo             = q.ValidTo,
                Status              = q.Status,
                GrandTotal          = q.GrandTotal,
                LineCount           = q.Lines.Count,
                SentAt              = q.SentAt,
                CreatedDate         = q.CreatedDate
            })
            .ToListAsync();

        if (rows.Count > 0)
        {
            var names = _partnerNames is null
                ? new Dictionary<Guid, string>()
                : await _partnerNames.GetNamesAsync(rows.Select(r => r.PartnerId).Distinct().ToList());
            var codes = new Dictionary<Guid, string?>();
            foreach (var currencyId in rows.Select(r => r.CurrencyId).Distinct())
                codes[currencyId] = await CurrencyCodeAsync(currencyId);
            foreach (var row in rows)
            {
                row.PartnerName  = names.GetValueOrDefault(row.PartnerId);
                row.CurrencyCode = codes.GetValueOrDefault(row.CurrencyId);
            }
        }

        return new PaginatedResponse<SaleQuotationListItemModel>
        {
            Data         = rows,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    /// <summary>API-CONTRACT §5 — from state only; the frontend ANDs each with the caller's permission.</summary>
    private static List<string> AllowedActions(SaleQuotation q)
    {
        var actions = new List<string>();
        if (q.Status == Draft && q.Lines.Any(l => l.LineType == NormalLine || l.LineType == AlternativeLine))
            actions.Add("SEND");
        if (q.Status == Sent)
            actions.AddRange(["RECORD_RESPONSE", "ACCEPT", "REJECT"]);
        if (q.Status == Accepted)
            actions.Add("CONVERT");
        actions.Add("COPY");
        return actions;
    }

    // ── line building ─────────────────────────────────────────────────────────

    /// <summary>What one request's lines share: the quotation, catalog descriptions and per-request caches.</summary>
    private sealed class LineContext(SaleQuotation quotation, IReadOnlyDictionary<Guid, VariantDescription> variants)
    {
        public SaleQuotation Quotation { get; } = quotation;
        public IReadOnlyDictionary<Guid, VariantDescription> Variants { get; } = variants;
        public Dictionary<Guid, TaxCodeInfo?> TaxCodes      { get; } = [];
        public Dictionary<Guid, string?>      CurrencyCodes { get; } = [];
        public (Guid? Id, bool Known) BaseCurrency { get; set; }
        /// <summary>A35 D-13 — the quotation currency's decimals for line amounts.</summary>
        public int Decimals { get; set; } = CurrencyConventions.DefaultDecimalPlaces;
    }

    private async Task<LineContext> NewContextAsync(SaleQuotation quotation, IEnumerable<Guid?> variantUuids) =>
        new(quotation, await DescribeAsync(variantUuids)) { Decimals = await AmountDecimalsAsync(quotation.CurrencyId) };

    private async Task<IReadOnlyDictionary<Guid, VariantDescription>> DescribeAsync(IEnumerable<Guid?> variantUuids)
    {
        var ids = variantUuids.Where(v => v is { } g && g != Guid.Empty).Select(v => v!.Value).Distinct().ToList();
        if (_variants is null || ids.Count == 0) return new Dictionary<Guid, VariantDescription>();
        return await _variants.DescribeVariantsAsync(ids) ?? new Dictionary<Guid, VariantDescription>();
    }

    /// <summary>
    /// Validates one line request and writes it onto <paramref name="line"/> (new or existing), totals included. The
    /// ALTERNATIVE link is resolved separately (<see cref="LinkAlternative"/>), once every line of a create exists.
    /// </summary>
    private async Task ApplyLineAsync(SaleQuotationLine line, SaleQuotationLineRequest req, LineContext context)
    {
        var typeText = string.IsNullOrWhiteSpace(req.LineType) ? NormalLine : req.LineType.Trim().ToUpperInvariant();
        if (!EnumCode<SaleQuotationLineType>.TryParse(typeText, out var type))
            throw new BadRequestException($"'{req.LineType}' is not a quotation line type. Use NORMAL, ALTERNATIVE or REJECTED.");

        var variant = req.VariantUuid is { } vu && vu != Guid.Empty ? vu : (Guid?)null;
        var described = variant is { } d ? context.Variants.GetValueOrDefault(d) : null;

        // A34 D-15 — a lead time is calculated for one variant and quantity: once either changes it no longer holds.
        if (line.VariantUuid != variant || line.Quantity != req.Quantity)
        {
            line.CalculatedLeadTimeDays = null;
            line.CalculatedDeliveryDate = null;
            line.LeadTimeCalculatedAt   = null;
        }

        line.LineType             = EnumCode<SaleQuotationLineType>.Of(type);
        line.VariantUuid          = variant;
        line.UomCode              = Text(req.UomCode, 20, "unit of measure") ?? described?.UomCode;
        line.PromisedDeliveryDate = req.PromisedDeliveryDate?.Date;
        line.Notes                = Text(req.Notes, 1000, "line notes");

        if (type == SaleQuotationLineType.Rejected)
        {
            if (req.Quantity < 0m)
                throw new BadRequestException("A line's quantity cannot be negative.");
            if (req.RejectionReasonUuid is not { } reasonUuid || reasonUuid == Guid.Empty)
                throw new BadRequestException("A REJECTED line needs a rejection reason.");
            line.RejectionReasonId = await ActiveReasonIdAsync(reasonUuid);
            line.RejectionNotes    = Text(req.RejectionNotes, 500, "rejection notes");
            line.Quantity          = req.Quantity;
            line.ProductDescription = Text(req.ProductDescription, 500, "product description") ?? described?.DisplayName
                ?? throw new BadRequestException("A REJECTED line with no catalog item needs a product description.");
            // Nothing is offered, so nothing is priced; the customer has nothing to respond to.
            line.UnitPrice = 0m; line.DiscountPercent = 0m; line.TaxPercent = 0m; line.TaxCodeUuid = null; line.TaxCode = null;
            line.AlternativeForLineId = null; line.AlternativeForLine = null; line.AlternativeNotes = null;
            line.CustomerResponse = Pending; line.CustomerResponseDate = null; line.CustomerCounterPrice = null;
            ComputeLine(line, context.Decimals);
            return;
        }

        if (variant is not { } variantUuid)
            throw new BadRequestException($"A {line.LineType} line needs a catalog item (variantUuid).");
        if (req.Quantity <= 0m)
            throw new BadRequestException("A quoted line's quantity must be greater than zero.");
        if (decimal.Round(req.Quantity, 4) != req.Quantity)
            throw new BadRequestException($"A line's quantity can have at most four decimal places; {req.Quantity} has more.");
        if (req.DiscountPercent is < 0m or > 100m || decimal.Round(req.DiscountPercent, 2) != req.DiscountPercent)
            throw new BadRequestException(
                $"A line's discount must be a percentage from 0 to 100 with at most two decimal places; {req.DiscountPercent} is not.");

        string? displayName = described?.DisplayName;
        if (_availability is not null)
        {
            // The SO's own channel rules, so an accepted quotation can actually be converted.
            var availability = await _availability.GetAvailabilityAsync(variantUuid);
            displayName ??= availability?.DisplayName;
            if (availability is null || !availability.IsAvailableForRetail)
                throw new BadRequestException($"{availability?.DisplayName ?? variantUuid.ToString()} is not available for retail sale.");
            if (availability.SaleOrderMinQty is > 0 && req.Quantity < availability.SaleOrderMinQty)
                throw new BadRequestException(
                    $"Quantity {req.Quantity} is below the minimum order quantity of {availability.SaleOrderMinQty} for {availability.DisplayName}.");
            if (availability.SaleOrderMaxQty is > 0 && req.Quantity > availability.SaleOrderMaxQty)
                throw new BadRequestException(
                    $"Quantity {req.Quantity} exceeds the maximum order quantity of {availability.SaleOrderMaxQty} for {availability.DisplayName}.");
        }

        var (taxCodeUuid, taxCode, taxPercent) = await ResolveTaxAsync(req, context);

        decimal unitPrice;
        if (req.UnitPrice is { } given)
        {
            RequirePrice(given, "A line's unit price");
            unitPrice = given;
        }
        else
        {
            var quotation = context.Quotation;
            var resolution = await _pricing.ResolveSalePriceAsync(variantUuid, quotation.PartnerId, req.Quantity, quotation.ValidFrom);
            if (!resolution.Found || resolution.UnitPrice is not { } resolved)
                throw new BadRequestException(
                    $"No sale price could be resolved for {displayName ?? variantUuid.ToString()} — enter a unit price for the line.");
            unitPrice = await ToQuotationCurrencyAsync(resolved, resolution.CurrencyId, context, displayName ?? variantUuid.ToString());
        }

        line.Quantity           = req.Quantity;
        line.UnitPrice          = unitPrice;
        line.DiscountPercent    = req.DiscountPercent;
        line.TaxPercent         = taxPercent;
        line.TaxCodeUuid        = taxCodeUuid;
        line.TaxCode            = taxCode;
        line.ProductDescription = Text(req.ProductDescription, 500, "product description") ?? displayName ?? variantUuid.ToString();
        line.RejectionReasonId  = null;
        line.RejectionReason    = null;
        line.RejectionNotes     = null;
        line.AlternativeNotes   = type == SaleQuotationLineType.Alternative ? Text(req.AlternativeNotes, 500, "alternative notes") : null;
        if (type != SaleQuotationLineType.Alternative)
        {
            line.AlternativeForLineId = null;
            line.AlternativeForLine   = null;
        }
        ComputeLine(line, context.Decimals);
    }

    /// <summary>
    /// BR-C2-06 — an ALTERNATIVE points to a REJECTED line of the same quotation: an existing line by uuid, or a line
    /// of the same quotation (or of the same create request) by number.
    /// </summary>
    private static void LinkAlternative(SaleQuotationLine line, SaleQuotationLineRequest req, SaleQuotation quotation)
    {
        if (line.LineType != AlternativeLine) return;

        SaleQuotationLine? target;
        if (req.AlternativeForLineUuid is { } forUuid && forUuid != Guid.Empty)
            target = quotation.Lines.FirstOrDefault(l => l.UUID == forUuid)
                     ?? throw new BadRequestException(
                         $"The line an alternative is offered for ({forUuid}) is not a line of this quotation.");
        else if (req.AlternativeForLineNumber is { } forNumber)
            target = quotation.Lines.FirstOrDefault(l => l.LineNumber == forNumber)
                     ?? throw new BadRequestException($"An alternative is offered for line {forNumber}, but this quotation has no line {forNumber}.");
        else
            throw new BadRequestException(
                "An ALTERNATIVE line must say which REJECTED line it is an alternative for (alternativeForLineUuid or alternativeForLineNumber).");

        if (ReferenceEquals(target, line))
            throw new BadRequestException("A line cannot be an alternative for itself.");
        if (target.LineType != RejectedLine)
            throw new BadRequestException(
                $"Line {target.LineNumber} is {target.LineType}: an alternative can only be offered for a REJECTED line.");

        line.AlternativeForLine = target;
        if (target.Id != 0) line.AlternativeForLineId = target.Id;
    }

    private async Task<int> ActiveReasonIdAsync(Guid reasonUuid)
    {
        var org = _tenant.OrganizationId;
        var reason = await _db.RejectionReasons.AsNoTracking()
            .FirstOrDefaultAsync(r => r.UUID == reasonUuid && r.OrganizationId == org)
            ?? throw new BadRequestException($"Rejection reason {reasonUuid} does not exist in this organization.");
        if (!reason.IsActive)
            throw new BadRequestException(
                $"Rejection reason {reason.Code} is inactive and cannot be set on a line. Pick an active reason.");
        return reason.Id;
    }

    /// <summary>The SaleOrderService.ResolveTaxAsync rules (SAP alignment S-3).</summary>
    private async Task<(Guid? Uuid, string? Code, decimal Percent)> ResolveTaxAsync(SaleQuotationLineRequest req, LineContext context)
    {
        if (req.TaxCodeUuid is not { } codeUuid || codeUuid == Guid.Empty)
        {
            if (req.TaxPercent is < 0m or > 100m)
                throw new BadRequestException($"A line's tax percentage must be between 0 and 100; {req.TaxPercent:0.##} is not.");
            if (decimal.Round(req.TaxPercent, 2) != req.TaxPercent)
                throw new BadRequestException(
                    $"A line's tax percentage can have at most two decimal places, like 17.25; {req.TaxPercent} has more.");
            return (null, null, req.TaxPercent);
        }

        if (_taxCodes is null)
            throw new BadRequestException(
                "Tax codes cannot be checked here, so a quotation line cannot name one. Enter the tax percentage instead.");

        if (!context.TaxCodes.TryGetValue(codeUuid, out var code))
            context.TaxCodes[codeUuid] = code = await _taxCodes.GetAsync(codeUuid);

        if (code is null)
            throw new BadRequestException(
                $"Tax code {codeUuid} does not exist in this organization. Pick one of the codes under Settings → Tax Codes.");
        if (!code.IsActive)
            throw new BadRequestException(
                $"Tax code {code.Code} is inactive and cannot be used on a new or edited line. " +
                "Pick another code, or reactivate it under Settings → Tax Codes.");
        if (!TaxCodeUsage.Allows(code.Usage, TaxCodeUsage.Sales))
            throw new BadRequestException(
                $"Tax code {code.Code} is for {code.Usage.ToLowerInvariant()} only and cannot be used on a sales quotation line. " +
                "Pick a code whose usage is SALES or BOTH.");

        return (code.Uuid, code.Code, code.RatePercent);
    }

    /// <summary>SaleOrderService.ToOrderCurrencyAsync's rules, against the quotation's currency and valid-from date.</summary>
    private async Task<decimal> ToQuotationCurrencyAsync(decimal price, Guid? priceCurrencyId, LineContext context, string variantName)
    {
        if (_exchangeRates is null || _currencyCodes is null)
            return price;

        var quotation = context.Quotation;
        if (!context.BaseCurrency.Known)
            context.BaseCurrency = (await _orgCurrency.GetBaseCurrencyIdAsync(_tenant.OrganizationId), true);

        var fromId = priceCurrencyId ?? context.BaseCurrency.Id;
        if (fromId is not { } from || from == quotation.CurrencyId)
            return price;

        var fromCode = await CachedCurrencyCodeAsync(from, context);
        var toCode   = await CachedCurrencyCodeAsync(quotation.CurrencyId, context);
        if (fromCode is null || toCode is null)
            throw new BadRequestException(
                $"The price of {variantName} is quoted in a different currency from this quotation, and " +
                $"{(fromCode is null ? "the price's" : "the quotation's")} currency has no ISO code in Settings → Currencies, " +
                "so it cannot be converted. Give the currency its code, or enter the unit price.");
        if (string.Equals(fromCode, toCode, StringComparison.OrdinalIgnoreCase))
            return price;

        var quote = await _exchangeRates.GetRateAsync(fromCode, toCode, quotation.ValidFrom);
        if (quote is null)
            throw new BadRequestException(
                $"The price of {variantName} is quoted in {fromCode}, but this quotation is in {toCode} and there is no " +
                $"{fromCode} → {toCode} exchange rate on or before {quotation.ValidFrom:dd MMM yyyy}. " +
                "Add one under Settings → Exchange Rates, or enter the unit price.");

        return ExchangeRateMath.Convert(price, quote.Rate);
    }

    private async Task<string?> CachedCurrencyCodeAsync(Guid currencyId, LineContext context)
    {
        if (!context.CurrencyCodes.TryGetValue(currencyId, out var code))
            context.CurrencyCodes[currencyId] = code = await CurrencyCodeAsync(currencyId);
        return code;
    }

    private async Task<string?> CurrencyCodeAsync(Guid currencyId)
    {
        if (_currencyCodes is null) return null;
        var code = await _currencyCodes.GetCodeAsync(currencyId);
        return string.IsNullOrWhiteSpace(code) ? null : code.Trim();
    }

    // ── totals ────────────────────────────────────────────────────────────────

    /// <summary>The SaleOrderLine formula: qty × price × (1 − disc%) × (1 + tax%), rounded to 2 away from zero.</summary>
    /// <remarks>A35 D-13 — at the quotation currency's decimals (JPY 0), at most 2 (the columns are decimal(18,2)).</remarks>
    private static void ComputeLine(SaleQuotationLine line, int decimals)
    {
        var net = line.Quantity * line.UnitPrice * (1 - line.DiscountPercent / 100m);
        line.TaxAmount = Math.Round(net * line.TaxPercent / 100m, decimals, MidpointRounding.AwayFromZero);
        line.LineTotal = Math.Round(net * (1 + line.TaxPercent / 100m), decimals, MidpointRounding.AwayFromZero);
    }

    /// <summary>A35 D-13 — the quotation currency's decimals from Finance (2 without it), at most 2.</summary>
    private async Task<int> AmountDecimalsAsync(Guid currencyId) =>
        _orgCurrencies is null
            ? CurrencyConventions.DefaultDecimalPlaces
            : Math.Clamp(await _orgCurrencies.GetDecimalPlacesAsync(_tenant.OrganizationId, currencyId), 0, CurrencyConventions.DefaultDecimalPlaces);

    /// <summary>SaleOrderService.ApplyTotals over the offered (non-REJECTED) lines.</summary>
    private static void ApplyTotals(SaleQuotation quotation, int decimals)
    {
        decimal subtotal = 0, discount = 0, tax = 0;
        foreach (var line in quotation.Lines.Where(l => l.LineType != RejectedLine))
        {
            var gross        = line.Quantity * line.UnitPrice;
            var lineDiscount = gross * line.DiscountPercent / 100m;
            subtotal += gross;
            discount += lineDiscount;
            tax      += (gross - lineDiscount) * line.TaxPercent / 100m;
        }

        quotation.Subtotal       = Math.Round(subtotal, decimals, MidpointRounding.AwayFromZero);
        quotation.DiscountAmount = Math.Round(discount, decimals, MidpointRounding.AwayFromZero);
        quotation.TaxAmount      = Math.Round(tax, decimals, MidpointRounding.AwayFromZero);
        quotation.GrandTotal     = quotation.Subtotal - quotation.DiscountAmount + quotation.TaxAmount;
    }

    // ── small rules ───────────────────────────────────────────────────────────

    /// <summary>BR-C2-01 — an active partner of this organization flagged as a customer.</summary>
    private async Task<PartnerRoleInfo> RequireCustomerAsync(Guid partnerId)
    {
        if (partnerId == Guid.Empty)
            throw new BadRequestException("A quotation must be for a customer.");
        var partner = await _partners.GetAsync(partnerId)
                      ?? throw new BadRequestException($"Customer {partnerId} does not exist in this organization.");
        if (!partner.IsCustomer)
            throw new BadRequestException($"{partner.Name} is not a customer — a quotation can only be made for a partner flagged as a customer.");
        if (!partner.IsActive)
            throw new BadRequestException($"{partner.Name} is inactive — reactivate the customer before quoting them.");
        return partner;
    }

    /// <summary>BR-C2-03 — date-only validity, valid-to on or after valid-from (valid-from defaults to today).</summary>
    private static (DateTime From, DateTime To) Validity(DateTime? validFrom, DateTime validTo)
    {
        if (validTo == default)
            throw new BadRequestException("A quotation needs a valid-to date.");
        var from = (validFrom ?? DateTime.UtcNow).Date;
        var to   = validTo.Date;
        if (to < from)
            throw new BadRequestException(
                $"The quotation's valid-to date ({to:yyyy-MM-dd}) must be on or after its valid-from date ({from:yyyy-MM-dd}).");
        return (from, to);
    }

    /// <summary>
    /// A35 D-14 — the quotation's currency: the one asked for (a new choice must be an active org currency), else the
    /// quotation's own (an update), else the inquiry's, else the customer's default sale currency, else the sale base.
    /// </summary>
    private async Task<Guid> CurrencyAsync(Guid? requested, Guid partnerId, Guid? inherited = null, Guid? current = null)
    {
        var org = _tenant.OrganizationId;
        if (requested is { } id && id != Guid.Empty)
        {
            if (id != current && id != inherited)
                await DemandCurrency.RequireActiveOrgCurrencyAsync(_orgCurrencies, _orgCurrency, org, id, TransactionDomain.Sale, await CurrencyCodeAsync(id));
            return id;
        }
        if (current is { } existing && existing != Guid.Empty) return existing;
        if (inherited is { } fromInquiry && fromInquiry != Guid.Empty) return fromInquiry;
        return await DemandCurrency.DefaultForPartnerAsync(_partnerCurrencies, _orgCurrency, org, partnerId, TransactionDomain.Sale)
               ?? throw new BadRequestException(
                   "Currency is required — this organization has no base currency configured, so it must be supplied explicitly.");
    }

    /// <summary>One line's base amounts at an already-locked rate (the base's decimals from Finance, else 2).</summary>
    private async Task RebaseLineAsync(SaleQuotation quotation, SaleQuotationLine line, decimal rate, Guid baseId)
    {
        var baseDecimals = _orgCurrencies is null
            ? CurrencyConventions.DefaultDecimalPlaces
            : await _orgCurrencies.GetDecimalPlacesAsync(_tenant.OrganizationId, baseId);
        var same = quotation.CurrencyId == baseId;
        var lockInfo = new DocumentRateLock(quotation.CurrencyId, string.Empty, baseId, string.Empty, TransactionDomain.Sale,
            rate, DateOnly.FromDateTime(quotation.RateLockedAt ?? DateTime.UtcNow), same, baseDecimals, baseDecimals);
        line.UnitPriceBase      = lockInfo.ToBase(line.UnitPrice);
        line.DiscountAmountBase = lockInfo.ToBase(line.Quantity * line.UnitPrice * line.DiscountPercent / 100m);
        line.TaxAmountBase      = lockInfo.ToBase(line.TaxAmount);
        line.LineTotalBase      = lockInfo.ToBase(line.LineTotal);
    }

    private static void RequireEditable(SaleQuotation quotation)
    {
        if (quotation.Status != Draft)
            throw new BadRequestException(
                $"Quotation {quotation.QuotationNumber} is {quotation.Status} and not editable — only a DRAFT can change. " +
                "Copy it to a new quotation to make changes.");
    }

    private static void RequirePrice(decimal price, string what)
    {
        if (price < 0m)
            throw new BadRequestException($"{what} cannot be negative.");
        if (decimal.Round(price, 4) != price)
            throw new BadRequestException($"{what} can have at most four decimal places; {price} has more.");
    }

    private static string? Text(string? value, int max, string what)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > max)
            throw new BadRequestException($"The {what} can be at most {max} characters long.");
        return trimmed;
    }

    private static void Touch(SaleQuotation quotation, int userId)
    {
        quotation.ModifiedBy   = userId;
        quotation.ModifiedDate = DateTime.UtcNow;
    }
}

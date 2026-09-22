using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Couriers.Dhl;

/// <summary>
/// DHL Express, through its MyDHL REST API: rates, shipments (with the label in the same answer) and
/// tracking.
/// <para>
/// <b>Not one adapter for "DHL".</b> DHL's divisions — Express, eCommerce, Parcel, Freight — are separate
/// APIs on separate contracts with separate credentials. This is Express, and its key says so: the key is
/// saved on carrier rows, so a second DHL adapter later needs its own and renaming this one would strand
/// every carrier pointed at it.
/// </para>
/// <para>
/// <b>What a failed call means.</b> DHL has no idempotency key, so a booking that times out may or may not
/// have created a parcel. That is <see cref="CourierOutcome.Failed"/> — unknown — and the retry ledger
/// then asks a person to check with DHL rather than trying again into a duplicate. A 4xx is DHL's answer
/// and is <see cref="CourierOutcome.Refused"/>, with DHL's own words.
/// </para>
/// </summary>
internal sealed class DhlExpressCourierProvider : ICourierProvider, ICourierCredentialSpec
{
    internal const string ProviderKey    = "DHL_EXPRESS";
    internal const string HttpClientName = "dhl-express";

    internal const string LiveBaseUrl = "https://express.api.dhl.com/mydhlapi";
    internal const string TestBaseUrl = "https://express.api.dhl.com/mydhlapi/test";

    // The credentials this adapter reads. Anything not marked required has a sensible default.
    internal const string ApiKeyCredential            = "ApiKey";
    internal const string ApiSecretCredential         = "ApiSecret";
    internal const string AccountNumberCredential     = "AccountNumber";
    internal const string EnvironmentCredential       = "Environment";
    internal const string BaseUrlCredential           = "BaseUrl";
    internal const string ShipperPostalCodeCredential = "ShipperPostalCode";
    internal const string ShipperCompanyCredential    = "ShipperCompanyName";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory              _http;
    private readonly ILogger<DhlExpressCourierProvider> _log;
    private readonly TimeProvider                    _clock;

    public DhlExpressCourierProvider(
        IHttpClientFactory http, ILogger<DhlExpressCourierProvider> log, TimeProvider? clock = null)
    {
        _http  = http;
        _log   = log;
        _clock = clock ?? TimeProvider.System;
    }

    public string Key => ProviderKey;

    public string DisplayName => "DHL Express (MyDHL API)";

    public CourierCapabilities Capabilities { get; } = new(
        SupportsBooking:       true,
        // MyDHL has no call to cancel a shipment — only a pickup. Saying so is better than a button that
        // tells someone a real parcel was stopped when it was not.
        SupportsCancellation:  false,
        // The label comes back once, in the booking's own answer, and is stored. There is no call to
        // fetch it again (Get Image serves waybills and invoices, not the transport label).
        SupportsLabels:        false,
        SupportsTracking:      true,
        SupportsPickupBooking: true,
        // Express does not collect cash on delivery.
        SupportsCod:           false,
        SupportsMultiPiece:    true,
        HonoursIdempotencyKey: false,
        SupportsRating:        true);

    public IReadOnlyList<CourierCredentialSpec> Credentials { get; } =
    [
        new(ApiKeyCredential,            "The MyDHL API key DHL gave you.", true, true),
        new(ApiSecretCredential,         "The MyDHL API secret that goes with it.", true, true),
        new(AccountNumberCredential,     "Your DHL Express shipper account number.", true, false),
        new(EnvironmentCredential,       "TEST or LIVE. Left out means TEST, so nothing real is booked by accident.", false, false),
        new(ShipperPostalCodeCredential, "Postal code to collect from when the ship-from address has none — warehouse records do not.", false, false),
        new(ShipperCompanyCredential,    "Your company name as DHL should print it on the label.", false, false),
        new(BaseUrlCredential,           "Override the DHL endpoint (https only). Rarely needed.", false, false)
    ];

    // ── Rating ────────────────────────────────────────────────────────────────

    public async Task<CourierRateResult> RateAsync(CourierRateRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        if (!TryOpen(request.Credentials, out var dhl, out var problem))
            return Rated(CourierOutcome.Refused, problem);

        if (request.Packages.Count == 0)
            return Rated(CourierOutcome.Refused, "There is nothing to rate: the consignment has no packages.");

        var refusal = DhlPayloads.AddressProblem("ship-from", request.ShipFrom, false, dhl.ShipperPostalCode)
                   ?? DhlPayloads.AddressProblem("ship-to", request.ShipTo, false, null)
                   ?? request.Packages.Select(DhlPayloads.PackageProblem).FirstOrDefault(p => p is not null);

        if (refusal is not null) return Rated(CourierOutcome.Refused, refusal);

        var product = DhlPayloads.Clip(request.ServiceCode, 6);
        var crossesBorder = DhlPayloads.IsoOf(request.ShipFrom) != DhlPayloads.IsoOf(request.ShipTo);

        var body = new
        {
            customerDetails = new
            {
                shipperDetails  = DhlPayloads.RatesAddress(request.ShipFrom, dhl.ShipperPostalCode),
                receiverDetails = DhlPayloads.RatesAddress(request.ShipTo, null)
            },
            accounts            = new[] { new { typeCode = "shipper", number = dhl.AccountNumber } },
            productsAndServices = product is null ? null : new[] { new { productCode = product } },
            plannedShippingDateAndTime = DhlPayloads.Stamp(PlannedShipping(request.ShipDate)),
            unitOfMeasurement   = "metric",
            isCustomsDeclarable = crossesBorder,
            estimatedDeliveryDate = new { isRequested = true, typeCode = "QDDC" },
            nextBusinessDay     = false,
            packages            = request.Packages.Select(DhlPayloads.RatedPackage).ToArray()
        };

        var reply = await SendAsync(dhl, HttpMethod.Post, "/rates", body, ct);

        if (reply.Failure is not null) return Rated(CourierOutcome.Failed, reply.Failure);

        if (reply.Status is < 200 or >= 300)
            return Rated(OutcomeOf(reply.Status), DhlPayloads.DescribeError(reply.Status, reply.Body), reply.Body);

        var options = ReadRates(reply.Body, product);

        return options.Count == 0
            ? Rated(CourierOutcome.Refused,
                    product is null
                        ? "DHL offers no product for this route and weight."
                        : $"DHL does not offer product '{product}' for this route and weight.",
                    reply.Body)
            : new CourierRateResult(CourierOutcome.Succeeded, Key, options, RawResponse: reply.Body);
    }

    // ── Booking ───────────────────────────────────────────────────────────────

    public async Task<CourierBookingResult> BookAsync(CourierBookingRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        if (!TryOpen(request.Credentials, out var dhl, out var problem))
            return Booked(CourierOutcome.Refused, problem);

        if (request.Packages.Count == 0)
            return Booked(CourierOutcome.Refused, "There is nothing to book: the consignment has no packages.");

        var product = DhlPayloads.Clip(request.ServiceCode, 6);
        if (product is null)
            return Booked(CourierOutcome.Refused,
                "DHL needs a product code (for example N for Express Domestic). Set the account's default " +
                "service, or name one when booking.");

        if (!string.Equals(request.FreightTerms, "PREPAID", StringComparison.OrdinalIgnoreCase))
            return Booked(CourierOutcome.Refused,
                $"Only prepaid shipments can be booked through the DHL API. {request.FreightTerms} needs the " +
                "payer's own DHL account number, which this system does not hold.");

        var refusal = DhlPayloads.AddressProblem("ship-from", request.ShipFrom, true, dhl.ShipperPostalCode)
                   ?? DhlPayloads.AddressProblem("ship-to", request.ShipTo, true, null)
                   ?? request.Packages.Select(DhlPayloads.PackageProblem).FirstOrDefault(p => p is not null);

        if (refusal is not null) return Booked(CourierOutcome.Refused, refusal);

        // Crossing a border makes the parcel dutiable, and DHL then wants a customs declaration: each item's
        // description, HS code, value and country of origin. None of that is held here — the product master
        // has no HS code or origin — and inventing it would put a false declaration on a real parcel.
        if (DhlPayloads.IsoOf(request.ShipFrom) != DhlPayloads.IsoOf(request.ShipTo))
            return Booked(CourierOutcome.Refused,
                "This parcel crosses a border, so DHL needs a customs declaration — each item's description, " +
                "HS code, value and country of origin — and this system does not hold that yet. Book it on " +
                "DHL's own portal and record the airway bill.");

        var description = string.Join(", ", request.ReferenceNumbers.Where(r => !string.IsNullOrWhiteSpace(r)));
        if (description.Length == 0) description = request.ConsignmentNumber;

        var body = new
        {
            plannedShippingDateAndTime = DhlPayloads.Stamp(PlannedShipping(request.PickupWindowStart)),
            // A window is a request for a collection; without one, DHL collects on its standing schedule.
            pickup   = new { isRequested = request.PickupWindowStart is not null || request.PickupWindowEnd is not null },
            productCode = product,
            accounts = new[] { new { typeCode = "shipper", number = dhl.AccountNumber } },
            outputImageProperties = new
            {
                printerDPI     = 300,
                encodingFormat = "pdf",
                imageOptions   = new[] { new { typeCode = "label" } }
            },
            customerDetails = new
            {
                shipperDetails  = DhlPayloads.Party(request.ShipFrom, dhl.ShipperPostalCode, dhl.ShipperCompany),
                receiverDetails = DhlPayloads.Party(request.ShipTo, null, null)
            },
            content = new
            {
                packages            = request.Packages.Select(p => DhlPayloads.BookedPackage(p, description)).ToArray(),
                isCustomsDeclarable = false,
                description         = DhlPayloads.Clip(description, 70),
                incoterm            = "DAP",
                unitOfMeasurement   = "metric"
            },
            customerReferences = new[] { new { typeCode = "CU", value = DhlPayloads.Clip(request.ConsignmentNumber, 35) } }
        };

        var reply = await SendAsync(dhl, HttpMethod.Post, "/shipments", body, ct);

        if (reply.Failure is not null) return Booked(CourierOutcome.Failed, reply.Failure);

        if (reply.Status is < 200 or >= 300)
            return Booked(OutcomeOf(reply.Status), DhlPayloads.DescribeError(reply.Status, reply.Body),
                          raw: DhlPayloads.Redacted(reply.Body), code: reply.Status.ToString(CultureInfo.InvariantCulture));

        return ReadBooking(reply.Body);
    }

    private CourierBookingResult ReadBooking(string body)
    {
        var redacted = DhlPayloads.Redacted(body);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var awb = DhlPayloads.Text(doc.RootElement, "shipmentTrackingNumber");

            // DHL said yes and gave nothing to track. A parcel may well exist, so this is unknown — never
            // "refused", which would invite a second booking.
            if (string.IsNullOrWhiteSpace(awb))
                return Booked(CourierOutcome.Failed,
                    "DHL accepted the shipment but its answer has no tracking number. Check DHL's portal " +
                    "before booking again.", raw: redacted);

            string? firstPiece = null;
            if (doc.RootElement.TryGetProperty("packages", out var pieces) && pieces.ValueKind == JsonValueKind.Array)
                firstPiece = pieces.EnumerateArray().Select(p => DhlPayloads.Text(p, "trackingNumber"))
                                   .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

            return new CourierBookingResult(
                CourierOutcome.Succeeded, Key,
                AwbNumber:        awb.Trim(),
                CarrierReference: firstPiece,
                // DHL's own trackingUrl is an API URL, not a page anyone can open. The carrier's
                // tracking template builds the public one from the airway bill.
                Label:            DhlPayloads.LabelOf(doc.RootElement, awb.Trim()),
                RawResponse:      redacted);
        }
        catch (JsonException)
        {
            return Booked(CourierOutcome.Failed,
                "DHL answered, but not with anything readable. Check DHL's portal before booking again.",
                raw: redacted);
        }
    }

    // ── Cancellation and labels ───────────────────────────────────────────────

    public Task<CourierCancelResult> CancelAsync(CourierCancelRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(new CourierCancelResult(
            CourierOutcome.Unsupported, Key,
            "MyDHL cannot cancel a shipment through its API. Cancel it on DHL's portal or with your DHL " +
            "contact, then cancel the consignment here."));
    }

    public Task<CourierLabelResult> GetLabelAsync(
        string awbNumber, IReadOnlyDictionary<string, string> credentials, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(new CourierLabelResult(
            CourierOutcome.Unsupported, Key,
            Message: "DHL returns the label once, when the shipment is created, and it is kept with the " +
                     "consignment. It cannot be fetched from DHL again."));
    }

    // ── Tracking ──────────────────────────────────────────────────────────────

    public async Task<CourierTrackingResult> TrackAsync(CourierTrackingRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        if (!TryOpen(request.Credentials, out var dhl, out var problem, needsAccount: false))
            return Tracked(CourierOutcome.Refused, problem);

        var awb = request.AwbNumber?.Trim();
        if (string.IsNullOrEmpty(awb))
            return Tracked(CourierOutcome.Refused, "There is no airway bill to track.");

        var path = $"/tracking?shipmentTrackingNumber={Uri.EscapeDataString(awb)}" +
                   "&trackingView=all-checkpoints&levelOfDetail=shipment";

        var reply = await SendAsync(dhl, HttpMethod.Get, path, null, ct);

        if (reply.Failure is not null) return Tracked(CourierOutcome.Failed, reply.Failure);

        if (reply.Status is < 200 or >= 300)
            return Tracked(OutcomeOf(reply.Status), DhlPayloads.DescribeError(reply.Status, reply.Body));

        var events = ReadEvents(reply.Body, awb);

        // DHL answers 200 with no shipments for a number it does not know, as well as 404.
        return events is null
            ? Tracked(CourierOutcome.Refused, $"DHL has no shipment '{awb}'.")
            : new CourierTrackingResult(CourierOutcome.Succeeded, Key, events);
    }

    /// <summary>Events oldest first, with DHL's local times turned into UTC using the offset each one carries.</summary>
    private static List<CourierTrackingEvent>? ReadEvents(string body, string awb)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("shipments", out var shipments)
             || shipments.ValueKind != JsonValueKind.Array || shipments.GetArrayLength() == 0)
                return null;

            var shipment = shipments.EnumerateArray()
                .FirstOrDefault(s => string.Equals(DhlPayloads.Text(s, "shipmentTrackingNumber"), awb, StringComparison.OrdinalIgnoreCase));

            if (shipment.ValueKind != JsonValueKind.Object) shipment = shipments[0];

            var events = new List<(int Index, CourierTrackingEvent Event)>();

            if (shipment.TryGetProperty("events", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                var index = 0;

                foreach (var e in list.EnumerateArray())
                {
                    if (!TryWhen(e, out var occurredAt)) continue;

                    var code        = DhlPayloads.Text(e, "typeCode");
                    var description = DhlPayloads.Text(e, "description");

                    events.Add((index++, new CourierTrackingEvent(
                        OccurredAt:    occurredAt,
                        Milestone:     LogisticsCode.Of(DhlTrackingMap.MilestoneFor(code, description)),
                        CarrierStatus: code,
                        Description:   description,
                        Location:      LocationOf(e),
                        SignedBy:      DhlPayloads.Text(e, "signedBy") is { Length: > 0 } who ? who : null)));
                }
            }

            // Stable on ties: carriers stamp several events with one minute, and DHL lists them in order.
            return [.. events.OrderBy(x => x.Event.OccurredAt).ThenBy(x => x.Index).Select(x => x.Event)];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// DHL gives a local date and time plus a <c>GMTOffset</c> on every event. Read as UTC without the
    /// offset, an event in Karachi would look five hours in the future and be thrown away as invalid.
    /// </summary>
    private static bool TryWhen(JsonElement e, out DateTime utc)
    {
        utc = default;

        var date = DhlPayloads.Text(e, "date");
        var time = DhlPayloads.Text(e, "time") ?? "00:00:00";

        if (!DateTime.TryParse($"{date} {time}", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return false;

        var offset = TimeSpan.Zero;
        if (DhlPayloads.Text(e, "GMTOffset") is { Length: > 0 } text
         && TimeSpan.TryParse(text.TrimStart('+'), CultureInfo.InvariantCulture, out var parsed))
            offset = text.StartsWith('-') ? -parsed.Duration() : parsed;

        // No real zone is further out than this. A malformed offset is read as none rather than allowed
        // to throw out of a poll that has other parcels to get through.
        if (offset.Duration() > TimeSpan.FromHours(14)) offset = TimeSpan.Zero;

        utc = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).UtcDateTime;
        return true;
    }

    private static string? LocationOf(JsonElement e)
    {
        if (!e.TryGetProperty("serviceArea", out var areas) || areas.ValueKind != JsonValueKind.Array) return null;

        var first = areas.EnumerateArray().FirstOrDefault();

        return DhlPayloads.Text(first, "description") is { Length: > 0 } description
            ? description
            : DhlPayloads.Text(first, "code");
    }

    // ── Reading rates ─────────────────────────────────────────────────────────

    private static List<CourierRateOption> ReadRates(string body, string? onlyProduct)
    {
        var options = new Dictionary<string, CourierRateOption>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array)
                return [];

            foreach (var product in products.EnumerateArray())
            {
                var code = DhlPayloads.Text(product, "productCode");

                if (string.IsNullOrWhiteSpace(code) || options.ContainsKey(code.Trim())) continue;
                if (onlyProduct is not null && !string.Equals(code.Trim(), onlyProduct, StringComparison.OrdinalIgnoreCase)) continue;

                if (OptionOf(product, code.Trim()) is { } option)
                    options[option.ServiceCode] = option;
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return [.. options.Values];
    }

    private static CourierRateOption? OptionOf(JsonElement product, string code)
    {
        // What would actually be billed. BILLC is the billing currency; the others are DHL's published
        // and base currencies, used only when it is absent.
        if (!TryTotal(product, out var total, out var currency, out var currencyType)) return null;

        var (baseAmount, surcharges) = Itemise(product, currencyType, total);

        var weight = product.TryGetProperty("weight", out var w) ? w : default;
        var chargeable = new[] { DhlPayloads.Number(weight, "provided"), DhlPayloads.Number(weight, "volumetric") }
            .Where(n => n is > 0m).Select(n => n!.Value).DefaultIfEmpty().Max();

        var capability = product.TryGetProperty("deliveryCapabilities", out var c) ? c : default;

        DateTime? estimated = DateTime.TryParse(DhlPayloads.Text(capability, "estimatedDeliveryDateAndTime"),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
            ? when
            : null;

        return new CourierRateOption(
            ServiceCode:        code,
            ServiceName:        DhlPayloads.Text(product, "productName"),
            TotalAmount:        total,
            Currency:           currency,
            BaseAmount:         baseAmount,
            Surcharges:         surcharges,
            ChargeableWeightKg: chargeable > 0m ? chargeable : null,
            EstimatedDelivery:  estimated,
            TransitDays:        DhlPayloads.Number(capability, "totalTransitDays") is { } days ? (int)days : null,
            // QDDC is DHL's service commitment, as quoted; QDDF is only its fastest transit time.
            IsGuaranteed:       string.Equals(DhlPayloads.Text(capability, "deliveryTypeCode"), "QDDC", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryTotal(JsonElement product, out decimal total, out string currency, out string currencyType)
    {
        total = 0m; currency = string.Empty; currencyType = string.Empty;

        if (!product.TryGetProperty("totalPrice", out var prices) || prices.ValueKind != JsonValueKind.Array) return false;

        var all = prices.EnumerateArray().ToList();

        var chosen = new[] { "BILLC", "PULCL", "BASEC" }
            .Select(type => all.FirstOrDefault(p => string.Equals(DhlPayloads.Text(p, "currencyType"), type, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(p => p.ValueKind == JsonValueKind.Object);

        if (chosen.ValueKind != JsonValueKind.Object) chosen = all.FirstOrDefault();
        if (chosen.ValueKind != JsonValueKind.Object) return false;

        if (DhlPayloads.Number(chosen, "price") is not { } price || price <= 0m) return false;

        var iso = DhlPayloads.Text(chosen, "priceCurrency")?.Trim().ToUpperInvariant();
        if (iso is not { Length: 3 }) return false;

        total        = price;
        currency     = iso;
        currencyType = DhlPayloads.Text(chosen, "currencyType") ?? string.Empty;
        return true;
    }

    /// <summary>
    /// The base charge and each named surcharge, but only when they add up to what DHL says the total is.
    /// A split that does not reconcile is worse than none: an invoice is matched against it line by line,
    /// and a wrong line is a dispute that was manufactured here.
    /// </summary>
    private static (decimal? Base, IReadOnlyList<CourierSurcharge>? Surcharges) Itemise(
        JsonElement product, string currencyType, decimal total)
    {
        if (!product.TryGetProperty("detailedPriceBreakdown", out var breakdowns) || breakdowns.ValueKind != JsonValueKind.Array)
            return (null, null);

        var group = breakdowns.EnumerateArray().FirstOrDefault(b =>
            string.Equals(DhlPayloads.Text(b, "currencyType"), currencyType, StringComparison.OrdinalIgnoreCase));

        if (group.ValueKind != JsonValueKind.Object
         || !group.TryGetProperty("breakdown", out var lines) || lines.ValueKind != JsonValueKind.Array)
            return (null, null);

        var baseAmount = 0m;
        var surcharges = new List<CourierSurcharge>();

        foreach (var line in lines.EnumerateArray())
        {
            if (DhlPayloads.Number(line, "price") is not { } price || price <= 0m) continue;

            var serviceCode = DhlPayloads.Text(line, "serviceCode");

            // The product itself carries no service code; everything DHL adds on top does.
            if (string.IsNullOrWhiteSpace(serviceCode)) baseAmount += price;
            else surcharges.Add(new CourierSurcharge(serviceCode.Trim(), DhlPayloads.Text(line, "name"), price));
        }

        if (product.TryGetProperty("totalPriceBreakdown", out var totals) && totals.ValueKind == JsonValueKind.Array)
        {
            var tax = totals.EnumerateArray()
                .Where(t => string.Equals(DhlPayloads.Text(t, "currencyType"), currencyType, StringComparison.OrdinalIgnoreCase))
                .SelectMany(t => t.TryGetProperty("priceBreakdown", out var b) && b.ValueKind == JsonValueKind.Array
                    ? b.EnumerateArray() : Enumerable.Empty<JsonElement>())
                .Where(b => string.Equals(DhlPayloads.Text(b, "typeCode"), "STTXA", StringComparison.OrdinalIgnoreCase))
                .Select(b => DhlPayloads.Number(b, "price") ?? 0m)
                .Sum();

            if (tax > 0m) surcharges.Add(new CourierSurcharge("TAX", "Tax", tax));
        }

        var parts = baseAmount + surcharges.Sum(s => s.Amount);

        return baseAmount > 0m && Math.Abs(parts - total) <= 0.01m
            ? (baseAmount, surcharges)
            : (null, null);
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private sealed record DhlSession(
        string BaseUrl, string Authorization, string? AccountNumber, string? ShipperPostalCode, string? ShipperCompany);

    private sealed record DhlReply(int Status, string Body, string? Failure);

    /// <summary>
    /// Reads the account's credentials. The environment defaults to TEST: an account with nothing said
    /// about it must never be the one that books a real parcel.
    /// </summary>
    private bool TryOpen(
        IReadOnlyDictionary<string, string> credentials, out DhlSession session, out string problem,
        bool needsAccount = true)
    {
        session = null!;

        string? Get(string key) =>
            credentials.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        foreach (var (key, needed) in new[] { (ApiKeyCredential, true), (ApiSecretCredential, true), (AccountNumberCredential, needsAccount) })
        {
            if (needed && Get(key) is null)
            {
                problem = $"The DHL account has no {key} credential. Add it under the carrier's account credentials.";
                return false;
            }
        }

        var baseUrl = Get(BaseUrlCredential);
        if (baseUrl is not null && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            problem = $"The {BaseUrlCredential} credential must be an https address.";
            return false;
        }

        if (baseUrl is null)
        {
            var environment = Get(EnvironmentCredential)?.ToUpperInvariant();

            if (environment is not (null or "TEST" or "LIVE"))
            {
                problem = $"The {EnvironmentCredential} credential is '{environment}'. It must be TEST or LIVE.";
                return false;
            }

            baseUrl = environment == "LIVE" ? LiveBaseUrl : TestBaseUrl;
        }

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Get(ApiKeyCredential)}:{Get(ApiSecretCredential)}"));

        session = new DhlSession(baseUrl.TrimEnd('/'), token, Get(AccountNumberCredential),
                                 Get(ShipperPostalCodeCredential), Get(ShipperCompanyCredential));
        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// One call. A reply of any status is an answer; only a call that never completed sets
    /// <see cref="DhlReply.Failure"/>. A caller cancelling is not a failure and is rethrown.
    /// </summary>
    private async Task<DhlReply> SendAsync(DhlSession dhl, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(method, dhl.BaseUrl + path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic", dhl.Authorization);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // Lets DHL support find this call in their logs from ours.
        message.Headers.TryAddWithoutValidation("Message-Reference", Guid.NewGuid().ToString());

        if (body is not null)
            message.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

        try
        {
            using var client   = _http.CreateClient(HttpClientName);
            using var response = await client.SendAsync(message, ct);

            return new DhlReply((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Timed out, refused, dropped. Whether DHL saw the request is exactly what is not known.
            _log.LogWarning("DHL {Method} {Path} did not complete: {Reason}", method, path, ex.Message);

            return new DhlReply(0, string.Empty,
                $"The call to DHL did not complete ({ex.GetType().Name}). Whether DHL received it is not known.");
        }
    }

    /// <summary>
    /// 429 means DHL did not look at the request, so a retry is safe: refused. Any other 4xx is DHL's
    /// answer. A 5xx says nothing about whether a parcel was created: failed, which the ledger treats as unknown.
    /// </summary>
    private static CourierOutcome OutcomeOf(int status) =>
        status >= 500 ? CourierOutcome.Failed : CourierOutcome.Refused;

    /// <summary>
    /// The ship time DHL will accept: not in the past (a request stamped "now" arrives late and is judged
    /// already gone), and a plain date meaning mid-morning rather than midnight.
    /// </summary>
    private DateTime PlannedShipping(DateTime? requested)
    {
        var now   = _clock.GetUtcNow().UtcDateTime;
        var floor = now.AddMinutes(30);

        var at = requested is { } r
            ? (r.TimeOfDay == TimeSpan.Zero ? r.Date.AddHours(10) : r)
            : now.AddHours(1);

        return at < floor ? floor : at;
    }

    private CourierRateResult Rated(CourierOutcome outcome, string message, string? raw = null) =>
        new(outcome, Key, [], message, raw);

    private CourierBookingResult Booked(
        CourierOutcome outcome, string message, string? raw = null, string? code = null) =>
        new(outcome, Key, Message: message, CarrierErrorCode: code, RawResponse: raw);

    private CourierTrackingResult Tracked(CourierOutcome outcome, string message) =>
        new(outcome, Key, [], message);
}

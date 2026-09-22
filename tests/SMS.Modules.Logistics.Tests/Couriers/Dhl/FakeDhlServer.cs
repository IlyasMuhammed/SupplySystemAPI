using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace SMS.Modules.Logistics.Tests.Couriers.Dhl;

/// <summary>
/// A stand-in for DHL Express's MyDHL API that enforces what its OpenAPI schema makes mandatory, the way
/// DHL does — with a 422 naming the missing key. An adapter that sends an incomplete payload cannot pass
/// against this by luck.
/// </summary>
internal sealed class FakeDhlServer : HttpMessageHandler, IHttpClientFactory
{
    internal sealed record Call(HttpMethod Method, Uri Uri, string? Body, string? Authorization)
    {
        public string Path => Uri.AbsolutePath;
        public JsonNode? Json => Body is null ? null : JsonNode.Parse(Body);
    }

    private readonly Dictionary<string, JsonNode> _shipments = [];
    private long _next = 1_000_000_000;

    public List<Call> Calls { get; } = [];

    /// <summary>Answers a call itself instead of the default behaviour. Null falls through.</summary>
    public Func<Call, HttpResponseMessage?>? Respond { get; set; }

    /// <summary>Thrown instead of answering — a dropped connection, a timeout.</summary>
    public Exception? Throw { get; set; }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var call = new Call(
            request.Method, request.RequestUri!,
            request.Content is null ? null : await request.Content.ReadAsStringAsync(ct),
            request.Headers.Authorization?.ToString());

        Calls.Add(call);

        if (Throw is not null) throw Throw;
        if (Respond?.Invoke(call) is { } custom) return custom;

        var path = call.Path;

        if (call.Method == HttpMethod.Post && path.EndsWith("/rates"))     return Rates(call);
        if (call.Method == HttpMethod.Post && path.EndsWith("/shipments")) return Shipment(call);
        if (call.Method == HttpMethod.Get  && path.EndsWith("/tracking"))  return Tracking(call);

        return Reply(HttpStatusCode.NotFound, Error("Not found", "No such endpoint.", 404));
    }

    // ── Rates ─────────────────────────────────────────────────────────────────

    private static HttpResponseMessage Rates(Call call)
    {
        var body = call.Json!;

        var missing = MissingKey(body,
            "customerDetails.shipperDetails.postalCode", "customerDetails.shipperDetails.cityName",
            "customerDetails.shipperDetails.countryCode", "customerDetails.receiverDetails.postalCode",
            "plannedShippingDateAndTime", "unitOfMeasurement", "isCustomsDeclarable", "packages");

        if (missing is not null) return Reply((HttpStatusCode)422, Error("Validation error", $"#/: required key [{missing}] not found", 422));

        var wanted = body["productsAndServices"]?.AsArray().Select(p => p!["productCode"]!.GetValue<string>()).ToList();

        var products = new JsonArray();

        // 1500.00 = 1200 base + 300 fuel: the split reconciles, as a real quote's does.
        if (wanted is null || wanted.Contains("N"))
            products.Add(Product("N", "EXPRESS DOMESTIC", 1500m, 1200m, 300m, "QDDC", 1));

        if (wanted is null || wanted.Contains("P"))
            products.Add(Product("P", "EXPRESS WORLDWIDE", 2400m, 2000m, 400m, "QDDF", 3));

        return Reply(HttpStatusCode.OK, new JsonObject { ["products"] = products });
    }

    internal static JsonObject Product(
        string code, string name, decimal total, decimal baseCharge, decimal fuel, string delivery, int days) => new()
    {
        ["productName"] = name,
        ["productCode"] = code,
        ["weight"]      = new JsonObject { ["volumetric"] = 2.0, ["provided"] = 12.5, ["unitOfMeasurement"] = "metric" },
        ["totalPrice"]  = new JsonArray
        {
            new JsonObject { ["currencyType"] = "BASEC", ["priceCurrency"] = "USD", ["price"] = 9.99 },
            new JsonObject { ["currencyType"] = "BILLC", ["priceCurrency"] = "PKR", ["price"] = total },
            new JsonObject { ["currencyType"] = "PULCL", ["priceCurrency"] = "PKR", ["price"] = total }
        },
        ["totalPriceBreakdown"] = new JsonArray
        {
            new JsonObject
            {
                ["currencyType"] = "BILLC", ["priceCurrency"] = "PKR",
                ["priceBreakdown"] = new JsonArray { new JsonObject { ["typeCode"] = "STTXA", ["price"] = 0 } }
            }
        },
        ["detailedPriceBreakdown"] = new JsonArray
        {
            new JsonObject
            {
                ["currencyType"] = "BILLC", ["priceCurrency"] = "PKR",
                ["breakdown"] = new JsonArray
                {
                    new JsonObject { ["name"] = name, ["price"] = baseCharge },
                    new JsonObject
                    {
                        ["name"] = "FUEL SURCHARGE", ["serviceCode"] = "FF", ["serviceTypeCode"] = "SCH", ["price"] = fuel
                    },
                    new JsonObject { ["name"] = "SATURDAY DELIVERY", ["serviceCode"] = "AA", ["serviceTypeCode"] = "XCH" }
                }
            }
        },
        ["deliveryCapabilities"] = new JsonObject
        {
            ["deliveryTypeCode"] = delivery,
            ["estimatedDeliveryDateAndTime"] = "2026-09-25T12:00:00",
            ["totalTransitDays"] = days
        }
    };

    // ── Shipments ─────────────────────────────────────────────────────────────

    private HttpResponseMessage Shipment(Call call)
    {
        var body = call.Json!;

        var missing = MissingKey(body,
            "plannedShippingDateAndTime", "productCode", "accounts",
            "customerDetails.shipperDetails.postalAddress.postalCode",
            "customerDetails.shipperDetails.postalAddress.cityName",
            "customerDetails.shipperDetails.postalAddress.countryCode",
            "customerDetails.shipperDetails.postalAddress.addressLine1",
            "customerDetails.shipperDetails.contactInformation.phone",
            "customerDetails.shipperDetails.contactInformation.companyName",
            "customerDetails.shipperDetails.contactInformation.fullName",
            "customerDetails.receiverDetails.postalAddress.postalCode",
            "customerDetails.receiverDetails.postalAddress.addressLine1",
            "customerDetails.receiverDetails.contactInformation.phone",
            "customerDetails.receiverDetails.contactInformation.companyName",
            "customerDetails.receiverDetails.contactInformation.fullName",
            "content.packages", "content.isCustomsDeclarable", "content.description",
            "content.incoterm", "content.unitOfMeasurement");

        if (missing is not null)
            return Reply((HttpStatusCode)422, Error("Validation error", $"#/: required key [{missing}] not found", 422));

        var awb = (_next++).ToString();
        _shipments[awb] = body;

        var pieces = new JsonArray();
        var index  = 1;
        foreach (var _ in body["content"]!["packages"]!.AsArray())
            pieces.Add(new JsonObject { ["referenceNumber"] = index, ["trackingNumber"] = $"JD01460000{awb}{index++}" });

        return Reply(HttpStatusCode.Created, new JsonObject
        {
            ["shipmentTrackingNumber"] = awb,
            ["trackingUrl"] = $"https://expressapi.dhl.com/mydhlapi/shipments/{awb}/tracking",
            ["packages"] = pieces,
            ["documents"] = new JsonArray
            {
                new JsonObject
                {
                    ["imageFormat"] = "PDF",
                    ["typeCode"]    = "label",
                    ["content"]     = Convert.ToBase64String(Encoding.ASCII.GetBytes("%PDF-1.4 a DHL label"))
                }
            }
        });
    }

    // ── Tracking ──────────────────────────────────────────────────────────────

    private HttpResponseMessage Tracking(Call call)
    {
        var awb = System.Web.HttpUtility.ParseQueryString(call.Uri.Query)["shipmentTrackingNumber"];

        if (awb is null || !_shipments.ContainsKey(awb))
            return Reply(HttpStatusCode.NotFound, Error("Not found", $"No shipment found for {awb}.", 404));

        // Newest first, as a carrier is free to send them. The adapter has to sort.
        var events = new JsonArray
        {
            Event("2026-09-19", "09:15:00", "+05:00", "WC", "With delivery courier", "Lahore-PK"),
            Event("2026-09-18", "22:40:00", "+05:00", "DF", "Departed Facility in Karachi", "Karachi-PK"),
            Event("2026-09-18", "14:05:00", "+05:00", "PU", "Shipment picked up", "Karachi-PK")
        };

        return Reply(HttpStatusCode.OK, new JsonObject
        {
            ["shipments"] = new JsonArray
            {
                new JsonObject { ["shipmentTrackingNumber"] = awb, ["status"] = "success", ["events"] = events }
            }
        });
    }

    internal static JsonObject Event(
        string date, string time, string offset, string code, string description, string area) => new()
    {
        ["date"] = date, ["time"] = time, ["GMTOffset"] = offset, ["typeCode"] = code,
        ["description"] = description, ["signedBy"] = "",
        ["serviceArea"] = new JsonArray { new JsonObject { ["code"] = area[..3].ToUpperInvariant(), ["description"] = area } }
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal static HttpResponseMessage Reply(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    internal static JsonObject Error(string title, string detail, int status) => new()
    {
        ["instance"] = "/expressapi/x", ["title"] = title, ["detail"] = detail,
        ["message"] = title, ["status"] = status.ToString()
    };

    /// <summary>The first dotted path that is absent or null, or null when all are there.</summary>
    private static string? MissingKey(JsonNode root, params string[] paths)
    {
        foreach (var path in paths)
        {
            JsonNode? node = root;
            foreach (var part in path.Split('.'))
            {
                node = node is JsonObject obj && obj.TryGetPropertyValue(part, out var next) ? next : null;
                if (node is null) break;
            }

            if (node is null) return path.Split('.')[^1];
        }

        return null;
    }
}

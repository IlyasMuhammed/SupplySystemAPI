using Intuit.Ipp.Core;
using Intuit.Ipp.Core.Configuration;
using Intuit.Ipp.Core.Rest;
using Intuit.Ipp.Exception;
using Intuit.Ipp.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Providers.QuickBooks;
using Qbo = Intuit.Ipp.Data;

namespace SMS.Modules.Integration.Tests.QuickBooks.Support;

/// <summary>Offline building blocks: SDK contexts, genuine SDK exceptions, fakes, sample entities.</summary>
internal static class QboTestKit
{
    public const string RealmId     = "9130357992222222";
    public const string AccessToken = "eyJ.secret-access-token.sig";

    public static ProviderContext Ctx(IntegrationEnvironment environment = IntegrationEnvironment.Sandbox) =>
        new(7, Guid.Parse("11111111-2222-3333-4444-555555555555"), RealmId, environment);

    /// <summary>
    /// A real ServiceContext, built without any network: JSON both ways, no SDK retries, no SDK request
    /// logging, pinned minor version — what the connection package's factory is expected to produce.
    /// </summary>
    public static ServiceContext NewServiceContext(string? baseUrl = null)
    {
        var context = new ServiceContext(RealmId, IntuitServicesType.QBO, new OAuth2RequestValidator(AccessToken));
        context.IppConfiguration.Message.Request.SerializationFormat  = SerializationFormat.Json;
        context.IppConfiguration.Message.Response.SerializationFormat = SerializationFormat.Json;
        context.IppConfiguration.MinorVersion.Qbo = "75";
        context.IppConfiguration.RetryPolicy = null;
        context.IppConfiguration.Logger.RequestLog.EnableRequestResponseLogging = false;
        if (baseUrl is not null) context.IppConfiguration.BaseUrl.Qbo = baseUrl;
        context.Timeout = 10_000;
        return context;
    }

    // ── Genuine SDK exceptions, built exactly the way SDK 14.7.1.6 builds them ─────────────

    private static readonly FaultHandler Faults = new(NewServiceContext());

    public static string FaultJson(string type, params (string Code, string Message, string? Detail, string? Element)[] errors)
    {
        var array = new JArray(errors.Select(e =>
        {
            var o = new JObject { ["Message"] = e.Message, ["code"] = e.Code };
            if (e.Detail is not null) o["Detail"] = e.Detail;
            if (e.Element is not null) o["element"] = e.Element;
            return o;
        }));
        return new JObject
        {
            ["Fault"] = new JObject { ["Error"] = array, ["type"] = type },
            ["time"]  = "2026-09-30T10:00:00.000-07:00"
        }.ToString(Newtonsoft.Json.Formatting.None);
    }

    public static string ValidationFault(string code, string message, string? detail = null, string? element = null) =>
        FaultJson("ValidationFault", (code, message, detail, element));

    public static IdsException? Parse(string faultJson) => Faults.ParseErrorResponseAndPrepareException(faultJson);

    /// <summary>FaultHandler, HTTP 400: IdsException("BadRequest", "400", source, parsed Fault).</summary>
    public static IdsException Http400(string faultJson, string? tid = "tid-400")
    {
        var ex = new IdsException("BadRequest", "400", "System.Net.Requests", Parse(faultJson));
        ex.Intuit_Tid = tid;
        return ex;
    }

    /// <summary>FaultHandler, HTTP 401: InvalidTokenException("Unauthorized-401", parsed Fault).</summary>
    public static IdsException Http401(string? faultJson = null, string? tid = "tid-401")
    {
        faultJson ??= FaultJson("AuthenticationFault", ("3200", "message=AuthenticationFailed; errorCode=003200; statusCode=401", null, null));
        var ex = new InvalidTokenException("Unauthorized-401", Parse(faultJson));
        ex.Intuit_Tid = tid;
        return ex;
    }

    /// <summary>FaultHandler, HTTP 429: body not parsed.</summary>
    public static IdsException Http429(string? tid = "tid-429")
    {
        var ex = new IdsException("TooManyRequests", "429", "System.Net.Requests", new ThrottleExceededException());
        ex.Intuit_Tid = tid;
        return ex;
    }

    /// <summary>FaultHandler, HTTP 403/404/500/503: body not parsed, EndpointNotFoundException inside.</summary>
    public static IdsException HttpEndpoint(int status, string? tid = "tid-5xx")
    {
        var name = status switch { 403 => "Forbidden", 404 => "NotFound", 500 => "InternalServerError", 503 => "ServiceUnavailable", _ => "Status" };
        var ex = new IdsException(name, status.ToString(), "System.Net.Requests",
            new EndpointNotFoundException($"Call to the endpoint returned a {status} response"));
        ex.Intuit_Tid = tid;
        return ex;
    }

    /// <summary>FaultHandler, any other status: a bare IdsException with the status as ErrorCode.</summary>
    public static IdsException HttpOther(int status, string name = "BadGateway") => new(name, status.ToString(), "System.Net.Requests");

    /// <summary>No response at all (timeout, reset): the SDK swallows the WebException, then this.</summary>
    public static IdsException NoResponse()
    {
        try
        {
            CoreHelper.CheckNullResponseAndThrowException(string.Empty);
        }
        catch (IdsException ex)
        {
            return ex;
        }
        throw new InvalidOperationException("The SDK did not throw for an empty response.");
    }

    // ── Sample payloads ─────────────────────────────────────────────────────────────────────

    public static RemoteAddress FullAddress() => new()
    {
        Line1 = "12 Mall Road", Line2 = "Floor 3", City = "Lahore", Region = "PB", PostalCode = "54000", Country = "Pakistan"
    };

    public static RemoteCustomer FullCustomer() => new()
    {
        DisplayName  = "King's Groceries",
        CompanyName  = "King's Groceries (Pvt) Ltd",
        Email        = "accounts@kings.example",
        Phone        = "+92 42 111 222 333",
        Fax          = "+92 42 111 222 334",
        Website      = "https://kings.example",
        TaxId        = "3520212345671",
        BillAddress  = FullAddress(),
        CurrencyCode = "PKR",
        TermId       = "3",
        Notes        = "Deliver before noon",
        Active       = true
    };

    public static RemoteVendor FullVendor() => new()
    {
        DisplayName   = "Acme Supplies",
        CompanyName   = "Acme Supplies Ltd",
        Email         = "ap@acme.example",
        Phone         = "+92 21 555 0000",
        Fax           = "+92 21 555 0001",
        Website       = "https://acme.example",
        TaxId         = "NTN-7788",
        BillAddress   = FullAddress(),
        CurrencyCode  = "PKR",
        TermId        = "4",
        Notes         = "Main supplier",
        AccountNumber = "ACC-0042",
        Active        = true
    };

    public static RemoteItem FullItem() => new()
    {
        Name             = "Widget - Blue",
        Sku              = "WID-BLU",
        Type             = RemoteItemType.NonInventory,
        Description      = "A blue widget",
        UnitPrice        = 12.50m,
        PurchaseDesc     = "Blue widget, bought",
        PurchaseCost     = 7.25m,
        IncomeAccountId  = "79",
        ExpenseAccountId = "80",
        Active           = true
    };

    public static RemoteInvoice FullInvoice(decimal discount = 5m) => new()
    {
        CustomerId        = "58",
        DocNumber         = "INV-1001",
        TxnDate           = new DateTime(2026, 9, 30),
        DueDate           = new DateTime(2026, 10, 30),
        CurrencyCode      = "PKR",
        Lines =
        {
            new RemoteSalesLine { ItemId = "11", Description = "Blue widgets", Quantity = 2m, UnitPrice = 12.50m, Amount = 25m, TaxCodeId = "5" },
            new RemoteSalesLine { ItemId = "12", Description = "Installation", Quantity = 1m, UnitPrice = 40m, Amount = 40m, TaxCodeId = "6" }
        },
        DiscountAmount    = discount,
        DiscountAccountId = "86",
        CustomerMemo      = "Thank you for your business",
        PrivateNote       = "SO SO-17 · DLV DN-9"
    };

    public static RemoteBill FullBill() => new()
    {
        VendorId     = "31",
        DocNumber    = "SUP-INV-77",
        TxnDate      = new DateTime(2026, 9, 28),
        DueDate      = new DateTime(2026, 10, 28),
        CurrencyCode = "PKR",
        Lines =
        {
            new RemoteBillLine { ItemId = "11", Description = "Widgets", Quantity = 10m, UnitPrice = 7.25m, Amount = 72.50m, TaxCodeId = "7" },
            new RemoteBillLine { AccountId = "81", Description = "Freight", Amount = 15m, TaxCodeId = "8" }
        },
        PrivateNote  = "INV-77 · PO PO-3 · GRN GRN-5"
    };

    public static QuickBooksAccountingProvider Provider(IQboServiceContextFactory contexts, IQboClientFactory clients) =>
        new(contexts, clients, new QboQueryBuilder(), new QboErrorTranslator(), NullLogger<QuickBooksAccountingProvider>.Instance);

    public static Qbo.Customer SavedCustomer(string id = "58", string syncToken = "0", string name = "King's Groceries", bool? active = true)
    {
        var c = new Qbo.Customer { Id = id, SyncToken = syncToken, DisplayName = name };
        if (active is { } a) { c.Active = a; c.ActiveSpecified = true; }
        return c;
    }
}

/// <summary>Hands out a fixed ServiceContext, or throws what it is told to.</summary>
internal sealed class StubContextFactory : IQboServiceContextFactory
{
    private readonly Func<ServiceContext> _create;

    public StubContextFactory(Func<ServiceContext>? create = null) => _create = create ?? (() => QboTestKit.NewServiceContext());

    public static StubContextFactory Throwing(Exception exception) => new(() => throw exception);

    public int Calls { get; private set; }
    public ProviderContext? LastContext { get; private set; }

    public Task<ServiceContext> CreateAsync(ProviderContext ctx, CancellationToken ct = default)
    {
        Calls++;
        LastContext = ctx;
        try
        {
            return Task.FromResult(_create());
        }
        catch (Exception ex)
        {
            return Task.FromException<ServiceContext>(ex);
        }
    }
}

/// <summary>Scripted IQboClient: records every call and answers through delegates.</summary>
internal sealed class FakeQboClient : IQboClient, IQboClientFactory
{
    public Func<Qbo.IEntity, Qbo.IEntity>? OnAdd;
    public Func<Qbo.IEntity, Qbo.IEntity>? OnUpdate;
    public Func<Qbo.IEntity, Qbo.IEntity?>? OnFindById;
    public Func<Qbo.IEntity, Qbo.IEntity>? OnVoid;
    public Func<Type, string, IEnumerable<object>>? OnQuery;

    public List<(string Method, object Argument)> Calls { get; } = new();
    public List<string> Queries { get; } = new();
    public int ClientsCreated { get; private set; }

    public IQboClient Create(ServiceContext context)
    {
        ClientsCreated++;
        return this;
    }

    public Qbo.IEntity Add(Qbo.IEntity entity)
    {
        Calls.Add(("Add", entity));
        return (OnAdd ?? throw new InvalidOperationException("Add not scripted"))(entity);
    }

    public Qbo.IEntity Update(Qbo.IEntity entity)
    {
        Calls.Add(("Update", entity));
        return (OnUpdate ?? throw new InvalidOperationException("Update not scripted"))(entity);
    }

    public Qbo.IEntity? FindById(Qbo.IEntity entity)
    {
        Calls.Add(("FindById", entity));
        return (OnFindById ?? throw new InvalidOperationException("FindById not scripted"))(entity);
    }

    public Qbo.IEntity Void(Qbo.IEntity entity)
    {
        Calls.Add(("Void", entity));
        return (OnVoid ?? throw new InvalidOperationException("Void not scripted"))(entity);
    }

    public IReadOnlyList<T> Query<T>(string query) where T : class, Qbo.IEntity
    {
        Calls.Add(("Query", query));
        Queries.Add(query);
        var rows = (OnQuery ?? throw new InvalidOperationException("Query not scripted"))(typeof(T), query);
        return rows.Cast<T>().ToList();
    }
}

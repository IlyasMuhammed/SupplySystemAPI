using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Reports.Tests;

internal static class TestAssemblySetup
{
    /// <summary>
    /// QuestPDF refuses to render until a licence tier is declared. <c>Program.cs</c> does it at
    /// application startup, which tests never reach, so it is declared once per test assembly.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize() =>
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
}

internal sealed class FixedClock : TimeProvider
{
    public static readonly DateTime Start = new(2026, 9, 20, 10, 30, 0, DateTimeKind.Utc);

    public override DateTimeOffset GetUtcNow() => new(Start);
}

/// <summary>
/// A tenant's sale orders, the customers and currencies they refer to, and the service that reports on
/// them. Built on the in-memory provider, so it shows what the service <i>decides</i>; that the query
/// also translates to SQL is the SQL Server tests' job.
/// </summary>
internal sealed class RegisterWorld
{
    internal static readonly Guid Org      = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid OtherOrg = Guid.Parse("22222222-2222-2222-2222-222222222222");

    internal readonly string DatabaseName = Guid.NewGuid().ToString();

    internal readonly Guid Pkr = Guid.NewGuid();
    internal readonly Guid Usd = Guid.NewGuid();

    internal static readonly Guid AcmeId   = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    internal static readonly Guid GlobexId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    internal Guid Acme   => AcmeId;
    internal Guid Globex => GlobexId;

    /// <summary>What the customer lookup knows. A customer absent from it has no name to show.</summary>
    internal readonly Dictionary<Guid, string> Names = new();

    /// <summary>What the catalog knows. An order in a currency absent from it has no code.</summary>
    internal readonly List<CurrencyModel> Currencies = [];

    internal string? CompanyName { get; set; } = "Northwind Trading";

    private int _sequence;

    internal RegisterWorld()
    {
        Names[Acme]   = "Acme Ltd";
        Names[Globex] = "Globex Corp";
        Currencies.Add(new CurrencyModel { Id = Pkr, Name = "Pakistani Rupee", Code = "PKR" });
        Currencies.Add(new CurrencyModel { Id = Usd, Name = "US Dollar", Code = "USD" });
    }

    internal DemandDbContext Db(Guid? org = null, bool superAdmin = false) =>
        new(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(DatabaseName).Options,
            new StaticTenantContext { OrganizationId = org ?? Org, IsSuperAdmin = superAdmin });

    internal SalesReportService Service(Guid? org = null, Mock<ISupplierNameLookupService>? names = null)
    {
        names ??= new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.Where(Names.ContainsKey).ToDictionary(id => id, id => Names[id]));

        var lookups = new Mock<ILookupsService>();
        lookups.Setup(l => l.GetCurrencies()).Returns(() => [.. Currencies]);

        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync(() =>
            CompanyName is null ? null : new PoDocumentTemplateModel { CompanyName = CompanyName });

        return new SalesReportService(Db(org), names.Object, lookups.Object, templates.Object, new FixedClock());
    }

    /// <summary>Adds an order and saves it. The number is made up unless given.</summary>
    internal SaleOrder Order(
        DateTime? orderDate = null, string status = "CONFIRMED", string deliveryMode = "SHIP", Guid? partner = null,
        Guid? currency = null, decimal subtotal = 100m, decimal discount = 0m, decimal tax = 0m, decimal? grand = null,
        int lines = 1, bool deleted = false, Guid? org = null, string? number = null, DateTime? expected = null)
    {
        var order = NewOrder(orderDate, status, deliveryMode, partner, currency, subtotal, discount, tax, grand, lines, deleted, org, number, expected);
        using var db = Db(order.OrganizationId);
        db.SaleOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>Adds many orders in one save, for the volume tests.</summary>
    internal void Orders(int count, Func<int, DateTime> dateOf, string status = "CONFIRMED")
    {
        using var db = Db();
        for (var i = 0; i < count; i++)
            db.SaleOrders.Add(NewOrder(dateOf(i), status, "SHIP", Acme, Pkr, 10m, 0m, 0m, null, 0, false, null, null, null));
        db.SaveChanges();
    }

    private SaleOrder NewOrder(
        DateTime? orderDate, string status, string deliveryMode, Guid? partner, Guid? currency,
        decimal subtotal, decimal discount, decimal tax, decimal? grand, int lines, bool deleted, Guid? org,
        string? number, DateTime? expected)
    {
        var n = Interlocked.Increment(ref _sequence);
        var order = new SaleOrder
        {
            UUID                 = Guid.NewGuid(),
            TraceId              = Guid.NewGuid(),
            OrganizationId       = org ?? Org,
            SoNumber             = number ?? $"SO-2026-{n:00000}",
            PartnerId            = partner ?? Acme,
            OrderDate            = orderDate ?? new DateTime(2026, 9, 10),
            ExpectedDeliveryDate = expected,
            CurrencyId           = currency ?? Pkr,
            Subtotal             = subtotal,
            DiscountAmount       = discount,
            TaxAmount            = tax,
            GrandTotal           = grand ?? subtotal - discount + tax,
            Status               = status,
            DeliveryMode         = deliveryMode,
            IsDeleted            = deleted,
            CreatedBy            = 1
        };

        for (var i = 1; i <= lines; i++)
            order.Lines.Add(new SaleOrderLine
            {
                UUID = Guid.NewGuid(), OrganizationId = order.OrganizationId, VariantUuid = Guid.NewGuid(),
                Quantity = 1m, UnitPrice = 1m, LineTotal = 1m
            });

        return order;
    }

    /// <summary>A finished report for the exporter tests, without going through the service.</summary>
    internal static SalesOrderRegisterItem Item(
        string number = "SO-2026-00001", string? customer = "Acme Ltd", string status = "CONFIRMED", string mode = "SHIP",
        string currency = "PKR", decimal subtotal = 1000m, decimal discount = 50m, decimal tax = 152m, decimal grand = 1102m,
        int lines = 2, DateTime? date = null, DateTime? expected = null) =>
        new()
        {
            Uuid = Guid.NewGuid(), SoNumber = number, OrderDate = date ?? new DateTime(2026, 9, 10), ExpectedDeliveryDate = expected,
            PartnerId = Guid.NewGuid(), CustomerName = customer, Status = status, DeliveryMode = mode, CurrencyCode = currency,
            LineCount = lines, Subtotal = subtotal, DiscountAmount = discount, TaxAmount = tax, GrandTotal = grand
        };

    internal static SalesOrderRegisterReport Report(IReadOnlyList<SalesOrderRegisterItem> items, SalesOrderRegisterCriteria? criteria = null)
    {
        var totals = items.GroupBy(i => i.CurrencyCode).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new SalesOrderRegisterTotal
        {
            CurrencyCode = g.Key, OrderCount = g.Count(), Subtotal = g.Sum(i => i.Subtotal), DiscountAmount = g.Sum(i => i.DiscountAmount),
            TaxAmount = g.Sum(i => i.TaxAmount), GrandTotal = g.Sum(i => i.GrandTotal)
        }).ToList();

        return new SalesOrderRegisterReport
        {
            CompanyName = "Northwind Trading", GeneratedAt = FixedClock.Start, Criteria = criteria ?? new SalesOrderRegisterCriteria(),
            Items = [.. items], Totals = totals, TotalRecords = items.Count, Page = 1, PageSize = Math.Max(1, items.Count), TotalPages = items.Count == 0 ? 0 : 1
        };
    }
}

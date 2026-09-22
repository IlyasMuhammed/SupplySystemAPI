using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Tests;

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

internal sealed class TestClock : TimeProvider
{
    public static readonly DateTime Start = new(2026, 9, 20, 10, 30, 0, DateTimeKind.Utc);

    public DateTime Value { get; set; } = Start;
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Value, DateTimeKind.Utc));
}

/// <summary>Builders shared by the receivables tests.</summary>
internal static class Receivables
{
    internal const int User = 42;

    internal static FinanceDbContext Db(Guid org, string dbName, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new FinanceDbContext(options.Options, new StaticTenantContext { OrganizationId = org });
    }

    /// <summary>Sees every organization — an auditor, or a check that a write landed where it should.</summary>
    internal static FinanceDbContext Auditor(string dbName) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options,
            new StaticTenantContext { IsSuperAdmin = true });

    internal static Mock<ISupplierNameLookupService> Names(string name = "Acme Ltd")
    {
        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.ToDictionary(id => id, _ => name));
        return names;
    }

    internal static Mock<ILookupsService> Lookups()
    {
        var lookups = new Mock<ILookupsService>();
        lookups.Setup(l => l.GetCurrencies()).Returns(
        [
            new CurrencyModel { Id = Guid.NewGuid(), Name = "Pakistani Rupee", Code = "PKR" },
            new CurrencyModel { Id = Guid.NewGuid(), Name = "US Dollar", Code = "USD" }
        ]);
        return lookups;
    }

    internal static SalesInvoice Invoice(
        Guid org, Guid partner, string number, DateTime invoiceDate, decimal grand,
        string status = "ISSUED", decimal paid = 0m, string currency = "PKR", bool deleted = false,
        string saleOrderNumber = "SO-2026-00042", string? deliveryNumber = null, string partnerName = "Acme Ltd") =>
        new()
        {
            UUID = Guid.NewGuid(), OrganizationId = org, TraceId = Guid.NewGuid(), InvoiceNumber = number,
            SaleOrderUuid = Guid.NewGuid(), SaleOrderNumber = saleOrderNumber,
            DeliveryUuid = deliveryNumber is null ? null : Guid.NewGuid(), DeliveryNumber = deliveryNumber,
            PartnerId = partner, PartnerName = partnerName,
            InvoiceDate = invoiceDate, DueDate = invoiceDate.AddDays(30),
            Subtotal = grand, GrandTotal = grand, AmountPaid = paid, BalanceDue = grand - paid,
            Status = status, CurrencyCode = currency, IsDelete = deleted,
            CreatedBy = 1, CreatedDate = invoiceDate
        };

    internal static async Task Seed(FinanceDbContext db, params object[] entities)
    {
        db.AddRange(entities);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}

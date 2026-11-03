using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.ArAp.Receivables;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A48 AR × D-CORE-14 (LocalDB only) — every AR write path on SQL Server configured as the API configures it
/// (<c>EnableRetryOnFailure</c>, so a user transaction outside the execution strategy throws): the credit check with its lock
/// and an override row, credit memo create + apply (transaction-owned app lock), and a dunning run with a charge.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class A48ArRetryingStrategySqlServerTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A48Ar_{Guid.NewGuid():N};";
    private readonly Guid _org = Guid.NewGuid();
    private readonly FakePartnerLookup _partners = new();

    private FinanceDbContext Finance() => new(
        new DbContextOptionsBuilder<FinanceDbContext>()
            .UseSqlServer(_connection, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null)).Options,
        new StaticTenantContext { OrganizationId = _org });

    private DemandDbContext Demand() => new(
        new DbContextOptionsBuilder<DemandDbContext>()
            .UseSqlServer(_connection, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null)).Options,
        new StaticTenantContext { OrganizationId = _org });

    public async Task InitializeAsync()
    {
        await using var db = Finance();
        await db.Database.EnsureCreatedAsync();
        await using var demand = Demand();
        await demand.Database.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var db = Finance();
        await db.Database.EnsureDeletedAsync();
    }

    private Guid Customer(string? email = "ar@acme.test", decimal? limit = null)
    {
        var id = Guid.NewGuid();
        _partners.Add(new PartnerInfo(id, "C-1", "Acme Ltd", true, false, true, email, null, null, null, null, limit, 30, null));
        return id;
    }

    /// <summary>An issued invoice booked on the customer ledger, as the sales side leaves it.</summary>
    private async Task<Guid> IssuedInvoiceAsync(Guid customer, decimal amount, DateTime due)
    {
        await using var db = Finance();
        var invoice = new SalesInvoice
        {
            UUID = Guid.NewGuid(), OrganizationId = _org, TraceId = Guid.NewGuid(), InvoiceNumber = $"SINV-T-{Guid.NewGuid():N}"[..20],
            PartnerId = customer, PartnerName = "Acme Ltd", InvoiceDate = due.AddDays(-30), DueDate = due,
            Subtotal = amount, GrandTotal = amount, BalanceDue = amount, Status = SalesInvoiceStatuses.Issued, CurrencyCode = "PKR",
            CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        invoice.Lines.Add(new SalesInvoiceLine { UUID = Guid.NewGuid(), OrganizationId = _org, LineNo = 1, VariantUuid = Guid.NewGuid(),
            Description = "Item", Quantity = 1m, UnitPrice = amount, LineTotal = amount });
        db.SalesInvoices.Add(invoice);
        await new CustomerLedgerService(db).TrackEntryAsync(new CustomerLedgerPosting(customer, CustomerLedgerEntryTypes.Invoice,
            "SalesInvoice", invoice.UUID, invoice.InvoiceNumber, amount, 0m, "PKR", null, invoice.InvoiceDate, 1));
        await db.SaveChangesAsync();
        return invoice.UUID;
    }

    private ArApCaller Caller => new(_org, 42, PermissionCodes.ArApCodes, "Tester");

    [Fact]
    public async Task Credit_check_lock_and_override_work_with_the_retrying_strategy()
    {
        var c = Customer(limit: 100m);
        await IssuedInvoiceAsync(c, 90m, DateTime.UtcNow.Date.AddDays(10));
        await using (var db = Finance())
        {
            db.OrgSettings.Add(new FinanceOrgSettings { OrganizationId = _org, CreditControlMode = CreditControlModes.Block });
            await db.SaveChangesAsync();
        }

        await using var fin = Finance();
        await using var dem = Demand();
        var credit = new CreditControlService(fin, new CreditExposureReader(fin, dem), new PartnerFinancialProfileService(fin, _partners),
            NullLogger<CreditControlService>.Instance, _partners);

        var request = new CreditCheckRequest(_org, c, 50m, null, 50m, "SALE_ORDER", Guid.NewGuid(), "SO-1");
        await using (await credit.LockCustomerAsync(_org, c))
        {
            var result = await credit.CheckAsync(request);
            result.Approved.Should().BeFalse();
            result.Exposure.Should().Be(90m);
            await credit.RecordOverrideAsync(request, result, "Approved by director", 42, "Tester");
        }
        await credit.PlaceHoldAsync(c, "Dispute", 42, "Tester");

        await using var check = Finance();
        (await check.CreditHoldRequests.IgnoreQueryFilters().Where(r => r.OrganizationId == _org).Select(r => r.Action).ToListAsync())
            .Should().BeEquivalentTo(["Override", "Hold"]);
    }

    [Fact]
    public async Task Credit_memo_create_and_apply_run_inside_the_execution_strategy()
    {
        var c = Customer();
        var first = await IssuedInvoiceAsync(c, 1_000m, DateTime.UtcNow.Date.AddDays(10));
        var second = await IssuedInvoiceAsync(c, 300m, DateTime.UtcNow.Date.AddDays(10));

        CreditMemoModel memo;
        await using (var db = Finance())
            memo = await new ArCreditMemoService(db, new CustomerLedgerService(db), new ArDocumentCurrency(),
                    NullLogger<ArCreditMemoService>.Instance, _partners)
                .CreateAsync(new CreateCreditMemoRequest
                {
                    CustomerUuid = c, OriginalInvoiceUuid = first, Reason = "Allowance", ApplyToOriginal = false,
                    Lines = [new CreditMemoLineRequest { Amount = 200m }]
                }, Caller);

        await using (var db = Finance())
            memo = await new ArCreditMemoService(db, new CustomerLedgerService(db), new ArDocumentCurrency(),
                    NullLogger<ArCreditMemoService>.Instance, _partners)
                .ApplyAsync(memo.Uuid, new ApplyCreditMemoRequest
                {
                    Applications = [new() { InvoiceUuid = first, Amount = 50m }, new() { InvoiceUuid = second, Amount = 150m }]
                }, Caller);

        memo.Remaining.Should().Be(0m);
        await using var check = Finance();
        (await check.SalesInvoices.IgnoreQueryFilters().SingleAsync(i => i.UUID == second)).BalanceDue.Should().Be(150m);
        (await check.CustomerCreditApplications.IgnoreQueryFilters().CountAsync(a => a.CreditNoteUuid == memo.Uuid)).Should().Be(2);
    }

    [Fact]
    public async Task A_dunning_run_with_a_charge_previews_and_executes_with_the_retrying_strategy()
    {
        var c = Customer();
        await IssuedInvoiceAsync(c, 1_000m, DateTime.UtcNow.Date.AddDays(-20));
        var mail = new Mock<IEmailSender>();
        mail.Setup(m => m.SendWithResultAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmailSendResult.Sent);

        DunningService Service(FinanceDbContext db, DemandDbContext demand)
        {
            var profiles = new PartnerFinancialProfileService(db, _partners);
            var credit = new CreditControlService(db, new CreditExposureReader(db, demand), profiles, NullLogger<CreditControlService>.Instance, _partners);
            var charges = new DunningChargeWriter(db, new CustomerLedgerService(db), new ArDocumentCurrency());
            return new DunningService(db, profiles, new ArApDefaultsSeeder(db), credit, charges, new DunningTemplateEngine(),
                NullLogger<DunningService>.Instance, _partners, mail.Object);
        }

        DunningProcedureModel procedure = new()
        {
            Code = "FEES", Name = "Fees", IsDefault = true, IsActive = true,
            Levels = [new() { LevelNumber = 1, DaysAfterPrevious = 0, SubjectTemplate = "Reminder", BodyTemplate = "Pay {Amount}", ChargeAmount = 25m }]
        };
        await using (var db = Finance()) await using (var dem = Demand()) await Service(db, dem).CreateProcedureAsync(procedure, Caller);

        DunningRunModel run;
        await using (var db = Finance()) await using (var dem = Demand()) run = await Service(db, dem).PreviewAsync(new DunningPreviewRequest(), Caller);
        await using (var db = Finance()) await using (var dem = Demand()) run = await Service(db, dem).ExecuteAsync(run.Uuid, Caller);

        run.Status.Should().Be("Completed");
        run.Items.Single().Should().Match<DunningRunItemModel>(i => i.Status == "Sent" && i.ChargeDocumentNumber != null);
        mail.Verify(m => m.SendWithResultAsync("ar@acme.test", "Reminder", It.IsAny<string>(), It.IsAny<IReadOnlyList<EmailAttachment>?>(), It.IsAny<CancellationToken>()), Times.Once);

        await using (var db = Finance()) await using (var dem = Demand())
            await FluentActions.Awaiting(() => Service(db, dem).ExecuteAsync(run.Uuid, Caller)).Should().ThrowAsync<SMS.Shared.Exceptions.ConflictException>();
    }
}

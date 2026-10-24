using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Accounting.Tests;

/// <summary>A42 D-2 (model), tenant filters (custom template filter, T-COA-11), D-4 gate, D-20 publisher, D-23 vocabularies.</summary>
public class ModuleInfrastructureTests
{
    [Fact]
    public void Every_table_is_in_schema_finance_with_the_spec_names()
    {
        // A SQL Server model (never connects): the relational column facets are only there.
        using var db = new AccountingDbContext(new DbContextOptionsBuilder<AccountingDbContext>()
            .UseSqlServer("Server=(localdb)\\none;Database=none;Trusted_Connection=True").Options, new StaticTenantContext());
        var model = db.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        var tables = model.GetEntityTypes().Select(e => (e.GetSchema(), e.GetTableName())).ToList();
        tables.Should().OnlyContain(t => t.Item1 == "finance");
        tables.Select(t => t.Item2).Should().BeEquivalentTo(
            "CoaTemplates", "CoaTemplateItems", "ChartOfAccounts", "FiscalYears", "FiscalPeriods", "AccountingDefaults", "CoaSetupStatuses",
            "OpeningBalanceEntries", "ExternalSystemMappings", "JournalEntries", "JournalEntryLines", "GlSetupHistory",
            // A44 — posting log + finance posting settings.
            "FinancialPostings", "FinancialPostingTargets", "TenantFinanceConfigs",
            // A47 — journal engine.
            "JournalTypes", "JournalEntrySequences", "ReconcileGroups");
        model.FindEntityType(typeof(CoaTemplate))!.FindProperty(nameof(CoaTemplate.IsGlobal))!.GetColumnName().Should().Be("IsSystem");
        model.FindEntityType(typeof(ChartOfAccount))!.FindProperty(nameof(ChartOfAccount.CurrentBalance))!.GetColumnType().Should().Be("decimal(18,4)");
        model.FindEntityType(typeof(ChartOfAccount))!.GetCheckConstraints().Select(c => c.Name).Should().Contain(
            ["CK_ChartOfAccounts_Level", "CK_ChartOfAccounts_RootType", "CK_ChartOfAccounts_IsGroup", "CK_ChartOfAccounts_OpeningOneSide"]);
    }

    [Fact]
    public async Task System_templates_are_visible_to_every_organization_custom_ones_only_to_their_own()
    {
        using var h = new AccountingHarness();
        var other = Guid.NewGuid();
        h.Db.CoaTemplates.AddRange(
            new CoaTemplate { TemplateCode = "GLOBAL_GENERAL", TemplateName = "Global", IsGlobal = true },
            new CoaTemplate { TemplateCode = "MINE", TemplateName = "Mine" }, // stamped with the caller's organization
            new CoaTemplate { TemplateCode = "THEIRS", TemplateName = "Theirs", OrganizationId = other });
        await h.Db.SaveChangesAsync();

        (await h.Db.CoaTemplates.Select(t => t.TemplateCode).ToListAsync()).Should().BeEquivalentTo("GLOBAL_GENERAL", "MINE");
        (await h.Db.CoaTemplates.SingleAsync(t => t.TemplateCode == "MINE")).OrganizationId.Should().Be(h.Org);
        (await h.Db.CoaTemplates.SingleAsync(t => t.TemplateCode == "GLOBAL_GENERAL")).Should().Match<CoaTemplate>(t => t.IsSystem && t.OrganizationId == null);

        h.Tenant.OrganizationId = other;
        h.NewScope();
        (await h.Db.CoaTemplates.Select(t => t.TemplateCode).ToListAsync()).Should().BeEquivalentTo("GLOBAL_GENERAL", "THEIRS");

        h.Tenant.IsSuperAdmin = true;
        h.NewScope();
        (await h.Db.CoaTemplates.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task One_organizations_accounts_are_invisible_to_another()
    {
        using var h = new AccountingHarness();
        var other = Guid.NewGuid();
        await h.SeedChartAsync(other, ("1111", RootTypes.Asset, AccountTypes.Cash));

        (await h.Db.ChartOfAccounts.CountAsync()).Should().Be(0);
        (await h.Setup.GetAsync(h.Viewer())).AccountsCreatedCount.Should().Be(0);
        (await h.Setup.GetAsync(h.Viewer(other))).AccountsCreatedCount.Should().Be(16);
    }

    private static ActionExecutingContext GateContext(IServiceProvider services)
    {
        var http = new DefaultHttpContext { RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(action, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
    }

    [Fact]
    public async Task The_gate_answers_403_until_the_setup_is_complete()
    {
        using var h = new AccountingHarness();
        var gate = new RequiresGlSetupAttribute();
        var ran = 0;
        ActionExecutionDelegate next = () =>
        {
            ran++;
            return Task.FromResult(new ActionExecutedContext(GateContext(h.Services), [], new object()));
        };

        var ctx = GateContext(h.Services);
        await gate.OnActionExecutionAsync(ctx, next);

        ran.Should().Be(0);
        var result = ctx.Result.Should().BeOfType<ObjectResult>().Which;
        result.StatusCode.Should().Be(403);
        result.Value.Should().BeOfType<GlSetupRequiredModel>().Which.Should().Match<GlSetupRequiredModel>(b =>
            b.Error == "FINANCE_SETUP_REQUIRED" && b.CurrentStatus == SetupStatuses.NotStarted && b.RedirectUrl == "/portal/pages/finance/gl/setup"
            && !b.Success && b.Message.Length > 0);

        await h.ReadyToCompleteAsync();
        await h.Setup.CompleteAsync(h.Admin());
        ctx = GateContext(h.Services);
        await gate.OnActionExecutionAsync(ctx, next);
        ran.Should().Be(1);
        ctx.Result.Should().BeNull();
    }

    private sealed class Boom : IAccountingEventHandler<CoaUploadedEvent>
    {
        public Task HandleAsync(CoaUploadedEvent evt, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task A_failing_handler_never_fails_the_publisher_and_history_is_still_written()
    {
        var dbName = Guid.NewGuid().ToString();
        var org = Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new StaticTenantContext { OrganizationId = org });
        services.AddDbContext<AccountingDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IAccountingEventHandler<CoaUploadedEvent>, Boom>();
        AccountingModuleExtensions.AddAccountingServices(services);
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IAccountingEventPublisher>()
            .PublishAsync(new CoaUploadedEvent(org, "chart.csv", 42, "csv", 7, "Uma Uploader"));

        var row = await scope.ServiceProvider.GetRequiredService<AccountingDbContext>().GlSetupHistory.SingleAsync();
        row.Should().Match<GlSetupHistory>(r => r.EventType == nameof(CoaUploadedEvent) && r.UserId == 7 && r.UserName == "Uma Uploader"
            && r.Summary == "Chart of accounts uploaded from chart.csv (CSV): 42 accounts created." && r.PayloadJson!.Contains("\"fileName\":\"chart.csv\""));
    }

    [Fact]
    public async Task History_rows_are_immutable()
    {
        using var h = new AccountingHarness();
        await h.Get<IAccountingEventPublisher>().PublishAsync(new CoaSetupStatusChangedEvent(h.Org, "NotStarted", "AccountsCreated"));
        var row = await h.Db.GlSetupHistory.SingleAsync();
        row.Summary = "changed";
        await ((Func<Task>)(() => h.Db.SaveChangesAsync())).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void Every_event_has_a_summary()
    {
        var org = Guid.NewGuid();
        var id = Guid.NewGuid();
        IAccountingEvent[] events =
        [
            new CoaTemplateAppliedEvent(org, id, "US_GENERAL", "United States — General Business", 70),
            new CoaUploadedEvent(org, "a.json", 3, "json"),
            new ChartOfAccountCreatedEvent(org, id, "1115", "Petty Cash 2", 4, RootTypes.Asset),
            new ChartOfAccountUpdatedEvent(org, id, "1115", ["accountName", "description"]),
            new ChartOfAccountDeletedEvent(org, id, "1115", "Petty Cash 2"),
            new FiscalYearCreatedEvent(org, id, "FY 2026", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)),
            new FiscalPeriodStatusChangedEvent(org, id, "January 2026", PeriodStatuses.Open, PeriodStatuses.Locked),
            new AccountingDefaultAssignedEvent(org, DefaultKeys.Cash, id, "1111"),
            new AccountingDefaultAssignedEvent(org, DefaultKeys.Cash, null, null),
            new OpeningBalancePostedEvent(org, 1000m, 900m, 100m, new DateOnly(2025, 12, 31), "JE-2026-00001"),
            new OpeningBalancePostedEvent(org, 0, 0, 0, new DateOnly(2025, 12, 31), null, Skipped: true),
            new CoaSetupStatusChangedEvent(org, SetupStatuses.Complete, SetupStatuses.NotStarted, "Setup reset"),
            new CoaSetupCompletedEvent(org, DateTime.UtcNow, 1, 70),
            new ExternalMappingCreatedEvent(org, id, ExternalSystemTypes.QuickBooksOnline, id, "1111", "35"),
        ];
        var summaries = events.Select(GlSetupHistoryWriter.Summarize).ToList();
        summaries.Should().OnlyContain(s => s.EndsWith(".") && !s.StartsWith(nameof(IAccountingEvent)));
        summaries[0].Should().Be("Template United States — General Business (US_GENERAL) applied: 70 accounts created.");
        summaries[9].Should().Be("Opening balances posted as JE-2026-00001: debits 1,000.00, credits 900.00, difference 100.00 to Opening Balance Equity (3900), as of 2025-12-31.");
        summaries[10].Should().Be("Opening balances skipped (no balances).");
        summaries[11].Should().Be("Setup reset: status Complete → NotStarted.");
        summaries[13].Should().Be("Account 1111 mapped to 35 in QuickBooks Online.");
    }

    [Theory]
    [InlineData(AccountTypes.Cash, RootTypes.Asset, true)]
    [InlineData(AccountTypes.Cash, RootTypes.Liability, false)]
    [InlineData(AccountTypes.Tax, RootTypes.Asset, true)]
    [InlineData(AccountTypes.Tax, RootTypes.Liability, true)]
    [InlineData(AccountTypes.Tax, RootTypes.Expense, false)]
    [InlineData(AccountTypes.OtherIncome, RootTypes.Revenue, true)]
    [InlineData(AccountTypes.Depreciation, RootTypes.Expense, true)]
    [InlineData(AccountTypes.AccumulatedDepreciation, RootTypes.Asset, true)]
    [InlineData("Bogus", RootTypes.Asset, false)]
    public void Account_types_follow_appendix_A(string type, string root, bool valid) =>
        AccountTypes.IsValidFor(type, root).Should().Be(valid);

    [Fact]
    public void Vocabularies()
    {
        AccountTypes.All.Should().HaveCount(17);
        RootTypes.All.Select(r => RootTypes.RootCode[r]).Should().Equal("1", "2", "3", "4", "5");
        SetupStatuses.Rank(SetupStatuses.Complete).Should().Be(5);
        SetupStatuses.AllowsChartReplacement(SetupStatuses.AccountsCreated).Should().BeTrue();
        SetupStatuses.AllowsChartReplacement(SetupStatuses.FiscalYearSet).Should().BeFalse();
        ExternalSystemTypes.All.Should().HaveCount(12);
        DefaultKeys.Definitions.Should().HaveCount(16);
        DefaultKeys.Definitions.Select(d => d.Category).Distinct().Should().BeSubsetOf(DefaultCategories.All);
        DefaultKeys.Definitions.Should().OnlyContain(d => AccountTypes.IsValid(d.ExpectedAccountType));
        DefaultKeys.IsLocked(DefaultKeys.OpeningBalance).Should().BeTrue();
        DefaultKeys.IsLocked(DefaultKeys.Cash).Should().BeFalse();
    }
}

using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Demand.Controllers;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A32-PC-09 / T-C2-11 / BR-C2-10 — the daily sweep marks SENT quotations past valid_to EXPIRED, in every
/// organization (a Hangfire job has no user, so it cannot rely on the ambient tenant), and nothing else.
/// </summary>
public class QuotationExpiryJobTests
{
    private static readonly DateTime Today = new(2026, 11, 2);

    private static (DemandDbContext Db, QuotationExpiryJob Job) NewJob(Guid ambientOrg)
    {
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = ambientOrg });
        return (db, new QuotationExpiryJob(db, NullLogger<QuotationExpiryJob>.Instance));
    }

    private static SaleQuotation Quotation(Guid org, string status, DateTime validTo, string number) => new()
    {
        OrganizationId = org, QuotationNumber = number, PartnerId = Guid.NewGuid(), CurrencyId = Guid.NewGuid(),
        Status = status, ValidFrom = validTo.AddDays(-30), ValidTo = validTo, CreatedBy = 1
    };

    [Fact]
    public async Task T_C2_11_SENT_quotations_past_valid_to_expire_in_every_organization_and_nothing_else_changes()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var (db, job) = NewJob(orgA);
        db.SaleQuotations.AddRange(
            Quotation(orgA, "SENT",     Today.AddDays(-1), "A-past"),
            Quotation(orgA, "SENT",     Today,             "A-today"),      // valid through today
            Quotation(orgA, "DRAFT",    Today.AddDays(-9), "A-draft"),
            Quotation(orgA, "ACCEPTED", Today.AddDays(-9), "A-accepted"),
            Quotation(orgB, "SENT",     Today.AddDays(-3), "B-past"));
        await db.SaveChangesAsync();

        var expired = await job.RunAsync(Today);

        expired.Should().Be(2);
        var byNumber = await db.SaleQuotations.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(q => q.QuotationNumber);
        byNumber["A-past"].Status.Should().Be("EXPIRED");
        byNumber["A-past"].ModifiedDate.Should().NotBeNull();
        byNumber["B-past"].Status.Should().Be("EXPIRED", "the job walks every organization, not just the ambient one");
        byNumber["A-today"].Status.Should().Be("SENT");
        byNumber["A-draft"].Status.Should().Be("DRAFT");
        byNumber["A-accepted"].Status.Should().Be("ACCEPTED");
        HangfireTenantScope.OrganizationId.Should().BeNull("the tenant scope is cleared after each organization");
    }

    [Fact]
    public async Task Running_twice_changes_nothing_the_second_time()
    {
        var org = Guid.NewGuid();
        var (db, job) = NewJob(org);
        db.SaleQuotations.Add(Quotation(org, "SENT", Today.AddDays(-1), "A"));
        await db.SaveChangesAsync();

        (await job.RunAsync(Today)).Should().Be(1);
        (await job.RunAsync(Today)).Should().Be(0);
        (await db.SaleQuotations.AsNoTracking().SingleAsync()).Status.Should().Be("EXPIRED");
    }

    [Fact]
    public void The_job_is_scheduled_daily_shortly_after_01_00_UTC()
    {
        QuotationExpiryJob.Cron.Should().Be("7 1 * * *");
        QuotationExpiryJob.RecurringJobId.Should().Be("sale-quotation-expiry");
    }
}

/// <summary>A32-PC-06/07 — every sale-quotation action carries exactly the contract's permission (API-CONTRACT §2/§5).</summary>
public class SaleQuotationsControllerPermissionTests
{
    [Theory]
    [InlineData("GetList",                PermissionCodes.SALE_QUOTATION_VIEW)]
    [InlineData("GetById",                PermissionCodes.SALE_QUOTATION_VIEW)]
    [InlineData("Create",                 PermissionCodes.SALE_QUOTATION_CREATE)]
    [InlineData("Copy",                   PermissionCodes.SALE_QUOTATION_CREATE)]
    [InlineData("Update",                 PermissionCodes.SALE_QUOTATION_EDIT)]
    [InlineData("AddLine",                PermissionCodes.SALE_QUOTATION_EDIT)]
    [InlineData("UpdateLine",             PermissionCodes.SALE_QUOTATION_EDIT)]
    [InlineData("DeleteLine",             PermissionCodes.SALE_QUOTATION_EDIT)]
    [InlineData("RecordCustomerResponse", PermissionCodes.SALE_QUOTATION_EDIT)]
    [InlineData("Accept",                 PermissionCodes.SALE_QUOTATION_EDIT)]
    [InlineData("Reject",                 PermissionCodes.SALE_QUOTATION_EDIT)]
    [InlineData("Send",                   PermissionCodes.SALE_QUOTATION_SEND)]
    [InlineData("ConvertToOrder",         PermissionCodes.SALE_ORDER_CREATE)]
    public void Each_action_requires_exactly_its_contract_permission(string action, string code)
    {
        var method = typeof(SaleQuotationsController).GetMethod(action)!;
        method.Should().NotBeNull();
        var attrs = method.GetCustomAttributes<RequirePermissionAttribute>().ToList();
        attrs.Should().ContainSingle().Which.AnyOf.Should().Equal(code);
    }

    [Fact]
    public void Every_public_action_is_gated_and_none_is_anonymous()
    {
        var actions = typeof(SaleQuotationsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        actions.Should().HaveCount(13);
        actions.Should().OnlyContain(m => m.GetCustomAttributes<RequirePermissionAttribute>().Any());
        actions.Should().NotContain(m => m.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>().Any());
        typeof(SaleQuotationsController).GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/sale-quotations");
        typeof(SaleQuotationsController).GetCustomAttribute<RequiresFeatureAttribute>().Should().NotBeNull();
    }
}

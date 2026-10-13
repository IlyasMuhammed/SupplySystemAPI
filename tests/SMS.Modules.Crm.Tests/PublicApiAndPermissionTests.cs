using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Crm.Controllers;
using SMS.Modules.Crm.Models;
using SMS.Modules.Crm.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Crm.Tests;

/// <summary>Register 1K — public lead API (§14.6, D-16), RBAC (Appendix A), events (Appendix B), module wiring.</summary>
public sealed class PublicApiAndPermissionTests
{
    private static async Task<(CrmHarness H, string Key)> WithKeyAsync(int perMinute = 100)
    {
        var h = new CrmHarness();
        var created = await h.Get<ILeadApiKeyService>().CreateAsync(h.Admin(), new LeadApiKeyUpsert { Name = "Website", RequestsPerMinute = perMinute });
        return (h, created.Key);
    }

    [Fact]
    public async Task A_valid_key_creates_an_unassigned_NEW_lead_and_returns_its_number()
    {
        var (h, key) = await WithKeyAsync();
        using var _ = h;
        key.Should().StartWith("crm_");
        h.Db.LeadApiKeys.Single().KeyHash.Should().NotBe(key, "only the hash is stored");

        var result = await h.Get<IPublicLeadService>().CreateAsync(key, new PublicLeadRequest
        {
            FirstName = "Ahmed", LastName = "Khan", CompanyName = "PakElectro", Email = "ahmed@pakelec.com", Phone = "+92-300-1234567",
            Topic = "Interested in inverter AC", Description = "Saw your Facebook ad", SourceMedium = "social", SourceReference = "fb-ad-12345"
        });
        result.LeadNumber.Should().MatchRegex(@"^LD-\d{4}-\d{5}$");

        var lead = await h.Leads.GetByIdAsync(h.Admin(), result.LeadId);
        lead.Status.Should().Be("NEW");
        lead.AssignedToUserId.Should().BeNull();
        lead.CreatedByName.Should().Be("API: Website");
        lead.MobilePhone.Should().Be("+92-300-1234567");
        lead.SourceReference.Should().Be("fb-ad-12345");
        h.Db.LeadApiKeys.Single().LastUsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Source_and_campaign_codes_resolve_in_the_keys_organization_and_count_the_lead()
    {
        var (h, key) = await WithKeyAsync();
        using var _ = h;
        var campaign = await h.Get<ICampaignService>().CreateAsync(h.Manager(), new CampaignUpsert { Code = "SUMMER-AC-2026", Name = "Summer" });
        var svc = h.Get<IPublicLeadService>();
        var result = await svc.CreateAsync(key, new PublicLeadRequest
        {
            FirstName = "A", LastName = "B", Topic = "T", SourceCode = "facebook", CampaignCode = "summer-ac-2026"
        });
        var lead = await h.Leads.GetByIdAsync(h.Admin(), result.LeadId);
        lead.LeadSourceName.Should().Be("Facebook");
        lead.CampaignName.Should().Be("Summer");
        (await h.Get<ICampaignService>().GetByIdAsync(h.Manager(), campaign.Uuid)).ActualLeads.Should().Be(1);

        await svc.Invoking(s => s.CreateAsync(key, new PublicLeadRequest { FirstName = "A", LastName = "B", Topic = "T", SourceCode = "NOPE" }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*source_code*");
        h.Gate.Off.Add(CrmFeatureCodes.CampaignTracking);
        var ignored = await svc.CreateAsync(key, new PublicLeadRequest { FirstName = "A", LastName = "B", Topic = "T", CampaignCode = "SUMMER-AC-2026" });
        (await h.Leads.GetByIdAsync(h.Admin(), ignored.LeadId)).CampaignUuid.Should().BeNull("campaign tracking off = the code is ignored");
    }

    [Fact]
    public async Task An_invalid_or_inactive_key_is_401_and_a_switched_off_CRM_is_403()
    {
        var (h, key) = await WithKeyAsync();
        using var _ = h;
        var svc = h.Get<IPublicLeadService>();
        var req = new PublicLeadRequest { FirstName = "A", LastName = "B", Topic = "T" };
        await svc.Invoking(s => s.CreateAsync(null, req)).Should().ThrowAsync<UnauthorizedException>();
        await svc.Invoking(s => s.CreateAsync("crm_wrong", req)).Should().ThrowAsync<UnauthorizedException>();

        var keys = h.Get<ILeadApiKeyService>();
        var k = (await keys.ListAsync(h.Admin())).Single();
        await keys.UpdateAsync(h.Admin(), k.Uuid, new LeadApiKeyUpsert { IsActive = false });
        await svc.Invoking(s => s.CreateAsync(key, req)).Should().ThrowAsync<UnauthorizedException>();
        await keys.UpdateAsync(h.Admin(), k.Uuid, new LeadApiKeyUpsert { IsActive = true });

        h.Gate.Off.Add(ModuleCodes.Crm);
        await svc.Invoking(s => s.CreateAsync(key, req)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task The_rate_limit_answers_429_past_the_keys_per_minute_allowance()
    {
        var (h, key) = await WithKeyAsync(perMinute: 2);
        using var _ = h;
        var svc = h.Get<IPublicLeadService>();
        var req = new PublicLeadRequest { FirstName = "A", LastName = "B", Topic = "T" };
        await svc.CreateAsync(key, req);
        await svc.CreateAsync(key, req);
        await svc.Invoking(s => s.CreateAsync(key, req)).Should().ThrowAsync<CrmRateLimitException>();
    }

    [Fact]
    public void The_rate_limiter_opens_a_new_window_each_minute()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 11, 10, 0, 5, TimeSpan.Zero));
        var limiter = new CrmApiRateLimiter(clock);
        limiter.TryAcquire(1, 1).Should().BeTrue();
        limiter.TryAcquire(1, 1).Should().BeFalse();
        limiter.TryAcquire(2, 1).Should().BeTrue("each key has its own window");
        clock.Now = clock.Now.AddMinutes(1);
        limiter.TryAcquire(1, 1).Should().BeTrue();
    }

    [Fact]
    public void The_public_body_reads_snake_case_as_in_the_spec_and_camel_case_too()
    {
        var snake = PublicLeadsController.Parse("""{ "first_name": "Ahmed", "last_name": "Khan", "company_name": "PakElectro", "source_code": "FACEBOOK", "campaign_code": "SUMMER" }""")!;
        snake.FirstName.Should().Be("Ahmed");
        snake.CompanyName.Should().Be("PakElectro");
        snake.SourceCode.Should().Be("FACEBOOK");
        snake.CampaignCode.Should().Be("SUMMER");
        PublicLeadsController.Parse("""{ "firstName": "Bilal", "sourceCode": "WALK_IN" }""")!.FirstName.Should().Be("Bilal");
    }

    [Fact]
    public async Task Api_key_names_are_unique_and_limits_bounded()
    {
        var (h, _) = await WithKeyAsync();
        using var __ = h;
        var keys = h.Get<ILeadApiKeyService>();
        await keys.Invoking(k => k.CreateAsync(h.Admin(), new LeadApiKeyUpsert { Name = "Website" })).Should().ThrowAsync<ConflictException>();
        await keys.Invoking(k => k.CreateAsync(h.Admin(), new LeadApiKeyUpsert { Name = "X", RequestsPerMinute = 0 })).Should().ThrowAsync<BadRequestException>();
    }

    // ── RBAC (Appendix A) ──

    private static IReadOnlyList<string> Required(Type controller, string action) =>
        controller.GetMethod(action)!.GetCustomAttributes<RequirePermissionAttribute>().SelectMany(a => a.AnyOf).ToList();

    [Fact]
    public void Endpoints_carry_the_appendix_A_permissions()
    {
        Required(typeof(LeadsController), nameof(LeadsController.Assign)).Should().Equal(P.CRM_ASSIGN_LEAD);
        Required(typeof(LeadsController), nameof(LeadsController.Reopen)).Should().Equal(P.CRM_REOPEN_LEAD);
        Required(typeof(LeadsController), nameof(LeadsController.Convert)).Should().Equal(P.CRM_CONVERT_LEAD);
        Required(typeof(LeadsController), nameof(LeadsController.Delete)).Should().Equal(P.CRM_DELETE_LEAD);
        Required(typeof(LeadsController), nameof(LeadsController.ExportTimeline)).Should().Equal(P.CRM_EXPORT_TIMELINE);
        Required(typeof(OpportunitiesController), nameof(OpportunitiesController.Won)).Should().Equal(P.CRM_CLOSE_OPPORTUNITY);
        Required(typeof(OpportunitiesController), nameof(OpportunitiesController.Reopen)).Should().Equal(P.CRM_REOPEN_OPPORTUNITY);
        Required(typeof(OpportunitiesController), nameof(OpportunitiesController.CreateInquiry)).Should().BeEquivalentTo([P.CRM_EDIT_OPPORTUNITY, P.SALE_INQUIRY_CREATE]);
        Required(typeof(ActivitiesController), nameof(ActivitiesController.Delete)).Should().Equal(P.CRM_DELETE_ACTIVITY);
        Required(typeof(CrmConfigController), nameof(CrmConfigController.CreatePipelineStage)).Should().Equal(P.CRM_ADMIN);
        Required(typeof(CrmConfigController), nameof(CrmConfigController.CreateCampaign)).Should().Equal(P.CRM_MANAGE_CAMPAIGNS);
        typeof(PublicLeadsController).GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>().Should().NotBeNull();
        foreach (var c in new[] { typeof(LeadsController), typeof(OpportunitiesController), typeof(ActivitiesController), typeof(CrmConfigController) })
            c.GetCustomAttribute<RequiresFeatureAttribute>()!.FeatureCode.Should().Be(ModuleCodes.Crm);
    }

    [Fact]
    public void A_sales_rep_cannot_assign_reopen_or_delete_and_the_admin_holds_every_CRM_permission()
    {
        Roles.SalesRep.Should().NotContain([P.CRM_ASSIGN_LEAD, P.CRM_REOPEN_LEAD, P.CRM_DELETE_LEAD, P.CRM_REOPEN_OPPORTUNITY, P.CRM_ADMIN]);
        Roles.SalesManager.Should().Contain([P.CRM_ASSIGN_LEAD, P.CRM_REOPEN_LEAD]).And.NotContain(P.CRM_ADMIN);
        var crmCodes = typeof(PermissionCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.Name.StartsWith("CRM_")).Select(f => (string)f.GetValue(null)!).ToList();
        crmCodes.Should().HaveCount(21);
        PermissionCodes.All.Should().Contain(crmCodes);
        crmCodes.Should().OnlyContain(c => ModuleCodeMap.ForPermission(c) == ModuleCodes.Crm);
    }

    [Fact]
    public async Task The_lead_assigned_notification_reaches_the_new_owner_only()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        h.Notifications.Sent.Should().BeEmpty("assigning a lead to yourself notifies nobody");
        await h.Leads.AssignAsync(h.Manager(), lead.Uuid, new LeadAssignRequest { UserId = 11 });
        h.Notifications.Sent.Should().ContainSingle(n => n.UserId == 11 && n.NavigationUrl == $"/portal/pages/crm/leads/{lead.Uuid}");
    }

    [Fact]
    public async Task The_impact_check_counts_open_leads_and_opportunities()
    {
        using var h = new CrmHarness();
        await h.CreateLeadAsync();
        await h.ConvertedAsync();
        var impact = h.Get<IEnumerable<IModuleImpactProvider>>().Single(p => p.ModuleCode == ModuleCodes.Crm);
        var items = await impact.GetInProgressAsync(h.Org);
        items.Should().BeEquivalentTo([new ModuleImpactItem("Leads – Open", 1), new ModuleImpactItem("Opportunities – Open", 1)]);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

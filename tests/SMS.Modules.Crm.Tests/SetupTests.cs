using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Crm.Domain;
using SMS.Modules.Crm.Models;
using SMS.Modules.Crm.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Crm.Tests;

/// <summary>Register 1B / 1C / 1D — lead sources, campaigns, sales teams, pipeline stages.</summary>
public sealed class SetupTests
{
    // ── 1B lead sources ──

    [Fact]
    public async Task Fourteen_lead_sources_are_seeded_per_organization_on_first_use()
    {
        using var h = new CrmHarness();
        var sources = await h.Get<ILeadSourceService>().GetAllAsync(h.Admin(), includeInactive: true);

        sources.Should().HaveCount(14);
        sources.Select(s => s.Code).Should().Equal("WEB_FORM", "FACEBOOK", "INSTAGRAM", "WHATSAPP", "PHONE_CALL", "WALK_IN", "REFERRAL",
            "EXISTING_CUSTOMER", "EMAIL", "LINKEDIN", "IMPORT", "PARTNER", "TRADE_SHOW", "OTHER");
        sources.Should().OnlyContain(s => s.IsSystem && s.IsActive);

        // Idempotent: a second call adds nothing.
        await h.Get<ICrmSeeder>().EnsureAsync(h.Org);
        (await h.Db.LeadSources.CountAsync(s => s.OrganizationId == h.Org)).Should().Be(14);
    }

    [Fact]
    public async Task A_lead_source_code_is_unique_per_organization_and_a_built_in_code_cannot_change()
    {
        using var h = new CrmHarness();
        var svc = h.Get<ILeadSourceService>();
        var created = await svc.CreateAsync(h.Admin(), new LeadSourceUpsert { Code = "tiktok", Name = "TikTok" });
        created.Code.Should().Be("TIKTOK");
        created.IsSystem.Should().BeFalse();

        await svc.Invoking(s => s.CreateAsync(h.Admin(), new LeadSourceUpsert { Code = "TIKTOK", Name = "Again" }))
            .Should().ThrowAsync<ConflictException>();
        var facebook = (await svc.GetAllAsync(h.Admin(), false)).First(s => s.Code == "FACEBOOK");
        await svc.Invoking(s => s.UpdateAsync(h.Admin(), facebook.Uuid, new LeadSourceUpsert { Code = "FB" }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*built-in*");

        // Deactivated sources leave the default list.
        await svc.UpdateAsync(h.Admin(), created.Uuid, new LeadSourceUpsert { IsActive = false });
        (await svc.GetAllAsync(h.Admin(), false)).Should().NotContain(s => s.Code == "TIKTOK");
    }

    [Fact]
    public void The_model_declares_the_spec_unique_indexes()
    {
        using var h = new CrmHarness();
        static bool Unique(Microsoft.EntityFrameworkCore.Metadata.IEntityType t, string name) =>
            t.GetIndexes().Any(i => i.GetDatabaseName() == name && i.IsUnique);
        var model = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
            .GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>(h.Db).Model;
        Unique(model.FindEntityType(typeof(CrmLeadSource))!, "UQ_LeadSources_OrgCode").Should().BeTrue();
        Unique(model.FindEntityType(typeof(CrmCampaign))!, "UQ_Campaigns_OrgCode").Should().BeTrue();
        Unique(model.FindEntityType(typeof(CrmSalesTeam))!, "UQ_SalesTeams_OrgCode").Should().BeTrue();
        Unique(model.FindEntityType(typeof(CrmSalesTeamMember))!, "UQ_SalesTeamMembers_TeamUser").Should().BeTrue();
        Unique(model.FindEntityType(typeof(CrmPipelineStage))!, "UQ_PipelineStages_OrgTeamCode").Should().BeTrue();
        Unique(model.FindEntityType(typeof(CrmLead))!, "UQ_Leads_OrgNumber").Should().BeTrue();
        Unique(model.FindEntityType(typeof(CrmOpportunity))!, "UQ_Opportunities_OrgNumber").Should().BeTrue();
        model.FindEntityType(typeof(CrmLead))!.GetIndexes().Select(i => i.GetDatabaseName())
            .Should().Contain(["IX_Leads_Status", "IX_Leads_Email", "IX_Leads_Phone", "IX_Leads_CompanyName", "IX_Leads_CreatedAt", "IX_Leads_PipelineStage"]);
        model.FindEntityType(typeof(CrmActivity))!.GetCheckConstraints().Select(c => c.Name)
            .Should().Contain("CK_Activities_ParentRef");
    }

    // ── 1B campaigns ──

    [Fact]
    public async Task A_campaign_is_created_with_its_fields_and_its_code_is_unique_per_organization()
    {
        using var h = new CrmHarness();
        var svc = h.Get<ICampaignService>();
        var facebook = (await h.Get<ILeadSourceService>().GetAllAsync(h.Admin(), false)).First(s => s.Code == "FACEBOOK");
        var c = await svc.CreateAsync(h.Manager(), new CampaignUpsert
        {
            Code = "summer-ac-2026", Name = "Summer AC Sale 2026", CampaignType = "social_media", LeadSourceUuid = facebook.Uuid,
            StartDate = new DateTime(2026, 6, 1), EndDate = new DateTime(2026, 8, 31), BudgetAmount = 150000m, CurrencyCode = "pkr", ExpectedLeads = 200
        });

        c.Code.Should().Be("SUMMER-AC-2026");
        c.Status.Should().Be("DRAFT");
        c.CampaignType.Should().Be("SOCIAL_MEDIA");
        c.CurrencyCode.Should().Be("PKR");
        c.LeadSourceName.Should().Be("Facebook");
        c.ActualLeads.Should().Be(0);

        await svc.Invoking(s => s.CreateAsync(h.Manager(), new CampaignUpsert { Code = "SUMMER-AC-2026", Name = "Copy" }))
            .Should().ThrowAsync<ConflictException>();
        await svc.Invoking(s => s.CreateAsync(h.Manager(), new CampaignUpsert { Code = "X1", Name = "Bad", BudgetAmount = 5, CurrencyCode = "EUR" }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*not an active currency*");
        await svc.Invoking(s => s.CreateAsync(h.Manager(), new CampaignUpsert { Code = "X2", Name = "Bad", StartDate = new DateTime(2026, 2, 1), EndDate = new DateTime(2026, 1, 1) }))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Theory]
    [InlineData("DRAFT", "ACTIVE", true)]
    [InlineData("ACTIVE", "PAUSED", true)]
    [InlineData("PAUSED", "ACTIVE", true)]
    [InlineData("ACTIVE", "COMPLETED", true)]
    [InlineData("PAUSED", "CANCELLED", true)]
    [InlineData("DRAFT", "COMPLETED", false)]
    [InlineData("COMPLETED", "ACTIVE", false)]
    [InlineData("CANCELLED", "DRAFT", false)]
    public async Task Campaign_status_follows_draft_active_paused_completed_cancelled(string from, string to, bool allowed)
    {
        using var h = new CrmHarness();
        var svc = h.Get<ICampaignService>();
        var c = await svc.CreateAsync(h.Manager(), new CampaignUpsert { Code = "C1", Name = "Campaign" });
        var path = from switch
        {
            "ACTIVE" => new[] { "ACTIVE" }, "PAUSED" => ["ACTIVE", "PAUSED"], "COMPLETED" => ["ACTIVE", "COMPLETED"],
            "CANCELLED" => ["CANCELLED"], _ => []
        };
        foreach (var step in path) await svc.UpdateAsync(h.Manager(), c.Uuid, new CampaignUpsert { Name = "Campaign", Status = step });

        var act = () => svc.UpdateAsync(h.Manager(), c.Uuid, new CampaignUpsert { Name = "Campaign", Status = to });
        if (allowed) (await act()).Status.Should().Be(to);
        else await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Actual_leads_counts_leads_created_with_the_campaign_and_the_detail_shows_them_by_status()
    {
        using var h = new CrmHarness();
        var c = await h.Get<ICampaignService>().CreateAsync(h.Manager(), new CampaignUpsert { Code = "C1", Name = "Campaign" });
        var lead = CrmHarness.NewLead();
        lead.CampaignUuid = c.Uuid;
        await h.CreateLeadAsync(lead: lead);
        await h.CreateLeadAsync(lead: lead);
        await h.CreateLeadAsync(); // no campaign

        var detail = await h.Get<ICampaignService>().GetByIdAsync(h.Manager(), c.Uuid);
        detail.ActualLeads.Should().Be(2);
        detail.LeadsByStatus.Should().ContainKey("NEW").WhoseValue.Should().Be(2);
    }

    [Fact]
    public async Task A_deleted_campaign_is_gone_from_the_list_and_cannot_be_given_to_a_lead()
    {
        using var h = new CrmHarness();
        var svc = h.Get<ICampaignService>();
        var c = await svc.CreateAsync(h.Manager(), new CampaignUpsert { Code = "C1", Name = "Campaign" });
        await svc.DeleteAsync(h.Manager(), c.Uuid);
        (await svc.ListAsync(h.Manager(), new CampaignListFilter())).Data.Should().BeEmpty();
        var lead = CrmHarness.NewLead();
        lead.CampaignUuid = c.Uuid;
        await h.Invoking(x => x.CreateLeadAsync(lead: lead)).Should().ThrowAsync<BadRequestException>();
    }

    // ── 1C sales teams ──

    [Fact]
    public async Task A_sales_team_code_is_unique_and_the_leader_becomes_a_leader_member()
    {
        using var h = new CrmHarness();
        var svc = h.Get<ISalesTeamService>();
        var team = await svc.CreateAsync(h.Admin(), new SalesTeamUpsert { Code = "RETAIL", Name = "Retail Sales", TeamLeaderUserId = 20 });

        team.TeamLeaderName.Should().Be("Maria Manager");
        team.Members.Should().ContainSingle(m => m.UserId == 20 && m.Role == "LEADER");
        await svc.Invoking(s => s.CreateAsync(h.Admin(), new SalesTeamUpsert { Code = "retail", Name = "Again" }))
            .Should().ThrowAsync<ConflictException>();
        await svc.Invoking(s => s.CreateAsync(h.Admin(), new SalesTeamUpsert { Code = "T2", Name = "Bad leader", TeamLeaderUserId = 30 }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*active user*");
    }

    [Fact]
    public async Task A_user_is_a_member_of_a_team_once_but_may_be_in_several_teams()
    {
        using var h = new CrmHarness();
        var svc = h.Get<ISalesTeamService>();
        var retail = await svc.CreateAsync(h.Admin(), new SalesTeamUpsert { Code = "RETAIL", Name = "Retail" });
        var corporate = await svc.CreateAsync(h.Admin(), new SalesTeamUpsert { Code = "CORP", Name = "Corporate" });

        await svc.AddMemberAsync(h.Admin(), retail.Uuid, new SalesTeamMemberRequest { UserId = 10 });
        await svc.Invoking(s => s.AddMemberAsync(h.Admin(), retail.Uuid, new SalesTeamMemberRequest { UserId = 10 }))
            .Should().ThrowAsync<ConflictException>();
        var corp = await svc.AddMemberAsync(h.Admin(), corporate.Uuid, new SalesTeamMemberRequest { UserId = 10 });
        corp.Members.Should().ContainSingle(m => m.UserId == 10);

        var promoted = await svc.AddMemberAsync(h.Admin(), retail.Uuid, new SalesTeamMemberRequest { UserId = 10, Role = "LEADER" });
        promoted.TeamLeaderUserId.Should().Be(10);
        var removed = await svc.RemoveMemberAsync(h.Admin(), retail.Uuid, 10);
        removed.Members.Should().BeEmpty();
        removed.TeamLeaderUserId.Should().BeNull();
    }

    // ── 1D pipeline stages ──

    [Fact]
    public async Task Eight_default_stages_are_seeded_and_returned_in_sequence_order()
    {
        using var h = new CrmHarness();
        var stages = await h.Get<IPipelineStageService>().GetStagesAsync(h.Rep(), new PipelineStageFilter());

        stages.Select(s => (s.Code, s.StageType, s.Sequence, s.WinProbability)).Should().Equal(
            ("NEW_LEAD", "LEAD", 10, 5), ("INITIAL_CONTACT", "LEAD", 20, 10), ("QUALIFICATION", "LEAD", 30, 20),
            ("NEEDS_ANALYSIS", "OPPORTUNITY", 40, 40), ("PROPOSAL", "OPPORTUNITY", 50, 60), ("NEGOTIATION", "OPPORTUNITY", 60, 75),
            ("WON", "OPPORTUNITY", 70, 100), ("LOST", "OPPORTUNITY", 80, 0));
        stages.Single(s => s.Code == "WON").IsWon.Should().BeTrue();
        stages.Single(s => s.Code == "LOST").IsLost.Should().BeTrue();
        (await h.Get<IPipelineStageService>().GetStagesAsync(h.Rep(), new PipelineStageFilter { StageType = "LEAD" })).Should().HaveCount(3);
    }

    [Fact]
    public async Task A_team_with_its_own_stages_uses_them_and_a_team_without_falls_back_to_the_organization()
    {
        using var h = new CrmHarness();
        var teams = h.Get<ISalesTeamService>();
        var svc = h.Get<IPipelineStageService>();
        var custom = await teams.CreateAsync(h.Admin(), new SalesTeamUpsert { Code = "CORP", Name = "Corporate" });
        var plain = await teams.CreateAsync(h.Admin(), new SalesTeamUpsert { Code = "RETAIL", Name = "Retail" });
        await svc.CreateAsync(h.Admin(), new PipelineStageUpsert { Code = "PROSPECT", Name = "Prospect", StageType = "LEAD", SalesTeamUuid = custom.Uuid, Sequence = 5, WinProbability = 5 });
        await svc.CreateAsync(h.Admin(), new PipelineStageUpsert { Code = "DISCOVERY", Name = "Discovery", StageType = "LEAD", SalesTeamUuid = custom.Uuid, Sequence = 15 });

        var corpLead = await svc.GetStagesAsync(h.Rep(), new PipelineStageFilter { SalesTeamUuid = custom.Uuid, StageType = "LEAD" });
        corpLead.Select(s => s.Code).Should().Equal("PROSPECT", "DISCOVERY");
        // The team has no opportunity stages of its own: the organization's.
        (await svc.GetStagesAsync(h.Rep(), new PipelineStageFilter { SalesTeamUuid = custom.Uuid, StageType = "OPPORTUNITY" }))
            .Select(s => s.Code).Should().StartWith("NEEDS_ANALYSIS");
        (await svc.GetStagesAsync(h.Rep(), new PipelineStageFilter { SalesTeamUuid = plain.Uuid, StageType = "LEAD" }))
            .Select(s => s.Code).Should().Equal("NEW_LEAD", "INITIAL_CONTACT", "QUALIFICATION");

        (await teams.GetByIdAsync(h.Admin(), custom.Uuid)).HasOwnStages.Should().BeTrue();
    }

    [Fact]
    public async Task A_stage_with_open_leads_cannot_be_deactivated_and_a_stage_code_is_unique_per_pipeline()
    {
        using var h = new CrmHarness();
        var svc = h.Get<IPipelineStageService>();
        var lead = await h.CreateLeadAsync();
        lead.PipelineStageName.Should().Be("New Lead");
        var newLead = (await svc.GetStagesAsync(h.Admin(), new PipelineStageFilter())).First(s => s.Code == "NEW_LEAD");

        await svc.Invoking(s => s.DeactivateAsync(h.Admin(), newLead.Uuid)).Should().ThrowAsync<ConflictException>();
        await svc.Invoking(s => s.CreateAsync(h.Admin(), new PipelineStageUpsert { Code = "NEW_LEAD", Name = "Dup", StageType = "LEAD" }))
            .Should().ThrowAsync<ConflictException>();

        var empty = await svc.CreateAsync(h.Admin(), new PipelineStageUpsert { Code = "PARKED", Name = "Parked", StageType = "LEAD", Sequence = 99 });
        await svc.DeactivateAsync(h.Admin(), empty.Uuid);
        (await svc.GetStagesAsync(h.Admin(), new PipelineStageFilter())).Should().NotContain(s => s.Code == "PARKED");
        (await svc.GetStagesAsync(h.Admin(), new PipelineStageFilter { IncludeInactive = true })).Should().Contain(s => s.Code == "PARKED" && !s.IsActive);
    }

    [Fact]
    public async Task Win_probability_is_0_to_100_and_only_an_opportunity_stage_can_be_won_or_lost()
    {
        using var h = new CrmHarness();
        var svc = h.Get<IPipelineStageService>();
        await svc.Invoking(s => s.CreateAsync(h.Admin(), new PipelineStageUpsert { Code = "X", Name = "X", StageType = "LEAD", WinProbability = 101 }))
            .Should().ThrowAsync<BadRequestException>();
        await svc.Invoking(s => s.CreateAsync(h.Admin(), new PipelineStageUpsert { Code = "Y", Name = "Y", StageType = "LEAD", IsWon = true }))
            .Should().ThrowAsync<BadRequestException>();
    }
}

using FluentAssertions;
using SMS.Modules.Crm.Models;
using SMS.Modules.Crm.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Crm.Tests;

/// <summary>Register 1E — leads (TS-01..TS-03, TS-09..TS-12, BR-01..BR-10, BR-21).</summary>
public sealed class LeadTests
{
    [Fact]
    public async Task TS01_a_lead_is_created_NEW_with_a_number_in_the_first_lead_stage_assigned_to_its_creator()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();

        lead.Status.Should().Be("NEW");
        lead.LeadNumber.Should().MatchRegex(@"^LD-\d{4}-\d{5}$");
        lead.FullName.Should().Be("Ahmed Khan");
        lead.PipelineStageName.Should().Be("New Lead");
        lead.AssignedToUserId.Should().Be(10);
        lead.AssignedToName.Should().Be("Salim Ahmed");
        lead.Rating.Should().Be("WARM");
        lead.Priority.Should().Be("NORMAL");
        lead.AllowedActions.Should().Contain("DISQUALIFY").And.NotContain("QUALIFY");

        var second = await h.CreateLeadAsync();
        second.LeadNumber.Should().NotBe(lead.LeadNumber);
    }

    [Theory]
    [InlineData(null, "Khan", "Topic")]
    [InlineData("Ahmed", null, "Topic")]
    [InlineData("Ahmed", "Khan", null)]
    [InlineData("Ahmed", "Khan", "   ")]
    public async Task BR02_first_name_last_name_and_topic_are_required(string? first, string? last, string? topic)
    {
        using var h = new CrmHarness();
        var req = new LeadUpsert { CompanyName = "Co", FirstName = first, LastName = last, Topic = topic };
        await h.Leads.Invoking(l => l.CreateAsync(h.Rep(), req)).Should().ThrowAsync<BadRequestException>().WithMessage("*required*");
    }

    [Fact]
    public async Task TS02_a_B2C_lead_without_a_company_takes_the_full_name_as_its_company()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync(lead: CrmHarness.NewLead("Sarah", "Malik", company: null));
        lead.CompanyName.Should().Be("Sarah Malik");
    }

    [Fact]
    public async Task Creating_a_lead_publishes_LeadCreated_and_counts_it_on_its_campaign()
    {
        using var h = new CrmHarness();
        var campaign = await h.Get<ICampaignService>().CreateAsync(h.Manager(), new CampaignUpsert { Code = "SUMMER", Name = "Summer" });
        var req = CrmHarness.NewLead();
        req.CampaignUuid = campaign.Uuid;
        var lead = await h.CreateLeadAsync(lead: req);

        h.Events.Of<LeadCreatedEvent>().Should().ContainSingle(e => e.LeadId == lead.Uuid && e.CampaignId == campaign.Uuid && e.OrganizationId == h.Org);
        (await h.Get<ICampaignService>().GetByIdAsync(h.Manager(), campaign.Uuid)).ActualLeads.Should().Be(1);
    }

    [Fact]
    public async Task A_campaign_cannot_be_given_while_campaign_tracking_is_switched_off()
    {
        using var h = new CrmHarness();
        var campaign = await h.Get<ICampaignService>().CreateAsync(h.Manager(), new CampaignUpsert { Code = "SUMMER", Name = "Summer" });
        h.Gate.Off.Add(SMS.Shared.Common.CrmFeatureCodes.CampaignTracking);
        var req = CrmHarness.NewLead();
        req.CampaignUuid = campaign.Uuid;
        await h.Invoking(x => x.CreateLeadAsync(lead: req)).Should().ThrowAsync<BadRequestException>().WithMessage("*switched off*");
    }

    [Fact]
    public async Task A_rep_creates_leads_for_themselves_only_a_manager_for_someone_else()
    {
        using var h = new CrmHarness();
        var req = CrmHarness.NewLead();
        req.AssignedToUserId = 11;
        await h.Invoking(x => x.CreateLeadAsync(h.Rep(), req)).Should().ThrowAsync<ForbiddenException>();
        var lead = await h.CreateLeadAsync(h.Manager(), req);
        lead.AssignedToUserId.Should().Be(11);
        h.Events.Of<LeadAssignedEvent>().Should().ContainSingle(e => e.NewUserId == 11);
        h.Notifications.Sent.Should().ContainSingle(n => n.UserId == 11 && n.Type == "CRM_LEAD_ASSIGNED");
    }

    [Fact]
    public async Task Amounts_need_an_active_currency_of_the_organization()
    {
        using var h = new CrmHarness();
        var req = CrmHarness.NewLead();
        req.CurrencyCode = null;
        await h.Invoking(x => x.CreateLeadAsync(lead: req)).Should().ThrowAsync<BadRequestException>().WithMessage("*currency*");
        req.CurrencyCode = "EUR";
        await h.Invoking(x => x.CreateLeadAsync(lead: req)).Should().ThrowAsync<BadRequestException>().WithMessage("*not an active currency*");
        req.CurrencyCode = "usd";
        (await h.CreateLeadAsync(lead: req)).CurrencyCode.Should().Be("USD");
    }

    // ── state machine through the service (D-9) ──

    [Fact]
    public async Task BR08_NEW_to_CONTACTED_by_hand_needs_a_logged_conversation()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        await h.Leads.Invoking(l => l.ChangeStatusAsync(h.Rep(), lead.Uuid, "CONTACTED", null))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*Log a call*");

        // A note is not a conversation; a still-open scheduled call is not one either.
        await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert { LeadUuid = lead.Uuid, ActivityType = "NOTE", Subject = "Note" });
        await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert
        {
            LeadUuid = lead.Uuid, ActivityType = "PHONE_CALL", Subject = "Call tomorrow", ScheduledDate = DateTime.UtcNow.AddDays(1)
        });
        (await h.Leads.GetByIdAsync(h.Rep(), lead.Uuid)).Status.Should().Be("NEW");
        await h.Leads.Invoking(l => l.ChangeStatusAsync(h.Rep(), lead.Uuid, "CONTACTED", null)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task TS04_logging_a_call_on_a_NEW_lead_makes_it_CONTACTED_and_moves_it_to_Initial_Contact()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        await h.LogCallAsync(lead.Uuid);

        var after = await h.Leads.GetByIdAsync(h.Rep(), lead.Uuid);
        after.Status.Should().Be("CONTACTED");
        after.PipelineStageName.Should().Be("Initial Contact");
        after.AllowedActions.Should().Contain("QUALIFY");
        h.Events.Of<LeadStatusChangedEvent>().Should().ContainSingle(e => e.OldStatus == "NEW" && e.NewStatus == "CONTACTED");
        h.Events.Of<ActivityCreatedEvent>().Should().ContainSingle(e => e.LeadId == lead.Uuid && e.ActivityType == "PHONE_CALL");
    }

    [Fact]
    public async Task TS03_TS05_NEW_cannot_jump_to_QUALIFIED_but_CONTACTED_qualifies()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        await h.Leads.Invoking(l => l.QualifyAsync(h.Rep(), lead.Uuid))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*must be contacted*");

        await h.LogCallAsync(lead.Uuid);
        var qualified = await h.Leads.QualifyAsync(h.Rep(), lead.Uuid);
        qualified.Status.Should().Be("QUALIFIED");
        qualified.QualifiedAt.Should().NotBeNull();
        qualified.PipelineStageName.Should().Be("Qualification");
        qualified.AllowedActions.Should().Contain("CONVERT");
        h.Events.Of<LeadQualifiedEvent>().Should().ContainSingle(e => e.LeadId == lead.Uuid);
    }

    [Fact]
    public async Task TS09_disqualifying_needs_a_reason_and_records_it()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        await h.Leads.Invoking(l => l.DisqualifyAsync(h.Rep(), lead.Uuid, "  ")).Should().ThrowAsync<BadRequestException>();

        var d = await h.Leads.DisqualifyAsync(h.Rep(), lead.Uuid, "No budget this year");
        d.Status.Should().Be("DISQUALIFIED");
        d.StatusReason.Should().Be("No budget this year");
        d.DisqualifiedAt.Should().NotBeNull();
        h.Events.Of<LeadDisqualifiedEvent>().Should().ContainSingle(e => e.Reason == "No budget this year");
    }

    [Fact]
    public async Task TS10_TS11_a_disqualified_lead_is_reopened_to_NEW_only_with_the_reopen_permission()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        await h.LogCallAsync(lead.Uuid);
        await h.Leads.DisqualifyAsync(h.Rep(), lead.Uuid, "Wrong number");

        await h.Leads.Invoking(l => l.ReopenAsync(h.Rep(), lead.Uuid)).Should().ThrowAsync<ForbiddenException>();
        await h.Leads.Invoking(l => l.ChangeStatusAsync(h.Rep(), lead.Uuid, "NEW", null)).Should().ThrowAsync<ForbiddenException>();

        var reopened = await h.Leads.ReopenAsync(h.Manager(), lead.Uuid);
        reopened.Status.Should().Be("NEW");
        reopened.DisqualifiedAt.Should().BeNull();
        reopened.StatusReason.Should().BeNull();
        reopened.PipelineStageName.Should().Be("New Lead");
    }

    [Fact]
    public async Task TS12_BR06_a_converted_lead_cannot_be_edited_assigned_moved_or_deleted()
    {
        using var h = new CrmHarness();
        var (lead, _) = await h.ConvertedAsync();
        lead.Status.Should().Be("CONVERTED");
        lead.CanEdit.Should().BeFalse();
        lead.AllowedActions.Should().BeEmpty();

        await h.Leads.Invoking(l => l.UpdateAsync(h.Manager(), lead.Uuid, CrmHarness.NewLead())).Should().ThrowAsync<ConflictException>();
        await h.Leads.Invoking(l => l.AssignAsync(h.Manager(), lead.Uuid, new LeadAssignRequest { UserId = 11 })).Should().ThrowAsync<ConflictException>();
        await h.Leads.Invoking(l => l.DisqualifyAsync(h.Manager(), lead.Uuid, "x")).Should().ThrowAsync<ConflictException>();
        await h.Leads.Invoking(l => l.DeleteAsync(h.Manager(), lead.Uuid)).Should().ThrowAsync<ConflictException>();
        await h.Leads.Invoking(l => l.ChangeStatusAsync(h.Manager(), lead.Uuid, "CONVERTED", null)).Should().ThrowAsync<BadRequestException>();
    }

    // ── ownership (D-5) ──

    [Fact]
    public async Task A_rep_changes_only_leads_assigned_to_them_a_manager_any()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync(h.Rep(10));
        var other = h.Rep(11);

        await h.Leads.Invoking(l => l.UpdateAsync(other, lead.Uuid, CrmHarness.NewLead(topic: "Changed"))).Should().ThrowAsync<ForbiddenException>();
        await h.Leads.Invoking(l => l.DisqualifyAsync(other, lead.Uuid, "x")).Should().ThrowAsync<ForbiddenException>();
        (await h.Leads.GetByIdAsync(other, lead.Uuid)).CanEdit.Should().BeFalse();

        (await h.Leads.UpdateAsync(h.Manager(), lead.Uuid, CrmHarness.NewLead(topic: "Changed"))).Topic.Should().Be("Changed");
        (await h.Leads.UpdateAsync(h.Rep(10), lead.Uuid, CrmHarness.NewLead(topic: "Mine"))).Topic.Should().Be("Mine");
    }

    // ── assignment (BR-10) ──

    [Fact]
    public async Task Assigning_sets_assigned_at_and_the_previous_owner_and_publishes_LeadAssigned()
    {
        using var h = new CrmHarness();
        var team = await h.Get<ISalesTeamService>().CreateAsync(h.Admin(), new SalesTeamUpsert { Code = "RETAIL", Name = "Retail" });
        var lead = await h.CreateLeadAsync();
        var before = (await h.Leads.GetByIdAsync(h.Manager(), lead.Uuid)).AssignedAt;

        var assigned = await h.Leads.AssignAsync(h.Manager(), lead.Uuid, new LeadAssignRequest { UserId = 11, SalesTeamUuid = team.Uuid });
        assigned.AssignedToUserId.Should().Be(11);
        assigned.PreviousOwnerUserId.Should().Be(10);
        assigned.PreviousOwnerName.Should().Be("Salim Ahmed");
        assigned.SalesTeamName.Should().Be("Retail");
        assigned.AssignedAt.Should().NotBeNull().And.BeOnOrAfter(before!.Value);
        h.Events.Of<LeadAssignedEvent>().Should().ContainSingle(e => e.NewUserId == 11 && e.PreviousUserId == 10);

        await h.Leads.Invoking(l => l.AssignAsync(h.Manager(), lead.Uuid, new LeadAssignRequest { UserId = 30 }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*active user*");
        var unassigned = await h.Leads.AssignAsync(h.Manager(), lead.Uuid, new LeadAssignRequest { UserId = null });
        unassigned.AssignedToUserId.Should().BeNull();
        unassigned.PreviousOwnerUserId.Should().Be(11);
    }

    // ── duplicates (BR-09) ──

    [Fact]
    public async Task The_duplicate_check_matches_email_phone_digits_and_company_and_lists_partner_matches()
    {
        using var h = new CrmHarness();
        var existing = await h.CreateLeadAsync();
        h.Accounts.AddAccount(h.Org, "PakElectro Pvt Ltd", phone: "+92-42-35761234");

        var byEmail = await h.Leads.DuplicateCheckAsync(h.Rep(), new LeadDuplicateQuery { Email = "AHMED@pakelec.com" });
        byEmail.Leads.Should().ContainSingle(d => d.Lead.Uuid == existing.Uuid && d.MatchedOn.SequenceEqual(new[] { "EMAIL" }));

        var byPhone = await h.Leads.DuplicateCheckAsync(h.Rep(), new LeadDuplicateQuery { Phone = "+92 321 7654321" });
        byPhone.Leads.Should().ContainSingle(d => d.MatchedOn.Contains("PHONE"));

        var byCompany = await h.Leads.DuplicateCheckAsync(h.Rep(), new LeadDuplicateQuery { CompanyName = " pakelectro " });
        byCompany.Leads.Should().ContainSingle(d => d.MatchedOn.Contains("COMPANY"));
        byCompany.Accounts.Should().ContainSingle(a => a.Name == "PakElectro Pvt Ltd");

        (await h.Leads.DuplicateCheckForLeadAsync(h.Rep(), existing.Uuid)).Leads.Should().BeEmpty("a lead is not its own duplicate");
        (await h.Leads.DuplicateCheckAsync(h.Rep(), new LeadDuplicateQuery { Email = "nobody@x.test" })).Leads.Should().BeEmpty();
    }

    // ── kanban, list ──

    [Fact]
    public async Task The_kanban_groups_open_leads_by_their_lead_stage_with_counts_and_totals()
    {
        using var h = new CrmHarness();
        var a = await h.CreateLeadAsync();
        var b = await h.CreateLeadAsync(lead: CrmHarness.NewLead("Bilal", "Raza", "SunTech"));
        await h.LogCallAsync(b.Uuid);
        var c = await h.CreateLeadAsync(lead: CrmHarness.NewLead("Old", "Lead", "Gone"));
        await h.Leads.DisqualifyAsync(h.Rep(), c.Uuid, "Spam");

        var board = await h.Leads.GetKanbanAsync(h.Rep(), new LeadKanbanFilter());
        board.Columns.Select(col => col.Code).Should().Equal("NEW_LEAD", "INITIAL_CONTACT", "QUALIFICATION");
        board.Columns[0].Items.Select(i => i.Uuid).Should().Equal(a.Uuid);
        board.Columns[1].Items.Select(i => i.Uuid).Should().Equal(b.Uuid);
        board.Columns[0].TotalRevenue.Should().Be(280000m);
        board.Columns[0].WinProbability.Should().Be(5);
        board.Columns.SelectMany(col => col.Items).Should().NotContain(i => i.Uuid == c.Uuid, "disqualified leads are not on the board");

        // A drag moves the stage without touching the status.
        var qualification = board.Columns[2].StageUuid;
        var moved = await h.Leads.MoveStageAsync(h.Rep(), a.Uuid, qualification);
        moved.PipelineStageName.Should().Be("Qualification");
        moved.Status.Should().Be("NEW");
        var oppStage = (await h.Get<IPipelineStageService>().GetStagesAsync(h.Rep(), new PipelineStageFilter { StageType = "OPPORTUNITY" }))[0].Uuid;
        await h.Leads.Invoking(l => l.MoveStageAsync(h.Rep(), a.Uuid, oppStage)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task The_list_filters_sorts_and_pages()
    {
        using var h = new CrmHarness();
        for (var i = 1; i <= 7; i++)
        {
            var req = CrmHarness.NewLead($"First{i}", $"Last{i}", $"Company {i:D2}", email: $"p{i}@x.test", mobile: $"+92-300-000000{i}");
            req.Rating = i % 2 == 0 ? "HOT" : "COLD";
            await h.CreateLeadAsync(lead: req);
        }

        var page1 = await h.Leads.ListAsync(h.Rep(), new LeadListFilter { Page = 1, PageSize = 3, SortField = "companyName", SortOrder = "asc" });
        page1.TotalRecords.Should().Be(7);
        page1.TotalPages.Should().Be(3);
        page1.Data.Select(d => d.CompanyName).Should().Equal("Company 01", "Company 02", "Company 03");
        (await h.Leads.ListAsync(h.Rep(), new LeadListFilter { Page = 3, PageSize = 3, SortField = "companyName", SortOrder = "asc" }))
            .Data.Select(d => d.CompanyName).Should().Equal("Company 07");
        (await h.Leads.ListAsync(h.Rep(), new LeadListFilter { Rating = "hot" })).TotalRecords.Should().Be(3);
        (await h.Leads.ListAsync(h.Rep(), new LeadListFilter { Search = "p5@" })).Data.Should().ContainSingle();
        (await h.Leads.ListAsync(h.Rep(), new LeadListFilter { Status = "NEW,CONTACTED" })).TotalRecords.Should().Be(7);
        (await h.Leads.ListAsync(h.Rep(11), new LeadListFilter { Mine = true })).TotalRecords.Should().Be(0);
    }

    [Fact]
    public async Task BR21_TS25_another_organization_never_sees_or_changes_the_lead()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        var outsider = new CrmCaller(Guid.NewGuid(), 10, Roles.Admin);

        await h.Leads.Invoking(l => l.GetByIdAsync(outsider, lead.Uuid)).Should().ThrowAsync<NotFoundException>();
        await h.Leads.Invoking(l => l.UpdateAsync(outsider, lead.Uuid, CrmHarness.NewLead())).Should().ThrowAsync<NotFoundException>();
        (await h.Leads.ListAsync(outsider, new LeadListFilter())).TotalRecords.Should().Be(0);
        (await h.Leads.DuplicateCheckAsync(outsider, new LeadDuplicateQuery { Email = "ahmed@pakelec.com" })).Leads.Should().BeEmpty();
    }

    [Fact]
    public async Task A_deleted_lead_is_gone_and_only_a_converted_one_refuses()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        await h.Leads.DeleteAsync(h.Manager(), lead.Uuid);
        await h.Leads.Invoking(l => l.GetByIdAsync(h.Manager(), lead.Uuid)).Should().ThrowAsync<NotFoundException>();
        (await h.Leads.ListAsync(h.Manager(), new LeadListFilter())).TotalRecords.Should().Be(0);
    }
}

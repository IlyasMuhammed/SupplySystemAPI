using FluentAssertions;
using SMS.Modules.Crm.Models;
using SMS.Modules.Crm.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Crm.Tests;

/// <summary>Register 1I / 1J — conversion (C6), opportunities (C7), cross-references (§12.5).</summary>
public sealed class ConversionOpportunityTests
{
    [Fact]
    public async Task TS06_converting_with_a_new_account_creates_customer_contact_and_opportunity_and_closes_the_lead()
    {
        using var h = new CrmHarness();
        var lead = await h.QualifiedLeadAsync();
        var result = await h.Conversion.ConvertAsync(h.Rep(), lead.Uuid, new LeadConvertRequest());

        result.IsNewAccount.Should().BeTrue();
        result.IsNewContact.Should().BeTrue();
        result.OpportunityNumber.Should().MatchRegex(@"^OP-\d{4}-\d{5}$");
        h.Accounts.Created.Should().ContainSingle(a => a.Name == "PakElectro" && a.CustomerType == "COMPANY" && a.Email == "ahmed@pakelec.com");
        h.Accounts.Contacts.Should().ContainSingle(c => c.Id == result.ContactId && c.AccountUuid == result.AccountUuid && c.Name == "Ahmed Khan");

        var after = await h.Leads.GetByIdAsync(h.Rep(), lead.Uuid);
        after.Status.Should().Be("CONVERTED");
        after.ConvertedAt.Should().NotBeNull();
        after.Account!.Uuid.Should().Be(result.AccountUuid);
        after.ContactPerson!.Id.Should().Be(result.ContactId);
        after.ConvertedOpportunity!.Number.Should().Be(result.OpportunityNumber);

        var opp = await h.Opportunities.GetByIdAsync(h.Rep(), result.OpportunityUuid);
        opp.Topic.Should().Be(lead.Topic);
        opp.Status.Should().Be("OPEN");
        opp.PipelineStageName.Should().Be("Needs Analysis", "the first opportunity stage");
        opp.EstimatedRevenue.Should().Be(280000m);
        opp.CurrencyCode.Should().Be("PKR");
        opp.AssignedToUserId.Should().Be(10);
        opp.SourceLeadNumber.Should().Be(lead.LeadNumber);
        opp.AccountName.Should().Be("PakElectro");
        opp.Pipeline.Select(s => s.Code).Should().Equal("NEEDS_ANALYSIS", "PROPOSAL", "NEGOTIATION", "WON", "LOST");

        h.Events.Of<LeadConvertedEvent>().Should().ContainSingle(e => e.OpportunityId == result.OpportunityUuid && e.AccountId == result.AccountUuid);
        h.Events.Of<OpportunityCreatedEvent>().Should().ContainSingle(e => e.LeadId == lead.Uuid);
    }

    [Fact]
    public async Task A_B2C_lead_converts_into_an_individual_customer()
    {
        using var h = new CrmHarness();
        var lead = await h.QualifiedLeadAsync(lead: CrmHarness.NewLead("Sarah", "Malik", company: null));
        await h.Conversion.ConvertAsync(h.Rep(), lead.Uuid, new LeadConvertRequest());
        h.Accounts.Created.Should().ContainSingle(a => a.Name == "Sarah Malik" && a.CustomerType == "INDIVIDUAL");
    }

    [Fact]
    public async Task TS07_linking_an_existing_account_and_contact_creates_no_partner_and_makes_a_vendor_a_customer()
    {
        using var h = new CrmHarness();
        var account = h.Accounts.AddAccount(h.Org, "PakElectro Pvt Ltd", customer: false, vendor: true);
        var contact = h.Accounts.AddContact(h.Org, account, "Ahmed Khan", "ahmed@pakelec.com");
        var lead = await h.QualifiedLeadAsync();

        var result = await h.Conversion.ConvertAsync(h.Rep(), lead.Uuid, new LeadConvertRequest { ExistingAccountUuid = account, ExistingContactId = contact });
        result.IsNewAccount.Should().BeFalse();
        result.IsNewContact.Should().BeFalse();
        result.AccountUuid.Should().Be(account);
        result.ContactId.Should().Be(contact);
        h.Accounts.Created.Should().BeEmpty();
        h.Accounts.Accounts.Single(a => a.Uuid == account).IsCustomer.Should().BeTrue("the linked partner becomes a customer");
    }

    [Fact]
    public async Task An_existing_account_with_a_new_contact_and_the_link_refusals()
    {
        using var h = new CrmHarness();
        var account = h.Accounts.AddAccount(h.Org, "PakElectro Pvt Ltd");
        var otherAccount = h.Accounts.AddAccount(h.Org, "Other Co");
        var foreignContact = h.Accounts.AddContact(h.Org, otherAccount, "Someone");
        var inactive = h.Accounts.AddAccount(h.Org, "Closed Co", active: false);
        var lead = await h.QualifiedLeadAsync();

        await h.Conversion.Invoking(c => c.ConvertAsync(h.Rep(), lead.Uuid, new LeadConvertRequest { ExistingAccountUuid = account, ExistingContactId = foreignContact }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*another account*");
        await h.Conversion.Invoking(c => c.ConvertAsync(h.Rep(), lead.Uuid, new LeadConvertRequest { ExistingContactId = foreignContact }))
            .Should().ThrowAsync<BadRequestException>();
        await h.Conversion.Invoking(c => c.ConvertAsync(h.Rep(), lead.Uuid, new LeadConvertRequest { ExistingAccountUuid = inactive }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*inactive*");
        await h.Conversion.Invoking(c => c.ConvertAsync(h.Rep(), lead.Uuid, new LeadConvertRequest { ExistingAccountUuid = Guid.NewGuid() }))
            .Should().ThrowAsync<BadRequestException>();

        var result = await h.Conversion.ConvertAsync(h.Rep(), lead.Uuid, new LeadConvertRequest { ExistingAccountUuid = account });
        result.IsNewAccount.Should().BeFalse();
        result.IsNewContact.Should().BeTrue();
        h.Accounts.Contacts.Single(c => c.Id == result.ContactId).AccountUuid.Should().Be(account);
    }

    [Theory]
    [InlineData("NEW")]
    [InlineData("CONTACTED")]
    [InlineData("DISQUALIFIED")]
    public async Task BR05_only_a_qualified_lead_converts(string status)
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        if (status != "NEW") await h.LogCallAsync(lead.Uuid);
        if (status == "DISQUALIFIED") await h.Leads.DisqualifyAsync(h.Rep(), lead.Uuid, "No");
        await h.Conversion.Invoking(c => c.ConvertAsync(h.Rep(), lead.Uuid, new LeadConvertRequest()))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*qualified*");
    }

    [Fact]
    public async Task A_converted_lead_cannot_convert_twice()
    {
        using var h = new CrmHarness();
        var (lead, _) = await h.ConvertedAsync();
        await h.Conversion.Invoking(c => c.ConvertAsync(h.Rep(), lead.Uuid, new LeadConvertRequest())).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task TS08_the_conversion_dialog_lists_possible_duplicate_accounts_exact_name_first()
    {
        using var h = new CrmHarness();
        h.Accounts.AddAccount(h.Org, "PakElectro Pvt Ltd");
        h.Accounts.AddAccount(h.Org, "PakElectro");
        h.Accounts.AddAccount(h.Org, "Unrelated", email: "ahmed@pakelec.com");
        h.Accounts.AddAccount(Guid.NewGuid(), "PakElectro"); // another organization
        var lead = await h.QualifiedLeadAsync();

        var matches = await h.Conversion.AccountMatchesAsync(h.Rep(), lead.Uuid, null);
        matches.Select(m => m.Name).Should().Equal("PakElectro", "PakElectro Pvt Ltd", "Unrelated");
        (await h.Conversion.AccountMatchesAsync(h.Rep(), lead.Uuid, "unrel")).Should().ContainSingle(m => m.Name == "Unrelated");
    }

    [Fact]
    public void BR11_there_is_no_way_to_create_an_opportunity_except_converting_a_lead()
    {
        typeof(IOpportunityService).GetMethods().Should().NotContain(m => m.Name.StartsWith("Create") && m.Name.Contains("Opportunity"));
        typeof(SMS.Modules.Crm.Controllers.OpportunitiesController).GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), false).Length > 0)
            .SelectMany(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.HttpPostAttribute), false).Cast<Microsoft.AspNetCore.Mvc.HttpPostAttribute>())
            .Should().OnlyContain(a => a.Template != null && a.Template.Contains('/'), "every POST acts on an existing opportunity");
    }

    // ── won / lost / reopen ──

    [Fact]
    public async Task TS16_BR12_an_opportunity_without_a_confirmed_sale_order_cannot_be_won()
    {
        using var h = new CrmHarness();
        var (_, conv) = await h.ConvertedAsync();
        await h.Opportunities.Invoking(o => o.MarkWonAsync(h.Rep(), conv.OpportunityUuid)).Should().ThrowAsync<BadRequestException>().WithMessage("*sale order*");
        h.Sales.Add(conv.OpportunityUuid, CrmSalesDocumentKind.Order, "DRAFT", 300000m);
        await h.Opportunities.Invoking(o => o.MarkWonAsync(h.Rep(), conv.OpportunityUuid)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task TS15_BR16_won_takes_the_confirmed_orders_total_and_closes_in_the_won_stage()
    {
        using var h = new CrmHarness();
        var (_, conv) = await h.ConvertedAsync();
        var order = h.Sales.Add(conv.OpportunityUuid, CrmSalesDocumentKind.Order, "CONFIRMED", 285500m, "PKR");

        var won = await h.Opportunities.MarkWonAsync(h.Rep(), conv.OpportunityUuid);
        won.Status.Should().Be("WON");
        won.ActualRevenue.Should().Be(285500m);
        won.ActualCloseDate.Should().Be(DateTime.UtcNow.Date);
        won.PipelineStageName.Should().Be("Won");
        won.AllowedActions.Should().BeEmpty();
        won.SalesDocuments.Should().ContainSingle(d => d.Uuid == order.Uuid);
        h.Events.Of<OpportunityWonEvent>().Should().ContainSingle(e => e.SaleOrderId == order.Uuid && e.ActualRevenue == 285500m);

        await h.Opportunities.Invoking(o => o.ReopenAsync(h.Manager(), conv.OpportunityUuid)).Should().ThrowAsync<ConflictException>();
        await h.Opportunities.Invoking(o => o.MarkLostAsync(h.Manager(), conv.OpportunityUuid, "x", null)).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task TS17_TS18_BR13_lost_needs_a_reason_and_BR14_a_manager_reopens_it()
    {
        using var h = new CrmHarness();
        var (_, conv) = await h.ConvertedAsync();
        await h.Opportunities.Invoking(o => o.MarkLostAsync(h.Rep(), conv.OpportunityUuid, " ", null)).Should().ThrowAsync<BadRequestException>();

        var lost = await h.Opportunities.MarkLostAsync(h.Rep(), conv.OpportunityUuid, "Price too high", "CoolAir");
        lost.Status.Should().Be("LOST");
        lost.WinLossReason.Should().Be("Price too high");
        lost.CompetitorName.Should().Be("CoolAir");
        lost.PipelineStageName.Should().Be("Lost");
        lost.ActualCloseDate.Should().NotBeNull();
        h.Events.Of<OpportunityLostEvent>().Should().ContainSingle(e => e.Reason == "Price too high" && e.Competitor == "CoolAir");

        await h.Opportunities.Invoking(o => o.ReopenAsync(h.Rep(), conv.OpportunityUuid)).Should().ThrowAsync<ForbiddenException>();
        var reopened = await h.Opportunities.ReopenAsync(h.Manager(), conv.OpportunityUuid);
        reopened.Status.Should().Be("OPEN");
        reopened.WinLossReason.Should().BeNull();
        reopened.CompetitorName.Should().BeNull();
        reopened.ActualCloseDate.Should().BeNull();
        reopened.PipelineStageName.Should().Be("Needs Analysis");
    }

    [Fact]
    public async Task Stage_moves_stay_within_the_open_opportunity_stages()
    {
        using var h = new CrmHarness();
        var (_, conv) = await h.ConvertedAsync();
        var opp = await h.Opportunities.GetByIdAsync(h.Rep(), conv.OpportunityUuid);
        var proposal = opp.Pipeline.Single(s => s.Code == "PROPOSAL").Uuid;
        var won = opp.Pipeline.Single(s => s.Code == "WON").Uuid;

        var moved = await h.Opportunities.ChangeStageAsync(h.Rep(), conv.OpportunityUuid, proposal);
        moved.PipelineStageName.Should().Be("Proposal / Quotation");
        moved.WinProbability.Should().Be(60);
        h.Events.Of<OpportunityStageChangedEvent>().Should().ContainSingle(e => e.NewStageId == proposal);
        await h.Opportunities.Invoking(o => o.ChangeStageAsync(h.Rep(), conv.OpportunityUuid, won)).Should().ThrowAsync<BadRequestException>().WithMessage("*Won*");

        var board = await h.Opportunities.GetKanbanAsync(h.Rep(), new OpportunityKanbanFilter());
        board.Columns.Select(c => c.Code).Should().Equal("NEEDS_ANALYSIS", "PROPOSAL", "NEGOTIATION");
        board.Columns[1].Items.Should().ContainSingle(i => i.Uuid == conv.OpportunityUuid);
        board.Columns[1].TotalRevenue.Should().Be(280000m);
    }

    [Fact]
    public async Task A_rep_works_only_their_own_opportunities_and_only_a_manager_gives_one_away()
    {
        using var h = new CrmHarness();
        var (_, conv) = await h.ConvertedAsync(h.Rep(10));
        var update = new OpportunityUpdate { Topic = "Changed", EstimatedRevenue = 1, CurrencyCode = "PKR", AssignedToUserId = 10 };
        await h.Opportunities.Invoking(o => o.UpdateAsync(h.Rep(11), conv.OpportunityUuid, update)).Should().ThrowAsync<ForbiddenException>();
        await h.Opportunities.Invoking(o => o.MarkLostAsync(h.Rep(11), conv.OpportunityUuid, "x", null)).Should().ThrowAsync<ForbiddenException>();
        await h.Opportunities.Invoking(o => o.UpdateAsync(h.Rep(10), conv.OpportunityUuid,
            new OpportunityUpdate { Topic = "Mine", AssignedToUserId = 11 })).Should().ThrowAsync<ForbiddenException>();
        (await h.Opportunities.UpdateAsync(h.Manager(), conv.OpportunityUuid, new OpportunityUpdate { Topic = "Given", AssignedToUserId = 11 }))
            .AssignedToUserId.Should().Be(11);
    }

    // ── SCM documents (D-13) ──

    [Fact]
    public async Task TS13_TS14_inquiry_and_quotation_are_created_for_the_account_and_linked_back()
    {
        using var h = new CrmHarness();
        var (_, conv) = await h.ConvertedAsync();

        var inquiry = await h.Opportunities.CreateInquiryAsync(h.Rep(), conv.OpportunityUuid);
        inquiry.Kind.Should().Be("INQUIRY");
        var quotation = await h.Opportunities.CreateQuotationAsync(h.Rep(), conv.OpportunityUuid);
        quotation.Kind.Should().Be("QUOTATION");
        h.Sales.QuotationCalls.Should().ContainSingle(c => c.Account == conv.AccountUuid && c.FromInquiry == null,
            "the inquiry is still RECEIVED, so the quotation stands on its own");

        var opp = await h.Opportunities.GetByIdAsync(h.Rep(), conv.OpportunityUuid);
        opp.SalesDocuments.Select(d => d.Kind).Should().Equal("INQUIRY", "QUOTATION");
        h.Db.Opportunities.Single().SourceInquiryUuid.Should().Be(inquiry.Uuid);
        h.Db.Opportunities.Single().SourceQuotationUuid.Should().Be(quotation.Uuid);
    }

    [Fact]
    public async Task A_quotation_is_made_from_a_review_complete_inquiry_of_the_opportunity()
    {
        using var h = new CrmHarness();
        var (_, conv) = await h.ConvertedAsync();
        var reviewed = h.Sales.Add(conv.OpportunityUuid, CrmSalesDocumentKind.Inquiry, "REVIEW_COMPLETE");
        await h.Opportunities.CreateQuotationAsync(h.Rep(), conv.OpportunityUuid);
        h.Sales.QuotationCalls.Single().FromInquiry.Should().Be(reviewed.Uuid);
    }

    [Fact]
    public async Task Create_order_hands_over_the_form_prefill_and_the_accepted_quotation()
    {
        using var h = new CrmHarness();
        var (_, conv) = await h.ConvertedAsync();
        var prefill = await h.Opportunities.CreateOrderAsync(h.Rep(), conv.OpportunityUuid);
        prefill.AccountUuid.Should().Be(conv.AccountUuid);
        prefill.OpportunityNumber.Should().Be(conv.OpportunityNumber);
        prefill.AcceptedQuotationUuid.Should().BeNull();

        var accepted = h.Sales.Add(conv.OpportunityUuid, CrmSalesDocumentKind.Quotation, "ACCEPTED", 1000m);
        (await h.Opportunities.CreateOrderAsync(h.Rep(), conv.OpportunityUuid)).AcceptedQuotationUuid.Should().Be(accepted.Uuid);
    }

    [Fact]
    public async Task Sales_documents_need_the_sale_permission_and_Demand_switched_on()
    {
        using var h = new CrmHarness();
        var (_, conv) = await h.ConvertedAsync();
        var noSales = h.With(10, SMS.Shared.Authorization.PermissionCodes.CRM_EDIT_OPPORTUNITY);
        await h.Opportunities.Invoking(o => o.CreateInquiryAsync(noSales, conv.OpportunityUuid)).Should().ThrowAsync<ForbiddenException>();
        h.Gate.Off.Add(ModuleCodes.Demand);
        await h.Opportunities.Invoking(o => o.CreateInquiryAsync(h.Rep(), conv.OpportunityUuid)).Should().ThrowAsync<BadRequestException>().WithMessage("*switched off*");
    }

    // ── cross-references (§12.5, BR-22, TS-23/24) ──

    [Fact]
    public async Task TS23_TS24_cross_references_show_only_the_same_companys_leads_even_for_the_same_person()
    {
        using var h = new CrmHarness();
        var companyA = h.Accounts.AddAccount(h.Org, "Company A");
        var companyB = h.Accounts.AddAccount(h.Org, "Company B");
        var ahmedAtA = h.Accounts.AddContact(h.Org, companyA, "Ahmed Khan");
        var ahmedAtB = h.Accounts.AddContact(h.Org, companyB, "Ahmed Khan");

        async Task<Guid> ConvertInto(Guid account, int contact, string topic)
        {
            var lead = await h.QualifiedLeadAsync(lead: CrmHarness.NewLead(company: "Whatever", topic: topic));
            await h.Conversion.ConvertAsync(h.Rep(), lead.Uuid, new LeadConvertRequest { ExistingAccountUuid = account, ExistingContactId = contact });
            return lead.Uuid;
        }

        var a1 = await ConvertInto(companyA, ahmedAtA, "A first");
        var a2 = await ConvertInto(companyA, ahmedAtA, "A second");
        var b1 = await ConvertInto(companyB, ahmedAtB, "B only");

        var atA = await h.Leads.GetCrossReferencesAsync(h.Rep(), a1);
        atA.Account!.Name.Should().Be("Company A");
        atA.AccountLeads.Select(x => x.Uuid).Should().Equal(a2);
        atA.ContactLeads.Select(x => x.Uuid).Should().Equal(a2);

        var atB = await h.Leads.GetCrossReferencesAsync(h.Rep(), b1);
        atB.AccountLeads.Should().BeEmpty();
        atB.ContactLeads.Should().BeEmpty("Ahmed's Company A leads never show at Company B");

        var unlinked = await h.CreateLeadAsync();
        (await h.Leads.GetCrossReferencesAsync(h.Rep(), unlinked.Uuid)).AccountLeads.Should().BeEmpty();
    }
}

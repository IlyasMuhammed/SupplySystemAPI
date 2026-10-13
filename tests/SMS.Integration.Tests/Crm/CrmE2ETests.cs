using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.RouteClassification;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.ServiceOrders;
using Xunit;

namespace SMS.Integration.Tests.Crm;

/// <summary>
/// A41 (QA) — the CRM end to end on the real host (LocalDB), in a fresh ENTERPRISE organization (MODULE_CRM on from the plan).
/// docs/crm/*.md.
/// <list type="bullet">
/// <item>Lead → contact activity (NEW → CONTACTED, BR-08; notes sanitized) → qualify → convert (new account + contact) →
/// opportunity → inquiry / sale order from it (Demand carries SourceOpportunityUuid, FROM_OPPORTUNITY) → timeline + export → won.</item>
/// <item>Public lead API: X-Api-Key decides the organization, 401 without/with a bad or inactive key, per-key rate limit (429).</item>
/// <item>Own-records rule (D-5), permissions (403) and the module switch.</item>
/// </list>
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~CrmE2ETests</c>.</para>
/// </summary>
public sealed class CrmE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapKit _root;

    public CrmE2ETests(SapWebApplicationFactory factory) => _root = new SapKit(factory, "A41");

    private async Task<(Guid Org, SapKit K, Guid Pkr)> WorldAsync(string prefix)
    {
        var pkr = await _root.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        var (org, k, _) = await _root.SecondOrganizationAsync(prefix);
        await _root.SetOrgBaseCurrencyAsync(org, pkr);
        await k.EnsureOrgCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        return (org, k, pkr);
    }

    private static Task<Api> CreateLead(SapKit k, string tag, HttpClient? client = null, Guid? source = null) =>
        k.Post("/api/crm/leads", new
        {
            firstName = "Sara", lastName = k.Next(tag), topic = $"{tag} 50 pumps", companyName = k.Next($"{tag} Co"),
            email = $"{tag.ToLowerInvariant()}-{Guid.NewGuid():N}@lead.test", phone = "+92 300 1234567",
            estimatedRevenue = 250000m, currencyCode = "PKR", rating = "HOT", leadSourceUuid = source
        }, client);

    [Fact]
    public async Task Lead_to_opportunity_to_sale_order()
    {
        var (_, k, pkr) = await WorldAsync("JRN");

        // Seeded per organization (D-7): sources and both stage sets.
        (await k.Ok(k.Get("/api/crm/meta"), "meta")).A("leadStatuses").Should().HaveCount(5);
        var sources = (await k.Ok(k.Get("/api/crm/lead-sources"), "lead sources")).Items();
        sources.Should().NotBeEmpty("the CRM seeds default lead sources");
        (await k.Ok(k.Get("/api/crm/pipeline-stages?stageType=LEAD"), "lead stages")).Items().Should().NotBeEmpty();
        (await k.Ok(k.Get("/api/crm/pipeline-stages?stageType=OPPORTUNITY"), "opp stages")).Items().Should().NotBeEmpty();

        // Create
        var lead = await k.Ok(CreateLead(k, "JRN", source: sources[0].G("uuid")), "create lead");
        var leadUuid = lead.G("uuid");
        lead.S("leadNumber").Should().StartWith($"LD-{DateTime.UtcNow.Year}-");
        lead.S("status").Should().Be("NEW");
        lead.S("fullName").Should().StartWith("Sara ");

        var dup = await k.Ok(k.Get($"/api/crm/leads/duplicate-check?email={Uri.EscapeDataString(lead.S("email")!)}"), "duplicate check");
        dup.A("leads").Select(d => d.P("lead").G("uuid")).Should().Contain(leadUuid);

        // A contact activity moves NEW → CONTACTED; the notes keep formatting only.
        var activity = await k.Ok(k.Post("/api/crm/activities", new
        {
            leadUuid, activityType = "PHONE_CALL", subject = "Discovery call", outcome = "SUCCESSFUL",
            description = "<p><b>Discussed</b> volumes<script>alert(1)</script></p>",
            followUpDate = DateTime.UtcNow.Date.AddDays(3).ToString("yyyy-MM-dd"), followUpType = "MEETING"
        }), "log a call");
        activity.S("description").Should().Contain("<b>Discussed</b>").And.NotContain("script");
        (await k.Ok(k.Get($"/api/crm/leads/{leadUuid}"), "read lead")).S("status").Should().Be("CONTACTED", "BR-08");
        (await k.Ok(k.Get("/api/crm/activities/pending?mine=true"), "pending")).Items()
            .Should().Contain(p => p.P("activity").G("uuid") == activity.G("uuid"), "the follow-up is due");

        // Qualify → convert (no account picked: a new customer and contact are created)
        (await k.Ok(k.Post($"/api/crm/leads/{leadUuid}/qualify"), "qualify")).S("status").Should().Be("QUALIFIED");
        var conv = await k.Ok(k.Post($"/api/crm/leads/{leadUuid}/convert", new { }), "convert");
        conv.B("isNewAccount").Should().BeTrue();
        conv.S("opportunityNumber").Should().StartWith($"OP-{DateTime.UtcNow.Year}-");
        var opp = conv.G("opportunityUuid");
        var account = conv.G("accountUuid");

        var converted = await k.Ok(k.Get($"/api/crm/leads/{leadUuid}"), "read converted lead");
        converted.S("status").Should().Be("CONVERTED");
        converted.P("convertedOpportunity").G("uuid").Should().Be(opp);
        (await k.Put($"/api/crm/leads/{leadUuid}", new { topic = "x" })).Status.Should().Be(HttpStatusCode.Conflict, "a converted lead is closed");
        (await k.Post($"/api/crm/leads/{leadUuid}/convert", new { })).Status.Should().Be(HttpStatusCode.Conflict, "converted once");

        var contacts = (await k.Ok(k.Get($"/api/crm/accounts/{account}/contacts"), "account contacts")).Items();
        contacts.Should().ContainSingle(c => c.S("name")!.StartsWith("Sara "));
        (await k.Ok(k.Get($"/api/partners/{account}"), "the new customer")).B("isCustomer").Should().BeTrue();

        var detail = await k.Ok(k.Get($"/api/crm/opportunities/{opp}"), "read opportunity");
        detail.S("status").Should().Be("OPEN");
        detail.G("sourceLeadUuid").Should().Be(leadUuid);
        detail.D("estimatedRevenue").Should().Be(250000m);
        var actions = detail.A("allowedActions").Select(a => a.GetString()).ToList();
        actions.Should().Contain(["WON", "LOST", "CREATE_INQUIRY", "CREATE_QUOTATION", "CREATE_ORDER"]);

        // Stage move on the board
        var stages = detail.A("pipeline").Where(s => !s.B("isWon") && !s.B("isLost")).ToList();
        var next = stages.Last();
        (await k.Ok(k.Patch($"/api/crm/opportunities/{opp}/stage", new { stageUuid = next.G("uuid") }), "move stage"))
            .G("pipelineStageUuid").Should().Be(next.G("uuid"));
        var board = await k.Ok(k.Get("/api/crm/opportunities/kanban"), "kanban");
        board.A("columns").Single(c => c.G("stageUuid") == next.G("uuid")).A("items").Select(i => i.G("uuid")).Should().Contain(opp);

        // Sales documents from the opportunity (D-13)
        var inquiry = await k.Ok(k.Post($"/api/crm/opportunities/{opp}/create-inquiry"), "create inquiry");
        inquiry.S("kind").Should().Be("INQUIRY");
        (await k.Ok(k.Get($"/api/sale-inquiries/{inquiry.G("uuid")}"), "read inquiry")).NG("sourceOpportunityUuid").Should().Be(opp);

        var prefill = await k.Ok(k.Post($"/api/crm/opportunities/{opp}/create-order"), "order prefill");
        prefill.G("accountUuid").Should().Be(account);
        prefill.G("opportunityUuid").Should().Be(opp);

        await k.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        await k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await k.CreateApproverPlaceholdersAsync();
        var item = await k.CreateProductAsync("Pump", 100m, 180m);
        await k.StockUpAsync(await k.CreateVendorAsync("Vendor"), await k.CreateWarehouseAsync(), (item, 10m, 100m));
        var so = (await k.Ok(k.Post("/api/sale-orders", new
        {
            PartnerId = account, CurrencyId = pkr, DeliveryMode = "SELF_PICKUP", SourceOpportunityUuid = opp,
            Lines = new[] { SapKit.Line(item, 2m) }
        }), "sale order from the opportunity")).GetGuid();
        var order = await k.GetSaleOrderAsync(so);
        order.S("sourceType").Should().Be("FROM_OPPORTUNITY");
        order.NG("sourceOpportunityUuid").Should().Be(opp);

        detail = await k.Ok(k.Get($"/api/crm/opportunities/{opp}"), "read opportunity again");
        detail.A("salesDocuments").Select(d => d.S("kind")).Should().Contain(["INQUIRY", "ORDER"]);

        // Timeline: the lead's history carried over, plus the SCM documents
        var timeline = (await k.Ok(k.Get($"/api/crm/opportunities/{opp}/timeline"), "timeline")).A("timeline");
        var kinds = timeline.Select(t => t.S("entryType")).ToList();
        kinds.Should().Contain(["CONVERTED", "ACTIVITY", "SCM_DOCUMENT"]);
        timeline.Single(t => t.S("entryType") == "ACTIVITY").B("fromLead").Should().BeTrue();

        using (var export = await k.Admin.GetAsync($"/api/crm/opportunities/{opp}/timeline/export?format=xlsx"))
        {
            export.StatusCode.Should().Be(HttpStatusCode.OK);
            export.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            var bytes = await export.Content.ReadAsByteArrayAsync();
            bytes.Take(2).Should().Equal((byte)'P', (byte)'K');
        }

        // BR-12: won only once its sale order is confirmed; BR-16: the order's total becomes the actual revenue.
        (await k.Post($"/api/crm/opportunities/{opp}/won")).Status.Should().Be(HttpStatusCode.BadRequest, "the order is still a draft");
        await k.Ok(k.Post($"/api/sale-orders/{so}/confirm"), "confirm the sale order");
        var won = await k.Ok(k.Post($"/api/crm/opportunities/{opp}/won"), "won");
        won.S("status").Should().Be("WON");
        won.D("actualRevenue").Should().Be((await k.GetSaleOrderAsync(so)).D("grandTotal"));
        (await k.Post($"/api/crm/opportunities/{opp}/lost", new { reason = "x" })).Status.Should().NotBe(HttpStatusCode.OK);
        (await k.Post($"/api/crm/opportunities/{opp}/reopen")).Status.Should().Be(HttpStatusCode.Conflict, "a won opportunity is closed for good");
    }

    [Fact]
    public async Task Public_lead_api_keys_the_organization_and_is_rate_limited()
    {
        var (org, k, _) = await WorldAsync("PUB");
        var created = await k.Ok(k.Post("/api/crm/api-keys", new { name = "Website", requestsPerMinute = 2 }), "create api key");
        var key = created.S("key")!;
        var keyUuid = created.P("apiKey").G("uuid");
        (await k.Ok(k.Get("/api/crm/api-keys"), "list keys")).Items().Single().S("keyPrefix").Should().Be(key[..12]);
        (await k.Ok(k.Get("/api/crm/api-keys"), "list keys")).GetRawText().Should().NotContain(key, "the secret is shown once");

        using var anon = _root.F.CreateAnonymousClient();
        async Task<Api> Send(string? apiKey, string email)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/public/crm/leads");
            if (apiKey is not null) req.Headers.Add("X-Api-Key", apiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["first_name"] = "Web", ["last_name"] = "Visitor", ["company_name"] = "Inbound Ltd", ["email"] = email,
                ["topic"] = "Pricing request", ["source_medium"] = "contact-form"
            }), Encoding.UTF8, "application/json");
            using var resp = await anon.SendAsync(req);
            var raw = await resp.Content.ReadAsStringAsync();
            var body = string.IsNullOrWhiteSpace(raw) ? JsonDocument.Parse("{}").RootElement.Clone() : JsonDocument.Parse(raw).RootElement.Clone();
            return new Api(resp.StatusCode, body, raw);
        }

        (await Send(null, "a@web.test")).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await Send("sk_not_a_key", "a@web.test")).Status.Should().Be(HttpStatusCode.Unauthorized);

        var email = $"web-{Guid.NewGuid():N}@web.test";
        var ok = await Send(key, email);
        ok.Status.Should().Be(HttpStatusCode.OK, ok.ToString());
        ok.Result.S("leadNumber").Should().StartWith("LD-");
        var leadUuid = ok.Result.G("leadId");

        var rows = await _root.F.QueryAsync("SELECT OrganizationId, Status, SourceMedium FROM crm.Leads WHERE Uuid = @u", ("@u", leadUuid));
        rows.Should().ContainSingle();
        rows[0]["OrganizationId"].Should().Be(org, "the key alone decides the organization");
        rows[0]["Status"].Should().Be("NEW");
        rows[0]["SourceMedium"].Should().Be("contact-form");
        var list = await k.Ok(k.Get($"/api/crm/leads?search={Uri.EscapeDataString(email)}"), "the organization sees it");
        list.A("data").Select(l => l.G("uuid")).Should().Contain(leadUuid);

        (await Send(key, $"w2-{Guid.NewGuid():N}@web.test")).Status.Should().Be(HttpStatusCode.OK, "second of two a minute");
        (await Send(key, $"w3-{Guid.NewGuid():N}@web.test")).Status.Should().Be((HttpStatusCode)429, "the third in the minute is over the key's limit");

        await k.Ok(k.Put($"/api/crm/api-keys/{keyUuid}", new { isActive = false }), "deactivate key");
        (await Send(key, $"w4-{Guid.NewGuid():N}@web.test")).Status.Should().Be(HttpStatusCode.Unauthorized, "an inactive key");
    }

    [Fact]
    public async Task Own_records_permissions_and_the_module_switch()
    {
        var (org, k, _) = await WorldAsync("PRM");
        var rep = await k.LoginWithPermissionsAsync("REP",
            "CRM_VIEW_LEADS", "CRM_CREATE_LEAD", "CRM_EDIT_LEAD", "CRM_QUALIFY_LEAD", "CRM_VIEW_ACTIVITIES", "CRM_CREATE_ACTIVITY");
        var nobody = await k.LoginWithPermissionsAsync("NONE", "SALE_ORDER_VIEW");

        // The rep owns what they create; the admin's lead is visible but not theirs to change (D-5).
        var mine = await k.Ok(CreateLead(k, "REP", rep), "rep creates a lead");
        var theirs = await k.Ok(CreateLead(k, "ADM"), "admin creates a lead");
        var mineUuid = mine.G("uuid");
        var theirsUuid = theirs.G("uuid");

        var asRep = await k.Ok(k.Get($"/api/crm/leads/{theirsUuid}", rep), "rep reads the admin's lead");
        asRep.B("canEdit").Should().BeFalse();
        asRep.A("allowedActions").Should().BeEmpty();
        (await k.Put($"/api/crm/leads/{theirsUuid}", new { topic = "hijack" }, rep)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await k.Post($"/api/crm/leads/{theirsUuid}/qualify", null, rep)).Status.Should().Be(HttpStatusCode.Forbidden, "not theirs, whatever its status");
        (await k.Post($"/api/crm/leads/{mineUuid}/qualify", null, rep)).Status.Should().Be(HttpStatusCode.BadRequest, "NEW → QUALIFIED skips first contact");
        await k.Ok(k.Post("/api/crm/activities", new { leadUuid = mineUuid, activityType = "MEETING", subject = "Intro" }, rep), "rep logs a meeting");
        (await k.Ok(k.Post($"/api/crm/leads/{mineUuid}/qualify", null, rep), "rep qualifies their own")).S("status").Should().Be("QUALIFIED");

        // Codes the rep does not hold
        (await k.Post($"/api/crm/leads/{mineUuid}/convert", new { }, rep)).Status.Should().Be(HttpStatusCode.Forbidden, "no CRM_CONVERT_LEAD");
        (await k.Patch($"/api/crm/leads/{mineUuid}/assign", new { userId = (int?)null }, rep)).Status.Should().Be(HttpStatusCode.Forbidden, "no CRM_ASSIGN_LEAD");
        (await k.Get("/api/crm/opportunities", rep)).Status.Should().Be(HttpStatusCode.Forbidden, "no CRM_VIEW_OPPORTUNITIES");
        (await k.Get("/api/crm/api-keys", rep)).Status.Should().Be(HttpStatusCode.Forbidden, "no CRM_ADMIN");
        (await k.Get("/api/crm/leads", nobody)).Status.Should().Be(HttpStatusCode.Forbidden, "no CRM code at all");

        // A manager assigns the admin's lead to the rep: now it is theirs.
        await _root.EnsureNotificationsTableAsync();
        var repId = ServiceKit.AdminUserId(rep);
        (await k.Ok(k.Patch($"/api/crm/leads/{theirsUuid}/assign", new { userId = repId }), "assign to rep")).I("assignedToUserId").Should().Be(repId);
        (await k.Ok(k.Get($"/api/crm/leads/{theirsUuid}", rep), "rep reads it now")).B("canEdit").Should().BeTrue();
        var inbox = await k.Ok(k.Get("/api/notifications?page=1&pageSize=50", rep), "rep's notifications");
        inbox.GetRawText().Should().Contain(theirs.S("leadNumber")!, "D-17: the new owner is told");

        // Another organization cannot see these leads.
        var (_, other, _) = await WorldAsync("OTH");
        (await other.Get($"/api/crm/leads/{mineUuid}")).Status.Should().Be(HttpStatusCode.NotFound);

        // Module off: writes refused (A37 D-6: reads stay).
        await _root.SetFeatureAsync(org, "MODULE_CRM", false);
        (await CreateLead(k, "OFF")).Status.Should().Be(HttpStatusCode.Forbidden, "MODULE_CRM is off");
        await _root.SetFeatureAsync(org, "MODULE_CRM", true);
        (await CreateLead(k, "ON")).Status.Should().Be(HttpStatusCode.OK, "MODULE_CRM back on");
    }
}

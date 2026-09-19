using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Visibility;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Visibility;

// T-62 — the consignee's own view, addressed by an opaque per-consignment token (decision G11).
// The module's second anonymous endpoint, and the one place where getting the payload wrong means
// showing a stranger somebody's address.
public class PublicTrackingTests
{
    private const int User = 42;

    private static readonly DateTime T0 = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>A tenant that exists, is active, and has Logistics switched on.</summary>
    private sealed class Tenants : ITenantSnapshotProvider
    {
        public bool IsActive  { get; set; } = true;
        public bool HasModule { get; set; } = true;
        public bool Exists    { get; set; } = true;

        public Task<TenantSnapshot?> GetSnapshotAsync(Guid organizationId) =>
            Task.FromResult(Exists
                ? new TenantSnapshot(IsActive, HasModule
                    ? new HashSet<string> { "MODULE_LOGISTICS" }
                    : new HashSet<string>())
                : null);

        public void Invalidate(Guid organizationId) { }
    }

    private sealed record Harness(
        LogisticsDbContext Db, PublicTrackingService Tracking, Tenants Tenant, string DbName);

    private static Harness NewHarness()
    {
        var (db, _, dbName) = LogisticsTestDb.New();
        var tenants = new Tenants();
        return new Harness(db, new PublicTrackingService(db, tenants), tenants, dbName);
    }

    private static async Task<Guid> NewConsignment(
        Harness h, string status = "IN_TRANSIT", string carrierName = "Beta Road",
        DateTime? eta = null, DateTime? arrivedAt = null)
    {
        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CarrierName = carrierName, Status = status,
            MasterAwb = "AWB-SECRET-1", CarrierReference = "REF-9",
            Eta = eta, ActualArrivalAt = arrivedAt,
            CodAmount = 5000m, CodCurrency = "PKR",
            FreightCost = 1260m, FreightCurrency = "PKR",
            CreatedBy = User, CreatedDate = T0
        };

        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return consignment.UUID;
    }

    private static async Task NewEvent(
        Harness h, Guid consignmentUuid, string milestone,
        DateTime occurredAt, string? location = null, string? description = null,
        string? signedBy = null)
    {
        var consignment = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == consignmentUuid);

        h.Db.ConsignmentTrackingEvents.Add(new ConsignmentTrackingEvent
        {
            UUID = Guid.NewGuid(), ConsignmentId = consignment.Id,
            Milestone = milestone, OccurredAt = occurredAt, ReceivedAt = occurredAt,
            Location = location, Description = description, SignedBy = signedBy,
            CarrierStatus = "XX01",
            Source = LogisticsCode.Of(TrackingEventSource.Webhook),
            EventKey = Guid.NewGuid().ToString("N").PadRight(64, '0')[..64],
            CreatedDate = occurredAt
        });

        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    private static async Task<string> Issue(Harness h, Guid consignmentUuid) =>
        (await h.Tracking.IssueAsync(consignmentUuid, User))!.Token;

    // ── Issuing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_link_is_256_bits_of_randomness_and_no_two_are_alike()
    {
        var h = NewHarness();
        var first  = await Issue(h, await NewConsignment(h));
        var second = await Issue(h, await NewConsignment(h));

        first.Should().HaveLength(64, "32 bytes, hex-encoded");
        first.Should().MatchRegex("^[0-9a-f]{64}$");
        first.Should().NotBe(second);
    }

    [Fact]
    public async Task The_link_is_relative_because_this_module_does_not_know_its_own_host()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);

        var link = await h.Tracking.IssueAsync(consignment, User);

        link!.Path.Should().Be($"/track/{link.Token}");
        link.Path.Should().NotContain("http");
    }

    [Fact]
    public async Task Re_issuing_replaces_the_old_link_and_says_so()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var first = await Issue(h, consignment);

        var second = await h.Tracking.IssueAsync(consignment, User);

        second!.ReplacedPrevious.Should().BeTrue();
        second.Token.Should().NotBe(first);

        (await h.Tracking.TrackAsync(first)).Should().BeNull(
            "a link sent to the wrong person is dealt with by re-issuing");
        (await h.Tracking.TrackAsync(second.Token)).Should().NotBeNull();
    }

    [Fact]
    public async Task Revoking_stops_the_page_working()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var token = await Issue(h, consignment);

        (await h.Tracking.RevokeAsync(consignment, User)).Should().BeTrue();

        (await h.Tracking.TrackAsync(token)).Should().BeNull();
        (await h.Tracking.GetLinkAsync(consignment)).Should().BeNull();
    }

    [Fact]
    public async Task Reading_a_link_never_creates_one()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);

        (await h.Tracking.GetLinkAsync(consignment)).Should().BeNull();

        var stored = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == consignment);
        stored.TrackingToken.Should().BeNull();
    }

    [Fact]
    public async Task Issuing_against_a_consignment_that_is_not_there_returns_nothing()
    {
        var h = NewHarness();

        (await h.Tracking.IssueAsync(Guid.NewGuid(), User)).Should().BeNull();
        (await h.Tracking.RevokeAsync(Guid.NewGuid(), User)).Should().BeFalse();
    }

    // ── What the page shows ───────────────────────────────────────────────────

    [Fact]
    public async Task The_page_says_where_the_goods_are_in_words_a_consignee_would_use()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, status: "OUT_FOR_DELIVERY", eta: T0.AddDays(1));
        var token = await Issue(h, consignment);

        var page = await h.Tracking.TrackAsync(token);

        page!.Status.Should().Be("Out for delivery", "not OUT_FOR_DELIVERY");
        page.Carrier.Should().Be("Beta Road");
        page.EstimatedArrival.Should().Be(T0.AddDays(1));
        page.Reference.Should().StartWith("SHP-2026-");
    }

    [Fact]
    public async Task The_timeline_reads_oldest_first_in_plain_words()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var token = await Issue(h, consignment);

        await NewEvent(h, consignment, "OUT_FOR_DELIVERY", T0.AddHours(6), location: "Karachi");
        await NewEvent(h, consignment, "PICKED_UP",        T0,             location: "Lahore hub");
        await NewEvent(h, consignment, "IN_TRANSIT",       T0.AddHours(3));

        var page = await h.Tracking.TrackAsync(token);

        page!.Events.Select(e => e.Status)
            .Should().Equal("Collected", "In transit", "Out for delivery");
        page.Events[0].Location.Should().Be("Lahore hub");
        page.LastUpdatedAt.Should().Be(T0.AddHours(6));
    }

    [Fact]
    public async Task A_delivery_says_when_it_arrived()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, status: "DELIVERED", arrivedAt: T0.AddDays(2));
        var token = await Issue(h, consignment);

        var page = await h.Tracking.TrackAsync(token);

        page!.Status.Should().Be("Delivered");
        page.DeliveredAt.Should().Be(T0.AddDays(2));
    }

    [Fact]
    public async Task A_consignment_still_moving_does_not_claim_an_arrival_time()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, status: "IN_TRANSIT", arrivedAt: T0);
        var token = await Issue(h, consignment);

        (await h.Tracking.TrackAsync(token))!.DeliveredAt.Should().BeNull();
    }

    [Fact]
    public async Task An_internal_status_never_reaches_the_public_as_its_code()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, status: "LABEL_READY");
        var token = await Issue(h, consignment);

        var page = await h.Tracking.TrackAsync(token);

        page!.Status.Should().Be("In progress");
        page.Status.Should().NotContain("LABEL");
    }

    [Fact]
    public async Task A_milestone_nobody_has_decided_about_is_dropped_rather_than_shown_as_a_code()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var token = await Issue(h, consignment);

        await NewEvent(h, consignment, "PICKED_UP",        T0);
        // Means something to us and nothing to the person waiting in.
        await NewEvent(h, consignment, "RETURN_INITIATED", T0.AddHours(1));

        var page = await h.Tracking.TrackAsync(token);

        page!.Events.Should().ContainSingle().Which.Status.Should().Be("Collected");
    }

    [Fact]
    public async Task The_timeline_is_capped()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var token = await Issue(h, consignment);

        for (var i = 0; i < PublicTrackingService.MaxEvents + 15; i++)
            await NewEvent(h, consignment, "IN_TRANSIT", T0.AddHours(i));

        var page = await h.Tracking.TrackAsync(token);

        page!.Events.Should().HaveCount(PublicTrackingService.MaxEvents);
        page.Events[^1].OccurredAt.Should()
            .Be(T0.AddHours(PublicTrackingService.MaxEvents + 14), "the newest are the ones kept");
    }

    // ── What the page must never show ─────────────────────────────────────────

    [Fact]
    public async Task The_page_carries_no_address_no_value_and_no_airway_bill()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var token = await Issue(h, consignment);

        var page = await h.Tracking.TrackAsync(token);

        var fields = typeof(PublicTrackingModel).GetProperties().Select(p => p.Name).ToList();

        fields.Should().NotContain(
            ["ShipToAddress", "ShipFromAddress", "MasterAwb", "CarrierReference",
             "CodAmount", "FreightCost", "ConsigneeName", "OrganizationId"],
            "the payload is a whitelist, so nothing added to Consignment later can leak here");

        // The whitelist is the guard; this is the assertion that it actually held.
        System.Text.Json.JsonSerializer.Serialize(page)
            .Should().NotContain("AWB-SECRET-1").And.NotContain("5000");
    }

    [Fact]
    public async Task A_carriers_own_scan_description_is_never_shown()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var token = await Issue(h, consignment);

        await NewEvent(h, consignment, "DELIVERED", T0,
            description: "Left with Mrs Khan at 14 Jubilee Road",
            signedBy: "Mrs Khan");

        var page = await h.Tracking.TrackAsync(token);

        var json = System.Text.Json.JsonSerializer.Serialize(page);

        json.Should().NotContain("Jubilee Road",
            "a carrier's free text is whatever the carrier chose to write, and carriers write addresses in it");
        json.Should().NotContain("Mrs Khan");
    }

    // ── Getting in ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_wrong_token_gets_nothing()
    {
        var h = NewHarness();
        await Issue(h, await NewConsignment(h));

        (await h.Tracking.TrackAsync(new string('a', 64))).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    public async Task A_token_that_is_not_even_the_right_shape_costs_nothing_to_refuse(string token)
    {
        var h = NewHarness();
        await Issue(h, await NewConsignment(h));

        (await h.Tracking.TrackAsync(token)).Should().BeNull();
    }

    [Fact]
    public async Task A_four_kilobyte_token_is_refused_before_the_database_is_touched()
    {
        var h = NewHarness();

        (await h.Tracking.TrackAsync(new string('a', 4096))).Should().BeNull();
    }

    [Fact]
    public async Task Case_does_not_matter_because_links_get_retyped()
    {
        var h = NewHarness();
        var token = await Issue(h, await NewConsignment(h));

        (await h.Tracking.TrackAsync(token.ToUpperInvariant())).Should().NotBeNull();
        (await h.Tracking.TrackAsync($"  {token}  ")).Should().NotBeNull();
    }

    [Fact]
    public async Task A_deactivated_organization_answers_exactly_as_a_wrong_token_does()
    {
        var h = NewHarness();
        var token = await Issue(h, await NewConsignment(h));

        h.Tenant.IsActive = false;

        (await h.Tracking.TrackAsync(token)).Should().BeNull(
            "a different answer would confirm the token was right");
    }

    [Fact]
    public async Task An_organization_without_the_module_answers_the_same_way()
    {
        var h = NewHarness();
        var token = await Issue(h, await NewConsignment(h));

        h.Tenant.HasModule = false;

        (await h.Tracking.TrackAsync(token)).Should().BeNull(
            "an anonymous request bypasses the feature filter, so this is checked by hand");
    }

    [Fact]
    public async Task An_organization_that_no_longer_exists_answers_the_same_way()
    {
        var h = NewHarness();
        var token = await Issue(h, await NewConsignment(h));

        h.Tenant.Exists = false;

        (await h.Tracking.TrackAsync(token)).Should().BeNull();
    }

    [Fact]
    public async Task A_deleted_consignment_answers_the_same_way()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var token = await Issue(h, consignment);

        var row = await h.Db.Consignments.SingleAsync(c => c.UUID == consignment);
        row.IsDelete = true;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        (await h.Tracking.TrackAsync(token)).Should().BeNull();
    }

    [Fact]
    public async Task The_page_opens_without_any_tenant_at_all()
    {
        var h = NewHarness();
        var token = await Issue(h, await NewConsignment(h));

        // A stranger's context: a different organization, which is what an anonymous request
        // actually has. The token is the only thing identifying anything.
        var anonymous = new PublicTrackingService(
            LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid()), h.Tenant);

        (await anonymous.TrackAsync(token)).Should().NotBeNull(
            "the consignee has no organization, and the page still has to open");
    }
}

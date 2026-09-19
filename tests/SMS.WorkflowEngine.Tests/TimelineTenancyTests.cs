using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Domain;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.WorkflowEngine.Tests;

/// <summary>
/// A29-P5-08 §13.7 — timeline tenancy: org filter on DocumentTimelines, (org_id, trace_id) unique,
/// org id from the caller's own tenant context. Every test here shares ONE database between two
/// organizations, the only way to actually exercise the tenant filter rather than assume it.
/// </summary>
public class TimelineTenancyTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static WorkflowDbContext Open(string dbName, StaticTenantContext tenant) =>
        new(new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options, tenant);

    private static TimelineService ServiceFor(string dbName, Guid org, bool superAdmin = false)
    {
        // GetTimelineDetailAsync resolves actor names; nobody here needs one.
        var users = new Mock<IUserQueryService>();
        users.Setup(u => u.GetUsersAsync(It.IsAny<IReadOnlyList<int>>())).ReturnsAsync([]);
        return new(Open(dbName, new StaticTenantContext { OrganizationId = org, IsSuperAdmin = superAdmin }), users.Object, []);
    }

    private static TimelineEvent Event(string type) =>
        new(type, "SO", Guid.NewGuid(), "SO-2026-00042", DateTime.UtcNow, 1, null);

    // ── Index shape (the migration's target) ─────────────────────────────────

    [Fact]
    public void Trace_id_is_unique_per_organization_and_only_indexed_alone_not_unique()
    {
        using var db = Open(Guid.NewGuid().ToString(), new StaticTenantContext());
        var indexes = db.Model.FindEntityType(typeof(DocumentTimeline))!.GetIndexes().ToList();

        var composite = indexes.Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(DocumentTimeline.OrganizationId), nameof(DocumentTimeline.TraceId)]));
        composite.IsUnique.Should().BeTrue("§17.2: DocumentTimelines (org, trace_id) UNIQUE");

        var traceOnly = indexes.Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(DocumentTimeline.TraceId)]));
        traceOnly.IsUnique.Should().BeFalse("a global unique on trace_id would let one tenant's trace block another's");
    }

    // ── Isolation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_organizations_timeline_is_invisible_to_another_by_every_read()
    {
        var db = Guid.NewGuid().ToString();
        var trace = Guid.NewGuid();
        await ServiceFor(db, OrgA).AppendEventAsync(trace, Event("SO_CREATED"), "SO", "SO-2026-00042");

        var asB = ServiceFor(db, OrgB);

        (await asB.GetTimelineDetailAsync(trace)).Should().BeNull();
        (await asB.GetTimelineAsync(trace)).Should().BeEmpty();
        (await ServiceFor(db, OrgA).GetTimelineAsync(trace)).Should().ContainSingle();
    }

    [Fact]
    public async Task The_same_trace_id_in_two_organizations_keeps_two_separate_timelines()
    {
        var db = Guid.NewGuid().ToString();
        var trace = Guid.NewGuid();
        var asA = ServiceFor(db, OrgA);
        var asB = ServiceFor(db, OrgB);

        await asA.AppendEventAsync(trace, Event("SO_CREATED"), "SO", "SO-A");
        await asB.AppendEventAsync(trace, Event("PR_CREATED"), "PR", "PR-B");
        await asA.AppendEventAsync(trace, Event("SO_CONFIRMED"));

        (await asA.GetTimelineAsync(trace)).Select(e => e.EventType).Should().Equal("SO_CREATED", "SO_CONFIRMED");
        (await asB.GetTimelineAsync(trace)).Select(e => e.EventType).Should().Equal("PR_CREATED");

        await using var raw = Open(db, new StaticTenantContext { IsSuperAdmin = true });
        (await raw.DocumentTimelines.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_new_timeline_row_is_stamped_with_the_callers_own_organization()
    {
        var db = Guid.NewGuid().ToString();
        var trace = Guid.NewGuid();

        await ServiceFor(db, OrgA).AppendEventAsync(trace, Event("SO_CREATED"));

        await using var raw = Open(db, new StaticTenantContext { IsSuperAdmin = true });
        (await raw.DocumentTimelines.SingleAsync()).OrganizationId.Should().Be(OrgA);
    }

    [Fact]
    public async Task A_super_admin_bypasses_the_filter_and_can_read_any_organizations_timeline()
    {
        var db = Guid.NewGuid().ToString();
        var trace = Guid.NewGuid();
        await ServiceFor(db, OrgA).AppendEventAsync(trace, Event("SO_CREATED"));

        (await ServiceFor(db, OrgB, superAdmin: true).GetTimelineDetailAsync(trace)).Should().NotBeNull();
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.WorkflowEngine.Tests;

/// <summary>
/// A29-P5-04 §13.7 — "Hangfire jobs get org id as a parameter."
/// <para>
/// TenantPropagatingJobFilter only captures an organization from an HTTP request's claims at
/// enqueue time. A job enqueued from inside another Hangfire job (no request, no claims) therefore
/// hands its own job nothing, and TenantContext falls back to the default organization for anything
/// that job creates. These tests use a tenant context that behaves exactly as the real one does in a
/// Hangfire worker — ambient scope if set, the default org otherwise — to prove the explicit-org
/// overload gets a new timeline row stamped with the right organization regardless.
/// </para>
/// </summary>
public class TimelineAppendJobTests
{
    private static readonly Guid DefaultOrg = Guid.NewGuid(); // stands in for TenantDefaults.ScmDemoOrganizationId

    // Mirrors SMS.Shared's internal TenantContext for the no-HttpContext (Hangfire worker) case.
    private sealed class WorkerTenantContext : ITenantContext
    {
        public Guid OrganizationId => HangfireTenantScope.OrganizationId ?? DefaultOrg;
        public bool IsSuperAdmin   => HangfireTenantScope.OrganizationId is null;
    }

    private static WorkflowDbContext NewDb() =>
        new(new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options, new WorkerTenantContext());

    private static TimelineAppendJob NewJob(WorkflowDbContext db) =>
        new(new TimelineService(db, Mock.Of<IUserQueryService>(), []));

    private static TimelineEvent Event() =>
        new("PO_CREATED_FROM_SO", "PO", Guid.NewGuid(), "PO-2026-00001", DateTime.UtcNow, 7, "40 units of CBL-4MM");

    private static async Task<Guid> OrgOfOnlyRow(WorkflowDbContext db) =>
        (await db.DocumentTimelines.IgnoreQueryFilters().AsNoTracking().SingleAsync()).OrganizationId;

    [Fact]
    public async Task An_explicit_org_stamps_a_new_timeline_row_even_with_no_ambient_scope()
    {
        var db = NewDb();
        var org = Guid.NewGuid();
        HangfireTenantScope.OrganizationId = null;

        await NewJob(db).AppendAsync(Guid.NewGuid(), Event(), null, null, org);

        (await OrgOfOnlyRow(db)).Should().Be(org);
    }

    [Fact]
    public async Task Without_an_explicit_org_a_new_row_created_outside_a_request_lands_under_the_default_org()
    {
        // The defect this task exists to avoid, pinned so the contrast is on record.
        var db = NewDb();
        HangfireTenantScope.OrganizationId = null;

        await NewJob(db).AppendAsync(Guid.NewGuid(), Event());

        (await OrgOfOnlyRow(db)).Should().Be(DefaultOrg);
    }

    [Fact]
    public async Task An_explicit_org_wins_over_a_different_ambient_one()
    {
        var db = NewDb();
        var explicitOrg = Guid.NewGuid();
        HangfireTenantScope.OrganizationId = Guid.NewGuid();

        await NewJob(db).AppendAsync(Guid.NewGuid(), Event(), null, null, explicitOrg);

        (await OrgOfOnlyRow(db)).Should().Be(explicitOrg);
        HangfireTenantScope.OrganizationId = null;
    }

    [Fact]
    public async Task The_previous_ambient_scope_is_put_back_afterwards()
    {
        var db = NewDb();
        var ambient = Guid.NewGuid();
        HangfireTenantScope.OrganizationId = ambient;

        await NewJob(db).AppendAsync(Guid.NewGuid(), Event(), null, null, Guid.NewGuid());

        HangfireTenantScope.OrganizationId.Should().Be(ambient);
        HangfireTenantScope.OrganizationId = null;
    }

    [Fact]
    public async Task No_ambient_scope_stays_none_afterwards()
    {
        var db = NewDb();
        HangfireTenantScope.OrganizationId = null;

        await NewJob(db).AppendAsync(Guid.NewGuid(), Event(), null, null, Guid.NewGuid());

        HangfireTenantScope.OrganizationId.Should().BeNull();
    }

    [Fact]
    public async Task The_scope_is_restored_even_when_the_append_throws()
    {
        var ambient = Guid.NewGuid();
        HangfireTenantScope.OrganizationId = ambient;
        var timeline = new Mock<ITimelineService>();
        timeline.Setup(t => t.AppendEventAsync(It.IsAny<Guid>(), It.IsAny<TimelineEvent>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var act = () => new TimelineAppendJob(timeline.Object).AppendAsync(Guid.NewGuid(), Event(), null, null, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>();
        HangfireTenantScope.OrganizationId.Should().Be(ambient);
        HangfireTenantScope.OrganizationId = null;
    }

    [Fact]
    public async Task The_existing_four_argument_append_still_uses_whatever_scope_the_filter_set()
    {
        var db = NewDb();
        var filterSetOrg = Guid.NewGuid();
        HangfireTenantScope.OrganizationId = filterSetOrg;

        await NewJob(db).AppendAsync(Guid.NewGuid(), Event());

        (await OrgOfOnlyRow(db)).Should().Be(filterSetOrg);
        HangfireTenantScope.OrganizationId = null;
    }

    [Fact]
    public async Task Appending_to_an_existing_trace_with_an_explicit_org_extends_it_rather_than_duplicating()
    {
        var db = NewDb();
        var org = Guid.NewGuid();
        var trace = Guid.NewGuid();
        HangfireTenantScope.OrganizationId = null;
        var job = NewJob(db);

        await job.AppendAsync(trace, Event(), "SO", "SO-2026-00042", org);
        await job.AppendAsync(trace, Event(), null, null, org);

        (await db.DocumentTimelines.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        var events = await new TimelineService(db, Mock.Of<IUserQueryService>(), []).GetTimelineAsync(trace);
        events.Should().HaveCount(2);
    }
}

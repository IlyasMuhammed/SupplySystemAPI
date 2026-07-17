using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.WorkflowEngine.Tests;

// ── Test builder ──────────────────────────────────────────────────────────────

file static class TimelineBuild
{
    internal static WorkflowDbContext NewDb(string? dbName = null) =>
        new(new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    internal static TimelineService NewService(WorkflowDbContext db, IEnumerable<ITraceIdResolver>? resolvers = null) =>
        new(db, resolvers ?? []);

    internal static TimelineEvent Event(
        string eventType = "PR_CREATED", string interfaceCode = "PR", Guid? documentId = null, DateTime? occurredAt = null) =>
        new(eventType, interfaceCode, documentId ?? Guid.NewGuid(), "PR-2026-00001", occurredAt ?? DateTime.UtcNow, PerformedBy: 1, Notes: null);
}

// ── AppendEventAsync ─────────────────────────────────────────────────────────

public class AppendEventAsync_Tests
{
    [Fact]
    public async Task Append_On_New_TraceId_Creates_Row_With_Single_Element_Array()
    {
        var svc     = TimelineBuild.NewService(TimelineBuild.NewDb());
        var traceId = Guid.NewGuid();

        await svc.AppendEventAsync(traceId, TimelineBuild.Event(), chainRootType: "PR", chainRootRef: "PR-2026-00001");

        var events = await svc.GetTimelineAsync(traceId);
        events.Should().ContainSingle();
    }

    [Fact]
    public async Task Append_On_Existing_TraceId_Extends_The_Array()
    {
        var svc     = TimelineBuild.NewService(TimelineBuild.NewDb());
        var traceId = Guid.NewGuid();

        await svc.AppendEventAsync(traceId, TimelineBuild.Event("PR_CREATED"));
        await svc.AppendEventAsync(traceId, TimelineBuild.Event("PR_APPROVED"));

        var events = await svc.GetTimelineAsync(traceId);
        events.Should().HaveCount(2);
        events.Select(e => e.EventType).Should().BeEquivalentTo(["PR_CREATED", "PR_APPROVED"]);
    }

    [Fact]
    public async Task FirstEventAt_Never_Changes_While_LastEventAt_Updates_On_Every_Append()
    {
        var db      = TimelineBuild.NewDb();
        var svc     = TimelineBuild.NewService(db);
        var traceId = Guid.NewGuid();

        await svc.AppendEventAsync(traceId, TimelineBuild.Event());
        var afterFirst = await db.DocumentTimelines.AsNoTracking().FirstAsync(t => t.TraceId == traceId);

        await Task.Delay(15);
        await svc.AppendEventAsync(traceId, TimelineBuild.Event());
        var afterSecond = await db.DocumentTimelines.AsNoTracking().FirstAsync(t => t.TraceId == traceId);

        afterSecond.FirstEventAt.Should().Be(afterFirst.FirstEventAt);
        afterSecond.LastEventAt.Should().BeAfter(afterFirst.LastEventAt);
    }

    [Fact]
    public async Task Chain_Root_Is_Set_Only_On_First_Event_And_Not_Revisited()
    {
        var db      = TimelineBuild.NewDb();
        var svc     = TimelineBuild.NewService(db);
        var traceId = Guid.NewGuid();

        await svc.AppendEventAsync(traceId, TimelineBuild.Event(), chainRootType: "PR", chainRootRef: "PR-2026-00001");
        // A later append passes different root info — must be ignored since the row already exists.
        await svc.AppendEventAsync(traceId, TimelineBuild.Event("PO_CREATED", "PO"), chainRootType: "PO", chainRootRef: "PO-2026-00099");

        var timeline = await db.DocumentTimelines.AsNoTracking().FirstAsync(t => t.TraceId == traceId);
        timeline.ChainRootType.Should().Be("PR");
        timeline.ChainRootRef.Should().Be("PR-2026-00001");
    }

    [Fact]
    public async Task Concurrent_Appends_To_New_TraceId_Both_Succeed()
    {
        var dbName  = Guid.NewGuid().ToString();
        var traceId = Guid.NewGuid();

        var svc1 = TimelineBuild.NewService(TimelineBuild.NewDb(dbName));
        var svc2 = TimelineBuild.NewService(TimelineBuild.NewDb(dbName));

        await Task.WhenAll(
            svc1.AppendEventAsync(traceId, TimelineBuild.Event("EVENT_A")),
            svc2.AppendEventAsync(traceId, TimelineBuild.Event("EVENT_B")));

        var verifySvc = TimelineBuild.NewService(TimelineBuild.NewDb(dbName));
        var events    = await verifySvc.GetTimelineAsync(traceId);

        events.Should().HaveCount(2);
        events.Select(e => e.EventType).Should().BeEquivalentTo(["EVENT_A", "EVENT_B"]);
    }

    [Fact]
    public async Task Concurrent_Appends_To_Existing_TraceId_Both_Succeed_Array_Contains_Both()
    {
        var dbName  = Guid.NewGuid().ToString();
        var traceId = Guid.NewGuid();

        await TimelineBuild.NewService(TimelineBuild.NewDb(dbName))
            .AppendEventAsync(traceId, TimelineBuild.Event("PR_CREATED"));

        var svc1 = TimelineBuild.NewService(TimelineBuild.NewDb(dbName));
        var svc2 = TimelineBuild.NewService(TimelineBuild.NewDb(dbName));

        await Task.WhenAll(
            svc1.AppendEventAsync(traceId, TimelineBuild.Event("EVENT_A")),
            svc2.AppendEventAsync(traceId, TimelineBuild.Event("EVENT_B")));

        var verifySvc = TimelineBuild.NewService(TimelineBuild.NewDb(dbName));
        var events    = await verifySvc.GetTimelineAsync(traceId);

        events.Should().HaveCount(3);
        events.Select(e => e.EventType).Should().Contain(["PR_CREATED", "EVENT_A", "EVENT_B"]);
    }
}

// ── GetTimelineAsync ─────────────────────────────────────────────────────────

public class GetTimelineAsync_Tests
{
    [Fact]
    public async Task Returns_Events_In_Ascending_Order()
    {
        var svc     = TimelineBuild.NewService(TimelineBuild.NewDb());
        var traceId = Guid.NewGuid();
        var t0      = DateTime.UtcNow;

        await svc.AppendEventAsync(traceId, TimelineBuild.Event("THIRD",  occurredAt: t0.AddMinutes(3)));
        await svc.AppendEventAsync(traceId, TimelineBuild.Event("FIRST",  occurredAt: t0));
        await svc.AppendEventAsync(traceId, TimelineBuild.Event("SECOND", occurredAt: t0.AddMinutes(1)));

        var events = await svc.GetTimelineAsync(traceId);

        events.Select(e => e.EventType).Should().Equal("FIRST", "SECOND", "THIRD");
    }

    [Fact]
    public async Task Returns_Empty_For_Unknown_TraceId()
    {
        var svc = TimelineBuild.NewService(TimelineBuild.NewDb());

        var events = await svc.GetTimelineAsync(Guid.NewGuid());

        events.Should().BeEmpty();
    }
}

// ── GetTimelineDetailAsync ───────────────────────────────────────────────────

public class GetTimelineDetailAsync_Tests
{
    [Fact]
    public async Task Returns_Full_Detail_With_Chain_Root_And_Ordered_Events()
    {
        var svc     = TimelineBuild.NewService(TimelineBuild.NewDb());
        var traceId = Guid.NewGuid();
        var t0      = DateTime.UtcNow;

        await svc.AppendEventAsync(traceId, TimelineBuild.Event("PR_CREATED", occurredAt: t0),
            chainRootType: "PR", chainRootRef: "PR-2026-00001");
        await svc.AppendEventAsync(traceId, TimelineBuild.Event("PR_APPROVED", occurredAt: t0.AddMinutes(1)));

        var detail = await svc.GetTimelineDetailAsync(traceId);

        detail.Should().NotBeNull();
        detail!.TraceId.Should().Be(traceId);
        detail.ChainRootType.Should().Be("PR");
        detail.ChainRootRef.Should().Be("PR-2026-00001");
        detail.Events.Select(e => e.EventType).Should().Equal("PR_CREATED", "PR_APPROVED");
        detail.TotalEventCount.Should().Be(2);
        detail.FirstEventAt.Should().BeBefore(detail.LastEventAt);
    }

    [Fact]
    public async Task Returns_Full_Chain_Not_Just_One_Document_Types_Events()
    {
        var svc     = TimelineBuild.NewService(TimelineBuild.NewDb());
        var traceId = Guid.NewGuid();

        await svc.AppendEventAsync(traceId, TimelineBuild.Event("PR_CREATED", "PR"), chainRootType: "PR", chainRootRef: "PR-2026-00001");
        await svc.AppendEventAsync(traceId, TimelineBuild.Event("PO_CREATED", "PO"));
        await svc.AppendEventAsync(traceId, TimelineBuild.Event("GRN_CREATED", "GRN"));

        var detail = await svc.GetTimelineDetailAsync(traceId);

        detail!.Events.Select(e => e.InterfaceCode).Should().Contain(["PR", "PO", "GRN"]);
        detail.TotalEventCount.Should().Be(detail.Events.Count);
    }

    [Fact]
    public async Task Returns_Null_For_Unknown_TraceId()
    {
        var svc = TimelineBuild.NewService(TimelineBuild.NewDb());

        var detail = await svc.GetTimelineDetailAsync(Guid.NewGuid());

        detail.Should().BeNull();
    }
}

// ── ResolveTraceIdAsync ──────────────────────────────────────────────────────

public class ResolveTraceIdAsync_Tests
{
    [Fact]
    public async Task Dispatches_To_Resolver_By_InterfaceCode()
    {
        var expectedTraceId = Guid.NewGuid();
        var documentId      = Guid.NewGuid();

        var poResolver = new Mock<ITraceIdResolver>();
        poResolver.SetupGet(r => r.InterfaceCode).Returns("PO");
        poResolver.Setup(r => r.ResolveTraceIdAsync(documentId)).ReturnsAsync(expectedTraceId);

        var svc = TimelineBuild.NewService(TimelineBuild.NewDb(), [poResolver.Object]);

        var result = await svc.ResolveTraceIdAsync("PO", documentId);

        result.Should().Be(expectedTraceId);
    }

    [Fact]
    public async Task Resolve_For_NonExistent_Document_Returns_Null()
    {
        var poResolver = new Mock<ITraceIdResolver>();
        poResolver.SetupGet(r => r.InterfaceCode).Returns("PO");
        poResolver.Setup(r => r.ResolveTraceIdAsync(It.IsAny<Guid>())).ReturnsAsync((Guid?)null);

        var svc = TimelineBuild.NewService(TimelineBuild.NewDb(), [poResolver.Object]);

        var result = await svc.ResolveTraceIdAsync("PO", Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_For_Unregistered_InterfaceCode_Returns_Null()
    {
        var svc = TimelineBuild.NewService(TimelineBuild.NewDb());

        var result = await svc.ResolveTraceIdAsync("UNKNOWN", Guid.NewGuid());

        result.Should().BeNull();
    }
}

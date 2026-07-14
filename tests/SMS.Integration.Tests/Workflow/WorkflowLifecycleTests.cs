using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Integration.Tests.Workflow.Infrastructure;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Models;
using Xunit;

namespace SMS.Integration.Tests.Workflow;

[Collection("WorkflowIntegration")]
public class WorkflowLifecycleTests
{
    private static readonly JsonSerializerOptions Json =
        new() { PropertyNameCaseInsensitive = true };

    private readonly WorkflowWebApplicationFactory _factory;

    public WorkflowLifecycleTests(WorkflowWebApplicationFactory factory)
        => _factory = factory;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<Guid> SubmitAsync(
        int submitterId, string interfaceCode, decimal? conditionValue = null)
    {
        var client  = _factory.CreateClientFor(submitterId);
        var payload = new SubmitDocumentCommand
        {
            InterfaceCode  = interfaceCode,
            DocumentId     = Guid.NewGuid(),
            DocumentNumber = $"{interfaceCode}-TEST-{Guid.NewGuid():N}",
            ConditionValue = conditionValue
        };

        var resp = await client.PostAsJsonAsync("/api/workflow/submit", payload);
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"submit failed: {await resp.Content.ReadAsStringAsync()}");

        return await ReadResultAsync<Guid>(resp);
    }

    private async Task ApproveAsync(int approverId, Guid approvalUuid, string? remarks = null)
    {
        var client = _factory.CreateClientFor(approverId);
        var resp   = await client.PostAsJsonAsync("/api/workflow/approve",
            new { approvalUUID = approvalUuid, remarks });
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"approve failed: {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task RejectAsync(int rejectorId, Guid approvalUuid, string reason = "Integration test rejection")
    {
        var client = _factory.CreateClientFor(rejectorId);
        var resp   = await client.PostAsJsonAsync(
            $"/api/workflow/approvals/{approvalUuid}/reject",
            new { rejectionReason = reason });
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"reject failed: {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task RecallAsync(int recallerId, Guid approvalUuid)
    {
        var client = _factory.CreateClientFor(recallerId);
        var resp   = await client.PostAsJsonAsync(
            $"/api/workflow/approvals/{approvalUuid}/recall",
            new { recallReason = "Integration test recall" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"recall failed: {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task CancelAsync(int cancellerId, Guid approvalUuid)
    {
        var client = _factory.CreateClientFor(cancellerId);
        var resp   = await client.PostAsJsonAsync(
            $"/api/workflow/approvals/{approvalUuid}/cancel",
            new { cancellationReason = "Integration test cancel" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"cancel failed: {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task<Guid> ReissueAsync(int reissuerId, Guid approvalUuid, decimal? conditionValue = null)
    {
        var client = _factory.CreateClientFor(reissuerId);
        var resp   = await client.PostAsJsonAsync(
            $"/api/workflow/approvals/{approvalUuid}/reissue",
            new { conditionValue });
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"reissue failed: {await resp.Content.ReadAsStringAsync()}");
        return await ReadResultAsync<Guid>(resp);
    }

    private static async Task<T> ReadResultAsync<T>(HttpResponseMessage resp)
    {
        var json    = await resp.Content.ReadAsStringAsync();
        var wrapped = JsonSerializer.Deserialize<ApiResponse<T>>(json, Json)!;
        wrapped.Success.Should().BeTrue($"response not successful: {json}");
        return wrapped.Result!;
    }

    private Task<string> ApprovalStatusAsync(Guid approvalUuid)
        => _factory.DbQueryAsync(async db =>
        {
            var a = await db.DocumentApprovals.FirstAsync(x => x.UUID == approvalUuid);
            return a.Status;
        })!;

    private Task<int> AuditLogCountAsync(Guid approvalUuid)
        => _factory.DbQueryAsync(async db =>
        {
            var approvalId = (await db.DocumentApprovals.FirstAsync(x => x.UUID == approvalUuid)).Id;
            return await db.WorkflowAuditLogs.CountAsync(l => l.ApprovalId == approvalId);
        });

    // ─────────────────────────────────────────────────────────────────────────
    // Test 1: PO Tier-1 — submit → single approval → APPROVED
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PO_Tier1_single_approval_completes_workflow()
    {
        var uuid = await SubmitAsync(User3, "PO", conditionValue: 5_000m);

        await ApproveAsync(User1, uuid);

        (await ApprovalStatusAsync(uuid)).Should().Be("APPROVED");
        (await AuditLogCountAsync(uuid)).Should().Be(2); // SUBMITTED + APPROVED
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 2: PO Tier-3 — submit → L1 → L2 → L3 → APPROVED
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PO_Tier3_three_approvals_completes_workflow()
    {
        var uuid = await SubmitAsync(User3, "PO", conditionValue: 750_000m);

        await ApproveAsync(User1, uuid, "L1 ok");
        await ApproveAsync(User2, uuid, "L2 ok");
        await ApproveAsync(User3, uuid, "L3 ok");

        (await ApprovalStatusAsync(uuid)).Should().Be("APPROVED");
        (await AuditLogCountAsync(uuid)).Should().Be(4); // SUBMITTED + 3×APPROVED
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 3: Reject → Reissue creates new revision
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reject_then_Reissue_creates_revision_2_PENDING()
    {
        var uuid = await SubmitAsync(User3, "PO", conditionValue: 5_000m);

        await RejectAsync(User1, uuid, "Not approved");

        (await ApprovalStatusAsync(uuid)).Should().Be("REJECTED");

        var newUuid = await ReissueAsync(User3, uuid, conditionValue: 5_000m);

        // Original becomes CLOSED
        (await ApprovalStatusAsync(uuid)).Should().Be("CLOSED");

        // New revision is PENDING with RevisionNo = 2
        var (newStatus, newRevision) = await _factory.DbQueryAsync(async db =>
        {
            var a = await db.DocumentApprovals.FirstAsync(x => x.UUID == newUuid);
            return (a.Status, a.RevisionNo);
        });
        newStatus.Should().Be("PENDING");
        newRevision.Should().Be(2);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 4: Recall from step 2 → reverts to step 1 → full re-approval → APPROVED
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Recall_at_step2_reverts_then_full_reapproval_completes()
    {
        // 2-step PO Standard (BETWEEN 50001–500000)
        var uuid = await SubmitAsync(User3, "PO", conditionValue: 150_000m);

        // User1 approves step 1 → advances to step 2
        await ApproveAsync(User1, uuid);

        // Submitter (User3) recalls from step 2
        await RecallAsync(User3, uuid);

        // Step 1 is back to PENDING — User1 re-approves
        await ApproveAsync(User1, uuid);

        // Step 2 is now PENDING — User2 approves
        await ApproveAsync(User2, uuid);

        (await ApprovalStatusAsync(uuid)).Should().Be("APPROVED");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 5: ANY_ONE group — first approval skips remaining approvers
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ANY_ONE_group_first_approval_skips_other_two()
    {
        var uuid = await SubmitAsync(User3, "GRPANY");

        // User1 approves — step should complete, User2 and User3 skipped
        await ApproveAsync(User1, uuid);

        (await ApprovalStatusAsync(uuid)).Should().Be("APPROVED");

        var skippedCount = await _factory.DbQueryAsync(async db =>
        {
            var approvalId = (await db.DocumentApprovals.FirstAsync(x => x.UUID == uuid)).Id;
            return await db.DocumentApprovalSteps
                .CountAsync(s => s.ApprovalId == approvalId && s.Status == "SKIPPED");
        });
        skippedCount.Should().Be(2);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 6: ALL group — requires all three approvals
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ALL_group_requires_all_three_approvals()
    {
        var uuid = await SubmitAsync(User3, "GRPALL");

        // After User1 approves — still PENDING (need ALL)
        await ApproveAsync(User1, uuid);
        (await ApprovalStatusAsync(uuid)).Should().Be("PENDING");

        // After User2 — still PENDING
        await ApproveAsync(User2, uuid);
        (await ApprovalStatusAsync(uuid)).Should().Be("PENDING");

        // User3 completes the set
        await ApproveAsync(User3, uuid);
        (await ApprovalStatusAsync(uuid)).Should().Be("APPROVED");

        // No SKIPPED rows for ALL mode
        var skippedCount = await _factory.DbQueryAsync(async db =>
        {
            var approvalId = (await db.DocumentApprovals.FirstAsync(x => x.UUID == uuid)).Id;
            return await db.DocumentApprovalSteps
                .CountAsync(s => s.ApprovalId == approvalId && s.Status == "SKIPPED");
        });
        skippedCount.Should().Be(0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 7: Conditional routing — 3 values → 3 distinct workflow tiers
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Conditional_routing_selects_correct_tier_for_each_value()
    {
        // 30k → unconditional (PO Simple, 1 step)
        var uuid30k = await SubmitAsync(User3, "PO", conditionValue: 30_000m);
        // 150k → BETWEEN 50001–500000 (PO Standard, 2 steps)
        var uuid150k = await SubmitAsync(User3, "PO", conditionValue: 150_000m);
        // 750k → GT 500000 (PO Senior, 3 steps)
        var uuid750k = await SubmitAsync(User3, "PO", conditionValue: 750_000m);

        var (steps30k, steps150k, steps750k) = await _factory.DbQueryAsync(async db =>
        {
            var t30  = (await db.DocumentApprovals.FirstAsync(x => x.UUID == uuid30k)).TotalSteps;
            var t150 = (await db.DocumentApprovals.FirstAsync(x => x.UUID == uuid150k)).TotalSteps;
            var t750 = (await db.DocumentApprovals.FirstAsync(x => x.UUID == uuid750k)).TotalSteps;
            return (t30, t150, t750);
        });

        steps30k.Should().Be(1,  "30k should route to 1-step PO Simple");
        steps150k.Should().Be(2, "150k should route to 2-step PO Standard");
        steps750k.Should().Be(3, "750k should route to 3-step PO Senior");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Test 8: Cancel — all PENDING steps SKIPPED, status = CANCELLED
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_marks_all_pending_steps_skipped_and_cancelled()
    {
        // 2-step PO Standard — both steps start PENDING
        var uuid = await SubmitAsync(User3, "PO", conditionValue: 150_000m);

        await CancelAsync(User3, uuid);

        (await ApprovalStatusAsync(uuid)).Should().Be("CANCELLED");

        var allSkipped = await _factory.DbQueryAsync(async db =>
        {
            var approvalId = (await db.DocumentApprovals.FirstAsync(x => x.UUID == uuid)).Id;
            var statuses   = await db.DocumentApprovalSteps
                .Where(s => s.ApprovalId == approvalId)
                .Select(s => s.Status)
                .ToListAsync();
            return statuses.All(s => s == "SKIPPED");
        });
        allSkipped.Should().BeTrue("all steps should be SKIPPED after cancel");
    }

    // ── Constants pulled from factory ─────────────────────────────────────────
    private const int User1 = WorkflowWebApplicationFactory.User1;
    private const int User2 = WorkflowWebApplicationFactory.User2;
    private const int User3 = WorkflowWebApplicationFactory.User3;
}

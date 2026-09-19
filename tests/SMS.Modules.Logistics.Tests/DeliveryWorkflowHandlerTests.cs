using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-14 — the workflow-engine bridge.
//
// The engine speaks PENDING / APPROVED / REJECTED / CANCELLED and each module maps that onto its
// own statuses, so these tests exercise the mapping rather than delivery statuses directly.
public class DeliveryWorkflowHandlerTests
{
    private const int User = 42;

    private sealed record Harness(
        LogisticsDbContext Db,
        DeliveryRepository Deliveries,
        DeliveryStatusHandler Handler);

    private static Harness NewHarness()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var repo = new DeliveryRepository(
            db,
            new DocumentNumberGenerator(db, tenant),
            new AddressNormalizer(new FakeCityLookup()));

        return new Harness(db, repo, new DeliveryStatusHandler(db));
    }

    private static async Task<Guid> NewDelivery(Harness h, string status = "STAGED")
    {
        var uuid = await h.Deliveries.CreateAsync(new CreateDeliveryRequest
        {
            SourceType = "MANUAL",
            Direction  = "OUTBOUND",
            Lines = [new CreateDeliveryLineRequest
            {
                ItemDescription = "4mm cable", UnitOfMeasure = "M", QtyOrdered = 100m
            }]
        }, User);

        if (status != "DRAFT")
        {
            var delivery = await h.Db.DeliveryOrders.SingleAsync(d => d.UUID == uuid);
            delivery.Status = status;
            await h.Db.SaveChangesAsync();
            h.Db.ChangeTracker.Clear();
        }

        return uuid;
    }

    private static async Task<string> StatusOf(Harness h, Guid uuid) =>
        (await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid)).Status;

    // ── TC-14.1 ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_handler_owns_the_delivery_interface_code() =>
        NewHarness().Handler.InterfaceCode.Should().Be("DELIVERY");

    // ── TC-14.2 / TC-14.3 ────────────────────────────────────────────────────

    [Fact]
    public async Task Reading_the_status_returns_the_deliverys_own_status()
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "PACKED");

        (await h.Handler.GetStatusAsync(uuid)).Should().Be("PACKED");
    }

    [Fact]
    public async Task Reading_an_unknown_document_returns_null_rather_than_throwing()
    {
        // The engine asks about documents it may no longer own; a throw here would surface as a
        // workflow failure rather than a missing document.
        var h = NewHarness();

        var act = async () => await h.Handler.GetStatusAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
        (await h.Handler.GetStatusAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task A_soft_deleted_delivery_reads_as_absent()
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "DRAFT");
        await h.Deliveries.DeleteAsync(uuid);

        (await h.Handler.GetStatusAsync(uuid)).Should().BeNull();
    }

    // ── TC-14.4 — the workflow vocabulary maps onto delivery statuses ────────

    [Fact]
    public async Task Starting_the_workflow_moves_a_staged_delivery_to_pending_approval()
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "STAGED");

        await h.Handler.UpdateStatusAsync(uuid, "PENDING");

        (await StatusOf(h, uuid)).Should().Be("PENDING_APPROVAL");
    }

    [Fact]
    public async Task Approval_clears_the_delivery_to_be_issued_without_issuing_it()
    {
        // Approval grants permission; it does not move stock. Jumping straight to GOODS_ISSUED
        // would leave the status claiming the stock had left while the ledger said otherwise.
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "PENDING_APPROVAL");

        await h.Handler.UpdateStatusAsync(uuid, "APPROVED");

        var delivery = await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid);
        delivery.Status.Should().Be("STAGED");
        delivery.ApprovedAt.Should().NotBeNull("the approval stamp is what goods issue will check");
    }

    [Fact]
    public async Task Rejection_returns_the_delivery_to_staged_with_no_approval_stamp()
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "PENDING_APPROVAL");

        await h.Handler.UpdateStatusAsync(uuid, "REJECTED");

        var delivery = await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid);
        delivery.Status.Should().Be("STAGED");
        delivery.ApprovedAt.Should().BeNull(
            "approval and rejection share a destination and are told apart by the stamp");
    }

    [Fact]
    public async Task A_rejection_after_an_approval_withdraws_the_stamp()
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "PENDING_APPROVAL");

        await h.Handler.UpdateStatusAsync(uuid, "APPROVED");
        await h.Handler.UpdateStatusAsync(uuid, "PENDING");
        await h.Handler.UpdateStatusAsync(uuid, "REJECTED");

        (await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid))
            .ApprovedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("CLOSED")]
    public async Task A_workflow_cancellation_cancels_the_delivery(string workflowStatus)
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "PENDING_APPROVAL");

        await h.Handler.UpdateStatusAsync(uuid, workflowStatus);

        var delivery = await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid);
        delivery.Status.Should().Be("CANCELLED");
        delivery.ClosureReason.Should().Contain("approval workflow");
        delivery.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_workflow_status_changes_nothing()
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "PACKED");

        await h.Handler.UpdateStatusAsync(uuid, "SOMETHING_ELSE");

        (await StatusOf(h, uuid)).Should().Be("PACKED");
    }

    [Fact]
    public async Task Updating_an_unknown_document_is_a_no_op()
    {
        var h = NewHarness();

        var act = async () => await h.Handler.UpdateStatusAsync(Guid.NewGuid(), "APPROVED");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Re_signalling_a_status_the_delivery_already_holds_is_harmless()
    {
        // A workflow can legitimately re-apply a status it has already applied; that must not
        // fail as an illegal self-transition.
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "STAGED");

        var act = async () => await h.Handler.UpdateStatusAsync(uuid, "REJECTED"); // STAGED → STAGED

        await act.Should().NotThrowAsync();
        (await StatusOf(h, uuid)).Should().Be("STAGED");
    }

    // ── TC-14.5 — the handler cannot bypass the state machine ────────────────

    [Theory]
    [InlineData("GOODS_ISSUED")]
    [InlineData("IN_TRANSIT")]
    [InlineData("DELIVERED")]
    public async Task The_workflow_cannot_cancel_a_delivery_whose_stock_has_left(string status)
    {
        // A workflow cancellation is not a reason to make an exception. The stock is gone, and
        // cancelling would leave the ledger asserting a movement the document denies.
        var h    = NewHarness();
        var uuid = await NewDelivery(h, status);

        var act = async () => await h.Handler.UpdateStatusAsync(uuid, "CANCELLED");

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage($"*{status}*");
        (await StatusOf(h, uuid)).Should().Be(status, "the refused transition left nothing behind");
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("PICKING")]
    public async Task The_workflow_cannot_start_an_approval_from_an_illegal_status(string status)
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, status);

        var act = async () => await h.Handler.UpdateStatusAsync(uuid, "PENDING");

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*PENDING_APPROVAL*");
    }

    [Fact]
    public async Task The_workflow_cannot_approve_a_delivery_that_was_never_submitted()
    {
        var h    = NewHarness();
        var uuid = await NewDelivery(h, "DRAFT");

        var act = async () => await h.Handler.UpdateStatusAsync(uuid, "APPROVED");

        await act.Should().ThrowAsync<ConflictException>();
        (await h.Db.DeliveryOrders.AsNoTracking().SingleAsync(d => d.UUID == uuid))
            .ApprovedAt.Should().BeNull("a refused approval must not leave a stamp behind");
    }

    // ── TC-14.6 — dispatch through the composite ────────────────────────────

    [Fact]
    public async Task The_composite_dispatcher_routes_delivery_to_this_handler()
    {
        // Mirrors SMS.WorkflowEngine's DocumentStatusService: a dictionary keyed on InterfaceCode,
        // case-insensitive, with unknown codes silently ignored.
        var h = NewHarness();
        var uuid = await NewDelivery(h, "STAGED");

        var handlers = new IDocumentStatusHandler[] { h.Handler }
            .ToDictionary(x => x.InterfaceCode.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);

        handlers.Should().ContainKey("DELIVERY");
        handlers.Should().ContainKey("delivery", "the dispatcher matches case-insensitively");

        (await handlers["DELIVERY"].GetStatusAsync(uuid)).Should().Be("STAGED");
    }

    [Fact]
    public void The_interface_code_does_not_collide_with_another_module()
    {
        // DocumentStatusService builds its map with ToDictionary, which throws on a duplicate
        // key — two handlers claiming one code would break every workflow at startup, not just
        // deliveries. "DELIVERY" is distinct from GRN, GRN_QC, PR, PO, MIR_PROJECT, MIR_GENERAL.
        string[] codesInUse = ["GRN", "GRN_QC", "PR", "PO", "MIR_PROJECT", "MIR_GENERAL", "SRO"];

        codesInUse.Should().NotContain(NewHarness().Handler.InterfaceCode);
    }
}

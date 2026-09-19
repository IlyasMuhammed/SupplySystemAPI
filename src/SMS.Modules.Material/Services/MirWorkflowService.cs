using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.Modules.Material.Services;

internal sealed class MirWorkflowService : IMirWorkflowService
{
    private readonly MaterialDbContext        _db;
    private readonly InventoryDbContext       _inv;
    private readonly IWorkflowActionService   _engine;
    private readonly IWorkflowInboxService    _inbox;
    private readonly IStockAvailabilityService _stock;
    private readonly IStockReservationService _reservations;
    private readonly IBackgroundJobClient     _jobs;

    public MirWorkflowService(
        MaterialDbContext        db,
        InventoryDbContext       inv,
        IWorkflowActionService   engine,
        IWorkflowInboxService    inbox,
        IStockAvailabilityService stock,
        IStockReservationService reservations,
        IBackgroundJobClient     jobs)
    {
        _db     = db;
        _inv    = inv;
        _engine = engine;
        _inbox  = inbox;
        _stock  = stock;
        _reservations = reservations;
        _jobs   = jobs;
    }

    // ── Submit ────────────────────────────────────────────────────────────────

    public async Task<Guid> SubmitAsync(Guid mirUuid, int userId)
    {
        var mir = await _db.MaterialIssueRequests
            .Include(m => m.Lines)
            .FirstOrDefaultAsync(m => m.UUID == mirUuid && !m.IsDelete)
            ?? throw new NotFoundException("MIR", mirUuid);

        if (mir.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT MIRs can be submitted. Current status: {mir.Status}.");

        if (mir.Lines.Count == 0)
            throw new UnprocessableEntityException("Cannot submit an MIR with no lines.");

        string interfaceCode;
        IReadOnlyDictionary<string, int>? namedApprovers = null;

        if (mir.RequestType == "PROJECT")
        {
            if (mir.ProjectId is null)
                throw new UnprocessableEntityException("PROJECT-type MIR has no project linked.");

            var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == mir.ProjectId)
                ?? throw new NotFoundException("Project", mir.UUID);

            interfaceCode  = "MIR_PROJECT";
            namedApprovers = new Dictionary<string, int>
            {
                ["PROJECT_MANAGER"] = project.ProjectManagerId
            };
        }
        else
        {
            interfaceCode = "MIR_GENERAL";
        }

        return await _engine.SubmitAsync(new SubmitDocumentCommand
        {
            InterfaceCode   = interfaceCode,
            DocumentId      = mir.UUID,
            DocumentNumber  = mir.RequestNo,
            ConditionValue  = mir.EstimatedValue,
            SubmittedBy     = userId,
            NamedApprovers  = namedApprovers,
            SkipStatusCheck = false
        });
    }

    // ── Approve ───────────────────────────────────────────────────────────────

    public async Task ApproveAsync(Guid mirUuid, MirWorkflowApproveRequest req, int userId)
    {
        var mir = await _db.MaterialIssueRequests
            .Include(m => m.Lines)
            .Include(m => m.Project)
            .FirstOrDefaultAsync(m => m.UUID == mirUuid && !m.IsDelete)
            ?? throw new NotFoundException("MIR", mirUuid);

        var approval = await _inbox.GetApprovalDetailAsync(req.ApprovalUUID)
            ?? throw new NotFoundException("Workflow approval", req.ApprovalUUID);

        var currentStep = approval.CurrentStepNumber;

        var lineInputs = req.LineApprovals.Count > 0
            ? req.LineApprovals
            : mir.Lines.Select(l => new MirLineApprovalInput
              {
                  LineUuid    = l.UUID,
                  ApprovedQty = l.RequestedQty
              }).ToList();

        // Resolve the site warehouse UUID for availability checks (PROJECT type only).
        var warehouseUuid = mir.RequestType == "PROJECT"
            ? mir.Project?.SiteWarehouseId
            : null;

        // Validate + collect best availability snapshot per line input.
        var bestSnapshots = new Dictionary<Guid, StockAvailabilityDto>();

        foreach (var input in lineInputs)
        {
            var line = mir.Lines.FirstOrDefault(l => l.UUID == input.LineUuid)
                ?? throw new BadRequestException($"Line {input.LineUuid} does not belong to MIR {mirUuid}.");

            if (input.ApprovedQty < 0)
                throw new UnprocessableEntityException(
                    $"Line '{line.ItemDescription}': approved qty cannot be negative.");

            var ceiling = await GetCeilingAsync(mir.Id, line.Id, currentStep) ?? line.RequestedQty;

            if (input.ApprovedQty > ceiling)
                throw new UnprocessableEntityException(
                    $"Line '{line.ItemDescription}': approved qty {input.ApprovedQty} " +
                    $"exceeds the previous step's ceiling of {ceiling}.");

            if (input.ApprovedQty > 0)
            {
                var snapshots = await _stock.GetAvailabilityAsync(line.VariantUuid, warehouseUuid);
                var totalAvail = snapshots.Sum(s => s.QtyAvailable);

                if (totalAvail < input.ApprovedQty)
                {
                    var best = snapshots.OrderByDescending(s => s.QtyAvailable).FirstOrDefault();
                    throw new UnprocessableEntityException(
                        $"Line '{line.ItemDescription}': approved qty {input.ApprovedQty} exceeds " +
                        $"available stock {totalAvail:F4} " +
                        $"(on-hand: {best?.QtyOnHand ?? 0:F4}, reserved: {best?.QtyReserved ?? 0:F4}). " +
                        "Approval blocked by stock availability check.");
                }

                // Pick the single best warehouse item to reserve from on final approval.
                var bestItem = snapshots.OrderByDescending(s => s.QtyAvailable).First();
                bestSnapshots[input.LineUuid] = bestItem;
            }

            _db.MirLineApprovals.Add(new MirLineApproval
            {
                UUID                 = Guid.NewGuid(),
                MirId                = mir.Id,
                LineId               = line.Id,
                WorkflowApprovalUUID = req.ApprovalUUID,
                StepNumber           = currentStep,
                ApprovedBy           = userId,
                ApprovedAt           = DateTime.UtcNow,
                ApprovedQty          = input.ApprovedQty
            });
        }

        await _db.SaveChangesAsync();

        await _engine.ApproveAsync(new ApproveCommand
        {
            ApprovalUUID = req.ApprovalUUID,
            ApprovedBy   = userId,
            Remarks      = req.Remarks
        });

        // Re-query the MIR status — if this was the final step the status handler has updated it.
        var finalStatus = await _db.MaterialIssueRequests
            .Where(m => m.Id == mir.Id)
            .Select(m => m.Status)
            .FirstOrDefaultAsync();

        if (finalStatus is "APPROVED" or "PARTIALLY_APPROVED")
        {
            await CreateReservationsAsync(mir.Id, mirUuid, userId, lineInputs, bestSnapshots);

            var interfaceCode = mir.RequestType == "PROJECT" ? "MIR_PROJECT" : "MIR_GENERAL";
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                mir.TraceId,
                new TimelineEvent("MIR_APPROVED", interfaceCode, mirUuid, mir.RequestNo, DateTime.UtcNow, userId, req.Remarks),
                interfaceCode, mir.RequestNo));
        }
    }

    // ── Reject ────────────────────────────────────────────────────────────────

    public async Task RejectAsync(Guid mirUuid, MirWorkflowRejectRequest req, int userId)
    {
        if (string.IsNullOrWhiteSpace(req.Reason))
            throw new UnprocessableEntityException("Rejection reason is required.");

        var mir = await _db.MaterialIssueRequests
            .FirstOrDefaultAsync(m => m.UUID == mirUuid && !m.IsDelete)
            ?? throw new NotFoundException("MIR", mirUuid);

        mir.RejectionReason = req.Reason;
        mir.ModifiedDate    = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _engine.RejectAsync(new RejectCommand
        {
            ApprovalUUID    = req.ApprovalUUID,
            RejectedBy      = userId,
            RejectionReason = req.Reason
        });
    }

    // ── Stock Availability (display) ──────────────────────────────────────────

    public async Task<MirStockAvailabilityResponse> GetStockAvailabilityAsync(Guid mirUuid)
    {
        var mir = await _db.MaterialIssueRequests
            .Include(m => m.Lines)
            .Include(m => m.Project)
            .FirstOrDefaultAsync(m => m.UUID == mirUuid && !m.IsDelete)
            ?? throw new NotFoundException("MIR", mirUuid);

        var warehouseUuid = mir.RequestType == "PROJECT"
            ? mir.Project?.SiteWarehouseId
            : null;

        var variantUuids  = mir.Lines.Select(l => l.VariantUuid).Distinct().ToList();
        var allSnapshots  = await _stock.GetAvailabilityForVariantsAsync(variantUuids, warehouseUuid);

        // Latest approved qty per line
        var lineIds     = mir.Lines.Select(l => l.Id).ToList();
        var latestQtys  = await _db.MirLineApprovals
            .Where(a => a.MirId == mir.Id && lineIds.Contains(a.LineId))
            .GroupBy(a => a.LineId)
            .Select(g => new { LineId = g.Key, ApprovedQty = g.OrderByDescending(a => a.StepNumber).First().ApprovedQty })
            .ToDictionaryAsync(x => x.LineId, x => x.ApprovedQty);

        // Snapshot lookup per variant (aggregate when no warehouse filter)
        var snapshotByVariant = allSnapshots
            .GroupBy(s => s.VariantUuid)
            .ToDictionary(g => g.Key, g => g.ToList());

        string? commonWarehouseName = null;
        if (warehouseUuid.HasValue && allSnapshots.Count > 0)
            commonWarehouseName = allSnapshots.First().WarehouseName;

        var lines = mir.Lines.OrderBy(l => l.LineNo).Select(l =>
        {
            var snaps     = snapshotByVariant.TryGetValue(l.VariantUuid, out var s) ? s : [];
            var onHand    = snaps.Sum(x => x.QtyOnHand);
            var reserved  = snaps.Sum(x => x.QtyReserved);
            var available = snaps.Sum(x => x.QtyAvailable);
            var rp        = snaps.FirstOrDefault()?.ReorderPoint;
            var whName    = snaps.Count == 1 ? snaps[0].WarehouseName : null;
            var latestQty = latestQtys.TryGetValue(l.Id, out var aq) ? aq : (decimal?)null;

            return new MirLineAvailabilityModel
            {
                LineUuid          = l.UUID,
                VariantUuid       = l.VariantUuid,
                ItemDescription   = l.ItemDescription,
                RequestedQty      = l.RequestedQty,
                LatestApprovedQty = latestQty,
                QtyOnHand         = onHand,
                QtyReserved       = reserved,
                QtyAvailable      = available,
                ReorderPoint      = rp,
                WarehouseName     = whName ?? commonWarehouseName,
                IsAvailable       = available >= l.RequestedQty
            };
        }).ToList();

        return new MirStockAvailabilityResponse
        {
            WarehouseUuid = warehouseUuid,
            WarehouseName = commonWarehouseName,
            Lines         = lines
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<decimal?> GetCeilingAsync(int mirId, int lineId, int currentStepNumber)
    {
        return await _db.MirLineApprovals
            .Where(a => a.MirId == mirId && a.LineId == lineId && a.StepNumber < currentStepNumber)
            .OrderByDescending(a => a.StepNumber)
            .Select(a => (decimal?)a.ApprovedQty)
            .FirstOrDefaultAsync();
    }

    private async Task CreateReservationsAsync(
        int mirId,
        Guid mirUuid,
        int userId,
        List<MirLineApprovalInput> lineInputs,
        Dictionary<Guid, StockAvailabilityDto> bestSnapshots)
    {
        // Map LineUuid -> LineId
        var lineMap = await _db.MaterialIssueRequestDetails
            .Where(d => d.MaterialIssueRequestId == mirId)
            .Select(d => new { d.UUID, d.Id })
            .ToDictionaryAsync(x => x.UUID, x => x.Id);

        var now = DateTime.UtcNow;

        // Reservations go through the shared ledger, which owns both the reservation rows and
        // InventoryItem.QtyReserved and writes them in one transaction. The previous code here
        // saved the two through separate contexts, so a failure between them left a hold
        // recorded with no counter behind it.
        var requests = new List<ReservationRequest>();

        foreach (var input in lineInputs)
        {
            if (input.ApprovedQty <= 0) continue;
            if (!bestSnapshots.TryGetValue(input.LineUuid, out var snap)) continue;
            if (!lineMap.ContainsKey(input.LineUuid)) continue;

            requests.Add(new ReservationRequest(
                snap.VariantUuid, snap.WarehouseUuid, input.ApprovedQty, input.LineUuid));
        }

        if (requests.Count == 0) return;

        var result = await _reservations.ReserveAsync(
            ReservationSourceType.Mir, mirUuid, requests, userId);

        if (!result.Succeeded)
        {
            var detail = string.Join(" ", result.Shortfalls.Select(s =>
                $"{s.Requested:0.###} requested, {s.Available:0.###} available."));

            throw new UnprocessableEntityException(
                $"Stock is no longer available to reserve for this request. {detail}");
        }
    }
}

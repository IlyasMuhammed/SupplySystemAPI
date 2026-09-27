using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Controllers;

/// <summary>A30 §28.3 — production orders, their materials, readiness and floor issues.</summary>
[ApiController]
[Route("api/production-orders")]
[RequiresFeature("MODULE_MANUFACTURING")]
public class ProductionOrdersController : ControllerBase
{
    private readonly IProductionOrderService        _orders;
    private readonly IProductionMaterialIssueService _issues;

    public ProductionOrdersController(IProductionOrderService orders, IProductionMaterialIssueService issues)
    {
        _orders = orders;
        _issues = issues;
    }

    [HttpPost]
    [RequirePermission(PermissionCodes.PROD_CREATE)]
    public async Task<IActionResult> Create([FromBody] CreateProductionOrderRequest req)
    {
        if (req.Plan && !User.HasPermission(PermissionCodes.PROD_PLAN))
            throw new ForbiddenException("Planning a production order on creation needs the plan permission.");
        var uuid = await _orders.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, "Production order created."));
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.PROD_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] ProductionOrderListFilter filter) =>
        Ok(ApiResponse<PaginatedResponse<ProductionOrderListItemModel>>.Ok(await _orders.GetListAsync(filter)));

    /// <summary>§29.3 shortage dashboard — kept ahead of the {uuid} route so "shortages" is never read as one.</summary>
    [HttpGet("shortages")]
    [RequirePermission(PermissionCodes.PROD_VIEW)]
    public async Task<IActionResult> GetShortages([FromQuery] Guid? warehouseUuid) =>
        Ok(ApiResponse<IReadOnlyList<MaterialShortageModel>>.Ok(await _orders.GetShortagesAsync(warehouseUuid)));

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.PROD_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var order = await _orders.GetByUuidAsync(uuid) ?? throw new NotFoundException("Production order", uuid);
        return Ok(ApiResponse<ProductionOrderDetailModel>.Ok(order));
    }

    [HttpPut("{uuid:guid}")]
    [RequirePermission(PermissionCodes.PROD_CREATE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateProductionOrderRequest req)
    {
        await _orders.UpdateAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok("Production order updated."));
    }

    [HttpPost("{uuid:guid}/plan")]
    [RequirePermission(PermissionCodes.PROD_PLAN)]
    public async Task<IActionResult> Plan(Guid uuid) =>
        Ok(ApiResponse<ProductionReadinessModel>.Ok(await _orders.PlanAsync(uuid, User.GetUserId()), "Production order planned."));

    [HttpPost("{uuid:guid}/start")]
    [RequirePermission(PermissionCodes.PROD_START)]
    public async Task<IActionResult> Start(Guid uuid)
    {
        await _orders.StartAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok("Production started."));
    }

    [HttpPost("{uuid:guid}/report-output")]
    [RequirePermission(PermissionCodes.PROD_REPORT)]
    public async Task<IActionResult> ReportOutput(Guid uuid, [FromBody] ReportOutputRequest req)
    {
        await _orders.ReportOutputAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok("Output reported."));
    }

    [HttpPost("{uuid:guid}/complete")]
    [RequirePermission(PermissionCodes.PROD_REPORT)]
    public async Task<IActionResult> Complete(Guid uuid)
    {
        await _orders.CompleteAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok("Production order sent to quality inspection."));
    }

    [HttpPost("{uuid:guid}/cancel")]
    [RequirePermission(PermissionCodes.PROD_CANCEL)]
    public async Task<IActionResult> Cancel(Guid uuid, [FromBody] CancelProductionOrderRequest req)
    {
        await _orders.CancelAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok("Production order cancelled."));
    }

    [HttpGet("{uuid:guid}/materials")]
    [RequirePermission(PermissionCodes.PROD_VIEW)]
    public async Task<IActionResult> GetMaterials(Guid uuid) =>
        Ok(ApiResponse<IReadOnlyList<ProductionMaterialModel>>.Ok(await _orders.GetMaterialsAsync(uuid)));

    /// <summary>A31 C10 — GRN receipt no longer runs allocation by itself; this is the "check availability
    /// now" trigger for the whole order, run over every distinct material variant/warehouse it uses.</summary>
    [HttpPost("{uuid:guid}/run-allocation")]
    [RequirePermission(PermissionCodes.ALLOCATION_RUN)]
    public async Task<IActionResult> RunAllocation(Guid uuid) =>
        Ok(ApiResponse<IReadOnlyList<AllocationRunResult>>.Ok(await _orders.RunAllocationAsync(uuid, User.GetUserId()), "Allocation run."));

    [HttpGet("{uuid:guid}/readiness")]
    [RequirePermission(PermissionCodes.PROD_VIEW)]
    public async Task<IActionResult> GetReadiness(Guid uuid)
    {
        var readiness = await _orders.GetReadinessAsync(uuid) ?? throw new NotFoundException("Production order", uuid);
        return Ok(ApiResponse<ProductionReadinessModel>.Ok(readiness));
    }

    [HttpGet("{uuid:guid}/supply-requirements")]
    [RequirePermission(PermissionCodes.PROD_VIEW)]
    public async Task<IActionResult> GetSupplyRequirements(Guid uuid) =>
        Ok(ApiResponse<IReadOnlyList<SupplyRequirementModel>>.Ok(await _orders.GetSupplyRequirementsForOrderAsync(uuid)));

    [HttpGet("{uuid:guid}/issues")]
    [RequirePermission(PermissionCodes.PROD_VIEW)]
    public async Task<IActionResult> GetIssues(Guid uuid) =>
        Ok(ApiResponse<IReadOnlyList<ProductionIssueModel>>.Ok(await _orders.GetIssuesForOrderAsync(uuid)));

    /// <summary>ADDITIONAL and SUBSTITUTION need the production manager permission on top of MI_CREATE — they take stock beyond what was held.</summary>
    [HttpPost("{uuid:guid}/issues")]
    [RequirePermission(PermissionCodes.MI_CREATE)]
    public async Task<IActionResult> CreateIssue(Guid uuid, [FromBody] CreateProductionIssueRequest req)
    {
        var type = (req.IssueType ?? string.Empty).Trim().ToUpperInvariant();
        if (type is ProductionIssueType.Additional or ProductionIssueType.Substitution && !User.HasPermission(PermissionCodes.PROD_MANAGER))
            throw new ForbiddenException("Issuing beyond the requirement, or a substitution, needs the production manager permission.");
        if (req.Confirm && !User.HasPermission(PermissionCodes.MI_CONFIRM))
            throw new ForbiddenException("Confirming an issue on creation needs the confirm permission.");

        var issueUuid = await _issues.CreateAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(issueUuid, "Material issue created."));
    }
}

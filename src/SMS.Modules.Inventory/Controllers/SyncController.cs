using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Controllers;

/// <summary>A37 §6 (D-16) — the catalog delta for offline clients. Customers are Suppliers' <c>/api/sync/customers</c>.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Inventory)]
public class SyncController : ControllerBase
{
    private readonly ICatalogSyncService _sync;

    public SyncController(ICatalogSyncService sync) => _sync = sync;

    /// <param name="since">ISO-8601 UTC; only rows modified strictly after it. Omitted = everything.</param>
    /// <param name="limit">Per collection; default 500, at most 1000.</param>
    [HttpGet("api/sync/catalog")]
    [RequirePermission(PermissionCodes.INVENTORY_VIEW)]
    public async Task<IActionResult> GetCatalog([FromQuery] DateTime? since, [FromQuery] int? limit, CancellationToken ct)
    {
        var result = await _sync.GetChangesAsync(since, limit, ct);
        return Ok(ApiResponse<CatalogSyncResponse>.Ok(result));
    }
}

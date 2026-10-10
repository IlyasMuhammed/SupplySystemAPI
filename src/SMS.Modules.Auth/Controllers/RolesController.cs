using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Auth.Authorization;
using SMS.Modules.Auth.Models;
using SMS.Modules.Auth.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Auth.Controllers;

[Route("api/roles")]
[ApiController]
public class RolesController : ControllerBase
{
    private readonly IAuthService _authService;

    public RolesController(IAuthService authService) => _authService = authService;

    // Every action here is gated on USER_MANAGE alone (System Admin already holds it as part of
    // its full permission set, so this doesn't remove their access to anything) — the finer-grained
    // authorization lives in AuthRepository's role-CRUD methods: an Org Admin can freely
    // create/edit/deactivate/reassign-permissions on roles owned by their own org, but is rejected
    // (ForbiddenException, see GuardNotGlobalUnlessSuperAdmin) from touching the shared global
    // catalog every other organization also relies on. Reads are scoped the same way by the query
    // filter on Role: an Org Admin only ever sees global roles plus their own org's custom ones.

    // ── GET /api/roles ────────────────────────────────────────────────────────
    // ?assignable=true — only the roles the caller may give a user (the create/edit user picker). The Roles screen
    // keeps the full list: an Org Admin still sees System Admin and Organization Admin there, they just can't hand them out.
    [HttpGet]
    [RequirePermission(PermissionCodes.USER_MANAGE)]
    public async Task<IActionResult> GetRoles([FromQuery] bool assignable = false)
    {
        var result = assignable
            ? await _authService.GetAssignableRolesAsync(AuthCaller.From(User))
            : await _authService.GetRolesAsync();
        return Ok(ApiResponse<List<RoleListItemModel>>.Ok(result, "Roles retrieved successfully."));
    }

    // ── POST /api/roles ───────────────────────────────────────────────────────
    [HttpPost]
    [RequirePermission(PermissionCodes.USER_MANAGE)]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest req)
    {
        var result = await _authService.CreateRoleAsync(req);
        return CreatedAtAction(nameof(GetRole), new { id = result.RoleId },
            ApiResponse<RoleListItemModel>.Ok(result, "Role created successfully."));
    }

    // ── GET /api/roles/{id} ───────────────────────────────────────────────────
    [HttpGet("{id:int}")]
    [RequirePermission(PermissionCodes.USER_MANAGE)]
    public async Task<IActionResult> GetRole(int id)
    {
        var result = await _authService.GetRoleDetailAsync(id);
        return Ok(ApiResponse<RoleDetailModel>.Ok(result, "Role retrieved successfully."));
    }

    // ── PUT /api/roles/{id} ───────────────────────────────────────────────────
    [HttpPut("{id:int}")]
    [RequirePermission(PermissionCodes.USER_MANAGE)]
    public async Task<IActionResult> UpdateRole(int id, [FromBody] UpdateRoleRequest req)
    {
        var updated = await _authService.UpdateRoleAsync(id, req);
        if (!updated) return NotFound(ApiResponse.Fail($"Role {id} not found."));
        return Ok(ApiResponse.Ok("Role updated successfully."));
    }

    // ── PUT /api/roles/{id}/permissions ──────────────────────────────────────
    [HttpPut("{id:int}/permissions")]
    [RequirePermission(PermissionCodes.USER_MANAGE)]
    public async Task<IActionResult> ReplacePermissions(int id, [FromBody] ReplacePermissionsRequest req)
    {
        var ok = await _authService.ReplaceRolePermissionsAsync(id, req);
        if (!ok) return NotFound(ApiResponse.Fail($"Role {id} not found."));
        return Ok(ApiResponse.Ok("Permissions updated successfully."));
    }

    // ── PATCH /api/roles/{id}/deactivate ─────────────────────────────────────
    [HttpPatch("{id:int}/deactivate")]
    [RequirePermission(PermissionCodes.USER_MANAGE)]
    public async Task<IActionResult> Deactivate(int id)
    {
        var activeCount = await _authService.GetActiveUserCountForRoleAsync(id);
        if (activeCount > 0)
            return Conflict(ApiResponse.Fail(
                $"This role has {activeCount} active user(s) assigned. Reassign or deactivate them first.",
                new RoleDeactivateConflictResult { ActiveUserCount = activeCount }));

        var ok = await _authService.DeactivateRoleAsync(id);
        if (!ok) return NotFound(ApiResponse.Fail($"Role {id} not found."));
        return Ok(ApiResponse.Ok("Role deactivated."));
    }

    // ── GET /api/roles/{id}/users ─────────────────────────────────────────────
    [HttpGet("{id:int}/users")]
    [RequirePermission(PermissionCodes.USER_MANAGE)]
    public async Task<IActionResult> GetRoleUsers(int id)
    {
        var result = await _authService.GetRoleUsersAsync(id);
        return Ok(ApiResponse<List<RoleUserModel>>.Ok(result, "Users retrieved successfully."));
    }
}

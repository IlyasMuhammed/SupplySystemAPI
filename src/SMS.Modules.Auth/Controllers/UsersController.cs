using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Auth.Authorization;
using SMS.Modules.Auth.Models;
using SMS.Modules.Auth.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Auth.Controllers;

[Route("api/users")]
[ApiController]
[RequirePermission(PermissionCodes.USER_MANAGE)]
public class UsersController : ControllerBase
{
    private readonly IAuthService _authService;

    public UsersController(IAuthService authService) => _authService = authService;

    // ── POST /api/users ───────────────────────────────────────────────────────
    [HttpPost]
//    [RequirePermission(PermissionCodes.USER_MANAGE)]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest dto)
    {
        if (string.IsNullOrWhiteSpace(dto.FirstName) || dto.FirstName.Length < 2)
            return BadRequest(ApiResponse.Fail("First name must be at least 2 characters."));

        if (string.IsNullOrWhiteSpace(dto.Email))
            return BadRequest(ApiResponse.Fail(StaticResponseMessage.emailShouldNotBeEmpty));

        if (dto.RoleID <= 0)
            return BadRequest(ApiResponse.Fail("A valid RoleID is required."));

        if (dto.SupplierType != "INTERNAL" && dto.SupplierType != "EXTERNAL")
            return BadRequest(ApiResponse.Fail("Supplier Type must be Internal or External."));

        var result = await _authService.AdminCreateUserAsync(dto, AuthCaller.From(User));

        return CreatedAtAction(nameof(GetUser), new { id = result.UserID },
            ApiResponse<UserDetailModel>.Ok(result, StaticResponseMessage.recordCreatedSuccessfully));
    }

    // ── GET /api/users ────────────────────────────────────────────────────────
    [HttpGet]
  //  [RequirePermission(PermissionCodes.USER_MANAGE)]
    public async Task<IActionResult> GetUsers(
        [FromQuery] int? roleId,
        [FromQuery] string? status,
        [FromQuery] string? department,
        [FromQuery] string? search,
        [FromQuery] string? supplierType,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var filter = new UserListFilter
        {
            RoleId       = roleId,
            Status       = status,
            Department   = department,
            Search       = search,
            SupplierType = supplierType,
            Page         = page < 1 ? 1 : page,
            PageSize     = pageSize is < 1 or > 100 ? 20 : pageSize
        };

        var result = await _authService.GetUsersAsync(filter);
        return Ok(ApiResponse<object>.Ok(result, StaticResponseMessage.theRequestSuccessful));
    }

    // ── GET /api/users/:id ────────────────────────────────────────────────────
    [HttpGet("{id:int}")]
    //[RequirePermission(PermissionCodes.USER_MANAGE)]
    public async Task<IActionResult> GetUser(int id)
    {
        var result = await _authService.GetUserDetailAsync(id);
        return Ok(ApiResponse<UserDetailModel>.Ok(result, StaticResponseMessage.theRequestSuccessful));
    }

    // ── PATCH /api/users/:id ──────────────────────────────────────────────────
    [HttpPatch("{id:int}")]
    //[RequirePermission(PermissionCodes.USER_MANAGE)]
    public async Task<IActionResult> PatchUser(int id, [FromBody] PatchUserRequest dto)
    {
        await _authService.PatchUserAsync(id, dto, AuthCaller.From(User));
        return Ok(ApiResponse.Ok(StaticResponseMessage.accountUpdatedSuccessfully));
    }

    // ── PUT /api/users/:id/role ───────────────────────────────────────────────
    [HttpPut("{id:int}/role")]
    ///[RequirePermission(PermissionCodes.USER_MANAGE)]
    public async Task<IActionResult> AssignRole(int id, [FromBody] AssignRoleRequest dto)
    {
        if (dto.RoleID <= 0)
            return BadRequest(ApiResponse.Fail("A valid RoleID is required."));

        await _authService.AssignRoleAsync(id, dto.RoleID, AuthCaller.From(User));
        return Ok(ApiResponse.Ok("Role updated and active sessions invalidated."));
    }

    // ── POST /api/users/:id/reset-password ───────────────────────────────────
    [HttpPost("{id:int}/reset-password")]
    //[RequirePermission(PermissionCodes.USER_MANAGE)]
    public async Task<IActionResult> AdminResetPassword(int id)
    {
        await _authService.AdminResetPasswordAsync(id, AuthCaller.From(User));
        return Ok(ApiResponse.Ok(StaticResponseMessage.passwordResetTokenSendToYourAccount));
    }

    // ── POST /api/users/:id/set-password ─────────────────────────────────────
    // Platform super admins only (the permission code alone is held by roles any organization can have, hence
    // RequireSuperAdmin as on api/system/organizations). For an account whose owner never got, or can't reach,
    // the invite or reset e-mail. The service refuses your own account and anyone outside reach.
    [HttpPost("{id:int}/set-password")]
    [RequirePermission(PermissionCodes.PLATFORM_SUPER_ADMIN)]
    [RequireSuperAdmin]
    public async Task<IActionResult> SetPassword(int id, [FromBody] SetUserPasswordRequest dto)
    {
        if (!AuthController.IsStrongPassword(dto?.NewPassword))
            return BadRequest(ApiResponse.Fail(StaticResponseMessage.passwordMustBeAtLeast8CharactersLongContainAnUppercaseLetterALowercaseLetterANumberAndASpecialCharacter));

        await _authService.SetPasswordAsSuperAdminAsync(id, dto!.NewPassword, AuthCaller.From(User));
        return Ok(ApiResponse.Ok("Password set. The user's other sessions were signed out."));
    }

    // ── DELETE /api/users/:id ─────────────────────────────────────────────────
    [HttpDelete("{id:int}")]
   // [RequirePermission(PermissionCodes.USER_MANAGE)]
    public async Task<IActionResult> DeleteUser(int id)
    {
        await _authService.SoftDeleteUserAsync(id, AuthCaller.From(User));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully));
    }
}

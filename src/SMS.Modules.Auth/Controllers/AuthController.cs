using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SMS.Modules.Auth.Authorization;
using SMS.Modules.Auth.Infrastructure;
using SMS.Modules.Auth.Models;
using SMS.Modules.Auth.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;
using System.Text.RegularExpressions;

namespace SMS.Modules.Auth.Controllers;

/// <summary>
/// Sign-in, account recovery, the signed-in user's own account, and a legacy copy of user/role administration.
/// <para>
/// <b>Every action is in exactly one category</b> (AuthControllerEndpointClassificationTests holds the list):
/// anonymous (sign-in and recovery, rate limited per IP — see <see cref="AuthRateLimits"/>); self-service (signed
/// in, and the target is always the token's own user — an id in the body is overwritten, never trusted); or
/// gated by a permission. Until 2026-10-02 the administration actions here had their [Authorize] commented out
/// and no permission at all, so any signed-in user of any organization could grant themselves every permission,
/// rewrite the roles every organization shares, or change the super admin's e-mail and then reset its password.
/// </para>
/// </summary>
[Route("api/auth")]
[ApiController]
public class AuthController : ControllerBase
{
    private static readonly HashSet<string> _allowedMimeTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/webp" };
    private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB

    // One answer whether or not the address has an account: a different one for unknown addresses told anyone
    // which e-mails are registered. The frontend shows its own success text and moves on to the code form.
    internal const string ResetCodeSentIfAccountExists =
        "If an active account uses that e-mail address, a password reset code has been sent to it.";
    internal const string ResetCodeSpent =
        "Too many incorrect codes. This code can no longer be used; please request a new code.";

    private readonly IAuthService _authService;
    private readonly IWebHostEnvironment _env;

    public AuthController(IAuthService authService, IWebHostEnvironment env)
    {
        _authService = authService;
        _env = env;
    }

    /// <summary>The same rule register and accept-invite always applied, now also on reset and change.</summary>
    private static bool IsStrongPassword(string? password) =>
        password is { Length: >= 8 }
        && password.Any(char.IsUpper) && password.Any(char.IsLower) && password.Any(char.IsDigit)
        && Regex.IsMatch(password, @"[!@#$%^&*]");

    private static BadRequestObjectResult WeakPassword() =>
        new(ApiResponse.Fail(StaticResponseMessage.passwordMustBeAtLeast8CharactersLongContainAnUppercaseLetterALowercaseLetterANumberAndASpecialCharacter));

    // ── POST /api/auth/login ──────────────────────────────────────────────────
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimits.SignIn)]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequestModel request)
    {
        // AccountLockedException (429) and UnauthorizedException (401) surface
        // through GlobalExceptionMiddleware — no manual catch needed here.
        var result = await _authService.LoginAsync(request);
        return Ok(ApiResponse<LoginResponseModel>.Ok(result, StaticResponseMessage.theRequestSuccessful));
    }

    // ── POST /api/auth/refresh ────────────────────────────────────────────────
    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequestModel request)
    {
        var result = await _authService.RefreshAsync(request.RefreshToken);
        return Ok(ApiResponse<RefreshResponseModel>.Ok(result, StaticResponseMessage.theRequestSuccessful));
    }

    // ── POST /api/auth/logout ─────────────────────────────────────────────────
    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutRequestModel request)
    {
        await _authService.LogoutAsync(request.RefreshToken);
        return Ok(ApiResponse.Ok(StaticResponseMessage.loggedOutSuccessfully));
    }

    // ── GET /api/auth/me ──────────────────────────────────────────────────────
    // Self-service: the token's own user.
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var userId = User.GetUserId();
        if (userId == 0)
            return Unauthorized(ApiResponse.Fail("Invalid token"));

        var result = await _authService.GetCurrentUserAsync(userId);
        return Ok(ApiResponse<CurrentUserModel>.Ok(result, StaticResponseMessage.theRequestSuccessful));
    }

    // ── POST /api/auth/register ───────────────────────────────────────────────
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimits.AccountRecovery)]
    [HttpPost("register")]
    public IActionResult CreateAccount([FromBody] UserAccountModel dto)
    {
        if (!IsStrongPassword(dto.Password))
            return WeakPassword();

        if (string.IsNullOrWhiteSpace(dto.FirstName) || dto.FirstName.Length < 3)
            return BadRequest(ApiResponse.Fail(StaticResponseMessage.firstNameLengthShouldBeGreaterThan3Characters));

        if (string.IsNullOrWhiteSpace(dto.LastName) || dto.LastName.Length < 3)
            return BadRequest(ApiResponse.Fail(StaticResponseMessage.lastNameLengthShouldBeGreaterThan3Characters));

        if (_authService.FindByEmail(dto.Email) != null)
            return BadRequest(ApiResponse.Fail(StaticResponseMessage.accountAlreadyExsistWithThisEmail));

        var resp = _authService.CreateUserAccount(dto);
        return resp == 1
            ? Ok(ApiResponse.Ok(StaticResponseMessage.accountSuccessfullyCreatedAndAccountActivationLinkSentToYourProvidedEmail))
            : BadRequest(ApiResponse.Fail(StaticResponseMessage.accountNotCreatedInternalServerError));
    }

    // ── POST /api/auth/activate ───────────────────────────────────────────────
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimits.AccountRecovery)]
    [HttpPost("activate")]
    public IActionResult ActivateAccount([FromQuery] string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return BadRequest(ApiResponse.Fail(StaticResponseMessage.pleaseEnterValidUserName));

        return _authService.ActivationTokenValidation(token) switch
        {
            1  => Ok(ApiResponse.Ok(StaticResponseMessage.accountActivatedSuccessfully)),
            -1 => BadRequest(ApiResponse.Fail(StaticResponseMessage.accountActivationLinkExpire)),
            _  => BadRequest(ApiResponse.Fail(StaticResponseMessage.invalidTokenAccountNotActivated))
        };
    }

    // ── POST /api/auth/accept-invite ──────────────────────────────────────────
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimits.AccountRecovery)]
    [HttpPost("accept-invite")]
    public async Task<IActionResult> AcceptInvite([FromBody] AcceptInviteRequest request)
    {
        if (!IsStrongPassword(request.NewPassword))
            return WeakPassword();

        await _authService.AcceptInviteAsync(request.Token, request.NewPassword);
        return Ok(ApiResponse.Ok("Invite accepted — you can now log in."));
    }

    // ── POST /api/auth/forgot-password ────────────────────────────────────────
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimits.AccountRecovery)]
    [HttpPost("forgot-password")]
    public IActionResult ForgotPassword([FromQuery] string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(ApiResponse.Fail(StaticResponseMessage.emailShouldNotBeEmpty));

        // Whether a code went out (no such account, or this address has had its codes for the hour) is
        // deliberately not part of the answer.
        _authService.SendPasswordResetToken(email);
        return Ok(ApiResponse.Ok(ResetCodeSentIfAccountExists));
    }

    // ── POST /api/auth/reset-password ─────────────────────────────────────────
    [AllowAnonymous]
    [EnableRateLimiting(AuthRateLimits.AccountRecovery)]
    [HttpPost("reset-password")]
    public IActionResult ResetPassword([FromBody] ForgotPasswordModel dto)
    {
        // Checked first, so a weak password does not spend one of the code's few attempts.
        if (!IsStrongPassword(dto.Password))
            return WeakPassword();

        return _authService.SetPasswordVerification(dto) switch
        {
            1  => Ok(ApiResponse.Ok(StaticResponseMessage.yourAccountPasswordSuccessfullyUpdated)),
            -1 => BadRequest(ApiResponse.Fail(StaticResponseMessage.passwordResetVerificationCodeExpire)),
            -2 => BadRequest(ApiResponse.Fail(ResetCodeSpent)),
            _  => BadRequest(ApiResponse.Fail(StaticResponseMessage.inValidInputs))
        };
    }

    // ── PUT /api/auth/profile ─────────────────────────────────────────────────
    // Self-service. The body's UserID used to choose whose profile changed: change the super admin's e-mail,
    // then "forgot password", and the account was yours. The token decides now; the frontend still sends
    // its own id, which is simply overwritten.
    [HttpPut("profile")]
    public IActionResult UpdateProfile([FromBody] UpdatePersonalInfoModel dto)
    {
        var userId = User.GetUserId();
        if (userId == 0) return Unauthorized(ApiResponse.Fail("Invalid token"));
        dto.UserID = userId;

        if (string.IsNullOrWhiteSpace(dto.Email))
            return BadRequest(ApiResponse.Fail(StaticResponseMessage.emailShouldNotBeEmpty));

        return _authService.UpdatePersonalInformation(dto) switch
        {
            > 0 => Ok(ApiResponse.Ok(StaticResponseMessage.accountUpdatedSuccessfully)),
            -2  => BadRequest(ApiResponse.Fail(StaticResponseMessage.accountAlreadyExsistWithThisEmail)),
            _   => BadRequest(ApiResponse.Fail(StaticResponseMessage.accountNotFound))
        };
    }

    // ── PUT /api/auth/password ────────────────────────────────────────────────
    // Self-service, the same way: the current password is checked against the token's user, not the body's.
    [HttpPut("password")]
    public IActionResult UpdatePassword([FromBody] UpdatePasswordModel dto)
    {
        var userId = User.GetUserId();
        if (userId == 0) return Unauthorized(ApiResponse.Fail("Invalid token"));
        dto.UserID = userId;

        if (!IsStrongPassword(dto.NewPassword))
            return WeakPassword();
        if (dto.NewPassword != dto.ConfirmPassword)
            return BadRequest(ApiResponse.Fail("The new password and its confirmation do not match."));

        return _authService.UpdatePasswordInformation(dto) switch
        {
            1  => Ok(ApiResponse.Ok(StaticResponseMessage.accountUpdatedSuccessfully)),
            -1 => BadRequest(ApiResponse.Fail(StaticResponseMessage.accountNotFound)),
            _  => BadRequest(ApiResponse.Fail("Current password is incorrect"))
        };
    }

    // ── POST /api/auth/profile/picture ───────────────────────────────────────
    // Self-service: the file is named after the token's user.
    [HttpPost("profile/picture")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadProfilePicture([FromForm] ProfilePictureUploadRequest request)
    {
        var userId = User.GetUserId();
        if (userId == 0) return Unauthorized(ApiResponse.Fail("Invalid token"));

        var file = request.File;

        if (file == null || file.Length == 0)
            return BadRequest(ApiResponse.Fail("No file provided."));
        if (file.Length > MaxFileSizeBytes)
            return BadRequest(ApiResponse.Fail("File exceeds maximum size of 5 MB."));
        if (!_allowedMimeTypes.Contains(file.ContentType))
            return BadRequest(ApiResponse.Fail("Only JPEG, PNG, and WebP images are allowed."));
        // The Content-Type is the client's claim; the first bytes are what the file is. Anything else is
        // refused before a byte reaches wwwroot, where it would be served back to every browser.
        if (!await LooksLikeAsync(file, file.ContentType))
            return BadRequest(ApiResponse.Fail("The file is not a valid JPEG, PNG, or WebP image."));

        var ext = file.ContentType switch
        {
            "image/png"  => ".png",
            "image/webp" => ".webp",
            _            => ".jpg"
        };

        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "profile-pictures");
        Directory.CreateDirectory(uploadsDir);

        foreach (var old in Directory.GetFiles(uploadsDir, $"{userId}_*"))
            System.IO.File.Delete(old);

        var fileName = $"{userId}_{Guid.NewGuid():N}{ext}";
        var filePath  = Path.Combine(uploadsDir, fileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
            await file.CopyToAsync(stream);

        var pictureUrl = $"{Request.Scheme}://{Request.Host}/uploads/profile-pictures/{fileName}";
        await _authService.UpdateProfilePictureUrlAsync(userId, pictureUrl);

        return Ok(ApiResponse<object>.Ok(new { profilePictureUrl = pictureUrl }, "Profile picture updated."));
    }

    /// <summary>Whether the file starts with the signature of the image type it claims to be.</summary>
    private static async Task<bool> LooksLikeAsync(IFormFile file, string contentType)
    {
        var head = new byte[12];
        int read;
        await using (var stream = file.OpenReadStream())
            read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false);

        return contentType.ToLowerInvariant() switch
        {
            "image/png"  => read >= 8 && head.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "image/jpeg" => read >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF,
            "image/webp" => read >= 12 && head.AsSpan(0, 4).SequenceEqual("RIFF"u8) && head.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _            => false
        };
    }

    // ── DELETE /api/auth/profile/picture ─────────────────────────────────────
    // Self-service: removes the token's user's own picture.
    [HttpDelete("profile/picture")]
    public async Task<IActionResult> DeleteProfilePicture()
    {
        var userId = User.GetUserId();
        if (userId == 0) return Unauthorized(ApiResponse.Fail("Invalid token"));

        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "profile-pictures");
        if (Directory.Exists(uploadsDir))
        {
            foreach (var old in Directory.GetFiles(uploadsDir, $"{userId}_*"))
                System.IO.File.Delete(old);
        }

        await _authService.UpdateProfilePictureUrlAsync(userId, null);
        return Ok(ApiResponse.Ok("Profile picture removed."));
    }

    // ── Legacy user & role administration ─────────────────────────────────────
    //
    // Duplicates of api/users and api/roles (UsersController, RolesController), which are what the frontend
    // calls; nothing in the app calls these. Kept for API clients, behind the same USER_MANAGE, and held to
    // a stricter rule in AuthService: own organization only (a super admin may cross), nobody edits
    // themselves or a super admin, and nobody but a super admin grants a permission they do not hold.

    // ── GET /api/auth/users ───────────────────────────────────────────────────
    [RequirePermission(PermissionCodes.USER_MANAGE)]
    [HttpGet("users")]
    public async Task<IActionResult> GetAllUsers([FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0) pageSize = 10;
        return Ok(ApiResponse<object>.Ok(await _authService.GetAllUsersAsync(page, pageSize, AuthCaller.From(User)), "Users retrieved successfully"));
    }

    // ── PATCH /api/auth/users/{userId}/deactivate ─────────────────────────────
    [RequirePermission(PermissionCodes.USER_MANAGE)]
    [HttpPatch("users/{userId:int}/deactivate")]
    public async Task<IActionResult> DeactivateUser(int userId)
    {
        await _authService.DeactivateUserAsync(userId, AuthCaller.From(User));
        return Ok(ApiResponse.Ok("User deactivated successfully"));
    }

    // ── GET /api/auth/roles/{roleId}/permissions ──────────────────────────────
    [RequirePermission(PermissionCodes.USER_MANAGE)]
    [HttpGet("roles/{roleId:int}/permissions")]
    public async Task<IActionResult> GetRolePermissions(int roleId) =>
        Ok(ApiResponse<object>.Ok(await _authService.GetPermissionsByRoleAsync(roleId, AuthCaller.From(User)), "Permissions retrieved"));

    // ── PUT /api/auth/roles/{roleId}/permissions ──────────────────────────────
    // Platform administration: a global role is shared by every organization, so changing one additionally
    // needs the is_super_admin claim (AuthService). An organization edits its own roles through api/roles.
    [RequirePermission(PermissionCodes.PLATFORM_SUPER_ADMIN)]
    [HttpPut("roles/{roleId:int}/permissions")]
    public async Task<IActionResult> SaveRolePermissions(int roleId, [FromBody] List<PermissionModel> permissions)
    {
        await _authService.SaveRolePermissionsAsync(roleId, permissions, AuthCaller.From(User));
        return Ok(ApiResponse.Ok("Role permissions saved"));
    }

    // ── GET /api/auth/users/{userId}/permissions ──────────────────────────────
    [RequirePermission(PermissionCodes.USER_MANAGE)]
    [HttpGet("users/{userId:int}/permissions")]
    public async Task<IActionResult> GetUserPermissions(int userId) =>
        Ok(ApiResponse<object>.Ok(await _authService.GetUserPermissionsAsync(userId, AuthCaller.From(User)), "Permissions retrieved"));

    // ── PUT /api/auth/users/{userId}/permissions ──────────────────────────────
    [RequirePermission(PermissionCodes.USER_MANAGE)]
    [HttpPut("users/{userId:int}/permissions")]
    public async Task<IActionResult> SaveUserPermissions(int userId, [FromBody] List<PermissionModel> permissions)
    {
        await _authService.SaveUserPermissionsAsync(userId, permissions, AuthCaller.From(User));
        return Ok(ApiResponse.Ok("User permissions saved"));
    }
}

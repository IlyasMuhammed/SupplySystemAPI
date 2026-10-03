using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SMS.Modules.Auth.Controllers;
using SMS.Modules.Auth.Models;
using SMS.Modules.Auth.Services;
using SMS.Shared.Authorization;
using Xunit;

namespace SMS.Modules.Auth.Tests;

/// <summary>
/// Cross-cutting security audit of <c>api/auth</c>. The global AuthorizeFilter only requires a sign-in, so every
/// action below without <c>[RequirePermission]</c> is open to ANY signed-in user of ANY organization — an Auditor or
/// a Requester included. Roles are global (shared by every organization) and RolePermission rows are not
/// tenant-scoped, so changing a role's permissions changes them for every tenant.
/// </summary>
public class AuthControllerSecurityAuditTests
{
    private static MethodInfo Action(string name) =>
        typeof(AuthController).GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;

    [Theory]
    [InlineData(nameof(AuthController.SaveRolePermissions))]   // PUT  roles/{roleId}/permissions — global roles, every tenant
    [InlineData(nameof(AuthController.SaveUserPermissions))]   // PUT  users/{userId}/permissions — grant yourself everything
    [InlineData(nameof(AuthController.DeactivateUser))]        // PATCH users/{userId}/deactivate — lock out any admin
    [InlineData(nameof(AuthController.GetAllUsers))]           // GET  users — every user's email and phone
    [InlineData(nameof(AuthController.GetRolePermissions))]
    [InlineData(nameof(AuthController.GetUserPermissions))]
    public void SecurityAudit_user_and_role_administration_requires_a_permission_not_just_a_sign_in(string action)
    {
        var method = Action(action);
        (method.GetCustomAttributes<RequirePermissionAttribute>().Any()
         || typeof(AuthController).GetCustomAttributes<RequirePermissionAttribute>().Any())
            .Should().BeTrue($"AuthController.{action} administers users/roles; a sign-in alone lets any user escalate");
    }

    private static AuthController SignedInAs(int userId, Mock<IAuthService> service) => new(service.Object, Mock.Of<IWebHostEnvironment>())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId.ToString())], "Test"))
            }
        }
    };

    [Fact]
    public void SecurityAudit_a_user_cannot_change_another_users_profile_e_g_their_email()
    {
        // Changing another user's (an admin's, the super admin's) e-mail and then using "forgot password" hands the
        // reset code to the attacker: an account takeover. The id must come from the token, not the body.
        var service = new Mock<IAuthService>();
        service.Setup(s => s.UpdatePersonalInformation(It.IsAny<UpdatePersonalInfoModel>())).Returns(1);

        var result = SignedInAs(42, service).UpdateProfile(new UpdatePersonalInfoModel
        {
            UserID = 1, FirstName = "Mallory", Email = "attacker@example.com"
        });

        service.Verify(s => s.UpdatePersonalInformation(It.Is<UpdatePersonalInfoModel>(m => m.UserID != 42)), Times.Never(),
            "user 42 may only edit user 42");
        if (result is OkObjectResult)
            service.Verify(s => s.UpdatePersonalInformation(It.Is<UpdatePersonalInfoModel>(m => m.UserID == 42)), Times.Once());
    }

    [Fact]
    public void SecurityAudit_a_user_cannot_change_another_users_password()
    {
        var service = new Mock<IAuthService>();
        service.Setup(s => s.UpdatePasswordInformation(It.IsAny<UpdatePasswordModel>())).Returns(1);

        SignedInAs(42, service).UpdatePassword(new UpdatePasswordModel
        {
            UserID = 1, CurrentPassword = "guess", NewPassword = "Owned!234", ConfirmPassword = "Owned!234"
        });

        service.Verify(s => s.UpdatePasswordInformation(It.Is<UpdatePasswordModel>(m => m.UserID != 42)), Times.Never(),
            "the body's UserID must not choose whose password is checked and changed");
    }
}

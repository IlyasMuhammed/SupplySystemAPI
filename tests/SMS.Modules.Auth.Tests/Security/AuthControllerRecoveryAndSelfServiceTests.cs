using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SMS.Modules.Auth.Controllers;
using SMS.Modules.Auth.Models;
using SMS.Modules.Auth.Services;
using SMS.Shared.Pagination;
using Xunit;

namespace SMS.Modules.Auth.Tests.Security;

/// <summary>
/// The anonymous recovery endpoints and the signed-in self-service ones, at the controller: what they answer,
/// and what they refuse before the service is ever asked.
/// </summary>
public class AuthControllerRecoveryAndSelfServiceTests
{
    private const string Strong = "Str0ng!Pass";

    private static AuthController Controller(Mock<IAuthService> service, int? signedInAs = null, IWebHostEnvironment? env = null)
    {
        var http = new DefaultHttpContext();
        if (signedInAs is { } id)
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", id.ToString())], "Test"));
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("api.test");
        return new AuthController(service.Object, env ?? Mock.Of<IWebHostEnvironment>())
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private static string MessageOf(IActionResult result) =>
        ((ApiResponse)((ObjectResult)result).Value!).Message;

    // ── forgot-password: no account enumeration ────────────────────────────────

    [Fact]
    public void Forgot_password_answers_an_unknown_email_exactly_as_it_answers_a_real_one()
    {
        var service = new Mock<IAuthService>();
        service.Setup(s => s.SendPasswordResetToken("real@a.test")).Returns(1);
        service.Setup(s => s.SendPasswordResetToken("nobody@a.test")).Returns(0);

        var real = Controller(service).ForgotPassword("real@a.test");
        var unknown = Controller(service).ForgotPassword("nobody@a.test");

        real.Should().BeOfType<OkObjectResult>();
        unknown.Should().BeOfType<OkObjectResult>("an error here tells anyone which addresses have accounts");
        MessageOf(unknown).Should().Be(MessageOf(real));
    }

    [Fact]
    public void Forgot_password_with_no_email_is_refused_without_asking_the_service()
    {
        var service = new Mock<IAuthService>();

        Controller(service).ForgotPassword("  ").Should().BeOfType<BadRequestObjectResult>();

        service.Verify(s => s.SendPasswordResetToken(It.IsAny<string>()), Times.Never());
    }

    // ── reset-password ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("short1!")]       // too short
    [InlineData("alllowercase1!")] // no upper case
    [InlineData("NoDigitsHere!")]  // no digit
    [InlineData("NoSpecial123")]   // no special character
    public void Reset_password_refuses_a_password_that_register_and_accept_invite_would_refuse(string weak)
    {
        var service = new Mock<IAuthService>();
        service.Setup(s => s.SetPasswordVerification(It.IsAny<ForgotPasswordModel>())).Returns(1);

        var result = Controller(service).ResetPassword(new ForgotPasswordModel { Email = "real@a.test", Password = weak, verificationCode = "123456" });

        result.Should().BeOfType<BadRequestObjectResult>();
        service.Verify(s => s.SetPasswordVerification(It.IsAny<ForgotPasswordModel>()), Times.Never(),
            "a weak password must not even spend one of the code's attempts");
    }

    [Fact]
    public void Reset_password_says_when_the_code_has_been_spent_by_too_many_wrong_guesses()
    {
        var service = new Mock<IAuthService>();
        service.Setup(s => s.SetPasswordVerification(It.IsAny<ForgotPasswordModel>())).Returns(-2);

        var result = Controller(service).ResetPassword(new ForgotPasswordModel { Email = "real@a.test", Password = Strong, verificationCode = "000000" });

        result.Should().BeOfType<BadRequestObjectResult>();
        MessageOf(result).Should().Contain("new code");
    }

    // ── password change (signed in) ────────────────────────────────────────────

    [Theory]
    [InlineData("weak", "weak")]
    [InlineData(Strong, Strong + "x")]
    public void Changing_your_password_refuses_a_weak_or_unconfirmed_new_password(string newPassword, string confirm)
    {
        var service = new Mock<IAuthService>();
        service.Setup(s => s.UpdatePasswordInformation(It.IsAny<UpdatePasswordModel>())).Returns(1);

        var result = Controller(service, signedInAs: 42).UpdatePassword(new UpdatePasswordModel
        {
            CurrentPassword = "Current#Pass1", NewPassword = newPassword, ConfirmPassword = confirm
        });

        result.Should().BeOfType<BadRequestObjectResult>();
        service.Verify(s => s.UpdatePasswordInformation(It.IsAny<UpdatePasswordModel>()), Times.Never());
    }

    [Fact]
    public void Changing_your_password_acts_on_the_token_user_even_when_the_body_names_nobody()
    {
        var service = new Mock<IAuthService>();
        service.Setup(s => s.UpdatePasswordInformation(It.IsAny<UpdatePasswordModel>())).Returns(1);

        var result = Controller(service, signedInAs: 42).UpdatePassword(new UpdatePasswordModel
        {
            CurrentPassword = "Current#Pass1", NewPassword = Strong, ConfirmPassword = Strong
        });

        result.Should().BeOfType<OkObjectResult>();
        service.Verify(s => s.UpdatePasswordInformation(It.Is<UpdatePasswordModel>(m => m.UserID == 42)), Times.Once());
    }

    [Fact]
    public void A_profile_change_to_an_email_another_account_uses_is_refused_as_a_bad_request()
    {
        var service = new Mock<IAuthService>();
        service.Setup(s => s.UpdatePersonalInformation(It.IsAny<UpdatePersonalInfoModel>())).Returns(-2);

        var result = Controller(service, signedInAs: 42).UpdateProfile(new UpdatePersonalInfoModel { FirstName = "Rita", Email = "taken@a.test" });

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── profile picture: the bytes, not the client's word ──────────────────────

    [Fact]
    public async Task A_profile_picture_whose_bytes_are_not_an_image_is_refused_whatever_content_type_it_claims()
    {
        var service = new Mock<IAuthService>();
        var webRoot = Directory.CreateTempSubdirectory("auth-pic-");
        try
        {
            var env = Mock.Of<IWebHostEnvironment>(e => e.WebRootPath == webRoot.FullName);
            var bytes = "MZ\u0090\0\u0003\0\0\0this is a program, not a picture"u8.ToArray();

            var result = await Controller(service, signedInAs: 42, env).UploadProfilePicture(new ProfilePictureUploadRequest
            {
                File = FormFile(bytes, "image/png")
            });

            result.Should().BeOfType<BadRequestObjectResult>();
            service.Verify(s => s.UpdateProfilePictureUrlAsync(It.IsAny<int>(), It.IsAny<string?>()), Times.Never());
            Directory.GetFiles(webRoot.FullName, "*", SearchOption.AllDirectories).Should().BeEmpty("nothing may be written to disk");
        }
        finally { webRoot.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("image/png",  new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 })]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 0 })]
    [InlineData("image/webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0x24, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 })]
    public async Task A_real_image_is_stored_for_the_token_user(string contentType, byte[] header)
    {
        var service = new Mock<IAuthService>();
        var webRoot = Directory.CreateTempSubdirectory("auth-pic-");
        try
        {
            var env = Mock.Of<IWebHostEnvironment>(e => e.WebRootPath == webRoot.FullName);
            var bytes = header.Concat(new byte[64]).ToArray();

            var result = await Controller(service, signedInAs: 42, env).UploadProfilePicture(new ProfilePictureUploadRequest
            {
                File = FormFile(bytes, contentType)
            });

            result.Should().BeOfType<OkObjectResult>();
            service.Verify(s => s.UpdateProfilePictureUrlAsync(42, It.Is<string?>(u => u!.Contains("/uploads/profile-pictures/42_"))), Times.Once());
        }
        finally { webRoot.Delete(recursive: true); }
    }

    private static IFormFile FormFile(byte[] bytes, string contentType) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "upload.bin")
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
}

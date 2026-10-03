using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Integration.Auth;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Integration.Controllers.Admin;

/// <summary>
/// The QuickBooks connection card on SCM's settings screen: status, connect, test, disconnect.
/// A person is acting, so this is JWT + <c>INTEGRATION_*</c> permissions; the anonymous half of
/// connecting is <see cref="CallbackController"/>.
/// </summary>
[ApiController]
[Route("api/integrations/quickbooks")]
[RequiresFeature(IntegrationFeature.Code)]
public class ConnectionController : ControllerBase
{
    private readonly IQuickBooksConnectionService _connections;

    public ConnectionController(IQuickBooksConnectionService connections) => _connections = connections;

    /// <summary>Status, company, realm, environment, token expiries and mode.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet("connection")]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(ApiResponse<ConnectionStatusModel>.Ok(await _connections.GetStatusAsync(ct)));

    /// <summary>
    /// Starts connecting: returns Intuit's consent URL for the browser to open. 409 when the server has no
    /// Intuit app keys, or when a company is already connected (disconnect first).
    /// </summary>
    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPost("connect")]
    public async Task<IActionResult> Connect(CancellationToken ct) =>
        Ok(ApiResponse<ConnectResponse>.Ok(await _connections.ConnectAsync(User.GetUserId(), ct)));

    /// <summary>A cheap authenticated read of the company. Never throws for a QuickBooks-side failure — see Ok/Message.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpPost("test")]
    public async Task<IActionResult> Test(CancellationToken ct) =>
        Ok(ApiResponse<TestConnectionResult>.Ok(await _connections.TestAsync(ct)));

    /// <summary>Revokes at Intuit (best effort) and forgets the tokens. Mappings and the realm are kept.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpDelete("connection")]
    public async Task<IActionResult> Disconnect(CancellationToken ct) =>
        Ok(ApiResponse<ConnectionStatusModel>.Ok(await _connections.DisconnectAsync(User.GetUserId(), ct),
            "QuickBooks disconnected. Nothing in QuickBooks was changed."));
}

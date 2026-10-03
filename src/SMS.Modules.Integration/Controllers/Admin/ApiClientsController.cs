using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Integration.Auth;
using SMS.Modules.Integration.Core.ApiClients;
using SMS.Modules.Integration.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Integration.Controllers.Admin;

/// <summary>
/// Other systems allowed to push through the data endpoints, and their API keys. <c>INTEGRATION_MANAGE</c>
/// throughout, reads included: a client list is a list of who can write into the company's books.
/// The Angular app manages keys but never holds one.
/// </summary>
[ApiController]
[Route("api/integrations/quickbooks/api-clients")]
[RequiresFeature(IntegrationFeature.Code)]
[RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
public class ApiClientsController : ControllerBase
{
    private readonly IApiClientService _clients;

    public ApiClientsController(IApiClientService clients) => _clients = clients;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(ApiResponse<List<ApiClientModel>>.Ok(await _clients.ListAsync(ct)));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApiClientRequest request, CancellationToken ct) =>
        Ok(ApiResponse<ApiClientModel>.Ok(await _clients.CreateAsync(request, User.GetUserId(), ct)));

    /// <summary>The key is in this response and nowhere else, ever. At most two active keys per client.</summary>
    [HttpPost("{id:guid}/keys")]
    public async Task<IActionResult> IssueKey(Guid id, [FromBody] IssueApiKeyRequest? request, CancellationToken ct) =>
        Ok(ApiResponse<IssuedApiKeyModel>.Ok(
            await _clients.IssueKeyAsync(id, request ?? new IssueApiKeyRequest(), User.GetUserId(), ct),
            "Copy the key now — it cannot be shown again."));

    [HttpDelete("{id:guid}/keys/{keyId:guid}")]
    public async Task<IActionResult> RevokeKey(Guid id, Guid keyId, CancellationToken ct) =>
        Ok(ApiResponse<ApiClientModel>.Ok(await _clients.RevokeKeyAsync(id, keyId, User.GetUserId(), ct)));

    /// <summary>Deactivates the client and revokes all its keys. Records it already pushed stay mapped.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct) =>
        Ok(ApiResponse<ApiClientModel>.Ok(await _clients.DeactivateAsync(id, User.GetUserId(), ct)));
}

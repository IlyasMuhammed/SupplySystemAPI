using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Auth;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Controllers.Gateway;

// ── Response shapes (plan §5.2). One error shape everywhere: { code, message, errors? }. ─────────

public sealed class GatewayAcceptedResponse
{
    public string  Outcome { get; set; } = string.Empty;
    public string? State   { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<GatewayDependencyModel>? MissingDependencies { get; set; }
}

public sealed class GatewayDependencyModel
{
    public string Kind       { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
}

public sealed class GatewayDisabledResponse
{
    public string Outcome { get; set; } = nameof(GatewayOutcome.Disabled);
    public string Message { get; set; } = string.Empty;
}

public sealed class GatewayErrorResponse
{
    public string Code    { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GatewayError>? Errors { get; set; }
}

/// <summary>
/// The data endpoints other systems call with an API key (plan §5.2). A thin HTTP layer over
/// <see cref="IQuickBooksGateway"/> — the same validation and pipeline SCM's own modules use in-process.
/// Records are mapped under the API client's name, so they never collide with SCM's.
/// <para>
/// <b>Never <c>[AllowAnonymous]</c></b>: without an authenticated principal the tenant filter is
/// bypassed. The API-key scheme at class level turns every unauthenticated call into a 401.
/// </para>
/// </summary>
[ApiController]
[Route("api/gateway/quickbooks/v1")]
[Authorize(AuthenticationSchemes = ApiKeyDefaults.Scheme)]
[RequiresFeature("MODULE_INTEGRATION")]
[EnableRateLimiting(ApiKeyDefaults.RateLimitPolicy)]
public class QuickBooksDataController : ControllerBase
{
    private readonly IQuickBooksGateway                 _gateway;
    private readonly ILogger<QuickBooksDataController> _logger;

    public QuickBooksDataController(IQuickBooksGateway gateway, ILogger<QuickBooksDataController> logger)
    {
        _gateway = gateway;
        _logger  = logger;
    }

    [RequireApiScope(ApiScopes.CustomersWrite)]
    [HttpPut("customers/{externalId}")]
    public Task<IActionResult> PutCustomer(string externalId, [FromBody] CustomerPayload? payload, CancellationToken ct) =>
        UpsertAsync(SyncKind.Customer, externalId, payload, p => p.ExternalId, (p, id) => p.ExternalId = id,
            p => _gateway.UpsertCustomerAsync(p, ct));

    [RequireApiScope(ApiScopes.VendorsWrite)]
    [HttpPut("vendors/{externalId}")]
    public Task<IActionResult> PutVendor(string externalId, [FromBody] VendorPayload? payload, CancellationToken ct) =>
        UpsertAsync(SyncKind.Vendor, externalId, payload, p => p.ExternalId, (p, id) => p.ExternalId = id,
            p => _gateway.UpsertVendorAsync(p, ct));

    [RequireApiScope(ApiScopes.ItemsWrite)]
    [HttpPut("items/{externalId}")]
    public Task<IActionResult> PutItem(string externalId, [FromBody] ItemPayload? payload, CancellationToken ct) =>
        UpsertAsync(SyncKind.Item, externalId, payload, p => p.ExternalId, (p, id) => p.ExternalId = id,
            p => _gateway.UpsertItemAsync(p, ct));

    [RequireApiScope(ApiScopes.InvoicesWrite)]
    [HttpPut("sales-invoices/{externalId}")]
    public Task<IActionResult> PutSalesInvoice(string externalId, [FromBody] SalesInvoicePayload? payload, CancellationToken ct) =>
        UpsertAsync(SyncKind.SalesInvoice, externalId, payload, p => p.ExternalId, (p, id) => p.ExternalId = id,
            p => _gateway.UpsertSalesInvoiceAsync(p, ct));

    [RequireApiScope(ApiScopes.InvoicesWrite)]
    [HttpPost("sales-invoices/{externalId}/void")]
    public async Task<IActionResult> VoidSalesInvoice(string externalId, CancellationToken ct)
    {
        var result = await _gateway.VoidSalesInvoiceAsync(externalId, ct);
        Log("Void", SyncKind.SalesInvoice, externalId, result);
        return ToHttp(result);
    }

    [RequireApiScope(ApiScopes.BillsWrite)]
    [HttpPut("bills/{externalId}")]
    public Task<IActionResult> PutBill(string externalId, [FromBody] BillPayload? payload, CancellationToken ct) =>
        UpsertAsync(SyncKind.Bill, externalId, payload, p => p.ExternalId, (p, id) => p.ExternalId = id,
            p => _gateway.UpsertBillAsync(p, ct));

    /// <summary>One record's status. <c>kind</c>: customers | vendors | items | sales-invoices | bills.</summary>
    [RequireApiScope(ApiScopes.StatusRead)]
    [HttpGet("{kind}/{externalId}")]
    public async Task<IActionResult> GetStatus(string kind, string externalId, CancellationToken ct)
    {
        if (!SyncKindNames.TryParse(kind, out var syncKind))
            return NotFound(new GatewayErrorResponse { Code = "unknown_kind", Message = $"Unknown record kind '{kind}'. Use customers, vendors, items, sales-invoices or bills." });

        var status = (await _gateway.GetStatusAsync(syncKind, [externalId], ct)).FirstOrDefault();
        return status is null
            ? NotFound(new GatewayErrorResponse { Code = "not_found", Message = $"No {syncKind} with ExternalId '{externalId}' has been sent." })
            : Ok(status);
    }

    /// <summary>Batch status: <c>{ kind, externalIds[] }</c> → <c>{ items: [...] }</c>.</summary>
    [RequireApiScope(ApiScopes.StatusRead)]
    [HttpPost("status")]
    public async Task<IActionResult> PostStatus([FromBody] StatusLookupRequest? request, CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new GatewayErrorResponse { Code = "invalid_body", Message = "Send { kind, externalIds }." });
        if (!SyncKindNames.TryParse(request.Kind, out var kind))
            return BadRequest(new GatewayErrorResponse { Code = "unknown_kind", Message = $"Unknown record kind '{request.Kind}'." });

        var ids = (request.ExternalIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids.Count > SyncAdminService.MaxIdsPerRequest)
            return BadRequest(new GatewayErrorResponse { Code = "too_many_ids", Message = $"At most {SyncAdminService.MaxIdsPerRequest} ids per request." });

        var items = await _gateway.GetStatusAsync(kind, ids, ct);
        return Ok(new StatusLookupResult { Items = items.ToList() });
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private async Task<IActionResult> UpsertAsync<T>(
        SyncKind kind, string externalId, T? payload, Func<T, string?> getId, Action<T, string> setId,
        Func<T, Task<GatewayResult>> send) where T : class
    {
        if (payload is null)
            return BadRequest(new GatewayErrorResponse { Code = "invalid_body", Message = "The request body is missing or is not valid JSON." });

        // The id in the URL is the record's identity; a different one in the body is a mistake, not a rename.
        var bodyId = getId(payload);
        if (!string.IsNullOrWhiteSpace(bodyId) && !string.Equals(bodyId.Trim(), externalId?.Trim(), StringComparison.Ordinal))
            return BadRequest(new GatewayErrorResponse
            {
                Code    = "external_id_mismatch",
                Message = $"The body's externalId '{bodyId}' differs from the one in the URL '{externalId}'. Send the same id, or leave it out of the body."
            });

        setId(payload, externalId ?? string.Empty);

        var result = await send(payload);
        Log("Upsert", kind, externalId, result);
        return ToHttp(result);
    }

    /// <summary>GatewayResult → HTTP (plan §5.2).</summary>
    internal IActionResult ToHttp(GatewayResult result) => result.Outcome switch
    {
        GatewayOutcome.Accepted => StatusCode(StatusCodes.Status202Accepted, new GatewayAcceptedResponse
        {
            Outcome = result.Outcome.ToString(),
            State   = result.State?.ToString()
        }),

        // External callers have no source the gateway can ask: they must send these records themselves.
        GatewayOutcome.WaitingOnDependency => StatusCode(StatusCodes.Status202Accepted, new GatewayAcceptedResponse
        {
            Outcome             = result.Outcome.ToString(),
            State               = (result.State ?? SyncState.WaitingOnDependency).ToString(),
            MissingDependencies = result.MissingDependencies
                .Select(d => new GatewayDependencyModel { Kind = d.Kind.ToString(), ExternalId = d.ExternalId })
                .ToList()
        }),

        GatewayOutcome.Invalid => BadRequest(new GatewayErrorResponse
        {
            Code    = "validation_failed",
            Message = result.Errors.Count == 1
                ? result.Errors[0].Message
                : $"The record was refused by {result.Errors.Count} validation rules; nothing was sent. See errors.",
            Errors  = result.Errors
        }),

        GatewayOutcome.NotConnected => Conflict(new GatewayErrorResponse
        {
            Code    = "not_connected",
            Message = "This organization has no usable QuickBooks connection. Nothing was stored; send it again once QuickBooks is connected."
        }),

        GatewayOutcome.Disabled => Ok(new GatewayDisabledResponse
        {
            Message = "Syncing of this record is switched off (auto-push off, skipped in matching, or dated before the start date). Nothing to do."
        }),

        _ => StatusCode(StatusCodes.Status500InternalServerError, new GatewayErrorResponse { Code = "unexpected_outcome", Message = result.Outcome.ToString() })
    };

    private void Log(string action, SyncKind kind, string? externalId, GatewayResult result) =>
        _logger.LogInformation(
            "QuickBooks data API: client {ApiClientId} {Action} {Kind} {ExternalId} → {Outcome} ({State}).",
            User.FindFirst(IntegrationClaims.ApiClientId)?.Value ?? "unknown", action, kind, externalId, result.Outcome, result.State);
}

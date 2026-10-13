using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Crm.Models;
using SMS.Modules.Crm.Services;
using SMS.Shared.Pagination;

namespace SMS.Modules.Crm.Controllers;

/// <summary>
/// A41 §14.6 / D-16 — external lead capture (website forms, other systems). No user session: the X-Api-Key header
/// decides the organization. The body is snake_case as the spec shows it (camelCase is accepted too).
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/public/crm/leads")]
public sealed class PublicLeadsController : ControllerBase
{
    private static readonly JsonSerializerOptions SnakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true
    };

    private readonly IPublicLeadService _leads;

    public PublicLeadsController(IPublicLeadService leads) => _leads = leads;

    [HttpPost]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> Create([FromHeader(Name = "X-Api-Key")] string? apiKey, CancellationToken ct)
    {
        PublicLeadRequest? req;
        try
        {
            using var reader = new StreamReader(Request.Body);
            var json = await reader.ReadToEndAsync(ct);
            req = Parse(json);
        }
        catch (JsonException)
        {
            return BadRequest(ApiResponse.Fail("The body is not valid JSON."));
        }
        if (req is null) return BadRequest(ApiResponse.Fail("A lead is required."));

        try
        {
            var result = await _leads.CreateAsync(apiKey, req, ct);
            return Ok(ApiResponse<PublicLeadResult>.Ok(result, "Lead created."));
        }
        catch (CrmRateLimitException ex)
        {
            Response.Headers.RetryAfter = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests, ApiResponse.Fail(ex.Message));
        }
    }

    /// <summary>snake_case first (the spec's body), then camelCase/PascalCase for callers that send those.</summary>
    internal static PublicLeadRequest? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var snake = JsonSerializer.Deserialize<PublicLeadRequest>(json, SnakeCase);
        var camel = JsonSerializer.Deserialize<PublicLeadRequest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (snake is null) return camel;
        if (camel is null) return snake;
        return new PublicLeadRequest
        {
            FirstName = snake.FirstName ?? camel.FirstName, LastName = snake.LastName ?? camel.LastName,
            CompanyName = snake.CompanyName ?? camel.CompanyName, Email = snake.Email ?? camel.Email, Phone = snake.Phone ?? camel.Phone,
            Topic = snake.Topic ?? camel.Topic, Description = snake.Description ?? camel.Description,
            SourceCode = snake.SourceCode ?? camel.SourceCode, CampaignCode = snake.CampaignCode ?? camel.CampaignCode,
            SourceMedium = snake.SourceMedium ?? camel.SourceMedium, SourceReference = snake.SourceReference ?? camel.SourceReference,
            ExternalSourceSystem = snake.ExternalSourceSystem ?? camel.ExternalSourceSystem,
            ExternalSourceId = snake.ExternalSourceId ?? camel.ExternalSourceId
        };
    }
}

using Microsoft.AspNetCore.Mvc;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Onboarding.Templates;

/// <summary>A50 TPL (FSD §9.6, tasks 320–322) — api/onboarding/document-templates (API-CONTRACT "D-TPL").</summary>
[ApiController]
[Route("api/onboarding/document-templates")]
[RequirePermission(P.DOC_TEMPLATE_MANAGE)]
public sealed class DocumentTemplatesController : ControllerBase
{
    private readonly IDocumentTemplateService _svc;

    public DocumentTemplatesController(IDocumentTemplateService svc) => _svc = svc;

    private int? UserId => User.GetUserId() is var id && id > 0 ? id : null;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? type, [FromQuery] bool includeInactive, CancellationToken ct) =>
        Ok(ApiResponse<List<DocumentTemplateModel>>.Ok(await _svc.ListAsync(type, includeInactive, UserId, ct)));

    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<DocumentTemplateModel>.Ok(await _svc.GetAsync(uuid, ct)));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDocumentTemplateRequest request, CancellationToken ct) =>
        Ok(ApiResponse<DocumentTemplateModel>.Ok(await _svc.CreateAsync(request, UserId, ct), "Template created."));

    [HttpPut("{uuid:guid}")]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateDocumentTemplateRequest request, CancellationToken ct) =>
        Ok(ApiResponse<DocumentTemplateModel>.Ok(await _svc.UpdateAsync(uuid, request, UserId, ct), "Template saved."));

    [HttpPost("{uuid:guid}/set-default")]
    public async Task<IActionResult> SetDefault(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<DocumentTemplateModel>.Ok(await _svc.SetDefaultAsync(uuid, UserId, ct), "Default template set."));

    [HttpPost("reset/{type}")]
    public async Task<IActionResult> Reset(string type, CancellationToken ct) =>
        Ok(ApiResponse<DocumentTemplateModel>.Ok(await _svc.ResetToDefaultAsync(type, UserId, ct), "Template reset to the default."));

    /// <summary>A sample document as a PDF; the optional body (the PUT shape) previews unsaved values.</summary>
    [HttpPost("{uuid:guid}/preview")]
    public async Task<IActionResult> Preview(Guid uuid, [FromBody] UpdateDocumentTemplateRequest? unsaved, CancellationToken ct) =>
        File(await _svc.PreviewAsync(uuid, unsaved, ct), "application/pdf", "template-preview.pdf");

    [HttpGet("placeholders/{type}")]
    public IActionResult Placeholders(string type) => Ok(ApiResponse<IReadOnlyList<PlaceholderModel>>.Ok(_svc.Placeholders(type)));
}

/// <summary>A50 TPL (FSD §9.7, task 323) — api/onboarding/email-templates.</summary>
[ApiController]
[Route("api/onboarding/email-templates")]
[RequirePermission(P.EMAIL_TEMPLATE_MANAGE)]
public sealed class EmailTemplatesController : ControllerBase
{
    private readonly IEmailTemplateService _svc;

    public EmailTemplatesController(IEmailTemplateService svc) => _svc = svc;

    private int? UserId => User.GetUserId() is var id && id > 0 ? id : null;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? type, [FromQuery] bool includeInactive, CancellationToken ct) =>
        Ok(ApiResponse<List<EmailTemplateModel>>.Ok(await _svc.ListAsync(type, includeInactive, ct)));

    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) => Ok(ApiResponse<EmailTemplateModel>.Ok(await _svc.GetAsync(uuid, ct)));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateEmailTemplateRequest request, CancellationToken ct) =>
        Ok(ApiResponse<EmailTemplateModel>.Ok(await _svc.CreateAsync(request, UserId, ct), "Template created."));

    [HttpPut("{uuid:guid}")]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateEmailTemplateRequest request, CancellationToken ct) =>
        Ok(ApiResponse<EmailTemplateModel>.Ok(await _svc.UpdateAsync(uuid, request, UserId, ct), "Template saved."));

    [HttpPost("{uuid:guid}/set-default")]
    public async Task<IActionResult> SetDefault(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<EmailTemplateModel>.Ok(await _svc.SetDefaultAsync(uuid, UserId, ct), "Default template set."));

    [HttpPost("reset/{type}")]
    public async Task<IActionResult> Reset(string type, CancellationToken ct) =>
        Ok(ApiResponse<EmailTemplateModel>.Ok(await _svc.ResetToDefaultAsync(type, UserId, ct), "Template reset to the default."));

    [HttpPost("{uuid:guid}/preview")]
    public async Task<IActionResult> Preview(Guid uuid, [FromBody] PreviewEmailRequest? request, CancellationToken ct) =>
        Ok(ApiResponse<EmailPreviewModel>.Ok(await _svc.PreviewAsync(uuid, request?.Placeholders, ct)));

    [HttpPost("{uuid:guid}/send-test")]
    public async Task<IActionResult> SendTest(Guid uuid, [FromBody] SendTestEmailRequest request, CancellationToken ct)
    {
        var result = await _svc.SendTestAsync(uuid, request?.ToEmail, ct);
        return Ok(ApiResponse<SendTestEmailResult>.Ok(result, result.Sent ? "Test email sent." : $"The test email was not sent: {result.Error}"));
    }

    [HttpGet("placeholders/{type}")]
    public IActionResult Placeholders(string type) => Ok(ApiResponse<IReadOnlyList<PlaceholderModel>>.Ok(_svc.Placeholders(type)));
}


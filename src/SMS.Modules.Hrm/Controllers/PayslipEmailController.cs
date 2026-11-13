using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Hrm.Services.Pay;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Controllers;

/// <summary>
/// A50 D-30 / D-93 — emailing payslips (docs/hrm/API-CONTRACT.md §9.x). The recipient is always the employee's own address on file: neither
/// request has a "to". One payslip: payroll-view in scope, or self service on one's own. A whole run: HRM_PAYSLIP_EMAIL and access to all employees.
/// </summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Payroll)]
public sealed class PayslipEmailController : HrmControllerBase
{
    private readonly IPayslipEmailService _svc;
    public PayslipEmailController(ITenantContext tenant, IPayslipEmailService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("Email one payslip to the employee's own address")]
    [HttpPost("api/hrm/payslips/{uuid:guid}/email"), RequirePermission(P.HRM_PAYROLL_VIEW, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Send(Guid uuid, CancellationToken ct)
    {
        var result = await _svc.SendAsync(Caller, uuid, ct);
        Response.Headers.CacheControl = "private, no-store";
        return Done(result, result.Sent ? "Payslip emailed." : "The email could not be sent.");
    }

    [Microsoft.AspNetCore.Http.EndpointSummary("Email every payslip of a finalized run to its employee")]
    [HttpPost("api/hrm/payroll-runs/{uuid:guid}/payslips/email"), RequirePermission(P.HRM_PAYSLIP_EMAIL)]
    public async Task<IActionResult> SendRun(Guid uuid, [FromBody] PayslipBulkEmailRequest? req, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Done(await _svc.SendRunAsync(Caller, uuid, req, ct), "Payslips emailed.");
    }
}

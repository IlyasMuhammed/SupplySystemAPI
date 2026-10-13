using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// D-4 — the GL setup gate, applied ONLY to post-setup GL endpoints (A42: the GL dashboard; A43: journals and postings).
/// Existing finance endpoints and every A42 setup endpoint are not gated. While the organization's setup is not Complete
/// the action does not run: 403 <c>{ success: false, error: "FINANCE_SETUP_REQUIRED", message, currentStatus, redirectUrl }</c>.
/// Runs after authorization and the feature gate (it is an action filter).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequiresGlSetupAttribute : ActionFilterAttribute
{
    internal const string Message = "The general ledger setup must be completed before using this feature.";

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var tenant = services.GetRequiredService<ITenantContext>();
        var status = await services.GetRequiredService<ISetupStatusService>()
            .GetStatusCodeAsync(tenant.OrganizationId, context.HttpContext.RequestAborted);

        if (status == SetupStatuses.Complete)
        {
            await next();
            return;
        }

        context.Result = new ObjectResult(Body(status)) { StatusCode = StatusCodes.Status403Forbidden };
    }

    internal static GlSetupRequiredModel Body(string currentStatus) => new()
    {
        Success = false,
        Message = Message,
        CurrentStatus = currentStatus,
        RedirectUrl = AccountingCodes.SetupRedirectUrl,
    };
}

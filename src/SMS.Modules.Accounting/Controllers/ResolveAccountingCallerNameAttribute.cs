using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;

namespace SMS.Modules.Accounting.Controllers;

/// <summary>
/// A47 QA-1 — resolves the caller's display name once per request (the organization's user directory by user id, else the
/// token's name claim; <see cref="AccountingCaller.ResolveNameAsync"/>) and keeps it in <c>HttpContext.Items</c>, where
/// <see cref="AccountingControllerBase"/>'s caller picks it up. So every name snapshot an Accounting action stores
/// (journal CreatedByName/PostedByName/ModifiedByName, reconcile groups, period/year close, the GL history) gets a name,
/// with old tokens too. <c>IOrgUserDirectory</c> is optional (Auth implements it). REV-14 — a resolved name is cached per
/// (organization, user) for <see cref="CacheFor"/> when an <see cref="IMemoryCache"/> is registered, so reads do not hit
/// Auth on every request (a renamed user shows the new name within minutes).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ResolveAccountingCallerNameAttribute : ActionFilterAttribute
{
    internal const string ItemKey = "SMS.Accounting.CallerName";
    internal static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    internal static string CacheKey(Guid organizationId, int userId) => $"acct:caller-name:{organizationId:N}:{userId}";

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.Identity?.IsAuthenticated == true)
        {
            var services = http.RequestServices;
            var org = services.GetRequiredService<ITenantContext>().OrganizationId;
            var cache = services.GetService<IMemoryCache>();
            var key = CacheKey(org, http.User.GetUserId());
            if (cache is not null && cache.TryGetValue(key, out string? cached))
                http.Items[ItemKey] = cached;
            else
            {
                var name = await AccountingCaller.ResolveNameAsync(http.User, org, services.GetService<IOrgUserDirectory>(), http.RequestAborted);
                http.Items[ItemKey] = name;
                if (cache is not null && name is not null) cache.Set(key, name, CacheFor);
            }
        }
        await next();
    }
}

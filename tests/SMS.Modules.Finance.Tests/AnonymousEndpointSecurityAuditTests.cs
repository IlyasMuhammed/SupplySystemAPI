using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// Cross-cutting security audit: an anonymous request has no organization, and TenantContext then reports
/// IsSuperAdmin = true — the tenant query filter AND the module (feature) gate are bypassed. So an
/// <c>[AllowAnonymous]</c> action on an ordinary controller reads or writes every organization's rows. Only the
/// deliberately public controllers (sign-in, token-gated portals, webhooks, the OAuth callback, public tracking),
/// each of which looks rows up by a secret token or key, may be anonymous. This scans every SMS module assembly
/// in the test's output directory (Finance, Demand, Warehouse, WorkflowEngine and whatever else is referenced).
/// </summary>
public class AnonymousEndpointSecurityAuditTests
{
    private static readonly HashSet<string> DeliberatelyPublic = new(StringComparer.Ordinal)
    {
        "AuthController",              // login, refresh, register, activate, invite, password reset
        "RfqPortalController",         // supplier RFQ portal, by signed link token
        "SroPortalController",         // supplier return acknowledgement, by signed link token
        "CarrierWebhooksController",   // carrier webhooks, signature-checked and rate-limited
        "PublicTrackingController",    // public shipment tracking, by tracking token, rate-limited
        "CallbackController",          // QuickBooks OAuth callback, by one-time state token
        "WhatsAppWebhookController",   // WhatsApp webhook, verify-token checked
    };

    private static IEnumerable<Assembly> SmsAssemblies()
    {
        var dir = AppContext.BaseDirectory;
        foreach (var path in Directory.EnumerateFiles(dir, "SMS.*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith(".Tests", StringComparison.Ordinal)) continue;
            Assembly? assembly = null;
            try { assembly = Assembly.Load(new AssemblyName(name)); } catch (Exception) { }
            if (assembly is not null) yield return assembly;
        }
    }

    private static IEnumerable<Type> Controllers(Assembly assembly)
    {
        Type?[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types; }
        return types.Where(t => t is { IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t))!;
    }

    [Fact]
    public void SecurityAudit_no_ordinary_controller_action_is_anonymous()
    {
        var scanned = 0;
        var anonymous = new List<string>();

        foreach (var controller in SmsAssemblies().SelectMany(Controllers))
        {
            scanned++;
            if (DeliberatelyPublic.Contains(controller.Name)) continue;

            if (controller.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
                anonymous.Add($"{controller.FullName} (whole controller)");

            anonymous.AddRange(controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
                .Select(m => $"{controller.FullName}.{m.Name}"));
        }

        scanned.Should().BeGreaterThan(20, "the scan must actually have found the modules' controllers");
        anonymous.Should().BeEmpty("an anonymous request bypasses the tenant filter and the module gate: it would see every organization's data");
    }
}

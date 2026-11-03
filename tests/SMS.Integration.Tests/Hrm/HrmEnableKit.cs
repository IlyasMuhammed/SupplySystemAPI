using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SapAlignment;

namespace SMS.Integration.Tests.Hrm;

/// <summary>
/// A49 FD-9: how an organization gets HRM on. The super admin's licence no longer switches it on without a payroll cost center, so every
/// HRM E2E starts here: licence, the wizard's step (a cost center coded PAYROLL), the guarded enable.
/// </summary>
internal static class HrmEnableKit
{
    public const string PayrollCostCenter = "PAYROLL";

    public static async Task EnableHrmAsync(this SapKit root, ModOrg o, params string[] subFeatures)
    {
        await root.LicenceAsync(o.OrgId, ("MODULE_HRM", true));
        await o.K.Ok(o.K.Post("/api/hrm/setup/payroll-cost-center", new { newCostCenter = new { code = PayrollCostCenter, name = "Payroll" } }), "payroll cost center");
        await o.K.Ok(o.K.Post("/api/tenant/modules/MODULE_HRM/enable", new { }), "enable HRM");
        if (subFeatures.Length > 0)
            await root.LicenceAsync(o.OrgId, subFeatures.Select(f => (f, true)).ToArray());
    }
}

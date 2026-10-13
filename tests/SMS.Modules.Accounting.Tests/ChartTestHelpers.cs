using System.Text;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Services;

namespace SMS.Modules.Accounting.Tests;

/// <summary>BE1 phase 2 helpers over <see cref="AccountingHarness"/>: templates, chart service, CSV builders.</summary>
internal static class ChartTestHelpers
{
    public static ITemplateService Templates(this AccountingHarness h) => h.Get<ITemplateService>();
    public static IChartOfAccountsService Chart(this AccountingHarness h) => h.Get<IChartOfAccountsService>();

    /// <summary>Runs the real seeder over the embedded Templates/*.json.</summary>
    public static Task SeedTemplatesAsync(this AccountingHarness h) => h.Get<ITemplateSeeder>().SeedAsync();

    public static async Task<Guid> TemplateUuidAsync(this AccountingHarness h, string code) =>
        (await h.Db.CoaTemplates.IgnoreQueryFilters().SingleAsync(t => t.TemplateCode == code && t.IsGlobal)).Uuid;

    /// <summary>Seeds the templates and applies one (US_GENERAL by default).</summary>
    public static async Task ApplyAsync(this AccountingHarness h, string code = "US_GENERAL")
    {
        await h.SeedTemplatesAsync();
        await h.Chart().ApplyTemplateAsync(h.Admin(), await h.TemplateUuidAsync(code));
    }

    public static async Task<ChartOfAccount> AccountAsync(this AccountingHarness h, string code, Guid? org = null) =>
        await h.Db.ChartOfAccounts.IgnoreQueryFilters().SingleAsync(a => a.OrganizationId == (org ?? h.Org) && a.AccountCode == code && a.IsActive);

    public const string CsvHeader = "account_code,account_name,parent_code,level,root_type,is_group,account_type,description";

    /// <summary>A valid chart: per root one L2 and one L3 group, then <paramref name="leavesPerRoot"/> leaves (15 + 5×n accounts).</summary>
    public static List<string> ValidCsvLines(int leavesPerRoot = 7)
    {
        var types = new Dictionary<string, string>
        {
            [RootTypes.Asset] = AccountTypes.OtherCurrentAsset, [RootTypes.Liability] = AccountTypes.OtherCurrentLiability,
            [RootTypes.Equity] = AccountTypes.Equity, [RootTypes.Revenue] = AccountTypes.Revenue, [RootTypes.Expense] = AccountTypes.Expense,
        };
        var lines = new List<string> { CsvHeader };
        foreach (var root in RootTypes.All)
        {
            var d = RootTypes.RootCode[root];
            lines.Add($"{d},{SystemAccounts.RootNames[root]},,1,{root},true,,");
            lines.Add($"{d}1,{root} group,{d},2,{root},true,,");
            lines.Add($"{d}11,{root} sub-group,{d}1,3,{root},true,,");
            for (var i = 1; i <= leavesPerRoot; i++)
                lines.Add($"{d}11{i:D2},{root} account {i},{d}11,4,{root},false,{types[root]},");
        }
        return lines;
    }

    public static byte[] Bytes(IEnumerable<string> lines) => Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n");
}

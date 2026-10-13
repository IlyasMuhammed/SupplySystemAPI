using System.Text;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Services;

/// <summary>A42 API-CONTRACT "Templates" — system templates plus the caller's own (D-5, D-33). System templates are read-only (BR-TPL-001).</summary>
public interface ITemplateService
{
    Task<List<CoaTemplateModel>> ListAsync(AccountingCaller caller, string? countryCode, string? businessType, string? search, CancellationToken ct = default);
    Task<TemplateFiltersModel> FiltersAsync(AccountingCaller caller, CancellationToken ct = default);
    Task<CoaTemplateDetailModel> GetAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default);
    Task<List<TemplateItemModel>> PreviewAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default);
    Task<GlFile> DownloadAsync(AccountingCaller caller, Guid uuid, string? format, CancellationToken ct = default);
    GlFile Blank(string? format);
    /// <summary>Deactivates one of the organization's own templates; a system template → 403 (BR-TPL-001, T-TPL-04).</summary>
    Task DeleteAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default);
}

internal sealed class TemplateService : ITemplateService
{
    internal const string ReadOnlyMessage = "System templates are read-only.";
    internal const string NotFoundMessage = "Template not found.";

    /// <summary>Display names of the seeded templates' countries (ISO 3166-1 alpha-3).</summary>
    internal static readonly IReadOnlyDictionary<string, string> CountryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["USA"] = "United States", ["GBR"] = "United Kingdom", ["CAN"] = "Canada", ["AUS"] = "Australia", ["DEU"] = "Germany",
        ["ARE"] = "United Arab Emirates", ["SAU"] = "Saudi Arabia", ["IND"] = "India", ["PAK"] = "Pakistan",
    };

    private readonly AccountingDbContext _db;

    public TemplateService(AccountingDbContext db) => _db = db;

    /// <summary>Active system templates and the organization's own (explicit — a super admin bypasses the filter).</summary>
    private IQueryable<CoaTemplate> Visible(Guid org) =>
        _db.CoaTemplates.IgnoreQueryFilters().Where(t => t.IsActive && (t.IsGlobal || t.OrganizationId == org));

    private static CoaTemplateModel ToModel(CoaTemplate t) => new()
    {
        Uuid = t.Uuid, TemplateCode = t.TemplateCode, TemplateName = t.TemplateName, CountryCode = t.CountryCode, BusinessType = t.BusinessType,
        CurrencyCode = t.CurrencyCode, Description = t.Description, AccountCount = t.AccountCount, Version = t.Version, IsSystem = t.IsGlobal,
    };

    public async Task<List<CoaTemplateModel>> ListAsync(AccountingCaller caller, string? countryCode, string? businessType, string? search, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var q = Visible(caller.OrganizationId).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            var c = countryCode.Trim();
            // "GLOBAL" (or "none") = country-agnostic templates.
            q = c.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase) || c.Equals("none", StringComparison.OrdinalIgnoreCase)
                ? q.Where(t => t.CountryCode == null)
                : q.Where(t => t.CountryCode == c);
        }
        if (!string.IsNullOrWhiteSpace(businessType)) q = q.Where(t => t.BusinessType == businessType.Trim());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(t => t.TemplateName.Contains(s) || t.TemplateCode.Contains(s) || (t.Description != null && t.Description.Contains(s)));
        }
        var list = await q.OrderBy(t => t.IsGlobal ? 0 : 1).ThenBy(t => t.SortOrder).ThenBy(t => t.TemplateName).ToListAsync(ct);
        return list.Select(ToModel).ToList();
    }

    public async Task<TemplateFiltersModel> FiltersAsync(AccountingCaller caller, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var rows = await Visible(caller.OrganizationId).AsNoTracking()
            .Select(t => new { t.CountryCode, t.BusinessType }).ToListAsync(ct);
        return new TemplateFiltersModel
        {
            Countries = rows.Where(r => r.CountryCode != null).Select(r => r.CountryCode!).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(c => new TemplateFilterOption { Code = c, Name = CountryNames.GetValueOrDefault(c, c) })
                .OrderBy(c => c.Name).ToList(),
            BusinessTypes = rows.Where(r => r.BusinessType != null).Select(r => r.BusinessType!).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(b => b).ToList(),
        };
    }

    private async Task<(CoaTemplate Template, List<CoaRow> Rows)> LoadAsync(Guid org, Guid uuid, CancellationToken ct)
    {
        var template = await Visible(org).AsNoTracking().FirstOrDefaultAsync(t => t.Uuid == uuid, ct)
            ?? throw new NotFoundException(NotFoundMessage);
        var items = await _db.CoaTemplateItems.AsNoTracking().Where(i => i.TemplateId == template.Id).ToListAsync(ct);
        return (template, TemplateRows(items));
    }

    /// <summary>A template's items as file rows, depth-first.</summary>
    internal static List<CoaRow> TemplateRows(IReadOnlyList<CoaTemplateItem> items)
    {
        var codeById = items.ToDictionary(i => i.Id, i => i.AccountCode);
        var rows = items.Select(i => new CoaRow
        {
            Code = i.AccountCode, Name = i.AccountName, ParentCode = i.ParentItemId is { } p ? codeById.GetValueOrDefault(p) : null,
            Level = i.Level, RootType = i.RootType, IsGroup = i.IsGroup, AccountType = i.AccountType, Description = i.Description,
            IsDefaultAccount = i.IsDefaultAccount, DefaultAccountKey = i.DefaultAccountKey, SortOrder = i.SortOrder,
        }).ToList();
        return CoaFile.DepthFirst(rows, r => r.Code, r => r.ParentCode, r => r.SortOrder);
    }

    internal static List<AccountNodeModel> Tree(IReadOnlyList<CoaRow> depthFirst)
    {
        var nodes = new Dictionary<string, AccountNodeModel>(StringComparer.Ordinal);
        var roots = new List<AccountNodeModel>();
        foreach (var r in depthFirst)
        {
            var node = new AccountNodeModel
            {
                Code = r.Code, Name = r.Name, RootType = r.RootType, Level = r.Level ?? 0, IsGroup = r.IsGroup == true,
                AccountType = r.AccountType, Description = r.Description,
            };
            nodes[r.Code] = node;
            if (r.ParentCode is not null && nodes.TryGetValue(r.ParentCode, out var parent)) parent.Children.Add(node);
            else roots.Add(node);
        }
        return roots;
    }

    public async Task<CoaTemplateDetailModel> GetAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var (t, rows) = await LoadAsync(caller.OrganizationId, uuid, ct);
        return new CoaTemplateDetailModel
        {
            Uuid = t.Uuid, TemplateCode = t.TemplateCode, TemplateName = t.TemplateName, CountryCode = t.CountryCode, BusinessType = t.BusinessType,
            CurrencyCode = t.CurrencyCode, Description = t.Description, AccountCount = t.AccountCount, Version = t.Version, IsSystem = t.IsGlobal,
            Accounts = Tree(rows),
        };
    }

    public async Task<List<TemplateItemModel>> PreviewAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var (_, rows) = await LoadAsync(caller.OrganizationId, uuid, ct);
        return rows.Select(r => new TemplateItemModel
        {
            AccountCode = r.Code, AccountName = r.Name, ParentCode = r.ParentCode, Level = r.Level ?? 0, RootType = r.RootType, IsGroup = r.IsGroup == true,
            AccountType = r.AccountType, Description = r.Description, IsDefaultAccount = r.IsDefaultAccount, DefaultAccountKey = r.DefaultAccountKey,
        }).ToList();
    }

    public async Task<GlFile> DownloadAsync(AccountingCaller caller, Guid uuid, string? format, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var f = CoaFile.NormalizeFormat(format);
        var (t, rows) = await LoadAsync(caller.OrganizationId, uuid, ct);
        return f == CoaFile.Csv
            ? new GlFile(CoaFile.WriteCsv(rows), $"{t.TemplateCode}.csv", "text/csv")
            : new GlFile(CoaFile.WriteJson(new CoaFile.JsonHeader(t.TemplateCode, t.TemplateName, t.CountryCode, t.CurrencyCode, t.Version), rows),
                $"{t.TemplateCode}.json", "application/json");
    }

    public GlFile Blank(string? format)
    {
        var f = CoaFile.NormalizeFormat(format);
        var text = TemplateSeeder.ReadEmbedded($"blank-coa-template.{f}")
            ?? (f == CoaFile.Csv ? string.Join(',', CoaFile.CsvColumns) + "\r\n" : "{ \"accounts\": [] }");
        var bytes = f == CoaFile.Csv
            ? Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text.TrimStart('﻿'))).ToArray()
            : Encoding.UTF8.GetBytes(text.TrimStart('﻿'));
        return new GlFile(bytes, $"coa-template.{f}", f == CoaFile.Csv ? "text/csv" : "application/json");
    }

    public async Task DeleteAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        var t = await Visible(caller.OrganizationId).FirstOrDefaultAsync(x => x.Uuid == uuid, ct) ?? throw new NotFoundException(NotFoundMessage);
        if (t.IsGlobal) throw new ForbiddenException(ReadOnlyMessage);
        t.IsActive = false;
        t.ModifiedBy = caller.UserIdOrNull;
        await _db.SaveChangesAsync(ct);
    }
}

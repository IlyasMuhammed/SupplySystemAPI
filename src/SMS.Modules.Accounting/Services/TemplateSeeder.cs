using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// D-5 / D-6 — seeds the system chart of accounts templates from the embedded <c>Templates/templates.json</c> (headers)
/// and each header's <c>itemsFile</c> (the §5.4 tree). Idempotent, runs at every API start after the migration:
/// headers are upserted by TemplateCode (system rows only); a template's items are replaced when the file's version differs
/// from the stored one, or the template has no items yet while its file has some (D-39); AccountCount = the stored items.
/// An invalid items file is logged and skipped (the header stays, with its previous items).
/// </summary>
public interface ITemplateSeeder
{
    Task SeedAsync(CancellationToken ct = default);
}

internal sealed class TemplateSeeder : ITemplateSeeder
{
    internal const string ResourcePrefix = "SMS.Modules.Accounting.Templates.";

    internal sealed class HeaderEntry
    {
        [JsonPropertyName("templateCode")] public string TemplateCode { get; set; } = string.Empty;
        [JsonPropertyName("templateName")] public string TemplateName { get; set; } = string.Empty;
        [JsonPropertyName("countryCode")]  public string? CountryCode { get; set; }
        [JsonPropertyName("businessType")] public string? BusinessType { get; set; }
        [JsonPropertyName("currencyCode")] public string? CurrencyCode { get; set; }
        [JsonPropertyName("description")]  public string? Description { get; set; }
        [JsonPropertyName("version")]      public string? Version { get; set; }
        [JsonPropertyName("itemsFile")]    public string? ItemsFile { get; set; }
    }

    private readonly AccountingDbContext _db;
    private readonly ILogger<TemplateSeeder>? _log;
    private readonly Func<string, string?> _readResource;

    public TemplateSeeder(AccountingDbContext db, ILogger<TemplateSeeder>? log = null) : this(db, log, ReadEmbedded) { }

    /// <summary>Tests pass their own file source.</summary>
    internal TemplateSeeder(AccountingDbContext db, ILogger<TemplateSeeder>? log, Func<string, string?> readResource)
    {
        _db = db;
        _log = log;
        _readResource = readResource;
    }

    internal static string? ReadEmbedded(string fileName)
    {
        using var stream = typeof(TemplateSeeder).Assembly.GetManifestResourceStream(ResourcePrefix + fileName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var headersJson = _readResource("templates.json") ?? throw new InvalidOperationException("Embedded Templates/templates.json is missing.");
        var headers = JsonSerializer.Deserialize<List<HeaderEntry>>(headersJson) ?? [];

        var existing = await _db.CoaTemplates.IgnoreQueryFilters()
            .Where(t => t.IsGlobal && t.OrganizationId == null).ToListAsync(ct);

        var order = 0;
        foreach (var h in headers)
        {
            order += 10;
            var template = existing.FirstOrDefault(t => t.TemplateCode == h.TemplateCode);
            var isNew = template is null;
            if (template is null)
            {
                template = new CoaTemplate { TemplateCode = h.TemplateCode, IsGlobal = true, OrganizationId = null, Version = "" };
                _db.CoaTemplates.Add(template);
                existing.Add(template);
            }
            Set(template, t => t.TemplateName, h.TemplateName);
            Set(template, t => t.CountryCode, h.CountryCode);
            Set(template, t => t.BusinessType, h.BusinessType);
            Set(template, t => t.CurrencyCode, h.CurrencyCode);
            Set(template, t => t.Description, h.Description);
            if (template.SortOrder != order) template.SortOrder = order;
            if (!template.IsActive) template.IsActive = true;
            await _db.SaveChangesAsync(ct);

            var version = h.Version ?? "1.0";
            var storedItems = isNew ? 0 : await _db.CoaTemplateItems.CountAsync(i => i.TemplateId == template.Id, ct);
            List<CoaRow>? rows = null;
            if (h.ItemsFile is { Length: > 0 } file)
            {
                var text = _readResource(file);
                if (text is null) _log?.LogError("Template {Code}: items file {File} is not embedded.", h.TemplateCode, file);
                else
                {
                    var parsed = CoaFile.ParseJson(text, out var fileHeader);
                    var errors = parsed.Errors.Concat(CoaValidator.Validate(parsed.Rows)).ToList();
                    if (errors.Count > 0)
                        _log?.LogError("Template {Code}: {File} is invalid and was not seeded: {Errors}", h.TemplateCode, file,
                            string.Join(" | ", errors.Take(20).Select(e => $"{e.Rule} {e.Message}")));
                    else
                    {
                        rows = parsed.Rows;
                        version = fileHeader?.Version ?? version;
                    }
                }
            }

            if (rows is not null && (template.Version != version || (storedItems == 0 && rows.Count > 0)))
            {
                await ReplaceItemsAsync(template, rows, ct);
                storedItems = rows.Count;
                _log?.LogInformation("Template {Code}: {Count} accounts seeded (version {Version}).", h.TemplateCode, rows.Count, version);
            }
            if (rows is not null || template.Version.Length == 0) template.Version = version;
            if (template.AccountCount != storedItems) template.AccountCount = storedItems;
            if (_db.ChangeTracker.HasChanges()) await _db.SaveChangesAsync(ct);
        }
    }

    private static void Set<T>(CoaTemplate t, System.Linq.Expressions.Expression<Func<CoaTemplate, T>> property, T value)
    {
        var info = (PropertyInfo)((System.Linq.Expressions.MemberExpression)property.Body).Member;
        if (!Equals(info.GetValue(t), value)) info.SetValue(t, value);
    }

    private async Task ReplaceItemsAsync(CoaTemplate template, List<CoaRow> rows, CancellationToken ct)
    {
        await _db.InTransactionAsync(async () =>
        {
            var old = _db.CoaTemplateItems.Where(i => i.TemplateId == template.Id);
            if (_db.Database.IsRelational()) await old.ExecuteDeleteAsync(ct);
            else
            {
                _db.CoaTemplateItems.RemoveRange(await old.ToListAsync(ct));
                await _db.SaveChangesAsync(ct);
            }

            var byCode = new Dictionary<string, CoaTemplateItem>(StringComparer.Ordinal);
            foreach (var r in rows.OrderBy(r => r.Level))
            {
                var item = new CoaTemplateItem
                {
                    TemplateId = template.Id,
                    AccountCode = r.Code,
                    AccountName = r.Name,
                    Parent = r.ParentCode is null ? null : byCode[r.ParentCode],
                    Level = (byte)r.Level!.Value,
                    RootType = r.RootType,
                    IsGroup = r.IsGroup == true,
                    AccountType = r.AccountType,
                    Description = r.Description,
                    SortOrder = r.SortOrder,
                    IsDefaultAccount = r.IsDefaultAccount,
                    DefaultAccountKey = r.IsDefaultAccount ? r.DefaultAccountKey : null,
                };
                byCode[r.Code] = item;
                _db.CoaTemplateItems.Add(item);
            }
            await _db.SaveChangesAsync(ct);
        }, ct);
    }
}

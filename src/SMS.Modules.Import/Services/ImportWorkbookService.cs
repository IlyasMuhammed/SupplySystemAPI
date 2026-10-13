using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Import.Data;
using SMS.Modules.Import.Domain;
using SMS.Modules.Import.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Import.Services;

public interface IImportWorkbookService
{
    /// <summary>C5 — Data (header, help, sample rows, validations) / Instructions / hidden Lookups.</summary>
    Task<ImportFile> TemplateAsync(string entityType, CancellationToken ct = default);
    /// <summary>C5 — one sheet per profile in import order ("1 - Units" …).</summary>
    Task<ImportFile> MigrationWorkbookAsync(CancellationToken ct = default);
    /// <summary>Failed rows with their original columns + Status + Error Details (red), and a summary by code.</summary>
    Task<ImportFile> ErrorReportAsync(Guid uuid, ImportCaller caller, CancellationToken ct = default);
    /// <summary>Every row with its outcome and the created/updated record's uuid.</summary>
    Task<ImportFile> ResultsReportAsync(Guid uuid, ImportCaller caller, CancellationToken ct = default);
}

internal sealed class ImportWorkbookService : IImportWorkbookService
{
    /// <summary>Rows of the Data sheet that carry the dropdown / type validations.</summary>
    internal const int ValidatedRows = 10_000;
    internal const string DataSheet = ImportFileParser.TemplateDataSheet, InstructionsSheet = "Instructions", LookupsSheet = "Lookups";
    internal static readonly XLColor HelpFont = XLColor.Gray, SampleFill = XLColor.LightBlue, RequiredFont = XLColor.Red,
        FailedFill = XLColor.FromHtml("#F8D7DA");

    private readonly ImportDbContext _db;
    private readonly ImportTargetRegistry _registry;
    private readonly ITenantContext _tenant;
    private readonly ImportSessionService _sessions;
    private readonly ILogger _log;

    public ImportWorkbookService(ImportDbContext db, ImportTargetRegistry registry, ITenantContext tenant, ImportSessionService sessions,
        ILogger<ImportWorkbookService>? log = null)
    {
        _db = db; _registry = registry; _tenant = tenant; _sessions = sessions;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    // ── Template ─────────────────────────────────────────────────────────────

    public async Task<ImportFile> TemplateAsync(string entityType, CancellationToken ct = default)
    {
        var entry = _registry.Get(entityType);
        IReadOnlyList<ImportTemplateList> lists;
        try { lists = await entry.Target.GetTemplateListsAsync(_tenant.OrganizationId, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Import template {EntityType}: the target's lists failed; the template has no dropdowns.", entry.Definition.EntityType);
            lists = [];
        }

        using var wb = new XLWorkbook();
        var data = wb.AddWorksheet(DataSheet);
        var instructions = wb.AddWorksheet(InstructionsSheet);
        var lookups = wb.AddWorksheet(LookupsSheet);
        lookups.Visibility = XLWorksheetVisibility.Hidden;

        var fields = entry.Fields;
        var help = fields.Select(HelpText).ToList();
        var sample = fields.Select(f => f.SampleValue ?? string.Empty).ToList();
        for (var c = 0; c < fields.Count; c++)
        {
            var f = fields[c];
            var header = data.Cell(1, c + 1);
            header.Value = f.DisplayName;
            header.Style.Font.Bold = true;
            if (f.IsRequired) header.Style.Font.FontColor = RequiredFont;
            if (f.FieldName.Equals(ImportLimits.ExternalIdField, StringComparison.OrdinalIgnoreCase))
                header.CreateComment().AddText(ImportTargetRegistry.ExternalIdField.Description!);

            var helpCell = data.Cell(2, c + 1);
            helpCell.Value = help[c];
            helpCell.Style.Font.Italic = true;
            helpCell.Style.Font.FontColor = HelpFont;
            helpCell.Style.Alignment.WrapText = true;

            var sampleCell = data.Cell(3, c + 1);
            sampleCell.Value = sample[c];
            sampleCell.Style.Fill.BackgroundColor = SampleFill;
        }
        data.SheetView.FreezeRows(1);
        data.Columns(1, Math.Max(1, fields.Count)).Width = 22;

        // Hidden Lookups sheet: one column per template list, a defined name over its values, dropdowns from it.
        var listRanges = new Dictionary<string, IXLRange>(StringComparer.OrdinalIgnoreCase);
        var col = 1;
        foreach (var list in lists.Where(l => l.Values.Count > 0))
        {
            lookups.Cell(1, col).Value = list.Key;
            var values = list.Values.Take(1000).ToList();
            for (var i = 0; i < values.Count; i++) lookups.Cell(i + 2, col).Value = values[i];
            var range = lookups.Range(2, col, values.Count + 1, col);
            wb.DefinedNames.Add(ListName(list.Key), range);
            listRanges[list.Key] = range;
            col++;
        }

        for (var c = 0; c < fields.Count; c++)
        {
            var f = fields[c];
            var target = data.Range(2, c + 1, ValidatedRows + 1, c + 1);
            if (f.FieldType == ImportFieldTypes.Lookup && f.LookupList is { } key && listRanges.TryGetValue(key, out var source))
            {
                var dv = target.CreateDataValidation();
                dv.List(source, true);
                dv.ShowErrorMessage = false; // the importer reports unknown codes; Excel only suggests
            }
            else if (f.FieldType == ImportFieldTypes.Boolean)
            {
                var dv = target.CreateDataValidation();
                dv.List("\"Yes,No\"", true);
            }
            else if (f.FieldType == ImportFieldTypes.Date)
            {
                var dv = target.CreateDataValidation();
                dv.Date.EqualOrGreaterThan(new DateTime(1900, 1, 1));
                dv.ErrorMessage = $"{f.DisplayName} must be a date.";
                dv.ShowErrorMessage = true;
            }
            else if (f.FieldType == ImportFieldTypes.Decimal)
            {
                var dv = target.CreateDataValidation();
                dv.Decimal.Between((double)(f.MinValue ?? -1_000_000_000_000m), (double)(f.MaxValue ?? 1_000_000_000_000m));
                dv.ErrorMessage = $"{f.DisplayName} must be a number.";
                dv.ShowErrorMessage = true;
            }
            else if (f.FieldType == ImportFieldTypes.Integer)
            {
                var dv = target.CreateDataValidation();
                dv.WholeNumber.Between((int)Math.Clamp(f.MinValue ?? -1_000_000_000m, int.MinValue, int.MaxValue),
                    (int)Math.Clamp(f.MaxValue ?? 1_000_000_000m, int.MinValue, int.MaxValue));
                dv.ErrorMessage = $"{f.DisplayName} must be a whole number.";
                dv.ShowErrorMessage = true;
            }
        }

        WriteInstructions(instructions, entry, lists);

        // The parser skips the help and sample rows on upload while they still hold exactly this content.
        wb.CustomProperties.Add(ImportFileParser.TemplateMarker, entry.Definition.EntityType);
        wb.CustomProperties.Add(ImportFileParser.HelpHashProperty, ImportFileParser.RowHash(help));
        wb.CustomProperties.Add(ImportFileParser.SampleHashProperty, ImportFileParser.RowHash(sample));

        return new ImportFile(Save(wb), $"{entry.Definition.EntityType.ToLowerInvariant()}-import-template.xlsx");
    }

    internal static string ListName(string key) =>
        "List_" + new string(key.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    internal static string HelpText(ImportFieldDefinition f)
    {
        var parts = new List<string> { f.IsRequired ? "Required." : "Optional." };
        parts.Add(f.FieldType switch
        {
            ImportFieldTypes.Date     => "Date (yyyy-MM-dd).",
            ImportFieldTypes.Boolean  => "Yes or No.",
            ImportFieldTypes.Integer  => "Whole number.",
            ImportFieldTypes.Decimal  => f.DecimalPlaces is { } p ? $"Number, up to {p} decimals." : "Number.",
            ImportFieldTypes.Currency => "3-letter currency code (e.g. USD).",
            ImportFieldTypes.Lookup   => "Pick from the list.",
            ImportFieldTypes.Email    => "Email address.",
            ImportFieldTypes.Phone    => "Phone number.",
            _                         => f.MaxLength is { } m ? $"Text, up to {m} characters." : "Text."
        });
        if (!string.IsNullOrWhiteSpace(f.Description) && !f.FieldName.Equals(ImportLimits.ExternalIdField, StringComparison.OrdinalIgnoreCase))
            parts.Add(f.Description.Trim());
        return string.Join(" ", parts);
    }

    private static void WriteInstructions(IXLWorksheet ws, ImportTargetEntry entry, IReadOnlyList<ImportTemplateList> lists)
    {
        var def = entry.Definition;
        ws.Cell(1, 1).Value = $"{def.Name} import";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        var lines = new[]
        {
            def.Description ?? string.Empty,
            "Fill one record per row on the Data sheet, starting below the blue sample row (you may overwrite or delete the grey help row and the blue sample row).",
            "Headers in red are required. Do not rename the headers; extra columns are ignored unless you map them.",
            "External ID (first column) is optional: your previous system's id. Importing the same External ID again updates the record it created.",
            $"Records are matched to existing ones by: {string.Join(", ", def.MatchFields.Select(m => entry.Field(m)?.DisplayName ?? m))}.",
            "Dates: yyyy-MM-dd (also MM-dd-yyyy, dd-MMM-yyyy or an Excel date). Yes/No columns accept Yes, No, Y, N, 1, 0, True, False.",
        };
        for (var i = 0; i < lines.Length; i++) ws.Cell(i + 2, 1).Value = lines[i];

        var top = lines.Length + 3;
        string[] headers = ["Column", "Field name", "Required", "Type", "Max length", "Min", "Max", "Decimals", "Allowed values", "Description", "Example"];
        for (var c = 0; c < headers.Length; c++)
        {
            ws.Cell(top, c + 1).Value = headers[c];
            ws.Cell(top, c + 1).Style.Font.Bold = true;
        }
        var listByKey = lists.ToDictionary(l => l.Key, StringComparer.OrdinalIgnoreCase);
        var r = top + 1;
        foreach (var f in entry.Fields)
        {
            ws.Cell(r, 1).Value = f.DisplayName;
            ws.Cell(r, 2).Value = f.FieldName;
            ws.Cell(r, 3).Value = f.IsRequired ? "Yes" : "No";
            if (f.IsRequired) ws.Cell(r, 1).Style.Font.FontColor = RequiredFont;
            ws.Cell(r, 4).Value = f.FieldType;
            if (f.MaxLength is { } ml) ws.Cell(r, 5).Value = ml;
            if (f.MinValue is { } min) ws.Cell(r, 6).Value = min;
            if (f.MaxValue is { } max) ws.Cell(r, 7).Value = max;
            if (f.DecimalPlaces is { } dp) ws.Cell(r, 8).Value = dp;
            if (f.FieldType == ImportFieldTypes.Boolean) ws.Cell(r, 9).Value = "Yes / No";
            else if (f.LookupList is { } key)
                ws.Cell(r, 9).Value = listByKey.TryGetValue(key, out var l) && l.Values.Count > 0
                    ? string.Join(", ", l.Values.Take(20)) + (l.Values.Count > 20 ? $" … ({l.Values.Count})" : string.Empty)
                    : $"Codes from {key}";
            ws.Cell(r, 10).Value = f.Description ?? string.Empty;
            ws.Cell(r, 11).Value = f.SampleValue ?? string.Empty;
            r++;
        }
        ws.Column(1).Width = 28;
        ws.Columns(2, 11).Width = 18;
        ws.Column(10).Width = 60;
    }

    // ── Migration workbook ───────────────────────────────────────────────────

    public Task<ImportFile> MigrationWorkbookAsync(CancellationToken ct = default)
    {
        using var wb = new XLWorkbook();
        var position = 1;
        foreach (var entry in _registry.All)
        {
            var ws = wb.AddWorksheet(SheetName(position, entry.Definition.Name));
            for (var c = 0; c < entry.Fields.Count; c++)
            {
                var f = entry.Fields[c];
                var cell = ws.Cell(1, c + 1);
                cell.Value = f.DisplayName;
                cell.Style.Font.Bold = true;
                if (f.IsRequired) cell.Style.Font.FontColor = RequiredFont;
                cell.CreateComment().AddText(HelpText(f));
            }
            ws.SheetView.FreezeRows(1);
            ws.Columns(1, Math.Max(1, entry.Fields.Count)).Width = 22;
            position++;
        }
        if (position == 1) wb.AddWorksheet("No imports");
        return Task.FromResult(new ImportFile(Save(wb), "data-migration-workbook.xlsx"));
    }

    internal static string SheetName(int position, string name)
    {
        var clean = new string($"{position} - {name}".Where(c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).ToArray());
        return clean.Length <= 31 ? clean : clean[..31];
    }

    // ── Reports ──────────────────────────────────────────────────────────────

    public async Task<ImportFile> ErrorReportAsync(Guid uuid, ImportCaller caller, CancellationToken ct = default)
    {
        var session = await _sessions.LoadAsync(uuid, caller, false, ct);
        var headers = JsonSerializer.Deserialize<List<string>>(session.HeadersJson, ImportSessionService.Json) ?? [];
        var rows = await _db.Rows.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.SessionId == session.Id && (r.Status == ImportRowStatus.Invalid || r.Status == ImportRowStatus.Error))
            .OrderBy(r => r.RowNumber).ToListAsync(ct);

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Errors");
        WriteHeader(ws, headers.Concat(["Row", "Status", "Error Details"]).ToList());
        var r = 2;
        var counts = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            var raw = JsonSerializer.Deserialize<string?[]>(row.RawJson, ImportSessionService.Json) ?? [];
            for (var c = 0; c < headers.Count; c++) ws.Cell(r, c + 1).Value = c < raw.Length ? raw[c] ?? string.Empty : string.Empty;
            var errors = ImportSessionService.Errors(row);
            foreach (var e in errors) counts[e.Code] = counts.GetValueOrDefault(e.Code) + 1;
            ws.Cell(r, headers.Count + 1).Value = row.RowNumber;
            ws.Cell(r, headers.Count + 2).Value = row.Status;
            ws.Cell(r, headers.Count + 3).Value = string.Join(" ", errors.Select(e => $"[{e.Code}] {e.Message}"));
            ws.Range(r, 1, r, headers.Count + 3).Style.Fill.BackgroundColor = FailedFill;
            r++;
        }
        ws.SheetView.FreezeRows(1);

        var summary = wb.AddWorksheet("Summary");
        WriteHeader(summary, ["Error code", "Count"]);
        var s = 2;
        foreach (var (code, count) in counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key))
        {
            summary.Cell(s, 1).Value = code;
            summary.Cell(s, 2).Value = count;
            s++;
        }
        summary.Cell(s + 1, 1).Value = "Failed rows";
        summary.Cell(s + 1, 2).Value = rows.Count;
        // Re-uploadable: the parser keeps only the original columns of an "Errors" sheet carrying this count.
        if (headers.Count > 0) wb.CustomProperties.Add(ImportFileParser.ReportColumnsProperty, headers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new ImportFile(Save(wb), $"import-errors-{session.Uuid:N}.xlsx");
    }

    public async Task<ImportFile> ResultsReportAsync(Guid uuid, ImportCaller caller, CancellationToken ct = default)
    {
        var session = await _sessions.LoadAsync(uuid, caller, false, ct);
        var headers = JsonSerializer.Deserialize<List<string>>(session.HeadersJson, ImportSessionService.Json) ?? [];
        var rows = await _db.Rows.IgnoreQueryFilters().AsNoTracking().Where(r => r.SessionId == session.Id)
            .OrderBy(r => r.RowNumber).ToListAsync(ct);

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Results");
        WriteHeader(ws, headers.Concat(["Row", "Status", "Record UUID", "Error Details"]).ToList());
        var r = 2;
        foreach (var row in rows)
        {
            var raw = JsonSerializer.Deserialize<string?[]>(row.RawJson, ImportSessionService.Json) ?? [];
            for (var c = 0; c < headers.Count; c++) ws.Cell(r, c + 1).Value = c < raw.Length ? raw[c] ?? string.Empty : string.Empty;
            ws.Cell(r, headers.Count + 1).Value = row.RowNumber;
            ws.Cell(r, headers.Count + 2).Value = row.Status;
            ws.Cell(r, headers.Count + 3).Value = row.EntityUuid?.ToString() ?? string.Empty;
            ws.Cell(r, headers.Count + 4).Value = string.Join(" ", ImportSessionService.Errors(row).Select(e => $"[{e.Code}] {e.Message}"));
            if (row.Status is ImportRowStatus.Invalid or ImportRowStatus.Error)
                ws.Range(r, 1, r, headers.Count + 4).Style.Fill.BackgroundColor = FailedFill;
            r++;
        }
        ws.SheetView.FreezeRows(1);
        return new ImportFile(Save(wb), $"import-results-{session.Uuid:N}.xlsx");
    }

    private static void WriteHeader(IXLWorksheet ws, IReadOnlyList<string> headers)
    {
        for (var c = 0; c < headers.Count; c++)
        {
            ws.Cell(1, c + 1).Value = headers[c];
            ws.Cell(1, c + 1).Style.Font.Bold = true;
        }
    }

    private static byte[] Save(XLWorkbook wb)
    {
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}

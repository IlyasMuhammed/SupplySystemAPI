using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using SMS.Modules.Import.Domain;
using SMS.Modules.Import.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Import.Services;

internal sealed record ParsedRow(int RowNumber, string?[] Values);

internal sealed record ParsedFile(
    string FileType, IReadOnlyList<string> SheetNames, string? SheetName, IReadOnlyList<string> Headers, IReadOnlyList<ParsedRow> Rows);

/// <summary>
/// A40 C2 — parses an uploaded file IN MEMORY (D-10: never written to disk). File rules IMP-FILE-01..05 with the
/// contract's exact messages: type, size, rows, header row, data rows; IMP-FILE-06 sheet choice (first visible sheet
/// unless named). Excel cells keep their typed value (dates → yyyy-MM-dd, numbers invariant); CSV: BOM-aware, "," or
/// ";" (or tab) detected from the header line, quoted fields may hold delimiters and line breaks.
/// </summary>
internal static class ImportFileParser
{
    /// <summary>Workbook custom properties written by the template (help and sample rows are skipped on upload while they
    /// still hold exactly the generated content).</summary>
    internal const string TemplateMarker = "SmsImportTemplate", HelpHashProperty = "SmsImportHelpHash", SampleHashProperty = "SmsImportSampleHash";
    internal const string TemplateDataSheet = "Data";
    /// <summary>On an error report: how many of its columns are the original file's (Row / Status / Error Details follow and
    /// are dropped on re-upload, so "Status" never auto-maps onto an Active field).</summary>
    internal const string ReportColumnsProperty = "SmsImportReportColumns", ErrorReportSheet = "Errors";

    public static string FileTypeOf(string fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".xlsx" => ImportFileTypes.Xlsx,
            ".csv"  => ImportFileTypes.Csv,
            _       => throw new BadRequestException(ImportMessages.FileType)
        };
    }

    public static ParsedFile Parse(Stream content, string fileName, long length, string? sheetName, int maxRows, int maxFileSizeMb)
    {
        var fileType = FileTypeOf(fileName);
        if (length > (long)maxFileSizeMb * 1024 * 1024) throw new BadRequestException(ImportMessages.FileSize(maxFileSizeMb));

        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        if (buffer.Length > (long)maxFileSizeMb * 1024 * 1024) throw new BadRequestException(ImportMessages.FileSize(maxFileSizeMb));
        buffer.Position = 0;

        var parsed = fileType == ImportFileTypes.Xlsx ? ParseXlsx(buffer, sheetName, maxRows) : ParseCsv(buffer, maxRows);
        if (parsed.Rows.Count == 0) throw new BadRequestException(ImportMessages.NoDataRows);
        return parsed;
    }

    // ── XLSX ─────────────────────────────────────────────────────────────────

    private static ParsedFile ParseXlsx(MemoryStream buffer, string? sheetName, int maxRows)
    {
        XLWorkbook workbook;
        try { workbook = new XLWorkbook(buffer); }
        catch (Exception) { throw new BadRequestException(ImportMessages.Unreadable); }

        using (workbook)
        {
            var visible = workbook.Worksheets.Where(w => w.Visibility == XLWorksheetVisibility.Visible).ToList();
            var sheetNames = visible.Select(w => w.Name).ToList();
            IXLWorksheet? sheet;
            if (!string.IsNullOrWhiteSpace(sheetName))
                sheet = workbook.Worksheets.FirstOrDefault(w => w.Name.Equals(sheetName.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? throw new BadRequestException(ImportMessages.SheetNotFound(sheetName.Trim()));
            else
                sheet = visible.FirstOrDefault() ?? workbook.Worksheets.FirstOrDefault();
            if (sheet is null) throw new BadRequestException(ImportMessages.NoHeaders);

            var lastColumn = sheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
            if (ReportColumns(workbook, sheet) is { } original) lastColumn = Math.Min(lastColumn, original);
            var headers = Enumerable.Range(1, lastColumn).Select(c => CellText(sheet.Cell(1, c))?.Trim() ?? string.Empty).ToList();
            if (headers.All(h => h.Length == 0)) throw new BadRequestException(ImportMessages.NoHeaders);
            headers = headers.Select((h, i) => h.Length == 0 ? $"Column {i + 1}" : h).ToList();

            var (helpHash, sampleHash) = TemplateHashes(workbook, sheet);
            var rows = new List<ParsedRow>();
            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
            for (var r = 2; r <= lastRow; r++)
            {
                var values = new string?[lastColumn];
                for (var c = 1; c <= lastColumn; c++)
                {
                    var text = CellText(sheet.Cell(r, c));
                    values[c - 1] = string.IsNullOrWhiteSpace(text) ? null : text;
                }
                if (values.All(v => v is null)) continue;
                if ((r == 2 && helpHash is not null && RowHash(values) == helpHash) ||
                    (r == 3 && sampleHash is not null && RowHash(values) == sampleHash)) continue;
                rows.Add(new ParsedRow(r, values));
                if (rows.Count > maxRows) throw new BadRequestException(ImportMessages.FileRows(maxRows));
            }
            return new ParsedFile(ImportFileTypes.Xlsx, sheetNames, sheet.Name, headers, rows);
        }
    }

    private static (string? Help, string? Sample) TemplateHashes(XLWorkbook workbook, IXLWorksheet sheet)
    {
        if (!sheet.Name.Equals(TemplateDataSheet, StringComparison.OrdinalIgnoreCase)) return (null, null);
        var props = workbook.CustomProperties.ToDictionary(p => p.Name, p => p.Value?.ToString(), StringComparer.OrdinalIgnoreCase);
        if (!props.ContainsKey(TemplateMarker)) return (null, null);
        return (props.GetValueOrDefault(HelpHashProperty), props.GetValueOrDefault(SampleHashProperty));
    }

    private static int? ReportColumns(XLWorkbook workbook, IXLWorksheet sheet)
    {
        if (!sheet.Name.Equals(ErrorReportSheet, StringComparison.OrdinalIgnoreCase)) return null;
        var prop = workbook.CustomProperties.FirstOrDefault(p => p.Name.Equals(ReportColumnsProperty, StringComparison.OrdinalIgnoreCase));
        return prop is not null && int.TryParse(Convert.ToString(prop.Value, CultureInfo.InvariantCulture), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// <summary>Stable hash of a row's text values (trailing empty cells ignored) — matches the template writer.</summary>
    internal static string RowHash(IEnumerable<string?> values)
    {
        var list = values.Select(v => v?.Trim() ?? string.Empty).ToList();
        while (list.Count > 0 && list[^1].Length == 0) list.RemoveAt(list.Count - 1);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001F", list))));
    }

    internal static string? CellText(IXLCell cell)
    {
        var value = cell.Value;
        switch (value.Type)
        {
            case XLDataType.Blank:
                return null;
            case XLDataType.Boolean:
                return value.GetBoolean() ? "true" : "false";
            case XLDataType.Number:
            {
                var n = value.GetNumber();
                return Math.Abs(n) < 7.9e27 ? ((decimal)n).ToString(CultureInfo.InvariantCulture) : n.ToString("R", CultureInfo.InvariantCulture);
            }
            case XLDataType.DateTime:
            {
                var d = value.GetDateTime();
                return d.TimeOfDay == TimeSpan.Zero
                    ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            }
            case XLDataType.TimeSpan:
                return value.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture);
            case XLDataType.Error:
                return value.GetError().ToString();
            default:
                return value.GetText();
        }
    }

    // ── CSV ──────────────────────────────────────────────────────────────────

    private static ParsedFile ParseCsv(MemoryStream buffer, int maxRows)
    {
        string text;
        using (var reader = new StreamReader(buffer, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, leaveOpen: true))
            text = reader.ReadToEnd();
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        var delimiter = DetectDelimiter(text);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            HasHeaderRecord = false,
            BadDataFound = null,
            MissingFieldFound = null,
            DetectColumnCountChanges = false,
            IgnoreBlankLines = true,
            TrimOptions = TrimOptions.None,
        };

        using var csv = new CsvParser(new StringReader(text), config);
        if (!csv.Read()) throw new BadRequestException(ImportMessages.NoHeaders);
        var headers = (csv.Record ?? []).Select(h => h?.Trim() ?? string.Empty).ToList();
        while (headers.Count > 0 && headers[^1].Length == 0) headers.RemoveAt(headers.Count - 1);
        if (headers.Count == 0 || headers.All(h => h.Length == 0)) throw new BadRequestException(ImportMessages.NoHeaders);
        headers = headers.Select((h, i) => h.Length == 0 ? $"Column {i + 1}" : h).ToList();

        var rows = new List<ParsedRow>();
        var recordNumber = 1;
        while (csv.Read())
        {
            recordNumber++;
            var record = csv.Record ?? [];
            var values = new string?[headers.Count];
            for (var i = 0; i < headers.Count; i++)
                values[i] = i < record.Length && !string.IsNullOrWhiteSpace(record[i]) ? record[i] : null;
            if (values.All(v => v is null)) continue;
            rows.Add(new ParsedRow(recordNumber, values));
            if (rows.Count > maxRows) throw new BadRequestException(ImportMessages.FileRows(maxRows));
        }
        return new ParsedFile(ImportFileTypes.Csv, [], null, headers, rows);
    }

    /// <summary>The header line's most frequent of ";", "," and tab outside quotes (ties → ",").</summary>
    internal static string DetectDelimiter(string text)
    {
        int comma = 0, semi = 0, tab = 0;
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && (c == '\n' || c == '\r')) break;
            else if (!quoted && c == ',') comma++;
            else if (!quoted && c == ';') semi++;
            else if (!quoted && c == '\t') tab++;
        }
        if (semi > comma && semi >= tab) return ";";
        if (tab > comma && tab > semi) return "\t";
        return ",";
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using SMS.Modules.Accounting.Models;

namespace SMS.Modules.Accounting.Services;

/// <summary>One account of a CoA file or template (spec §5.3/§5.4, Appendix C), before validation.</summary>
internal sealed class CoaRow
{
    /// <summary>CSV: physical line where the record starts (header = 1).</summary>
    public int?    Line              { get; set; }
    /// <summary>JSON: e.g. accounts[0].children[2].</summary>
    public string? Path              { get; set; }
    public string  Code              { get; set; } = string.Empty;
    public string  Name              { get; set; } = string.Empty;
    public string? ParentCode        { get; set; }
    /// <summary>Null when the level is missing or not a number (<see cref="LevelText"/> keeps what was written).</summary>
    public int?    Level             { get; set; }
    public string? LevelText         { get; set; }
    public string  RootType          { get; set; } = string.Empty;
    public bool?   IsGroup           { get; set; }
    public string? IsGroupText       { get; set; }
    public string? AccountType       { get; set; }
    public string? Description       { get; set; }
    public bool    IsDefaultAccount  { get; set; }
    public string? DefaultAccountKey { get; set; }
    /// <summary>Position among its siblings in the file (display order).</summary>
    public int     SortOrder         { get; set; }

    public UploadError Error(string rule, string message) => new() { Line = Line, Path = Path, Code = Code.Length > 0 ? Code : null, Rule = rule, Message = message };
}

/// <summary>A parsed file: its rows, or the R1 errors that stopped parsing.</summary>
internal sealed record CoaParseResult(List<CoaRow> Rows, List<UploadError> Errors, string Format);

/// <summary>
/// Reads and writes the chart of accounts CSV (§5.3 / C.1, header and column order fixed) and JSON (§5.4 / C.2, nested
/// tree, snake_case) formats. Used by template export, tenant export, the template seeder and uploads.
/// </summary>
internal static class CoaFile
{
    public const string Csv = "csv", Json = "json";
    public static readonly string[] CsvColumns = ["account_code", "account_name", "parent_code", "level", "root_type", "is_group", "account_type", "description"];
    public const string R1Message = "Invalid file format. Upload a CSV or JSON file conforming to the SMS CoA schema.";

    public static string NormalizeFormat(string? format) => (format ?? Csv).Trim().ToLowerInvariant() switch
    {
        Csv => Csv,
        Json => Json,
        _ => throw new SMS.Shared.Exceptions.BadRequestException("Format must be csv or json.")
    };

    // ── CSV ──────────────────────────────────────────────────────────────────

    /// <summary>RFC 4180 field: quoted when it holds a comma, a quote or a line break; quotes doubled.</summary>
    public static string CsvField(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>UTF-8 with BOM (Excel opens it as UTF-8), CRLF line ends.</summary>
    public static byte[] WriteCsv(IEnumerable<CoaRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', CsvColumns)).Append("\r\n");
        foreach (var r in rows)
        {
            sb.Append(CsvField(r.Code)).Append(',')
              .Append(CsvField(r.Name)).Append(',')
              .Append(CsvField(r.ParentCode)).Append(',')
              .Append(r.Level?.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(CsvField(r.RootType)).Append(',')
              .Append(r.IsGroup == true ? "true" : "false").Append(',')
              .Append(CsvField(r.AccountType)).Append(',')
              .Append(CsvField(r.Description)).Append("\r\n");
        }
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    /// <summary>Splits CSV text into records (quoted fields may span lines); each with the line it starts on.</summary>
    internal static List<(int Line, List<string> Fields)> SplitCsv(string text, out string? error)
    {
        error = null;
        var records = new List<(int, List<string>)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var inQuotes = false;
        var fieldStarted = false;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void EndRecord()
        {
            EndField();
            if (!(fields.Count == 1 && fields[0].Length == 0)) records.Add((recordLine, fields));
            fields = [];
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '"' when !fieldStarted && field.Length == 0:
                    inQuotes = true;
                    fieldStarted = true;
                    break;
                case ',':
                    EndField();
                    break;
                case '\r':
                    break;
                case '\n':
                    EndRecord();
                    line++;
                    recordLine = line;
                    break;
                default:
                    field.Append(c);
                    fieldStarted = true;
                    break;
            }
        }
        if (inQuotes) error = $"Unclosed quote in the record starting at line {recordLine}.";
        if (field.Length > 0 || fields.Count > 0) EndRecord();
        return records;
    }

    public static CoaParseResult ParseCsv(string text)
    {
        var errors = new List<UploadError>();
        var records = SplitCsv(text.TrimStart('﻿'), out var quoteError);
        if (quoteError is not null || records.Count == 0)
            return new([], [new UploadError { Line = records.Count == 0 ? 1 : null, Rule = "R1", Message = R1Message + (quoteError is null ? "" : " " + quoteError) }], Csv);

        var header = records[0].Fields.Select(h => h.Trim().ToLowerInvariant()).ToList();
        if (!header.SequenceEqual(CsvColumns))
            return new([], [new UploadError { Line = 1, Rule = "R1",
                Message = $"{R1Message} The first line must be: {string.Join(',', CsvColumns)}" }], Csv);

        var rows = new List<CoaRow>();
        var siblingOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (line, f) in records.Skip(1))
        {
            if (f.Count != CsvColumns.Length)
            {
                errors.Add(new UploadError { Line = line, Rule = "R1", Message = $"Line {line} has {f.Count} columns; {CsvColumns.Length} are required." });
                continue;
            }
            string? Opt(int i) => string.IsNullOrWhiteSpace(f[i]) ? null : f[i].Trim();
            var parent = Opt(2);
            var key = parent ?? "";
            siblingOrder[key] = siblingOrder.GetValueOrDefault(key) + 1;
            rows.Add(new CoaRow
            {
                Line = line,
                Code = f[0].Trim(),
                Name = f[1].Trim(),
                ParentCode = parent,
                LevelText = f[3].Trim(),
                Level = int.TryParse(f[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) ? level : null,
                RootType = f[4].Trim(),
                IsGroupText = f[5].Trim(),
                IsGroup = ParseBool(f[5]),
                AccountType = Opt(6),
                Description = Opt(7),
                SortOrder = siblingOrder[key] * 10,
            });
        }
        return new(rows, errors, Csv);
    }

    internal static bool? ParseBool(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" => true,
        "false" or "0" or "no" => false,
        _ => null
    };

    // ── JSON ─────────────────────────────────────────────────────────────────

    public sealed record JsonHeader(string? TemplateCode, string? TemplateName, string? CountryCode, string? CurrencyCode, string? Version);

    /// <summary>The nested tree of §5.4 (children omitted on leaves; the default markers only when set).</summary>
    public static byte[] WriteJson(JsonHeader header, IReadOnlyList<CoaRow> rows)
    {
        var children = rows.Where(r => r.ParentCode is not null).ToLookup(r => r.ParentCode!, StringComparer.Ordinal);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("template_code", header.TemplateCode);
            w.WriteString("template_name", header.TemplateName);
            w.WriteString("country_code", header.CountryCode);
            w.WriteString("currency_code", header.CurrencyCode);
            w.WriteString("version", header.Version ?? "1.0");
            w.WriteStartArray("accounts");
            foreach (var root in rows.Where(r => r.ParentCode is null)) WriteNode(w, root, children);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteNode(Utf8JsonWriter w, CoaRow r, ILookup<string, CoaRow> children)
    {
        w.WriteStartObject();
        w.WriteString("code", r.Code);
        w.WriteString("name", r.Name);
        w.WriteString("root_type", r.RootType);
        w.WriteNumber("level", r.Level ?? 0);
        w.WriteBoolean("is_group", r.IsGroup == true);
        if (r.AccountType is not null) w.WriteString("account_type", r.AccountType);
        if (r.Description is not null) w.WriteString("description", r.Description);
        if (r.IsDefaultAccount && r.DefaultAccountKey is not null)
        {
            w.WriteBoolean("is_default_account", true);
            w.WriteString("default_account_key", r.DefaultAccountKey);
        }
        var kids = children[r.Code].ToList();
        if (kids.Count > 0 || r.IsGroup == true)
        {
            w.WriteStartArray("children");
            foreach (var k in kids) WriteNode(w, k, children);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    /// <summary>
    /// The §5.4 tree (a node's parent is the node it is nested in). A top-level node may also name its parent with
    /// <c>parent_code</c>, so a flat list works too. Problems are reported with the node's path.
    /// </summary>
    public static CoaParseResult ParseJson(string text, out JsonHeader? header)
    {
        header = null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text.TrimStart('﻿'), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            return new([], [new UploadError { Line = (int?)ex.LineNumber + 1, Rule = "R1", Message = R1Message }], Json);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
                return new([], [new UploadError { Rule = "R1", Message = R1Message + " The file must be an object with an \"accounts\" array." }], Json);

            header = new JsonHeader(Str(root, "template_code"), Str(root, "template_name"), Str(root, "country_code"), Str(root, "currency_code"), Str(root, "version"));
            var rows = new List<CoaRow>();
            var errors = new List<UploadError>();
            var i = 0;
            foreach (var node in accounts.EnumerateArray())
            {
                Walk(node, $"accounts[{i}]", Str(node, "parent_code"), (i + 1) * 10, rows, errors);
                i++;
            }
            return new(rows, errors, Json);
        }
    }

    private static void Walk(JsonElement node, string path, string? parentCode, int sortOrder, List<CoaRow> rows, List<UploadError> errors)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new UploadError { Path = path, Rule = "R1", Message = $"{path} must be an account object." });
            return;
        }
        var row = new CoaRow
        {
            Path = path,
            Code = Str(node, "code")?.Trim() ?? string.Empty,
            Name = Str(node, "name")?.Trim() ?? string.Empty,
            ParentCode = string.IsNullOrWhiteSpace(parentCode) ? null : parentCode.Trim(),
            RootType = Str(node, "root_type")?.Trim() ?? string.Empty,
            AccountType = NullIfBlank(Str(node, "account_type")),
            Description = NullIfBlank(Str(node, "description")),
            SortOrder = sortOrder,
        };
        if (node.TryGetProperty("level", out var level))
        {
            row.LevelText = level.ToString();
            row.Level = level.ValueKind == JsonValueKind.Number && level.TryGetInt32(out var l) ? l
                : level.ValueKind == JsonValueKind.String && int.TryParse(level.GetString(), out var ls) ? ls : null;
        }
        if (node.TryGetProperty("is_group", out var isGroup))
        {
            row.IsGroupText = isGroup.ToString();
            row.IsGroup = isGroup.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.String => ParseBool(isGroup.GetString()), _ => null };
        }
        if (node.TryGetProperty("is_default_account", out var isDefault) && isDefault.ValueKind == JsonValueKind.True)
        {
            row.IsDefaultAccount = true;
            row.DefaultAccountKey = NullIfBlank(Str(node, "default_account_key"));
        }
        rows.Add(row);

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var child in children.EnumerateArray())
            {
                Walk(child, $"{path}.children[{i}]", row.Code, (i + 1) * 10, rows, errors);
                i++;
            }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Null => null, JsonValueKind.Undefined => null, _ => v.ToString() }
            : null;

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Depth-first order: roots by code, then each parent's children by sort order then code (export and preview).</summary>
    public static List<T> DepthFirst<T>(IReadOnlyList<T> items, Func<T, string> code, Func<T, string?> parentCode, Func<T, int> sortOrder)
    {
        var byParent = items.Where(x => parentCode(x) is not null).ToLookup(x => parentCode(x)!, StringComparer.Ordinal);
        var result = new List<T>(items.Count);
        void Visit(T node)
        {
            result.Add(node);
            foreach (var child in byParent[code(node)].OrderBy(sortOrder).ThenBy(code, StringComparer.Ordinal)) Visit(child);
        }
        var codes = items.Select(code).ToHashSet(StringComparer.Ordinal);
        foreach (var root in items.Where(x => parentCode(x) is null || !codes.Contains(parentCode(x)!)).OrderBy(code, StringComparer.Ordinal)) Visit(root);
        return result;
    }
}

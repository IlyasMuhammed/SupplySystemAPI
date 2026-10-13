using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.SapAlignment;

namespace SMS.Integration.Tests.DataImport;

/// <summary>A downloaded file: status, bytes and the file name the server sent.</summary>
internal sealed record Download(HttpStatusCode Status, byte[] Content, string? FileName, string? ContentType);

/// <summary>
/// A40 (QA) — what <see cref="DataImportE2ETests"/> and <see cref="DataImportOpeningE2ETests"/> need on top of SapKit / Mods:
/// spreadsheets and CSV files built in memory (ClosedXML, as the Import module itself), the multipart upload, the session calls
/// (mappings, validate, execute, cancel, errors, reports) and the reads only SQL can do.
/// </summary>
internal static class Imp
{
    public const string Module = "MODULE_DATA_IMPORT";
    public const string OpeningFeature = "FEATURE_OPENING_BALANCE_IMPORT";

    public static readonly string[] Finished = ["COMPLETED", "COMPLETED_WITH_ERRORS", "FAILED", "CANCELLED"];

    /// <summary>The factory must be on LocalDB — never the Azure database in appsettings.json.</summary>
    public static void ShouldBeLocalDb(this SapWebApplicationFactory f)
    {
        var cs = f.Services.GetRequiredService<IConfiguration>()["Data:mainOrg"];
        cs.Should().Contain("(localdb)", "the E2E tests may only run against LocalDB");
        cs.Should().Contain(f.DatabaseName);
        cs.Should().NotContain("database.windows.net");
    }

    // ── Files ────────────────────────────────────────────────────────────────────

    /// <summary>An .xlsx with one sheet: headers in row 1, then the rows (null = empty cell). Strings stay text.</summary>
    public static byte[] Xlsx(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows, string sheet = "Sheet1",
        Action<XLWorkbook>? more = null)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(sheet);
        for (var c = 0; c < headers.Count; c++) ws.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Count; c++) Set(ws.Cell(r, c + 1), row[c]);
            r++;
        }
        more?.Invoke(wb);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public static void Set(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null: break;
            case string s: cell.Value = s; break;
            case decimal d: cell.Value = d; break;
            case int i: cell.Value = i; break;
            case double x: cell.Value = x; break;
            case DateTime dt: cell.Value = dt; break;
            case bool b: cell.Value = b; break;
            default: cell.Value = value.ToString(); break;
        }
    }

    public static byte[] Csv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        static string Q(string? v) => v is null ? "" : v.Contains(',') || v.Contains('"') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
        var sb = new StringBuilder();
        sb.Append(string.Join(",", headers.Select(Q))).Append("\r\n");
        foreach (var row in rows) sb.Append(string.Join(",", row.Select(Q))).Append("\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static XLWorkbook Open(byte[] content) => new(new MemoryStream(content));

    /// <summary>A sheet's used rows (from row 1) as text, trailing empties trimmed.</summary>
    public static List<List<string>> Rows(IXLWorksheet ws)
    {
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        var result = new List<List<string>>();
        for (var r = 1; r <= lastRow; r++)
            result.Add(Enumerable.Range(1, lastCol).Select(c => ws.Cell(r, c).GetFormattedString()).ToList());
        return result;
    }

    // ── Upload ───────────────────────────────────────────────────────────────────

    public static async Task<Api> Upload(this SapKit k, string entityType, byte[] content, string fileName, string? strategy = null,
        string? sheet = null, string? preset = null, HttpClient? client = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? "text/csv" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(entityType), "entityType");
        if (strategy is not null) form.Add(new StringContent(strategy), "duplicateStrategy");
        if (sheet is not null) form.Add(new StringContent(sheet), "sheetName");
        if (preset is not null) form.Add(new StringContent(preset), "mappingPreset");

        using var resp = await (client ?? k.Admin).PostAsync("/api/import/sessions", form);
        var raw = await resp.Content.ReadAsStringAsync();
        JsonElement parsed;
        try { parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw).RootElement.Clone(); }
        catch (JsonException) { parsed = JsonDocument.Parse("{}").RootElement.Clone(); }
        return new Api(resp.StatusCode, parsed, raw);
    }

    /// <summary>Uploaded (201); returns the SessionDetail.</summary>
    public static async Task<JsonElement> UploadOk(this SapKit k, string entityType, byte[] content, string fileName, string? strategy = null,
        string? sheet = null, string? preset = null, HttpClient? client = null)
    {
        var api = await k.Upload(entityType, content, fileName, strategy, sheet, preset, client);
        api.Status.Should().Be(HttpStatusCode.Created, $"upload {entityType} {fileName} — {api}");
        return api.Result;
    }

    public static Task<Api> Validate(this SapKit k, Guid session, HttpClient? client = null) =>
        k.Post($"/api/import/sessions/{session}/validate", null, client);

    public static Task<JsonElement> ValidateOk(this SapKit k, Guid session, HttpClient? client = null) =>
        k.Ok(k.Validate(session, client), "validate");

    public static Task<Api> Execute(this SapKit k, Guid session, HttpClient? client = null) =>
        k.Post($"/api/import/sessions/{session}/execute", null, client);

    public static Task<JsonElement> ExecuteOk(this SapKit k, Guid session, HttpClient? client = null) =>
        k.Ok(k.Execute(session, client), "execute");

    public static Task<JsonElement> SessionAsync(this SapKit k, Guid session, HttpClient? client = null) =>
        k.Ok(k.Get($"/api/import/sessions/{session}", client), "read session");

    public static Task<Api> PutMappings(this SapKit k, Guid session, params (int Column, string? Field)[] items) =>
        k.Put($"/api/import/sessions/{session}/mappings",
            new { mappings = items.Select(i => new { sourceColumnIndex = i.Column, targetFieldName = i.Field }).ToArray() });

    /// <summary>Upload → validate → execute (inline). Returns (validation result, final session).</summary>
    public static async Task<(JsonElement Validation, JsonElement Session)> ImportAsync(this SapKit k, string entityType, byte[] content,
        string fileName, string? strategy = null, HttpClient? client = null)
    {
        var s = (await k.UploadOk(entityType, content, fileName, strategy, client: client)).G("uuid");
        var v = await k.ValidateOk(s, client);
        if (v.P("session").I("validRows") == 0) return (v, v.P("session"));
        var e = await k.ExecuteOk(s, client);
        e.B("isBackground").Should().BeFalse("a small file runs inline");
        return (v, e.P("session"));
    }

    public static async Task<(JsonElement Validation, JsonElement Session)> ImportXlsxAsync(this SapKit k, string entityType,
        IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows, string? strategy = null) =>
        await k.ImportAsync(entityType, Xlsx(headers, rows), $"{entityType.ToLowerInvariant()}.xlsx", strategy);

    public static async Task<List<JsonElement>> ErrorRowsAsync(this SapKit k, Guid session) =>
        (await k.Ok(k.Get($"/api/import/sessions/{session}/errors?pageSize=200"), "errors")).A("data");

    /// <summary>The validation errors as "row:field:code".</summary>
    public static List<string> Codes(this JsonElement validation) =>
        validation.A("errors").Select(e => $"{e.I("rowNumber")}:{e.S("fieldName")}:{e.S("errorCode")}").ToList();

    public static string Counts(this JsonElement s) =>
        $"{s.S("status")} total={s.I("totalRows")} valid={s.I("validRows")} invalid={s.I("invalidRows")} imported={s.I("importedRows")} " +
        $"updated={s.I("updatedRows")} skipped={s.I("skippedRows")} error={s.I("errorRows")}";

    // ── Downloads ────────────────────────────────────────────────────────────────

    public static async Task<Download> DownloadAsync(this SapKit k, string url, HttpClient? client = null)
    {
        using var resp = await (client ?? k.Admin).GetAsync(url);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return new Download(resp.StatusCode, bytes, resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"'),
            resp.Content.Headers.ContentType?.MediaType);
    }

    public static async Task<byte[]> DownloadOkAsync(this SapKit k, string url, string? expectedName = null)
    {
        var d = await k.DownloadAsync(url);
        d.Status.Should().Be(HttpStatusCode.OK, $"download {url} — {Encoding.UTF8.GetString(d.Content.Take(500).ToArray())}");
        d.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        if (expectedName is not null) d.FileName.Should().Be(expectedName);
        return d.Content;
    }

    // ── Waiting / the host ───────────────────────────────────────────────────────

    public static Task<JsonElement> WaitFinishedAsync(this SapKit k, Guid session, int seconds = 120) =>
        SapKit.WaitForAsync(() => k.SessionAsync(session), s => Finished.Contains(s.S("status")), $"import {session} to finish", seconds);

    /// <summary>The Import module's internal <c>ImportRecoveryJob</c>, run from a fresh scope as Hangfire would.</summary>
    public static async Task<int> RunRecoveryJobAsync(this SapWebApplicationFactory f)
    {
        var type = typeof(SMS.Modules.Import.IImportModule).Assembly.GetType("SMS.Modules.Import.Services.ImportRecoveryJob", throwOnError: true)!;
        await using var scope = f.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService(type);
        return await (Task<int>)type.GetMethod("RunAsync")!.Invoke(job, null)!;
    }

    public static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static Task<Api> SetOpeningDate(this SapKit k, DateTime? date) =>
        k.Put("/api/finance/opening-balance-date", new { date = date is null ? null : Day(date.Value) });

    // ── SQL reads ────────────────────────────────────────────────────────────────

    public static async Task<Dictionary<string, object?>?> RowAsync(this SapWebApplicationFactory f, string sql, params (string, object)[] p) =>
        (await f.QueryAsync(sql, p)).SingleOrDefault();

    public static async Task<int> CountAsync(this SapWebApplicationFactory f, string sql, params (string, object)[] p) =>
        Convert.ToInt32((await f.QueryAsync(sql, p))[0].Values.First());
}

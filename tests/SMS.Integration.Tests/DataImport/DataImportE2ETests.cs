using System.Net;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using FluentAssertions;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.ServiceOrders;
using Xunit;
using static SMS.Integration.Tests.DataImport.Imp;

namespace SMS.Integration.Tests.DataImport;

/// <summary>
/// A40 (QA) — Data Import end to end on the real host (LocalDB, never the Azure database) with the REAL Import, Inventory,
/// Lookups, Suppliers and Finance implementations wired together (the module unit tests used fakes or in-memory stores). Each
/// test runs in an ENTERPRISE organization of its own (MODULE_DATA_IMPORT is core, FEATURE_OPENING_BALANCE_IMPORT from the plan)
/// driven by its org admin (every IMPORT_* permission). Files are built in memory with ClosedXML / plain CSV text.
/// TS-040 scenarios (GL, chart of accounts and employee ones dropped by D-1) → tests:
/// <list type="bullet">
/// <item>File rules IMP-FILE-01..06, org limit overrides — <see cref="Upload_refuses_bad_files_with_the_contract_messages"/>.</item>
/// <item>Auto-mapping tiers, manual remap, IMP-MAP-01..03, presets, preview — <see cref="Auto_mapping_confidence_manual_remap_and_mapping_rules"/>.</item>
/// <item>Templates (help / sample rows skipped), migration workbook, profiles — <see cref="Template_round_trip_and_migration_workbook"/>.</item>
/// <item>Migration wizard order UNIT → … → PRICING_RULE with each target's refusals — <see cref="Migration_wizard_imports_every_master_in_order"/>.</item>
/// <item>SKIP / UPDATE / CREATE_NEW / ERROR on a re-import, UPDATE keeps blanks — <see cref="Duplicate_strategies_on_a_reimport_and_update_keeps_blank_fields"/>.</item>
/// <item>External references — <see cref="External_ids_resolve_to_the_record_of_the_first_import"/>.</item>
/// <item>Error report / results report — <see cref="Error_report_and_results_report_hold_the_right_rows"/>.</item>
/// <item>IMP-EXC-02 batch rollback — <see cref="One_bad_row_rolls_back_its_whole_batch"/>.</item>
/// <item>Tenancy + permissions (D-37) — <see cref="Sessions_are_private_to_the_organization_and_need_import_permissions"/>.</item>
/// </list>
/// Opening balances, the background path and crash recovery: <see cref="DataImportOpeningE2ETests"/>.
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~DataImportE2ETests</c>.</para>
/// </summary>
public sealed class DataImportE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapKit _root;
    private readonly SapWebApplicationFactory _f;

    public DataImportE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeLocalDb();
        _root = new SapKit(factory, "A40");
    }

    private static readonly string[] WhHeaders = ["Code", "Name", "City"];

    private static IReadOnlyList<object?>[] Wh(params (string? Code, string? Name, string? City)[] rows) =>
        rows.Select(r => (IReadOnlyList<object?>)new object?[] { r.Code, r.Name, r.City }).ToArray();

    private async Task<Dictionary<string, object?>?> WarehouseAsync(ModOrg o, string code) =>
        await _f.RowAsync("SELECT Uuid, Code, Name, City, Address, IsActive FROM inventory.Warehouses WHERE OrganizationId = @o AND Code = @c",
            ("@o", o.OrgId), ("@c", code));

    private Task<int> WarehouseCountAsync(ModOrg o, string code) =>
        _f.CountAsync("SELECT COUNT(*) FROM inventory.Warehouses WHERE OrganizationId = @o AND Code = @c", ("@o", o.OrgId), ("@c", code));

    // ── IMP-FILE-01..06 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_refuses_bad_files_with_the_contract_messages()
    {
        var o = await _root.OrgAsync("UPL");
        var k = o.K;
        var good = Csv(WhHeaders, [["W1", "One", "Lahore"]]);

        (await k.Upload("WAREHOUSE", good, "warehouses.txt")).ShouldFail(HttpStatusCode.BadRequest, "IMP-FILE-01",
            "Only .xlsx and .csv files are accepted. Please save the file as .xlsx (Excel 2007+).");
        (await k.Upload("WAREHOUSE", good, "warehouses.xls")).ShouldFail(HttpStatusCode.BadRequest, "old Excel",
            "Only .xlsx and .csv files are accepted.");
        (await k.Upload("WAREHOUSE", Encoding.UTF8.GetBytes("this is not a zip"), "broken.xlsx")).ShouldFail(HttpStatusCode.BadRequest,
            "unreadable", "The file could not be read. Please check that it is a valid .xlsx or .csv file.");
        (await k.Upload("WAREHOUSE", Xlsx(Array.Empty<string>(), [["W1", "One"]]), "noheader.xlsx")).ShouldFail(HttpStatusCode.BadRequest,
            "IMP-FILE-04 (xlsx)", "No column headers detected in the first row.");
        (await k.Upload("WAREHOUSE", Encoding.UTF8.GetBytes(",,\r\nW1,One,Lahore\r\n"), "noheader.csv")).ShouldFail(HttpStatusCode.BadRequest,
            "IMP-FILE-04 (csv)", "No column headers detected in the first row.");
        (await k.Upload("WAREHOUSE", Csv(WhHeaders, []), "empty.csv")).ShouldFail(HttpStatusCode.BadRequest,
            "IMP-FILE-05 (csv)", "The file has no data rows.");
        (await k.Upload("WAREHOUSE", Xlsx(WhHeaders, []), "empty.xlsx")).ShouldFail(HttpStatusCode.BadRequest,
            "IMP-FILE-05 (xlsx)", "The file has no data rows.");
        (await k.Upload("WAREHOUSE", Array.Empty<byte>(), "zero.csv")).ShouldFail(HttpStatusCode.BadRequest,
            "an empty upload", "The file has no data rows.");
        (await k.Upload("WAREHOUSE", Xlsx(WhHeaders, Wh(("W1", "One", null))), "x.xlsx", sheet: "Nope")).ShouldFail(HttpStatusCode.BadRequest,
            "IMP-FILE-06", "Sheet 'Nope' was not found in the workbook.");
        (await k.Upload("NOPE", good, "x.csv")).ShouldFail(HttpStatusCode.NotFound, "unknown entity", "Unknown import type 'NOPE'.");
        (await k.Upload("WAREHOUSE", good, "x.csv", strategy: "MERGE")).ShouldFail(HttpStatusCode.BadRequest, "strategy",
            "Duplicate strategy must be SKIP, UPDATE, CREATE_NEW or ERROR.");

        // IMP-FILE-06 — the named sheet; sheetNames lists the visible ones
        var twoSheets = Xlsx(["Notes"], [["ignore me"]], "Readme", wb =>
        {
            var ws = wb.AddWorksheet("Stores");
            for (var c = 0; c < WhHeaders.Length; c++) ws.Cell(1, c + 1).Value = WhHeaders[c];
            ws.Cell(2, 1).Value = "S1"; ws.Cell(2, 2).Value = "Store one";
            ws.Cell(3, 1).Value = "S2"; ws.Cell(3, 2).Value = "Store two";
            wb.AddWorksheet("Hidden").Visibility = XLWorksheetVisibility.Hidden;
        });
        var first = await k.UploadOk("WAREHOUSE", twoSheets, "two.xlsx");
        (first.S("sheetName"), first.I("totalRows")).Should().Be(("Readme", 1), "no sheet named → the first visible sheet");
        first.Strings("sheetNames").Should().Equal("Readme", "Stores");
        var stores = await k.UploadOk("WAREHOUSE", twoSheets, "two.xlsx", sheet: "stores");
        (stores.S("sheetName"), stores.I("totalRows"), stores.S("fileType"), stores.S("status")).Should().Be(("Stores", 2, "XLSX", "UPLOADED"));

        // org limits (an override may only lower them): 5 rows, 1 MB
        (await k.Put("/api/import/profiles/WAREHOUSE/settings", new { maxRowsPerFile = 60000 })).ShouldFail(HttpStatusCode.BadRequest,
            "limits only go down", "Maximum rows per file must be between 1 and 50000.");
        (await k.Put("/api/import/profiles/WAREHOUSE/settings", new { maxFileSizeMb = 11 })).ShouldFail(HttpStatusCode.BadRequest,
            "limits only go down", "Maximum file size must be between 1 and 10 MB.");
        var limited = await k.Ok(k.Put("/api/import/profiles/WAREHOUSE/settings", new { maxRowsPerFile = 5, maxFileSizeMb = 1 }), "lower limits");
        (limited.I("maxRowsPerFile"), limited.I("maxFileSizeMb")).Should().Be((5, 1));
        (await k.Ok(k.Get("/api/import/profiles/WAREHOUSE"), "profile")).I("maxRowsPerFile").Should().Be(5);

        var five = Enumerable.Range(1, 5).Select(i => (IReadOnlyList<string?>)new string?[] { $"R{i}", $"Row {i}", null }).ToList();
        (await k.UploadOk("WAREHOUSE", Csv(WhHeaders, five), "five.csv")).I("totalRows").Should().Be(5, "exactly the limit is accepted");
        var six = five.Append(["R6", "Row 6", null]).ToList();
        (await k.Upload("WAREHOUSE", Csv(WhHeaders, six), "six.csv")).ShouldFail(HttpStatusCode.BadRequest, "IMP-FILE-03",
            "File exceeds the maximum of 5 rows.");
        var big = Csv(["Code", "Name", "Address"], [["B1", "Big", new string('x', 1_200_000)]]);
        (await k.Upload("WAREHOUSE", big, "big.csv")).ShouldFail(HttpStatusCode.BadRequest, "IMP-FILE-02", "File exceeds the maximum size of 1 MB.");
        // another profile keeps the defaults
        (await k.Ok(k.Get("/api/import/profiles/UNIT"), "unit profile")).I("maxRowsPerFile").Should().Be(50000);
    }

    // ── C6 auto-mapping, IMP-MAP-01..03 ──────────────────────────────────────────

    [Fact]
    public async Task Auto_mapping_confidence_manual_remap_and_mapping_rules()
    {
        var o = await _root.OrgAsync("MAP");
        var k = o.K;
        string[] headers = ["Code", "warehouse name", "Citty", "IsActive", "Ext ID", "Zzz Notes"];
        var s = await k.UploadOk("WAREHOUSE", Csv(headers, [["M1", "Mapped one", "Lahore", "Yes", "OLD-1", "Mall Road 1"]]), "map.csv");
        var uuid = s.G("uuid");

        var m = s.A("mappings").ToDictionary(x => x.I("sourceColumnIndex"));
        (m[0].S("targetFieldName"), m[0].ND("confidenceScore"), m[0].B("isAutoMapped")).Should().Be(("code", 1.00m, true), "exact display name");
        (m[1].S("targetFieldName"), m[1].ND("confidenceScore")).Should().Be(("name", 0.90m), "alias warehouse_name");
        m[2].S("targetFieldName").Should().Be("city", "fuzzy");
        m[2].ND("confidenceScore").Should().BeInRange(0.70m, 0.89m, "fuzzy matches score 0.70–0.89");
        (m[3].S("targetFieldName"), m[3].ND("confidenceScore")).Should().Be(("is_active", 0.95m), "normalized");
        (m[4].S("targetFieldName"), m[4].ND("confidenceScore")).Should().Be(("external_id", 0.90m), "external_id alias");
        (m[5].IsNull("targetFieldName"), m[5].IsNull("confidenceScore"), m[5].B("isAutoMapped")).Should().Be((true, true, false));
        m[0].S("targetDisplayName").Should().Be("Code");
        s.Strings("unmappedColumns").Should().Equal("Zzz Notes");
        s.Strings("unmappedRequiredFields").Should().BeEmpty();

        // the preview before validation shows the raw mapped values
        var preview = await k.Ok(k.Get($"/api/import/sessions/{uuid}/preview?rows=5"), "preview");
        preview.A("columns").Select(c => c.S("fieldName")).Should().Equal("code", "name", "city", "is_active", "external_id");
        preview.A("rows").Single().P("values").S("is_active").Should().Be("Yes");

        // manual remap (a patch: other columns keep their mapping)
        var remapped = await k.Ok(k.PutMappings(uuid, (5, "address")), "map Zzz Notes → address");
        var after = remapped.A("mappings").ToDictionary(x => x.I("sourceColumnIndex"));
        (after[5].S("targetFieldName"), after[5].B("isAutoMapped"), after[5].IsNull("confidenceScore")).Should().Be(("address", false, true));
        (after[2].S("targetFieldName"), after[2].B("isAutoMapped")).Should().Be(("city", true), "untouched columns keep the auto mapping");
        remapped.S("status").Should().Be("MAPPING");
        remapped.Strings("unmappedColumns").Should().BeEmpty();

        // D-47 — an incomplete mapping is saved; IMP-MAP-01 is enforced by validate
        var incomplete = await k.Ok(k.PutMappings(uuid, (0, null)), "unmap the code");
        incomplete.Strings("unmappedRequiredFields").Should().Equal("code");
        (await k.Validate(uuid)).ShouldFail(HttpStatusCode.BadRequest, "IMP-MAP-01", "Map required field: Code.");
        (await k.SessionAsync(uuid)).S("status").Should().Be("MAPPING", "a refused validation leaves the session as it was");
        await k.Ok(k.PutMappings(uuid, (0, "code")), "map the code back");
        // IMP-MAP-03 and unknown targets are refused by the PUT and leave the mapping as it was
        (await k.PutMappings(uuid, (5, "name"))).ShouldFail(HttpStatusCode.BadRequest, "IMP-MAP-03", "Field 'Name' is mapped twice.");
        (await k.PutMappings(uuid, (5, "address"), (5, "city"))).ShouldFail(HttpStatusCode.BadRequest, "column twice",
            "Column 'Zzz Notes' is mapped twice.");
        (await k.PutMappings(uuid, (5, "bogus"))).ShouldFail(HttpStatusCode.BadRequest, "unknown field", "Unknown field 'bogus'.");
        (await k.PutMappings(uuid, (9, "address"))).ShouldFail(HttpStatusCode.BadRequest, "unknown column", "The file has no column 9.");
        var kept = (await k.Ok(k.Get($"/api/import/sessions/{uuid}/mappings"), "mappings")).Items().ToDictionary(x => x.I("sourceColumnIndex"));
        (kept[0].S("targetFieldName"), kept[5].S("targetFieldName")).Should().Be(("code", "address"));

        // IMP-MAP-02 — at least one key field (or the external id) must be mapped
        var cust = await k.UploadOk("CUSTOMER", Csv(["Customer Name", "Phone"], [["Keyless", "+92 300 1234567"]]), "keyless.csv");
        cust.Strings("unmappedRequiredFields").Should().BeEmpty();
        (await k.Validate(cust.G("uuid"))).ShouldFail(HttpStatusCode.BadRequest, "IMP-MAP-02", "Map at least one key field: Customer Code, Email.");

        // a validated session goes back to MAPPING (rows PENDING) on any mapping change
        var v = await k.ValidateOk(uuid);
        (v.P("session").S("status"), v.P("session").I("validRows")).Should().Be(("VALIDATED", 1));
        var reset = await k.Ok(k.PutMappings(uuid, (2, null)), "unmap the city");
        (reset.S("status"), reset.I("validRows"), reset.Strings("unmappedColumns").Single()).Should().Be(("MAPPING", 0, "Citty"));
        await k.Ok(k.PutMappings(uuid, (2, "city")), "map the city back");

        // presets: save, list, apply to a new upload (preset mappings are manual)
        await k.Ok(k.Post($"/api/import/sessions/{uuid}/mappings/save", new { presetName = "Legacy WH" }), "save preset");
        (await k.Ok(k.Get("/api/import/mapping-presets?entityType=WAREHOUSE"), "presets")).Items().Select(p => p.S("name")).Should().Contain("Legacy WH");
        var withPreset = await k.UploadOk("WAREHOUSE", Csv(headers, [["M2", "Two", "Multan", "No", null, "Street 2"]]), "map2.csv", preset: "legacy wh");
        var pm = withPreset.A("mappings").ToDictionary(x => x.I("sourceColumnIndex"));
        (pm[5].S("targetFieldName"), pm[5].B("isAutoMapped"), pm[0].B("isAutoMapped")).Should().Be(("address", false, false), "preset mappings are manual");
        (await k.Upload("WAREHOUSE", Csv(headers, [["M3", "x", null, null, null, null]]), "m.csv", preset: "Nope")).ShouldFail(HttpStatusCode.BadRequest,
            "unknown preset", "Mapping preset 'Nope' was not found.");

        // execute the remapped session: the manual mapping is what lands
        await k.ExecuteOk((await k.ValidateOk(uuid)).P("session").G("uuid"));
        var row = await WarehouseAsync(o, "M1");
        V(row!, "Name", "City", "Address").Should().Be("Mapped one|Lahore|Mall Road 1");
        (await k.PutMappings(uuid, (5, null))).ShouldFail(HttpStatusCode.BadRequest, "locked", "Mappings cannot change once the import has started.");
    }

    // ── C5 templates ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Template_round_trip_and_migration_workbook()
    {
        var o = await _root.OrgAsync("TPL");
        var k = o.K;

        var profiles = (await k.Ok(k.Get("/api/import/profiles"), "profiles")).Items();
        profiles.Select(p => p.S("entityType")).Should().Equal("UNIT", "PRODUCT_CATEGORY", "PRODUCT", "WAREHOUSE", "WAREHOUSE_LOCATION", "TAX_CODE",
            "CUSTOMER", "SUPPLIER", "SUPPLIER_CONTACT", "PRICING_RULE", "OPENING_STOCK", "CUSTOMER_OPENING_BALANCE", "SUPPLIER_OPENING_BALANCE");
        profiles.Where(p => p.B("isOpeningBalance")).Select(p => (p.S("duplicateStrategy"), p.B("allowUpdate"), p.B("requiresOpeningBalancePermission")))
            .Should().AllBeEquivalentTo(("ERROR", false, true));
        var wh = await k.Ok(k.Get("/api/import/profiles/WAREHOUSE"), "warehouse profile");
        wh.A("fields").Select(f => f.S("fieldName")).Should().StartWith(new[] { "external_id", "code", "name" });
        wh.Strings("matchFields").Should().Equal("code");

        // the template: Data / Instructions / hidden Lookups, required headers red, help row 2, sample row 3
        var bytes = await k.DownloadOkAsync("/api/import/profiles/WAREHOUSE/template", "warehouse-import-template.xlsx");
        using (var wb = Open(bytes))
        {
            wb.Worksheets.Select(w => w.Name).Should().Equal("Data", "Instructions", "Lookups");
            wb.Worksheet("Lookups").Visibility.Should().Be(XLWorksheetVisibility.Hidden);
            var data = wb.Worksheet("Data");
            Rows(data)[0].Should().Equal("External ID", "Code", "Name", "Address", "City", "Country", "Active");
            data.Cell(1, 2).Style.Font.FontColor.Should().Be(XLColor.Red, "Code is required");
            data.Cell(2, 2).GetString().Should().StartWith("Required.");
            data.Cell(3, 2).GetString().Should().Be("WH-MAIN", "the sample row");

            // fill rows 4 and 5, leave the help and sample rows as generated
            object?[][] fill = [[null, "TPL-1", "Template One", null, "Lahore", "PK", "Yes"], ["EXT-T2", "TPL-2", "Template Two", null, "Karachi", null, null]];
            for (var r = 0; r < fill.Length; r++)
                for (var c = 0; c < fill[r].Length; c++) Set(data.Cell(4 + r, c + 1), fill[r][c]);
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            bytes = ms.ToArray();
        }
        var s = await k.UploadOk("WAREHOUSE", bytes, "warehouse-import-template.xlsx");
        (s.I("totalRows"), s.S("sheetName")).Should().Be((2, "Data"), "the untouched help and sample rows are skipped");
        s.A("mappings").Where(m => !m.IsNull("targetFieldName")).Select(m => m.ND("confidenceScore")).Should().OnlyContain(c => c == 1.00m,
            "the template's headers map exactly");
        var preview = await k.Ok(k.Get($"/api/import/sessions/{s.G("uuid")}/preview"), "preview");
        preview.A("rows").Select(r => r.I("rowNumber")).Should().Equal(4, 5);
        var v = await k.ValidateOk(s.G("uuid"));
        v.P("session").I("validRows").Should().Be(2, string.Join(" | ", v.Codes()));
        var done = (await k.ExecuteOk(s.G("uuid"))).P("session");
        done.Counts().Should().StartWith("COMPLETED total=2 valid=2 invalid=0 imported=2");
        (await WarehouseCountAsync(o, "WH-MAIN")).Should().Be(0, "the sample row was not imported");
        (await WarehouseAsync(o, "TPL-1"))!["City"].Should().Be("Lahore");

        // an overwritten sample row is data
        var again = await k.DownloadOkAsync("/api/import/profiles/WAREHOUSE/template");
        using (var wb = Open(again))
        {
            var data = wb.Worksheet("Data");
            data.Cell(3, 2).Value = "TPL-3";
            data.Cell(3, 3).Value = "Template Three";
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            again = ms.ToArray();
        }
        var s3 = await k.UploadOk("WAREHOUSE", again, "t3.xlsx");
        s3.I("totalRows").Should().Be(1, "the overwritten sample row counts, the generated help row does not");
        (await k.Ok(k.Get($"/api/import/sessions/{s3.G("uuid")}/preview"), "preview")).A("rows").Single().I("rowNumber").Should().Be(3);

        // template dropdown lists come from live organization data (the warehouse codes just imported)
        using (var wb = Open(await k.DownloadOkAsync("/api/import/profiles/WAREHOUSE_LOCATION/template", "warehouse_location-import-template.xlsx")))
        {
            var lookups = Rows(wb.Worksheet("Lookups"));
            var col = lookups[0].IndexOf("warehouses");
            col.Should().BeGreaterThanOrEqualTo(0);
            lookups.Skip(1).Select(r => r[col]).Where(x => x.Length > 0).Should().Contain(new[] { "TPL-1", "TPL-2" });
            wb.DefinedNames.Select(n => n.Name).Should().Contain("List_warehouses");
            Rows(wb.Worksheet("Instructions"))[0][0].Should().Be("Warehouse locations import");
        }

        // the migration workbook: one sheet per profile, in import order
        using (var wb = Open(await k.DownloadOkAsync("/api/import/migration-workbook", "data-migration-workbook.xlsx")))
        {
            var names = wb.Worksheets.Select(w => w.Name).ToList();
            names.Should().HaveCount(13);
            names[0].Should().Be("1 - Units of measure");
            names[1].Should().Be("2 - Product categories");
            names[12].Should().Be("13 - Supplier opening balances");
            names.Should().OnlyContain(n => n.Length <= 31);
            Rows(wb.Worksheet(4))[0].Should().StartWith(new[] { "External ID", "Code", "Name" }, "sheet 4 = warehouses");
        }
        (await k.DownloadAsync("/api/import/profiles/NOPE/template")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    // ── The migration wizard order ───────────────────────────────────────────────

    [Fact]
    public async Task Migration_wizard_imports_every_master_in_order()
    {
        var o = await _root.OrgAsync("WIZ");
        var k = o.K;
        var t = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        static string Rc(JsonElement v) => string.Join(" | ", v.A("errors").Select(e => $"{e.I("rowNumber")}:{e.S("fieldName")}:{e.S("errorCode")}:{e.S("message")}"));
        static List<string> RowCodes(JsonElement v) => v.A("errors").Select(e => $"{e.I("rowNumber")}:{e.S("errorCode")}").Distinct().ToList();

        // 1. Units — PCS is a global unit: SKIP leaves it alone
        var (uv, us) = await k.ImportXlsxAsync("UNIT", ["Code", "Name"], [["CRT", "Crate"], ["drm", "Drum"], ["PCS", "Piece"]]);
        us.Counts().Should().Be("COMPLETED total=3 valid=3 invalid=0 imported=2 updated=0 skipped=1 error=0", Rc(uv));
        (await k.Ok(k.Get("/api/import/profiles/UNIT"), "unit profile")).I("completedSessions").Should().Be(1);
        (await _f.QueryAsync("SELECT v.DisplayName, v.IsGlobal FROM lookups.LookupValues v JOIN lookups.LookupTypes t ON t.Id = v.TypeId " +
                             "WHERE t.Slug = 'uom' AND v.OrganizationId = @o", ("@o", o.OrgId)))
            .Select(r => $"{r["DisplayName"]}|{r["IsGlobal"]}").Should().BeEquivalentTo("Crate (CRT)|False", "Drum (DRM)|False");

        // 2. Categories — two levels, parents earlier in the file; refusals: a later parent, three levels, an unknown parent
        var (cv, cs) = await k.ImportXlsxAsync("PRODUCT_CATEGORY", ["Code", "Name", "Parent Code"],
        [
            ["RAW", "Raw materials", null], ["RAW-MET", "Metals", "RAW"], ["FG", "Finished goods", null], ["FG-CAB", "Cables", "fg"],
            ["ORPHAN", "Orphan", "LATE"], ["LATE", "Late parent", null], ["DEEP", "Too deep", "RAW-MET"], ["NOPAR", "No parent", "ZZZ"]
        ]);
        cs.Counts().Should().Be("COMPLETED_WITH_ERRORS total=8 valid=5 invalid=3 imported=5 updated=0 skipped=0 error=0", Rc(cv));
        RowCodes(cv).Should().BeEquivalentTo("6:MISSING_DEPENDENCY", "8:MISSING_DEPENDENCY", "9:MISSING_DEPENDENCY");
        cv.A("errors").Single(e => e.I("rowNumber") == 8).S("message").Should().Contain("Only two category levels exist");
        (await _f.QueryAsync("SELECT Code FROM inventory.ProductCategories WHERE OrganizationId = @o", ("@o", o.OrgId)))
            .Select(r => (string)r["Code"]!).Should().BeEquivalentTo("RAW", "FG", "LATE");
        (await _f.QueryAsync("SELECT s.Code AS S, c.Code AS C FROM inventory.ProductSubCategories s JOIN inventory.ProductCategories c ON c.Id = s.CategoryId " +
                             "WHERE c.OrganizationId = @o", ("@o", o.OrgId)))
            .Select(r => $"{r["C"]}/{r["S"]}").Should().BeEquivalentTo("RAW/RAW-MET", "FG/FG-CAB");

        // 3. Products — classification as on the product screens
        string rm = $"RM-{t}", fg = $"FG-{t}", sv = $"SV-{t}";
        var (pv, ps) = await k.ImportXlsxAsync("PRODUCT",
            ["SKU", "Name", "Category Code", "Unit", "Sale Price", "Cost Price", "Product Type", "Supply Method", "Available for Production", "Available for Retail", "Is Service"],
            [
                [rm, $"Copper rod {t}", "RAW-MET", "CRT", null, "10", "RAW_MATERIAL", null, "Yes", "No", null],
                [fg, $"Cable 4mm {t}", "FG-CAB", "DRM", "150", "100", "FINISHED_GOOD", "MANUFACTURE", "Yes", "Yes", null],
                [$"FGP-{t}", $"Bought FG {t}", "FG", "DRM", "1", "1", "FINISHED_GOOD", "PURCHASE", null, null, null],
                [$"ST-{t}", $"Stock thing {t}", "FG", "CRT", "1", "1", "STOCK_ITEM", null, "Yes", null, null],
                [sv, $"Install {t}", null, null, "500", null, null, null, null, "Yes", "Yes"],
                [$"BU-{t}", $"Bad unit {t}", null, "ZZZ", null, null, null, null, null, null, null],
                [$"BT-{t}", $"Bad type {t}", null, null, null, null, "WIDGET", null, null, null, null],
                [$"SN-{t}", $"Service says no {t}", null, null, null, null, "SERVICE", null, null, null, "No"]
            ]);
        ps.Counts().Should().Be("COMPLETED_WITH_ERRORS total=8 valid=3 invalid=5 imported=3 updated=0 skipped=0 error=0", Rc(pv));
        RowCodes(pv).Should().BeEquivalentTo("4:RULE_VIOLATION", "5:RULE_VIOLATION", "7:LOOKUP_NOT_FOUND", "8:LOOKUP_NOT_FOUND", "9:RULE_VIOLATION");
        async Task<Dictionary<string, object?>> ProductAsync(string sku) => (await _f.RowAsync(
            "SELECT p.ProductType, p.SupplyMethod, p.IsManufacturable, p.IsStockable, p.UomCode, c.Code AS Cat, s.Code AS Sub, v.Sku AS VSku, " +
            "v.IsAvailableForProduction AS Prod, v.IsAvailableForRetail AS Retail, v.IsAvailableForPos AS Pos, v.SellingPrice, v.PurchasePrice " +
            "FROM inventory.Products p JOIN inventory.ProductVariants v ON v.ProductId = p.Id " +
            "LEFT JOIN inventory.ProductCategories c ON c.Id = p.CategoryId LEFT JOIN inventory.ProductSubCategories s ON s.Id = p.SubCategoryId " +
            "WHERE p.OrganizationId = @o AND p.Sku = @s", ("@o", o.OrgId), ("@s", sku)))!;
        var rmRow = await ProductAsync(rm);
        V(rmRow, "ProductType", "SupplyMethod", "Prod", "Retail", "Pos", "UomCode", "Cat", "Sub", "VSku")
            .Should().Be($"RAW_MATERIAL|PURCHASE|True|False|False|CRT|RAW|RAW-MET|{rm}");
        var fgRow = await ProductAsync(fg);
        V(fgRow, "ProductType", "SupplyMethod", "IsManufacturable", "Prod", "Retail", "UomCode", "Cat", "Sub")
            .Should().Be("FINISHED_GOOD|MANUFACTURE|True|True|True|DRM|FG|FG-CAB");
        (Convert.ToDecimal(fgRow["SellingPrice"]), Convert.ToDecimal(fgRow["PurchasePrice"])).Should().Be((150m, 100m));
        var svRow = await ProductAsync(sv);
        V(svRow, "ProductType", "SupplyMethod", "IsStockable").Should().Be("SERVICE|SERVICE|False");

        // 4. Warehouses, 5. locations (a bin row creates its zone; zone/bin codes upper-case)
        var (wv, ws) = await k.ImportXlsxAsync("WAREHOUSE", ["Code", "Name", "City", "Country"],
            [["WH-MAIN", $"Main {t}", "Lahore", "PK"], ["WH-2", $"Second {t}", "Karachi", "Pakistan"]]);
        ws.Counts().Should().StartWith("COMPLETED total=2 valid=2 invalid=0 imported=2", Rc(wv));
        var (lv, ls) = await k.ImportXlsxAsync("WAREHOUSE_LOCATION", ["Warehouse Code", "Zone Code", "Zone Name", "Bin Code", "Bin Name"],
        [
            ["WH-MAIN", "a", "Zone A", null, null], ["WH-MAIN", "A", null, "a-01", "Shelf 1"], ["WH-MAIN", "B", null, "B-01", null],
            ["WH-NOPE", "A", null, null, null]
        ]);
        ls.Counts().Should().Be("COMPLETED_WITH_ERRORS total=4 valid=3 invalid=1 imported=3 updated=0 skipped=0 error=0", Rc(lv));
        RowCodes(lv).Should().Equal("5:LOOKUP_NOT_FOUND");
        (await _f.QueryAsync("SELECT z.Code, z.Name FROM inventory.Zones z JOIN inventory.Warehouses w ON w.Id = z.WarehouseId " +
                             "WHERE w.OrganizationId = @o AND w.Code = 'WH-MAIN'", ("@o", o.OrgId)))
            .Select(r => $"{r["Code"]}={r["Name"]}").Should().BeEquivalentTo("A=Zone A", "B=B");
        (await _f.QueryAsync("SELECT b.Code, b.Description FROM inventory.Bins b JOIN inventory.Zones z ON z.Id = b.ZoneId " +
                             "JOIN inventory.Warehouses w ON w.Id = z.WarehouseId WHERE w.OrganizationId = @o", ("@o", o.OrgId)))
            .Select(r => $"{r["Code"]}={r["Description"]}").Should().BeEquivalentTo("A-01=Shelf 1", "B-01=");

        // 6. Tax codes — upper-case codes, one default per side
        var (tv, ts) = await k.ImportXlsxAsync("TAX_CODE", ["Tax Code", "Name", "Rate %", "Usage", "Default"],
        [
            ["gst18", "GST 18%", "18", "SALES", "Yes"], ["PGST18", "Purchase GST", "18", "PURCHASE", "Yes"], ["EXEMPT", "Exempt", "0", null, null],
            ["BAD RATE", "x", "150", null, null], ["GST5", "GST 5%", "5", "SALES", "Yes"]
        ]);
        ts.Counts().Should().Be("COMPLETED_WITH_ERRORS total=5 valid=3 invalid=2 imported=3 updated=0 skipped=0 error=0", Rc(tv));
        RowCodes(tv).Should().Contain(new[] { "5:PATTERN_MISMATCH", "6:MULTIPLE_DEFAULTS" });
        var taxes = (await k.Ok(k.Get("/api/finance/tax-codes?includeInactive=true"), "tax codes")).Items().ToDictionary(x => x.S("code")!);
        (taxes["GST18"].S("usage"), taxes["GST18"].B("isDefault"), taxes["GST18"].D("ratePercent")).Should().Be(("SALES", true, 18m));
        (taxes["EXEMPT"].S("usage"), taxes["EXEMPT"].B("isDefault")).Should().Be(("BOTH", false));
        taxes.Values.Count(x => x.B("isDefault") && x.S("usage") is "SALES" or "BOTH").Should().Be(1, "one default per side");

        // 7. Customers — generated codes one past the highest (the file's explicit code included), walk-in protected
        var (kv, ks) = await k.ImportXlsxAsync("CUSTOMER",
            ["Customer Code", "Customer Name", "Email", "Phone", "Country", "Customer Type", "Payment Terms (days)"],
            [
                [null, $"Acme {t}", "acme@a40.test", "+92 42 1234567", "Pakistan", "COMPANY", "30"],
                ["C-00050", $"Beta {t}", "beta@a40.test", null, "PAK", "INDIVIDUAL", "15"],
                [null, $"Gamma {t}", "gamma@a40.test", null, "pk", null, null],
                ["C-WALKIN", "Walk in", null, null, null, null, null],
                [null, $"Bad mail {t}", "not-an-email", null, null, null, null],
                [null, $"Nowhere {t}", null, null, "Atlantis", null, null]
            ]);
        ks.Counts().Should().Be("COMPLETED_WITH_ERRORS total=6 valid=3 invalid=3 imported=3 updated=0 skipped=0 error=0", Rc(kv));
        RowCodes(kv).Should().BeEquivalentTo("5:WALK_IN_PROTECTED", "6:INVALID_EMAIL", "7:LOOKUP_NOT_FOUND");
        var customers = (await _f.QueryAsync("SELECT SupplierCode, SupplierName, Country, CustomerType, PaymentTermsDays FROM suppliers.BusinessPartners " +
                                            "WHERE OrganizationId = @o AND IsCustomer = 1 AND IsSystem = 0", ("@o", o.OrgId)))
            .ToDictionary(r => (string)r["SupplierName"]!);
        customers.Values.Select(c => (string)c["SupplierCode"]!).Should().BeEquivalentTo("C-00050", "C-00051", "C-00052");
        V(customers[$"Beta {t}"], "Country", "CustomerType", "PaymentTermsDays").Should().Be("PK|INDIVIDUAL|15");
        V(customers[$"Acme {t}"], "Country", "CustomerType").Should().Be("PK|COMPANY");

        // 8. Suppliers — approved (ACTIVE + approver) on import; 9. contacts
        var (sv1, ss) = await k.ImportXlsxAsync("SUPPLIER", ["Supplier Code", "Supplier Name", "Email", "Currency", "Payment Terms (days)"],
            [["SUP-001", $"Global Parts {t}", "sales@gp.test", "pkr", "30"], ["SUP-002", $"Metal Works {t}", null, null, null], ["SUP-003", $"Bad cur {t}", null, "XYZ", null]]);
        ss.Counts().Should().Be("COMPLETED_WITH_ERRORS total=3 valid=2 invalid=1 imported=2 updated=0 skipped=0 error=0", Rc(sv1));
        RowCodes(sv1).Should().Equal("4:LOOKUP_NOT_FOUND");
        var sup = (await _f.RowAsync("SELECT UUID, Id, Status, IsActive, IsVendor, ApprovedBy, PaymentTermsDays FROM suppliers.BusinessPartners " +
                                     "WHERE OrganizationId = @o AND SupplierCode = 'SUP-001'", ("@o", o.OrgId)))!;
        V(sup, "Status", "IsActive", "IsVendor", "PaymentTermsDays").Should().Be("ACTIVE|True|True|30");
        sup["ApprovedBy"].Should().NotBeNull("D-4: approved on import");

        var (cov, cos) = await k.ImportXlsxAsync("SUPPLIER_CONTACT", ["Supplier Code", "Contact Name", "Email", "Primary Contact"],
            [["SUP-001", "Sara", "sara@gp.test", "Yes"], ["SUP-001", "Ali", "ali@gp.test", null], ["SUP-404", "Nobody", "n@gp.test", null]]);
        cos.Counts().Should().Be("COMPLETED_WITH_ERRORS total=3 valid=2 invalid=1 imported=2 updated=0 skipped=0 error=0", Rc(cov));
        (await _f.QueryAsync("SELECT ContactName, IsPrimary FROM suppliers.SupplierContacts WHERE SupplierId = @s", ("@s", sup["Id"]!)))
            .Select(r => $"{r["ContactName"]}:{r["IsPrimary"]}").Should().BeEquivalentTo("Sara:True", "Ali:False");

        // 10. Pricing rules — a list price and a customer contract price for the finished good
        var (prv, prs) = await k.ImportXlsxAsync("PRICING_RULE", ["SKU", "Customer Code", "Price Type", "Min Quantity", "Price", "Currency", "Valid From"],
        [
            [fg, null, "SELLING", null, "145", null, "2026-01-01"],
            [fg, "C-00050", "CONTRACT", "10", "130", "PKR", "2026-01-01"],
            [fg, null, "CONTRACT", null, "120", null, null],
            [fg, null, "COST", null, "90", null, null],
            ["NOPE-SKU", null, "SELLING", null, "1", null, null]
        ]);
        prs.Counts().Should().Be("COMPLETED_WITH_ERRORS total=5 valid=2 invalid=3 imported=2 updated=0 skipped=0 error=0", Rc(prv));
        var beta = (Guid)(await _f.RowAsync("SELECT UUID FROM suppliers.BusinessPartners WHERE OrganizationId = @o AND SupplierCode = 'C-00050'", ("@o", o.OrgId)))!["UUID"]!;
        (await _f.QueryAsync("SELECT r.PriceType, r.PartnerId, r.MinQty, r.UnitPrice, r.CurrencyId FROM inventory.PricingRules r " +
                             "JOIN inventory.ProductVariants v ON v.Id = r.VariantId WHERE v.Sku = @s AND r.OrganizationId = @o", ("@s", fg), ("@o", o.OrgId)))
            .Select(r => $"{r["PriceType"]}|{(r["PartnerId"] as Guid?)?.ToString() ?? "-"}|{Convert.ToDecimal(r["MinQty"] ?? 0m):0.##}|{Convert.ToDecimal(r["UnitPrice"]):0.##}")
            .Should().BeEquivalentTo("SELLING|-|0|145", $"CONTRACT|{beta}|10|130");

        // the migration wizard's dashboard: every master step done once
        var dash = (await k.Ok(k.Get("/api/import/profiles"), "profiles")).Items().ToDictionary(p => p.S("entityType")!);
        foreach (var step in new[] { "UNIT", "PRODUCT_CATEGORY", "PRODUCT", "WAREHOUSE", "WAREHOUSE_LOCATION", "TAX_CODE", "CUSTOMER", "SUPPLIER", "SUPPLIER_CONTACT", "PRICING_RULE" })
        {
            dash[step].I("completedSessions").Should().Be(1, step);
            dash[step].IsNull("lastCompletedAt").Should().BeFalse(step);
        }
        dash["UNIT"].P("lastImportedRows").GetInt32().Should().Be(2);
        dash["OPENING_STOCK"].I("completedSessions").Should().Be(0);
    }

    // ── Duplicate strategies ─────────────────────────────────────────────────────

    [Fact]
    public async Task Duplicate_strategies_on_a_reimport_and_update_keeps_blank_fields()
    {
        var o = await _root.OrgAsync("DUP");
        var k = o.K;
        string[] full = ["Code", "Name", "City", "Address", "Active"];
        await k.ImportXlsxAsync("WAREHOUSE", full, [["WD-1", "Dup One", "Lahore", "Mall Road", "No"], ["WD-2", "Dup Two", "Karachi", "Port", "Yes"]]);
        var wd1 = (await WarehouseAsync(o, "WD-1"))!;
        wd1["IsActive"].Should().Be(false);

        // SKIP (the default)
        var (_, skip) = await k.ImportXlsxAsync("WAREHOUSE", full, [["WD-1", "Renamed", null, null, null]]);
        skip.Counts().Should().Be("COMPLETED total=1 valid=1 invalid=0 imported=0 updated=0 skipped=1 error=0");
        (await WarehouseAsync(o, "WD-1"))!["Name"].Should().Be("Dup One");

        // UPDATE — sent values change, blank cells leave the stored values alone (IMP-EXC-04)
        var (_, upd) = await k.ImportXlsxAsync("WAREHOUSE", full, [["wd-1", "Renamed One", null, null, null]], "UPDATE");
        upd.Counts().Should().Be("COMPLETED total=1 valid=1 invalid=0 imported=0 updated=1 skipped=0 error=0");
        var after = (await WarehouseAsync(o, "WD-1"))!;
        after["Uuid"].Should().Be(wd1["Uuid"]);
        V(after, "Name", "City", "Address").Should().Be("Renamed One|Lahore|Mall Road");
        after["IsActive"].Should().Be(false, "a blank Active cell on UPDATE must not re-activate the warehouse");

        // CREATE_NEW never duplicates a natural key; the new row still goes in
        var (cnv, cn) = await k.ImportXlsxAsync("WAREHOUSE", full, [["WD-1", "Again", null, null, null], ["WD-3", "Dup Three", null, null, null]], "CREATE_NEW");
        cn.Counts().Should().Be("COMPLETED_WITH_ERRORS total=2 valid=1 invalid=1 imported=1 updated=0 skipped=0 error=0");
        cnv.A("errors").Single().S("errorCode").Should().Be("DUPLICATE_IN_DB");
        cnv.A("errors").Single().S("message").Should().StartWith("CREATE_NEW cannot create a duplicate of an existing Code");
        (await WarehouseCountAsync(o, "WD-1")).Should().Be(1);

        // ERROR — a match is a row error; nothing valid → execute refused
        var es = (await k.UploadOk("WAREHOUSE", Xlsx(full, [["WD-2", "Again", null, null, null]]), "e.xlsx", "ERROR")).G("uuid");
        var ev = await k.ValidateOk(es);
        (ev.A("errors").Single().S("errorCode"), ev.A("errors").Single().S("message")).Should().Be(("DUPLICATE_IN_DB", "A record with the same Code already exists."));
        (await k.Execute(es)).ShouldFail(HttpStatusCode.BadRequest, "nothing valid", "There are no valid rows to import.");

        // UPDATE keeps blank fields on the other owners' targets too: a tax code (Finance) and a customer (Suppliers)
        string[] tax = ["Tax Code", "Name", "Rate %", "Usage", "Default", "Active"];
        await k.ImportXlsxAsync("TAX_CODE", tax, [["VAT10", "VAT 10", "10", "SALES", "Yes", null], ["OLD5", "Old 5", "5", "PURCHASE", "No", "No"]]);
        var (tuv, tu) = await k.ImportXlsxAsync("TAX_CODE", tax, [["VAT10", "VAT ten", "10", null, null, null], ["OLD5", "Old 5", "6", null, null, null]], "UPDATE");
        tu.Counts().Should().Be("COMPLETED total=2 valid=2 invalid=0 imported=0 updated=2 skipped=0 error=0", string.Join(" | ", tuv.Codes()));
        var taxes = (await k.Ok(k.Get("/api/finance/tax-codes?includeInactive=true"), "tax codes")).Items().ToDictionary(x => x.S("code")!);
        (taxes["VAT10"].S("name"), taxes["VAT10"].S("usage"), taxes["VAT10"].B("isDefault"), taxes["VAT10"].D("ratePercent"))
            .Should().Be(("VAT ten", "SALES", true, 10m), "blank usage / default leave the code as it was");
        (taxes["OLD5"].D("ratePercent"), taxes["OLD5"].B("isActive"), taxes["OLD5"].S("usage")).Should().Be((6m, false, "PURCHASE"),
            "a blank Active cell does not re-activate an inactive code");

        string[] cust = ["Customer Code", "Customer Name", "City", "Customer Type", "Active"];
        await k.ImportXlsxAsync("CUSTOMER", cust, [["C-00100", "Ind Cust", "Multan", "INDIVIDUAL", "No"]]);
        var (_, cu) = await k.ImportXlsxAsync("CUSTOMER", cust, [["C-00100", "Ind Customer", null, null, null]], "UPDATE");
        cu.Counts().Should().Be("COMPLETED total=1 valid=1 invalid=0 imported=0 updated=1 skipped=0 error=0");
        var c = (await _f.RowAsync("SELECT SupplierName, City, CustomerType, IsActive FROM suppliers.BusinessPartners WHERE OrganizationId = @o AND SupplierCode = 'C-00100'",
            ("@o", o.OrgId)))!;
        V(c, "SupplierName", "City", "CustomerType", "IsActive").Should().Be("Ind Customer|Multan|INDIVIDUAL|False",
            "blank type / active cells keep the stored values");

        // a service product updated from a file without the Is Service column stays a service (no "is_service = false" default)
        var svc = $"SVC-{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        await k.ImportXlsxAsync("PRODUCT", ["SKU", "Name", "Is Service", "Active"], [[svc, $"Service {svc}", "Yes", "No"]]);
        var (pv, pu) = await k.ImportXlsxAsync("PRODUCT", ["SKU", "Name", "Sale Price"], [[svc, $"Service {svc} renamed", "75"]], "UPDATE");
        pu.Counts().Should().Be("COMPLETED total=1 valid=1 invalid=0 imported=0 updated=1 skipped=0 error=0", string.Join(" | ", pv.Codes()));
        var p = (await _f.RowAsync("SELECT p.Name, p.ProductType, p.IsActive, p.Status FROM inventory.Products p WHERE p.OrganizationId = @o AND p.Sku = @s",
            ("@o", o.OrgId), ("@s", svc)))!;
        V(p, "Name", "ProductType", "IsActive", "Status").Should().Be($"Service {svc} renamed|SERVICE|False|INACTIVE");
    }

    // ── External references ──────────────────────────────────────────────────────

    [Fact]
    public async Task External_ids_resolve_to_the_record_of_the_first_import()
    {
        var o = await _root.OrgAsync("EXT");
        var k = o.K;
        string[] h = ["Legacy ID", "Customer Name", "Email"];
        var (_, first) = await k.ImportXlsxAsync("CUSTOMER", h, [["LEG-1", "Old Name", "old@a40.test"], ["LEG-2", "Other", "other@a40.test"]]);
        first.Counts().Should().StartWith("COMPLETED total=2 valid=2 invalid=0 imported=2");
        first.A("mappings").Single(m => m.I("sourceColumnIndex") == 0).S("targetFieldName").Should().Be("external_id", "Legacy ID is an external_id alias");
        var leg1 = (Guid)(await _f.RowAsync("SELECT UUID FROM suppliers.BusinessPartners WHERE OrganizationId = @o AND SupplierName = 'Old Name'", ("@o", o.OrgId)))!["UUID"]!;
        (await _f.RowAsync("SELECT EntityUuid FROM import.external_references WHERE OrganizationId = @o AND EntityType = 'CUSTOMER' AND ExternalId = 'LEG-1'",
            ("@o", o.OrgId)))!["EntityUuid"].Should().Be(leg1);

        // no code, a new email — only the external id can find the customer: UPDATE changes that record
        var s = (await k.UploadOk("CUSTOMER", Xlsx(h, [["leg-1", "New Name", "new@a40.test"], ["LEG-3", "Third", "third@a40.test"]]), "c2.xlsx", "UPDATE")).G("uuid");
        await k.ValidateOk(s);
        var done = (await k.ExecuteOk(s)).P("session");
        done.Counts().Should().Be("COMPLETED total=2 valid=2 invalid=0 imported=1 updated=1 skipped=0 error=0");
        var row = (await _f.RowAsync("SELECT SupplierName, Email FROM suppliers.BusinessPartners WHERE UUID = @u", ("@u", leg1)))!;
        V(row, "SupplierName", "Email").Should().Be("New Name|new@a40.test");
        (await _f.CountAsync("SELECT COUNT(*) FROM suppliers.BusinessPartners WHERE OrganizationId = @o AND IsCustomer = 1 AND IsSystem = 0", ("@o", o.OrgId)))
            .Should().Be(3, "the external id matched; no duplicate customer");
        using (var wb = Open(await k.DownloadOkAsync($"/api/import/sessions/{s}/results/download")))
        {
            var rows = Rows(wb.Worksheet("Results"));
            rows[1].Take(5).Should().Equal("leg-1", "New Name", "new@a40.test", "2", "UPDATED");
            rows[1][5].Should().Be(leg1.ToString());
        }

        // SKIP with an external id: the referenced record is skipped
        var (_, skip) = await k.ImportXlsxAsync("CUSTOMER", h, [["LEG-2", "Whatever", "zzz@a40.test"]]);
        skip.Counts().Should().Be("COMPLETED total=1 valid=1 invalid=0 imported=0 updated=0 skipped=1 error=0");
        // the same external id twice in a file
        var (dv, _) = await k.ImportXlsxAsync("CUSTOMER", h, [["LEG-9", "A", null], ["leg-9", "B", null]]);
        dv.A("errors").Select(e => e.S("errorCode")).Should().Contain("DUPLICATE_IN_FILE");
    }

    // ── Reports ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Error_report_and_results_report_hold_the_right_rows()
    {
        var o = await _root.OrgAsync("REP");
        var k = o.K;
        string[] h = ["Code", "Name", "City", "Remarks"];
        var s = (await k.UploadOk("WAREHOUSE", Xlsx(h,
        [
            ["RP-1", "Report One", "Lahore", "ok"], [null, "No code", null, "missing code"], [new string('C', 25), "Too long", null, null],
            ["RP-2", "Report Two", null, null], ["rp-2", "Report Two again", null, "dup"]
        ]), "report.xlsx")).G("uuid");
        var v = await k.ValidateOk(s);
        v.P("session").Counts().Should().StartWith("VALIDATED total=5 valid=2 invalid=3");
        var byCode = v.P("errorCountByCode");
        (byCode.I("REQUIRED"), byCode.I("MAX_LENGTH"), byCode.I("DUPLICATE_IN_FILE")).Should().Be((1, 1, 1));
        var errors = await k.ErrorRowsAsync(s);
        errors.Select(e => e.I("rowNumber")).Should().Equal(3, 4, 6);
        errors[0].P("values").S("name").Should().Be("No code");
        errors[0].A("errors").Single().S("field").Should().Be("code");

        using (var wb = Open(await k.DownloadOkAsync($"/api/import/sessions/{s}/errors/download", $"import-errors-{s:N}.xlsx")))
        {
            var rows = Rows(wb.Worksheet("Errors"));
            rows[0].Should().Equal("Code", "Name", "City", "Remarks", "Row", "Status", "Error Details");
            rows.Should().HaveCount(4, "the header + the 3 failed rows only");
            rows.Skip(1).Select(r => r[4]).Should().Equal("3", "4", "6");
            rows[1].Take(4).Should().Equal("", "No code", "", "missing code");
            rows[1][5].Should().Be("INVALID");
            rows[1][6].Should().StartWith("[REQUIRED]");
            rows[3][6].Should().Contain("[DUPLICATE_IN_FILE]");
            var summary = Rows(wb.Worksheet("Summary"));
            summary[0].Should().Equal("Error code", "Count");
            summary.Skip(1).Where(r => r[0].Length > 0).Select(r => $"{r[0]}={r[1]}").Should()
                .Contain(new[] { "REQUIRED=1", "MAX_LENGTH=1", "DUPLICATE_IN_FILE=1", "Failed rows=3" });
        }

        var done = (await k.ExecuteOk(s)).P("session");
        done.Counts().Should().Be("COMPLETED_WITH_ERRORS total=5 valid=2 invalid=3 imported=2 updated=0 skipped=0 error=0");
        var rp1 = (Guid)(await WarehouseAsync(o, "RP-1"))!["Uuid"]!;
        using (var wb = Open(await k.DownloadOkAsync($"/api/import/sessions/{s}/results/download", $"import-results-{s:N}.xlsx")))
        {
            var rows = Rows(wb.Worksheet("Results"));
            rows[0].Should().Equal("Code", "Name", "City", "Remarks", "Row", "Status", "Record UUID", "Error Details");
            rows.Should().HaveCount(6, "every data row");
            rows.Skip(1).Select(r => $"{r[4]}:{r[5]}").Should().Equal("2:IMPORTED", "3:INVALID", "4:INVALID", "5:IMPORTED", "6:INVALID");
            rows[1][6].Should().Be(rp1.ToString());
            rows[2][6].Should().BeEmpty();
            rows[2][7].Should().Contain("[REQUIRED]");
        }

        // the error report is re-uploadable: fix the cells, upload it again — the report's own columns stay unmapped
        byte[] fixedReport;
        using (var wb = Open(await k.DownloadOkAsync($"/api/import/sessions/{s}/errors/download")))
        {
            var ws = wb.Worksheet("Errors");
            ws.Cell(2, 1).Value = "RP-3";
            ws.Cell(3, 1).Value = "RP-4";
            ws.Cell(4, 1).Value = "RP-5";
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            fixedReport = ms.ToArray();
        }
        var again = await k.UploadOk("WAREHOUSE", fixedReport, "fixed.xlsx");
        var mapped = again.A("mappings").Where(m => !m.IsNull("targetFieldName")).Select(m => $"{m.S("sourceColumnName")}→{m.S("targetFieldName")}").ToList();
        mapped.Should().BeEquivalentTo(new[] { "Code→code", "Name→name", "City→city" }, "Row / Status / Error Details must not map onto fields");
        var rv = await k.ValidateOk(again.G("uuid"));
        rv.P("session").I("validRows").Should().Be(3, string.Join(" | ", rv.Codes()));
    }

    // ── IMP-EXC-02 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_bad_row_rolls_back_its_whole_batch()
    {
        var o = await _root.OrgAsync("RBK");
        var k = o.K;
        var s = (await k.UploadOk("WAREHOUSE", Xlsx(WhHeaders, Wh(("WB-1", "Batch One", null), ("WB-2", "Batch Two", null), ("WB-3", "Batch Three", null))), "b.xlsx")).G("uuid");
        (await k.ValidateOk(s)).P("session").I("validRows").Should().Be(3);
        // someone creates WB-2 between validation and execution: the target's re-check fails row 3 and the batch rolls back
        await k.Ok(k.Post("/api/warehouses", new { Code = "WB-2", Name = "Created by hand" }), "create WB-2 by hand");

        var done = (await k.ExecuteOk(s)).P("session");
        done.Counts().Should().Be("FAILED total=3 valid=3 invalid=0 imported=0 updated=0 skipped=0 error=3");
        var errors = await k.ErrorRowsAsync(s);
        errors.Select(e => (e.I("rowNumber"), e.S("status"), e.A("errors").Single().S("code"))).Should().Equal(
            (2, "ERROR", "IMPORT_ERROR"), (3, "ERROR", "IMPORT_ERROR"), (4, "ERROR", "IMPORT_ERROR"));
        errors[1].A("errors").Single().S("message").Should().StartWith("Row 3:");
        errors[1].A("errors").Single().S("field").Should().BeEmpty();
        errors[0].A("errors").Single().S("message").Should().Be("Batch rolled back because of row 3.");
        errors[2].A("errors").Single().S("message").Should().Be("Batch rolled back because of row 3.");
        (await WarehouseCountAsync(o, "WB-1")).Should().Be(0, "the whole batch rolled back");
        (await WarehouseCountAsync(o, "WB-3")).Should().Be(0);
        (await WarehouseAsync(o, "WB-2"))!["Name"].Should().Be("Created by hand");

        // the same on a Suppliers target (one SaveChanges per batch): a supplier code taken after validation
        var ss = (await k.UploadOk("SUPPLIER", Xlsx(["Supplier Code", "Supplier Name"], [["SB-1", "First vendor"], ["SB-2", "Second vendor"]]), "s.xlsx")).G("uuid");
        (await k.ValidateOk(ss)).P("session").I("validRows").Should().Be(2);
        await k.Ok(k.Post("/api/suppliers", new { SupplierName = "Vendor by hand", SupplierCode = "SB-2" }), "a vendor by hand");
        var sd = (await k.ExecuteOk(ss)).P("session");
        sd.Counts().Should().Be("FAILED total=2 valid=2 invalid=0 imported=0 updated=0 skipped=0 error=2");
        var se = await k.ErrorRowsAsync(ss);
        se[1].A("errors").Single().S("message").Should().StartWith("Row 3:");
        se[0].A("errors").Single().S("message").Should().Be("Batch rolled back because of row 3.");
        (await _f.CountAsync("SELECT COUNT(*) FROM suppliers.BusinessPartners WHERE OrganizationId = @o AND SupplierCode = 'SB-1'", ("@o", o.OrgId))).Should().Be(0);
    }

    /// <summary>SQL values joined with "|" (bools True/False) — compares typed expectations to boxed reader values.</summary>
    private static string V(Dictionary<string, object?> row, params string[] keys) =>
        string.Join("|", keys.Select(key => Convert.ToString(row[key], System.Globalization.CultureInfo.InvariantCulture)));

    // ── Tenancy, permissions ─────────────────────────────────────────────────────

    [Fact]
    public async Task Sessions_are_private_to_the_organization_and_need_import_permissions()
    {
        var a = await _root.OrgAsync("TNA");
        var b = await _root.OrgAsync("TNB");
        var s = (await a.K.UploadOk("WAREHOUSE", Csv(WhHeaders, [["T1", "Tenant one", null]]), "a.csv")).G("uuid");

        // org B sees nothing of org A's session
        foreach (var url in new[] { $"/api/import/sessions/{s}", $"/api/import/sessions/{s}/mappings", $"/api/import/sessions/{s}/preview",
                                    $"/api/import/sessions/{s}/errors", $"/api/import/sessions/{s}/progress" })
            (await b.K.Get(url)).ShouldFail(HttpStatusCode.NotFound, $"org B reads {url}", "Import session not found.");
        (await b.K.Validate(s)).ShouldFail(HttpStatusCode.NotFound, "org B validates", "Import session not found.");
        (await b.K.Execute(s)).ShouldFail(HttpStatusCode.NotFound, "org B executes");
        (await b.K.Post($"/api/import/sessions/{s}/cancel")).ShouldFail(HttpStatusCode.NotFound, "org B cancels");
        (await b.K.DownloadAsync($"/api/import/sessions/{s}/errors/download")).Status.Should().Be(HttpStatusCode.NotFound);
        (await b.K.DownloadAsync($"/api/import/sessions/{s}/results/download")).Status.Should().Be(HttpStatusCode.NotFound);
        (await b.K.Ok(b.K.Get("/api/import/sessions?pageSize=100"), "org B list")).A("data").Should().BeEmpty();
        (await a.K.Ok(a.K.Get("/api/import/sessions?pageSize=100"), "org A list")).A("data").Select(x => x.G("uuid")).Should().Contain(s);
        // org B imports its own T1 — codes are per organization
        (await b.K.ImportXlsxAsync("WAREHOUSE", WhHeaders, Wh(("T1", "Tenant B one", null)))).Session.S("status").Should().Be("COMPLETED");

        // no IMPORT_* permission → 403 everywhere
        var none = await a.K.LoginWithPermissionsAsync("noimport", "INVENTORY_VIEW");
        (await a.K.Get("/api/import/profiles", none)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await a.K.Get("/api/import/sessions", none)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await a.K.Get($"/api/import/sessions/{s}", none)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await a.K.Upload("WAREHOUSE", Csv(WhHeaders, [["T2", "x", null]]), "n.csv", client: none)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await a.K.Validate(s, none)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await a.K.Execute(s, none)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await a.K.DownloadAsync("/api/import/profiles/WAREHOUSE/template", none)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await a.K.DownloadAsync("/api/import/migration-workbook", none)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await a.K.Put("/api/import/profiles/WAREHOUSE/settings", new { maxRowsPerFile = 10 }, none)).Status.Should().Be(HttpStatusCode.Forbidden);

        // D-37 — IMPORT_UPLOAD sees its own sessions only, cannot validate/execute without IMPORT_EXECUTE
        var uploader = await a.K.LoginWithPermissionsAsync("uploader", "IMPORT_PROFILE_VIEW", "IMPORT_UPLOAD");
        (await a.K.Get("/api/import/profiles", uploader)).Status.Should().Be(HttpStatusCode.OK);
        var mine = await a.K.Upload("WAREHOUSE", Csv(WhHeaders, [["T3", "Uploader's", null]]), "u.csv", client: uploader);
        mine.Status.Should().Be(HttpStatusCode.Created, mine.ToString());
        var mineId = mine.Result.G("uuid");
        (await a.K.Get($"/api/import/sessions/{s}", uploader)).ShouldFail(HttpStatusCode.NotFound, "another user's session reads as 404");
        (await a.K.Ok(a.K.Get("/api/import/sessions", uploader), "uploader list")).A("data").Select(x => x.G("uuid")).Should().Equal(mineId);
        (await a.K.Validate(mineId, uploader)).Status.Should().Be(HttpStatusCode.Forbidden, "validate needs IMPORT_EXECUTE");
        (await a.K.Put("/api/import/profiles/WAREHOUSE/settings", new { maxRowsPerFile = 10 }, uploader)).Status.Should().Be(HttpStatusCode.Forbidden,
            "settings need IMPORT_PROFILE_MANAGE");
        // the org admin (IMPORT_MANAGE) sees and runs it
        (await a.K.SessionAsync(mineId)).S("fileName").Should().Be("u.csv");
        await a.K.ValidateOk(mineId);
        (await a.K.ExecuteOk(mineId)).P("session").S("status").Should().Be("COMPLETED");
        (await a.K.Ok(a.K.Get("/api/import/sessions?entityType=WAREHOUSE&status=COMPLETED"), "filtered list")).A("data")
            .Select(x => x.G("uuid")).Should().Equal(mineId);
    }
}

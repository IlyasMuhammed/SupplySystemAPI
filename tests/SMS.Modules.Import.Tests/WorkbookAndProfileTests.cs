using ClosedXML.Excel;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Import.Domain;
using SMS.Modules.Import.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Import.Tests;

/// <summary>A40 C5 templates, migration workbook, error/results reports; profiles (reconcile, overrides, stats); purge.</summary>
public class WorkbookAndProfileTests
{
    private static FakeTarget WithLists()
    {
        var t = FakeTarget.Products();
        t.Lists.Add(new ImportTemplateList("units", ["PCS", "KG", "BOX"]));
        return t;
    }

    [Fact]
    public async Task Template_has_data_instructions_and_hidden_lookups_with_styles_and_validations()
    {
        var host = new ImportTestHost(WithLists());
        var file = await host.Workbooks.TemplateAsync("product");
        file.FileName.Should().Be("product-import-template.xlsx");

        using var wb = new XLWorkbook(new MemoryStream(file.Content));
        wb.Worksheets.Select(w => w.Name).Should().Equal("Data", "Instructions", "Lookups");
        wb.Worksheet("Lookups").Visibility.Should().Be(XLWorksheetVisibility.Hidden);

        var data = wb.Worksheet("Data");
        data.Cell(1, 1).GetString().Should().Be("External ID");
        data.Cell(1, 1).HasComment.Should().BeTrue();
        data.Cell(1, 2).GetString().Should().Be("Product Code");
        data.Cell(1, 2).Style.Font.Bold.Should().BeTrue();
        data.Cell(1, 2).Style.Font.FontColor.Should().Be(XLColor.Red, "required header");
        data.Cell(1, 4).Style.Font.FontColor.Should().NotBe(XLColor.Red, "price is optional");
        data.Cell(2, 2).Style.Font.Italic.Should().BeTrue();
        data.Cell(2, 2).Style.Font.FontColor.Should().Be(XLColor.Gray);
        data.Cell(2, 2).GetString().Should().StartWith("Required.");
        data.Cell(3, 2).GetString().Should().Be("P-001");
        data.Cell(3, 2).Style.Fill.BackgroundColor.Should().Be(XLColor.LightBlue);
        data.SheetView.SplitRow.Should().Be(1);

        var validations = data.DataValidations.ToList();
        var unitCol = 10; // external_id + 9 fields; unit is the 10th column
        data.Cell(1, unitCol).GetString().Should().Be("Unit");
        validations.Should().Contain(v => v.AllowedValues == XLAllowedValues.List && v.Ranges.Any(r => r.FirstCell().Address.ColumnNumber == unitCol)
                                          && v.Value.Contains("Lookups"));
        validations.Should().Contain(v => v.AllowedValues == XLAllowedValues.List && v.Value == "\"Yes,No\"");
        validations.Should().Contain(v => v.AllowedValues == XLAllowedValues.Date);
        validations.Should().Contain(v => v.AllowedValues == XLAllowedValues.Decimal);
        validations.Should().Contain(v => v.AllowedValues == XLAllowedValues.WholeNumber);
        wb.DefinedNames.Contains("List_units").Should().BeTrue();
        wb.Worksheet("Lookups").Cell(2, 1).GetString().Should().Be("PCS");

        var instructions = wb.Worksheet("Instructions");
        instructions.CellsUsed().Select(c => c.GetString()).Should().Contain(["Field name", "code", "Allowed values", "PCS, KG, BOX"]);
    }

    [Fact]
    public async Task A_filled_template_uploads_without_its_help_and_sample_rows()
    {
        var host = new ImportTestHost(WithLists());
        var file = await host.Workbooks.TemplateAsync("PRODUCT");
        byte[] filled;
        using (var wb = new XLWorkbook(new MemoryStream(file.Content)))
        {
            var ws = wb.Worksheet("Data");
            ws.Cell(4, 2).Value = "P-100"; ws.Cell(4, 3).Value = "Real one";
            ws.Cell(5, 2).Value = "P-101"; ws.Cell(5, 3).Value = "Real two";
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            filled = ms.ToArray();
        }
        var s = await host.UploadBytesAsync(filled, "filled.xlsx");
        s.TotalRows.Should().Be(2);
        s.SheetNames.Should().Equal("Data", "Instructions");
        s.Mappings.Where(m => m.TargetFieldName != null).Should().OnlyContain(m => m.ConfidenceScore == 1.00m);

        // Overwriting the sample row makes it data.
        using (var wb = new XLWorkbook(new MemoryStream(file.Content)))
        {
            wb.Worksheet("Data").Cell(3, 2).Value = "P-200";
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            (await host.UploadBytesAsync(ms.ToArray(), "sample.xlsx")).TotalRows.Should().Be(1);
        }
    }

    [Fact]
    public async Task Migration_workbook_has_one_sheet_per_profile_in_import_order()
    {
        var host = new ImportTestHost(
            FakeTarget.Products("SUPPLIER", 8), FakeTarget.Products("UNIT", 1), FakeTarget.Products("PRODUCT", 3),
            FakeTarget.Products("CUSTOMER_OPENING_BALANCE_WITH_A_VERY_LONG_NAME", 12, opening: true));
        var file = await host.Workbooks.MigrationWorkbookAsync();
        using var wb = new XLWorkbook(new MemoryStream(file.Content));
        wb.Worksheets.Select(w => w.Name).Should().Equal("1 - Product", "2 - Product", "3 - Product", "4 - Opening stock");
        wb.Worksheet(1).Cell(1, 2).Style.Font.FontColor.Should().Be(XLColor.Red);
        WorkbookSheet("1 - Units").Should().Be("1 - Units");
        WorkbookSheet("12 - Supplier opening balances (open bills)").Length.Should().Be(31);

        static string WorkbookSheet(string n) => ImportWorkbookService.SheetName(int.Parse(n.Split(' ')[0]), n[(n.IndexOf('-') + 2)..]);
    }

    [Fact]
    public async Task Error_report_lists_failed_rows_in_red_with_a_summary_and_results_report_every_row()
    {
        var host = new ImportTestHost(FakeTarget.Products());
        var s = await host.UploadCsvAsync("Product Code,Product Name,Selling Price\nA,a,1\nB,,x\nA,dup,1\n");
        await host.Sessions.ValidateAsync(s.Uuid, ImportTestHost.Caller());

        var errors = await host.Workbooks.ErrorReportAsync(s.Uuid, ImportTestHost.Caller());
        using (var wb = new XLWorkbook(new MemoryStream(errors.Content)))
        {
            var ws = wb.Worksheet("Errors");
            ws.Row(1).CellsUsed().Select(c => c.GetString()).Should().Equal("Product Code", "Product Name", "Selling Price", "Row", "Status", "Error Details");
            ws.Cell(2, 1).GetString().Should().Be("B");
            ws.Cell(2, 6).GetString().Should().Contain("[REQUIRED]").And.Contain("[INVALID_TYPE]");
            ws.Cell(2, 1).Style.Fill.BackgroundColor.Should().Be(ImportWorkbookService.FailedFill);
            ws.Cell(3, 4).GetValue<int>().Should().Be(4);
            ws.LastRowUsed()!.RowNumber().Should().Be(3);
            var summary = wb.Worksheet("Summary");
            summary.CellsUsed().Select(c => c.GetString()).Should().Contain(["REQUIRED", "INVALID_TYPE", "DUPLICATE_IN_FILE", "Failed rows"]);
        }
        // re-uploadable: only the original columns come back (Row / Status / Error Details are dropped, so Status cannot
        // auto-map onto an Active field)
        var reparsed = ImportFileParser.Parse(new MemoryStream(errors.Content), "errors.xlsx", errors.Content.Length, null, 50_000, 10);
        reparsed.Headers.Should().Equal("Product Code", "Product Name", "Selling Price");
        reparsed.Rows.Select(r => r.Values.Length).Should().AllBeEquivalentTo(3);

        await host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller());
        var results = await host.Workbooks.ResultsReportAsync(s.Uuid, ImportTestHost.Caller());
        using (var wb = new XLWorkbook(new MemoryStream(results.Content)))
        {
            var ws = wb.Worksheet("Results");
            ws.LastRowUsed()!.RowNumber().Should().Be(4);
            ws.Cell(2, 5).GetString().Should().Be("IMPORTED");
            Guid.TryParse(ws.Cell(2, 6).GetString(), out _).Should().BeTrue();
            ws.Cell(3, 5).GetString().Should().Be("INVALID");
        }
    }

    // ── Profiles ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reconcile_mirrors_targets_and_deactivates_vanished_ones()
    {
        var host = new ImportTestHost(FakeTarget.Products(), FakeTarget.Products("UNIT", 1));
        await host.Profiles.ReconcileAsync();
        var profiles = await host.Db.Profiles.Include(p => p.Fields).ToListAsync();
        profiles.Should().HaveCount(2).And.OnlyContain(p => p.IsActive);
        profiles.Single(p => p.EntityType == "PRODUCT").Fields.Select(f => f.FieldName).Should().StartWith(["external_id", "code"]);
        profiles.Single(p => p.EntityType == "PRODUCT").MatchFieldsJson.Should().Be("[\"code\"]");

        host.Targets.RemoveAt(1);
        await host.Profiles.ReconcileAsync();
        await host.Profiles.ReconcileAsync();
        (await host.Db.Profiles.AsNoTracking().SingleAsync(p => p.EntityType == "UNIT")).IsActive.Should().BeFalse();
        (await host.Db.ProfileFields.CountAsync()).Should().Be(11 + 11);
        FluentActions.Invoking(() => new ImportTargetRegistry([FakeTarget.Products(), FakeTarget.Products()]))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Profiles_list_in_import_order_with_defaults_overrides_and_completed_stats()
    {
        var host = new ImportTestHost(FakeTarget.Products("PRODUCT", 3), FakeTarget.Products("UNIT", 1),
            FakeTarget.Products("OPENING_STOCK", 11, opening: true));
        var list = await host.Profiles.ListAsync();
        list.Select(p => p.EntityType).Should().Equal("UNIT", "PRODUCT", "OPENING_STOCK");
        list[1].Should().Match<SMS.Modules.Import.Models.ImportProfileModel>(p =>
            p.DuplicateStrategy == "SKIP" && p.MaxRowsPerFile == 50_000 && p.MaxFileSizeMb == 10 && p.AllowUpdate && p.CompletedSessions == 0);
        list[2].Should().Match<SMS.Modules.Import.Models.ImportProfileModel>(p =>
            p.DuplicateStrategy == "ERROR" && p.RequiresOpeningBalancePermission && !p.AllowUpdate);

        var detail = await host.Profiles.UpdateSettingsAsync("PRODUCT", new() { DuplicateStrategy = "update", MaxRowsPerFile = 2, MaxFileSizeMb = 1 }, ImportTestHost.Admin);
        (detail.DuplicateStrategy, detail.MaxRowsPerFile, detail.MaxFileSizeMb).Should().Be(("UPDATE", 2, 1));
        detail.Fields.First().FieldName.Should().Be("external_id");
        await FluentActions.Awaiting(() => host.Profiles.UpdateSettingsAsync("PRODUCT", new() { MaxRowsPerFile = 60_000 }, ImportTestHost.Admin))
            .Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => host.Profiles.UpdateSettingsAsync("OPENING_STOCK", new() { DuplicateStrategy = "SKIP" }, ImportTestHost.Admin))
            .Should().ThrowAsync<BadRequestException>();

        // The override applies to uploads: 3 rows > 2.
        await FluentActions.Awaiting(() => host.UploadCsvAsync(ImportTestHost.Csv(3))).Should().ThrowAsync<BadRequestException>()
            .WithMessage("File exceeds the maximum of 2 rows.");
        var s = await host.UploadCsvAsync(ImportTestHost.Csv(2));
        s.DuplicateStrategy.Should().Be("UPDATE");
        await host.Sessions.ValidateAsync(s.Uuid, ImportTestHost.Caller());
        await host.Execution.ExecuteAsync(s.Uuid, ImportTestHost.Caller());
        var after = (await host.Profiles.ListAsync()).Single(p => p.EntityType == "PRODUCT");
        (after.CompletedSessions, after.LastImportedRows).Should().Be((1, 2));
        after.LastCompletedAt.Should().NotBeNull();
        after.LastCompletedSessionUuid.Should().Be(s.Uuid);
        (await host.Profiles.GetAsync("PRODUCT")).LastCompletedSessionUuid.Should().Be(s.Uuid);
        (await host.Profiles.ListAsync()).Single(p => p.EntityType == "UNIT").LastCompletedSessionUuid.Should().BeNull();

        // Another organization keeps the defaults.
        host.Tenant.OrganizationId = Guid.NewGuid();
        (await host.Profiles.GetAsync("PRODUCT")).MaxRowsPerFile.Should().Be(50_000);
    }

    // ── Purge (D-10) ────────────────────────────────────────────────────────

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Purge_deletes_rows_of_sessions_older_than_90_days_and_keeps_the_summary()
    {
        var host = new ImportTestHost(FakeTarget.Products());
        var old = await host.UploadCsvAsync(ImportTestHost.Csv(3));
        var recent = await host.UploadCsvAsync(ImportTestHost.Csv(2));
        var oldRow = await host.Db.Sessions.SingleAsync(x => x.Uuid == old.Uuid);
        oldRow.CreatedAt = DateTime.UtcNow.AddDays(-91);
        await host.Db.SaveChangesAsync();

        var deleted = await new ImportPurgeJob(host.Db, new FixedClock(DateTimeOffset.UtcNow)).RunAsync();

        deleted.Should().Be(3);
        (await host.Db.Rows.CountAsync()).Should().Be(2);
        var summary = await host.Db.Sessions.AsNoTracking().SingleAsync(x => x.Uuid == old.Uuid);
        summary.TotalRows.Should().Be(3);
        summary.RowsPurgedAt.Should().NotBeNull();
        (await new ImportPurgeJob(host.Db).RunAsync()).Should().Be(0, "idempotent");
        (await host.Sessions.GetAsync(recent.Uuid, ImportTestHost.Caller())).TotalRows.Should().Be(2);
    }
}

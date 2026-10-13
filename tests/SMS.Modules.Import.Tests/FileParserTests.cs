using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using SMS.Modules.Import.Models;
using SMS.Modules.Import.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Import.Tests;

/// <summary>A40 C2 — IMP-FILE-01..06 with the contract's exact messages, CSV and XLSX edge cases, parsing in memory.</summary>
public class FileParserTests
{
    private static ParsedFile Csv(string text, int maxRows = 50_000, int maxMb = 10, Encoding? encoding = null)
    {
        var bytes = (encoding ?? new UTF8Encoding(false)).GetBytes(text);
        if (encoding is UTF8Encoding { } u && u.GetPreamble().Length > 0) bytes = u.GetPreamble().Concat(bytes).ToArray();
        return ImportFileParser.Parse(new MemoryStream(bytes), "f.csv", bytes.Length, null, maxRows, maxMb);
    }

    private static ParsedFile Xlsx(byte[] bytes, string? sheet = null, int maxRows = 50_000) =>
        ImportFileParser.Parse(new MemoryStream(bytes), "f.xlsx", bytes.Length, sheet, maxRows, 10);

    [Theory]
    [InlineData("data.xls")]
    [InlineData("data.txt")]
    [InlineData("data")]
    [InlineData("data.xlsm")]
    public void IMP_FILE_01_only_xlsx_and_csv(string name)
    {
        var act = () => ImportFileParser.Parse(new MemoryStream([1, 2]), name, 2, null, 10, 10);
        act.Should().Throw<BadRequestException>().WithMessage(ImportMessages.FileType);
        ImportMessages.FileType.Should().Be("Only .xlsx and .csv files are accepted. Please save the file as .xlsx (Excel 2007+).");
    }

    [Fact]
    public void IMP_FILE_02_size_limit_uses_the_profile_limit()
    {
        var bytes = new byte[2 * 1024 * 1024 + 1];
        var act = () => ImportFileParser.Parse(new MemoryStream(bytes), "a.CSV", bytes.Length, null, 10, 2);
        act.Should().Throw<BadRequestException>().WithMessage("File exceeds the maximum size of 2 MB.");
    }

    [Fact]
    public void IMP_FILE_03_row_limit()
    {
        var act = () => Csv("A\n1\n2\n3\n", maxRows: 2);
        act.Should().Throw<BadRequestException>().WithMessage("File exceeds the maximum of 2 rows.");
        Csv("A\n1\n2\n", maxRows: 2).Rows.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",,\n1,2,3\n")]
    public void IMP_FILE_04_no_headers(string text) =>
        FluentActions.Invoking(() => Csv(text)).Should().Throw<BadRequestException>().WithMessage("No column headers detected in the first row.");

    [Fact]
    public void IMP_FILE_05_no_data_rows()
    {
        FluentActions.Invoking(() => Csv("A,B\n\n , \n")).Should().Throw<BadRequestException>().WithMessage("The file has no data rows.");
        var xlsx = ImportTestHost.Xlsx(wb => wb.AddWorksheet("S").Cell(1, 1).Value = "Header");
        FluentActions.Invoking(() => Xlsx(xlsx)).Should().Throw<BadRequestException>().WithMessage("The file has no data rows.");
    }

    [Fact]
    public void Csv_quoted_commas_line_breaks_and_escaped_quotes()
    {
        var file = Csv("Code,Name,Note\r\nA1,\"Smith, John\",\"line1\nline2\"\r\nA2,\"He said \"\"hi\"\"\",x\r\n");
        file.Headers.Should().Equal("Code", "Name", "Note");
        file.Rows.Should().HaveCount(2);
        file.Rows[0].Values.Should().Equal("A1", "Smith, John", "line1\nline2");
        file.Rows[1].Values[1].Should().Be("He said \"hi\"");
        file.FileType.Should().Be("CSV");
        file.SheetNames.Should().BeEmpty();
    }

    [Fact]
    public void Csv_semicolon_delimiter_is_detected_and_bom_is_dropped()
    {
        var file = Csv("Code;Name;Price\n1;Café Ünïcødé 日本;1.234,50\n", encoding: new UTF8Encoding(true));
        file.Headers.Should().Equal("Code", "Name", "Price");
        file.Rows[0].Values.Should().Equal("1", "Café Ünïcødé 日本", "1.234,50");
    }

    [Fact]
    public void Csv_delimiter_detection_ignores_quoted_separators()
    {
        ImportFileParser.DetectDelimiter("\"a;b;c\",d\n").Should().Be(",");
        ImportFileParser.DetectDelimiter("a;b,c;d\n").Should().Be(";");
        ImportFileParser.DetectDelimiter("a\tb\tc\n").Should().Be("\t");
    }

    [Fact]
    public void Csv_blank_rows_are_skipped_and_row_numbers_follow_records()
    {
        var file = Csv("A,B\n1,2\n,\n3,4\n");
        file.Rows.Select(r => r.Values[0]).Should().Equal("1", "3");
        file.Rows[0].RowNumber.Should().Be(2);
    }

    [Fact]
    public void Xlsx_typed_cells_dates_numbers_booleans_and_serials()
    {
        var bytes = ImportTestHost.Xlsx(wb =>
        {
            var ws = wb.AddWorksheet("Items");
            ws.Cell(1, 1).Value = "Date"; ws.Cell(1, 2).Value = "Number"; ws.Cell(1, 3).Value = "Flag"; ws.Cell(1, 4).Value = "Serial";
            ws.Cell(1, 5).Value = "Phone";
            ws.Cell(2, 1).Value = new DateTime(2026, 3, 15);
            ws.Cell(2, 2).Value = 1234.5;
            ws.Cell(2, 3).Value = true;
            ws.Cell(2, 4).Value = 46096; // a date typed as a plain number (Excel serial)
            ws.Cell(2, 5).Value = 3001234567d;
        });
        var file = Xlsx(bytes);
        file.Rows[0].Values.Should().Equal("2026-03-15", "1234.5", "true", "46096", "3001234567");
        ImportValueParser.TryParseDate(file.Rows[0].Values[3]!, out var serial).Should().BeTrue();
        serial.Should().Be(new DateOnly(2026, 3, 15));
    }

    [Fact]
    public void Xlsx_multiple_sheets_first_visible_by_default_named_sheet_on_request()
    {
        var bytes = ImportTestHost.Xlsx(wb =>
        {
            var hidden = wb.AddWorksheet("Hidden");
            hidden.Cell(1, 1).Value = "X"; hidden.Cell(2, 1).Value = "hidden";
            hidden.Visibility = XLWorksheetVisibility.Hidden;
            var a = wb.AddWorksheet("Alpha"); a.Cell(1, 1).Value = "Code"; a.Cell(2, 1).Value = "A";
            var b = wb.AddWorksheet("Beta"); b.Cell(1, 1).Value = "Code"; b.Cell(2, 1).Value = "B"; b.Cell(3, 1).Value = "B2";
        });
        var first = Xlsx(bytes);
        first.SheetNames.Should().Equal("Alpha", "Beta");
        first.SheetName.Should().Be("Alpha");
        first.Rows.Single().Values[0].Should().Be("A");

        var beta = Xlsx(bytes, "beta");
        beta.SheetName.Should().Be("Beta");
        beta.Rows.Should().HaveCount(2);

        FluentActions.Invoking(() => Xlsx(bytes, "Gamma")).Should().Throw<BadRequestException>()
            .WithMessage("Sheet 'Gamma' was not found in the workbook.");
    }

    [Fact]
    public void Xlsx_unicode_empty_header_cells_and_empty_rows()
    {
        var bytes = ImportTestHost.Xlsx(wb =>
        {
            var ws = wb.AddWorksheet("S");
            ws.Cell(1, 1).Value = "Név"; ws.Cell(1, 3).Value = "Kod";
            ws.Cell(2, 1).Value = "Ärger ✓"; ws.Cell(2, 3).Value = "K1";
            ws.Cell(4, 1).Value = "second";
        });
        var file = Xlsx(bytes);
        file.Headers.Should().Equal("Név", "Column 2", "Kod");
        file.Rows.Select(r => r.RowNumber).Should().Equal(2, 4);
        file.Rows[0].Values[0].Should().Be("Ärger ✓");
    }

    [Fact]
    public void A_corrupt_xlsx_is_refused()
    {
        var act = () => ImportFileParser.Parse(new MemoryStream([1, 2, 3, 4]), "bad.xlsx", 4, null, 10, 10);
        act.Should().Throw<BadRequestException>().WithMessage(ImportMessages.Unreadable);
    }
}

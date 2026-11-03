using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Hrm.Services.Post;

/// <summary>
/// G-19 / §11A.3: the run's summarized journal for an external accountant, from the same transaction the gateway gets. Columns per contract §9:
/// account role, account code, cost center, debit, credit, description. Summarized, so it holds no employee data.
/// </summary>
internal interface IPayrollJournalExportService
{
    byte[] ToCsv(FinancialTransaction transaction);
    byte[] ToXlsx(FinancialTransaction transaction, string runNumber);
}

internal sealed class PayrollJournalExportService : IPayrollJournalExportService
{
    private static readonly string[] Headers = ["Account role", "Account code", "Cost center", "Debit", "Credit", "Description"];

    public byte[] ToCsv(FinancialTransaction transaction)
    {
        var text = new StringBuilder();
        text.AppendJoin(',', Headers).Append("\r\n");
        foreach (var line in Lines(transaction))
        {
            text.AppendJoin(',',
                Cell(line.AccountRole), Cell(line.AccountCode), Cell(line.CostCenter),
                line.Debit.ToString("0.00", CultureInfo.InvariantCulture), line.Credit.ToString("0.00", CultureInfo.InvariantCulture),
                Cell(line.Label));
            text.Append("\r\n");
        }
        // BOM: Excel otherwise misreads anything outside ASCII.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    public byte[] ToXlsx(FinancialTransaction transaction, string runNumber)
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Journal");
        sheet.Cell(1, 1).Value = $"Payroll journal {runNumber}";
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(2, 1).Value = $"Date {transaction.TransactionDate:yyyy-MM-dd}, currency {transaction.CurrencyCode}";

        const int head = 4;
        for (var c = 0; c < Headers.Length; c++) sheet.Cell(head, c + 1).Value = Headers[c];
        sheet.Row(head).Style.Font.Bold = true;

        var row = head;
        foreach (var line in Lines(transaction))
        {
            row++;
            sheet.Cell(row, 1).SetValue(line.AccountRole ?? string.Empty);
            sheet.Cell(row, 2).SetValue(line.AccountCode ?? string.Empty);
            sheet.Cell(row, 3).SetValue(line.CostCenter ?? string.Empty);
            sheet.Cell(row, 4).Value = line.Debit;
            sheet.Cell(row, 5).Value = line.Credit;
            sheet.Cell(row, 6).SetValue(line.Label ?? string.Empty);
        }

        row++;
        sheet.Cell(row, 1).Value = "Total";
        if (row > head + 1)
        {
            sheet.Cell(row, 4).FormulaA1 = $"SUM(D{head + 1}:D{row - 1})";
            sheet.Cell(row, 5).FormulaA1 = $"SUM(E{head + 1}:E{row - 1})";
        }
        sheet.Row(row).Style.Font.Bold = true;
        sheet.Columns(4, 5).Style.NumberFormat.Format = "#,##0.00";
        sheet.Columns().AdjustToContents();

        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    private static IEnumerable<FinancialTransactionLine> Lines(FinancialTransaction transaction) =>
        transaction.Lines.OrderBy(l => l.LineNumber);

    /// <summary>Quoted when it must be; a leading = + - @ is defused, so a cost center's name cannot run as a formula in the accountant's Excel.</summary>
    internal static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var text = value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }
}

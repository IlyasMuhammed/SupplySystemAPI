using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Modules.Reports.Services.Exports;
using UglyToad.PdfPig;
using Xunit;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-04 §15 R4, R5 and R6 — the six documents the three reports are exported as. The PDFs are read
/// back page by page with PdfPig and the workbooks cell by cell with ClosedXML, so what is asserted is
/// what a person who opens the file sees, not what the exporter thinks it wrote.
/// </summary>
public class SalesAnalysisExportTests
{
    private static readonly DateTime Generated = new(2026, 9, 20, 10, 30, 0, DateTimeKind.Utc);

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(pdf);

    private static string TextOf(byte[] pdf)
    {
        using var doc = Open(pdf);
        return string.Join('\n', doc.GetPages().Select(p => string.Join(' ', p.GetWords().Select(w => w.Text))));
    }

    private static XLWorkbook Workbook(byte[] xlsx) => new(new MemoryStream(xlsx));

    private static void ShouldBeLandscapeA4(byte[] pdf)
    {
        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
        using var doc = Open(pdf);
        (Math.Round(doc.GetPage(1).Width), Math.Round(doc.GetPage(1).Height)).Should().Be((842d, 595d));
    }

    // ═════════════════════════════ R4 Sales by product ═════════════════════════════

    private static SalesByProductItem Product(string? name, string currency, decimal qty, decimal revenue, decimal? average, Guid? uuid = null) =>
        new() { ProductUuid = uuid ?? Guid.NewGuid(), ProductName = name, CurrencyCode = currency, QuantitySold = qty, Revenue = revenue, AverageUnitPrice = average };

    private static readonly SalesByProductItem[] Products =
    [
        Product("Laptop", "PKR", 5m, 5300m, 1060m),
        Product("Mouse", "PKR", 10.5m, 210m, 20m),
        Product(null, "PKR", 2m, 60m, 30m),
        Product("Laptop", "USD", 1m, 900m, 900m),
    ];

    private static readonly SalesByProductTotal[] ProductTotals =
    [
        new() { CurrencyCode = "PKR", ProductCount = 3, Revenue = 5570m },
        new() { CurrencyCode = "USD", ProductCount = 1, Revenue = 900m },
    ];

    private static SalesByProductReport ByProduct(
        IReadOnlyList<SalesByProductItem>? items = null, IReadOnlyList<SalesByProductTotal>? totals = null, SalesByProductCriteria? criteria = null) =>
        new()
        {
            CompanyName = "Northwind Trading", GeneratedAt = Generated,
            Criteria = criteria ?? new SalesByProductCriteria { DateFrom = new DateTime(2026, 9, 1), DateTo = new DateTime(2026, 9, 30), PartnerId = Guid.NewGuid(), CustomerName = "Acme Ltd" },
            Items = [.. items ?? Products], Totals = [.. totals ?? ProductTotals],
            TotalRecords = (items ?? Products).Count, Page = 1, PageSize = Math.Max(1, (items ?? Products).Count), TotalPages = 1
        };

    [Fact]
    public void The_sales_by_product_pdf_is_a_landscape_pdf_that_names_the_company_report_period_and_customer()
    {
        var pdf = SalesByProductPdfExporter.Export(ByProduct());

        ShouldBeLandscapeA4(pdf);
        TextOf(pdf).Should().Contain("Northwind Trading").And.Contain("SALES BY PRODUCT")
            .And.Contain("Issued: 01 Sep 2026 to 30 Sep 2026").And.Contain("Customer: Acme Ltd").And.Contain("Revenue before tax")
            .And.Contain("Generated 2026-09-20 10:30 UTC");
    }

    [Fact]
    public void An_unfiltered_report_says_all_dates_and_all_customers_and_a_customer_with_no_name_is_named_by_id()
    {
        var unfiltered = SalesByProductPdfExporter.CriteriaLines(new SalesByProductCriteria());
        unfiltered.Should().Equal("Issued: All dates", "Customer: All customers", "Revenue before tax");

        var id = Guid.NewGuid();
        SalesByProductPdfExporter.CriteriaLines(new SalesByProductCriteria { PartnerId = id })[1].Should().Be($"Customer: {id}");
        SalesByProductPdfExporter.CriteriaLines(new SalesByProductCriteria { DateFrom = new DateTime(2026, 9, 1) })[0].Should().Be("Issued: From 01 Sep 2026");
        SalesByProductPdfExporter.CriteriaLines(new SalesByProductCriteria { DateTo = new DateTime(2026, 9, 30) })[0].Should().Be("Issued: Up to 30 Sep 2026");
    }

    [Fact]
    public void The_sales_by_product_pdf_has_a_row_per_product_a_dash_for_a_missing_name_and_a_bold_total_per_currency()
    {
        var text = TextOf(SalesByProductPdfExporter.Export(ByProduct()));

        text.Should().Contain("Product Cur. Quantity sold Avg unit price Revenue");
        text.Should().Contain("Laptop PKR 5 1,060.00 5,300.00");
        text.Should().Contain("Mouse PKR 10.5 20.00 210.00");
        text.Should().Contain("- PKR 2 30.00 60.00");
        text.Should().Contain("Laptop USD 1 900.00 900.00");
        text.Should().Contain("Total, 3 products PKR 5,570.00");
        text.Should().Contain("Total, 1 products USD 900.00");
    }

    [Fact]
    public void A_product_with_no_average_price_prints_a_dash_and_products_print_in_the_order_given()
    {
        var items = new[] { Product("First", "PKR", 0m, 0m, null), Product("Second", "PKR", 1m, 5m, 5m) };

        var text = TextOf(SalesByProductPdfExporter.Export(ByProduct(items, [new SalesByProductTotal { CurrencyCode = "PKR", ProductCount = 2, Revenue = 5m }])));

        text.Should().Contain("First PKR 0 - 0.00");
        text.IndexOf("First", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("Second", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_sold_is_a_page_that_says_so_and_has_no_table()
    {
        var text = TextOf(SalesByProductPdfExporter.Export(ByProduct([], [])));

        text.Should().Contain("No products were sold in this period.").And.NotContain("Quantity sold");
    }

    [Fact]
    public void A_long_sales_by_product_pdf_runs_over_pages_and_prints_every_product_once()
    {
        var items = Enumerable.Range(1, 200).Select(i => Product($"Product {i:000}", "PKR", 1m, i, i)).ToList();

        var pdf  = SalesByProductPdfExporter.Export(ByProduct(items, [ProductTotals[0]]));
        var text = TextOf(pdf);

        using var doc = Open(pdf);
        doc.NumberOfPages.Should().BeGreaterThan(3);
        foreach (var page in doc.GetPages()) string.Join(' ', page.GetWords().Select(w => w.Text)).Should().Contain($"{page.Number} of {doc.NumberOfPages}");
        foreach (var item in items)
            System.Text.RegularExpressions.Regex.Matches(text, System.Text.RegularExpressions.Regex.Escape(item.ProductName!) + " PKR").Count.Should().Be(1, item.ProductName);
    }

    [Fact]
    public void A_full_size_sales_by_product_pdf_renders()
    {
        var items = Enumerable.Range(1, SalesAnalysisReportService.MaxRows).Select(i => Product($"Product {i:00000}", "PKR", 1m, i, i)).ToList();

        var pdf = SalesByProductPdfExporter.Export(ByProduct(items, [ProductTotals[0]]));

        using var doc = Open(pdf);
        doc.NumberOfPages.Should().BeGreaterThan(100);
    }

    [Fact]
    public void The_sales_by_product_workbook_has_the_products_first_and_a_summary_second()
    {
        using var wb = Workbook(SalesByProductExcelExporter.Export(ByProduct()));

        wb.Worksheets.Select(w => w.Name).Should().Equal("Sales by Product", "Summary");
        var ws = wb.Worksheet(1);
        Enumerable.Range(1, 5).Select(c => ws.Cell(1, c).GetString()).Should().Equal("Product", "Currency", "Quantity Sold", "Average Unit Price", "Revenue");
        ws.Range(1, 1, 1, 5).Cells().Should().OnlyContain(c => c.Style.Font.Bold);
    }

    [Fact]
    public void Each_product_is_a_typed_row_in_the_order_given_with_amounts_formatted_as_money()
    {
        using var wb = Workbook(SalesByProductExcelExporter.Export(ByProduct()));
        var ws = wb.Worksheet(1);

        (ws.Cell(2, 1).GetString(), ws.Cell(2, 2).GetString()).Should().Be(("Laptop", "PKR"));
        ws.Cell(2, 3).DataType.Should().Be(XLDataType.Number);
        (((decimal)ws.Cell(2, 3).GetDouble()), ((decimal)ws.Cell(2, 4).GetDouble()), ((decimal)ws.Cell(2, 5).GetDouble())).Should().Be((5m, 1060m, 5300m));
        ((decimal)ws.Cell(3, 3).GetDouble()).Should().Be(10.5m);
        ws.Cell(4, 1).GetString().Should().BeEmpty("no name on record");
        ws.Cell(2, 4).Style.NumberFormat.Format.Should().Be("#,##0.00");
        ws.Cell(2, 5).Style.NumberFormat.Format.Should().Be("#,##0.00");
        ws.Cell(2, 3).Style.NumberFormat.Format.Should().Be("#,##0.####");
        ws.LastRowUsed()!.RowNumber().Should().Be(5);
    }

    [Fact]
    public void A_product_with_no_average_price_leaves_that_cell_empty()
    {
        var items = new[] { Product("First", "PKR", 0m, 0m, null) };

        using var wb = Workbook(SalesByProductExcelExporter.Export(ByProduct(items, [])));

        wb.Worksheet(1).Cell(2, 4).IsEmpty().Should().BeTrue();
    }

    [Fact]
    public void The_sales_by_product_summary_sheet_says_what_was_asked_for_and_the_totals_per_currency()
    {
        using var wb = Workbook(SalesByProductExcelExporter.Export(ByProduct()));
        var ws = wb.Worksheet("Summary");

        (ws.Cell(1, 1).GetString(), ws.Cell(1, 2).GetDateTime()).Should().Be(("Issued from", new DateTime(2026, 9, 1)));
        (ws.Cell(2, 1).GetString(), ws.Cell(2, 2).GetDateTime()).Should().Be(("Issued to", new DateTime(2026, 9, 30)));
        ws.Cell(3, 2).GetString().Should().Be("Acme Ltd");
        Enumerable.Range(1, 3).Select(c => ws.Cell(5, c).GetString()).Should().Equal("Currency", "Products", "Revenue");
        (ws.Cell(6, 1).GetString(), ws.Cell(6, 2).GetDouble(), ((decimal)ws.Cell(6, 3).GetDouble())).Should().Be(("PKR", 3d, 5570m));
        ws.Cell(7, 1).GetString().Should().Be("USD");
        ws.Cell(6, 3).Style.NumberFormat.Format.Should().Be("#,##0.00");
    }

    [Fact]
    public void An_unfiltered_sales_by_product_summary_says_all_dates_and_all_customers()
    {
        using var wb = Workbook(SalesByProductExcelExporter.Export(ByProduct(criteria: new SalesByProductCriteria())));
        var ws = wb.Worksheet("Summary");

        (ws.Cell(1, 2).GetString(), ws.Cell(2, 2).GetString(), ws.Cell(3, 2).GetString()).Should().Be(("All dates", "All dates", "All customers"));
    }

    [Fact]
    public void An_empty_sales_by_product_workbook_is_column_heads_only()
    {
        using var wb = Workbook(SalesByProductExcelExporter.Export(ByProduct([], [])));

        wb.Worksheet(1).LastRowUsed()!.RowNumber().Should().Be(1);
        wb.Worksheet("Summary").LastRowUsed()!.RowNumber().Should().Be(5);
    }

    [Fact]
    public void A_full_size_sales_by_product_workbook_writes_every_row()
    {
        var items = Enumerable.Range(1, SalesAnalysisReportService.MaxRows).Select(i => Product($"Product {i:00000}", "PKR", 1m, i, i)).ToList();

        using var wb = Workbook(SalesByProductExcelExporter.Export(ByProduct(items, [ProductTotals[0]])));

        wb.Worksheet(1).Cell(1 + SalesAnalysisReportService.MaxRows, 1).GetString().Should().Be($"Product {SalesAnalysisReportService.MaxRows:00000}");
    }

    // ═════════════════════════════ R5 Sales by customer ════════════════════════════

    private static SalesByCustomerItem Customer(string? name, string currency, int orders, int invoices, decimal revenue, decimal average) =>
        new() { PartnerId = Guid.NewGuid(), CustomerName = name, CurrencyCode = currency, OrderCount = orders, InvoiceCount = invoices, Revenue = revenue, AverageOrderValue = average };

    private static readonly SalesByCustomerItem[] Customers =
    [
        Customer("Acme Ltd", "PKR", 3, 4, 1500m, 500m),
        Customer("Globex Corp", "PKR", 2, 2, 300m, 150m),
        Customer(null, "PKR", 1, 1, 60m, 60m),
        Customer("Acme Ltd", "USD", 1, 1, 30m, 30m),
    ];

    private static readonly SalesByCustomerTotal[] CustomerTotals =
    [
        new() { CurrencyCode = "PKR", CustomerCount = 3, OrderCount = 6, InvoiceCount = 7, Revenue = 1860m, AverageOrderValue = 310m },
        new() { CurrencyCode = "USD", CustomerCount = 1, OrderCount = 1, InvoiceCount = 1, Revenue = 30m, AverageOrderValue = 30m },
    ];

    private static SalesByCustomerReport ByCustomer(
        IReadOnlyList<SalesByCustomerItem>? items = null, IReadOnlyList<SalesByCustomerTotal>? totals = null, SalesByCustomerCriteria? criteria = null) =>
        new()
        {
            CompanyName = "Northwind Trading", GeneratedAt = Generated,
            Criteria = criteria ?? new SalesByCustomerCriteria { DateFrom = new DateTime(2026, 9, 1), DateTo = new DateTime(2026, 9, 30) },
            Items = [.. items ?? Customers], Totals = [.. totals ?? CustomerTotals],
            TotalRecords = (items ?? Customers).Count, Page = 1, PageSize = Math.Max(1, (items ?? Customers).Count), TotalPages = 1
        };

    [Fact]
    public void The_sales_by_customer_pdf_is_a_landscape_pdf_that_names_the_company_report_and_period()
    {
        var pdf = SalesByCustomerPdfExporter.Export(ByCustomer());

        ShouldBeLandscapeA4(pdf);
        TextOf(pdf).Should().Contain("Northwind Trading").And.Contain("SALES BY CUSTOMER")
            .And.Contain("Issued: 01 Sep 2026 to 30 Sep 2026").And.Contain("Revenue before tax")
            .And.Contain("Average order value is revenue per sale order");
    }

    [Fact]
    public void The_sales_by_customer_pdf_has_a_row_per_customer_and_currency_and_a_bold_total_per_currency()
    {
        var text = TextOf(SalesByCustomerPdfExporter.Export(ByCustomer()));

        text.Should().Contain("Customer Cur. Orders Invoices Revenue Avg order value");
        text.Should().Contain("Acme Ltd PKR 3 4 1,500.00 500.00");
        text.Should().Contain("Globex Corp PKR 2 2 300.00 150.00");
        text.Should().Contain("- PKR 1 1 60.00 60.00");
        text.Should().Contain("Acme Ltd USD 1 1 30.00 30.00");
        text.Should().Contain("Total, 3 customers PKR 6 7 1,860.00 310.00");
        text.Should().Contain("Total, 1 customers USD 1 1 30.00 30.00");
    }

    [Fact]
    public void Nobody_billed_is_a_page_that_says_so_and_has_no_table()
    {
        var text = TextOf(SalesByCustomerPdfExporter.Export(ByCustomer([], [])));

        text.Should().Contain("No customers were billed in this period.").And.NotContain("Avg order value");
    }

    [Fact]
    public void An_open_ended_period_is_worded_as_one()
    {
        SalesByCustomerPdfExporter.CriteriaLines(new SalesByCustomerCriteria())[0].Should().Be("Issued: All dates");
        SalesByCustomerPdfExporter.CriteriaLines(new SalesByCustomerCriteria { DateFrom = new DateTime(2026, 9, 1) })[0].Should().Be("Issued: From 01 Sep 2026");
        SalesByCustomerPdfExporter.CriteriaLines(new SalesByCustomerCriteria { DateTo = new DateTime(2026, 9, 30) })[0].Should().Be("Issued: Up to 30 Sep 2026");
    }

    [Fact]
    public void A_long_sales_by_customer_pdf_runs_over_pages_and_prints_every_customer_once()
    {
        var items = Enumerable.Range(1, 200).Select(i => Customer($"Customer {i:000}", "PKR", 1, 1, i, i)).ToList();

        var text = TextOf(SalesByCustomerPdfExporter.Export(ByCustomer(items, [CustomerTotals[0]])));

        foreach (var item in items)
            System.Text.RegularExpressions.Regex.Matches(text, System.Text.RegularExpressions.Regex.Escape(item.CustomerName!) + " PKR").Count.Should().Be(1, item.CustomerName);
    }

    [Fact]
    public void The_sales_by_customer_workbook_has_the_customers_first_typed_and_a_summary_second()
    {
        using var wb = Workbook(SalesByCustomerExcelExporter.Export(ByCustomer()));

        wb.Worksheets.Select(w => w.Name).Should().Equal("Sales by Customer", "Summary");
        var ws = wb.Worksheet(1);
        Enumerable.Range(1, 6).Select(c => ws.Cell(1, c).GetString()).Should().Equal("Customer", "Currency", "Orders", "Invoices", "Revenue", "Average Order Value");
        ws.Range(1, 1, 1, 6).Cells().Should().OnlyContain(c => c.Style.Font.Bold);

        (ws.Cell(2, 1).GetString(), ws.Cell(2, 2).GetString(), ws.Cell(2, 3).GetDouble(), ws.Cell(2, 4).GetDouble()).Should().Be(("Acme Ltd", "PKR", 3d, 4d));
        (((decimal)ws.Cell(2, 5).GetDouble()), ((decimal)ws.Cell(2, 6).GetDouble())).Should().Be((1500m, 500m));
        ws.Cell(2, 3).DataType.Should().Be(XLDataType.Number);
        ws.Cell(2, 5).Style.NumberFormat.Format.Should().Be("#,##0.00");
        ws.Cell(4, 1).GetString().Should().BeEmpty("no name on record");
        ws.LastRowUsed()!.RowNumber().Should().Be(5);
    }

    [Fact]
    public void The_sales_by_customer_summary_sheet_says_the_period_and_the_totals_per_currency()
    {
        using var wb = Workbook(SalesByCustomerExcelExporter.Export(ByCustomer()));
        var ws = wb.Worksheet("Summary");

        (ws.Cell(1, 1).GetString(), ws.Cell(1, 2).GetDateTime(), ws.Cell(2, 1).GetString(), ws.Cell(2, 2).GetDateTime())
            .Should().Be(("Issued from", new DateTime(2026, 9, 1), "Issued to", new DateTime(2026, 9, 30)));
        Enumerable.Range(1, 6).Select(c => ws.Cell(4, c).GetString()).Should().Equal("Currency", "Customers", "Orders", "Invoices", "Revenue", "Average Order Value");
        (ws.Cell(5, 1).GetString(), ws.Cell(5, 2).GetDouble(), ws.Cell(5, 3).GetDouble(), ws.Cell(5, 4).GetDouble()).Should().Be(("PKR", 3d, 6d, 7d));
        (((decimal)ws.Cell(5, 5).GetDouble()), ((decimal)ws.Cell(5, 6).GetDouble())).Should().Be((1860m, 310m));
        ws.Cell(6, 1).GetString().Should().Be("USD");
    }

    [Fact]
    public void An_unfiltered_sales_by_customer_summary_says_all_dates_and_an_empty_workbook_is_column_heads_only()
    {
        using var open = Workbook(SalesByCustomerExcelExporter.Export(ByCustomer(criteria: new SalesByCustomerCriteria())));
        (open.Worksheet("Summary").Cell(1, 2).GetString(), open.Worksheet("Summary").Cell(2, 2).GetString()).Should().Be(("All dates", "All dates"));

        using var empty = Workbook(SalesByCustomerExcelExporter.Export(ByCustomer([], [])));
        empty.Worksheet(1).LastRowUsed()!.RowNumber().Should().Be(1);
        empty.Worksheet("Summary").LastRowUsed()!.RowNumber().Should().Be(4);
    }

    [Fact]
    public void A_null_report_is_a_programming_error_in_every_document()
    {
        ((Action)(() => SalesByProductPdfExporter.Export(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => SalesByProductExcelExporter.Export(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => SalesByCustomerPdfExporter.Export(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => SalesByCustomerExcelExporter.Export(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => FulfilmentStatusPdfExporter.Export(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => FulfilmentStatusExcelExporter.Export(null!))).Should().Throw<ArgumentNullException>();
    }

    // ═════════════════════════════ R6 Fulfilment status ════════════════════════════

    private static FulfilmentStatusItem Delivery(
        string number, string? order, string? customer, string status, string? mode, string? warehouse, DateTime? promised, int days,
        int lines = 1, decimal ordered = 10m, decimal delivered = 0m) =>
        new()
        {
            DeliveryUuid = Guid.NewGuid(), DeliveryNumber = number, SaleOrderUuid = Guid.NewGuid(), SaleOrderNumber = order, CustomerName = customer,
            Status = status, DeliveryMode = mode, WarehouseUuid = warehouse is null ? null : Guid.NewGuid(), WarehouseName = warehouse,
            RequestedDate = promised?.AddDays(-3), PromisedDate = promised, CreatedDate = new DateTime(2026, 9, 10, 9, 0, 0), DaysOpen = days,
            LineCount = lines, QuantityOrdered = ordered, QuantityDelivered = delivered
        };

    private static readonly FulfilmentStatusItem[] Deliveries =
    [
        Delivery("DLV-2026-00003", "SO-2026-00042", "Globex Corp", "PARTIALLY_DELIVERED", "SHIP", "Karachi Depot", new DateTime(2026, 9, 25), 10, 2, 15.5m, 4m),
        Delivery("DLV-2026-00005", "SO-2026-00044", "Acme Ltd", "PENDING_APPROVAL", "SELF_PICKUP", "Lahore Main", null, 3),
        Delivery("DLV-2026-00006", null, null, "DRAFT", null, null, null, 0, 0, 0m, 0m),
    ];

    private static FulfilmentStatusReport Fulfilment(
        IReadOnlyList<FulfilmentStatusItem>? items = null, FulfilmentStatusCriteria? criteria = null, bool counts = true) =>
        new()
        {
            CompanyName = "Northwind Trading", GeneratedAt = Generated, Criteria = criteria ?? new FulfilmentStatusCriteria(),
            ByStatus = counts ? [new() { Status = "DRAFT", Count = 1 }, new() { Status = "PENDING_APPROVAL", Count = 1 }, new() { Status = "PARTIALLY_DELIVERED", Count = 1 }] : [],
            ByWarehouse = counts ? [new() { WarehouseUuid = Guid.NewGuid(), WarehouseName = "Lahore Main", Count = 2 }, new() { WarehouseUuid = null, WarehouseName = null, Count = 1 }] : [],
            ByDeliveryMode = counts ? [new() { DeliveryMode = "SHIP", Count = 2 }, new() { DeliveryMode = "SELF_PICKUP", Count = 1 }] : [],
            Items = [.. items ?? Deliveries],
            TotalRecords = (items ?? Deliveries).Count, Page = 1, PageSize = Math.Max(1, (items ?? Deliveries).Count), TotalPages = 1
        };

    [Fact]
    public void The_fulfilment_pdf_is_a_landscape_pdf_that_names_the_company_report_and_filters()
    {
        var pdf = FulfilmentStatusPdfExporter.Export(Fulfilment());

        ShouldBeLandscapeA4(pdf);
        TextOf(pdf).Should().Contain("Northwind Trading").And.Contain("FULFILMENT STATUS")
            .And.Contain("Status: All open").And.Contain("Warehouse: All warehouses").And.Contain("Delivery mode: All");
    }

    [Fact]
    public void The_filters_are_worded_and_a_warehouse_with_no_name_is_named_by_id()
    {
        var id = Guid.NewGuid();
        var lines = FulfilmentStatusPdfExporter.CriteriaLines(new FulfilmentStatusCriteria { Status = "IN_TRANSIT", WarehouseUuid = id, WarehouseName = "Lahore Main", DeliveryMode = "SELF_PICKUP" });

        lines.Should().Equal("Status: IN TRANSIT", "Warehouse: Lahore Main", "Delivery mode: SELF PICKUP");
        FulfilmentStatusPdfExporter.CriteriaLines(new FulfilmentStatusCriteria { WarehouseUuid = id })[1].Should().Be($"Warehouse: {id}");
    }

    [Fact]
    public void The_fulfilment_pdf_counts_the_open_deliveries_three_ways()
    {
        var text = TextOf(FulfilmentStatusPdfExporter.Export(Fulfilment()));

        text.Should().Contain("OPEN DELIVERIES (3)");
        text.Should().Contain("Status Open").And.Contain("Warehouse Open").And.Contain("Delivery mode Open");
        text.Should().Contain("DRAFT 1").And.Contain("PENDING APPROVAL 1").And.Contain("PARTIALLY DELIVERED 1");
        text.Should().Contain("Lahore Main 2").And.Contain("- 1");
        text.Should().Contain("SHIP 2").And.Contain("SELF PICKUP 1");
    }

    [Fact]
    public void The_fulfilment_pdf_lists_each_delivery_with_the_longest_status_on_one_line_and_dashes_for_what_is_missing()
    {
        var text = TextOf(FulfilmentStatusPdfExporter.Export(Fulfilment()));

        text.Should().Contain("DELIVERIES, OLDEST FIRST");
        text.Should().Contain("DLV-2026-00003 SO-2026-00042 Globex Corp PARTIALLY DELIVERED SHIP Karachi Depot 25 Sep 2026 10 15.5 4");
        text.Should().Contain("DLV-2026-00005 SO-2026-00044 Acme Ltd PENDING APPROVAL SELF PICKUP Lahore Main - 3 10 0");
        text.Should().Contain("DLV-2026-00006 - - DRAFT - - - 0 0 0");
    }

    [Fact]
    public void Deliveries_print_in_the_order_given_which_is_oldest_first()
    {
        var text = TextOf(FulfilmentStatusPdfExporter.Export(Fulfilment()));

        var at = new[] { "DLV-2026-00003", "DLV-2026-00005", "DLV-2026-00006" }.Select(n => text.IndexOf(n, StringComparison.Ordinal)).ToArray();
        at.Should().OnlyContain(i => i >= 0).And.BeInAscendingOrder();
    }

    [Fact]
    public void The_heading_counts_every_open_delivery_the_report_matched_not_only_those_printed()
    {
        var report = Fulfilment();
        report.TotalRecords = 90;

        TextOf(FulfilmentStatusPdfExporter.Export(report)).Should().Contain("OPEN DELIVERIES (90)");
        using var wb = Workbook(FulfilmentStatusExcelExporter.Export(report));
        wb.Worksheet("Summary").Cell(4, 2).GetDouble().Should().Be(90d);
    }

    [Fact]
    public void A_delivery_with_no_mode_is_counted_under_a_dash()
    {
        var report = Fulfilment();
        report.ByDeliveryMode = [new FulfilmentModeCount { DeliveryMode = "SHIP", Count = 2 }, new FulfilmentModeCount { DeliveryMode = "", Count = 1 }];

        TextOf(FulfilmentStatusPdfExporter.Export(report)).Should().Contain("SHIP 2").And.Contain("- 1");
    }

    [Fact]
    public void Nothing_open_is_a_page_that_says_so_and_has_no_counts_or_table()
    {
        var text = TextOf(FulfilmentStatusPdfExporter.Export(Fulfilment([], counts: false)));

        text.Should().Contain("No open deliveries match these filters.").And.NotContain("OPEN DELIVERIES").And.NotContain("DELIVERIES, OLDEST FIRST");
    }

    [Fact]
    public void A_long_fulfilment_pdf_runs_over_pages_and_prints_every_delivery_once()
    {
        var items = Enumerable.Range(1, 200).Select(i => Delivery($"DLV-{i:000000}", "SO-1", "Acme Ltd", "RELEASED", "SHIP", "Lahore Main", null, i)).ToList();

        var pdf  = FulfilmentStatusPdfExporter.Export(Fulfilment(items));
        var text = TextOf(pdf);

        using var doc = Open(pdf);
        doc.NumberOfPages.Should().BeGreaterThan(3);
        foreach (var item in items)
            System.Text.RegularExpressions.Regex.Matches(text, System.Text.RegularExpressions.Regex.Escape(item.DeliveryNumber)).Count.Should().Be(1, item.DeliveryNumber);
    }

    [Fact]
    public void A_full_size_fulfilment_pdf_renders()
    {
        var items = Enumerable.Range(1, FulfilmentReportService.MaxRows).Select(i => Delivery($"DLV-{i:000000}", "SO-1", "Acme Ltd", "RELEASED", "SHIP", "Lahore Main", null, i)).ToList();

        var pdf = FulfilmentStatusPdfExporter.Export(Fulfilment(items));

        using var doc = Open(pdf);
        doc.NumberOfPages.Should().BeGreaterThan(100);
    }

    [Fact]
    public void The_fulfilment_workbook_has_the_deliveries_first_and_a_summary_second()
    {
        using var wb = Workbook(FulfilmentStatusExcelExporter.Export(Fulfilment()));

        wb.Worksheets.Select(w => w.Name).Should().Equal("Open Fulfilments", "Summary");
        var ws = wb.Worksheet(1);
        Enumerable.Range(1, 13).Select(c => ws.Cell(1, c).GetString()).Should().Equal(
            "Delivery", "Sale Order", "Customer", "Status", "Delivery Mode", "Warehouse", "Requested", "Promised", "Created",
            "Days Open", "Lines", "Qty Ordered", "Qty Delivered");
        ws.Range(1, 1, 1, 13).Cells().Should().OnlyContain(c => c.Style.Font.Bold);
    }

    [Fact]
    public void Each_delivery_is_a_typed_row_with_dates_and_quantities_as_such_and_empty_cells_for_what_is_missing()
    {
        using var wb = Workbook(FulfilmentStatusExcelExporter.Export(Fulfilment()));
        var ws = wb.Worksheet(1);

        (ws.Cell(2, 1).GetString(), ws.Cell(2, 2).GetString(), ws.Cell(2, 3).GetString(), ws.Cell(2, 4).GetString(), ws.Cell(2, 5).GetString(), ws.Cell(2, 6).GetString())
            .Should().Be(("DLV-2026-00003", "SO-2026-00042", "Globex Corp", "PARTIALLY_DELIVERED", "SHIP", "Karachi Depot"));
        ws.Cell(2, 7).GetDateTime().Should().Be(new DateTime(2026, 9, 22));
        ws.Cell(2, 8).GetDateTime().Should().Be(new DateTime(2026, 9, 25));
        ws.Cell(2, 9).GetDateTime().Should().Be(new DateTime(2026, 9, 10), "the time of day is not shown");
        (ws.Cell(2, 10).GetDouble(), ws.Cell(2, 11).GetDouble(), ((decimal)ws.Cell(2, 12).GetDouble()), ((decimal)ws.Cell(2, 13).GetDouble())).Should().Be((10d, 2d, 15.5m, 4m));
        ws.Cell(2, 7).Style.DateFormat.Format.Should().Be("yyyy-mm-dd");
        ws.Cell(2, 12).Style.NumberFormat.Format.Should().Be("#,##0.####");

        ws.Cell(3, 8).IsEmpty().Should().BeTrue("no promised date");
        (ws.Cell(4, 2).GetString(), ws.Cell(4, 3).GetString(), ws.Cell(4, 5).GetString(), ws.Cell(4, 6).GetString()).Should().Be(("", "", "", ""));
        ws.LastRowUsed()!.RowNumber().Should().Be(4);
    }

    [Fact]
    public void The_fulfilment_summary_sheet_says_the_filters_and_then_the_three_counts()
    {
        var criteria = new FulfilmentStatusCriteria { Status = "PICKING", WarehouseUuid = Guid.NewGuid(), WarehouseName = "Lahore Main", DeliveryMode = "SHIP" };
        using var wb = Workbook(FulfilmentStatusExcelExporter.Export(Fulfilment(criteria: criteria)));
        var ws = wb.Worksheet("Summary");

        (ws.Cell(1, 2).GetString(), ws.Cell(2, 2).GetString(), ws.Cell(3, 2).GetString(), ws.Cell(4, 2).GetDouble()).Should().Be(("PICKING", "Lahore Main", "SHIP", 3d));

        // 6 By status / 7 heads / 8-10 rows / 11 blank / 12 By warehouse / 13 heads / 14-15 rows / 16 blank / 17 By delivery mode / 18 heads / 19-20 rows
        ws.Cell(6, 1).GetString().Should().Be("By status");
        (ws.Cell(7, 1).GetString(), ws.Cell(7, 2).GetString()).Should().Be(("Status", "Open"));
        (ws.Cell(8, 1).GetString(), ws.Cell(8, 2).GetDouble(), ws.Cell(10, 1).GetString()).Should().Be(("DRAFT", 1d, "PARTIALLY_DELIVERED"));
        ws.Cell(12, 1).GetString().Should().Be("By warehouse");
        (ws.Cell(14, 1).GetString(), ws.Cell(14, 2).GetDouble(), ws.Cell(15, 1).GetString(), ws.Cell(15, 2).GetDouble()).Should().Be(("Lahore Main", 2d, "", 1d));
        ws.Cell(17, 1).GetString().Should().Be("By delivery mode");
        (ws.Cell(19, 1).GetString(), ws.Cell(19, 2).GetDouble(), ws.Cell(20, 1).GetString()).Should().Be(("SHIP", 2d, "SELF_PICKUP"));
        ws.Cell(6, 1).Style.Font.Bold.Should().BeTrue();
        ws.Range(7, 1, 7, 2).Cells().Should().OnlyContain(c => c.Style.Font.Bold);
        ws.LastRowUsed()!.RowNumber().Should().Be(20);
    }

    [Fact]
    public void An_unfiltered_fulfilment_summary_says_all_open_all_warehouses_all_modes_and_an_empty_workbook_is_column_heads_only()
    {
        using var open = Workbook(FulfilmentStatusExcelExporter.Export(Fulfilment()));
        var s = open.Worksheet("Summary");
        (s.Cell(1, 2).GetString(), s.Cell(2, 2).GetString(), s.Cell(3, 2).GetString()).Should().Be(("All open", "All warehouses", "All"));

        using var empty = Workbook(FulfilmentStatusExcelExporter.Export(Fulfilment([], counts: false)));
        empty.Worksheet(1).LastRowUsed()!.RowNumber().Should().Be(1);
        empty.Worksheet("Summary").Cell(4, 2).GetDouble().Should().Be(0d);
    }

    [Fact]
    public void A_full_size_fulfilment_workbook_writes_every_row()
    {
        var items = Enumerable.Range(1, FulfilmentReportService.MaxRows).Select(i => Delivery($"DLV-{i:000000}", "SO-1", "Acme Ltd", "RELEASED", "SHIP", "Lahore Main", null, i)).ToList();

        using var wb = Workbook(FulfilmentStatusExcelExporter.Export(Fulfilment(items)));

        wb.Worksheet(1).Cell(1 + FulfilmentReportService.MaxRows, 1).GetString().Should().Be($"DLV-{FulfilmentReportService.MaxRows:000000}");
    }
}

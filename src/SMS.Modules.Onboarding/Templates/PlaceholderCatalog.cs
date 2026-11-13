using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Templates;

/// <summary>One placeholder a template may use (docs/onboarding/TEMPLATE-PLACEHOLDERS.md).</summary>
public sealed record PlaceholderModel(string Token, string Label, string Group, string Description, bool Structural);

/// <summary>
/// A50 TPL (tasks 241, 257, 361) — the placeholders per document / email type, with the sample values the previews and
/// send-test use. The renderers that fill them live in the owning modules; this list is what they promise to fill.
/// </summary>
internal static class PlaceholderCatalog
{
    private sealed record P(string Name, string Label, string Group, string Sample, string Description = "", bool Structural = false);

    private static readonly P[] Company =
    [
        new("CompanyName",    "Company name",    "Company", "Sunrise Electricals", "From the organization's letterhead, else its name."),
        new("CompanyAddress", "Company address", "Company", "Plot 12, Korangi Industrial Area, Karachi"),
        new("CompanyPhone",   "Company phone",   "Company", "+92 21 111 000"),
        new("CompanyEmail",   "Company email",   "Company", "accounts@sunrise.example"),
        new("CompanyTaxId",   "Company tax ID",  "Company", "NTN-1234567"),
    ];

    private static readonly P[] DocumentCommon =
    [
        new("DocumentTitle",  "Document title",  "Document", "INVOICE", "INVOICE, QUOTATION, PURCHASE ORDER, PACKING LIST, PAYMENT RECEIPT, STATEMENT OF ACCOUNT."),
        new("DocumentNumber", "Document number", "Document", "SINV-20260920-0001"),
        new("DocumentDate",   "Document date",   "Document", "20 Sep 2026", "dd MMM yyyy."),
        new("CurrencyCode",   "Currency",        "Amounts",  "PKR"),
        new("TotalAmount",    "Total amount",    "Amounts",  "3,780.00", "N2, without the currency code."),
        new("AmountInWords",  "Amount in words", "Amounts",  "PKR Three thousand seven hundred and eighty only"),
        new("Notes",          "Notes",           "Document", "Deliver to gate 2"),
        new("GeneratedDate",  "Printed at",      "Document", "21 Sep 2026 14:05"),
        new("PageNumber",     "Page number",     "Document", "", "Footer only; empty elsewhere."),
        new("LineItems",          "Line items table",   "Blocks", "", "The lines as a table.", true),
        new("TaxSummary",         "Tax summary",        "Blocks", "", "Tax per rate / code.", true),
        new("Totals",             "Totals",             "Blocks", "", "Subtotal, discount, tax, total (and paid / balance).", true),
        new("BankDetails",        "Bank details",       "Blocks", "", "The template's bank details, when shown.", true),
        new("TermsAndConditions", "Terms & conditions", "Blocks", "", "The template's terms.", true),
        new("SignatureBlock",     "Signature block",    "Blocks", "", "Signature lines (or the disclaimer).", true),
    ];

    private static readonly Dictionary<string, P[]> DocumentSpecific = new(StringComparer.OrdinalIgnoreCase)
    {
        [DocumentTemplateTypes.SalesInvoice] =
        [
            new("CustomerName", "Customer name", "Customer", "Acme Traders Ltd"), new("CustomerAddress", "Customer address", "Customer", "5 Mall Road, Lahore"),
            new("CustomerEmail", "Customer email", "Customer", "buyer@acme.example"), new("CustomerPhone", "Customer phone", "Customer", "+92 300 1112222"),
            new("InvoiceNumber", "Invoice number", "Invoice", "SINV-20260920-0001"), new("InvoiceDate", "Invoice date", "Invoice", "20 Sep 2026"),
            new("DueDate", "Due date", "Invoice", "20 Oct 2026"), new("PaymentTerms", "Payment terms", "Invoice", "Net 30 days"),
            new("SaleOrderNumber", "Sale order", "Invoice", "SO-2026-00042"), new("DeliveryNumber", "Delivery", "Invoice", "DLV-2026-00007"),
            new("Status", "Status", "Invoice", "ISSUED"),
            new("Subtotal", "Subtotal", "Amounts", "4,000.00"), new("DiscountAmount", "Discount", "Amounts", "400.00"), new("TaxAmount", "Tax", "Amounts", "180.00"),
            new("AmountPaid", "Paid", "Amounts", "0.00"), new("BalanceDue", "Balance due", "Amounts", "3,780.00"),
        ],
        [DocumentTemplateTypes.Quotation] =
        [
            new("CustomerName", "Customer name", "Customer", "Acme Traders Ltd"), new("CustomerAddress", "Customer address", "Customer", "5 Mall Road, Lahore"),
            new("CustomerEmail", "Customer email", "Customer", "buyer@acme.example"), new("CustomerReference", "Customer reference", "Customer", "RFQ-889"),
            new("QuotationNumber", "Quotation number", "Quotation", "SQ-2026-00031"), new("QuotationDate", "Quotation date", "Quotation", "20 Sep 2026"),
            new("ValidUntil", "Valid until", "Quotation", "20 Oct 2026"), new("PaymentTerms", "Payment terms", "Quotation", "Net 30"),
            new("DeliveryTerms", "Delivery terms", "Quotation", "Ex-works Karachi"), new("Status", "Status", "Quotation", "SENT"),
            new("Subtotal", "Subtotal", "Amounts", "4,000.00"), new("DiscountAmount", "Discount", "Amounts", "400.00"), new("TaxAmount", "Tax", "Amounts", "180.00"),
        ],
        [DocumentTemplateTypes.PurchaseOrder] =
        [
            new("SupplierName", "Supplier name", "Supplier", "Lahore Cables Ltd"), new("SupplierAddress", "Supplier address", "Supplier", "9 Ferozepur Road, Lahore"),
            new("SupplierPhone", "Supplier phone", "Supplier", "+92 42 3333333"), new("SupplierMobile", "Supplier mobile", "Supplier", "+92 300 7654321"),
            new("PoNumber", "PO number", "Purchase order", "PO-2026-00017"), new("PoTitle", "PO title", "Purchase order", "Copper cable restock"),
            new("PoDate", "PO date", "Purchase order", "20 Sep 2026"), new("DeliveryDate", "Delivery date", "Purchase order", "05 Oct 2026"),
            new("DeliveryWarehouse", "Delivery warehouse", "Purchase order", "Central Warehouse"), new("BudgetCode", "Budget code(s)", "Purchase order", "BC-01"),
            new("Status", "Status", "Purchase order", "APPROVED"),
        ],
        [DocumentTemplateTypes.DeliveryNote] =
        [
            new("CustomerName", "Ship-to party", "Delivery", "Acme Traders Ltd"), new("DeliveryNumber", "Delivery number", "Delivery", "DLV-2026-00007"),
            new("SourceNumber", "Source document", "Delivery", "SO-2026-00042"), new("ShipFrom", "Ship from", "Delivery", "Central Warehouse, 12 Dock Road, Karachi"),
            new("ShipTo", "Ship to", "Delivery", "4 Industrial Estate, Lahore"), new("PromisedDate", "Promised date", "Delivery", "25 Sep 2026"),
            new("PackageCount", "Packages", "Delivery", "2"), new("TotalPieces", "Total pieces", "Delivery", "103.000"),
            new("GrossWeight", "Gross weight", "Delivery", "44.500 kg"), new("Status", "Status", "Delivery", "STAGED"),
        ],
        [DocumentTemplateTypes.PaymentReceipt] =
        [
            new("CustomerName", "Customer name", "Customer", "Acme Traders Ltd"), new("PaymentNumber", "Receipt / payment number", "Payment", "CPAY-20260925-0001"),
            new("PaymentDate", "Payment date", "Payment", "25 Sep 2026"), new("PaymentMethod", "Method", "Payment", "BANK TRANSFER"),
            new("Reference", "Cheque / bank reference", "Payment", "HBL-77812"), new("AmountPaid", "Amount received", "Amounts", "1,000.00"),
            new("AllocatedAmount", "Applied to invoices", "Amounts", "1,000.00"), new("UnallocatedAmount", "On account", "Amounts", "0.00"),
            new("InvoiceNumber", "Invoice(s) paid", "Payment", "SINV-20260920-0001"),
        ],
        [DocumentTemplateTypes.CustomerStatement] =
        [
            new("CustomerName", "Customer name", "Customer", "Acme Traders Ltd"), new("CustomerCode", "Customer code", "Customer", "C-0001"),
            new("PeriodFrom", "Period from", "Statement", "01 Sep 2026"), new("PeriodTo", "Period to", "Statement", "30 Sep 2026"),
            new("OpeningBalance", "Opening balance", "Amounts", "500.00"), new("ClosingBalance", "Closing balance", "Amounts", "3,280.00"),
        ],
    };

    private static readonly P[] EmailCommon =
    [
        .. Company,
        new("Message",     "Your message", "Email", "Please find the document attached.", "The sender's message from the email dialog (line breaks kept)."),
        new("DocumentUrl", "Document link", "Email", "", "Empty for now (no customer portal link)."),
    ];

    private static readonly Dictionary<string, P[]> EmailSpecific = new(StringComparer.OrdinalIgnoreCase)
    {
        [EmailTemplateTypes.InvoiceSent] =
        [
            new("CustomerName", "Customer name", "Customer", "Acme Traders Ltd"), new("CustomerEmail", "Customer email", "Customer", "buyer@acme.example"),
            new("InvoiceNumber", "Invoice number", "Invoice", "SINV-20260920-0001"), new("InvoiceDate", "Invoice date", "Invoice", "20 Sep 2026"),
            new("DueDate", "Due date", "Invoice", "20 Oct 2026"), new("TotalAmount", "Total", "Amounts", "3,780.00"),
            new("AmountDue", "Balance due", "Amounts", "3,780.00"), new("CurrencyCode", "Currency", "Amounts", "PKR"),
            new("AmountInWords", "Total in words", "Amounts", "PKR Three thousand seven hundred and eighty only"),
        ],
        [EmailTemplateTypes.QuotationSent] =
        [
            new("CustomerName", "Customer name", "Customer", "Acme Traders Ltd"), new("QuotationNumber", "Quotation number", "Quotation", "SQ-2026-00031"),
            new("QuotationDate", "Quotation date", "Quotation", "20 Sep 2026"), new("ValidUntil", "Valid until", "Quotation", "20 Oct 2026"),
            new("TotalAmount", "Total", "Amounts", "3,780.00"), new("CurrencyCode", "Currency", "Amounts", "PKR"),
            new("AmountInWords", "Total in words", "Amounts", "PKR Three thousand seven hundred and eighty only"),
        ],
        [EmailTemplateTypes.PoToSupplier] =
        [
            new("SupplierName", "Supplier name", "Supplier", "Lahore Cables Ltd"), new("PoNumber", "PO number", "Purchase order", "PO-2026-00017"),
            new("PoDate", "PO date", "Purchase order", "20 Sep 2026"), new("DeliveryDate", "Delivery date", "Purchase order", "05 Oct 2026"),
            new("TotalAmount", "Total", "Amounts", "5,150.00"), new("CurrencyCode", "Currency", "Amounts", "PKR"),
            new("AmountInWords", "Total in words", "Amounts", "PKR Five thousand one hundred and fifty only"),
        ],
        [EmailTemplateTypes.PaymentReminder] =
        [
            new("CustomerName", "Customer name", "Customer", "Acme Traders Ltd"), new("InvoiceNumber", "Invoice number", "Invoice", "SINV-20260920-0001"),
            new("DueDate", "Due date", "Invoice", "20 Oct 2026"), new("AmountDue", "Amount due", "Amounts", "3,780.00"),
            new("CurrencyCode", "Currency", "Amounts", "PKR"), new("DaysOverdue", "Days overdue", "Invoice", "12"),
        ],
        [EmailTemplateTypes.PaymentReceived] =
        [
            new("CustomerName", "Customer name", "Customer", "Acme Traders Ltd"), new("PaymentNumber", "Payment number", "Payment", "CPAY-20260925-0001"),
            new("PaymentDate", "Payment date", "Payment", "25 Sep 2026"), new("AmountPaid", "Amount received", "Amounts", "1,000.00"),
            new("CurrencyCode", "Currency", "Amounts", "PKR"), new("InvoiceNumber", "Invoice(s)", "Payment", "SINV-20260920-0001"),
        ],
        [EmailTemplateTypes.WelcomeEmail] =
        [
            new("UserName", "User's full name", "User", "Ali Khan"), new("FirstName", "First name", "User", "Ali"),
            new("OrganizationName", "Organization", "User", "Sunrise Electricals"), new("RoleName", "Role", "User", "Sales Manager"),
            new("LoginUrl", "Sign-in link", "User", "https://app.example/auth/login"), new("AcceptInviteUrl", "Invite link", "User", "", "Empty: the welcome email goes out after the invite was accepted."),
        ],
        [EmailTemplateTypes.PayslipSent] =
        [
            new("EmployeeName", "Employee name", "Payslip", "Ali Khan"), new("PayPeriod", "Pay period", "Payslip", "September 2026"),
            new("NetPay", "Net pay", "Payslip", "185,000.00"), new("CurrencyCode", "Currency", "Payslip", "PKR"),
        ],
    };

    public static bool IsDocumentType(string? type) => type is not null && DocumentTemplateTypes.All.Contains(type);
    public static bool IsEmailType(string? type) => type is not null && EmailTemplateTypes.All.Contains(type);

    public static IReadOnlyList<PlaceholderModel> ForDocument(string type) =>
        Company.Concat(DocumentCommon).Concat(DocumentSpecific.GetValueOrDefault(type) ?? [])
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())
            .Select(p => new PlaceholderModel("{" + p.Name + "}", p.Label, p.Group, p.Description, p.Structural)).ToList();

    public static IReadOnlyList<PlaceholderModel> ForEmail(string type) =>
        EmailCommon.Concat(EmailSpecific.GetValueOrDefault(type) ?? [])
            .Select(p => new PlaceholderModel("{" + p.Name + "}", p.Label, p.Group, p.Description, false)).ToList();

    public static Dictionary<string, string?> DocumentSamples(string type) =>
        Company.Concat(DocumentCommon).Concat(DocumentSpecific.GetValueOrDefault(type) ?? []).Where(p => !p.Structural)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (string?)g.Last().Sample, StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, string?> EmailSamples(string type) =>
        EmailCommon.Concat(EmailSpecific.GetValueOrDefault(type) ?? [])
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (string?)g.Last().Sample, StringComparer.OrdinalIgnoreCase);

    public static string Title(string type) => type switch
    {
        DocumentTemplateTypes.SalesInvoice      => "INVOICE",
        DocumentTemplateTypes.Quotation         => "QUOTATION",
        DocumentTemplateTypes.PurchaseOrder     => "PURCHASE ORDER",
        DocumentTemplateTypes.DeliveryNote      => "PACKING LIST",
        DocumentTemplateTypes.PaymentReceipt    => "PAYMENT RECEIPT",
        DocumentTemplateTypes.CustomerStatement => "STATEMENT OF ACCOUNT",
        _ => type.ToUpperInvariant()
    };

    /// <summary>A sample document of the type for the preview (D-68).</summary>
    public static TemplatedDocument SampleDocument(string type, DateTime now)
    {
        var values = DocumentSamples(type);
        values["DocumentTitle"] = Title(type);
        var doc = new TemplatedDocument { TemplateType = type, Title = Title(type), Values = values, GeneratedAt = now };

        switch (type)
        {
            case DocumentTemplateTypes.PurchaseOrder:
                values["PartyLabel"] = "To:"; values["PartyName"] = values["SupplierName"]; values["PartyAddress"] = values["SupplierAddress"];
                values["DocumentNumber"] = values["PoNumber"]; values["TotalAmount"] = "5,150.00";
                doc.LineColumns.AddRange([new("#", 0.4f), new("Item", 3f), new("UOM", 0.8f), new("Qty", 1f, true), new("Unit Price", 1.2f, true), new("Line Total", 1.3f, true)]);
                doc.LineRows.Add(["1", "4mm copper cable — 100m drum", "DRUM", "10.00", "500.00", "5,000.00"]);
                doc.LineRows.Add(["2", "Cable ties", "PACK", "3.00", "50.00", "150.00"]);
                doc.Totals.Add(new("Grand Total", "5,150.00 PKR", true));
                break;
            case DocumentTemplateTypes.DeliveryNote:
                values["PartyLabel"] = "Ship To:"; values["PartyName"] = values["CustomerName"]; values["PartyAddress"] = values["ShipTo"];
                values["DocumentNumber"] = values["DeliveryNumber"];
                doc.LineColumns.AddRange([new("Ln", 0.4f), new("Item Description", 3f), new("Batch", 1.2f), new("UOM", 0.8f), new("Qty", 1f, true)]);
                doc.LineRows.Add(["1", "4mm armoured cable", "B-2026-07", "M", "100.000"]);
                doc.LineRows.Add(["2", "Cable ties", "-", "PACK", "3.000"]);
                doc.Totals.AddRange([new("Packages", "2"), new("Total Pieces", "103.000"), new("Gross Weight", "44.500 kg", true)]);
                break;
            case DocumentTemplateTypes.PaymentReceipt:
                values["PartyLabel"] = "Received From:"; values["PartyName"] = values["CustomerName"]; values["PartyAddress"] = "";
                values["DocumentNumber"] = values["PaymentNumber"]; values["TotalAmount"] = "1,000.00";
                values["AmountInWords"] = "PKR One thousand only";
                doc.LineColumns.AddRange([new("Invoice", 2f), new("Applied", 1.2f, true), new("Balance after", 1.2f, true)]);
                doc.LineRows.Add(["SINV-20260920-0001", "1,000.00", "2,780.00"]);
                doc.Totals.AddRange([new("Amount received", "1,000.00 PKR", true), new("Applied to invoices", "1,000.00 PKR"), new("On account", "0.00 PKR")]);
                break;
            case DocumentTemplateTypes.CustomerStatement:
                values["PartyLabel"] = "Customer:"; values["PartyName"] = values["CustomerName"]; values["PartyAddress"] = "5 Mall Road, Lahore";
                values["DocumentNumber"] = values["CustomerCode"]; values["DocumentDate"] = values["PeriodTo"];
                doc.LineColumns.AddRange([new("Date", 1f), new("Document", 1.6f), new("Description", 2f), new("Debit", 1f, true), new("Credit", 1f, true), new("Balance", 1.1f, true)]);
                doc.LineRows.Add(["20 Sep 26", "SINV-20260920-0001", "Invoice", "3,780.00", "", "4,280.00"]);
                doc.LineRows.Add(["25 Sep 26", "CPAY-20260925-0001", "Payment", "", "1,000.00", "3,280.00"]);
                doc.Totals.AddRange([new("Opening balance", "500.00 PKR"), new("Closing balance", "3,280.00 PKR", true)]);
                break;
            default: // SalesInvoice, Quotation
                var invoice = type == DocumentTemplateTypes.SalesInvoice;
                values["PartyLabel"] = invoice ? "Billed To:" : "Quoted To:"; values["PartyName"] = values["CustomerName"]; values["PartyAddress"] = values["CustomerAddress"];
                values["DocumentNumber"] = invoice ? values["InvoiceNumber"] : values["QuotationNumber"];
                if (!invoice) values["HeaderFacts"] = $"Valid until: {values["ValidUntil"]}";
                else values["HeaderFacts"] = $"Due Date: {values["DueDate"]}";
                doc.LineColumns.AddRange([new("#", 0.4f), new("Description", 3f), new("Qty", 0.9f, true), new("Unit Price", 1.1f, true), new("Disc %", 0.8f, true), new("Tax %", 0.8f, true), new("Amount", 1.3f, true)]);
                doc.LineRows.Add(["1", "4mm copper cable, 100m drum", "100.00", "40.00", "10", "5", "3,780.00"]);
                doc.LineRows.Add(["2", "Cable ties", "3.00", "50.00", "0", "0", "150.00"]);
                doc.TaxSummary.Add(new("GST 5%", "180.00 PKR"));
                doc.Totals.AddRange([new("Subtotal", "4,000.00 PKR"), new("Discount", "-400.00 PKR"), new("Tax", "180.00 PKR"), new("Total", "3,780.00 PKR", true)]);
                break;
        }
        return doc;
    }
}

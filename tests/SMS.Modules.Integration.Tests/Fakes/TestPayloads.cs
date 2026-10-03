using SMS.Modules.Integration.Core.Sync;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Fakes;

/// <summary>Valid payloads by default; tests change the one thing they are about.</summary>
internal static class TestPayloads
{
    public static readonly DateTime TxnDate = new(2026, 9, 1);

    public static CustomerPayload Customer(string id = "C-1", string name = "Acme Traders", string? code = "C001", string? email = "ap@acme.example") => new()
    {
        ExternalId   = id,
        DisplayName  = name,
        CompanyName  = name,
        Code         = code,
        Email        = email,
        Phone        = "042-111-222",
        TaxId        = "TX-" + id,
        CurrencyCode = "PKR",
        BillingAddress = new AddressPayload { Line1 = "1 Mall Road", City = "Lahore", Country = "Pakistan" },
        IsActive     = true
    };

    public static VendorPayload Vendor(string id = "V-1", string name = "Karachi Supplies", string? code = "S001", string? accountNumber = "S001") => new()
    {
        ExternalId    = id,
        DisplayName   = name,
        CompanyName   = name,
        Code          = code,
        AccountNumber = accountNumber,
        Email         = "sales@ks.example",
        TaxId         = "VT-" + id,
        CurrencyCode  = "PKR",
        IsActive      = true
    };

    public static ItemPayload Item(string id = "I-1", string name = "Widget", string sku = "W-1", string? variant = null) => new()
    {
        ExternalId   = id,
        Name         = name,
        VariantName  = variant,
        Sku          = sku,
        Description  = name + " description",
        Kind         = ItemPayloadKind.Goods,
        SalesPrice   = 100m,
        PurchaseCost = 60m,
        IsSold       = true,
        IsPurchased  = true,
        IsActive     = true
    };

    public record struct Line(string Item, decimal Qty, decimal Price, decimal Discount = 0m, decimal Tax = 17m);

    /// <summary>A consistent invoice: totals computed exactly the way SCM computes them (see <see cref="ScmHeader"/>).</summary>
    public static SalesInvoicePayload Invoice(string id = "SI-1", string doc = "INV-0001", string customer = "C-1",
                                              decimal headerDiscount = 0m, params Line[] lines)
    {
        if (lines.Length == 0) lines = [new Line("I-1", 2, 100m)];

        var payload = new SalesInvoicePayload
        {
            ExternalId           = id,
            DocNumber            = doc,
            CustomerExternalId   = customer,
            TxnDate              = TxnDate,
            DueDate              = TxnDate.AddDays(30),
            CurrencyCode         = "PKR",
            Status               = SalesInvoicePayloadStatus.Issued,
            HeaderDiscountAmount = headerDiscount,
            PrivateNote          = "SO-1 · DLV-1"
        };

        var no = 1;
        foreach (var l in lines)
            payload.Lines.Add(new SalesInvoiceLinePayload
            {
                LineNo = no++, ItemExternalId = l.Item, Description = l.Item, Quantity = l.Qty, UnitPrice = l.Price,
                DiscountPercent = l.Discount, TaxPercent = l.Tax
            });

        var (_, _, tax, grand) = ScmHeader(lines.Select(l => (l.Qty, l.Price, l.Discount, l.Tax)));
        payload.ExpectedTaxAmount = tax;
        payload.ExpectedTotal     = grand - headerDiscount;
        return payload;
    }

    /// <summary>
    /// A copy of SMS.Modules.Finance's <c>SalesInvoiceTotals.Header</c> (not referenced: Integration knows no
    /// SCM module). Sums the unrounded line figures and rounds each total <b>once</b>.
    /// </summary>
    public static (decimal Subtotal, decimal Discount, decimal Tax, decimal Grand) ScmHeader(
        IEnumerable<(decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal TaxPercent)> lines)
    {
        decimal subtotal = 0, discount = 0, tax = 0;

        foreach (var (quantity, unitPrice, discountPercent, taxPercent) in lines)
        {
            var gross         = quantity * unitPrice;
            var lineDiscount  = gross * discountPercent / 100m;
            var afterDiscount = gross - lineDiscount;
            var lineTax       = afterDiscount * taxPercent / 100m;

            subtotal += gross;
            discount += lineDiscount;
            tax      += lineTax;
        }

        subtotal = Math.Round(subtotal, 2, MidpointRounding.AwayFromZero);
        discount = Math.Round(discount, 2, MidpointRounding.AwayFromZero);
        tax      = Math.Round(tax,      2, MidpointRounding.AwayFromZero);

        return (subtotal, discount, tax, subtotal - discount + tax);
    }

    public static BillPayload Bill(string id = "B-1", string doc = "SUP-INV-1", string vendor = "V-1", params BillLinePayload[] lines)
    {
        if (lines.Length == 0)
            lines =
            [
                new BillLinePayload { LineNo = 1, ItemExternalId = "I-1", Category = BillLineCategory.Goods, Quantity = 10, UnitPrice = 60m, Amount = 600m, TaxPercent = 17m },
                new BillLinePayload { LineNo = 2, Category = BillLineCategory.Freight, Amount = 50m },
                new BillLinePayload { LineNo = 3, Category = BillLineCategory.Other, Amount = 25m }
            ];

        var sum = lines.Sum(l => l.Amount);
        var tax = lines.Where(l => l.TaxPercent.HasValue).Sum(l => SyncPayloads.Money(l.Amount * l.TaxPercent!.Value / 100m));

        return new BillPayload
        {
            ExternalId        = id,
            DocNumber         = doc,
            VendorExternalId  = vendor,
            TxnDate           = TxnDate,
            DueDate           = TxnDate.AddDays(30),
            CurrencyCode      = "PKR",
            Lines             = lines.ToList(),
            ExpectedTaxAmount = tax,
            ExpectedTotal     = sum + tax,
            PrivateNote       = "PO-1 · GRN-1"
        };
    }
}

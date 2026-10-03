using FluentAssertions;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration;
using SMS.Modules.Finance.Services;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Finance.Tests.QuickBooks;

/// <summary>QuickBooks plan §4 — SalesInvoice + lines → SalesInvoicePayload, and the totals identity the gateway checks.</summary>
public class SalesInvoicePayloadFactoryTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public SalesInvoicePayloadFactoryTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    /// <summary>An invoice whose header is built by the real <see cref="SalesInvoiceTotals"/>, exactly as the service builds one.</summary>
    internal static SalesInvoice Built(params (decimal Qty, decimal Price, decimal Disc, decimal Tax)[] lines)
    {
        var (subtotal, discount, tax, grand) = SalesInvoiceTotals.Header(lines.Select(l => (l.Qty, l.Price, l.Disc, l.Tax)));
        var invoice = new SalesInvoice
        {
            UUID = Guid.NewGuid(), InvoiceNumber = "SINV-20260920-0001", PartnerId = Guid.NewGuid(),
            SaleOrderNumber = "SO-2026-00042", InvoiceDate = new DateTime(2026, 9, 20), DueDate = new DateTime(2026, 10, 20),
            Subtotal = subtotal, DiscountAmount = discount, TaxAmount = tax, GrandTotal = grand, BalanceDue = grand,
            Status = SalesInvoiceStatuses.Issued, CurrencyCode = "PKR"
        };
        for (var i = 0; i < lines.Length; i++)
            invoice.Lines.Add(new SalesInvoiceLine
            {
                LineNo = i + 1, VariantUuid = Guid.NewGuid(), Description = $"Item {i + 1}",
                Quantity = lines[i].Qty, UnitPrice = lines[i].Price, DiscountPercent = lines[i].Disc, TaxPercent = lines[i].Tax,
                LineTotal = SalesInvoiceTotals.LineTotal(lines[i].Qty, lines[i].Price, lines[i].Disc, lines[i].Tax)
            });
        return invoice;
    }

    /// <summary>
    /// The gateway's check, rounding the way SCM does: S = round(Σ qty × price, 2);
    /// D = round(Σ qty × price × disc% / 100, 2) + header discount; S − D + tax − total. Zero is a pass;
    /// the gateway accepts up to <see cref="GatewayTolerance"/> either way.
    /// </summary>
    internal static decimal GatewayDifference(SalesInvoicePayload p)
    {
        var subtotal = Math.Round(p.Lines.Sum(l => l.Quantity * l.UnitPrice), 2, MidpointRounding.AwayFromZero);
        var discount = Math.Round(p.Lines.Sum(l => l.Quantity * l.UnitPrice * l.DiscountPercent / 100m), 2, MidpointRounding.AwayFromZero)
                     + p.HeaderDiscountAmount;
        return subtotal - discount + p.ExpectedTaxAmount - p.ExpectedTotal;
    }

    /// <summary>What <c>PayloadValidator</c> in the Integration module allows between its sum and the caller's total.</summary>
    private const decimal GatewayTolerance = 0.01m;

    // ── Field by field ───────────────────────────────────────────────────────

    [Fact]
    public void An_invoice_carries_every_mapped_field()
    {
        var invoice = Built((100m, 40m, 10m, 5m), (3m, 12.5m, 0m, 16m));
        invoice.DeliveryNumber = "DLV-2026-00007";
        invoice.Notes = "  Thank you for your business.  ";
        invoice.CurrencyCode = " pkr ";

        var p = SalesInvoicePayloadFactory.Build(invoice);

        p.ExternalId.Should().Be(invoice.UUID.ToString());
        p.DocNumber.Should().Be("SINV-20260920-0001");
        p.CustomerExternalId.Should().Be(invoice.PartnerId.ToString(), "the partner's UUID — the customer's own ExternalId");
        p.TxnDate.Should().Be(new DateTime(2026, 9, 20));
        p.DueDate.Should().Be(new DateTime(2026, 10, 20));
        p.CurrencyCode.Should().Be("PKR");
        p.Status.Should().Be(SalesInvoicePayloadStatus.Issued);
        p.ExpectedTaxAmount.Should().Be(invoice.TaxAmount).And.Be(186m);
        p.ExpectedTotal.Should().Be(invoice.GrandTotal).And.Be(3823.50m);
        p.HeaderDiscountAmount.Should().Be(0m, "SCM has no header discount, and these lines round cleanly");
        p.CustomerMemo.Should().Be("Thank you for your business.");
        p.PrivateNote.Should().Be("SO SO-2026-00042 · DLV DLV-2026-00007");

        p.Lines.Should().HaveCount(2);
        var first = p.Lines[0];
        first.LineNo.Should().Be(1);
        first.ItemExternalId.Should().Be(invoice.Lines.First().VariantUuid.ToString(), "the variant's UUID — the item's own ExternalId");
        first.Description.Should().Be("Item 1");
        first.Quantity.Should().Be(100m);
        first.UnitPrice.Should().Be(40m, "gross, before the line's discount");
        first.DiscountPercent.Should().Be(10m);
        first.TaxPercent.Should().Be(5m);
    }

    // ── SAP alignment S-11: each line's tax code ─────────────────────────────

    [Fact]
    public void Each_line_carries_its_own_tax_code_snapshot_and_old_lines_none()
    {
        var invoice = Built((10m, 10m, 0m, 17m), (5m, 4m, 0m, 0m), (1m, 1m, 0m, 0m), (2m, 3m, 0m, 17m));
        var lines = invoice.Lines.OrderBy(l => l.LineNo).ToList();
        lines[0].TaxCode = "GST17";
        lines[1].TaxCode = " EXEMPT ";
        lines[2].TaxCode = null;      // a line from before tax codes
        lines[3].TaxCode = "   ";

        var p = SalesInvoicePayloadFactory.Build(invoice);

        p.Lines.Select(l => l.TaxCode).Should().Equal("GST17", "EXEMPT", null, null);
        p.Lines.Select(l => l.TaxPercent).Should().Equal(new[] { 17m, 0m, 0m, 17m }, "the rate snapshot still travels — it is what the totals use");
        GatewayDifference(p).Should().Be(0m, "a code changes nothing in the arithmetic");
    }

    [Fact]
    public void An_invoice_from_before_tax_codes_serializes_without_any_tax_code_field()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(SalesInvoicePayloadFactory.Build(Built((1m, 10m, 0m, 17m))));

        json.Should().NotContain("TaxCode", "a null code is omitted so stored payloads keep their fingerprints");
        json.Should().NotContain("ExchangeRate", "an invoice without a rate snapshot carries none either");
    }

    // ── SAP alignment S-5 / S-10: the rate fixed at issue travels with the invoice ──────────

    [Fact]
    public void A_foreign_currency_invoice_carries_the_rate_it_was_issued_at()
    {
        var invoice = Built((2m, 100m, 0m, 0m));
        invoice.CurrencyCode     = " usd ";
        invoice.ExchangeRate     = 281.5m;
        invoice.BaseCurrencyCode = "pkr";

        var p = SalesInvoicePayloadFactory.Build(invoice);

        p.ExchangeRate.Should().Be(281.5m, "QuickBooks must book it at the rate SMS booked it, not the rate table's of the day it is sent");
        p.ExchangeRateCurrencyCode.Should().Be("PKR");
    }

    [Theory]
    [InlineData("PKR", 1.0, "PKR")]     // home currency: the snapshot is 1 — nothing to send, payload as before
    [InlineData("USD", null, "PKR")]    // no rate was on file at issue
    [InlineData("USD", 281.5, null)]    // no base currency known
    [InlineData("USD", 0.0, "PKR")]     // not a rate
    public void Otherwise_an_invoice_carries_no_rate(string currency, double? rate, string? baseCurrency)
    {
        var invoice = Built((1m, 10m, 0m, 0m));
        invoice.CurrencyCode     = currency;
        invoice.ExchangeRate     = rate is null ? null : (decimal)rate;
        invoice.BaseCurrencyCode = baseCurrency;

        var p = SalesInvoicePayloadFactory.Build(invoice);

        p.ExchangeRate.Should().BeNull();
        p.ExchangeRateCurrencyCode.Should().BeNull();
    }

    [Fact]
    public void Lines_go_in_line_number_order_whatever_order_they_were_loaded_in()
    {
        var invoice = Built((1m, 1m, 0m, 0m), (2m, 2m, 0m, 0m), (3m, 3m, 0m, 0m));
        var shuffled = invoice.Lines.Reverse().ToList();
        invoice.Lines = shuffled;

        SalesInvoicePayloadFactory.Build(invoice).Lines.Select(l => l.LineNo).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void No_delivery_and_no_notes_leave_just_the_order_in_the_note_and_no_memo()
    {
        var invoice = Built((1m, 10m, 0m, 0m));
        invoice.DeliveryNumber = "  ";
        invoice.Notes = null;

        var p = SalesInvoicePayloadFactory.Build(invoice);

        p.PrivateNote.Should().Be("SO SO-2026-00042");
        p.CustomerMemo.Should().BeNull();
    }

    [Theory]
    [InlineData("ISSUED",         SalesInvoicePayloadStatus.Issued)]
    [InlineData("PARTIALLY_PAID", SalesInvoicePayloadStatus.Issued)]
    [InlineData("PAID",           SalesInvoicePayloadStatus.Issued)]
    [InlineData("OVERDUE",        SalesInvoicePayloadStatus.Issued)]
    [InlineData("CANCELLED",      SalesInvoicePayloadStatus.Cancelled)]
    [InlineData("CREDIT_NOTE",    SalesInvoicePayloadStatus.CreditNote)]
    public void Statuses_map_to_what_QuickBooks_can_tell_apart(string status, SalesInvoicePayloadStatus expected)
    {
        var invoice = Built((1m, 10m, 0m, 0m));
        invoice.Status = status;

        SalesInvoicePayloadFactory.Build(invoice).Status.Should().Be(expected);
    }

    // ── The totals identity ──────────────────────────────────────────────────

    /// <summary>
    /// The check the gateway used to make — every line rounded on its own. Only here to prove the random sets
    /// below are full of invoices that per-line rounding would have refused, so passing them means something.
    /// </summary>
    private static decimal PerLineRoundingDifference(SalesInvoicePayload p) =>
        p.Lines.Sum(l => Math.Round(l.Quantity * l.UnitPrice * (1 - l.DiscountPercent / 100m), 2, MidpointRounding.AwayFromZero))
        - p.HeaderDiscountAmount + p.ExpectedTaxAmount - p.ExpectedTotal;

    /// <summary>Builds <paramref name="count"/> invoices through the real totals code and checks each the gateway's way.</summary>
    private (int Refused, int PerLineWouldRefuse) CheckMany(int seed, int count, Func<Random, (decimal, decimal, decimal, decimal)> line)
    {
        var random = new Random(seed);
        int refused = 0, perLineWouldRefuse = 0;

        for (var n = 0; n < count; n++)
        {
            var lines   = Enumerable.Range(0, random.Next(1, 13)).Select(_ => line(random)).ToArray();
            var invoice = Built(lines);
            var payload = SalesInvoicePayloadFactory.Build(invoice);
            var because = $"invoice {n}: {string.Join(" | ", lines.Select(l => $"{l.Item1}×{l.Item2} -{l.Item3}% +{l.Item4}%"))}";

            payload.HeaderDiscountAmount.Should().Be(0m, $"SCM has no header discount — {because}");
            GatewayDifference(payload).Should().Be(0m, because);

            if (Math.Abs(GatewayDifference(payload)) > GatewayTolerance) refused++;
            if (Math.Abs(PerLineRoundingDifference(payload)) > GatewayTolerance) perLineWouldRefuse++;
        }

        return (refused, perLineWouldRefuse);
    }

    [Fact]
    public void For_thousands_of_invoices_built_by_the_real_totals_code_the_header_discount_is_zero_and_none_is_refused()
    {
        // Awkward numbers on purpose: fractional quantities to four places, prices to four, odd discounts.
        decimal[] taxRates = [0m, 5m, 13m, 16m, 17m, 18m, 12.5m];
        var (refused, perLine) = CheckMany(20260930, 5000, r => (
            Math.Round((decimal)r.NextDouble() * 500m, r.Next(0, 5)) + 0.0001m * r.Next(0, 2),
            Math.Round((decimal)r.NextDouble() * 5000m, r.Next(0, 5)),
            r.Next(0, 3) == 0 ? 0m : Math.Round((decimal)r.NextDouble() * 40m, r.Next(0, 3)),
            taxRates[r.Next(taxRates.Length)]));

        _output.WriteLine($"5000 invoices: refused {refused}; per-line rounding would have refused {perLine}.");
        refused.Should().Be(0);
        perLine.Should().BeGreaterThan(0, "the set must include invoices whose rounding is sensitive, or it proves nothing");
    }

    [Fact]
    public void For_everyday_invoices_the_header_discount_is_zero_and_none_is_refused()
    {
        // Whole quantities, two-decimal prices, whole-percent discounts: what a sale order usually carries.
        decimal[] taxRates = [0m, 5m, 16m, 17m, 18m];
        var (refused, perLine) = CheckMany(930, 5000, r => (
            (decimal)r.Next(1, 200),
            r.Next(1, 500_000) / 100m,
            r.Next(0, 2) == 0 ? 0m : r.Next(1, 26),
            taxRates[r.Next(taxRates.Length)]));

        _output.WriteLine($"5000 everyday invoices: refused {refused}; per-line rounding would have refused {perLine}.");
        refused.Should().Be(0);
    }

    [Fact]
    public void Sending_the_discount_amount_as_a_header_discount_would_discount_every_line_twice()
    {
        // What the plan's table used to say (HeaderDiscountAmount = DiscountAmount). DiscountAmount is already
        // the sum of the line discounts, and the gateway takes those off the lines itself.
        var invoice = Built((100m, 40m, 10m, 5m));
        var payload = SalesInvoicePayloadFactory.Build(invoice);

        payload.HeaderDiscountAmount.Should().Be(0m);
        GatewayDifference(payload).Should().Be(0m);

        payload.HeaderDiscountAmount = invoice.DiscountAmount;
        GatewayDifference(payload).Should().Be(-400m);
    }

    [Fact]
    public void Lines_that_each_round_up_pass_with_no_header_discount()
    {
        // Five lines of 1 × 1.005: each rounds to 1.01 (5.05 line by line); the invoice rounds 5.025 once, to
        // 5.03. Per-line rounding was two cents out; rounding once, as SCM does, it is exact.
        var invoice = Built(Enumerable.Repeat((1m, 1.005m, 0m, 0m), 5).ToArray());
        invoice.GrandTotal.Should().Be(5.03m);

        var payload = SalesInvoicePayloadFactory.Build(invoice);

        payload.HeaderDiscountAmount.Should().Be(0m);
        GatewayDifference(payload).Should().Be(0m);
        PerLineRoundingDifference(payload).Should().Be(0.02m);
    }

    [Theory]
    [InlineData(3, -0.01)]
    [InlineData(6, -0.02)]
    public void Lines_that_each_round_down_pass_with_no_header_discount(int lineCount, double perLineWasOutBy)
    {
        // n lines of 1 × 0.333: each rounds to 0.33; the invoice rounds 0.333n once. Six of them were the
        // case per-line rounding could not pass at all (1.98 against 2.00); rounding once, it is exact.
        var invoice = Built(Enumerable.Repeat((1m, 0.333m, 0m, 0m), lineCount).ToArray());

        var payload = SalesInvoicePayloadFactory.Build(invoice);

        payload.HeaderDiscountAmount.Should().Be(0m);
        GatewayDifference(payload).Should().Be(0m);
        PerLineRoundingDifference(payload).Should().Be((decimal)perLineWasOutBy);
    }

    [Fact]
    public void A_discount_edited_above_the_lines_own_goes_as_the_header_discount()
    {
        // Not something SCM does today — but a stored discount beyond the lines' must reach QuickBooks.
        var invoice = Built((10m, 10m, 10m, 0m));   // subtotal 100, line discounts 10, total 90
        invoice.DiscountAmount = 15m;
        invoice.GrandTotal     = 85m;

        var payload = SalesInvoicePayloadFactory.Build(invoice);

        payload.HeaderDiscountAmount.Should().Be(5m);
        GatewayDifference(payload).Should().Be(0m);
    }

    [Fact]
    public void A_discount_edited_below_the_lines_own_is_never_sent_negative_and_the_gateway_sees_the_disagreement()
    {
        var invoice = Built((10m, 10m, 10m, 0m));   // line discounts 10
        invoice.DiscountAmount = 8m;
        invoice.GrandTotal     = 92m;

        var payload = SalesInvoicePayloadFactory.Build(invoice);

        payload.HeaderDiscountAmount.Should().Be(0m, "the gateway refuses a negative header discount");
        GatewayDifference(payload).Should().Be(-2m, "the stored total does not match the invoice's own lines, and that is refused, not hidden");
    }

    [Fact]
    public void The_check_still_catches_a_stored_grand_total_that_disagrees_with_the_lines()
    {
        // The header discount is never derived from GrandTotal, so a corrupt total is not absorbed into it.
        var invoice = Built((10m, 10m, 0m, 0m));
        invoice.GrandTotal += 5m;

        var payload = SalesInvoicePayloadFactory.Build(invoice);

        payload.HeaderDiscountAmount.Should().Be(0m);
        GatewayDifference(payload).Should().Be(-5m);
    }
}

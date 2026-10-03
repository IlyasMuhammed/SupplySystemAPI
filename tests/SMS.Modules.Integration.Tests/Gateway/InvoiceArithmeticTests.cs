using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway.Validation;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Gateway;

/// <summary>
/// Totals the way SCM computes them (SalesInvoiceTotals.Header: rounded once over the invoice) must
/// validate, and what goes to QuickBooks (lines at per-line-rounded gross + one discount line) must
/// land on the caller's grand total.
/// </summary>
public class InvoiceArithmeticTests : IAsyncLifetime
{
    private static readonly decimal[] Discounts = [5m, 7.5m, 10m, 12.5m, 15m, 20m, 33.33m];
    private static readonly decimal[] Taxes     = [0m, 5m, 17m, 18m];

    private readonly SyncHarness _h = new();
    private IntegrationConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = await _h.ConnectAsync();
        await using var db = _h.DbAs(_h.OrgId);
        db.TaxCodeMappings.Add(new TaxCodeMapping { ConnectionId = _connection.Id, TaxPercent = 5m,  QboTaxCodeId = "TAX5" });
        db.TaxCodeMappings.Add(new TaxCodeMapping { ConnectionId = _connection.Id, TaxPercent = 18m, QboTaxCodeId = "TAX18" });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    /// <summary>A random invoice priced like SCM's: quantities from 1 in quarters, prices to 4 places, real discounts.</summary>
    private static SalesInvoicePayload RandomInvoice(Random random, int n)
    {
        var lines = Enumerable.Range(0, random.Next(1, 6)).Select(i => new TestPayloads.Line(
            Item:     $"I-{i}",
            Qty:      1m + random.Next(0, 80) / 4m,
            Price:    Math.Round(1m + (decimal)random.NextDouble() * 999m, random.Next(2, 5)),
            Discount: random.Next(2) == 0 ? 0m : Discounts[random.Next(Discounts.Length)],
            Tax:      Taxes[random.Next(Taxes.Length)])).ToArray();

        return TestPayloads.Invoice(id: $"SI-{n}", doc: $"INV-{n}", lines: lines);
    }

    [Fact]
    public async Task Thousands_of_invoices_totalled_like_SCM_all_validate_and_discounted_ones_land_exactly_on_the_total()
    {
        var random = new Random(20260930);
        const int count = 3000;

        var (invalid, discounted, inexact) = await _h.Scoped(async sp =>
        {
            var db        = sp.GetRequiredService<IntegrationDbContext>();
            var connection = await db.Connections.SingleAsync();
            var settings  = await db.Settings.SingleAsync();
            var validator = sp.GetRequiredService<IPayloadValidator>();
            var builder   = sp.GetRequiredService<IQboObjectBuilder>();

            var invalidList = new List<string>();
            var discountedCount = 0;
            var inexactList = new List<string>();

            for (var n = 0; n < count; n++)
            {
                var p   = RandomInvoice(random, n);
                var map = new EntityMap { ConnectionId = connection.Id, OrganizationId = _h.OrgId, Kind = SyncKind.SalesInvoice, ExternalId = p.ExternalId };

                var result = await validator.ValidateAsync(SyncKind.SalesInvoice, p, map, connection, settings);
                if (!result.IsValid)
                    invalidList.Add($"{p.ExternalId}: {string.Join("; ", result.Errors.Select(e => e.Message))}");

                if (!p.Lines.Any(l => l.DiscountPercent > 0)) continue;
                discountedCount++;

                var invoice = (RemoteInvoice)(await builder.BuildAsync(map, p, connection, settings, dryRun: true)).Entity;
                var quickBooksTotal = invoice.Lines.Sum(l => l.Amount) - invoice.DiscountAmount + p.ExpectedTaxAmount;
                if (quickBooksTotal != p.ExpectedTotal)
                    inexactList.Add($"{p.ExternalId}: QuickBooks {quickBooksTotal} vs {p.ExpectedTotal}");
                if (invoice.Lines.Any(l => l.Amount != SyncPayloads.Money(l.Quantity * l.UnitPrice)))
                    inexactList.Add($"{p.ExternalId}: a line amount is not Qty × UnitPrice");
            }

            return (invalidList, discountedCount, inexactList);
        });

        invalid.Should().BeEmpty();
        discounted.Should().BeGreaterThan(count / 3, "the sample must exercise discounts");
        inexact.Should().BeEmpty();
    }

    [Fact]
    public void The_old_per_line_rounding_would_have_refused_real_invoices()
    {
        // Two lines whose per-line nets round up but whose sum rounds once: SCM's total is a cent lower.
        var p = TestPayloads.Invoice(lines:
        [
            new TestPayloads.Line("I-1", 1, 10.005m, Discount: 0m, Tax: 0m),
            new TestPayloads.Line("I-2", 1, 10.005m, Discount: 0m, Tax: 0m),
            new TestPayloads.Line("I-3", 1, 10.005m, Discount: 0m, Tax: 0m)
        ]);

        p.ExpectedTotal.Should().Be(30.02m, "round(30.015) once");
        var perLine = p.Lines.Sum(l => SyncPayloads.Money(l.Quantity * l.UnitPrice));
        perLine.Should().Be(30.03m);

        InvoiceMath.Of(p).AddsUp(p).Should().BeTrue();
    }

    [Fact]
    public async Task No_discount_means_no_discount_line_even_when_per_line_rounding_leaves_a_cent_which_is_flagged()
    {
        var p = TestPayloads.Invoice(lines:
        [
            new TestPayloads.Line("I-1", 1, 10.005m, Tax: 0m),
            new TestPayloads.Line("I-2", 1, 10.005m, Tax: 0m),
            new TestPayloads.Line("I-3", 1, 10.005m, Tax: 0m)
        ]);
        await _h.UpdateSettingsAsync(s => s.DiscountAccountId = null);

        var built = await Build(p);
        var invoice = (RemoteInvoice)built.Entity;

        invoice.DiscountAmount.Should().Be(0m);
        invoice.DiscountAccountId.Should().BeNull();
        built.Warnings.Should().ContainSingle(w => w.Contains("0.01") && w.Contains("rounds each line"));
        (await Validate(p)).IsValid.Should().BeTrue("no discount line is sent, so no discount account is needed");
    }

    [Fact]
    public async Task A_discount_that_leaves_a_negative_residue_sends_no_discount_line_and_is_flagged()
    {
        // Per-line gross rounds down (0.33 × 3 = 0.99) while SCM's subtotal rounds once to 1.00, and the
        // 0.1% discount rounds away to 0.00: the residue is −0.01.
        var p = TestPayloads.Invoice(lines:
        [
            new TestPayloads.Line("I-1", 1, 0.334m, Discount: 0.1m, Tax: 0m),
            new TestPayloads.Line("I-2", 1, 0.334m, Tax: 0m),
            new TestPayloads.Line("I-3", 1, 0.334m, Tax: 0m)
        ]);
        await _h.UpdateSettingsAsync(s => s.DiscountAccountId = null);

        var math = InvoiceMath.Of(p);
        math.HasDiscount.Should().BeTrue();
        math.DiscountLine.Should().Be(0m);
        math.QuickBooksDifference.Should().Be(-0.01m);

        var built = await Build(p);
        ((RemoteInvoice)built.Entity).DiscountAmount.Should().Be(0m);
        built.Warnings.Should().ContainSingle(w => w.Contains("-0.01"));
        (await Validate(p)).IsValid.Should().BeTrue("a discount line of zero is not sent, so no account is needed");
    }

    [Fact]
    public async Task A_header_discount_is_part_of_the_discount_line()
    {
        var p = TestPayloads.Invoice(headerDiscount: 12.34m, lines: [new TestPayloads.Line("I-1", 3, 45.5m, Tax: 17m)]);

        var invoice = (RemoteInvoice)(await Build(p)).Entity;

        invoice.DiscountAmount.Should().Be(12.34m);
        (invoice.Lines.Sum(l => l.Amount) - invoice.DiscountAmount + p.ExpectedTaxAmount).Should().Be(p.ExpectedTotal);
    }

    [Fact]
    public async Task Bill_totals_allow_half_a_cent_per_line_as_a_warning_and_refuse_beyond()
    {
        var withinTolerance = TestPayloads.Bill();               // 3 lines → tolerance 0.025
        withinTolerance.ExpectedTotal += 0.02m;
        var ok = await Validate(withinTolerance, SyncKind.Bill);
        ok.IsValid.Should().BeTrue();
        ok.Warnings.Should().ContainSingle(w => w.Contains("0.02") && w.Contains("rounding"));

        var exact = await Validate(TestPayloads.Bill(), SyncKind.Bill);
        exact.Warnings.Should().BeEmpty();

        var beyond = TestPayloads.Bill();
        beyond.ExpectedTotal += 0.03m;
        (await Validate(beyond, SyncKind.Bill)).Errors.Should().ContainSingle(e => e.Code == "TOTAL_MISMATCH");

        PayloadValidator.BillTolerance(1).Should().Be(0.015m);
        PayloadValidator.BillTolerance(10).Should().Be(0.06m);
    }

    private Task<BuiltRemoteEntity> Build(SalesInvoicePayload p) =>
        _h.Scoped(async sp =>
        {
            var db = sp.GetRequiredService<IntegrationDbContext>();
            var map = new EntityMap { ConnectionId = _connection.Id, OrganizationId = _h.OrgId, Kind = SyncKind.SalesInvoice, ExternalId = p.ExternalId };
            return await sp.GetRequiredService<IQboObjectBuilder>().BuildAsync(map, p, await db.Connections.SingleAsync(), await db.Settings.SingleAsync(), dryRun: true);
        });

    private Task<PayloadValidationResult> Validate(object p, SyncKind kind = SyncKind.SalesInvoice) =>
        _h.Scoped(async sp =>
        {
            var db = sp.GetRequiredService<IntegrationDbContext>();
            var map = new EntityMap { ConnectionId = _connection.Id, OrganizationId = _h.OrgId, Kind = kind, ExternalId = "X" };
            return await sp.GetRequiredService<IPayloadValidator>().ValidateAsync(kind, p, map, await db.Connections.SingleAsync(), await db.Settings.SingleAsync());
        });
}

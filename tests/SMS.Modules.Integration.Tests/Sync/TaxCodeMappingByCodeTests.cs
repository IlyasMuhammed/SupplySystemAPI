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

namespace SMS.Modules.Integration.Tests.Sync;

/// <summary>
/// Plan S-11: QuickBooks tax mapping by the caller's tax code first, then by rate. The harness company maps
/// 0% → TAX0 and 17% → TAX17 as percent rows, and TAX-PUR is the default purchase tax code.
/// </summary>
public class TaxCodeMappingByCodeTests : IAsyncLifetime
{
    private readonly SyncHarness _h = new();
    private IntegrationConnection _connection = null!;

    public async Task InitializeAsync() => _connection = await _h.ConnectAsync();
    public async Task DisposeAsync() => await _h.DisposeAsync();

    private async Task MapCodeAsync(string code, decimal rate, string qboId)
    {
        await using var db = _h.DbAs(_h.OrgId);
        db.TaxCodeMappings.Add(new TaxCodeMapping { ConnectionId = _connection.Id, SourceTaxCode = code, TaxPercent = rate, QboTaxCodeId = qboId });
        await db.SaveChangesAsync();
    }

    private async Task MapRateAsync(decimal rate, string qboId)
    {
        await using var db = _h.DbAs(_h.OrgId);
        db.TaxCodeMappings.Add(new TaxCodeMapping { ConnectionId = _connection.Id, TaxPercent = rate, QboTaxCodeId = qboId });
        await db.SaveChangesAsync();
    }

    private Task<PayloadValidationResult> Validate(SyncKind kind, object payload) =>
        _h.Scoped(async sp =>
        {
            var db = sp.GetRequiredService<IntegrationDbContext>();
            var map = new EntityMap { ConnectionId = _connection.Id, Kind = kind, ExternalId = "NEW", SourceSystem = QuickBooksSourceSystems.Scm };
            return await sp.GetRequiredService<IPayloadValidator>()
                .ValidateAsync(kind, payload, map, await db.Connections.SingleAsync(), await db.Settings.SingleAsync());
        });

    private Task<BuiltRemoteEntity> Build(SyncKind kind, object payload) =>
        _h.Scoped(async sp =>
        {
            var db = sp.GetRequiredService<IntegrationDbContext>();
            var map = new EntityMap { ConnectionId = _connection.Id, OrganizationId = _h.OrgId, Kind = kind, ExternalId = "NEW", SourceSystem = QuickBooksSourceSystems.Scm };
            return await sp.GetRequiredService<IQboObjectBuilder>()
                .BuildAsync(map, payload, await db.Connections.SingleAsync(), await db.Settings.SingleAsync(), dryRun: true);
        });

    private static SalesInvoicePayload Invoice(params (string? Code, decimal Rate)[] lines)
    {
        var p = TestPayloads.Invoice(lines: lines.Select((l, i) => new TestPayloads.Line($"I-{i + 1}", 1, 100m, Tax: l.Rate)).ToArray());
        for (var i = 0; i < lines.Length; i++) p.Lines[i].TaxCode = lines[i].Code;
        return p;
    }

    private static List<string> TaxErrors(PayloadValidationResult r, string field) =>
        r.Errors.Where(e => e.Code == "TAX_UNMAPPED" && e.Field == field).Select(e => e.Message).ToList();

    // ── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_line_with_a_mapped_code_passes()
    {
        await MapCodeAsync("GST17", 17m, "TAX-GST");

        (await Validate(SyncKind.SalesInvoice, Invoice(("GST17", 17m)))).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_line_with_an_unmapped_code_is_refused_naming_the_code_even_when_its_rate_is_mapped()
    {
        // 17% is mapped as a percent row — but the line names a code, and the code is what decides.
        var r = await Validate(SyncKind.SalesInvoice, Invoice(("GST17", 17m), ("EXEMPT", 0m)));

        r.IsValid.Should().BeFalse();
        var message = TaxErrors(r, "lines.taxCode").Should().ContainSingle().Subject;
        message.Should().Contain("SMS tax codes EXEMPT, GST17").And.Contain("Mappings → Tax");
        TaxErrors(r, "lines.taxPercent").Should().BeEmpty("coded lines are never checked by rate");
    }

    [Fact]
    public async Task Codes_match_whatever_their_case_and_spacing()
    {
        await MapCodeAsync("GST17", 17m, "TAX-GST");

        (await Validate(SyncKind.SalesInvoice, Invoice((" gst17 ", 17m)))).IsValid.Should().BeTrue();
        var built = (RemoteInvoice)(await Build(SyncKind.SalesInvoice, Invoice((" gst17 ", 17m)))).Entity;
        built.Lines.Single().TaxCodeId.Should().Be("TAX-GST");
    }

    [Fact]
    public async Task A_blank_code_is_no_code_and_the_rate_decides()
    {
        var r = await Validate(SyncKind.SalesInvoice, Invoice(("  ", 17m), ("", 0m)));

        r.IsValid.Should().BeTrue("17% and 0% are mapped as percent rows");
        ((RemoteInvoice)(await Build(SyncKind.SalesInvoice, Invoice(("  ", 17m)))).Entity).Lines.Single().TaxCodeId.Should().Be("TAX17");
    }

    [Fact]
    public async Task Unmapped_codes_and_unmapped_rates_are_reported_separately()
    {
        var r = await Validate(SyncKind.SalesInvoice, Invoice(("VAT5", 5m), (null, 12.5m), (null, 17m)));

        TaxErrors(r, "lines.taxCode").Should().ContainSingle().Which.Should().Contain("SMS tax code VAT5").And.NotContain("12.5%");
        TaxErrors(r, "lines.taxPercent").Should().ContainSingle().Which.Should().Contain("tax rate 12.5%").And.NotContain("VAT5").And.NotContain("17%");
    }

    [Fact]
    public async Task A_code_rows_rate_never_maps_a_bare_rate()
    {
        // RED5's mapping stores 5% for information only. A code-less 5% line still needs a 5% percent row.
        await MapCodeAsync("RED5", 5m, "TAX-RED");

        var r = await Validate(SyncKind.SalesInvoice, Invoice((null, 5m)));

        TaxErrors(r, "lines.taxPercent").Should().ContainSingle().Which.Should().Contain("5%");
        ((RemoteInvoice)(await Build(SyncKind.SalesInvoice, Invoice((null, 5m)))).Entity).Lines.Single().TaxCodeId.Should().BeNull();
    }

    [Fact]
    public async Task A_coded_line_whose_code_is_unmapped_never_borrows_its_rates_mapping_even_in_the_builder()
    {
        // Review: the builder fell back to the rate's percent row (TAX17) for a coded line whose code is not mapped —
        // the very thing S-11 exists to prevent (two codes can share a rate). Validation refuses such a line first,
        // so it was latent; the builder now agrees with the validator instead of relying on it.
        var built = (RemoteInvoice)(await Build(SyncKind.SalesInvoice, Invoice(("GST17", 17m)))).Entity;

        built.Lines.Single().TaxCodeId.Should().BeNull("not TAX17");
    }

    [Fact]
    public async Task A_code_longer_than_a_mapping_can_hold_is_refused_as_such()
    {
        var code = new string('X', 21);

        var r = await Validate(SyncKind.SalesInvoice, Invoice((code, 17m)));

        r.Errors.Should().ContainSingle(e => e.Code == "TAX_CODE_TOO_LONG" && e.Message.Contains("21 characters"));
        TaxErrors(r, "lines.taxCode").Should().BeEmpty("it could never be mapped, so 'map it' would be bad advice");
    }

    // ── Building ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_code_mapping_wins_over_the_percent_mapping_for_the_same_rate()
    {
        await MapCodeAsync("GST17", 17m, "TAX-GST");

        var built = (RemoteInvoice)(await Build(SyncKind.SalesInvoice, Invoice(("GST17", 17m)))).Entity;

        built.Lines.Single().TaxCodeId.Should().Be("TAX-GST", "not TAX17, the 17% percent row");
    }

    [Fact]
    public async Task Two_codes_sharing_a_rate_go_to_their_own_QuickBooks_codes()
    {
        await MapCodeAsync("EXEMPT", 0m, "TAX-EX");
        await MapCodeAsync("ZERO", 0m, "TAX-ZR");

        var built = (RemoteInvoice)(await Build(SyncKind.SalesInvoice, Invoice(("EXEMPT", 0m), ("ZERO", 0m), (null, 0m)))).Entity;

        built.Lines.Select(l => l.TaxCodeId).Should().Equal("TAX-EX", "TAX-ZR", "TAX0");
    }

    [Fact]
    public async Task Old_and_new_lines_on_one_invoice_each_map_their_own_way()
    {
        await MapCodeAsync("GST17", 17m, "TAX-GST");
        var p = Invoice(("GST17", 17m), (null, 17m), (null, 0m));

        (await Validate(SyncKind.SalesInvoice, p)).IsValid.Should().BeTrue();
        ((RemoteInvoice)(await Build(SyncKind.SalesInvoice, p)).Entity).Lines.Select(l => l.TaxCodeId)
            .Should().Equal("TAX-GST", "TAX17", "TAX0");
    }

    // ── Bills (D-10 + S-11) ──────────────────────────────────────────────────

    /// <summary>A supplier invoice with a header purchase tax code: BillPayloadFactory puts the code and rate on every line.</summary>
    private static BillPayload BillWithHeaderCode(string code, decimal rate)
    {
        var p = TestPayloads.Bill(lines:
        [
            new BillLinePayload { LineNo = 1, ItemExternalId = "I-1", Category = BillLineCategory.Goods, Quantity = 10, UnitPrice = 60m, Amount = 600m },
            new BillLinePayload { LineNo = 2, Category = BillLineCategory.Freight, Amount = 50m },
            new BillLinePayload { LineNo = 3, Category = BillLineCategory.Other, Amount = 25m }
        ]);
        foreach (var line in p.Lines) { line.TaxCode = code; line.TaxPercent = rate; }
        p.ExpectedTaxAmount = SyncPayloads.Money(675m * rate / 100m);
        p.ExpectedTotal     = 675m + p.ExpectedTaxAmount;
        return p;
    }

    [Fact]
    public async Task A_bills_header_code_maps_every_line_and_the_default_purchase_code_is_not_used()
    {
        await MapCodeAsync("PGST17", 17m, "TAX-PGST");
        var p = BillWithHeaderCode("PGST17", 17m);

        (await Validate(SyncKind.Bill, p)).IsValid.Should().BeTrue();
        var bill = (RemoteBill)(await Build(SyncKind.Bill, p)).Entity;
        bill.Lines.Should().HaveCount(3).And.OnlyContain(l => l.TaxCodeId == "TAX-PGST");
    }

    [Fact]
    public async Task A_bills_unmapped_header_code_is_refused()
    {
        var r = await Validate(SyncKind.Bill, BillWithHeaderCode("PGST17", 17m));

        TaxErrors(r, "lines.taxCode").Should().ContainSingle().Which.Should().Contain("PGST17");
    }

    [Fact]
    public async Task Bill_lines_map_by_code_then_by_rate_and_fall_back_to_the_default_only_with_neither()
    {
        await MapCodeAsync("PGST17", 17m, "TAX-PGST");
        var p = TestPayloads.Bill(lines:
        [
            new BillLinePayload { LineNo = 1, Category = BillLineCategory.Other, Amount = 100m, TaxCode = "PGST17", TaxPercent = 17m },
            new BillLinePayload { LineNo = 2, Category = BillLineCategory.Other, Amount = 100m, TaxPercent = 17m },
            new BillLinePayload { LineNo = 3, Category = BillLineCategory.Other, Amount = 100m }
        ]);

        (await Validate(SyncKind.Bill, p)).IsValid.Should().BeTrue();
        ((RemoteBill)(await Build(SyncKind.Bill, p)).Entity).Lines.Select(l => l.TaxCodeId)
            .Should().Equal("TAX-PGST", "TAX17", "TAX-PUR");
    }

    // ── Through the gateway and the executor ─────────────────────────────────

    [Fact]
    public async Task A_coded_invoice_is_blocked_until_its_code_is_mapped_then_goes_with_that_code()
    {
        await _h.UpdateSettingsAsync(s => s.Mode = SyncMode.DryRun);
        await using (var db = _h.DbAs(_h.OrgId))
        {
            // Its customer and item are already in QuickBooks.
            foreach (var (kind, id) in new[] { (SyncKind.Customer, "C-1"), (SyncKind.Item, "I-1") })
                db.EntityMaps.Add(new EntityMap
                {
                    ConnectionId = _connection.Id, Kind = kind, ExternalId = id, SourceSystem = QuickBooksSourceSystems.Scm,
                    DisplayLabel = id, RemoteId = "R-" + id, RemoteSyncToken = "0", State = SyncState.Synced
                });
            await db.SaveChangesAsync();
        }
        var p = Invoice(("GST17", 17m));

        var first = await _h.Send(p);
        first.Outcome.Should().Be(GatewayOutcome.Invalid);
        (await _h.MapAsync(SyncKind.SalesInvoice, "SI-1")).Should().Match<EntityMap>(m => m.State == SyncState.Blocked && m.LastErrorCode == "TAX_UNMAPPED");

        await MapCodeAsync("GST17", 17m, "TAX-GST");
        (await _h.Send(p)).Outcome.Should().NotBe(GatewayOutcome.Invalid);
        await _h.DrainAsync();

        var map = await _h.MapAsync(SyncKind.SalesInvoice, "SI-1");
        map.State.Should().Be(SyncState.DryRunOk);
        (await _h.LogAsync(map.Id)).Last().RequestJson.Should().Contain("\"taxCodeId\":\"TAX-GST\"");
    }
}

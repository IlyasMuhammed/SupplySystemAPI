using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Connections;
using SMS.Shared.Exceptions;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Setup;

/// <summary>
/// Plan S-11 on the mapping screen's API: GET lists SMS tax codes (seen on stored lines, or mapped) and then
/// bare rates (seen on lines without a code, or mapped); PUT saves both kinds. Reference tax codes: 10 =
/// "GST 17%", 11 = "Exempt", 12 inactive.
/// </summary>
public class TaxCodeMappingSettingsTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static async Task<T> As<T>(ConnectionsHarness h, Func<IIntegrationSettingsService, Task<T>> act)
    {
        await using var scope = h.Scope(Org);
        return await act(scope.ServiceProvider.GetRequiredService<IIntegrationSettingsService>());
    }

    private static async Task<IntegrationConnection> ReadyAsync(ConnectionsHarness h)
    {
        var c = await h.SeedConnectionAsync(Org, ConnectionStatus.NeedsSetup);
        await SetupTestKit.SeedReferenceAsync(h, c);
        return c;
    }

    private static SaveTaxCodeMappingsRequest Save(params (string? Code, decimal Rate, string? Qbo)[] items) =>
        new() { Mappings = items.Select(i => new TaxCodeMappingItem { SourceTaxCode = i.Code, TaxPercent = i.Rate, QboTaxCodeId = i.Qbo }).ToList() };

    private static SalesInvoicePayload Invoice(params (string? Code, decimal Rate)[] lines)
    {
        var p = SetupTestKit.Invoice("PKR", lines.Select(l => l.Rate).ToArray());
        for (var i = 0; i < lines.Length; i++) p.Lines[i].TaxCode = lines[i].Code;
        return p;
    }

    // ── GET ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Codes_seen_are_listed_first_with_their_rate_and_count_then_rates_seen_only_on_lines_without_a_code()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await ReadyAsync(h);
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.SalesInvoice, "i-1", Invoice(("GST17", 17m), ("GST17", 17m), (null, 17m)));
        // A code whose rate changed: most lines carry 18%, so that is the rate shown. Stored camelCase, lower case — still read.
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.SalesInvoice, "i-2", Invoice(("gst17", 18m), ("GST17", 18m), ("GST17", 18m)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var bill = SetupTestKit.Bill("PKR", 5m, 5m, null);
        bill.Lines[0].TaxCode = "PST5";
        bill.Lines[1].TaxCode = "PST5";
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.Bill, "b-1", bill);

        var rows = await As(h, s => s.GetTaxMappingsAsync());

        rows.Select(r => (r.SourceTaxCode, r.TaxPercent, r.TimesSeen)).Should().Equal(
            ("GST17", 18m, 5),
            ("PST5", 5m, 2),
            ((string?)null, 17m, 1));
        rows.Should().OnlyContain(r => r.QboTaxCodeId == null);
    }

    [Fact]
    public async Task An_unmapped_code_is_offered_the_QuickBooks_code_its_rate_is_mapped_to_but_never_given_it()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await ReadyAsync(h);
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.SalesInvoice, "i-1", Invoice(("GST17", 17m), ("EXEMPT", 0m)));
        await As(h, s => s.SaveTaxMappingsAsync(Save((null, 17m, "10")), 7));

        var rows = await As(h, s => s.GetTaxMappingsAsync());

        var gst = rows.Single(r => r.SourceTaxCode == "GST17");
        gst.QboTaxCodeId.Should().BeNull("a suggestion is not a mapping");
        gst.SuggestedQboTaxCodeId.Should().Be("10");
        gst.SuggestedQboTaxCodeName.Should().Be("GST 17%");
        rows.Single(r => r.SourceTaxCode == "EXEMPT").SuggestedQboTaxCodeId.Should().BeNull("no 0% percent row");
        rows.Single(r => r.SourceTaxCode == null).Should().Match<TaxCodeMappingModel>(r => r.TaxPercent == 17m && r.QboTaxCodeId == "10" && r.TimesSeen == 0);
    }

    [Fact]
    public async Task Mapped_codes_appear_with_their_names_even_when_no_payload_uses_them()
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);

        await As(h, s => s.SaveTaxMappingsAsync(Save(("EXEMPT", 0m, "11"), ("GST17", 17m, "10"), (null, 17m, "10")), 7));
        var rows = await As(h, s => s.GetTaxMappingsAsync());

        rows.Should().BeEquivalentTo(new[]
        {
            new TaxCodeMappingModel { SourceTaxCode = "EXEMPT", TaxPercent = 0m,  QboTaxCodeId = "11", QboTaxCodeName = "Exempt" },
            new TaxCodeMappingModel { SourceTaxCode = "GST17",  TaxPercent = 17m, QboTaxCodeId = "10", QboTaxCodeName = "GST 17%" },
            new TaxCodeMappingModel { SourceTaxCode = null,     TaxPercent = 17m, QboTaxCodeId = "10", QboTaxCodeName = "GST 17%" }
        }, o => o.WithStrictOrdering());
    }

    // ── PUT ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Both_kinds_upsert_and_delete_in_one_audited_save_and_a_code_row_never_touches_a_percent_row()
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);
        await As(h, s => s.SaveTaxMappingsAsync(Save(("GST17", 17m, "10"), ("EXEMPT", 0m, "11"), (null, 17m, "10"), (null, 0m, "11")), 7));

        var rows = await As(h, s => s.SaveTaxMappingsAsync(Save(
            ("gst17", 17m, "11"),     // update, matched case-insensitively
            ("EXEMPT", 0m, ""),       // delete the code row…
            (null, 5m, "10")), 8));   // …add a percent row; the 17% and 0% percent rows are not mentioned and stay

        rows.Select(r => (r.SourceTaxCode, r.TaxPercent, r.QboTaxCodeId)).Should().Equal(
            ("GST17", 17m, "11"),
            ((string?)null, 0m, "11"),
            ((string?)null, 5m, "10"),
            ((string?)null, 17m, "10"));

        await using var db = h.OpenAs(Org);
        var stored = await db.TaxCodeMappings.OrderBy(m => m.Id).ToListAsync();
        stored.Should().HaveCount(4);
        stored.Single(m => m.SourceTaxCode != null).Should().Match<TaxCodeMapping>(m =>
            m.SourceTaxCode == "GST17" && m.QboTaxCodeId == "11" && m.ModifiedBy == 8);

        var audit = await db.SettingsAudit.Where(a => a.Area == "TaxMapping").OrderBy(a => a.Id).LastAsync();
        audit.UserId.Should().Be(8);
        audit.BeforeJson.Should().Contain("\"sourceTaxCode\":\"EXEMPT\"");
        audit.AfterJson.Should().NotContain("EXEMPT").And.Contain("\"sourceTaxCode\":\"GST17\"").And.Contain("\"sourceTaxCode\":null");
        JsonDocument.Parse(audit.AfterJson!).RootElement.GetArrayLength().Should().Be(4);
    }

    [Fact]
    public async Task A_code_is_stored_upper_case_with_its_rate_for_information()
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);

        await As(h, s => s.SaveTaxMappingsAsync(Save(("  gst17 ", 17.00004m, "10")), 7));

        await using var db = h.OpenAs(Org);
        var row = await db.TaxCodeMappings.SingleAsync();
        row.SourceTaxCode.Should().Be("GST17");
        row.TaxPercent.Should().Be(17m, "kept to four places, like a percent row");
    }

    [Fact]
    public async Task Deleting_a_code_that_was_never_mapped_is_a_no_op()
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);

        (await As(h, s => s.SaveTaxMappingsAsync(Save(("GST17", 17m, null)), 7))).Should().BeEmpty();
    }

    [Theory]
    [InlineData("GST17", "GST17")]
    [InlineData("gst17", "GST17 ")]
    public async Task A_code_twice_in_one_request_is_refused(string first, string second)
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);

        await FluentActions.Awaiting(() => As(h, s => s.SaveTaxMappingsAsync(Save((first, 17m, "10"), (second, 17m, "11")), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*GST17 appears more than once*");
    }

    [Fact]
    public async Task A_code_and_a_rate_with_the_same_percent_are_different_rows_not_duplicates()
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);

        var rows = await As(h, s => s.SaveTaxMappingsAsync(Save(("GST17", 17m, "10"), (null, 17m, "10"), ("ZERO", 0m, "11"), ("EXEMPT", 0m, "11")), 7));

        rows.Should().HaveCount(4);
    }

    [Fact]
    public async Task A_code_longer_than_twenty_characters_is_refused()
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);

        await FluentActions.Awaiting(() => As(h, s => s.SaveTaxMappingsAsync(Save((new string('C', 21), 17m, "10")), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*longer than 20 characters*");
    }

    [Theory]
    [InlineData("99", "is not one of QuickBooks' tax codes")]
    [InlineData("12", "is inactive")]
    public async Task A_code_rows_QuickBooks_code_must_exist_and_be_active(string qbo, string message)
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);

        await FluentActions.Awaiting(() => As(h, s => s.SaveTaxMappingsAsync(Save(("GST17", 17m, qbo)), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage($"*tax code for SMS tax code GST17*{message}*");
    }

    [Fact]
    public async Task An_empty_row_is_refused()
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);

        await FluentActions.Awaiting(() => As(h, s => s.SaveTaxMappingsAsync(new SaveTaxCodeMappingsRequest { Mappings = [null!] }, 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*row is empty*");
    }

    [Fact]
    public async Task Code_rows_are_tenant_scoped()
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);
        await As(h, s => s.SaveTaxMappingsAsync(Save(("GST17", 17m, "10")), 7));

        var other = Guid.NewGuid();
        var c2 = await h.SeedConnectionAsync(other, ConnectionStatus.NeedsSetup, realmId: "9130000000000002");
        await SetupTestKit.SeedReferenceAsync(h, c2);
        await using var scope = h.Scope(other);
        var service = scope.ServiceProvider.GetRequiredService<IIntegrationSettingsService>();

        (await service.GetTaxMappingsAsync()).Should().BeEmpty();
    }

    // ── SQL Server: the filtered unique indexes ─────────────────────────────────────────────

    [Fact]
    public async Task On_SQL_Server_code_rows_and_percent_rows_of_one_rate_coexist_and_a_code_is_unique_per_connection()
    {
        await using var sql = await LocalDb.CreateAsync();
        await using var h = ConnectionsHarness.Create(sql: sql);
        var c = await ReadyAsync(h);

        var rows = await As(h, s => s.SaveTaxMappingsAsync(Save(("GST17", 17m, "10"), (null, 17m, "10"), ("EXEMPT", 0m, "11"), ("ZERO", 0m, "11"), (null, 0m, "11")), 7));
        rows.Should().HaveCount(5);

        // Saving again updates in place.
        await As(h, s => s.SaveTaxMappingsAsync(Save(("gst17", 17m, "11"), (null, 17m, "11")), 7));
        await using (var db = h.OpenAs(Org))
            (await db.TaxCodeMappings.CountAsync()).Should().Be(5);

        // The database itself refuses a second row for a code (whatever its case) or a second percent row for a rate.
        foreach (var duplicate in new[]
                 {
                     new TaxCodeMapping { ConnectionId = c.Id, SourceTaxCode = "gst17", TaxPercent = 18m, QboTaxCodeId = "10" },
                     new TaxCodeMapping { ConnectionId = c.Id, SourceTaxCode = null, TaxPercent = 17m, QboTaxCodeId = "10" }
                 })
        {
            await using var db = h.OpenAs(Org);
            db.TaxCodeMappings.Add(duplicate);
            await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
        }
    }
}

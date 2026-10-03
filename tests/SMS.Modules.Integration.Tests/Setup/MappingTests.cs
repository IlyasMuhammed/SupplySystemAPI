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

public class MappingTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static async Task<T> As<T>(ConnectionsHarness h, Guid org, Func<IIntegrationSettingsService, Task<T>> act)
    {
        await using var scope = h.Scope(org);
        return await act(scope.ServiceProvider.GetRequiredService<IIntegrationSettingsService>());
    }

    private static async Task<IntegrationConnection> ReadyAsync(ConnectionsHarness h, Guid? org = null)
    {
        var c = await h.SeedConnectionAsync(org ?? OrgA, ConnectionStatus.NeedsSetup);
        await SetupTestKit.SeedReferenceAsync(h, c);
        return c;
    }

    private static SaveTaxCodeMappingsRequest Tax(params (decimal Rate, string? Code)[] items) =>
        new() { Mappings = items.Select(i => new TaxCodeMappingItem { TaxPercent = i.Rate, QboTaxCodeId = i.Code }).ToList() };

    private static SaveTermMappingsRequest Terms(params (string Id, string? Name, string? Term)[] items) =>
        new() { Mappings = items.Select(i => new TermMappingItem { PaymentTermExternalId = i.Id, PaymentTermName = i.Name, QboTermId = i.Term }).ToList() };

    // ── Tax: rates seen ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tax_mappings_list_the_rates_seen_in_invoices_and_bills_with_counts()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await ReadyAsync(h);
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.SalesInvoice, "i-1", SetupTestKit.Invoice("PKR", 17, 17, 0));
        // Stored with camelCase names, as a gateway using web defaults would — still read.
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.SalesInvoice, "i-2", SetupTestKit.Invoice("PKR", 5.0m),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.Bill, "b-1", SetupTestKit.Bill("PKR", 17, null));
        // A payload that is not an invoice at all is ignored; so is one that does not parse.
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.Customer, "c-1", new CustomerPayload { ExternalId = "c-1", DisplayName = "A" });
        await using (var db = h.OpenAs(OrgA))
        {
            db.EntityMaps.Add(new EntityMap { OrganizationId = OrgA, ConnectionId = c.Id, Kind = SyncKind.SalesInvoice, ExternalId = "bad", PayloadJson = "{not json" });
            await db.SaveChangesAsync();
        }

        var rows = await As(h, OrgA, s => s.GetTaxMappingsAsync());

        rows.Select(r => (r.TaxPercent, r.TimesSeen)).Should().Equal((0m, 1), (5m, 1), (17m, 3));
        rows.Should().OnlyContain(r => r.QboTaxCodeId == null);
    }

    [Fact]
    public async Task Stored_mappings_appear_with_their_code_name_even_when_no_payload_uses_them()
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);
        await As(h, OrgA, s => s.SaveTaxMappingsAsync(Tax((17m, "10"), (0m, "11")), 7));

        var rows = await As(h, OrgA, s => s.GetTaxMappingsAsync());

        rows.Should().BeEquivalentTo(new[]
        {
            new TaxCodeMappingModel { TaxPercent = 0m,  QboTaxCodeId = "11", QboTaxCodeName = "Exempt",  TimesSeen = 0 },
            new TaxCodeMappingModel { TaxPercent = 17m, QboTaxCodeId = "10", QboTaxCodeName = "GST 17%", TimesSeen = 0 }
        }, o => o.WithStrictOrdering());
    }

    // ── Tax: save ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Saving_upserts_updates_and_deletes_in_one_audited_change()
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);
        await As(h, OrgA, s => s.SaveTaxMappingsAsync(Tax((17m, "10"), (0m, "11"), (5m, "10")), 7));

        var rows = await As(h, OrgA, s => s.SaveTaxMappingsAsync(Tax((17m, "11"), (0m, ""), (5m, null), (8.5m, "10")), 8));

        rows.Select(r => (r.TaxPercent, r.QboTaxCodeId)).Should().Equal((8.5m, "10"), (17m, "11"));
        await using var db = h.OpenAs(OrgA);
        (await db.TaxCodeMappings.CountAsync()).Should().Be(2);
        var audits = await db.SettingsAudit.Where(a => a.Area == "TaxMapping").OrderBy(a => a.Id).ToListAsync();
        audits.Should().HaveCount(2);
        audits[1].UserId.Should().Be(8);
        audits[1].BeforeJson.Should().Contain("\"qboTaxCodeId\":\"11\"");
        JsonDocument.Parse(audits[1].AfterJson!).RootElement.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Rates_are_normalized_to_four_places()
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);

        await As(h, OrgA, s => s.SaveTaxMappingsAsync(Tax((17.000049m, "10")), 7));
        var rows = await As(h, OrgA, s => s.SaveTaxMappingsAsync(Tax((17.0000m, "11")), 7));

        rows.Should().ContainSingle().Which.Should().Match<TaxCodeMappingModel>(r => r.TaxPercent == 17m && r.QboTaxCodeId == "11");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100.01)]
    public async Task A_rate_out_of_range_is_refused(double rate)
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.SaveTaxMappingsAsync(Tax(((decimal)rate, "10")), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*out of range*");
    }

    [Fact]
    public async Task A_rate_twice_in_one_request_is_refused()
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.SaveTaxMappingsAsync(Tax((17m, "10"), (17.00m, "11")), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*17% appears more than once*");
    }

    [Theory]
    [InlineData("99", "is not one of QuickBooks' tax codes")]
    [InlineData("12", "is inactive")]
    public async Task A_tax_code_must_exist_and_be_active(string code, string message)
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.SaveTaxMappingsAsync(Tax((17m, code)), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage($"*tax code for 17%*{message}*");

        await using var db = h.OpenAs(OrgA);
        (await db.TaxCodeMappings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Tax_codes_cannot_be_checked_without_reference_data()
    {
        await using var h = ConnectionsHarness.Create();
        await h.SeedConnectionAsync(OrgA, ConnectionStatus.NeedsSetup);

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.SaveTaxMappingsAsync(Tax((17m, "10")), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*Refresh the reference data first*");
    }

    [Fact]
    public async Task Tax_mappings_are_tenant_scoped()
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h, OrgA);
        await ReadyAsync(h, OrgB);
        await As(h, OrgA, s => s.SaveTaxMappingsAsync(Tax((17m, "10")), 7));

        (await As(h, OrgB, s => s.GetTaxMappingsAsync())).Should().BeEmpty();
        (await As(h, OrgB, s => s.SaveTaxMappingsAsync(Tax((17m, "")), 7))).Should().BeEmpty();
        (await As(h, OrgA, s => s.GetTaxMappingsAsync())).Should().ContainSingle();
    }

    // ── Terms ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Term_mappings_list_the_terms_customers_and_vendors_use()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await ReadyAsync(h);
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.Customer, "c-1",
            new CustomerPayload { ExternalId = "c-1", DisplayName = "A", PaymentTermExternalId = "term-30" });
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.Vendor, "v-1",
            new VendorPayload { ExternalId = "v-1", DisplayName = "B", PaymentTermExternalId = "term-60" },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.Vendor, "v-2",
            new VendorPayload { ExternalId = "v-2", DisplayName = "C" });
        await As(h, OrgA, s => s.SaveTermMappingsAsync(Terms(("term-30", "Net 30 days", "20")), 7));

        var rows = await As(h, OrgA, s => s.GetTermMappingsAsync());

        rows.Should().HaveCount(2);
        rows.Should().ContainEquivalentOf(new TermMappingModel
        {
            PaymentTermExternalId = "term-30", PaymentTermName = "Net 30 days", QboTermId = "20", QboTermName = "Net 30"
        });
        rows.Should().ContainEquivalentOf(new TermMappingModel { PaymentTermExternalId = "term-60" });
    }

    [Fact]
    public async Task Term_mappings_upsert_and_delete()
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);
        await As(h, OrgA, s => s.SaveTermMappingsAsync(Terms(("t-1", "Thirty", "20"), ("t-2", "Sixty", "21")), 7));

        var rows = await As(h, OrgA, s => s.SaveTermMappingsAsync(Terms(("T-1", null, "21"), ("t-2", null, "")), 7));

        rows.Should().ContainSingle();
        rows[0].Should().BeEquivalentTo(new TermMappingModel
        {
            PaymentTermExternalId = "t-1", PaymentTermName = "Thirty", QboTermId = "21", QboTermName = "Net 60"
        }, "ids match case-insensitively and a missing name keeps the stored one");

        await using var db = h.OpenAs(OrgA);
        (await db.SettingsAudit.CountAsync(a => a.Area == "TermMapping")).Should().Be(2);
    }

    [Theory]
    [InlineData("99", "is not in the reference data")]
    [InlineData("22", "is inactive")]
    public async Task A_QuickBooks_term_must_exist_and_be_active(string term, string message)
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.SaveTermMappingsAsync(Terms(("t-1", "Thirty", term)), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage($"*{message}*");
    }

    [Fact]
    public async Task A_term_mapping_needs_the_SCM_term_id_once()
    {
        await using var h = ConnectionsHarness.Create();
        await ReadyAsync(h);

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.SaveTermMappingsAsync(Terms(("  ", null, "20")), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*needs the SCM payment term's id*");
        await FluentActions.Awaiting(() => As(h, OrgA, s => s.SaveTermMappingsAsync(Terms(("t", null, "20"), ("T", null, "21")), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*appears more than once*");
        await FluentActions.Awaiting(() => As(h, OrgA, s => s.SaveTermMappingsAsync(Terms((new string('x', 101), null, "20")), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*longer than 100*");
    }

    [Fact]
    public async Task Mappings_need_a_connection()
    {
        await using var h = ConnectionsHarness.Create();

        (await As(h, OrgA, s => s.GetTermMappingsAsync())).Should().BeEmpty();
        (await As(h, OrgA, s => s.GetTaxMappingsAsync())).Should().BeEmpty();
        await FluentActions.Awaiting(() => As(h, OrgA, s => s.SaveTermMappingsAsync(Terms(("t", null, "20")), 7)))
            .Should().ThrowAsync<ConflictException>();
    }
}

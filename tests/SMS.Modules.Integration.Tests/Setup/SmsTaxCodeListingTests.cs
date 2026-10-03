using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Connections;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Setup;

/// <summary>
/// GET tax-mappings lists every active SMS tax code — sales and purchase, from Finance through SMS.Shared's
/// <see cref="ITaxCodeLookup"/> — so the accountant can map a code before any invoice or bill uses it, with the
/// code's name, usage and rate. Reference tax codes: 10 = "GST 17%", 11 = "Exempt", 12 inactive.
/// </summary>
public class SmsTaxCodeListingTests
{
    private static readonly Guid Org   = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private readonly Dictionary<Guid, List<TaxCodeInfo>> _codes = new()
    {
        [Org] =
        [
            TenantTaxCodes.Code("GST17",  "GST 17%",          17m, TaxCodeUsage.Sales, isDefault: true),
            TenantTaxCodes.Code("EXEMPT", "Exempt supplies",  0m,  TaxCodeUsage.Both),
            TenantTaxCodes.Code("PGST17", "Input GST 17%",    17m, TaxCodeUsage.Purchase),
            TenantTaxCodes.Code("OLD16",  "Old GST 16%",      16m, TaxCodeUsage.Sales, active: false)
        ],
        [Other] = [TenantTaxCodes.Code("SECRET", "Another organization's code", 5m, TaxCodeUsage.Sales)]
    };

    private ConnectionsHarness Harness(Func<IServiceProvider, ITaxCodeLookup>? lookup = null) =>
        ConnectionsHarness.Create(extra: s =>
            s.AddScoped(lookup ?? (sp => new TenantTaxCodes(sp.GetRequiredService<ITenantContext>(), _codes))));

    private static async Task<T> As<T>(ConnectionsHarness h, Func<IIntegrationSettingsService, Task<T>> act, Guid? org = null)
    {
        await using var scope = h.Scope(org ?? Org);
        return await act(scope.ServiceProvider.GetRequiredService<IIntegrationSettingsService>());
    }

    private static async Task<IntegrationConnection> ReadyAsync(ConnectionsHarness h, Guid? org = null, string realm = "9130000000000001")
    {
        var c = await h.SeedConnectionAsync(org ?? Org, ConnectionStatus.NeedsSetup, realmId: realm);
        await SetupTestKit.SeedReferenceAsync(h, c);
        return c;
    }

    [Fact]
    public async Task Every_active_SMS_tax_code_is_listed_once_with_its_name_usage_and_rate_before_any_document_uses_it()
    {
        await using var h = Harness();
        await ReadyAsync(h);

        var rows = await As(h, s => s.GetTaxMappingsAsync());

        rows.Select(r => (r.SourceTaxCode, r.SourceTaxCodeName, r.SourceTaxCodeUsage, r.TaxPercent, r.TimesSeen, r.QboTaxCodeId)).Should().Equal(
            ("EXEMPT", "Exempt supplies", "BOTH",     0m,  0, (string?)null),     // on both lists, listed once
            ("GST17",  "GST 17%",         "SALES",    17m, 0, null),
            ("PGST17", "Input GST 17%",   "PURCHASE", 17m, 0, null));
        rows.Should().NotContain(r => r.SourceTaxCode == "OLD16", "inactive codes are listed only when a document or mapping uses them");
    }

    [Fact]
    public async Task A_code_used_on_documents_and_known_to_SMS_is_one_row_with_both_its_count_and_its_name()
    {
        await using var h = Harness();
        var c = await ReadyAsync(h);
        var invoice = SetupTestKit.Invoice("PKR", 17m, 17m, 16m);
        invoice.Lines[0].TaxCode = "gst17";
        invoice.Lines[1].TaxCode = "GST17";
        invoice.Lines[2].TaxCode = "OLD16";
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.SalesInvoice, "i-1", invoice);

        var rows = await As(h, s => s.GetTaxMappingsAsync());

        rows.Single(r => r.SourceTaxCode == "GST17").Should().Match<TaxCodeMappingModel>(r => r.TimesSeen == 2 && r.SourceTaxCodeName == "GST 17%");
        rows.Single(r => r.SourceTaxCode == "OLD16").Should().Match<TaxCodeMappingModel>(r =>
            r.TimesSeen == 1 && r.SourceTaxCodeName == null && r.TaxPercent == 16m);
    }

    [Fact]
    public async Task An_unused_SMS_code_is_offered_the_mapping_of_its_SMS_rate_never_given_it()
    {
        await using var h = Harness();
        await ReadyAsync(h);
        await As(h, s => s.SaveTaxMappingsAsync(new SaveTaxCodeMappingsRequest
        {
            Mappings = [new TaxCodeMappingItem { TaxPercent = 17m, QboTaxCodeId = "10" }, new TaxCodeMappingItem { TaxPercent = 0m, QboTaxCodeId = "11" }]
        }, 7));

        var rows = await As(h, s => s.GetTaxMappingsAsync());

        var gst = rows.Single(r => r.SourceTaxCode == "GST17");
        gst.QboTaxCodeId.Should().BeNull();
        gst.SuggestedQboTaxCodeId.Should().Be("10", "its SMS rate is 17%, not 0%");
        rows.Single(r => r.SourceTaxCode == "EXEMPT").SuggestedQboTaxCodeId.Should().Be("11");
    }

    [Fact]
    public async Task Mapping_an_unused_SMS_code_keeps_its_name_on_the_row()
    {
        await using var h = Harness();
        await ReadyAsync(h);

        var rows = await As(h, s => s.SaveTaxMappingsAsync(new SaveTaxCodeMappingsRequest
        {
            Mappings = [new TaxCodeMappingItem { SourceTaxCode = "PGST17", TaxPercent = 17m, QboTaxCodeId = "10" }]
        }, 7));

        rows.Single(r => r.SourceTaxCode == "PGST17").Should().Match<TaxCodeMappingModel>(r =>
            r.QboTaxCodeId == "10" && r.QboTaxCodeName == "GST 17%" && r.SourceTaxCodeName == "Input GST 17%" && r.SourceTaxCodeUsage == "PURCHASE");
    }

    [Fact]
    public async Task Another_organizations_SMS_codes_never_show()
    {
        await using var h = Harness();
        await ReadyAsync(h);
        await ReadyAsync(h, Other, "9130000000000002");

        (await As(h, s => s.GetTaxMappingsAsync())).Should().NotContain(r => r.SourceTaxCode == "SECRET");
        (await As(h, s => s.GetTaxMappingsAsync(), Other)).Select(r => r.SourceTaxCode).Should().Equal("SECRET");
    }

    [Fact]
    public async Task If_SMS_cannot_answer_the_screen_still_lists_what_documents_and_mappings_hold()
    {
        await using var h = Harness(_ => new ThrowingTaxCodes());
        var c = await ReadyAsync(h);
        var invoice = SetupTestKit.Invoice("PKR", 17m);
        invoice.Lines[0].TaxCode = "GST17";
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.SalesInvoice, "i-1", invoice);

        var rows = await As(h, s => s.GetTaxMappingsAsync());

        rows.Should().ContainSingle(r => r.SourceTaxCode == "GST17" && r.SourceTaxCodeName == null && r.TimesSeen == 1);
    }

    private sealed class ThrowingTaxCodes : ITaxCodeLookup
    {
        public Task<TaxCodeInfo?> GetAsync(Guid uuid, CancellationToken ct = default) => throw new InvalidOperationException("Finance is down");
        public Task<IReadOnlyList<TaxCodeInfo>> ListActiveAsync(string side, CancellationToken ct = default) => throw new InvalidOperationException("Finance is down");
        public Task<TaxCodeInfo?> GetDefaultAsync(string side, CancellationToken ct = default) => throw new InvalidOperationException("Finance is down");
    }
}

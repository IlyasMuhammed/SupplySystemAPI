using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Modules.Integration.Tests.Setup;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.PostingControl;

/// <summary>A44 D-16 account mapping (A42 chart ↔ QuickBooks accounts) and D-18 tax import, T-45..T-49.</summary>
public class AccountAndTaxMappingTests : IAsyncLifetime
{
    private readonly FakeAccountMap _ledger = new();
    private readonly Dictionary<Guid, List<TaxCodeInfo>> _taxCodes = new();
    private readonly SyncHarness    _h;
    private IntegrationConnection   _connection = null!;

    public AccountAndTaxMappingTests() =>
        _h = PostingControlKit.Harness(_ledger, extra: s =>
            s.AddScoped<ITaxCodeLookup>(sp => new TenantTaxCodes(sp.GetRequiredService<ITenantContext>(), _taxCodes)));

    public async Task InitializeAsync() => _connection = await _h.ConnectAsync();
    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<T> Accounts<T>(Func<IQuickBooksAccountMappingService, Task<T>> call) =>
        _h.Scoped(sp => call(sp.GetRequiredService<IQuickBooksAccountMappingService>()));

    private Task<T> Settings<T>(Func<IIntegrationSettingsService, Task<T>> call) =>
        _h.Scoped(sp => call(sp.GetRequiredService<IIntegrationSettingsService>()));

    private async Task SeedAccountsAsync(params RemoteAccount[] accounts)
    {
        var reference = PostingControlKit.Reference();
        await PostingControlKit.SeedReferenceAsync(_h, _connection, new RemoteReferenceData
        {
            Accounts = accounts, TaxCodes = reference.TaxCodes, Terms = reference.Terms, Preferences = reference.Preferences
        });
    }

    private async Task SeedTaxCodesAsync(params RemoteTaxCode[] codes)
    {
        var reference = PostingControlKit.Reference();
        await PostingControlKit.SeedReferenceAsync(_h, _connection, new RemoteReferenceData
        {
            Accounts = reference.Accounts, TaxCodes = codes, Terms = reference.Terms, Preferences = reference.Preferences
        });
    }

    private void Sms(params (string Code, string Name, decimal Rate)[] codes) =>
        _taxCodes[_h.OrgId] = codes.Select(c => new TaxCodeInfo(Guid.NewGuid(), c.Code, c.Name, c.Rate, TaxCodeUsage.Both, IsDefault: false, IsActive: true)).ToList();

    private static RemoteAccount Account(string id, string name, string type = "Income", string? number = null, bool active = true) =>
        new(id, name, type, null, null, "PKR", active) { AcctNum = number };

    // ── Accounts ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Auto_map_uses_account_number_then_exact_name_then_normalized_name_and_only_when_unambiguous()
    {
        await SeedAccountsAsync(
            Account("1", "Product Sales", number: "4000"),
            Account("2", "Service income"),
            Account("3", "Freight-Out", "Expense"),
            Account("4", "Rent", "Expense"), Account("5", "RENT", "Expense"),
            Account("6", "Old sales", active: false));
        var byNumber = _ledger.Leaf("4000", "Sales of goods");
        var byName   = _ledger.Leaf("4100", "  service   INCOME ");
        var byLoose  = _ledger.Leaf("5200", "Freight Out");
        var twoRents = _ledger.Leaf("5300", "Rent");
        _ledger.Leaf("5400", "Old sales");                                   // only an inactive QuickBooks account matches
        var mapped   = _ledger.Leaf("5500", "Product Sales");
        _ledger.Mapped["5500"] = ("2", "Service income");                    // already mapped: left alone

        var result = await Accounts(a => a.AutoMapAsync(new AutoMapAccountsRequest(), 7));

        result.Matches.Select(m => (m.Code, m.QboAccountId, m.Rule)).Should().BeEquivalentTo(new[]
        {
            ("4000", "1", AccountMappingService.RuleAcctNum),
            ("4100", "2", AccountMappingService.RuleExactName),
            ("5200", "3", AccountMappingService.RuleNormalizedName)
        });
        result.Ambiguous.Should().ContainSingle().Which.Should().Match<AmbiguousAccountModel>(a => a.Code == "5300" && a.Candidates.Count == 2);
        result.Unmatched.Should().Be(1);
        _ledger.Mapped["5500"].Id.Should().Be("2");
        _ledger.Upserts.Should().OnlyContain(u => u.Note!.StartsWith("AUTO_MAPPED:"));
        result.Accounts.Mapped.Should().Be(4);
        result.Accounts.Unmapped.Should().Be(2);
        _ = (byNumber, byName, byLoose, twoRents, mapped);
    }

    [Fact]
    public async Task Saving_validates_the_QuickBooks_account_and_an_empty_one_removes_the_mapping()
    {
        await SeedAccountsAsync(Account("1", "Sales"), Account("6", "Old", active: false));
        var leaf = _ledger.Leaf("4000", "Sales");

        var saved = await Accounts(a => a.SaveAsync(new SaveAccountMappingsRequest { Mappings = [new AccountMappingItem { AccountUuid = leaf.AccountUuid, QboAccountId = "1" }] }, 7));
        saved.Accounts.Single().Should().Match<AccountMappingRowModel>(r => r.Status == "Mapped" && r.QboAccountName == "Sales");

        foreach (var (id, reason) in new[] { ("6", "inactive"), ("99", "not in the reference data") })
        {
            var act = async () => await Accounts(a => a.SaveAsync(new SaveAccountMappingsRequest { Mappings = [new AccountMappingItem { AccountUuid = leaf.AccountUuid, QboAccountId = id }] }, 7));
            (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain(reason);
        }

        var unknown = async () => await Accounts(a => a.SaveAsync(new SaveAccountMappingsRequest { Mappings = [new AccountMappingItem { AccountUuid = Guid.NewGuid(), QboAccountId = "1" }] }, 7));
        await unknown.Should().ThrowAsync<BadRequestException>();

        (await Accounts(a => a.SaveAsync(new SaveAccountMappingsRequest { Mappings = [new AccountMappingItem { AccountUuid = leaf.AccountUuid }] }, 7)))
            .Accounts.Single().Status.Should().Be("Unmapped");
    }

    [Fact]
    public async Task Without_a_general_ledger_the_screen_is_empty_and_saving_is_refused()
    {
        await SeedAccountsAsync(Account("1", "Sales"));

        (await Accounts(a => a.GetAsync())).GlAvailable.Should().BeFalse();
        var act = async () => await Accounts(a => a.AutoMapAsync(new AutoMapAccountsRequest(), 7));
        await act.Should().ThrowAsync<ConflictException>();
    }

    // ── Tax import (D-18) ────────────────────────────────────────────────────

    [Fact]
    public async Task T45_a_rate_only_one_QuickBooks_code_has_is_saved()
    {
        await SeedTaxCodesAsync(new RemoteTaxCode("10", "GST 17%", null, true, 17m, true), new RemoteTaxCode("12", "Old 16%", null, true, 16m, false));
        Sms(("GST17", "GST 17%", 17m), ("GST16", "GST 16%", 16m));

        var result = await Settings(s => s.ImportTaxMappingsAsync(7));

        result.SavedRows.Should().ContainSingle(r => r.SourceTaxCode == "GST17" && r.QboTaxCodeId == "10");
        result.NeedsReview.Should().ContainSingle(r => r.SourceTaxCode == "GST16" && r.Candidates.Count == 0, "an inactive code is never suggested");
        result.Mappings.Single(m => m.SourceTaxCode == "GST17").QboTaxCodeId.Should().Be("10");
    }

    [Fact]
    public async Task T46_several_codes_at_a_rate_are_narrowed_by_name_or_left_for_a_person()
    {
        await SeedTaxCodesAsync(
            new RemoteTaxCode("10", "GST 17", null, true, 17m, true),
            new RemoteTaxCode("11", "PST 17", null, true, 17m, true));
        Sms(("PST", "Provincial sales tax", 17m), ("VAT", "Value added", 17m));

        var result = await Settings(s => s.ImportTaxMappingsAsync(7));

        result.SavedRows.Should().ContainSingle(r => r.SourceTaxCode == "PST" && r.QboTaxCodeId == "11");
        result.NeedsReview.Should().ContainSingle(r => r.SourceTaxCode == "VAT").Which.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public async Task T47_zero_and_exempt_use_NON_when_QuickBooks_has_it()
    {
        await SeedTaxCodesAsync(new RemoteTaxCode("NON", "Non", null, false, null, true), new RemoteTaxCode("10", "GST 17", null, true, 17m, true));
        Sms(("EXEMPT", "Exempt supplies", 0m));

        var result = await Settings(s => s.ImportTaxMappingsAsync(7));

        result.SavedRows.Should().ContainSingle(r => r.SourceTaxCode == "EXEMPT" && r.QboTaxCodeId == "NON");
    }

    [Fact]
    public async Task T48_without_NON_the_zero_code_named_like_the_SMS_code_is_chosen()
    {
        await SeedTaxCodesAsync(
            new RemoteTaxCode("20", "Exempt", null, false, 0m, true),
            new RemoteTaxCode("21", "Zero-rated", null, false, 0m, true),
            new RemoteTaxCode("22", "Out of scope", null, false, 0m, true));
        Sms(("EX", "Exempt goods", 0m), ("ZR", "Zero rated exports", 0m), ("Z0", "Nothing", 0m));

        var result = await Settings(s => s.ImportTaxMappingsAsync(7));

        result.SavedRows.Select(r => (r.SourceTaxCode, r.QboTaxCodeId)).Should().BeEquivalentTo([("EX", "20"), ("ZR", "21")]);
        result.NeedsReview.Should().ContainSingle(r => r.SourceTaxCode == "Z0").Which.Candidates.Should().HaveCount(2, "the exempt/zero named ones");
    }

    [Fact]
    public async Task T49_existing_mappings_are_never_changed_and_counted()
    {
        await SeedTaxCodesAsync(new RemoteTaxCode("10", "GST 17", null, true, 17m, true), new RemoteTaxCode("13", "Other 17", null, true, 17m, true));
        Sms(("GST17", "GST 17", 17m));
        await Settings(s => s.SaveTaxMappingsAsync(new SaveTaxCodeMappingsRequest { Mappings = [new TaxCodeMappingItem { SourceTaxCode = "GST17", TaxPercent = 17m, QboTaxCodeId = "13" }] }, 7));

        var result = await Settings(s => s.ImportTaxMappingsAsync(7));

        result.Saved.Should().Be(0);
        result.AlreadyMapped.Should().BeGreaterThan(0);
        result.Mappings.Single(m => m.SourceTaxCode == "GST17").QboTaxCodeId.Should().Be("13");
    }
}

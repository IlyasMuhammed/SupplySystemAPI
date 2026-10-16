using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Integration.Tests.PostingControl;

/// <summary>A44 D-15 provider defaults / overrides and D-16 effective accounts.</summary>
public class ProviderDefaultsTests : IAsyncLifetime
{
    private readonly FakeAccountMap _ledger = new();
    private readonly SyncHarness    _h;
    private IntegrationConnection   _connection = null!;

    public ProviderDefaultsTests() => _h = PostingControlKit.Harness(_ledger);

    public async Task InitializeAsync()
    {
        _connection = await _h.ConnectAsync();
        await PostingControlKit.SeedReferenceAsync(_h, _connection, PostingControlKit.Reference());
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<T> Defaults<T>(Func<IQuickBooksProviderDefaultsService, Task<T>> call) =>
        _h.Scoped(sp => call(sp.GetRequiredService<IQuickBooksProviderDefaultsService>()));

    private Task<IReadOnlyDictionary<string, string>> ReadAsync(string documentType) =>
        _h.Scoped(sp => sp.GetRequiredService<IProviderDefaultsReader>().GetAsync(_connection.Id, documentType));

    private static SaveProviderDefaultsRequest Save(params (string Key, string? Value)[] values) =>
        new() { Values = values.Select(v => new ProviderDefaultValueItem { SettingKey = v.Key, SettingValue = v.Value }).ToList() };

    [Fact]
    public async Task Every_key_is_offered_as_a_definition_without_a_value()
    {
        var model = await Defaults(d => d.GetDefaultsAsync());

        model.Defaults.Should().HaveCount(5 + 4 * 6);
        model.Defaults.Should().OnlyContain(d => d.SettingValue == null);
        model.Defaults.Single(d => d.SettingKey == "DepositToAccountRef.CARD").Should().Match<ProviderDefaultModel>(d =>
            d.BaseKey == "DepositToAccountRef" && d.Method == "CARD" && d.ReferenceKind == "Account" && d.AccountTypes.Contains("Bank"));
        (model.ClassTrackingEnabled, model.DepartmentTrackingEnabled).Should().Be((true, true));
        model.DocumentTypes.Should().NotContain(["Customer", "Vendor", "Item"]).And.Contain("Payment");
    }

    [Fact]
    public async Task Values_are_validated_against_the_reference_data_and_saved_with_their_names()
    {
        var model = await Defaults(d => d.SaveDefaultsAsync(Save(
            ("ClassRef", "C1"), ("DepartmentRef", "D1"), ("SalesTermRef", "20"), ("paymentmethodref.cash", "PM1"),
            ("DepositToAccountRef.CASH", "9"), ("BankAccountRef.CARD", "30")), 7));

        model.Defaults.Single(d => d.SettingKey == "PaymentMethodRef.CASH").ValueName.Should().Be("Cash");
        model.Defaults.Single(d => d.SettingKey == "DepositToAccountRef.CASH").ValueName.Should().Be("Undeposited Funds");
        model.Defaults.Where(d => d.SettingValue != null).Should().HaveCount(6).And.OnlyContain(d => d.Warning == null);

        foreach (var (key, value, reason) in new[]
                 {
                     ("ClassRef", "C2", "inactive"),
                     ("ClassRef", "C9", "not in the reference data"),
                     ("DepositToAccountRef.CASH", "1", "Income account"),
                     ("BankAccountRef.CHEQUE", "9", "Other Current Asset account"),
                     ("PosDefaultCustomerRef", "77", "not known here"),
                     ("NoSuchKey", "1", "Unknown provider default")
                 })
        {
            var act = async () => await Defaults(d => d.SaveDefaultsAsync(Save((key, value)), 7));
            (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain(reason, key);
        }

        // An empty value removes the key.
        (await Defaults(d => d.SaveDefaultsAsync(Save(("ClassRef", "")), 7)))
            .Defaults.Single(d => d.SettingKey == "ClassRef").SettingValue.Should().BeNull();
    }

    [Fact]
    public async Task An_override_wins_for_its_document_type_only()
    {
        await Defaults(d => d.SaveDefaultsAsync(Save(("SalesTermRef", "20"), ("DepositToAccountRef.CASH", "6")), 7));
        await Defaults(d => d.SaveOverridesAsync(new SaveDocumentOverridesRequest
        {
            Overrides = [new DocumentOverrideItem { DocumentType = "pOSSale", SettingKey = "DepositToAccountRef.CASH", SettingValue = "9" }]
        }, 7));

        (await ReadAsync("POSSale"))["DepositToAccountRef.CASH"].Should().Be("9");
        (await ReadAsync("Payment"))["DepositToAccountRef.CASH"].Should().Be("6");
        (await ReadAsync("Payment"))["SalesTermRef"].Should().Be("20");

        var overrides = await Defaults(d => d.GetOverridesAsync());
        overrides.Should().ContainSingle().Which.Should().Match<DocumentOverrideModel>(o => o.DocumentType == "POSSale" && o.ValueName == "Undeposited Funds");

        var customers = async () => await Defaults(d => d.SaveOverridesAsync(new SaveDocumentOverridesRequest
        {
            Overrides = [new DocumentOverrideItem { DocumentType = "Customer", SettingKey = "ClassRef", SettingValue = "C1" }]
        }, 7));
        await customers.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Class_and_location_are_not_given_to_the_builder_while_the_company_does_not_track_them()
    {
        await Defaults(d => d.SaveDefaultsAsync(Save(("ClassRef", "C1"), ("DepartmentRef", "D1")), 7));
        (await ReadAsync("SalesInvoice")).Keys.Should().Contain(["ClassRef", "DepartmentRef"]);

        await using (var db = _h.SuperDb())
        {
            db.ReferenceSnapshots.RemoveRange(db.ReferenceSnapshots);
            await db.SaveChangesAsync();
        }
        await PostingControlKit.SeedReferenceAsync(_h, _connection, PostingControlKit.Reference(classTracking: false, departmentTracking: false));

        (await ReadAsync("SalesInvoice")).Keys.Should().NotContain("ClassRef").And.NotContain("DepartmentRef");
        (await Defaults(d => d.GetDefaultsAsync())).Defaults.Single(d => d.SettingKey == "ClassRef").Warning.Should().Contain("does not track classes");
    }

    [Fact]
    public async Task Empty_settings_accounts_fall_back_to_the_general_ledger_mappings()
    {
        await _h.UpdateSettingsAsync(s => { s.DefaultIncomeAccountId = null; s.DiscountAccountId = null; s.FreightExpenseAccountId = null; });
        _ledger.Leaf("4000", "Sales");
        _ledger.Leaf("4900", "Discounts");
        _ledger.Leaf("5100", "Freight in");
        _ledger.Mapped["4000"] = ("1", "Sales");
        _ledger.Mapped["4900"] = ("3", "Office Expenses");
        _ledger.Mapped["5100"] = ("6", "Bank");                          // a Bank account cannot be a freight expense
        _ledger.Defaults["DEFAULT_REVENUE"]        = "4000";
        _ledger.Defaults["DEFAULT_DISCOUNT_GIVEN"] = "4900";
        _ledger.Defaults["DEFAULT_PURCHASE"]       = "5100";

        var effective = await _h.Scoped(async sp =>
        {
            var settings = await sp.GetRequiredService<Core.Connections.IConnectionAccessor>().GetOrCreateSettingsAsync(_connection);
            return await sp.GetRequiredService<IEffectiveAccountsResolver>().ResolveAsync(settings);
        });

        effective.Income.Should().Be(new EffectiveAccount("1", EffectiveAccounts.FromLedger));
        effective.Expense.Should().Be(new EffectiveAccount("ACC-EXP", EffectiveAccounts.FromSettings), "the settings account stays authoritative");
        effective.Discount.Should().Be(new EffectiveAccount("3", EffectiveAccounts.FromLedger));
        effective.FreightExpense.Should().Be(effective.Expense, "the mapped purchase account is not an expense account");
    }
}

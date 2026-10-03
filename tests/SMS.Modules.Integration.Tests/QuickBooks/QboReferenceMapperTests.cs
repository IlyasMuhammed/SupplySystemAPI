using FluentAssertions;
using Intuit.Ipp.Data;
using Intuit.Ipp.Utility;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Providers.QuickBooks.Mapping;

namespace SMS.Modules.Integration.Tests.QuickBooks;

/// <summary>Reference entities are read from JSON exactly as QuickBooks sends it, through the SDK's own deserializer.</summary>
public class QboReferenceMapperTests
{
    private static T Read<T>(string entityJson) where T : class
    {
        var json = "{\"" + typeof(T).Name + "\":" + entityJson + ",\"time\":\"2026-09-30T10:00:00.000-07:00\"}";
        var response = (IntuitResponse)new JsonObjectSerializer().Deserialize<IntuitResponse>(json);
        return (T)response.AnyIntuitObject;
    }

    [Fact]
    public void Account_uses_QuickBooks_wire_names_for_type()
    {
        var account = Read<Account>("""
            {"Name":"Cost of sales","Active":true,"Classification":"Expense","AccountType":"Cost of Goods Sold",
             "AccountSubType":"SuppliesMaterialsCogs","CurrencyRef":{"value":"PKR"},"Id":"80","SyncToken":"0"}
            """);

        QboReferenceMapper.ToAccount(account)
            .Should().Be(new RemoteAccount("80", "Cost of sales", "Cost of Goods Sold", "SuppliesMaterialsCogs", "Expense", "PKR", true));
    }

    [Theory]
    [InlineData("Income", "Income")]
    [InlineData("Accounts Receivable", "Accounts Receivable")]
    [InlineData("Other Current Liability", "Other Current Liability")]
    [InlineData("Expense", "Expense")]
    public void Account_type_round_trips(string wire, string expected)
    {
        var account = Read<Account>("{\"Name\":\"A\",\"AccountType\":\"" + wire + "\",\"Id\":\"1\",\"SyncToken\":\"0\"}");

        QboReferenceMapper.ToAccount(account).AccountType.Should().Be(expected);
    }

    [Fact]
    public void Inactive_account_and_missing_optional_fields()
    {
        var account = Read<Account>("{\"Name\":\"Old\",\"Active\":false,\"Id\":\"5\",\"SyncToken\":\"0\"}");

        QboReferenceMapper.ToAccount(account).Should().Be(new RemoteAccount("5", "Old", "", null, null, null, false));
    }

    [Fact]
    public void Tax_code_with_one_sales_rate_resolves_the_percent()
    {
        var code = Read<TaxCode>("""
            {"Name":"GST 17","Description":"Sales tax 17%","Active":true,"Taxable":true,"TaxGroup":false,
             "SalesTaxRateList":{"TaxRateDetail":[{"TaxRateRef":{"value":"4","name":"GST (Sales)"},"TaxTypeApplicable":"TaxOnAmount","TaxOrder":0}]},
             "PurchaseTaxRateList":{"TaxRateDetail":[]},"Id":"3","SyncToken":"0"}
            """);
        var rates = QboReferenceMapper.ToRateLookup([Read<TaxRate>("{\"Name\":\"GST (Sales)\",\"RateValue\":17,\"Active\":true,\"Id\":\"4\",\"SyncToken\":\"0\"}")]);

        QboReferenceMapper.ToTaxCode(code, rates)
            .Should().Be(new RemoteTaxCode("3", "GST 17", "Sales tax 17%", true, 17m, true));
    }

    [Fact]
    public void Tax_code_with_several_sales_rates_has_no_single_percent()
    {
        var code = Read<TaxCode>("""
            {"Name":"GST+PST","Taxable":true,"SalesTaxRateList":{"TaxRateDetail":[
                {"TaxRateRef":{"value":"4"}},{"TaxRateRef":{"value":"5"}}]},"Id":"9","SyncToken":"0"}
            """);
        var rates = new Dictionary<string, decimal> { ["4"] = 5m, ["5"] = 7m };

        QboReferenceMapper.ToTaxCode(code, rates).RatePercent.Should().BeNull();
    }

    [Fact]
    public void Tax_code_whose_rate_is_unknown_has_no_percent()
    {
        var code = Read<TaxCode>("""{"Name":"X","SalesTaxRateList":{"TaxRateDetail":[{"TaxRateRef":{"value":"77"}}]},"Id":"9","SyncToken":"0"}""");

        var mapped = QboReferenceMapper.ToTaxCode(code, new Dictionary<string, decimal>());

        mapped.RatePercent.Should().BeNull();
        mapped.Taxable.Should().BeFalse("no Taxable flag and no known positive rate");
    }

    [Fact]
    public void Zero_rated_code_resolves_to_zero_and_is_not_taxable()
    {
        var code = Read<TaxCode>("""{"Name":"Exempt","SalesTaxRateList":{"TaxRateDetail":[{"TaxRateRef":{"value":"2"}}]},"Id":"2","SyncToken":"0"}""");

        var mapped = QboReferenceMapper.ToTaxCode(code, new Dictionary<string, decimal> { ["2"] = 0m });

        mapped.RatePercent.Should().Be(0m);
        mapped.Taxable.Should().BeFalse();
    }

    [Fact]
    public void Purchase_only_code_has_no_sales_percent()
    {
        var code = Read<TaxCode>("""
            {"Name":"Input","Taxable":true,"SalesTaxRateList":{"TaxRateDetail":[]},
             "PurchaseTaxRateList":{"TaxRateDetail":[{"TaxRateRef":{"value":"6"}}]},"Id":"6","SyncToken":"0"}
            """);

        QboReferenceMapper.ToTaxCode(code, new Dictionary<string, decimal> { ["6"] = 17m }).RatePercent.Should().BeNull();
    }

    [Fact]
    public void Rate_lookup_skips_rates_without_a_value()
    {
        var lookup = QboReferenceMapper.ToRateLookup([
            Read<TaxRate>("{\"Name\":\"A\",\"RateValue\":5,\"Id\":\"1\",\"SyncToken\":\"0\"}"),
            Read<TaxRate>("{\"Name\":\"B\",\"Id\":\"2\",\"SyncToken\":\"0\"}")
        ]);

        lookup.Should().ContainKey("1").WhoseValue.Should().Be(5m);
        lookup.Should().NotContainKey("2");
    }

    [Fact]
    public void Term_reads_DueDays_from_the_choice_array()
    {
        var term = Read<Term>("""{"Name":"Net 30","Active":true,"Type":"STANDARD","DueDays":30,"Id":"3","SyncToken":"0"}""");

        QboReferenceMapper.ToTerm(term).Should().Be(new RemoteTerm("3", "Net 30", 30, true));
    }

    [Fact]
    public void Term_with_discount_days_still_finds_DueDays()
    {
        var term = Read<Term>("""{"Name":"2/10 Net 30","Type":"STANDARD","DiscountPercent":2,"DiscountDays":10,"DueDays":30,"Id":"4","SyncToken":"0"}""");

        QboReferenceMapper.ToTerm(term).DueDays.Should().Be(30);
    }

    [Fact]
    public void Date_driven_term_has_no_DueDays()
    {
        var term = Read<Term>("""{"Name":"15th of month","Type":"DATE_DRIVEN","DayOfMonthDue":15,"Id":"5","SyncToken":"0","Active":false}""");

        QboReferenceMapper.ToTerm(term).Should().Be(new RemoteTerm("5", "15th of month", null, false));
    }

    [Fact]
    public void Currency_maps_code_and_name()
    {
        var currency = Read<CompanyCurrency>("""{"Code":"USD","Name":"United States Dollar","Active":true,"Id":"1","SyncToken":"0"}""");

        QboReferenceMapper.ToCurrency(currency).Should().Be(new RemoteCurrency("USD", "United States Dollar"));
        QboReferenceMapper.IsActive(currency).Should().BeTrue();
    }

    [Fact]
    public void Preferences_read_currency_numbering_and_tax()
    {
        var prefs = Read<Preferences>("""
            {"SalesFormsPrefs":{"CustomTxnNumbers":true},"TaxPrefs":{"UsingSalesTax":true},
             "CurrencyPrefs":{"MultiCurrencyEnabled":true,"HomeCurrency":{"value":"PKR"}},"Id":"1","SyncToken":"5"}
            """);

        QboReferenceMapper.ToPreferences(prefs).Should().Be(new RemotePreferences("PKR", true, true, true));
    }

    [Fact]
    public void Missing_preference_sections_read_as_off()
    {
        var prefs = Read<Preferences>("""{"CurrencyPrefs":{"HomeCurrency":{"value":"USD"}},"Id":"1","SyncToken":"5"}""");

        QboReferenceMapper.ToPreferences(prefs).Should().Be(new RemotePreferences("USD", false, false, false));
    }

    [Theory]
    [InlineData("""{"SalesFormsPrefs":{"AllowDiscount":true},"Id":"1","SyncToken":"5"}""", true)]
    [InlineData("""{"SalesFormsPrefs":{"AllowDiscount":false},"Id":"1","SyncToken":"5"}""", false)]
    [InlineData("""{"SalesFormsPrefs":{"CustomTxnNumbers":true},"Id":"1","SyncToken":"5"}""", null)]
    public void The_allow_discount_preference_is_read_and_unknown_stays_null(string json, bool? expected)
    {
        QboReferenceMapper.ToPreferences(Read<Preferences>(json)).DiscountsEnabled.Should().Be(expected);
    }

    [Fact]
    public void Company_info_prefers_company_email_and_falls_back_to_legal_name()
    {
        var info = Read<CompanyInfo>("""
            {"CompanyName":"Sandbox Co","LegalName":"Sandbox Company Ltd","Country":"PK",
             "CompanyEmailAddr":{"Address":"owner@sandbox.example"},"Email":{"Address":"other@sandbox.example"},"Id":"1","SyncToken":"0"}
            """);
        QboReferenceMapper.ToCompanyInfo(info)
            .Should().Be(new RemoteCompanyInfo("Sandbox Co", "Sandbox Company Ltd", "PK", "owner@sandbox.example"));

        var legalOnly = Read<CompanyInfo>("""{"LegalName":"Legal Only Ltd","Email":{"Address":"e@x.example"},"Id":"1","SyncToken":"0"}""");
        QboReferenceMapper.ToCompanyInfo(legalOnly)
            .Should().Be(new RemoteCompanyInfo("Legal Only Ltd", "Legal Only Ltd", null, "e@x.example"));
    }
}

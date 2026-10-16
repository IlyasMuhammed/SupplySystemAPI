using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;
using static SMS.Modules.Integration.Providers.QuickBooks.Mapping.QboMap;

namespace SMS.Modules.Integration.Providers.QuickBooks.Mapping;

/// <summary>SDK reference entities → provider-neutral reference records.</summary>
internal static class QboReferenceMapper
{
    /// <summary>AccountType as QuickBooks writes it (<c>Income</c>, <c>Cost of Goods Sold</c>, …).</summary>
    public static RemoteAccount ToAccount(Account account) =>
        new(RequireId(account, "account"),
            account.Name ?? string.Empty,
            account.AccountTypeSpecified ? WireName(account.AccountType) : string.Empty,
            Value(account.AccountSubType),
            account.ClassificationSpecified ? WireName(account.Classification) : null,
            Value(account.CurrencyRef?.Value),
            ReadActive(account.Active, account.ActiveSpecified))
        {
            AcctNum = Value(account.AcctNum)
        };

    // A44 D-15 (CTRL) — classes, departments (Locations) and payment methods for the provider defaults.
    public static RemoteClass ToClass(Class c) =>
        new(RequireId(c, "class"), c.Name ?? string.Empty, Value(c.FullyQualifiedName), ReadActive(c.Active, c.ActiveSpecified));

    public static RemoteDepartment ToDepartment(Department d) =>
        new(RequireId(d, "department"), d.Name ?? string.Empty, Value(d.FullyQualifiedName), ReadActive(d.Active, d.ActiveSpecified));

    public static RemotePaymentMethod ToPaymentMethod(PaymentMethod p) =>
        new(RequireId(p, "payment method"), p.Name ?? string.Empty, Value(p.Type), ReadActive(p.Active, p.ActiveSpecified));

    /// <summary>
    /// RatePercent is filled only when the code has exactly one sales rate and that rate's value is known
    /// (<paramref name="ratesById"/>); a group of rates, a purchase-only code or an unresolved rate gives null.
    /// Taxable is QuickBooks' own flag when sent, otherwise "has a positive sales rate".
    /// </summary>
    public static RemoteTaxCode ToTaxCode(TaxCode code, IReadOnlyDictionary<string, decimal> ratesById)
    {
        var salesRates = code.SalesTaxRateList?.TaxRateDetail ?? [];
        decimal? rate = null;
        if (salesRates.Length == 1
            && salesRates[0]?.TaxRateRef?.Value is { } rateId
            && ratesById.TryGetValue(rateId, out var value))
        {
            rate = value;
        }

        var taxable = code.TaxableSpecified ? code.Taxable : rate is > 0;

        return new RemoteTaxCode(
            RequireId(code, "tax code"),
            code.Name ?? string.Empty,
            Value(code.Description),
            taxable,
            rate,
            ReadActive(code.Active, code.ActiveSpecified));
    }

    /// <summary>Rate id → percent, for rates QuickBooks sent a value for.</summary>
    public static Dictionary<string, decimal> ToRateLookup(IEnumerable<TaxRate> rates)
    {
        var lookup = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var rate in rates)
        {
            if (rate is null || !Has(rate.Id) || !rate.RateValueSpecified) continue;
            lookup[rate.Id] = rate.RateValue;
        }
        return lookup;
    }

    /// <summary>
    /// DueDays lives in the Term's xs:choice array: <c>AnyIntuitObjects[i]</c> where
    /// <c>ItemsElementName[i] == DueDays</c>. Date-driven terms have none (null).
    /// </summary>
    public static RemoteTerm ToTerm(Term term)
    {
        int? dueDays = null;
        var names  = term.ItemsElementName;
        var values = term.AnyIntuitObjects;
        if (names is not null && values is not null)
        {
            for (var i = 0; i < names.Length && i < values.Length; i++)
            {
                if (names[i] != ItemsChoiceType.DueDays) continue;
                dueDays = ToInt(values[i]);
                break;
            }
        }

        return new RemoteTerm(RequireId(term, "term"), term.Name ?? string.Empty, dueDays,
            ReadActive(term.Active, term.ActiveSpecified));
    }

    public static RemoteCurrency ToCurrency(CompanyCurrency currency) =>
        new(currency.Code ?? string.Empty, currency.Name ?? currency.Code ?? string.Empty);

    public static bool IsActive(CompanyCurrency currency) => ReadActive(currency.Active, currency.ActiveSpecified);

    public static RemotePreferences ToPreferences(Preferences preferences) =>
        new(Value(preferences.CurrencyPrefs?.HomeCurrency?.Value),
            preferences.CurrencyPrefs is { MultiCurrencyEnabledSpecified: true, MultiCurrencyEnabled: true },
            preferences.SalesFormsPrefs is { CustomTxnNumbersSpecified: true, CustomTxnNumbers: true },
            preferences.TaxPrefs is { UsingSalesTaxSpecified: true, UsingSalesTax: true })
        {
            DiscountsEnabled = preferences.SalesFormsPrefs is { AllowDiscountSpecified: true } prefs ? prefs.AllowDiscount : null,
            // A44 D-15 (CTRL): tracking preferences — a class / location default is sent only when tracked.
            ClassTrackingPerTxn     = preferences.AccountingInfoPrefs is { ClassTrackingPerTxnSpecified: true } a1 ? a1.ClassTrackingPerTxn : null,
            ClassTrackingPerTxnLine = preferences.AccountingInfoPrefs is { ClassTrackingPerTxnLineSpecified: true } a2 ? a2.ClassTrackingPerTxnLine : null,
            TrackDepartments        = preferences.AccountingInfoPrefs is { TrackDepartmentsSpecified: true } a3 ? a3.TrackDepartments : null,
            DepartmentTerminology   = Value(preferences.AccountingInfoPrefs?.DepartmentTerminology)
        };

    public static RemoteCompanyInfo ToCompanyInfo(CompanyInfo info) =>
        new(Value(info.CompanyName) ?? Value(info.LegalName) ?? string.Empty,
            Value(info.LegalName),
            Value(info.Country),
            Value(info.CompanyEmailAddr?.Address) ?? Value(info.Email?.Address) ?? Value(info.CustomerCommunicationEmailAddr?.Address));
}

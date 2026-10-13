using SMS.Modules.Accounting.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Accounting.Tests;

// BE2 test helpers (fiscal years, defaults, opening balances, mappings, dashboard) — fakes for the Shared seams other modules
// implement, and factories that build the services over an AccountingHarness with those fakes.

internal sealed class FakeOpeningDate : IOpeningBalanceDateProvider
{
    public Dictionary<Guid, DateOnly?> Dates { get; } = [];
    public Task<DateOnly?> GetAsync(Guid organizationId, CancellationToken ct = default) => Task.FromResult(Dates.GetValueOrDefault(organizationId));
}

internal sealed class FakeSubledger : IOpeningSubledgerTotals
{
    public OpeningSubledgerTotals Totals { get; set; } = new(0, 0, 0, "PKR");
    public bool Throw { get; set; }
    public Task<OpeningSubledgerTotals> GetAsync(Guid organizationId, CancellationToken ct = default) =>
        Throw ? throw new InvalidOperationException("finance down") : Task.FromResult(Totals);
}

internal sealed class FakeNumbers : IDocumentNumberGenerator
{
    private int _next;
    public List<(string Prefix, Guid? Org)> Calls { get; } = [];
    public Task<string> NextAsync(string prefix, DateTime? utcNow = null, Guid? organizationId = null, CancellationToken ct = default)
    {
        Calls.Add((prefix, organizationId));
        return Task.FromResult($"{prefix}-{(utcNow ?? DateTime.UtcNow).Year}-{++_next:D5}");
    }
}

internal sealed class FakeOrgCurrency : IOrganizationCurrencyService
{
    public Guid? Id { get; set; } = Guid.NewGuid();
    public Task<Guid?> GetBaseCurrencyIdAsync(Guid organizationId) => Task.FromResult(Id);
}

internal sealed class FakeCurrencyCodes : ICurrencyCodeLookup
{
    public string? Code { get; set; } = "PKR";
    public Task<string?> GetCodeAsync(Guid currencyId, CancellationToken ct = default) => Task.FromResult(Code);
}

internal static class SetupTestKit
{
    public static readonly DateOnly Fy2026Start = new(2026, 1, 1);

    public static FiscalYearService Fiscal(this AccountingHarness h, string? baseCurrency = "PKR") =>
        new(h.Db, h.Get<IAccountingInitializer>(), h.Setup, h.Events,
            new FiscalBaseCurrency(new FakeOrgCurrency(), new FakeCurrencyCodes { Code = baseCurrency }));

    public static DefaultsService Defaults(this AccountingHarness h) =>
        new(h.Db, h.Get<IAccountingInitializer>(), h.Setup, h.Events);

    public static OpeningBalanceService Opening(this AccountingHarness h, FakeOpeningDate? dates = null,
        IOpeningSubledgerTotals? subledger = null, IDocumentNumberGenerator? numbers = null) =>
        new(h.Db, h.Get<IAccountingInitializer>(), h.Setup, h.Events, new JournalNumbers(h.Db, numbers), dates, subledger);

    public static ExternalMappingService Mappings(this AccountingHarness h) =>
        new(h.Db, h.Get<IAccountingInitializer>(), h.Setup, h.Events);

    /// <summary>A conversion date provider answering <paramref name="date"/> for the harness organization.</summary>
    public static FakeOpeningDate DateOf(this AccountingHarness h, DateOnly? date)
    {
        var fake = new FakeOpeningDate();
        fake.Dates[h.Org] = date;
        return fake;
    }
}

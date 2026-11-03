namespace SMS.Shared.Integration.QuickBooks;

// A49 G-3 / D-10 — the QuickBooks half of "can this document post?", asked by SMS.Modules.Hrm without referencing Integration.

public sealed record QuickBooksAccountNeed(string? Role, string? AccountCode);

/// <param name="DocumentType">The FinDocType name (it is the sync-control type too).</param>
public sealed record QuickBooksPostingNeeds(
    string DocumentType, IReadOnlyCollection<QuickBooksAccountNeed> Accounts, IReadOnlyCollection<string> CostCenterCodes, string? CurrencyCode);

public sealed record QuickBooksCostCenterGap(string Code, string Name);
public sealed record QuickBooksCurrencyMismatch(string OrgCurrency, string QboCurrency);

public sealed class QuickBooksReadinessResult
{
    /// <summary>A usable connection exists and MODULE_INTEGRATION is on.</summary>
    public bool Connected { get; init; }
    /// <summary>
    /// The document type's sync control is on, so a posting is sent now. Off + hold: it is stored and waits, and nothing here can
    /// fail it; the accountant's enable refuses until this check passes (G-15), and validation runs again when it is released.
    /// </summary>
    public bool SendsNow { get; init; }
    public bool IsReady => MissingAccounts.Count == 0 && MissingCostCenters.Count == 0 && CurrencyMismatch is null && Problems.Count == 0;
    public IReadOnlyList<QuickBooksAccountNeed> MissingAccounts { get; init; } = [];
    public IReadOnlyList<QuickBooksCostCenterGap> MissingCostCenters { get; init; } = [];
    public QuickBooksCurrencyMismatch? CurrencyMismatch { get; init; }
    /// <summary>Anything else that would be refused (an account QuickBooks will not take on a journal line, classes switched off).</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}

public interface IQuickBooksPostingReadiness
{
    /// <summary>For the ambient organization. Never calls Intuit: reads mappings and the stored reference snapshot.</summary>
    Task<QuickBooksReadinessResult> CheckAsync(QuickBooksPostingNeeds needs, CancellationToken ct = default);
}

/// <summary>Hosts without SMS.Modules.Integration: QuickBooks is simply not a target.</summary>
public sealed class NullQuickBooksPostingReadiness : IQuickBooksPostingReadiness
{
    public Task<QuickBooksReadinessResult> CheckAsync(QuickBooksPostingNeeds needs, CancellationToken ct = default) =>
        Task.FromResult(new QuickBooksReadinessResult());
}

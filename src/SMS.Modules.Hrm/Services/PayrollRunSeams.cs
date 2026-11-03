using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Hrm.Services;

// PAY, API-CONTRACT §13: the readiness seam POST implements and the journal builder PAY implements. AddPost runs after AddPay, so POST registers with AddScoped and takes over the default.

public sealed record MissingQboAccount(string Role, string? AccountCode);
public sealed record MissingCostCenter(string Code, string Name);
public sealed record CurrencyMismatch(string OrgCurrency, string QboCurrency);
public sealed record UnverifiedStatutoryItem(string Kind, string Name);

/// <summary>API-CONTRACT §10.1.</summary>
public sealed record PayrollPostingReadiness
{
    public bool IsReady { get; init; } = true;
    public bool NoTarget { get; init; } = true;
    public bool InternalGlActive { get; init; }
    public bool QuickBooksActive { get; init; }
    public bool InternalGlReady { get; init; } = true;
    public bool QuickBooksReady { get; init; } = true;
    public IReadOnlyList<string> MissingGlRoles { get; init; } = [];
    public IReadOnlyList<MissingQboAccount> MissingQboAccounts { get; init; } = [];
    public IReadOnlyList<MissingCostCenter> MissingCostCenters { get; init; } = [];
    public CurrencyMismatch? CurrencyMismatch { get; init; }
    public IReadOnlyList<UnverifiedStatutoryItem> UnverifiedStatutory { get; init; } = [];
}

/// <summary>POST implements (D-10). Answers about account mappings only; the statutory half (G-21) is PAY's and is merged by the run service.</summary>
internal interface IPayrollPostingReadinessService
{
    Task<PayrollPostingReadiness> CheckAsync(Guid organizationId, int runId, CancellationToken ct = default);
}

/// <summary>Builds the summarized journal of a run (G-11): one line per role × cost center, never an employee.</summary>
internal interface IPayrollPostingBuilder
{
    /// <summary>The run must have PostingDate set. Rebuilt from the immutable snapshot, so a retry yields the same payload and the gateway's fingerprint holds.</summary>
    Task<FinancialTransaction> BuildAsync(Domain.PayrollRun run, CancellationToken ct = default);
    Task<FinancialTransaction> BuildPaymentAsync(Domain.PayrollRun run, DateOnly paidOn, CancellationToken ct = default);
}

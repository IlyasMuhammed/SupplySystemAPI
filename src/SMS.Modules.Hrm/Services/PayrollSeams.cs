using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Hrm.Services;

// The seam between a payroll run (PAY) and what other agents feed into it (LIFE), API-CONTRACT §13.
// LIFE wrote this file first so both could proceed; see the contract's Changes. Same namespace as the other seams.

/// <summary>One amount a provider wants on an employee's line. The amount is positive; the component's type says earning or deduction.</summary>
internal sealed record PayrollInput(string ComponentCode, decimal Amount, string Reason);

/// <summary>
/// Something a payroll run must take into account beyond salary (loans, advances, reimbursements). Register as many as you like;
/// the calculation calls every one. All three methods must run inside the run's own transaction where the run has one.
/// <para>
/// Protocol: <see cref="GetInputsAsync"/> on every calculation (it RESERVES what it returns for this run, so another run cannot
/// also take it, and is safe to repeat); <see cref="MarkConsumedAsync"/> once per unblocked line at Finalize;
/// <see cref="ReleaseAsync"/> when the run is cancelled, reopened to a recalculation that drops lines, or reversed.
/// </para>
/// </summary>
internal interface IPayrollInputProvider
{
    Task<IReadOnlyList<PayrollInput>> GetInputsAsync(Guid organizationId, int runId, int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>The run has finalized with this employee on it: what was reserved is now consumed. Repeating it changes nothing.</summary>
    Task MarkConsumedAsync(Guid organizationId, int runId, int employeeId, CancellationToken ct = default);

    /// <summary>The run no longer stands: everything reserved or consumed by it goes back, ready for a later run. Repeating it changes nothing.</summary>
    Task ReleaseAsync(Guid organizationId, int runId, CancellationToken ct = default);
}

/// <summary>What became of one posting attempt: a value of HrmPostingStates and a note for the screen (null when there is nothing to say).</summary>
internal sealed record HrmPostingOutcome(string State, string? Note);

/// <summary>
/// Calls the gateway, after the caller's commit, and says which HrmPostingStates value that means. NoTargetConfigured and
/// ModuleNotLicensed both mean "no posting required" (D-11). Never throws for a posting failure: the document is already committed,
/// so a failure becomes state Failed and the document's retry-posting route tries again under the same idempotency key.
/// </summary>
internal interface IHrmPostingHelper
{
    Task<HrmPostingOutcome> PostAsync(FinancialTransaction transaction, CancellationToken ct = default);
}

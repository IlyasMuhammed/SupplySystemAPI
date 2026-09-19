namespace SMS.Shared.Common;

/// <summary>
/// Hands out document numbers from a per-(organization, prefix, year) counter — format
/// <c>PREFIX-YYYY-NNNNN</c>, five digits, restarting at 1 each January, tracked separately per
/// organization. Implemented in SMS.Modules.Logistics against <c>logistics.document_number_sequences</c>
/// (the only physical counter table in the app) and shared here so other modules can draw from the
/// same table without a project reference to Logistics — e.g. Demand's SaleOrder numbering
/// (A29-P3-06 §4.1's <c>SO-</c> prefix) reuses it rather than standing up a second counter.
/// </summary>
public interface IDocumentNumberGenerator
{
    /// <summary>
    /// Reserves and returns the next document number for a prefix, e.g. <c>DLV-2026-00001</c>.
    /// The number is consumed whether or not the caller goes on to save anything.
    /// </summary>
    /// <param name="organizationId">
    /// Whose counter to draw from. Defaults to the ambient tenant. Pass it explicitly from code
    /// that runs outside a request — a startup job or a migration — where the ambient context is
    /// not the organization being worked on.
    /// </param>
    Task<string> NextAsync(
        string prefix,
        DateTime? utcNow = null,
        Guid? organizationId = null,
        CancellationToken ct = default);
}

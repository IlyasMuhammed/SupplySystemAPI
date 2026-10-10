namespace SMS.Shared.Common;

/// <summary>
/// A37 D-13 — what a customer owes, in the organization's sale base currency: <paramref name="Balance"/> = the base value
/// of every open (issued, partially paid, overdue) sales invoice's balance due; <paramref name="Overdue"/> = the part of it
/// past its due date. <paramref name="CurrencyCode"/> null when the base currency cannot be resolved.
/// </summary>
public sealed record CustomerBalanceInfo(decimal Balance, decimal Overdue, string? CurrencyCode);

/// <summary>
/// A37 D-13 — customer balances for the customer master (<c>GET /api/customers…</c>). Partner = suppliers.BusinessPartners.UUID.
/// Explicit organization; another organization's invoices never count. Partners with nothing open are absent from the
/// result (callers read them as zero). Implemented today in Suppliers over Finance's sales invoices
/// (<c>SalesInvoiceCustomerBalanceLookup</c>, registered with TryAdd) — Finance may register its own and it wins.
/// </summary>
public interface ICustomerBalanceLookup
{
    Task<IReadOnlyDictionary<Guid, CustomerBalanceInfo>> GetAsync(
        Guid organizationId, IReadOnlyCollection<Guid> partnerIds, CancellationToken ct = default);

    /// <summary>The organization's sale base currency code (what every balance is expressed in), null when unknown.</summary>
    Task<string?> GetBaseCurrencyCodeAsync(Guid organizationId, CancellationToken ct = default);
}

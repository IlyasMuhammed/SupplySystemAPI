using SMS.Modules.Finance.Models;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// The organization's tax codes as the Settings screen manages them (SAP alignment, S-1/S-3). Other
/// modules read codes through SMS.Shared's <c>ITaxCodeLookup</c>, not through this.
/// </summary>
public interface ITaxCodeService
{
    /// <summary>
    /// <paramref name="side"/> SALES or PURCHASE limits to codes usable there (BOTH included); null for all.
    /// Active only unless <paramref name="includeInactive"/>. Default first, then by code.
    /// </summary>
    Task<IReadOnlyList<TaxCodeModel>> ListAsync(string? side, bool includeInactive, CancellationToken ct = default);

    Task<TaxCodeSaved> CreateAsync(SaveTaxCodeRequest req, int userId, CancellationToken ct = default);

    /// <summary>
    /// Changes a code. Its rate may change (documents keep the rate they were raised with); its code text may
    /// only change while no document uses it. Deactivating is <c>IsActive = false</c> — codes are never deleted.
    /// </summary>
    Task<TaxCodeSaved> UpdateAsync(Guid uuid, SaveTaxCodeRequest req, int userId, CancellationToken ct = default);

    /// <summary>
    /// One SALES code (TAX17, TAX7_5 …) for each distinct tax % already used on the organization's sale-order
    /// lines that no active sales code covers. Asking again creates nothing new.
    /// </summary>
    Task<TaxCodesFromRatesOutcome> CreateFromRatesInUseAsync(int userId, CancellationToken ct = default);
}

public sealed record TaxCodesFromRatesOutcome(TaxCodesFromRatesResult Result, string Message);

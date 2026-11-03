using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.ArAp.Payables;

/// <summary>Who is acting (API-CONTRACT §0 "Who"): the int user id and a name snapshot.</summary>
public readonly record struct ApActor(int? UserId, string? UserName)
{
    public static ApActor System => new(null, "System");
}

/// <summary>Small helpers every AP service shares.</summary>
internal static class ApSupport
{
    internal const int DefaultPageSize = 25;
    internal const int MaxPageSize     = 200;

    internal static (int Page, int PageSize) Paging(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize is null or <= 0 ? DefaultPageSize : pageSize.Value, 1, MaxPageSize));

    internal static PaginatedResponse<T> Page<T>(List<T> data, int total, int page, int pageSize) => new()
    {
        Data = data, TotalRecords = total, Page = page, PageSize = pageSize,
        TotalPages = (int)Math.Ceiling(total / (double)pageSize)
    };

    internal static string RowVersionText(byte[]? rowVersion) =>
        rowVersion is { Length: > 0 } ? Convert.ToBase64String(rowVersion) : string.Empty;

    /// <summary>Applies the client's rowVersion as the original value, so a stale one is a DbUpdateConcurrencyException (409).</summary>
    internal static void ExpectRowVersion<T>(FinanceDbContext db, T entity, string? rowVersion) where T : class
    {
        if (string.IsNullOrWhiteSpace(rowVersion)) return;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(rowVersion.Trim()); }
        catch (FormatException) { throw new BadRequestException("rowVersion is not valid."); }
        db.Entry(entity).Property("RowVersion").OriginalValue = bytes;
    }

    /// <summary>SaveChanges, a lost optimistic race as the contract's 409.</summary>
    internal static async Task SaveAsync(FinanceDbContext db, string thing, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException($"{thing} was changed by someone else; reload and try again.");
        }
    }

    internal static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    internal static DateOnly Today(TimeProvider? clock) => DateOnly.FromDateTime((clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);

    /// <summary>The method code in the AP list (case-insensitive), or the contract's 400.</summary>
    internal static string? ApMethod(string? method, string field = "payment method")
    {
        if (string.IsNullOrWhiteSpace(method)) return null;
        return ArApEnums.Canonical(method, ArApEnums.ApPaymentMethods)
            ?? throw new BadRequestException(ArApEnums.UnknownText(field, method, ArApEnums.ApPaymentMethods));
    }

    internal static string Priority(string? priority, string field = "paymentPriority") =>
        ArApEnums.Canonical(priority, ArApEnums.PaymentPriority.All)
        ?? throw new BadRequestException(ArApEnums.UnknownText(field, priority, ArApEnums.PaymentPriority.All));

    /// <summary>The organization's purchase base currency code when the host can say (D-13), else null.</summary>
    internal static async Task<string?> PurchaseBaseCodeAsync(ICurrencyService? currency, ILookupsService? lookups, Guid organizationId, CancellationToken ct)
    {
        if (currency is null || lookups is null) return null;
        try
        {
            var id = await currency.GetBaseCurrencyIdAsync(organizationId, TransactionDomain.Purchase, ct);
            return lookups.GetCurrencies().FirstOrDefault(c => c.Id == id)?.Code?.Trim().ToUpperInvariant();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// An amount of a supplier invoice in the purchase base (D-13): × the invoice's locked rate when its currency is not
    /// the base; 1:1 (rate missing) otherwise.
    /// </summary>
    internal static decimal ToBase(decimal amount, string currencyCode, decimal? rate, string? baseCode) =>
        baseCode is null || string.Equals(currencyCode, baseCode, StringComparison.OrdinalIgnoreCase) || rate is not > 0m
            ? amount
            : Money(amount * rate.Value);

    /// <summary>The supplier invoices of this organization that are payables (approved, not deleted).</summary>
    internal static IQueryable<Invoice> Payables(FinanceDbContext db) =>
        Repositories.InvoiceRowLocks.Own(db).Where(i => i.MatchStatus == InvoiceMatchStatus.Approved);
}

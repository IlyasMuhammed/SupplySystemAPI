using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.Import;

/// <summary>A business partner as the A40 opening-balance imports need it (suppliers.BusinessPartners).</summary>
/// <param name="Code">BusinessPartner.SupplierCode — the customer / supplier code (C-00001, S-00001, …).</param>
public sealed record ImportPartner(
    Guid Uuid, string Code, string Name, bool IsCustomer, bool IsVendor, bool IsActive, int PaymentTermsDays);

/// <summary>
/// A40 (TFIN) — business partners by code, for Finance's opening-balance import targets. Implemented in Suppliers (which
/// references Finance, so the seam lives here, as <c>ICustomerPaymentTermsLookup</c> does). Explicit organization: another
/// organization's partners are absent. Deleted partners are absent; inactive ones are returned (IsActive false) so the
/// target can say why it refuses them.
/// </summary>
public interface IImportPartnerLookup
{
    /// <summary>Partners whose code is one of <paramref name="codes"/> (case-insensitive), keyed by the upper-case code.</summary>
    Task<IReadOnlyDictionary<string, ImportPartner>> FindByCodesAsync(
        Guid organizationId, IReadOnlyCollection<string> codes, CancellationToken ct = default);

    /// <summary>Active customer (<paramref name="customers"/>) or vendor codes, ordered, at most <paramref name="max"/>.</summary>
    Task<IReadOnlyList<string>> ListCodesAsync(Guid organizationId, bool customers, int max, CancellationToken ct = default);
}

/// <summary>A40 (TFIN) — what Finance's import targets share: value parsing, the organization check, the batch runner.</summary>
internal static class FinanceImport
{
    /// <summary>Template dropdowns are capped at this many values (API-CONTRACT ImportTemplateList).</summary>
    internal const int MaxListValues = 1000;

    internal const string OpeningDateNotSet = "Set the opening balance date in Finance settings first.";

    // Error codes beyond the generic ones of the contract.
    internal const string OpeningDateMissing = "OPENING_DATE_NOT_SET";
    internal const string AfterOpeningDate   = "AFTER_OPENING_DATE";
    internal const string NotACustomer       = "NOT_A_CUSTOMER";
    internal const string NotAVendor         = "NOT_A_SUPPLIER";
    internal const string InactivePartner    = "INACTIVE_PARTNER";
    internal const string NoExchangeRate     = "NO_EXCHANGE_RATE";
    internal const string MultipleDefaults   = "MULTIPLE_DEFAULTS";

    internal static string? Value(IReadOnlyDictionary<string, string?> values, string field) =>
        values.TryGetValue(field, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    internal static bool TryDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    internal static bool TryDecimal(string? value, out decimal amount) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);

    internal static bool TryBool(string? value, out bool result)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "true": case "yes": case "y": case "1": result = true;  return true;
            case "false": case "no": case "n": case "0": result = false; return true;
            default: result = false; return false;
        }
    }

    internal static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>
    /// The organization is explicit (D-3), but the services these targets write through read the caller's tenant: the
    /// two must agree unless the caller is a super admin (whose reads are unfiltered; every row is stamped explicitly).
    /// </summary>
    internal static void EnsureOrganization(FinanceDbContext db, Guid organizationId)
    {
        if (organizationId == Guid.Empty) throw new BadRequestException("The organization is required.");
        var tenant = db.TenantContext;
        if (!tenant.IsSuperAdmin && tenant.OrganizationId != organizationId)
            throw new BadRequestException("The import is for another organization than the caller's.");
    }

    /// <summary>Every row this unit adds belongs to the import's organization — set explicitly, never inferred.</summary>
    internal static void StampOrganization(FinanceDbContext db, Guid organizationId)
    {
        foreach (var e in db.ChangeTracker.Entries<ITenantScopedEntity>())
            if (e.State == EntityState.Added && e.Entity.OrganizationId == Guid.Empty)
                e.Entity.OrganizationId = organizationId;
    }

    /// <summary>
    /// The idempotency key of an opening document: a name-based uuid of (kind, organization, partner, upper-case
    /// reference). The unique source-uuid indexes then refuse a second opening document for the same reference, even
    /// from two imports racing.
    /// </summary>
    internal static Guid OpeningKey(string kind, Guid organizationId, Guid partnerId, string? reference)
    {
        var text  = $"A40|{kind}|{organizationId:N}|{partnerId:N}|{(reference ?? string.Empty).Trim().ToUpperInvariant()}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16).ToArray();
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50); // version 5 (name-based) in Guid's little-endian layout
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes);
    }

    /// <summary>"Row 7: message" — how a target refuses a batch (all-or-nothing, IMP-EXC-02).</summary>
    internal static BadRequestException RowError(int rowNumber, ImportFieldError error) =>
        new($"Row {rowNumber}: {error.Message}");

    /// <summary>The first error of the batch, as the exception ApplyAsync throws; null when every row is clean.</summary>
    internal static BadRequestException? FirstError(IEnumerable<ImportRowCheck> checks) =>
        checks.Where(c => c.Errors.Count > 0).Select(c => RowError(c.RowNumber, c.Errors[0])).FirstOrDefault();

    /// <summary>
    /// Runs one batch on SQL Server inside the context's execution strategy and one transaction (optionally holding an
    /// exclusive application lock first); the in-memory provider has neither and runs it as it is. A retry starts again from
    /// what is committed (the change tracker is cleared).
    /// </summary>
    internal static async Task<T> InTransactionAsync<T>(FinanceDbContext db, string? appLock, Func<Task<T>> work, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return await work();

        if (db.Database.CurrentTransaction is not null)
        {
            if (appLock is not null) await LockAsync(db, appLock, ct);
            return await work();
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var attempt  = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (appLock is not null) await LockAsync(db, appLock, ct);
            var result = await work();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    private static async Task LockAsync(FinanceDbContext db, string resource, CancellationToken ct)
    {
        var outcome = new SqlParameter("@outcome", SqlDbType.Int) { Direction = ParameterDirection.Output };
        await db.Database.ExecuteSqlRawAsync(
            "DECLARE @result int; "
          + "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000; "
          + "SET @outcome = @result;",
            [new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource }, outcome],
            ct);

        if (outcome.Value is not int granted || granted < 0)
            throw new ConflictException("Someone else is saving the same records right now, so nothing was imported. Try again in a moment.");
    }
}

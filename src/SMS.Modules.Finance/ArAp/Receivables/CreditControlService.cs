using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.ArAp.Receivables;

// ── Models (API-CONTRACT §2) ────────────────────────────────────────────────────────────────────────────────────────

public sealed class CreditExposureModel
{
    public Guid     CustomerUuid          { get; set; }
    public string   CustomerName          { get; set; } = string.Empty;
    public string?  CustomerCode          { get; set; }
    public string?  BaseCurrencyCode      { get; set; }
    public decimal? CreditLimit           { get; set; }
    public decimal? TemporaryCreditLimit  { get; set; }
    public DateOnly? TemporaryCreditExpiry { get; set; }
    public decimal? EffectiveCreditLimit  { get; set; }
    public decimal  OpenOrdersAmount      { get; set; }
    public decimal  OpenInvoicesAmount    { get; set; }
    public decimal  TotalExposure         { get; set; }
    public decimal? AvailableCredit       { get; set; }
    public bool     IsOverLimit           { get; set; }
    public bool     IsOnHold              { get; set; }
    public string   RiskTier              { get; set; } = ArApEnums.RiskTier.Medium;
    public string   CreditControlMode     { get; set; } = CreditControlModes.Off;
}

public sealed class CreditHistoryModel
{
    public Guid      Uuid                 { get; set; }
    public string    Action               { get; set; } = string.Empty;
    public string    Reason               { get; set; } = string.Empty;
    public string?   PreviousValue        { get; set; }
    public string?   NewValue             { get; set; }
    public decimal?  Amount               { get; set; }
    public DateOnly? ExpiryDate           { get; set; }
    public string?   SourceDocumentType   { get; set; }
    public Guid?     SourceDocumentUuid   { get; set; }
    public string?   SourceDocumentNumber { get; set; }
    public string?   RequestedByName      { get; set; }
    public DateTime  RequestedAt          { get; set; }
    public string    Status               { get; set; } = string.Empty;
}

public sealed record CreditCheckBody(decimal Amount, string? CurrencyCode);
public sealed record CreditReasonBody(string? Reason);
public sealed record TemporaryLimitBody(decimal? Amount, DateOnly? ExpiryDate, string? Reason);
public sealed record RiskTierBody(string? RiskTier, string? Reason);

public sealed class CreditExposureFilter
{
    public bool?   OverLimit { get; set; }
    public bool?   OnHold    { get; set; }
    public string? RiskTier  { get; set; }
    public string? Search    { get; set; }
    public int     Page      { get; set; } = 1;
    public int     PageSize  { get; set; } = 25;
}

/// <summary>
/// A48 AR — C2 credit control (tasks 084–117, D-5, API-CONTRACT §2, §10.2). Implements the shared <see cref="ICreditCheck"/>
/// Demand calls on sale-order confirmation and Finance on sales-invoice creation, plus the audited credit actions.
/// <para>
/// Exposure is computed live by <see cref="CreditExposureReader"/> in the sale base. The permanent limit is
/// BusinessPartner.CreditLimit (through <see cref="IPartnerLookupService"/>); hold, temporary limit and tier are on the
/// partner financial profile (D-4). Every action writes a <see cref="CreditHoldRequest"/> (BR-CR-006).
/// </para>
/// </summary>
internal sealed class CreditControlService : ICreditCheck
{
    internal const string DocSaleOrder    = "SALE_ORDER";
    internal const string DocSalesInvoice = "SALES_INVOICE";
    internal const string DocManual       = "MANUAL";

    private const int LockTimeoutMs = 15000;

    /// <summary>In-process half of the per-customer lock (also the whole lock on a non-SQL Server provider).</summary>
    private static readonly ConcurrentDictionary<(Guid, Guid), SemaphoreSlim> Gates = new();

    private readonly FinanceDbContext                _db;
    private readonly CreditExposureReader            _exposure;
    private readonly IPartnerFinancialProfileService _profiles;
    private readonly IPartnerLookupService?          _partners;
    private readonly ICurrencyService?               _currency;
    private readonly IOrgCurrencyLookup?             _orgCurrencies;
    private readonly ILogger<CreditControlService>   _log;
    private readonly TimeProvider                    _clock;

    public CreditControlService(
        FinanceDbContext db, CreditExposureReader exposure, IPartnerFinancialProfileService profiles,
        ILogger<CreditControlService> log, IPartnerLookupService? partners = null, ICurrencyService? currency = null,
        IOrgCurrencyLookup? orgCurrencies = null, TimeProvider? clock = null)
    {
        _db            = db;
        _exposure      = exposure;
        _profiles      = profiles;
        _log           = log;
        _partners      = partners;
        _currency      = currency;
        _orgCurrencies = orgCurrencies;
        _clock         = clock ?? TimeProvider.System;
    }

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    // ══ ICreditCheck ═══════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<CreditCheckResult> CheckAsync(CreditCheckRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var org  = request.OrganizationId;
        var mode = await ModeAsync(org, ct);
        if (mode == CreditControlModes.Off)
            return new CreditCheckResult(mode, true, null, null, false, null, 0m, 0m, 0m, 0m, 0m, null, null);

        var baseCode = await BaseCodeAsync(org, ct);
        var partner  = await PartnerAsync(org, request.CustomerUuid, ct);
        var profile  = await CheckProfileAsync(org, request.CustomerUuid, ct);
        var parts    = await _exposure.ForCustomerAsync(org, request.CustomerUuid, ct);
        var amount   = request.AmountBase ?? await ToBaseAsync(org, request.Amount, request.CurrencyCode, baseCode, ct);

        // A sales invoice bills order lines that are already open-order exposure: it moves value from orders to invoices,
        // it does not add to it (task 120).
        // REV-14: only up to what its own order still has open.
        var orders = parts.OpenOrders;
        if (request.DocumentType == DocSalesInvoice)
        {
            var own = request.DocumentUuid is { } invoiceUuid
                ? await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
                    .Where(i => i.OrganizationId == org && i.UUID == invoiceUuid).Select(i => i.SaleOrderUuid).FirstOrDefaultAsync(ct)
                : null;
            var ownOpen = own is { } so ? await _exposure.OpenOrderValueAsync(org, so, ct) : 0m;
            orders = Math.Max(0m, parts.OpenOrders - Math.Min(amount, ownOpen));
        }
        var standing = new CreditStanding(partner?.CreditLimit, profile.TemporaryCreditLimit, profile.TemporaryCreditExpiry,
            profile.CreditHold, profile.CreditHoldReason, orders, parts.OpenInvoices);

        var d = CreditRules.Decide(mode, standing, amount, Today, partner?.Name ?? "The customer", baseCode);
        return new CreditCheckResult(mode, d.Approved, d.BlockReason, d.Warning, profile.CreditHold, d.EffectiveLimit,
            standing.OpenOrders, standing.OpenInvoices, d.CurrentExposure, d.RequestedAmount, d.ProjectedExposure,
            d.AvailableCredit, baseCode);
    }

    /// <summary>REV-11 — the mode alone (one settings read).</summary>
    public Task<string> GetModeAsync(Guid organizationId, CancellationToken ct = default) => ModeAsync(organizationId, ct);

    public async Task RecordOverrideAsync(CreditCheckRequest request, CreditCheckResult result, string reason, int? userId,
        string? userName, CancellationToken ct = default)
    {
        var why = RequireReason(reason);
        var org = request.OrganizationId;
        var profile = await _profiles.GetOrCreateAsync(org, request.CustomerUuid, ArApEnums.PartnerRole.Customer, ct);
        var partner = await PartnerAsync(org, request.CustomerUuid, ct);
        Audit(profile, partner?.Name, ArApEnums.CreditHoldAction.Override,
            Truncate($"{request.DocumentNumber ?? request.DocumentType}: {why}", 500),
            previous: result.BlockReason is null ? null : Truncate(result.BlockReason, 500), next: "Overridden",
            amount: result.RequestedAmount, userId: userId, userName: userName,
            docType: request.DocumentType, docUuid: request.DocumentUuid, docNumber: request.DocumentNumber);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// TS-CR-008 — serializes check + confirm per customer: an in-process gate always, and on SQL Server an exclusive
    /// session app lock on a connection of its own (held until dispose, whatever the caller's transactions do).
    /// </summary>
    public async Task<IAsyncDisposable> LockCustomerAsync(Guid organizationId, Guid customerUuid, CancellationToken ct = default)
    {
        var gate = Gates.GetOrAdd((organizationId, customerUuid), _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(LockTimeoutMs, ct))
            throw new ConflictException("Another credit check for this customer is still running; try again.");

        SqlConnection? connection = null;
        var resource = $"finance.credit/{organizationId:N}/{customerUuid:N}";
        try
        {
            if (_db.Database.IsSqlServer() && _db.Database.GetConnectionString() is { Length: > 0 } cs)
            {
                connection = new SqlConnection(cs);
                await connection.OpenAsync(ct);
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', " +
                                  "@LockOwner = 'Session', @LockTimeout = @t; SELECT @r;";
                cmd.Parameters.Add(new SqlParameter("@res", SqlDbType.NVarChar, 255) { Value = resource });
                cmd.Parameters.Add(new SqlParameter("@t", SqlDbType.Int) { Value = LockTimeoutMs });
                var granted = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
                if (granted < 0)
                    throw new ConflictException("Another credit check for this customer is still running; try again.");
            }
            return new CustomerLock(gate, connection, resource, _log);
        }
        catch
        {
            if (connection is not null) await connection.DisposeAsync();
            gate.Release();
            throw;
        }
    }

    private sealed class CustomerLock(SemaphoreSlim gate, SqlConnection? connection, string resource, ILogger log) : IAsyncDisposable
    {
        private int _done;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            try
            {
                if (connection is not null)
                {
                    try
                    {
                        await using var cmd = connection.CreateCommand();
                        cmd.CommandText = "EXEC sp_releaseapplock @Resource = @res, @LockOwner = 'Session';";
                        cmd.Parameters.Add(new SqlParameter("@res", SqlDbType.NVarChar, 255) { Value = resource });
                        await cmd.ExecuteNonQueryAsync();
                    }
                    catch (Exception ex)
                    {
                        // Closing the connection releases a session lock anyway.
                        log.LogDebug(ex, "Releasing credit lock {Resource} explicitly failed; closing its connection releases it.", resource);
                    }
                    await connection.DisposeAsync();
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    // ══ Exposure ═══════════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<CreditExposureModel> GetExposureAsync(Guid customerUuid, CancellationToken ct = default)
    {
        var org     = Org();
        var partner = await RequireCustomerAsync(org, customerUuid, ct);
        var profile = await _profiles.GetOrCreateAsync(org, customerUuid, ArApEnums.PartnerRole.Customer, ct);
        if (await ExpireTemporaryLimitAsync(profile, partner.Name)) await SaveAsync(ct);

        var parts = await _exposure.ForCustomerAsync(org, customerUuid, ct);
        return ToModel(partner, profile, parts, await ModeAsync(org, ct), await BaseCodeAsync(org, ct));
    }

    /// <summary>The credit-control list: customers with a profile or anything open.</summary>
    public async Task<PaginatedResponse<CreditExposureModel>> ListExposuresAsync(CreditExposureFilter filter, CancellationToken ct = default)
    {
        var org = Org();
        var tier = filter.RiskTier is null ? null : ArApEnums.Canonical(filter.RiskTier, ArApEnums.RiskTier.All)
            ?? throw new BadRequestException(ArApEnums.UnknownText("riskTier", filter.RiskTier, ArApEnums.RiskTier.All));

        var profiles = await _db.Set<PartnerFinancialProfile>().IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == org && p.PartnerRole == ArApEnums.PartnerRole.Customer)
            .ToListAsync(ct);
        var byPartner = profiles.ToDictionary(p => p.PartnerUuid);

        var exposures = await _exposure.ForCustomersAsync(org, null, ct);
        var uuids = exposures.Keys.Concat(byPartner.Keys).Distinct().ToList();
        var partners = _partners is null
            ? new Dictionary<Guid, PartnerInfo>()
            : new Dictionary<Guid, PartnerInfo>(await _partners.GetManyAsync(org, uuids, null, ct));

        var mode = await ModeAsync(org, ct);
        var baseCode = await BaseCodeAsync(org, ct);
        var rows = uuids
            .Select(u => ToModel(
                partners.GetValueOrDefault(u) ?? new PartnerInfo(u, "", "(unknown customer)", true, false, true, null, null, null, null, null, null, 0, null),
                byPartner.GetValueOrDefault(u) ?? new PartnerFinancialProfile { PartnerUuid = u },
                exposures.GetValueOrDefault(u) ?? new ExposureParts(u, 0m, 0m, 0m), mode, baseCode))
            .Where(m => filter.OverLimit is null || m.IsOverLimit == filter.OverLimit)
            .Where(m => filter.OnHold is null || m.IsOnHold == filter.OnHold)
            .Where(m => tier is null || m.RiskTier == tier)
            .Where(m => string.IsNullOrWhiteSpace(filter.Search)
                     || m.CustomerName.Contains(filter.Search.Trim(), StringComparison.OrdinalIgnoreCase)
                     || (m.CustomerCode ?? "").Contains(filter.Search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.CustomerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Page(rows, filter.Page, filter.PageSize);
    }

    // ══ Hold / release / temporary limit / tier (tasks 105–113) ═════════════════════════════════════════════════════

    public async Task<CreditExposureModel> PlaceHoldAsync(Guid customerUuid, string? reason, int? userId, string? userName, CancellationToken ct = default)
    {
        var why = RequireReason(reason);
        var org = Org();
        var partner = await RequireCustomerAsync(org, customerUuid, ct);
        var profile = await _profiles.GetOrCreateAsync(org, customerUuid, ArApEnums.PartnerRole.Customer, ct);
        if (profile.CreditHold) throw new ConflictException($"{partner.Name} is already on credit hold.");

        ApplyHold(profile, why, userId, userName);
        Audit(profile, partner.Name, ArApEnums.CreditHoldAction.Hold, why, "Active", "On hold", userId: userId, userName: userName);
        await SaveAsync(ct);
        return await GetExposureAsync(customerUuid, ct);
    }

    public async Task<CreditExposureModel> ReleaseHoldAsync(Guid customerUuid, string? reason, int? userId, string? userName, CancellationToken ct = default)
    {
        var why = RequireReason(reason);
        var org = Org();
        var partner = await RequireCustomerAsync(org, customerUuid, ct);
        var profile = await _profiles.GetOrCreateAsync(org, customerUuid, ArApEnums.PartnerRole.Customer, ct);
        if (!profile.CreditHold) throw new ConflictException($"{partner.Name} is not on credit hold.");

        var previous = profile.CreditHoldReason;
        profile.CreditHold       = false;
        profile.CreditHoldReason = null;
        profile.CreditHoldAt     = null;
        profile.CreditHoldBy     = null;
        profile.CreditHoldByName = null;
        Touch(profile, userId, userName);
        Audit(profile, partner.Name, ArApEnums.CreditHoldAction.Release, why,
            previous is null ? "On hold" : Truncate($"On hold: {previous}", 100), "Active", userId: userId, userName: userName);
        await SaveAsync(ct);
        return await GetExposureAsync(customerUuid, ct);
    }

    public async Task<CreditExposureModel> SetTemporaryLimitAsync(
        Guid customerUuid, decimal? amount, DateOnly? expiry, string? reason, int? userId, string? userName, CancellationToken ct = default)
    {
        var why = RequireReason(reason);
        var org = Org();
        var partner = await RequireCustomerAsync(org, customerUuid, ct);
        var profile = await _profiles.GetOrCreateAsync(org, customerUuid, ArApEnums.PartnerRole.Customer, ct);
        var previous = Describe(profile.TemporaryCreditLimit, profile.TemporaryCreditExpiry);

        if (amount is null)
        {
            profile.TemporaryCreditLimit  = null;
            profile.TemporaryCreditExpiry = null;
            Touch(profile, userId, userName);
            Audit(profile, partner.Name, ArApEnums.CreditHoldAction.TempIncrease, why, previous, "None", userId: userId, userName: userName);
            await SaveAsync(ct);
            return await GetExposureAsync(customerUuid, ct);
        }

        if (amount <= 0m) throw new BadRequestException("The temporary limit must be greater than zero.");
        if (expiry is not { } until || until <= Today) throw new BadRequestException("The expiry date must be after today.");
        if (!CreditRules.HasPermanentLimit(partner.CreditLimit))
            throw new BadRequestException($"{partner.Name} has no permanent credit limit; set one on the customer before granting a temporary one.");
        if (amount <= partner.CreditLimit)
            throw new BadRequestException($"The temporary limit must be above the permanent limit ({CreditRules.Money(partner.CreditLimit!.Value)}).");

        var rounded = Math.Round(amount.Value, 2, MidpointRounding.AwayFromZero);
        profile.TemporaryCreditLimit  = rounded;
        profile.TemporaryCreditExpiry = until;
        Touch(profile, userId, userName);
        Audit(profile, partner.Name, ArApEnums.CreditHoldAction.TempIncrease, why, previous, Describe(rounded, until),
            amount: rounded, expiry: until, userId: userId, userName: userName);
        await SaveAsync(ct);
        return await GetExposureAsync(customerUuid, ct);
    }

    public async Task<CreditExposureModel> SetRiskTierAsync(
        Guid customerUuid, string? riskTier, string? reason, int? userId, string? userName, CancellationToken ct = default)
    {
        var tier = ArApEnums.Canonical(riskTier, ArApEnums.RiskTier.All)
            ?? throw new BadRequestException(ArApEnums.UnknownText("riskTier", riskTier, ArApEnums.RiskTier.All));
        var why = RequireReason(reason);
        var org = Org();
        var partner = await RequireCustomerAsync(org, customerUuid, ct);
        var profile = await _profiles.GetOrCreateAsync(org, customerUuid, ArApEnums.PartnerRole.Customer, ct);

        if (profile.RiskTier != tier)
        {
            var previous = profile.RiskTier;
            profile.RiskTier = tier;
            Touch(profile, userId, userName);
            Audit(profile, partner.Name, ArApEnums.CreditHoldAction.RiskTier, why, previous, tier, userId: userId, userName: userName);
            await SaveAsync(ct);
        }
        return await GetExposureAsync(customerUuid, ct);
    }

    public async Task<PaginatedResponse<CreditHistoryModel>> HistoryAsync(Guid customerUuid, int page, int pageSize, CancellationToken ct = default)
    {
        var org = Org();
        await RequireCustomerAsync(org, customerUuid, ct);
        var rows = await _db.Set<CreditHoldRequest>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && r.PartnerUuid == customerUuid)
            .OrderByDescending(r => r.RequestedAt).ThenByDescending(r => r.Id)
            .Select(r => new CreditHistoryModel
            {
                Uuid = r.Uuid, Action = r.Action, Reason = r.Reason, PreviousValue = r.PreviousValue, NewValue = r.NewValue,
                Amount = r.Amount, ExpiryDate = r.ExpiryDate, SourceDocumentType = r.SourceDocumentType,
                SourceDocumentUuid = r.SourceDocumentUuid, SourceDocumentNumber = r.SourceDocumentNumber,
                RequestedByName = r.RequestedByName, RequestedAt = r.RequestedAt, Status = r.Status
            })
            .ToListAsync(ct);
        return Page(rows, page, pageSize);
    }

    // ══ Shared with dunning ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Puts the profile on hold (tracked, unsaved). Used by the hold action and by dunning (BR-DN-005).</summary>
    internal void ApplyHold(PartnerFinancialProfile profile, string reason, int? userId, string? userName)
    {
        profile.CreditHold       = true;
        profile.CreditHoldReason = Truncate(reason, 500);
        profile.CreditHoldAt     = _clock.GetUtcNow().UtcDateTime;
        profile.CreditHoldBy     = userId;
        profile.CreditHoldByName = userName;
        Touch(profile, userId, userName);
    }

    /// <summary>Adds an audit row (tracked, unsaved).</summary>
    internal CreditHoldRequest Audit(
        PartnerFinancialProfile profile, string? partnerName, string action, string reason, string? previous, string? next,
        decimal? amount = null, DateOnly? expiry = null, int? userId = null, string? userName = null,
        string? docType = null, Guid? docUuid = null, string? docNumber = null)
    {
        var row = new CreditHoldRequest
        {
            OrganizationId       = profile.OrganizationId,
            Profile              = profile,
            ProfileId            = profile.Id,
            PartnerUuid          = profile.PartnerUuid,
            PartnerName          = partnerName is null ? null : Truncate(partnerName, 200),
            Action               = action,
            Reason               = Truncate(reason, 500),
            PreviousValue        = previous is null ? null : Truncate(previous, 100),
            NewValue             = next is null ? null : Truncate(next, 100),
            Amount               = amount,
            ExpiryDate           = expiry,
            SourceDocumentType   = docType,
            SourceDocumentUuid   = docUuid,
            SourceDocumentNumber = docNumber is null ? null : Truncate(docNumber, 50),
            RequestedBy          = userId,
            RequestedByName      = userName,
            RequestedAt          = _clock.GetUtcNow().UtcDateTime,
            Status               = ArApEnums.CreditHoldRequestStatus.Completed
        };
        _db.Set<CreditHoldRequest>().Add(row);
        return row;
    }

    /// <summary>BR-CR-005 — an expired temporary limit is cleared (with a TempExpired row). True when something changed.</summary>
    internal Task<bool> ExpireTemporaryLimitAsync(PartnerFinancialProfile profile, string? partnerName)
    {
        if (profile.TemporaryCreditLimit is null || profile.TemporaryCreditExpiry is not { } e || e >= Today)
            return Task.FromResult(false);

        var previous = Describe(profile.TemporaryCreditLimit, profile.TemporaryCreditExpiry);
        profile.TemporaryCreditLimit  = null;
        profile.TemporaryCreditExpiry = null;
        Audit(profile, partnerName, ArApEnums.CreditHoldAction.TempExpired, "Temporary limit expired", previous, "None");
        return Task.FromResult(true);
    }

    internal async Task<string> ModeAsync(Guid org, CancellationToken ct) =>
        CreditControlModesNormalize(await _db.OrgSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == org).Select(s => s.CreditControlMode).FirstOrDefaultAsync(ct));

    internal async Task<string?> BaseCodeAsync(Guid org, CancellationToken ct)
    {
        if (_currency is null) return null;
        try
        {
            var baseId = await _currency.GetBaseCurrencyIdAsync(org, TransactionDomain.Sale, ct);
            if (_orgCurrencies is not null && await _orgCurrencies.GetAsync(org, baseId, ct) is { } info) return info.Code;
            return (await _currency.LockRateAsync(org, baseId, Today, TransactionDomain.Sale, ct)).BaseCurrencyCode;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "The sale base currency of {Org} could not be read.", org);
            return null;
        }
    }

    // ══ Helpers ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Task 126 — the profile is created on first check (defaults: no hold, Medium). A partner the profile service cannot
    /// resolve (a host without Suppliers, a partner not flagged as customer) is checked against a default profile rather
    /// than failing the confirmation.
    /// </summary>
    private async Task<PartnerFinancialProfile> CheckProfileAsync(Guid org, Guid customer, CancellationToken ct)
    {
        try
        {
            return await _profiles.GetOrCreateAsync(org, customer, ArApEnums.PartnerRole.Customer, ct);
        }
        catch (Exception ex) when (ex is NotFoundException or BadRequestException)
        {
            _log.LogInformation(ex, "No financial profile for customer {Customer}; checked with the defaults.", customer);
            return new PartnerFinancialProfile { OrganizationId = org, PartnerUuid = customer };
        }
    }

    private async Task<decimal> ToBaseAsync(Guid org, decimal amount, string? currencyCode, string? baseCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(currencyCode) || baseCode is null
            || string.Equals(currencyCode.Trim(), baseCode, StringComparison.OrdinalIgnoreCase)
            || _currency is null || _orgCurrencies is null)
            return amount;
        try
        {
            var currency = await _orgCurrencies.GetByCodeAsync(org, currencyCode.Trim(), ct);
            if (currency is null) return amount;
            var converted = await _currency.ToBaseCurrencyAsync(org, amount, currency.CurrencyId, Today, TransactionDomain.Sale, ct);
            return converted.ConvertedAmount;
        }
        catch (Exception ex)
        {
            // Contract §0: no rate → counted 1:1.
            _log.LogInformation(ex, "No rate to take {Amount} {Currency} to the sale base for a credit check; counted 1:1.", amount, currencyCode);
            return amount;
        }
    }

    private static string CreditControlModesNormalize(string? mode) =>
        ArApEnums.Canonical(mode, ArApEnums.CreditControlMode.All) ?? CreditControlModes.Off;

    private CreditExposureModel ToModel(PartnerInfo partner, PartnerFinancialProfile profile, ExposureParts parts, string mode, string? baseCode)
    {
        var limit = CreditRules.EffectiveLimit(partner.CreditLimit, profile.TemporaryCreditLimit, profile.TemporaryCreditExpiry, Today);
        return new CreditExposureModel
        {
            CustomerUuid          = partner.Uuid,
            CustomerName          = partner.Name,
            CustomerCode          = string.IsNullOrWhiteSpace(partner.Code) ? null : partner.Code,
            BaseCurrencyCode      = baseCode,
            CreditLimit           = CreditRules.HasPermanentLimit(partner.CreditLimit) ? partner.CreditLimit : null,
            TemporaryCreditLimit  = profile.TemporaryCreditLimit,
            TemporaryCreditExpiry = profile.TemporaryCreditExpiry,
            EffectiveCreditLimit  = limit,
            OpenOrdersAmount      = parts.OpenOrders,
            OpenInvoicesAmount    = parts.OpenInvoices,
            TotalExposure         = parts.Total,
            AvailableCredit       = limit is { } l ? l - parts.Total : null,
            IsOverLimit           = limit is { } lim && parts.Total > lim,
            IsOnHold              = profile.CreditHold,
            RiskTier              = profile.RiskTier,
            CreditControlMode     = mode
        };
    }

    private async Task<PartnerInfo?> PartnerAsync(Guid org, Guid uuid, CancellationToken ct)
    {
        if (_partners is null) return null;
        try { return await _partners.GetAsync(org, uuid, null, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Customer {Partner} could not be read for a credit check.", uuid);
            return null;
        }
    }

    private async Task<PartnerInfo> RequireCustomerAsync(Guid org, Guid uuid, CancellationToken ct)
    {
        var partner = _partners is null ? null : await _partners.GetAsync(org, uuid, null, ct);
        if (partner is null) throw new NotFoundException("Business partner", uuid);
        if (!partner.IsCustomer) throw new BadRequestException($"{partner.Name} is not a customer.");
        return partner;
    }

    private Guid Org()
    {
        var org = _db.TenantContext.OrganizationId;
        if (org == Guid.Empty) throw new BadRequestException("Pick an organization first: credit control is one organization's books.");
        return org;
    }

    private void Touch(PartnerFinancialProfile p, int? userId, string? userName)
    {
        p.ModifiedAt     = _clock.GetUtcNow().UtcDateTime;
        p.ModifiedBy     = userId;
        p.ModifiedByName = userName;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The customer's financial profile was changed by someone else; reload and try again.");
        }
    }

    internal static string RequireReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? throw new BadRequestException("A reason is required.") : Truncate(reason.Trim(), 500);

    private static string Describe(decimal? amount, DateOnly? until) =>
        amount is { } a ? $"{CreditRules.Money(a)} until {until:yyyy-MM-dd}" : "None";

    internal static PaginatedResponse<T> Page<T>(List<T> rows, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = pageSize <= 0 ? 25 : Math.Min(200, pageSize);
        return new PaginatedResponse<T>
        {
            Data         = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            TotalRecords = rows.Count,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (rows.Count + pageSize - 1) / pageSize
        };
    }

    internal static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

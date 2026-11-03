using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.ArAp;

/// <summary>
/// A48 D-4 (CORE, API-CONTRACT §1, §10.3) — partner financial profiles: get-or-create, read, update and list, plus the A48
/// organization settings (credit control mode). AR changes the credit/dunning fields through the tracked entity returned by
/// <see cref="GetOrCreateAsync"/> in its own audited flows.
/// </summary>
/// <summary>The controller-facing part (public types only); <see cref="IPartnerFinancialProfileService"/> adds the internal seams.</summary>
public interface IPartnerProfileEndpoints
{
    Task<PartnerFinancialProfileModel> GetModelAsync(ArApCaller caller, Guid partnerUuid, string? role, CancellationToken ct = default);

    Task<PartnerFinancialProfileModel> UpdateAsync(ArApCaller caller, Guid partnerUuid, string? role,
        UpdatePartnerFinancialProfileRequest request, CancellationToken ct = default);

    Task<PaginatedResponse<PartnerFinancialProfileModel>> ListAsync(ArApCaller caller, PartnerProfileListQuery query, CancellationToken ct = default);

    Task<ArApSettingsModel> GetSettingsAsync(ArApCaller caller, CancellationToken ct = default);

    Task<ArApSettingsModel> UpdateSettingsAsync(ArApCaller caller, ArApSettingsModel request, CancellationToken ct = default);
}

internal interface IPartnerFinancialProfileService : IPartnerProfileEndpoints
{
    /// <summary>
    /// The tracked profile of (organization, partner, role), created with defaults when missing (a concurrent create is
    /// absorbed). The partner is not looked up — the caller knows it (a document's partner may since have been deleted);
    /// the endpoints check it (404 / 400 per §1.1). 400 for an unknown role.
    /// </summary>
    Task<PartnerFinancialProfile> GetOrCreateAsync(Guid organizationId, Guid partnerUuid, string role, CancellationToken ct = default);

    /// <summary>The organization's credit-control mode; Off when no settings row exists.</summary>
    Task<string> GetCreditControlModeAsync(Guid organizationId, CancellationToken ct = default);
}

/// <summary>BR-CR-002 with D-CORE-3: what limit a customer actually has.</summary>
internal static class CreditLimits
{
    /// <summary>Whether a temporary limit applies on <paramref name="today"/> (set, and not past its expiry).</summary>
    internal static bool TemporaryActive(decimal? temporary, DateOnly? expiry, DateOnly today) =>
        temporary is > 0m && (expiry is null || expiry.Value >= today);

    /// <summary>
    /// Null = no limit: the permanent limit (BusinessPartner.CreditLimit) is null or 0 — a temporary limit means nothing
    /// without one. Else MAX(permanent, temporary) while the temporary one is active, otherwise the permanent one.
    /// </summary>
    internal static decimal? Effective(decimal? permanent, decimal? temporary, DateOnly? expiry, DateOnly today)
    {
        if (permanent is not > 0m) return null;
        return TemporaryActive(temporary, expiry, today) ? Math.Max(permanent.Value, temporary!.Value) : permanent.Value;
    }
}

internal sealed class PartnerFinancialProfileService : IPartnerFinancialProfileService
{
    internal const string MsgRoleRequired = "role is required: Customer or Vendor.";

    private static readonly string[] CustomerReaders =
        [PermissionCodes.AR_VIEW, PermissionCodes.CREDIT_CHECK, PermissionCodes.CREDIT_MANAGE, PermissionCodes.FINANCE_AR_AP_ADMIN];
    private static readonly string[] VendorReaders =
        [PermissionCodes.AP_VIEW, PermissionCodes.AP_EXECUTE, PermissionCodes.FINANCE_AR_AP_ADMIN];

    private readonly FinanceDbContext        _db;
    private readonly IPartnerLookupService?  _partners;
    private readonly TimeProvider            _clock;

    public PartnerFinancialProfileService(FinanceDbContext db, IPartnerLookupService? partners = null, TimeProvider? clock = null)
    {
        _db       = db;
        _partners = partners;
        _clock    = clock ?? TimeProvider.System;
    }

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    // ── Get-or-create ────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<PartnerFinancialProfile> GetOrCreateAsync(Guid organizationId, Guid partnerUuid, string role, CancellationToken ct = default)
    {
        role = ParseRole(role);

        var existing = await FindAsync(organizationId, partnerUuid, role, ct);
        if (existing is not null) return existing;

        var profile = new PartnerFinancialProfile
        {
            OrganizationId = organizationId, PartnerUuid = partnerUuid, PartnerRole = role, CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        _db.PartnerFinancialProfiles.Add(profile);
        try
        {
            await _db.SaveChangesAsync(ct);
            return profile;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Someone else created it a moment ago: use theirs.
            _db.Entry(profile).State = EntityState.Detached;
            return await FindAsync(organizationId, partnerUuid, role, ct)
                ?? throw new ConflictException("The partner's financial profile could not be created; try again.");
        }
    }

    private Task<PartnerFinancialProfile?> FindAsync(Guid org, Guid partnerUuid, string role, CancellationToken ct) =>
        _db.PartnerFinancialProfiles.IgnoreQueryFilters()
            .Include(p => p.DunningProcedure).Include(p => p.EarlyPaymentTerm)
            .FirstOrDefaultAsync(p => p.OrganizationId == org && p.PartnerUuid == partnerUuid && p.PartnerRole == role, ct);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };

    // ── Read / update / list ─────────────────────────────────────────────────────────────────────────────────────────

    public async Task<PartnerFinancialProfileModel> GetModelAsync(ArApCaller caller, Guid partnerUuid, string? role, CancellationToken ct = default)
    {
        var r = ParseRole(role);
        RequireReader(caller, r);
        var partner = await ResolvePartnerAsync(caller.OrganizationId, partnerUuid, r, ct);
        var profile = await GetOrCreateAsync(caller.OrganizationId, partnerUuid, r, ct);
        return await WithEffectiveDunningLevelAsync(caller.OrganizationId, ToModel(profile, partner, Today), ct);
    }

    /// <summary>
    /// A48 AR D-AR-13 (QA-3) — a customer who owes nothing reads at dunning level 0 (contract §3), whatever is stored until
    /// the next preview persists the reset. Read-only: no write on GET.
    /// </summary>
    private async Task<PartnerFinancialProfileModel> WithEffectiveDunningLevelAsync(Guid org, PartnerFinancialProfileModel m, CancellationToken ct)
    {
        if (m.Role != ArApEnums.PartnerRole.Customer || m.CurrentDunningLevel == 0) return m;
        var owing = await Receivables.EffectiveDunningLevel.OwingAsync(_db, org, [m.PartnerUuid], ct);
        m.CurrentDunningLevel = Receivables.EffectiveDunningLevel.Of(m.CurrentDunningLevel, owing.Contains(m.PartnerUuid));
        return m;
    }

    public async Task<PartnerFinancialProfileModel> UpdateAsync(ArApCaller caller, Guid partnerUuid, string? role,
        UpdatePartnerFinancialProfileRequest request, CancellationToken ct = default)
    {
        caller.RequireAny(PermissionCodes.FINANCE_AR_AP_ADMIN);
        var r = ParseRole(role);
        var partner = await ResolvePartnerAsync(caller.OrganizationId, partnerUuid, r, ct);
        var p = await GetOrCreateAsync(caller.OrganizationId, partnerUuid, r, ct);
        var org = caller.OrganizationId;

        if (!string.IsNullOrWhiteSpace(request.RowVersion))
        {
            byte[] version;
            try { version = Convert.FromBase64String(request.RowVersion); }
            catch (FormatException) { throw new BadRequestException("rowVersion is not valid."); }
            _db.Entry(p).Property(x => x.RowVersion).OriginalValue = version;
        }

        string? oldTier = null;
        if (request.RiskTier is not null)
        {
            var tier = ArApEnums.Canonical(request.RiskTier, ArApEnums.RiskTier.All)
                ?? throw new BadRequestException(ArApEnums.UnknownText("riskTier", request.RiskTier, ArApEnums.RiskTier.All));
            if (tier != p.RiskTier) { oldTier = p.RiskTier; p.RiskTier = tier; }
        }

        if (request.ClearDunningProcedure == true || request.DunningProcedureUuid == Guid.Empty)
        {
            p.DunningProcedureId = null;
            p.DunningProcedure   = null;
        }
        else if (request.DunningProcedureUuid is { } procUuid)
        {
            var proc = await _db.DunningProcedures.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.OrganizationId == org && x.Uuid == procUuid, ct)
                ?? throw new BadRequestException($"Dunning procedure with key '{procUuid}' was not found.");
            if (!proc.IsActive) throw new BadRequestException($"Dunning procedure {proc.Code} is inactive.");
            p.DunningProcedureId = proc.Id;
            p.DunningProcedure   = proc;
        }

        if (request.ClearEarlyPaymentTerm == true || request.EarlyPaymentTermUuid == Guid.Empty)
        {
            p.EarlyPaymentTermId = null;
            p.EarlyPaymentTerm   = null;
        }
        else if (request.EarlyPaymentTermUuid is { } termUuid)
        {
            var term = await _db.EarlyPaymentTerms.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.OrganizationId == org && x.Uuid == termUuid, ct)
                ?? throw new BadRequestException($"Early payment term with key '{termUuid}' was not found.");
            if (!term.IsActive) throw new BadRequestException($"Early payment term {term.Code} is inactive.");
            p.EarlyPaymentTermId = term.Id;
            p.EarlyPaymentTerm   = term;
        }

        if (request.BlockSalesAtDunning is { } block)  p.BlockSalesAtDunning = block;
        if (request.ExcludeFromDunning is { } exclude) p.ExcludeFromDunning  = exclude;
        if (request.WithholdingTaxApplicable is { } wht) p.WithholdingTaxApplicable = wht;

        if (request.DefaultPaymentMethod is not null)
            p.DefaultPaymentMethod = PaymentMethodOrNull(request.DefaultPaymentMethod, CustomerPaymentMethods.All);
        if (request.PreferredPaymentMethod is not null)
            p.PreferredPaymentMethod = PaymentMethodOrNull(request.PreferredPaymentMethod, ArApEnums.ApPaymentMethods);
        if (request.PaymentPriority is not null)
            p.PaymentPriority = ArApEnums.Canonical(request.PaymentPriority, ArApEnums.PaymentPriority.All)
                ?? throw new BadRequestException(ArApEnums.UnknownText("paymentPriority", request.PaymentPriority, ArApEnums.PaymentPriority.All));

        if (request.Notes is not null)
        {
            var notes = request.Notes.Trim();
            if (notes.Length > 1000) throw new BadRequestException("Notes cannot be longer than 1000 characters.");
            p.Notes = notes.Length == 0 ? null : notes;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        p.ModifiedAt     = now;
        p.ModifiedBy     = caller.UserIdOrNull;
        p.ModifiedByName = caller.UserName;

        if (oldTier is not null)
            _db.CreditHoldRequests.Add(new CreditHoldRequest
            {
                OrganizationId = org, Profile = p, PartnerUuid = partnerUuid, PartnerName = partner?.Name,
                Action = ArApEnums.CreditHoldAction.RiskTier, Reason = "Changed in partner profile",
                PreviousValue = oldTier, NewValue = p.RiskTier, RequestedAt = now,
                RequestedBy = caller.UserIdOrNull, RequestedByName = caller.UserName,
                Status = ArApEnums.CreditHoldRequestStatus.Completed,
            });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The partner's financial profile was changed by someone else; reload and try again.");
        }
        return await WithEffectiveDunningLevelAsync(org, ToModel(p, partner, Today), ct);
    }

    public async Task<PaginatedResponse<PartnerFinancialProfileModel>> ListAsync(ArApCaller caller, PartnerProfileListQuery query, CancellationToken ct = default)
    {
        var role = ParseRole(query.Role);
        RequireReader(caller, role);
        var org = caller.OrganizationId;

        var q = _db.PartnerFinancialProfiles.IgnoreQueryFilters().AsNoTracking()
            .Include(p => p.DunningProcedure).Include(p => p.EarlyPaymentTerm)
            .Where(p => p.OrganizationId == org && p.PartnerRole == role);
        if (query.OnHold is { } onHold) q = q.Where(p => p.CreditHold == onHold);
        if (query.RiskTier is not null)
        {
            var tier = ArApEnums.Canonical(query.RiskTier, ArApEnums.RiskTier.All)
                ?? throw new BadRequestException(ArApEnums.UnknownText("riskTier", query.RiskTier, ArApEnums.RiskTier.All));
            q = q.Where(p => p.RiskTier == tier);
        }
        // The stored level is an upper bound of the effective one (D-AR-13): filter on it here, again on the effective level below.
        if (query.MinDunningLevel is { } minLevel) q = q.Where(p => p.CurrentDunningLevel >= minLevel);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            if (_partners is null) return Page([], 0, query);
            var hits = (await _partners.SearchAsync(org, query.Search.Trim(), role, 1000, ct)).Select(h => h.Uuid).ToList();
            q = q.Where(p => hits.Contains(p.PartnerUuid));
        }

        var rows = await q.ToListAsync(ct);
        var partners = _partners is null
            ? new Dictionary<Guid, PartnerInfo>()
            : await _partners.GetManyAsync(org, rows.Select(r => r.PartnerUuid), null, ct);
        var today = Today;

        // A48 AR D-AR-13 — customers who owe nothing read at dunning level 0.
        var owing = role == ArApEnums.PartnerRole.Customer
            ? await Receivables.EffectiveDunningLevel.OwingAsync(_db, org, rows.Where(r => r.CurrentDunningLevel > 0).Select(r => r.PartnerUuid).ToList(), ct)
            : null;

        var models = rows
            .Select(r => ToModel(r, partners.GetValueOrDefault(r.PartnerUuid), today))
            .Select(m =>
            {
                if (owing is not null && m.CurrentDunningLevel > 0)
                    m.CurrentDunningLevel = Receivables.EffectiveDunningLevel.Of(m.CurrentDunningLevel, owing.Contains(m.PartnerUuid));
                return m;
            })
            .Where(m => query.MinDunningLevel is not { } min || m.CurrentDunningLevel >= min)
            .OrderBy(m => m.PartnerName ?? "￿", StringComparer.OrdinalIgnoreCase).ThenBy(m => m.PartnerUuid)
            .ToList();
        return Page(models, models.Count, query);
    }

    private static PaginatedResponse<PartnerFinancialProfileModel> Page(List<PartnerFinancialProfileModel> all, int total, PartnerProfileListQuery query)
    {
        var page     = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, 200);
        return new PaginatedResponse<PartnerFinancialProfileModel>
        {
            Data         = all.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize),
        };
    }

    // ── Settings ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<string> GetCreditControlModeAsync(Guid organizationId, CancellationToken ct = default)
    {
        var mode = await _db.OrgSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId).Select(s => s.CreditControlMode).FirstOrDefaultAsync(ct);
        return ArApEnums.Canonical(mode, ArApEnums.CreditControlMode.All) ?? CreditControlModes.Off;
    }

    public async Task<ArApSettingsModel> GetSettingsAsync(ArApCaller caller, CancellationToken ct = default)
    {
        caller.RequireAny([.. PermissionCodes.ArApCodes]);
        return new ArApSettingsModel { CreditControlMode = await GetCreditControlModeAsync(caller.OrganizationId, ct) };
    }

    public async Task<ArApSettingsModel> UpdateSettingsAsync(ArApCaller caller, ArApSettingsModel request, CancellationToken ct = default)
    {
        caller.RequireAny(PermissionCodes.FINANCE_AR_AP_ADMIN);
        var mode = ArApEnums.Canonical(request.CreditControlMode, ArApEnums.CreditControlMode.All)
            ?? throw new BadRequestException(ArApEnums.UnknownText("creditControlMode", request.CreditControlMode, ArApEnums.CreditControlMode.All));

        var org = caller.OrganizationId;
        var now = _clock.GetUtcNow().UtcDateTime;
        var row = await _db.OrgSettings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.OrganizationId == org, ct);
        if (row is null)
        {
            row = new FinanceOrgSettings { OrganizationId = org, CreatedBy = caller.UserId, CreatedDate = now };
            _db.OrgSettings.Add(row);
        }
        row.CreditControlMode = mode;
        row.ModifiedBy   = caller.UserIdOrNull;
        row.ModifiedDate = now;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Created concurrently: apply to that row.
            _db.Entry(row).State = EntityState.Detached;
            var other = await _db.OrgSettings.IgnoreQueryFilters().FirstAsync(s => s.OrganizationId == org, ct);
            other.CreditControlMode = mode;
            other.ModifiedBy   = caller.UserIdOrNull;
            other.ModifiedDate = now;
            await _db.SaveChangesAsync(ct);
        }
        return new ArApSettingsModel { CreditControlMode = mode };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    internal static string ParseRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) throw new BadRequestException(MsgRoleRequired);
        return ArApEnums.Canonical(role, ArApEnums.PartnerRole.All)
            ?? throw new BadRequestException(ArApEnums.UnknownText("role", role, ArApEnums.PartnerRole.All));
    }

    private static void RequireReader(ArApCaller caller, string role) =>
        caller.RequireAny(role == ArApEnums.PartnerRole.Customer ? CustomerReaders : VendorReaders);

    /// <summary>The partner (404 / 400 per §1.1), or null when no lookup is registered (a host without Suppliers).</summary>
    private async Task<PartnerInfo?> ResolvePartnerAsync(Guid org, Guid partnerUuid, string role, CancellationToken ct)
    {
        if (_partners is null) return null;
        var partner = await _partners.GetAsync(org, partnerUuid, null, ct)
            ?? throw new NotFoundException("Business partner", partnerUuid);
        if (role == ArApEnums.PartnerRole.Customer && !partner.IsCustomer) throw new BadRequestException($"{partner.Name} is not a customer.");
        if (role == ArApEnums.PartnerRole.Vendor && !partner.IsVendor) throw new BadRequestException($"{partner.Name} is not a vendor.");
        return partner;
    }

    private static string? PaymentMethodOrNull(string value, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return ArApEnums.Canonical(value, allowed)
            ?? throw new BadRequestException(ArApEnums.UnknownText("payment method", value, allowed));
    }

    internal static PartnerFinancialProfileModel ToModel(PartnerFinancialProfile p, PartnerInfo? partner, DateOnly today)
    {
        var permanent = partner?.CreditLimit;
        return new PartnerFinancialProfileModel
        {
            Uuid                  = p.Uuid,
            PartnerUuid           = p.PartnerUuid,
            Role                  = p.PartnerRole,
            PartnerCode           = partner?.Code,
            PartnerName           = partner?.Name,
            PartnerEmail          = partner?.Email,
            PartnerIsActive       = partner?.IsActive ?? false,
            CreditLimit           = permanent,
            TemporaryCreditLimit  = p.TemporaryCreditLimit,
            TemporaryCreditExpiry = Date(p.TemporaryCreditExpiry),
            TemporaryLimitActive  = permanent is > 0m && CreditLimits.TemporaryActive(p.TemporaryCreditLimit, p.TemporaryCreditExpiry, today),
            EffectiveCreditLimit  = CreditLimits.Effective(permanent, p.TemporaryCreditLimit, p.TemporaryCreditExpiry, today),
            CreditHold            = p.CreditHold,
            CreditHoldReason      = p.CreditHoldReason,
            CreditHoldAt          = p.CreditHoldAt,
            CreditHoldByName      = p.CreditHoldByName,
            RiskTier              = p.RiskTier,
            DunningProcedureUuid  = p.DunningProcedure?.Uuid,
            DunningProcedureCode  = p.DunningProcedure?.Code,
            CurrentDunningLevel   = p.CurrentDunningLevel,
            LastDunningDate       = Date(p.LastDunningDate),
            BlockSalesAtDunning   = p.BlockSalesAtDunning,
            ExcludeFromDunning    = p.ExcludeFromDunning,
            DefaultPaymentMethod  = p.DefaultPaymentMethod,
            PaymentPriority       = p.PaymentPriority,
            PreferredPaymentMethod   = p.PreferredPaymentMethod,
            WithholdingTaxApplicable = p.WithholdingTaxApplicable,
            EarlyPaymentTermUuid  = p.EarlyPaymentTerm?.Uuid,
            EarlyPaymentTermCode  = p.EarlyPaymentTerm?.Code,
            PaymentTermsDays      = partner?.PaymentTermsDays,
            LastPaymentDate       = Date(p.LastPaymentDate),
            AverageDaysToPay      = p.AverageDaysToPay,
            TotalOverdueAmount    = p.TotalOverdueAmount,
            StatisticsUpdatedAt   = p.StatisticsUpdatedAt,
            Notes                 = p.Notes,
            CreatedAt             = p.CreatedAt,
            ModifiedAt            = p.ModifiedAt,
            ModifiedByName        = p.ModifiedByName,
            RowVersion            = p.RowVersion is { Length: > 0 } rv ? Convert.ToBase64String(rv) : null,
        };
    }

    private static string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd");
}

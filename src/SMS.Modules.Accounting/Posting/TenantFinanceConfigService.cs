using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Posting;

/// <summary>
/// D-8 — the organization's finance posting settings (<c>finance.TenantFinanceConfigs</c>, one row per organization, created
/// on the first PUT; a GET without a row answers the defaults and creates nothing). Also answers whether the internal GL
/// takes postings (D-10): FEATURE_GENERAL_LEDGER on, InternalAccountingEnabled, A42 setup Complete.
/// </summary>
public interface ITenantFinanceConfigService
{
    Task<PostingConfigModel> GetAsync(AccountingCaller caller, CancellationToken ct = default);
    Task<PostingConfigModel> UpdateAsync(AccountingCaller caller, UpdatePostingConfigRequest request, CancellationToken ct = default);
}

internal sealed class TenantFinanceConfigService : ITenantFinanceConfigService
{
    internal const string MonthMessage = "The fiscal year start month must be between 1 and 12.";

    private readonly AccountingDbContext _db;
    private readonly GlReadiness _readiness;
    private readonly FiscalBaseCurrency? _baseCurrency;

    public TenantFinanceConfigService(AccountingDbContext db, GlReadiness readiness, FiscalBaseCurrency? baseCurrency = null)
    {
        _db = db;
        _readiness = readiness;
        _baseCurrency = baseCurrency;
    }

    public async Task<PostingConfigModel> GetAsync(AccountingCaller caller, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var row = await _db.TenantFinanceConfigs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.OrganizationId == caller.OrganizationId, ct);
        // REV-7 — a read creates nothing: no row = the defaults (modifiedAt null).
        return await ToModelAsync(row ?? new TenantFinanceConfig { OrganizationId = caller.OrganizationId }, row is null, ct);
    }

    public async Task<PostingConfigModel> UpdateAsync(AccountingCaller caller, UpdatePostingConfigRequest request, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        ArgumentNullException.ThrowIfNull(request);
        if (request.FiscalYearStartMonth is { } month && month is < 1 or > 12) throw new BadRequestException(MonthMessage);

        var row = await EnsureAsync(caller.OrganizationId, ct);
        if (request.InternalAccountingEnabled is { } enabled) row.InternalAccountingEnabled = enabled;
        if (request.AutoPostToGL is { } auto) row.AutoPostToGL = auto;
        if (request.FiscalYearStartMonth is { } m) row.FiscalYearStartMonth = m;
        row.ModifiedBy = caller.UserIdOrNull;
        await _db.SaveChangesAsync(ct);
        return await ToModelAsync(row, false, ct);
    }

    /// <summary>Lazy init (concurrency-safe: a lost insert race re-reads the winner's row).</summary>
    internal async Task<TenantFinanceConfig> EnsureAsync(Guid org, CancellationToken ct)
    {
        var row = await _db.TenantFinanceConfigs.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.OrganizationId == org, ct);
        if (row is not null) return row;
        row = new TenantFinanceConfig { OrganizationId = org };
        _db.TenantFinanceConfigs.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
            return row;
        }
        catch (DbUpdateException)
        {
            _db.Entry(row).State = EntityState.Detached;
            return await _db.TenantFinanceConfigs.IgnoreQueryFilters().SingleAsync(c => c.OrganizationId == org, ct);
        }
    }

    private async Task<PostingConfigModel> ToModelAsync(TenantFinanceConfig row, bool unsaved, CancellationToken ct)
    {
        var state = await _readiness.GetAsync(row.OrganizationId, ct);
        var currency = row.DefaultCurrencyCode;
        if (currency is null && _baseCurrency is not null) currency = await _baseCurrency.GetCodeAsync(row.OrganizationId, ct);
        currency ??= await _readiness.ReportingCurrencyAsync(row.OrganizationId, DateOnly.FromDateTime(DateTime.UtcNow), ct);
        return new PostingConfigModel
        {
            InternalAccountingEnabled = row.InternalAccountingEnabled,
            AutoPostToGL              = row.AutoPostToGL,
            DefaultCurrencyCode       = currency,
            FiscalYearStartMonth      = row.FiscalYearStartMonth,
            GlFeatureEnabled          = state.FeatureOn,
            SetupStatus               = state.SetupStatus,
            GlReady                   = state.Ready,
            GlActive                  = state.Ready && row.InternalAccountingEnabled,
            NotReadyReason            = state.NotReadyReason,
            ModifiedAt                = unsaved ? null : row.ModifiedAt,
        };
    }
}

/// <summary>D-10 — is the internal GL usable for an organization (feature on and setup Complete), and the settings row.</summary>
internal sealed class GlReadiness
{
    internal const string FeatureOff = "FEATURE_OFF", SetupIncomplete = "SETUP_INCOMPLETE";

    private readonly AccountingDbContext _db;
    private readonly IModuleGate? _gate;

    public GlReadiness(AccountingDbContext db, IModuleGate? gate = null)
    {
        _db = db;
        _gate = gate;
    }

    internal sealed record State(bool FeatureOn, string SetupStatus, bool Ready, string? NotReadyReason);

    public async Task<State> GetAsync(Guid org, CancellationToken ct = default)
    {
        var featureOn = await _gate.IsOnAsync(org, AccountingCodes.FeatureGeneralLedger, ct);
        var status = await _db.CoaSetupStatuses.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == org).Select(s => s.Status).FirstOrDefaultAsync(ct) ?? SetupStatuses.NotStarted;
        var complete = status == SetupStatuses.Complete;
        return new State(featureOn, status, featureOn && complete, !featureOn ? FeatureOff : !complete ? SetupIncomplete : null);
    }

    /// <summary>The settings without creating a row (defaults when there is none).</summary>
    public async Task<(bool InternalAccountingEnabled, bool AutoPostToGL)> SettingsAsync(Guid org, CancellationToken ct = default)
    {
        var row = await _db.TenantFinanceConfigs.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.OrganizationId == org, ct);
        return row is null ? (false, true) : (row.InternalAccountingEnabled, row.AutoPostToGL);
    }

    /// <summary>The reporting currency of the fiscal year containing <paramref name="date"/>, else of the latest year.</summary>
    public async Task<string?> ReportingCurrencyAsync(Guid org, DateOnly date, CancellationToken ct = default)
    {
        var years = _db.FiscalYears.IgnoreQueryFilters().AsNoTracking().Where(y => y.OrganizationId == org);
        return await years.Where(y => y.StartDate <= date && date <= y.EndDate).Select(y => y.ReportingCurrencyCode).FirstOrDefaultAsync(ct)
               ?? await years.OrderByDescending(y => y.StartDate).Select(y => y.ReportingCurrencyCode).FirstOrDefaultAsync(ct);
    }
}

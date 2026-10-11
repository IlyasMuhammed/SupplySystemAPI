using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Hubs;
using SMS.Modules.Pos.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Pos.Services;

/// <summary>
/// A39 API-CONTRACT §1 / §5 — return reasons and return policies. D-4: the 9 reasons and the organization default policy are
/// seeded per organization lazily (first read, or when a return needs them) — never by a startup data migration.
/// Policy resolution: the store's active override → the active org default → none.
/// </summary>
public interface IPosReturnPolicyService
{
    Task<IReadOnlyList<PosReturnReasonModel>> ListReasonsAsync(PosReasonListFilter filter, CancellationToken ct = default);
    Task<PosReturnReasonModel> CreateReasonAsync(PosReturnReasonUpsertRequest req, CancellationToken ct = default);
    Task<PosReturnReasonModel> UpdateReasonAsync(Guid uuid, PosReturnReasonUpsertRequest req, CancellationToken ct = default);
    Task<PosReturnReasonModel> SetReasonStatusAsync(Guid uuid, bool isActive, CancellationToken ct = default);
    Task<PosReasonSyncModel> SyncReasonsAsync(DateTime? since, CancellationToken ct = default);

    Task<PosEffectivePolicyModel> GetEffectiveAsync(Guid storeUuid, CancellationToken ct = default);
    Task<PosEffectivePolicyModel> GetEffectiveForTerminalAsync(Guid terminalUuid, CancellationToken ct = default);
    Task<PosReturnPolicyModel> PutStorePolicyAsync(Guid storeUuid, PosReturnPolicyUpsertRequest req, int userId, CancellationToken ct = default);
    Task DeleteStorePolicyAsync(Guid storeUuid, CancellationToken ct = default);
    Task<IReadOnlyList<PosReturnPolicyModel>> ListPoliciesAsync(CancellationToken ct = default);
    Task<PosReturnPolicyModel> PutDefaultPolicyAsync(PosReturnPolicyUpsertRequest req, int userId, CancellationToken ct = default);
}

internal sealed class PosReturnPolicyService : IPosReturnPolicyService
{
    internal const string NoPolicyMessage = "No return policy configured. Contact your administrator.";
    internal const string SourceStore = "STORE", SourceOrgDefault = "ORG_DEFAULT";
    private static readonly Regex CodePattern = new("^[A-Z0-9_]{1,20}$", RegexOptions.Compiled);

    /// <summary>D-4 / POSR D-13 — the seeded reasons (code, name, note, manager, appliesTo, fee override).</summary>
    internal static readonly IReadOnlyList<(string Code, string Name, bool Note, bool Manager, string AppliesTo, decimal? Fee)> SeedReasons =
    [
        ("DEFECTIVE",       "Defective / damaged",      false, false, PosReasonAppliesTo.Both,   0m),
        ("WRONG_ITEM",      "Wrong item",               false, false, PosReasonAppliesTo.Both,   0m),
        ("WRONG_SIZE",      "Wrong size / colour",      false, false, PosReasonAppliesTo.Both,   null),
        ("NOT_AS_EXPECTED", "Not as expected",          true,  false, PosReasonAppliesTo.Both,   null),
        ("CHANGED_MIND",    "Changed mind",             false, false, PosReasonAppliesTo.Both,   null),
        ("DUPLICATE",       "Duplicate purchase",       false, false, PosReasonAppliesTo.Return, 0m),
        ("PRICE_MATCH",     "Price match",              true,  true,  PosReasonAppliesTo.Return, 0m),
        ("WARRANTY",        "Warranty claim",           true,  true,  PosReasonAppliesTo.Both,   0m),
        ("OTHER",           "Other",                    true,  false, PosReasonAppliesTo.Both,   null)
    ];

    private readonly PosDbContext    _db;
    private readonly ITenantContext  _tenant;
    private readonly PosReadModel    _read;
    private readonly IPosHubNotifier _hub;

    public PosReturnPolicyService(PosDbContext db, ITenantContext tenant, PosReadModel read, IPosHubNotifier hub)
    {
        _db = db; _tenant = tenant; _read = read; _hub = hub;
    }

    private Guid Org => _tenant.OrganizationId;

    // ══ Seed (D-4) ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates the 9 reasons and the org default policy when the organization has none. Safe under concurrency: a loser on
    /// the unique indexes detaches its own rows (the caller's other tracked entities are left alone).
    /// </summary>
    internal static async Task EnsureSeededAsync(PosDbContext db, Guid org, DateTime now, CancellationToken ct = default)
    {
        var added = new List<object>();
        if (!await db.ReturnReasons.IgnoreQueryFilters().AnyAsync(r => r.OrganizationId == org, ct))
            foreach (var (s, i) in SeedReasons.Select((s, i) => (s, i)))
            {
                var r = new PosReturnReason
                {
                    OrganizationId = org, Code = s.Code, Name = s.Name, RequiresNote = s.Note, RequiresManager = s.Manager,
                    AppliesTo = s.AppliesTo, RestockingFeeOverride = s.Fee, SortOrder = (i + 1) * 10, CreatedAt = now, ModifiedAt = now
                };
                db.ReturnReasons.Add(r);
                added.Add(r);
            }
        if (!await db.ReturnPolicies.IgnoreQueryFilters().AnyAsync(p => p.OrganizationId == org && p.StoreId == null, ct))
        {
            var p = DefaultPolicy(org, now);
            db.ReturnPolicies.Add(p);
            added.Add(p);
        }
        if (added.Count == 0) return;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another request seeded the same organization first.
            foreach (var entity in added) db.Entry(entity).State = EntityState.Detached;
        }
    }

    /// <summary>POSR D-13 — the org default policy's values.</summary>
    internal static PosReturnPolicy DefaultPolicy(Guid org, DateTime now) => new()
    {
        OrganizationId = org, StoreId = null, Name = "Organization default", ReturnWindowDays = 30, ExchangeWindowDays = null,
        RequireReceipt = true, AllowNoReceiptExchange = false, AllowCrossStoreReturn = true, CrossStoreRequiresManager = false,
        RestockingFeePct = 0m, MaxReturnNoAuth = null, RefundToOriginalMethod = false, AllowCashRefund = true, AllowCreditNoteRefund = true,
        CreditNoteExpiryDays = 365, IsActive = true, CreatedAt = now, ModifiedAt = now
    };

    /// <summary>D-4 resolution (after seeding): the store's active override, else the active org default, else null.</summary>
    internal static async Task<(PosReturnPolicy? Policy, string Source)> ResolveAsync(PosDbContext db, Guid org, int storeId, DateTime now, CancellationToken ct = default)
    {
        await EnsureSeededAsync(db, org, now, ct);
        var policies = await db.ReturnPolicies.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == org && (p.StoreId == storeId || p.StoreId == null) && p.IsActive).ToListAsync(ct);
        if (policies.FirstOrDefault(p => p.StoreId == storeId) is { } own) return (own, SourceStore);
        if (policies.FirstOrDefault(p => p.StoreId == null) is { } def) return (def, SourceOrgDefault);
        return (null, string.Empty);
    }

    // ══ Reasons ════════════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<PosReturnReasonModel>> ListReasonsAsync(PosReasonListFilter f, CancellationToken ct = default)
    {
        await EnsureSeededAsync(_db, Org, _read.UtcNow, ct);
        var q = _db.ReturnReasons.AsNoTracking();
        if (!f.IncludeInactive) q = q.Where(r => r.IsActive);
        var kind = f.AppliesTo?.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(kind))
        {
            if (!PosReasonAppliesTo.IsValid(kind)) throw new BadRequestException("appliesTo must be BOTH, RETURN or EXCHANGE.");
            if (kind != PosReasonAppliesTo.Both) q = q.Where(r => r.AppliesTo == PosReasonAppliesTo.Both || r.AppliesTo == kind);
        }
        return (await q.OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync(ct)).Select(ReasonModel).ToList();
    }

    public async Task<PosReturnReasonModel> CreateReasonAsync(PosReturnReasonUpsertRequest req, CancellationToken ct = default)
    {
        await EnsureSeededAsync(_db, Org, _read.UtcNow, ct);
        var reason = new PosReturnReason { OrganizationId = Org, CreatedAt = _read.UtcNow };
        await ApplyReasonAsync(reason, req, ct);
        _db.ReturnReasons.Add(reason);
        await SaveReasonAsync(reason.Code, ct);
        return ReasonModel(reason);
    }

    public async Task<PosReturnReasonModel> UpdateReasonAsync(Guid uuid, PosReturnReasonUpsertRequest req, CancellationToken ct = default)
    {
        var reason = await _db.ReturnReasons.FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Return reason", uuid);
        await ApplyReasonAsync(reason, req, ct);
        await SaveReasonAsync(reason.Code, ct);
        return ReasonModel(reason);
    }

    public async Task<PosReturnReasonModel> SetReasonStatusAsync(Guid uuid, bool isActive, CancellationToken ct = default)
    {
        var reason = await _db.ReturnReasons.FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Return reason", uuid);
        reason.IsActive = isActive;
        reason.ModifiedAt = _read.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ReasonModel(reason);
    }

    public async Task<PosReasonSyncModel> SyncReasonsAsync(DateTime? since, CancellationToken ct = default)
    {
        var now = _read.UtcNow;
        await EnsureSeededAsync(_db, Org, now, ct);
        var q = _db.ReturnReasons.AsNoTracking();
        if (since is { } s) q = q.Where(r => r.ModifiedAt > s);
        var reasons = await q.OrderBy(r => r.ModifiedAt).ThenBy(r => r.Id).ToListAsync(ct);
        return new PosReasonSyncModel
        {
            // A39 QA — UTC kind, so the cursor is sent with "Z" (unspecified ⇒ the PWA / the since binder read it as local time).
            ServerTime = now, NextSince = reasons.Count == 0 ? since : Utc(reasons.Max(r => r.ModifiedAt)), Reasons = reasons.Select(ReasonModel).ToList()
        };
    }

    private async Task ApplyReasonAsync(PosReturnReason reason, PosReturnReasonUpsertRequest req, CancellationToken ct)
    {
        var code = req.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!CodePattern.IsMatch(code)) throw new BadRequestException("Reason code must be 1 to 20 characters: A–Z, 0–9 or _.");
        var name = req.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100) throw new BadRequestException("Reason name is required (up to 100 characters).");
        var applies = string.IsNullOrWhiteSpace(req.AppliesTo) ? PosReasonAppliesTo.Both : req.AppliesTo.Trim().ToUpperInvariant();
        if (!PosReasonAppliesTo.IsValid(applies)) throw new BadRequestException("appliesTo must be BOTH, RETURN or EXCHANGE.");
        if (req.RestockingFeeOverride is { } fee && (fee < 0m || fee > 100m || decimal.Round(fee, 2) != fee))
            throw new BadRequestException("The restocking fee override must be between 0 and 100 with at most 2 decimals.");
        var description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
        if (description is { Length: > 500 }) throw new BadRequestException("The description has at most 500 characters.");
        if (await _db.ReturnReasons.AnyAsync(r => r.Code == code && r.Id != reason.Id, ct))
            throw new BadRequestException($"Reason code {code} already exists.");

        reason.Code = code; reason.Name = name; reason.Description = description; reason.RequiresNote = req.RequiresNote;
        reason.RequiresManager = req.RequiresManager; reason.AppliesTo = applies; reason.RestockingFeeOverride = req.RestockingFeeOverride;
        reason.SortOrder = req.SortOrder; reason.IsActive = req.IsActive; reason.ModifiedAt = _read.UtcNow;
    }

    private async Task SaveReasonAsync(string code, CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (_db.Database.IsRelational()) { throw new BadRequestException($"Reason code {code} already exists."); }
    }

    internal static PosReturnReasonModel ReasonModel(PosReturnReason r) => new()
    {
        Uuid = r.Uuid, Code = r.Code, Name = r.Name, Description = r.Description, RequiresNote = r.RequiresNote, RequiresManager = r.RequiresManager,
        AppliesTo = r.AppliesTo, RestockingFeeOverride = r.RestockingFeeOverride, SortOrder = r.SortOrder, IsActive = r.IsActive, ModifiedAt = Utc(r.ModifiedAt)
    };

    /// <summary>Stored values are UTC; EF reads them back unspecified.</summary>
    internal static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    // ══ Policies ═══════════════════════════════════════════════════════════════════════════════════════════════════

    public async Task<PosEffectivePolicyModel> GetEffectiveAsync(Guid storeUuid, CancellationToken ct = default)
    {
        var store = await _db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Uuid == storeUuid, ct) ?? throw new NotFoundException("Store", storeUuid);
        return await EffectiveModelAsync(store, ct);
    }

    public async Task<PosEffectivePolicyModel> GetEffectiveForTerminalAsync(Guid terminalUuid, CancellationToken ct = default)
    {
        var terminal = await _db.Terminals.AsNoTracking().Include(t => t.Store).FirstOrDefaultAsync(t => t.Uuid == terminalUuid, ct)
            ?? throw new NotFoundException("Terminal", terminalUuid);
        return await EffectiveModelAsync(terminal.Store, ct);
    }

    private async Task<PosEffectivePolicyModel> EffectiveModelAsync(PosStore store, CancellationToken ct)
    {
        var (policy, source) = await ResolveAsync(_db, Org, store.Id, _read.UtcNow, ct);
        if (policy is null) throw new BadRequestException(NoPolicyMessage);
        var names = await StoreNamesAsync(ct);
        return Fill(new PosEffectivePolicyModel { Source = source }, policy, names);
    }

    public async Task<PosReturnPolicyModel> PutStorePolicyAsync(Guid storeUuid, PosReturnPolicyUpsertRequest req, int userId, CancellationToken ct = default)
    {
        var store = await _db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Uuid == storeUuid, ct) ?? throw new NotFoundException("Store", storeUuid);
        await EnsureSeededAsync(_db, Org, _read.UtcNow, ct);
        var policy = await _db.ReturnPolicies.FirstOrDefaultAsync(p => p.StoreId == store.Id, ct);
        if (policy is null)
        {
            policy = new PosReturnPolicy { OrganizationId = Org, StoreId = store.Id, CreatedAt = _read.UtcNow };
            _db.ReturnPolicies.Add(policy);
        }
        await SavePolicyAsync(policy, req, userId, $"{store.Name} return policy", ct);
        await _hub.StoreEventAsync(Org, store.Uuid, PosHubEvents.ReturnPolicyChanged, new { storeUuid = store.Uuid });
        return Fill(new PosReturnPolicyModel(), policy, await StoreNamesAsync(ct));
    }

    public async Task DeleteStorePolicyAsync(Guid storeUuid, CancellationToken ct = default)
    {
        var store = await _db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Uuid == storeUuid, ct) ?? throw new NotFoundException("Store", storeUuid);
        var policy = await _db.ReturnPolicies.FirstOrDefaultAsync(p => p.StoreId == store.Id, ct);
        if (policy is null) return;
        _db.ReturnPolicies.Remove(policy);
        await _db.SaveChangesAsync(ct);
        await _hub.StoreEventAsync(Org, store.Uuid, PosHubEvents.ReturnPolicyChanged, new { storeUuid = store.Uuid });
    }

    public async Task<IReadOnlyList<PosReturnPolicyModel>> ListPoliciesAsync(CancellationToken ct = default)
    {
        await EnsureSeededAsync(_db, Org, _read.UtcNow, ct);
        var policies = await _db.ReturnPolicies.AsNoTracking().ToListAsync(ct);
        var names = await StoreNamesAsync(ct);
        return policies.OrderBy(p => p.StoreId is null ? 0 : 1).ThenBy(p => p.StoreId is { } s ? names.GetValueOrDefault(s).Name : string.Empty)
            .Select(p => Fill(new PosReturnPolicyModel(), p, names)).ToList();
    }

    public async Task<PosReturnPolicyModel> PutDefaultPolicyAsync(PosReturnPolicyUpsertRequest req, int userId, CancellationToken ct = default)
    {
        await EnsureSeededAsync(_db, Org, _read.UtcNow, ct);
        var policy = await _db.ReturnPolicies.FirstAsync(p => p.StoreId == null, ct);
        await SavePolicyAsync(policy, req, userId, "Organization default", ct);
        // Every store without its own policy follows the default: tell them all.
        var stores = await _db.Stores.AsNoTracking().Where(s => s.IsActive).Select(s => s.Uuid).ToListAsync(ct);
        foreach (var s in stores) await _hub.StoreEventAsync(Org, s, PosHubEvents.ReturnPolicyChanged, new { storeUuid = s });
        return Fill(new PosReturnPolicyModel(), policy, await StoreNamesAsync(ct));
    }

    private async Task SavePolicyAsync(PosReturnPolicy p, PosReturnPolicyUpsertRequest req, int userId, string defaultName, CancellationToken ct)
    {
        if (req.RowVersion is { Length: > 0 } rv && p.Id != 0)
        {
            if (_db.Database.IsRelational()) _db.Entry(p).Property(x => x.RowVersion).OriginalValue = rv;
            else if (p.RowVersion is { Length: > 0 } current && !current.SequenceEqual(rv))
                throw new ConflictException("The return policy was changed by someone else. Reload and try again.");
        }
        var name = string.IsNullOrWhiteSpace(req.Name) ? defaultName : req.Name.Trim();
        if (name.Length > 100) throw new BadRequestException("The policy name has at most 100 characters.");
        if (req.ReturnWindowDays < 0 || req.ExchangeWindowDays < 0 || req.CreditNoteExpiryDays < 0)
            throw new BadRequestException("Days cannot be negative.");
        if (req.RestockingFeePct < 0m || req.RestockingFeePct > 100m || decimal.Round(req.RestockingFeePct, 2) != req.RestockingFeePct)
            throw new BadRequestException("The restocking fee must be between 0 and 100 with at most 2 decimals.");
        if (req.MaxReturnNoAuth is { } max && (max < 0m || decimal.Round(max, 2) != max))
            throw new BadRequestException("The amount without authorization cannot be negative and has at most 2 decimals.");

        p.Name = name; p.ReturnWindowDays = req.ReturnWindowDays; p.ExchangeWindowDays = req.ExchangeWindowDays;
        p.RequireReceipt = req.RequireReceipt; p.AllowNoReceiptExchange = req.AllowNoReceiptExchange;
        p.AllowCrossStoreReturn = req.AllowCrossStoreReturn; p.CrossStoreRequiresManager = req.CrossStoreRequiresManager;
        p.RestockingFeePct = req.RestockingFeePct; p.MaxReturnNoAuth = req.MaxReturnNoAuth; p.RefundToOriginalMethod = req.RefundToOriginalMethod;
        p.AllowCashRefund = req.AllowCashRefund; p.AllowCreditNoteRefund = req.AllowCreditNoteRefund;
        p.CreditNoteExpiryDays = req.CreditNoteExpiryDays; p.IsActive = req.IsActive; p.ModifiedBy = userId; p.ModifiedAt = _read.UtcNow;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The return policy was changed by someone else. Reload and try again."); }
    }

    private async Task<Dictionary<int, (Guid Uuid, string Name)>> StoreNamesAsync(CancellationToken ct) =>
        await _db.Stores.AsNoTracking().Select(s => new { s.Id, s.Uuid, s.Name }).ToDictionaryAsync(s => s.Id, s => (s.Uuid, s.Name), ct);

    internal static T Fill<T>(T m, PosReturnPolicy p, IReadOnlyDictionary<int, (Guid Uuid, string Name)> stores) where T : PosReturnPolicyModel
    {
        var store = p.StoreId is { } sid && stores.TryGetValue(sid, out var s) ? s : ((Guid Uuid, string Name)?)null;
        m.Uuid = p.Uuid; m.StoreUuid = store?.Uuid; m.StoreName = store?.Name; m.Name = p.Name; m.ReturnWindowDays = p.ReturnWindowDays;
        m.ExchangeWindowDays = p.ExchangeWindowDays; m.RequireReceipt = p.RequireReceipt; m.AllowNoReceiptExchange = p.AllowNoReceiptExchange;
        m.AllowCrossStoreReturn = p.AllowCrossStoreReturn; m.CrossStoreRequiresManager = p.CrossStoreRequiresManager;
        m.RestockingFeePct = p.RestockingFeePct; m.MaxReturnNoAuth = p.MaxReturnNoAuth; m.RefundToOriginalMethod = p.RefundToOriginalMethod;
        m.AllowCashRefund = p.AllowCashRefund; m.AllowCreditNoteRefund = p.AllowCreditNoteRefund; m.CreditNoteExpiryDays = p.CreditNoteExpiryDays;
        m.IsActive = p.IsActive; m.RowVersion = p.RowVersion; m.ModifiedAt = p.ModifiedAt;
        return m;
    }
}

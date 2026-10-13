using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Crm.Data;
using SMS.Modules.Crm.Domain;
using SMS.Modules.Crm.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Crm.Services;

/// <summary>429 from the public lead API.</summary>
public sealed class CrmRateLimitException : Exception
{
    public CrmRateLimitException(int perMinute) : base($"Too many requests: this key may create {perMinute} leads per minute.") { }
}

/// <summary>D-16 — the per-key fixed one-minute window (in memory, per API process — R-4).</summary>
public sealed class CrmApiRateLimiter
{
    private readonly ConcurrentDictionary<int, (long Window, int Count)> _windows = new();
    private readonly TimeProvider _clock;

    public CrmApiRateLimiter(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public bool TryAcquire(int keyId, int perMinute)
    {
        var window = _clock.GetUtcNow().ToUnixTimeSeconds() / 60;
        var granted = false;
        _windows.AddOrUpdate(keyId,
            _ => { granted = perMinute > 0; return (window, 1); },
            (_, cur) =>
            {
                if (cur.Window != window) { granted = perMinute > 0; return (window, 1); }
                granted = cur.Count < perMinute;
                return granted ? (window, cur.Count + 1) : cur;
            });
        return granted;
    }
}

/// <summary>A41 §14.6 / D-16 — keys of the public lead API, managed by CRM_ADMIN.</summary>
public interface ILeadApiKeyService
{
    Task<IReadOnlyList<LeadApiKeyModel>> ListAsync(CrmCaller caller, CancellationToken ct = default);
    Task<LeadApiKeyCreated> CreateAsync(CrmCaller caller, LeadApiKeyUpsert req, CancellationToken ct = default);
    Task<LeadApiKeyModel> UpdateAsync(CrmCaller caller, Guid uuid, LeadApiKeyUpsert req, CancellationToken ct = default);
    Task DeleteAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
}

/// <summary>A41 §14.6 — lead creation by website forms and other systems (no user session).</summary>
public interface IPublicLeadService
{
    Task<PublicLeadResult> CreateAsync(string? apiKey, PublicLeadRequest req, CancellationToken ct = default);
}

internal sealed class LeadApiKeyService : ILeadApiKeyService, IPublicLeadService
{
    internal const string KeyPrefix = "crm_";
    private const int DefaultPerMinute = 100, MaxPerMinute = 10_000;

    private readonly CrmDbContext _db;
    private readonly LeadService _leads;
    private readonly ICrmSeeder _seeder;
    private readonly CrmApiRateLimiter _limiter;
    private readonly IModuleGate? _gate;

    public LeadApiKeyService(CrmDbContext db, LeadService leads, ICrmSeeder seeder, CrmApiRateLimiter limiter, IModuleGate? gate = null)
    {
        _db = db;
        _leads = leads;
        _seeder = seeder;
        _limiter = limiter;
        _gate = gate;
    }

    internal static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim()))).ToLowerInvariant();

    // ── Key management ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<LeadApiKeyModel>> ListAsync(CrmCaller caller, CancellationToken ct = default) =>
        (await _db.LeadApiKeys.AsNoTracking().Where(k => k.OrganizationId == caller.OrganizationId).OrderBy(k => k.Name).ToListAsync(ct))
            .Select(Map).ToList();

    public async Task<LeadApiKeyCreated> CreateAsync(CrmCaller caller, LeadApiKeyUpsert req, CancellationToken ct = default)
    {
        var name = CrmText.Required(req.Name, 100, "Name");
        if (await _db.LeadApiKeys.AnyAsync(k => k.OrganizationId == caller.OrganizationId && k.Name == name, ct))
            throw new ConflictException($"An API key named {name} already exists.");
        var raw = KeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var key = new CrmLeadApiKey
        {
            OrganizationId = caller.OrganizationId, Name = name, KeyPrefix = raw[..12], KeyHash = Hash(raw),
            RequestsPerMinute = Limit(req.RequestsPerMinute) ?? DefaultPerMinute, IsActive = req.IsActive ?? true, CreatedBy = caller.UserId
        };
        _db.LeadApiKeys.Add(key);
        await _db.SaveChangesAsync(ct);
        return new LeadApiKeyCreated { Key = raw, ApiKey = Map(key) };
    }

    public async Task<LeadApiKeyModel> UpdateAsync(CrmCaller caller, Guid uuid, LeadApiKeyUpsert req, CancellationToken ct = default)
    {
        var key = await _db.LeadApiKeys.FirstOrDefaultAsync(k => k.OrganizationId == caller.OrganizationId && k.Uuid == uuid, ct)
            ?? throw new NotFoundException("API key not found.");
        if (req.Name is not null)
        {
            var name = CrmText.Required(req.Name, 100, "Name");
            if (name != key.Name && await _db.LeadApiKeys.AnyAsync(k => k.OrganizationId == caller.OrganizationId && k.Name == name && k.Id != key.Id, ct))
                throw new ConflictException($"An API key named {name} already exists.");
            key.Name = name;
        }
        if (Limit(req.RequestsPerMinute) is { } limit) key.RequestsPerMinute = limit;
        if (req.IsActive is { } active) key.IsActive = active;
        await _db.SaveChangesAsync(ct);
        return Map(key);
    }

    public async Task DeleteAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var key = await _db.LeadApiKeys.FirstOrDefaultAsync(k => k.OrganizationId == caller.OrganizationId && k.Uuid == uuid, ct)
            ?? throw new NotFoundException("API key not found.");
        _db.LeadApiKeys.Remove(key);
        await _db.SaveChangesAsync(ct);
    }

    private static int? Limit(int? perMinute) => perMinute switch
    {
        null => null,
        < 1 or > MaxPerMinute => throw new BadRequestException($"Requests per minute must be between 1 and {MaxPerMinute}."),
        var v => v
    };

    private static LeadApiKeyModel Map(CrmLeadApiKey k) => new()
    {
        Uuid = k.Uuid, Name = k.Name, KeyPrefix = k.KeyPrefix, RequestsPerMinute = k.RequestsPerMinute, IsActive = k.IsActive,
        LastUsedAt = k.LastUsedAt, CreatedAt = k.CreatedAt
    };

    // ── Public lead creation ─────────────────────────────────────────────────────

    public async Task<PublicLeadResult> CreateAsync(string? apiKey, PublicLeadRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new UnauthorizedException("An API key is required (X-Api-Key header).");
        var hash = Hash(apiKey);
        // Anonymous request: the key alone decides the organization; every query below is explicit about it.
        var key = await _db.LeadApiKeys.IgnoreQueryFilters().FirstOrDefaultAsync(k => k.KeyHash == hash, ct);
        if (key is null || !key.IsActive) throw new UnauthorizedException("The API key is not valid.");
        var org = key.OrganizationId;
        if (_gate is not null && !await _gate.IsEnabledAsync(org, ModuleCodes.Crm, ct))
            throw new ForbiddenException("The CRM is switched off for this organization.");
        if (!_limiter.TryAcquire(key.Id, key.RequestsPerMinute)) throw new CrmRateLimitException(key.RequestsPerMinute);

        await _seeder.EnsureAsync(org, ct);
        Guid? sourceUuid = null;
        if (!string.IsNullOrWhiteSpace(req.SourceCode))
        {
            var code = req.SourceCode.Trim().ToUpperInvariant();
            sourceUuid = await _db.LeadSources.IgnoreQueryFilters().Where(s => s.OrganizationId == org && s.Code == code && s.IsActive)
                    .Select(s => (Guid?)s.Uuid).FirstOrDefaultAsync(ct)
                ?? throw new BadRequestException($"Unknown source_code {code}.");
        }
        Guid? campaignUuid = null;
        if (!string.IsNullOrWhiteSpace(req.CampaignCode)
            && (_gate is null || await _gate.IsEnabledAsync(org, CrmFeatureCodes.CampaignTracking, ct))) // D-6: off = ignored
        {
            var code = req.CampaignCode.Trim().ToUpperInvariant();
            campaignUuid = await _db.Campaigns.IgnoreQueryFilters().Where(c => c.OrganizationId == org && c.Code == code && !c.IsDeleted)
                    .Select(c => (Guid?)c.Uuid).FirstOrDefaultAsync(ct)
                ?? throw new BadRequestException($"Unknown campaign_code {code}.");
        }

        var caller = new CrmCaller(org, 0, [PermissionCodes.CRM_CREATE_LEAD], $"API: {key.Name}");
        var phone = CrmText.Optional(req.Phone, 30, "phone");
        var lead = await _leads.CreateAsync(caller, new LeadUpsert
        {
            FirstName = req.FirstName, LastName = req.LastName, CompanyName = req.CompanyName, Email = req.Email,
            MobilePhone = phone, Topic = req.Topic, Description = req.Description, LeadSourceUuid = sourceUuid, CampaignUuid = campaignUuid,
            SourceMedium = req.SourceMedium, SourceReference = req.SourceReference,
            ExternalSourceSystem = req.ExternalSourceSystem, ExternalSourceId = req.ExternalSourceId
        }, ct);

        key.LastUsedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new PublicLeadResult { LeadId = lead.Uuid, LeadNumber = lead.LeadNumber };
    }
}

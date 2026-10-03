using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Integration.Core.Reference;

/// <summary>The admin side of the cached QuickBooks reference data (plan QBI-12).</summary>
public interface IReferenceDataService
{
    /// <summary>What was last fetched, for the mapping dropdowns. Empty lists when nothing was fetched yet.</summary>
    Task<ReferenceDataModel> GetAsync(CancellationToken ct = default);

    /// <summary>Fetches accounts, tax codes, terms, currencies, preferences and company info again.</summary>
    Task<ReferenceDataModel> RefreshAsync(int userId, CancellationToken ct = default);
}

/// <summary>Stores reference data. Used by the callback (right after connecting) and the daily job.</summary>
internal interface IReferenceDataStore
{
    /// <summary>One snapshot per <see cref="ReferenceKinds"/> value, and the company fields on the connection.</summary>
    Task StoreAsync(IntegrationConnection connection, RemoteReferenceData data, CancellationToken ct = default);

    /// <summary>
    /// Asks the provider and stores what it returns. The provider's answer is returned as-is so the caller
    /// decides how loud a failure is — the callback shrugs, the admin refresh reports it.
    /// </summary>
    /// <exception cref="ConnectionUnavailableException">The connection cannot be used.</exception>
    /// <exception cref="TokenRefreshFailedException">No usable token right now.</exception>
    Task<ProviderResult<RemoteReferenceData>> FetchAndStoreAsync(IntegrationConnection connection, CancellationToken ct = default);
}

/// <summary>
/// Keeps QuickBooks' chart of accounts, tax codes, terms, currencies, preferences and company info as
/// JSON snapshots, one row per kind. Everything downstream — the mapping screen, settings validation,
/// preflight, the object builder — reads the snapshot, never QuickBooks, so a screen full of dropdowns
/// is one local query and not seven calls to Intuit.
/// </summary>
internal sealed class ReferenceDataService : IReferenceDataService, IReferenceDataStore, IReferenceDataReader
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IntegrationDbContext            _db;
    private readonly IConnectionAccessor             _accessor;
    private readonly IAccountingProviderRegistry     _providers;
    private readonly IConnectionHealth               _health;
    private readonly IServiceProvider                _services;
    private readonly ILogger<ReferenceDataService>   _logger;

    public ReferenceDataService(
        IntegrationDbContext db, IConnectionAccessor accessor, IAccountingProviderRegistry providers, IConnectionHealth health,
        IServiceProvider services, ILogger<ReferenceDataService> logger)
    {
        _db        = db;
        _accessor  = accessor;
        _providers = providers;
        _health    = health;
        _services  = services;
        _logger    = logger;
    }

    // ── Admin ───────────────────────────────────────────────────────────────────────────────

    public async Task<ReferenceDataModel> GetAsync(CancellationToken ct = default)
    {
        var connection = await _accessor.GetCurrentAsync(ct);
        if (connection is null) return new ReferenceDataModel();

        var (data, fetchedAt) = await LoadAsync(connection.Id, ct);
        return data is null ? new ReferenceDataModel() : ToModel(data, fetchedAt);
    }

    public async Task<ReferenceDataModel> RefreshAsync(int userId, CancellationToken ct = default)
    {
        var connection = await _accessor.GetCurrentAsync(ct);
        if (connection is null || !connection.IsUsable())
            throw new ConflictException("Connect QuickBooks before loading its accounts, tax codes and terms.");

        ProviderResult<RemoteReferenceData> result;
        try
        {
            result = await FetchAndStoreAsync(connection, ct);
        }
        catch (ConnectionUnavailableException ex) { throw new ConflictException(ex.Message); }
        catch (TokenRefreshFailedException ex)    { throw new ConflictException(ex.Message); }

        if (!result.IsSuccess)
            throw new ConflictException(
                $"QuickBooks did not return its reference data ({result.Outcome}): {result.Message ?? "no reason given"}.");

        return await GetAsync(ct);
    }

    // ── Store ───────────────────────────────────────────────────────────────────────────────

    public async Task<ProviderResult<RemoteReferenceData>> FetchAndStoreAsync(
        IntegrationConnection connection, CancellationToken ct = default)
    {
        var result = await _providers.Get(connection.ProviderKey).GetReferenceDataAsync(connection.ToProviderContext(), ct);

        if (result.IsSuccess && result.Value is not null)
        {
            await StoreAsync(connection, result.Value, ct);
            // A currency now active, multicurrency switched on, an account or tax code back in the list: records
            // Blocked for want of them are re-validated now (admin refresh, the daily job, a reconnect alike).
            await BlockedRecordRevalidator.AfterChangeAsync(_services, connection, _logger, ct);
        }
        else if (result.Outcome == ProviderOutcomeKind.AuthRevoked)
            await _health.MarkUnavailableAsync(connection.Id, ConnectionStatus.Revoked,
                result.Message ?? "QuickBooks refused the connection's authorization.", ct);

        return result;
    }

    public async Task StoreAsync(IntegrationConnection connection, RemoteReferenceData data, CancellationToken ct = default)
    {
        var now      = DateTime.UtcNow;
        var existing = await _db.ReferenceSnapshots
            .Where(s => s.ConnectionId == connection.Id)
            .ToDictionaryAsync(s => s.Kind, ct);

        void Put(string kind, object? value)
        {
            var json = JsonSerializer.Serialize(value, Json);

            if (existing.TryGetValue(kind, out var snapshot))
            {
                snapshot.Json      = json;
                snapshot.FetchedAt = now;
                return;
            }

            _db.ReferenceSnapshots.Add(new ReferenceSnapshot
            {
                OrganizationId = connection.OrganizationId,
                ConnectionId   = connection.Id,
                Kind           = kind,
                Json           = json,
                FetchedAt      = now
            });
        }

        Put(ReferenceKinds.Accounts,    data.Accounts);
        Put(ReferenceKinds.TaxCodes,    data.TaxCodes);
        Put(ReferenceKinds.Terms,       data.Terms);
        Put(ReferenceKinds.Currencies,  data.Currencies);
        Put(ReferenceKinds.Preferences, data.Preferences);
        Put(ReferenceKinds.CompanyInfo, data.CompanyInfo);

        // The connection carries the headline facts so the status card and preflight need no JSON.
        // Absent values keep what was known: a partial answer must not blank the company's name.
        await ConnectionPersistence.SaveWithRetryAsync(_db, connection, c =>
        {
            if (!string.IsNullOrWhiteSpace(data.CompanyInfo?.CompanyName))
                c.CompanyName = Truncate(data.CompanyInfo!.CompanyName, 200);
            if (!string.IsNullOrWhiteSpace(data.CompanyInfo?.Country))
                c.Country = Truncate(data.CompanyInfo!.Country!, 10);
            if (data.Preferences is not null)
            {
                if (!string.IsNullOrWhiteSpace(data.Preferences.HomeCurrencyCode))
                    c.HomeCurrencyCode = Truncate(data.Preferences.HomeCurrencyCode!.ToUpperInvariant(), 10);
                c.MultiCurrencyEnabled = data.Preferences.MultiCurrencyEnabled;
            }
        }, ct);
    }

    // ── Read ────────────────────────────────────────────────────────────────────────────────

    public async Task<RemoteReferenceData?> GetCachedAsync(int connectionId, CancellationToken ct = default) =>
        (await LoadAsync(connectionId, ct)).Data;

    internal async Task<(RemoteReferenceData? Data, DateTime? FetchedAt)> LoadAsync(int connectionId, CancellationToken ct)
    {
        var snapshots = await _db.ReferenceSnapshots
            .AsNoTracking()
            .Where(s => s.ConnectionId == connectionId)
            .ToListAsync(ct);

        if (snapshots.Count == 0) return (null, null);

        var byKind = snapshots.ToDictionary(s => s.Kind);

        T? Read<T>(string kind) where T : class
        {
            if (!byKind.TryGetValue(kind, out var snapshot)) return null;
            try
            {
                return JsonSerializer.Deserialize<T>(snapshot.Json, Json);
            }
            catch (JsonException)
            {
                // A snapshot that no longer parses (the shape changed between releases) is treated as
                // missing; the next refresh replaces it.
                return null;
            }
        }

        var data = new RemoteReferenceData
        {
            Accounts    = Read<List<RemoteAccount>>(ReferenceKinds.Accounts)    ?? [],
            TaxCodes    = Read<List<RemoteTaxCode>>(ReferenceKinds.TaxCodes)    ?? [],
            Terms       = Read<List<RemoteTerm>>(ReferenceKinds.Terms)          ?? [],
            Currencies  = Read<List<RemoteCurrency>>(ReferenceKinds.Currencies) ?? [],
            Preferences = Read<RemotePreferences>(ReferenceKinds.Preferences),
            CompanyInfo = Read<RemoteCompanyInfo>(ReferenceKinds.CompanyInfo)
        };

        return (data, snapshots.Max(s => s.FetchedAt));
    }

    internal static ReferenceDataModel ToModel(RemoteReferenceData data, DateTime? fetchedAt) => new()
    {
        FetchedAt = fetchedAt,
        Accounts  = data.Accounts
            .OrderBy(a => a.AccountType).ThenBy(a => a.Name)
            .Select(a => new ReferenceItemModel
            {
                Id = a.Id, Name = a.Name, Type = a.AccountType, SubType = a.AccountSubType, Active = a.Active
            }).ToList(),
        TaxCodes = data.TaxCodes
            .OrderBy(t => t.Name)
            .Select(t => new ReferenceItemModel
            {
                Id = t.Id, Name = t.Name, Type = t.Taxable ? "Taxable" : "NonTaxable", Rate = t.RatePercent, Active = t.Active
            }).ToList(),
        Terms = data.Terms
            .OrderBy(t => t.Name)
            .Select(t => new ReferenceItemModel { Id = t.Id, Name = t.Name, Days = t.DueDays, Active = t.Active })
            .ToList(),
        Currencies = data.Currencies
            .OrderBy(c => c.Code)
            .Select(c => new ReferenceItemModel { Id = c.Code, Name = c.Name })
            .ToList()
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

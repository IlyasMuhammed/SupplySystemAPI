using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Integration.Gateway.JournalEntries;

/// <summary>What one connection resolves a journal line against: role / account → QuickBooks account (G-1), cost center → Class (G-5).</summary>
internal sealed class JournalEntryRefs
{
    private readonly ExternalAccountMap _accounts;
    private readonly IReadOnlyDictionary<string, CostCenterQboMapping> _costCenters;

    /// <summary>The stored QuickBooks reference snapshot; null when never fetched.</summary>
    public RemoteReferenceData? Reference { get; }

    public JournalEntryRefs(ExternalAccountMap accounts, IEnumerable<CostCenterQboMapping> costCenters, RemoteReferenceData? reference = null)
    {
        Reference = reference;
        _accounts = accounts;
        _costCenters = costCenters.GroupBy(c => c.CostCenterCode, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The QuickBooks account id; an explicit account code wins over the role, as at the GL. Null = unmapped.</summary>
    public string? Account(string? role, string? accountCode)
    {
        if (!string.IsNullOrWhiteSpace(accountCode))
            return _accounts.ByAccountCode.TryGetValue(accountCode.Trim(), out var entry) && !string.IsNullOrWhiteSpace(entry.ExternalAccountCode)
                ? entry.ExternalAccountCode.Trim() : null;
        return !string.IsNullOrWhiteSpace(role) && _accounts.ByDefaultKey.TryGetValue(role.Trim(), out var id) && !string.IsNullOrWhiteSpace(id) ? id.Trim() : null;
    }

    /// <summary>An active row with a Class, else null: it never posts unclassified (§11A.2).</summary>
    public CostCenterQboMapping? CostCenter(string? code) =>
        code is not null && _costCenters.TryGetValue(code.Trim(), out var row) && row.IsActive && !string.IsNullOrWhiteSpace(row.QboClassRef) ? row : null;
}

internal interface IJournalEntryRefSource
{
    Task<JournalEntryRefs> LoadAsync(int connectionId, CancellationToken ct = default);
}

internal sealed class JournalEntryRefSource : IJournalEntryRefSource
{
    private readonly IntegrationDbContext _db;
    private readonly IServiceProvider _services;

    public JournalEntryRefSource(IntegrationDbContext db, IServiceProvider services)
    {
        _db = db;
        _services = services;
    }

    public async Task<JournalEntryRefs> LoadAsync(int connectionId, CancellationToken ct = default)
    {
        var accounts = await PostingReadiness.LedgerAsync(_services, ct);
        var rows = await _db.CostCenterQboMappings.AsNoTracking().Where(m => m.ConnectionId == connectionId).ToListAsync(ct);
        var reference = _services.GetService<IReferenceDataReader>() is { } reader ? await reader.GetCachedAsync(connectionId, ct) : null;
        return new JournalEntryRefs(accounts, rows, reference);
    }
}

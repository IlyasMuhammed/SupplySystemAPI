using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Integration.Core.Settings;

/// <summary>A49 G-5 (API-CONTRACT §10.4): the cost center → Class / Location map of the organization's connection.</summary>
public interface ICostCenterQboMappingService
{
    /// <summary>Syncs from HRM first, so the page always shows every cost center. Empty without a connection.</summary>
    Task<IReadOnlyList<CostCenterMappingModel>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CostCenterMappingModel>> GetUnmappedAsync(CancellationToken ct = default);
    Task<CostCenterMappingModel> UpdateAsync(string code, UpdateCostCenterMappingRequest request, int userId, CancellationToken ct = default);
    Task<CostCenterSyncResult> SyncAsync(int userId, CancellationToken ct = default);
    /// <summary>The QuickBooks refs of an active, mapped cost center; null otherwise.</summary>
    Task<CostCenterQboRefs?> ResolveAsync(string code, CancellationToken ct = default);
    /// <summary>QuickBooks' Classes and Locations from the stored reference snapshot, for the mapping pickers. Empty lists until it is fetched.</summary>
    Task<QboClassReferenceModel> ReferenceAsync(CancellationToken ct = default);
    /// <summary>For a message: the stored name, else HRM's, else the code.</summary>
    Task<string> NameOfAsync(string code, CancellationToken ct = default);
}

internal sealed class CostCenterQboMappingService : ICostCenterQboMappingService
{
    private readonly IntegrationDbContext _db;
    private readonly IConnectionAccessor _connections;
    private readonly IServiceProvider _services;

    public CostCenterQboMappingService(IntegrationDbContext db, IConnectionAccessor connections, IServiceProvider services)
    {
        _db = db;
        _connections = connections;
        _services = services;
    }

    private static CostCenterMappingModel ToModel(CostCenterQboMapping m) => new()
    {
        Uuid = m.Uuid, CostCenterCode = m.CostCenterCode, CostCenterName = m.CostCenterName, QboClassRef = m.QboClassRef, QboClassName = m.QboClassName,
        QboDepartmentRef = m.QboDepartmentRef, QboDepartmentName = m.QboDepartmentName, IsActive = m.IsActive,
        IsMapped = !string.IsNullOrWhiteSpace(m.QboClassRef)
    };

    public async Task<IReadOnlyList<CostCenterMappingModel>> ListAsync(CancellationToken ct = default)
    {
        var connection = await _connections.GetCurrentAsync(ct);
        if (connection is null) return [];
        await SyncRowsAsync(connection, null, ct);
        var rows = await _db.CostCenterQboMappings.AsNoTracking().Where(m => m.ConnectionId == connection.Id)
            .OrderBy(m => m.CostCenterCode).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<CostCenterMappingModel>> GetUnmappedAsync(CancellationToken ct = default) =>
        (await ListAsync(ct)).Where(m => m.IsActive && !m.IsMapped).ToList();

    public async Task<CostCenterMappingModel> UpdateAsync(string code, UpdateCostCenterMappingRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = await RequireConnectionAsync(ct);
        await SyncRowsAsync(connection, userId, ct);

        var key = code?.Trim().ToUpperInvariant() ?? string.Empty;
        var row = await _db.CostCenterQboMappings.FirstOrDefaultAsync(m => m.ConnectionId == connection.Id && m.CostCenterCode == key, ct)
            ?? throw new NotFoundException("Cost center", code ?? string.Empty);

        var before = new { row.QboClassRef, row.QboDepartmentRef };
        row.QboClassRef       = Clean(request.QboClassRef, 50, "QuickBooks Class");
        row.QboClassName      = Clean(request.QboClassName, 100, "QuickBooks Class name");
        row.QboDepartmentRef  = Clean(request.QboDepartmentRef, 50, "QuickBooks Location");
        row.QboDepartmentName = Clean(request.QboDepartmentName, 100, "QuickBooks Location name");
        row.ModifiedAt = DateTime.UtcNow;
        row.ModifiedBy = userId;
        IntegrationAudit.Add(_db, connection.Id, AuditAreas.SyncControl, $"Cost center {row.CostCenterCode} mapping", before, new { row.QboClassRef, row.QboDepartmentRef }, userId);
        await _db.SaveChangesAsync(ct);

        // Entries blocked on this map go now rather than on the next sweep.
        await Sync.BlockedRecordRevalidator.AfterChangeAsync(_services, connection, _services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CostCenterQboMappingService>>(), ct);
        return ToModel(row);
    }

    public async Task<CostCenterSyncResult> SyncAsync(int userId, CancellationToken ct = default) =>
        await SyncRowsAsync(await RequireConnectionAsync(ct), userId, ct);

    public async Task<CostCenterQboRefs?> ResolveAsync(string code, CancellationToken ct = default)
    {
        var connection = await _connections.GetCurrentAsync(ct);
        if (connection is null) return null;
        var key = code?.Trim().ToUpperInvariant() ?? string.Empty;
        var row = await _db.CostCenterQboMappings.AsNoTracking().FirstOrDefaultAsync(m => m.ConnectionId == connection.Id && m.CostCenterCode == key, ct);
        return row is { IsActive: true, QboClassRef: { Length: > 0 } }
            ? new CostCenterQboRefs(row.QboClassRef, row.QboClassName, row.QboDepartmentRef, row.QboDepartmentName)
            : null;
    }

    public async Task<QboClassReferenceModel> ReferenceAsync(CancellationToken ct = default)
    {
        var model = new QboClassReferenceModel();
        var connection = await _connections.GetCurrentAsync(ct);
        if (connection is null) return model;
        model.Connected = true;
        if (_services.GetService<IReferenceDataReader>() is not { } reader) return model;
        var data = await reader.GetCachedAsync(connection.Id, ct);
        if (data is null) return model;
        model.Loaded = true;
        model.Classes = data.Classes.Where(c => c.Active).Select(c => new QboRefModel { Id = c.Id, Name = c.FullyQualifiedName ?? c.Name }).ToList();
        model.Departments = data.Departments.Where(d => d.Active).Select(d => new QboRefModel { Id = d.Id, Name = d.FullyQualifiedName ?? d.Name }).ToList();
        model.TracksClasses = data.Preferences?.ClassTrackingEnabled;
        return model;
    }

    public async Task<string> NameOfAsync(string code, CancellationToken ct = default)
    {
        var connection = await _connections.GetCurrentAsync(ct);
        var stored = connection is null ? null : await _db.CostCenterQboMappings.AsNoTracking()
            .Where(m => m.ConnectionId == connection.Id && m.CostCenterCode == code).Select(m => m.CostCenterName).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(stored)) return stored;
        var org = connection?.OrganizationId ?? Guid.Empty;
        if (_services.GetService<IHrmCostCenterSource>() is { } source && org != Guid.Empty)
            return (await source.ListAsync(org, ct)).FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase))?.Name ?? code;
        return code;
    }

    /// <summary>G-5 / BR-G5-001: insert what is new, keep names current, deactivate what HRM no longer has active. The accountant's Class is never touched.</summary>
    private async Task<CostCenterSyncResult> SyncRowsAsync(IntegrationConnection connection, int? userId, CancellationToken ct)
    {
        var result = new CostCenterSyncResult();
        if (_services.GetService<IHrmCostCenterSource>() is not { } source) return result;

        var centers = await source.ListAsync(connection.OrganizationId, ct);
        var rows = await _db.CostCenterQboMappings.Where(m => m.ConnectionId == connection.Id).ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var center in centers)
        {
            var row = rows.FirstOrDefault(r => string.Equals(r.CostCenterCode, center.Code, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                _db.CostCenterQboMappings.Add(new CostCenterQboMapping
                {
                    OrganizationId = connection.OrganizationId, ConnectionId = connection.Id, CostCenterCode = center.Code, CostCenterName = center.Name,
                    IsActive = center.IsActive, CreatedAt = now, CreatedBy = userId
                });
                result.Added++;
            }
            else if (row.CostCenterName != center.Name || row.IsActive != center.IsActive)
            {
                row.CostCenterName = center.Name;
                row.IsActive = center.IsActive;
                row.ModifiedAt = now;
                row.ModifiedBy = userId;
                result.Updated++;
            }
        }
        foreach (var row in rows.Where(r => r.IsActive && centers.All(c => !string.Equals(c.Code, r.CostCenterCode, StringComparison.OrdinalIgnoreCase))))
        {
            row.IsActive = false;
            row.ModifiedAt = now;
            row.ModifiedBy = userId;
            result.Deactivated++;
        }
        if (_db.ChangeTracker.HasChanges())
        {
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Two page loads populating at once: the unique index stopped the second, and the first's rows are what we want.
                _db.ChangeTracker.Clear();
            }
        }
        return result;
    }

    private async Task<IntegrationConnection> RequireConnectionAsync(CancellationToken ct) =>
        await _connections.GetCurrentAsync(ct) ?? throw new ConflictException("Connect QuickBooks first: the map belongs to a connected company.");

    private static string? Clean(string? value, int max, string label)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (text is { Length: var n } && n > max) throw new BadRequestException($"{label} cannot be longer than {max} characters.");
        return text;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Integration.Core.Settings;

/// <summary>The Sync control tab (A44 D-14): one switch per document type, with hold / release.</summary>
public interface IQuickBooksSyncControlService
{
    Task<SyncControlModel> GetAsync(CancellationToken ct = default);
    Task<SyncControlUpdateResult> UpdateAsync(string documentType, UpdateSyncControlRequest request, int userId, CancellationToken ct = default);
    Task<SyncControlReleaseResult> ReleaseAsync(string documentType, int userId, CancellationToken ct = default);
}

/// <summary>A 409 that carries data for the screen (the held count, the open documents) in ApiResponse.result.</summary>
internal sealed class IntegrationConflictException : ConflictException
{
    public object Detail { get; }
    public IntegrationConflictException(string message, object detail) : base(message) => Detail = detail;
}

/// <summary>
/// Defaults, labels and the bridge to the six <c>AutoPush*</c> switches (D-14, R-1). Those switches stay in step with the
/// sync-control rows in both directions until every reader has moved over: a sync-control change writes the matching
/// switch, the old settings save writes the matching rows, and a connection with no row for a type falls back to its
/// switch — the migration gives every existing connection a row per type, so that fallback is only ever reached by a
/// settings row that was written directly (tests, hand-made data).
/// </summary>
internal static class SyncControlDefaults
{
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SyncControlDocumentTypes.Customer]     = "Customers",
        [SyncControlDocumentTypes.Vendor]       = "Vendors",
        [SyncControlDocumentTypes.Item]         = "Items",
        [SyncControlDocumentTypes.SalesInvoice] = "Sales invoices",
        [SyncControlDocumentTypes.CreditNote]   = "Credit notes",
        [SyncControlDocumentTypes.Payment]      = "Customer payments",
        [SyncControlDocumentTypes.PosSale]      = "POS sales",
        [SyncControlDocumentTypes.PosReturn]    = "POS returns",
        [SyncControlDocumentTypes.PurchaseBill] = "Purchase bills",
        [SyncControlDocumentTypes.BillPayment]  = "Bill payments",
        [SyncControlDocumentTypes.VendorCredit] = "Vendor credits"
    };

    /// <summary>
    /// D-60: what a new connection starts with (FSD §22.3, with D-14's exception that the purchase side starts off without
    /// hold). POS sales and returns start off and held — what "Sync POS sales" always did.
    /// </summary>
    public static SyncControlDecision NewConnection(string documentType) => documentType switch
    {
        SyncControlDocumentTypes.PosSale or SyncControlDocumentTypes.PosReturn => new(false, true),
        SyncControlDocumentTypes.PurchaseBill or SyncControlDocumentTypes.BillPayment
            or SyncControlDocumentTypes.VendorCredit => new(false, false),
        _ => SyncControlDecision.Enabled
    };

    /// <summary>The canonical name of a document type (any case), or null when it is not one.</summary>
    public static string? Normalize(string? documentType) =>
        string.IsNullOrWhiteSpace(documentType)
            ? null
            : SyncControlDocumentTypes.All.FirstOrDefault(t => string.Equals(t, documentType.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Require(string? documentType) =>
        Normalize(documentType) ?? throw new BadRequestException(
            $"Unknown document type '{documentType}'. Use one of: {string.Join(", ", SyncControlDocumentTypes.All)}.");

    /// <summary>
    /// D-67: a type with no row follows the old switches exactly as the gateway's legacy reader does (REV-10) — the
    /// A44 types follow their family: Payment / CreditNote → sales invoices, BillPayment / VendorCredit → bills; POS sales
    /// go with both POS and sales invoices on, are held while only POS is off, skipped while sales invoices are off.
    /// Null when there is no settings row (then BR-29: enabled).
    /// </summary>
    public static SyncControlDecision? Legacy(IntegrationSettings? s, string documentType) =>
        s is null ? null : LegacySyncControlReader.Decide(s, documentType);

    /// <summary>Row, else the old switch, else enabled (BR-29).</summary>
    public static SyncControlDecision Effective(SyncControlConfig? row, IntegrationSettings? settings, string documentType) =>
        row is not null ? new SyncControlDecision(row.IsEnabled, row.HoldWhenDisabled)
                        : Legacy(settings, documentType) ?? SyncControlDecision.Enabled;

    /// <summary>Writes a type's state onto its old switch (no-op for the types that never had one).</summary>
    public static void Mirror(IntegrationSettings s, string documentType, bool isEnabled)
    {
        switch (documentType)
        {
            case SyncControlDocumentTypes.Customer:     s.AutoPushCustomers     = isEnabled; break;
            case SyncControlDocumentTypes.Vendor:       s.AutoPushVendors       = isEnabled; break;
            case SyncControlDocumentTypes.Item:         s.AutoPushItems         = isEnabled; break;
            case SyncControlDocumentTypes.SalesInvoice: s.AutoPushSalesInvoices = isEnabled; break;
            case SyncControlDocumentTypes.PurchaseBill: s.AutoPushBills         = isEnabled; break;
            case SyncControlDocumentTypes.PosSale:      s.AutoPushPosSales      = isEnabled; break;
        }
    }

    /// <summary>
    /// A new connection's rows (D-60), added to the context with its first settings row; the old switches are set to
    /// match. Does not save. No-op when the connection already has rows.
    /// </summary>
    public static async Task SeedNewConnectionAsync(
        IntegrationDbContext db, IntegrationConnection connection, IntegrationSettings settings, CancellationToken ct)
    {
        if (connection.Id != 0 && await db.SyncControlConfigs.AnyAsync(c => c.ConnectionId == connection.Id, ct)) return;

        var now = DateTime.UtcNow;
        foreach (var type in SyncControlDocumentTypes.All)
        {
            var decision = NewConnection(type);
            db.SyncControlConfigs.Add(new SyncControlConfig
            {
                OrganizationId   = connection.OrganizationId,
                ConnectionId     = connection.Id,
                DocumentType     = type,
                IsEnabled        = decision.IsEnabled,
                HoldWhenDisabled = decision.HoldWhenDisabled,
                DisplayLabel     = Labels[type],
                ModifiedAt       = now
            });
            Mirror(settings, type, decision.IsEnabled);
        }
    }
}

/// <summary>
/// Hold / release through the gateway side's <see cref="IHeldRecordReleaser"/>, resolved optional-safe (hosts and tests
/// without the gateway). Without one, POS sales fall back to the original <see cref="PosSales"/> mechanics and other
/// types hold nothing.
/// </summary>
internal static class HeldRecords
{
    public static async Task<int> CountAsync(IServiceProvider services, IntegrationDbContext db, int connectionId, string type, CancellationToken ct)
    {
        if (services.GetService<IHeldRecordReleaser>() is { } releaser)
            return await releaser.CountHeldAsync(connectionId, type, ct);
        return type == SyncControlDocumentTypes.PosSale ? await PosSales.CountHeldAsync(db, connectionId, ct) : 0;
    }

    /// <summary>Never throws: a failure is logged and the hourly reconciliation releases whatever is still held.</summary>
    public static async Task<int> ReleaseAsync(IServiceProvider services, int connectionId, string type, ILogger logger, CancellationToken ct)
    {
        try
        {
            if (services.GetService<IHeldRecordReleaser>() is { } releaser)
                return await releaser.ReleaseAsync(connectionId, type, ct);
            return type == SyncControlDocumentTypes.PosSale ? await PosSales.ReleaseAsync(services, connectionId, logger, ct) : 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "QuickBooks connection {ConnectionId}: releasing held {Type} records failed; the reconciliation will retry.",
                connectionId, type);
            return 0;
        }
    }

    public static async Task<int> HoldQueuedAsync(IServiceProvider services, IntegrationDbContext db, int connectionId, string type, CancellationToken ct)
    {
        if (services.GetService<IHeldRecordReleaser>() is { } releaser)
            return await releaser.HoldQueuedAsync(connectionId, type, ct);
        if (type == SyncControlDocumentTypes.PosSale && services.GetService<ISyncOutbox>() is { } outbox)
            return await PosSales.HoldQueuedAsync(db, outbox, connectionId, DateTime.UtcNow, ct);
        return 0;
    }
}

/// <summary>
/// The reader the gateway asks before it queues anything (<see cref="ISyncControlReader"/>), and the Sync control tab.
/// Hold and release go through <see cref="IHeldRecordReleaser"/> (the gateway side owns what "held" means); without one
/// registered, POS sales fall back to the original <see cref="PosSales"/> mechanics and other types hold nothing.
/// </summary>
internal sealed class SyncControlService : IQuickBooksSyncControlService, ISyncControlReader
{
    private readonly IntegrationDbContext        _db;
    private readonly IConnectionAccessor         _accessor;
    private readonly IServiceProvider            _services;
    private readonly ILogger<SyncControlService> _logger;

    public SyncControlService(
        IntegrationDbContext db, IConnectionAccessor accessor, IServiceProvider services, ILogger<SyncControlService> logger)
    {
        _db       = db;
        _accessor = accessor;
        _services = services;
        _logger   = logger;
    }

    // ── Reader ──────────────────────────────────────────────────────────────────────────────

    public async Task<SyncControlDecision> CheckAsync(int connectionId, string documentType, CancellationToken ct = default)
    {
        var type = SyncControlDefaults.Normalize(documentType);
        if (type is null) return SyncControlDecision.Enabled;

        var row = await _db.SyncControlConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConnectionId == connectionId && c.DocumentType == type, ct);
        if (row is not null) return new SyncControlDecision(row.IsEnabled, row.HoldWhenDisabled);

        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.ConnectionId == connectionId, ct);
        return SyncControlDefaults.Effective(null, settings, type);
    }

    public async Task<IReadOnlyDictionary<string, SyncControlDecision>> GetAllAsync(int connectionId, CancellationToken ct = default)
    {
        var rows     = await RowsAsync(connectionId, tracking: false, ct);
        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.ConnectionId == connectionId, ct);

        return SyncControlDocumentTypes.All.ToDictionary(
            t => t, t => SyncControlDefaults.Effective(rows.GetValueOrDefault(t), settings, t), StringComparer.Ordinal);
    }

    // ── Admin ───────────────────────────────────────────────────────────────────────────────

    public async Task<SyncControlModel> GetAsync(CancellationToken ct = default)
    {
        var connection = await _accessor.GetCurrentAsync(ct);
        var model      = new SyncControlModel();

        if (connection is null)
        {
            model.Rows = SyncControlDocumentTypes.All.Select(t => ToModel(t, null, SyncControlDefaults.NewConnection(t), 0)).ToList();
            return model;
        }

        var rows     = await RowsAsync(connection.Id, tracking: false, ct);
        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.ConnectionId == connection.Id, ct);

        foreach (var type in SyncControlDocumentTypes.All)
        {
            var row = rows.GetValueOrDefault(type);
            model.Rows.Add(ToModel(type, row, SyncControlDefaults.Effective(row, settings, type), await CountHeldAsync(connection.Id, type, ct)));
        }

        model.TotalHeld = model.Rows.Sum(r => r.HeldCount);
        return model;
    }

    public async Task<SyncControlUpdateResult> UpdateAsync(
        string documentType, UpdateSyncControlRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var type       = SyncControlDefaults.Require(documentType);
        var connection = await RequireConnectionAsync(ct);
        var settings   = await _accessor.GetOrCreateSettingsAsync(connection, ct);
        var rows       = await RowsAsync(connection.Id, tracking: true, ct);
        var row        = rows.GetValueOrDefault(type);
        var before     = SyncControlDefaults.Effective(row, settings, type);

        // D-63: enabling sends what was held — say how much first, unless the admin already confirmed.
        var held = !before.IsEnabled && request.IsEnabled ? await CountHeldAsync(connection.Id, type, ct) : 0;
        if (held > 0 && !request.ConfirmRelease)
            throw new IntegrationConflictException(
                $"{held} held {SyncControlDefaults.Labels[type].ToLowerInvariant()} record(s) will be sent to QuickBooks when this is "
              + "turned on. Confirm to send them.",
                new ReleaseConfirmationModel { DocumentType = type, HeldCount = held });

        var now = DateTime.UtcNow;
        if (row is null)
        {
            row = new SyncControlConfig
            {
                OrganizationId = connection.OrganizationId,
                ConnectionId   = connection.Id,
                DocumentType   = type,
                DisplayLabel   = SyncControlDefaults.Labels[type]
            };
            _db.SyncControlConfigs.Add(row);
        }

        row.IsEnabled        = request.IsEnabled;
        row.HoldWhenDisabled = request.HoldWhenDisabled;
        row.ModifiedBy       = userId;
        row.ModifiedAt       = now;
        SyncControlDefaults.Mirror(settings, type, request.IsEnabled);

        IntegrationAudit.Add(_db, connection.Id, AuditAreas.SyncControl, $"Set {type}",
            new { DocumentType = type, before.IsEnabled, before.HoldWhenDisabled },
            new { DocumentType = type, row.IsEnabled, row.HoldWhenDisabled }, userId);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two first-time saves of the same type at once: the unique index stops the second.
            throw new ConflictException($"{SyncControlDefaults.Labels[type]} were changed by someone else just now. Reload and try again.");
        }

        var result = new SyncControlUpdateResult();

        // A type turned on or off can change what a Blocked record waits for (e.g. BANK_ACCOUNT_MISSING once bill payments are off).
        await BlockedRecordRevalidator.AfterChangeAsync(_services, connection, _logger, ct);

        // Disabled with hold: what is queued but not sent yet waits too (voids still go). Enabled: everything held goes.
        if (!row.IsEnabled && row.HoldWhenDisabled && before.IsEnabled)
            result.HeldNow = await HoldQueuedAsync(connection.Id, type, ct);
        if (row.IsEnabled)
            result.Released = await ReleaseHeldAsync(connection.Id, type, ct);

        result.Row = ToModel(type, row, new SyncControlDecision(row.IsEnabled, row.HoldWhenDisabled), await CountHeldAsync(connection.Id, type, ct));
        return result;
    }

    public async Task<SyncControlReleaseResult> ReleaseAsync(string documentType, int userId, CancellationToken ct = default)
    {
        var type       = SyncControlDefaults.Require(documentType);
        var connection = await RequireConnectionAsync(ct);

        if (!(await CheckAsync(connection.Id, type, ct)).IsEnabled)
            throw new ConflictException(
                $"{SyncControlDefaults.Labels[type]} are turned off, so held records cannot be sent. Turn them on first (that sends them).");

        var released = await ReleaseHeldAsync(connection.Id, type, ct);
        if (released > 0)
        {
            IntegrationAudit.Add(_db, connection.Id, AuditAreas.SyncControl, $"Released {type}", null, new { DocumentType = type, Released = released }, userId);
            await _db.SaveChangesAsync(ct);
        }

        return new SyncControlReleaseResult
        {
            DocumentType = type, Released = released, StillHeld = await CountHeldAsync(connection.Id, type, ct)
        };
    }

    // ── Hold / release (through the gateway side) ───────────────────────────────────────────

    private Task<int> CountHeldAsync(int connectionId, string type, CancellationToken ct) =>
        HeldRecords.CountAsync(_services, _db, connectionId, type, ct);

    private Task<int> ReleaseHeldAsync(int connectionId, string type, CancellationToken ct) =>
        HeldRecords.ReleaseAsync(_services, connectionId, type, _logger, ct);

    private Task<int> HoldQueuedAsync(int connectionId, string type, CancellationToken ct) =>
        HeldRecords.HoldQueuedAsync(_services, _db, connectionId, type, ct);

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, SyncControlConfig>> RowsAsync(int connectionId, bool tracking, CancellationToken ct)
    {
        var query = _db.SyncControlConfigs.Where(c => c.ConnectionId == connectionId);
        if (!tracking) query = query.AsNoTracking();
        return (await query.ToListAsync(ct))
            .Where(c => SyncControlDefaults.Normalize(c.DocumentType) is not null)
            .GroupBy(c => SyncControlDefaults.Normalize(c.DocumentType)!)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    private async Task<IntegrationConnection> RequireConnectionAsync(CancellationToken ct) =>
        await _accessor.GetCurrentAsync(ct)
        ?? throw new ConflictException("Connect QuickBooks first — sync control belongs to a connected company.");

    private static SyncControlRowModel ToModel(string type, SyncControlConfig? row, SyncControlDecision decision, int held) => new()
    {
        DocumentType     = type,
        DisplayLabel     = row?.DisplayLabel is { Length: > 0 } label ? label : SyncControlDefaults.Labels[type],
        IsEnabled        = decision.IsEnabled,
        HoldWhenDisabled = decision.HoldWhenDisabled,
        Action           = decision.Action.ToString(),
        HeldCount        = held,
        IsDefault        = row is null,
        ModifiedBy       = row?.ModifiedBy,
        ModifiedAt       = row?.ModifiedAt
    };
}

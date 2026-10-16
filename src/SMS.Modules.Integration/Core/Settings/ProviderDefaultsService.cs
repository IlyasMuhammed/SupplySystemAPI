using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Settings;

/// <summary>The Defaults tab (A44 D-15): connection-wide QuickBooks references, and per-document-type overrides.</summary>
public interface IQuickBooksProviderDefaultsService
{
    Task<ProviderDefaultsModel> GetDefaultsAsync(CancellationToken ct = default);
    Task<ProviderDefaultsModel> SaveDefaultsAsync(SaveProviderDefaultsRequest request, int userId, CancellationToken ct = default);
    Task<List<DocumentOverrideModel>> GetOverridesAsync(CancellationToken ct = default);
    Task<List<DocumentOverrideModel>> SaveOverridesAsync(SaveDocumentOverridesRequest request, int userId, CancellationToken ct = default);
}

/// <summary>One provider-default key, as the screen offers it.</summary>
internal sealed record ProviderDefaultDefinition(
    string Key, string BaseKey, string? Method, string Label, string ReferenceKind, IReadOnlyList<string> AccountTypes);

/// <summary>
/// The keys of <see cref="ProviderDefaultKeys"/> (A44-0190: seeded as definitions, never with values) and what each is
/// chosen from. Account keys name the QuickBooks account types QuickBooks accepts there.
/// </summary>
internal static class ProviderDefaultDefinitions
{
    public static readonly IReadOnlyList<string> Methods = ["CASH", "CHEQUE", "BANK_TRANSFER", "CARD", "ONLINE", "MOBILE"];

    public const string Class = "Class", Department = "Department", Term = "Term", PaymentMethod = "PaymentMethod",
                        Account = "Account", Customer = "Customer";

    private static readonly string[] DepositTypes = ["Bank", "Other Current Asset"];
    private static readonly string[] BankTypes    = ["Bank", "Credit Card"];
    private static readonly string[] RefundTypes  = ["Bank", "Other Current Asset", "Credit Card"];

    private static readonly Dictionary<string, string> MethodNames = new(StringComparer.Ordinal)
    {
        ["CASH"] = "cash", ["CHEQUE"] = "cheque", ["BANK_TRANSFER"] = "bank transfer",
        ["CARD"] = "card", ["ONLINE"] = "online", ["MOBILE"] = "mobile"
    };

    public static readonly IReadOnlyList<ProviderDefaultDefinition> All = Build();

    private static List<ProviderDefaultDefinition> Build()
    {
        var all = new List<ProviderDefaultDefinition>
        {
            new(ProviderDefaultKeys.ClassRef,      ProviderDefaultKeys.ClassRef,      null, "Class",                Class,      []),
            new(ProviderDefaultKeys.DepartmentRef, ProviderDefaultKeys.DepartmentRef, null, "Location (department)", Department, []),
            new(ProviderDefaultKeys.SalesTermRef,  ProviderDefaultKeys.SalesTermRef,  null, "Sales terms",          Term,       []),
            new(ProviderDefaultKeys.RefundFromAccountRef, ProviderDefaultKeys.RefundFromAccountRef, null, "Refunds paid from", Account, RefundTypes),
            new(ProviderDefaultKeys.PosDefaultCustomerRef, ProviderDefaultKeys.PosDefaultCustomerRef, null, "Walk-in POS customer", Customer, [])
        };

        foreach (var method in Methods)
        {
            var name = MethodNames[method];
            all.Add(new(ProviderDefaultKeys.ForMethod(ProviderDefaultKeys.PaymentMethodRef, method), ProviderDefaultKeys.PaymentMethodRef,
                method, $"Payment method for {name}", PaymentMethod, []));
            all.Add(new(ProviderDefaultKeys.ForMethod(ProviderDefaultKeys.DepositToAccountRef, method), ProviderDefaultKeys.DepositToAccountRef,
                method, $"Customer {name} receipts deposited to", Account, DepositTypes));
            all.Add(new(ProviderDefaultKeys.ForMethod(ProviderDefaultKeys.BankAccountRef, method), ProviderDefaultKeys.BankAccountRef,
                method, $"Bill payments by {name} paid from", Account, BankTypes));
            all.Add(new(ProviderDefaultKeys.ForMethod(ProviderDefaultKeys.RefundFromAccountRef, method), ProviderDefaultKeys.RefundFromAccountRef,
                method, $"{char.ToUpperInvariant(name[0])}{name[1..]} refunds paid from", Account, RefundTypes));
        }

        return all;
    }

    /// <summary>The definition of a key (base key any case, method upper-cased), or null.</summary>
    public static ProviderDefaultDefinition? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var trimmed = key.Trim();
        var dot     = trimmed.IndexOf('.');
        var canonical = dot < 0 ? trimmed : $"{trimmed[..dot]}.{trimmed[(dot + 1)..].ToUpperInvariant()}";
        return All.FirstOrDefault(d => string.Equals(d.Key, canonical, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Document types an override can be set for: the documents, not customers / vendors / items.</summary>
    public static readonly IReadOnlyList<string> OverrideDocumentTypes = SyncControlDocumentTypes.All
        .Where(t => t is not (SyncControlDocumentTypes.Customer or SyncControlDocumentTypes.Vendor or SyncControlDocumentTypes.Item))
        .ToList();
}

/// <summary>
/// Stores and validates the provider defaults and overrides, and is the <see cref="IProviderDefaultsReader"/> the
/// QuickBooks object builder reads (D-15, BR-12: an override for the document type wins over the default). Every value is
/// checked against the reference snapshot (or, for the walk-in customer, against the customers the gateway knows in
/// QuickBooks) before it is saved, like every other QuickBooks id in the settings.
/// <para>
/// D-65: the reader gives <c>ClassRef</c> / <c>DepartmentRef</c> only while the company's preferences say it tracks
/// classes / locations (not known = left out; QuickBooks refuses them otherwise), so the builder sends whatever it is given.
/// </para>
/// </summary>
internal sealed class ProviderDefaultsService : IQuickBooksProviderDefaultsService, IProviderDefaultsReader
{
    private readonly IntegrationDbContext _db;
    private readonly IConnectionAccessor  _accessor;
    private readonly IReferenceDataReader _reference;
    private readonly IServiceProvider     _services;
    private readonly ILogger<ProviderDefaultsService> _logger;

    public ProviderDefaultsService(
        IntegrationDbContext db, IConnectionAccessor accessor, IReferenceDataReader reference, IServiceProvider services,
        ILogger<ProviderDefaultsService> logger)
    {
        _db        = db;
        _accessor  = accessor;
        _reference = reference;
        _services  = services;
        _logger    = logger;
    }

    /// <summary>A default can be exactly what a Blocked record waited for (BANK_ACCOUNT_MISSING and the like).</summary>
    private Task RevalidateBlockedAsync(IntegrationConnection connection, CancellationToken ct) =>
        BlockedRecordRevalidator.AfterChangeAsync(_services, connection, _logger, ct);

    // ── Reader ──────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyDictionary<string, string>> GetAsync(int connectionId, string documentType, CancellationToken ct = default)
    {
        var values = (await _db.ProviderDefaults.AsNoTracking().Where(d => d.ConnectionId == connectionId).ToListAsync(ct))
            .Where(d => !string.IsNullOrWhiteSpace(d.SettingValue))
            .GroupBy(d => ProviderDefaultDefinitions.Find(d.SettingKey)?.Key ?? d.SettingKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().SettingValue.Trim(), StringComparer.OrdinalIgnoreCase);

        var type = SyncControlDefaults.Normalize(documentType);
        if (type is not null)
        {
            var overrides = await _db.DocumentOverrides.AsNoTracking()
                .Where(o => o.ConnectionId == connectionId && o.DocumentType == type)
                .ToListAsync(ct);
            foreach (var o in overrides.Where(o => !string.IsNullOrWhiteSpace(o.SettingValue)))
                values[ProviderDefaultDefinitions.Find(o.SettingKey)?.Key ?? o.SettingKey] = o.SettingValue.Trim();
        }

        if (values.ContainsKey(ProviderDefaultKeys.ClassRef) || values.ContainsKey(ProviderDefaultKeys.DepartmentRef))
        {
            // Only when the preferences say the company tracks them (not known = not sent: QuickBooks refuses them otherwise).
            var preferences = (await _reference.GetCachedAsync(connectionId, ct))?.Preferences;
            if (preferences?.ClassTrackingEnabled != true) values.Remove(ProviderDefaultKeys.ClassRef);
            if (preferences?.TrackDepartments != true)     values.Remove(ProviderDefaultKeys.DepartmentRef);
        }

        return values;
    }

    // ── Defaults ────────────────────────────────────────────────────────────────────────────

    public async Task<ProviderDefaultsModel> GetDefaultsAsync(CancellationToken ct = default)
    {
        var connection = await _accessor.GetCurrentAsync(ct);
        var data       = connection is null ? null : await _reference.GetCachedAsync(connection.Id, ct);
        var stored     = connection is null
            ? new Dictionary<string, ProviderDefault>(StringComparer.OrdinalIgnoreCase)
            : (await _db.ProviderDefaults.AsNoTracking().Where(d => d.ConnectionId == connection.Id).ToListAsync(ct))
                .GroupBy(d => ProviderDefaultDefinitions.Find(d.SettingKey)?.Key ?? d.SettingKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var customers = connection is null ? new Dictionary<string, string>() : await KnownCustomersAsync(connection.Id, ct);

        return new ProviderDefaultsModel
        {
            ClassTrackingEnabled      = data?.Preferences?.ClassTrackingEnabled,
            DepartmentTrackingEnabled = data?.Preferences?.TrackDepartments,
            DepartmentTerminology     = data?.Preferences?.DepartmentTerminology,
            DocumentTypes             = ProviderDefaultDefinitions.OverrideDocumentTypes.ToList(),
            Defaults = ProviderDefaultDefinitions.All.Select(def =>
            {
                stored.TryGetValue(def.Key, out var row);
                var (name, warning) = row is null ? (null, null) : Describe(def, row.SettingValue, data, customers);
                return new ProviderDefaultModel
                {
                    SettingKey    = def.Key,
                    BaseKey       = def.BaseKey,
                    Method        = def.Method,
                    Label         = def.Label,
                    ReferenceKind = def.ReferenceKind,
                    AccountTypes  = def.AccountTypes.ToList(),
                    SettingValue  = row?.SettingValue,
                    ValueName     = name,
                    Description   = row?.Description,
                    Warning       = warning,
                    ModifiedBy    = row?.ModifiedBy,
                    ModifiedAt    = row?.ModifiedAt
                };
            }).ToList()
        };
    }

    /// <summary>Upserts the keys sent (an empty value removes one); keys not sent are left alone.</summary>
    public async Task<ProviderDefaultsModel> SaveDefaultsAsync(SaveProviderDefaultsRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = await RequireConnectionAsync(ct);
        var items      = request.Values ?? [];

        if (items.Any(i => i is null)) throw new BadRequestException("A provider default row is empty.");

        var resolved = items.Select(i => (Item: i, Def: ProviderDefaultDefinitions.Find(i.SettingKey)
            ?? throw new BadRequestException($"Unknown provider default '{i.SettingKey}'."))).ToList();

        var duplicate = resolved.GroupBy(r => r.Def.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new BadRequestException($"{duplicate.Key} appears more than once.");

        foreach (var (item, _) in resolved)
            if (item.Description is { Length: > 300 })
                throw new BadRequestException($"The description of {item.SettingKey} is longer than 300 characters.");

        await ValidateAsync(connection, resolved.Select(r => (r.Def, Clean(r.Item.SettingValue))), ct);

        var existing = await _db.ProviderDefaults.Where(d => d.ConnectionId == connection.Id).ToListAsync(ct);
        var before   = existing.OrderBy(d => d.SettingKey).Select(d => new { d.SettingKey, d.SettingValue }).ToList();
        var now      = DateTime.UtcNow;

        foreach (var (item, def) in resolved)
        {
            var value = Clean(item.SettingValue);
            var row   = existing.FirstOrDefault(d => string.Equals(d.SettingKey, def.Key, StringComparison.OrdinalIgnoreCase));

            if (value is null)
            {
                if (row is not null)
                {
                    _db.ProviderDefaults.Remove(row);
                    existing.Remove(row);
                }
                continue;
            }

            if (row is null)
            {
                row = new ProviderDefault { OrganizationId = connection.OrganizationId, ConnectionId = connection.Id, SettingKey = def.Key };
                _db.ProviderDefaults.Add(row);
                existing.Add(row);
            }

            row.SettingKey   = def.Key;
            row.SettingValue = value;
            row.Description  = Clean(item.Description) ?? row.Description;
            row.ModifiedBy   = userId;
            row.ModifiedAt   = now;
        }

        var after = existing.OrderBy(d => d.SettingKey).Select(d => new { d.SettingKey, d.SettingValue }).ToList();
        IntegrationAudit.Add(_db, connection.Id, AuditAreas.ProviderDefaults, "Saved defaults", before, after, userId);
        await _db.SaveChangesAsync(ct);
        await RevalidateBlockedAsync(connection, ct);

        return await GetDefaultsAsync(ct);
    }

    // ── Overrides ───────────────────────────────────────────────────────────────────────────

    public async Task<List<DocumentOverrideModel>> GetOverridesAsync(CancellationToken ct = default)
    {
        var connection = await _accessor.GetCurrentAsync(ct);
        if (connection is null) return [];

        var data      = await _reference.GetCachedAsync(connection.Id, ct);
        var customers = await KnownCustomersAsync(connection.Id, ct);
        var rows      = await _db.DocumentOverrides.AsNoTracking().Where(o => o.ConnectionId == connection.Id).ToListAsync(ct);

        return rows
            .OrderBy(o => o.DocumentType, StringComparer.Ordinal).ThenBy(o => o.SettingKey, StringComparer.Ordinal)
            .Select(o =>
            {
                var def = ProviderDefaultDefinitions.Find(o.SettingKey);
                var (name, warning) = def is null ? (null, "Unknown setting.") : Describe(def, o.SettingValue, data, customers);
                return new DocumentOverrideModel
                {
                    DocumentType = o.DocumentType, SettingKey = o.SettingKey, SettingValue = o.SettingValue,
                    ValueName = name, Warning = warning, ModifiedBy = o.ModifiedBy, ModifiedAt = o.ModifiedAt
                };
            }).ToList();
    }

    /// <summary>Upserts the (type, key) pairs sent (an empty value removes one); others are left alone.</summary>
    public async Task<List<DocumentOverrideModel>> SaveOverridesAsync(SaveDocumentOverridesRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = await RequireConnectionAsync(ct);
        var items      = request.Overrides ?? [];

        if (items.Any(i => i is null)) throw new BadRequestException("An override row is empty.");

        var resolved = items.Select(i =>
        {
            var type = SyncControlDefaults.Normalize(i.DocumentType);
            if (type is null || !ProviderDefaultDefinitions.OverrideDocumentTypes.Contains(type))
                throw new BadRequestException(
                    $"'{i.DocumentType}' cannot have overrides. Use one of: {string.Join(", ", ProviderDefaultDefinitions.OverrideDocumentTypes)}.");
            var def = ProviderDefaultDefinitions.Find(i.SettingKey)
                ?? throw new BadRequestException($"Unknown provider default '{i.SettingKey}'.");
            return (Type: type, Def: def, Value: Clean(i.SettingValue));
        }).ToList();

        var duplicate = resolved.GroupBy(r => (r.Type, r.Def.Key)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new BadRequestException($"{duplicate.Key.Key} for {duplicate.Key.Type} appears more than once.");

        await ValidateAsync(connection, resolved.Select(r => (r.Def, r.Value)), ct);

        var existing = await _db.DocumentOverrides.Where(o => o.ConnectionId == connection.Id).ToListAsync(ct);
        var before   = Snapshot(existing);
        var now      = DateTime.UtcNow;

        foreach (var (type, def, value) in resolved)
        {
            var row = existing.FirstOrDefault(o => o.DocumentType == type
                                                && string.Equals(o.SettingKey, def.Key, StringComparison.OrdinalIgnoreCase));
            if (value is null)
            {
                if (row is not null)
                {
                    _db.DocumentOverrides.Remove(row);
                    existing.Remove(row);
                }
                continue;
            }

            if (row is null)
            {
                row = new DocumentOverride
                {
                    OrganizationId = connection.OrganizationId, ConnectionId = connection.Id, DocumentType = type, SettingKey = def.Key
                };
                _db.DocumentOverrides.Add(row);
                existing.Add(row);
            }

            row.SettingValue = value;
            row.ModifiedBy   = userId;
            row.ModifiedAt   = now;
        }

        IntegrationAudit.Add(_db, connection.Id, AuditAreas.ProviderDefaults, "Saved overrides", before, Snapshot(existing), userId);
        await _db.SaveChangesAsync(ct);
        await RevalidateBlockedAsync(connection, ct);

        return await GetOverridesAsync(ct);
    }

    // ── Validation ──────────────────────────────────────────────────────────────────────────

    private async Task ValidateAsync(
        IntegrationConnection connection, IEnumerable<(ProviderDefaultDefinition Def, string? Value)> values, CancellationToken ct)
    {
        var toCheck = values.Where(v => v.Value is not null).ToList();
        if (toCheck.Count == 0) return;

        if (toCheck.Any(v => v.Value!.Length > 50))
            throw new BadRequestException("That is not a QuickBooks id (too long).");

        RemoteReferenceData? data = null;
        if (toCheck.Any(v => v.Def.ReferenceKind != ProviderDefaultDefinitions.Customer))
            data = await _reference.GetCachedAsync(connection.Id, ct)
                ?? throw new BadRequestException(
                       "QuickBooks' reference data has not been loaded yet. Refresh the reference data first.");

        var customers = toCheck.Any(v => v.Def.ReferenceKind == ProviderDefaultDefinitions.Customer)
            ? await KnownCustomersAsync(connection.Id, ct)
            : new Dictionary<string, string>();

        foreach (var (def, value) in toCheck)
        {
            var id = value!;
            string Missing(string what) =>
                $"{def.Label}: QuickBooks {what} '{id}' is not in the reference data. Refresh the reference data, then choose again.";

            switch (def.ReferenceKind)
            {
                case ProviderDefaultDefinitions.Class:
                {
                    var c = data!.Classes.FirstOrDefault(x => x.Id == id) ?? throw new BadRequestException(Missing("class"));
                    if (!c.Active) throw new BadRequestException($"{def.Label}: QuickBooks class '{c.Name}' is inactive.");
                    break;
                }
                case ProviderDefaultDefinitions.Department:
                {
                    var d = data!.Departments.FirstOrDefault(x => x.Id == id) ?? throw new BadRequestException(Missing("location"));
                    if (!d.Active) throw new BadRequestException($"{def.Label}: QuickBooks location '{d.Name}' is inactive.");
                    break;
                }
                case ProviderDefaultDefinitions.Term:
                {
                    var t = data!.Terms.FirstOrDefault(x => x.Id == id) ?? throw new BadRequestException(Missing("term"));
                    if (!t.Active) throw new BadRequestException($"{def.Label}: QuickBooks term '{t.Name}' is inactive.");
                    break;
                }
                case ProviderDefaultDefinitions.PaymentMethod:
                {
                    var p = data!.PaymentMethods.FirstOrDefault(x => x.Id == id) ?? throw new BadRequestException(Missing("payment method"));
                    if (!p.Active) throw new BadRequestException($"{def.Label}: QuickBooks payment method '{p.Name}' is inactive.");
                    break;
                }
                case ProviderDefaultDefinitions.Account:
                {
                    var a = data!.Accounts.FirstOrDefault(x => x.Id == id) ?? throw new BadRequestException(Missing("account"));
                    if (!a.Active) throw new BadRequestException($"{def.Label}: QuickBooks account '{a.Name}' is inactive.");
                    if (!AccountTypeAllowed(def, a.AccountType))
                        throw new BadRequestException(
                            $"{def.Label}: '{a.Name}' is a {a.AccountType} account; it must be {string.Join(" or ", def.AccountTypes)}.");
                    break;
                }
                case ProviderDefaultDefinitions.Customer:
                    if (!customers.ContainsKey(id))
                        throw new BadRequestException(
                            $"{def.Label}: QuickBooks customer '{id}' is not known here. Import customers from QuickBooks (Match tab) "
                          + "or link the customer first, then choose again.");
                    break;
            }
        }
    }

    internal static bool AccountTypeAllowed(ProviderDefaultDefinition def, string? accountType) =>
        def.AccountTypes.Count == 0
        || def.AccountTypes.Any(t => IntegrationSettingsService.NormalizeAccountType(t) == IntegrationSettingsService.NormalizeAccountType(accountType));

    /// <summary>The value's name in QuickBooks and, when it is no longer usable or not sent, why.</summary>
    private static (string? Name, string? Warning) Describe(
        ProviderDefaultDefinition def, string value, RemoteReferenceData? data, IReadOnlyDictionary<string, string> customers)
    {
        if (data is null && def.ReferenceKind != ProviderDefaultDefinitions.Customer) return (null, null);

        (string? Name, bool Active)? found = def.ReferenceKind switch
        {
            ProviderDefaultDefinitions.Class         => data!.Classes.Where(x => x.Id == value).Select(x => ((string?)(x.FullyQualifiedName ?? x.Name), x.Active)).FirstOrDefault(),
            ProviderDefaultDefinitions.Department    => data!.Departments.Where(x => x.Id == value).Select(x => ((string?)(x.FullyQualifiedName ?? x.Name), x.Active)).FirstOrDefault(),
            ProviderDefaultDefinitions.Term          => data!.Terms.Where(x => x.Id == value).Select(x => ((string?)x.Name, x.Active)).FirstOrDefault(),
            ProviderDefaultDefinitions.PaymentMethod => data!.PaymentMethods.Where(x => x.Id == value).Select(x => ((string?)x.Name, x.Active)).FirstOrDefault(),
            ProviderDefaultDefinitions.Account       => data!.Accounts.Where(x => x.Id == value).Select(x => ((string?)x.Name, x.Active)).FirstOrDefault(),
            ProviderDefaultDefinitions.Customer      => customers.TryGetValue(value, out var n) ? (n, true) : null,
            _ => null
        };

        // A default tuple (no match) has a null name.
        if (found is null || found.Value.Name is null)
            return (null, "No longer in QuickBooks' reference data — refresh it, or choose again.");
        if (!found.Value.Active)
            return (found.Value.Name, "Inactive in QuickBooks — choose another.");
        if (def.ReferenceKind == ProviderDefaultDefinitions.Class && data?.Preferences?.ClassTrackingEnabled != true)
            return (found.Value.Name, data?.Preferences?.ClassTrackingEnabled == false
                ? "Not sent: the QuickBooks company does not track classes."
                : "Not sent until the reference data shows the company tracks classes — refresh it.");
        if (def.ReferenceKind == ProviderDefaultDefinitions.Department && data?.Preferences?.TrackDepartments != true)
            return (found.Value.Name, data?.Preferences?.TrackDepartments == false
                ? "Not sent: the QuickBooks company does not track locations."
                : "Not sent until the reference data shows the company tracks locations — refresh it.");
        return (found.Value.Name, null);
    }

    /// <summary>QuickBooks customer id → name: imported customers, and customers the gateway has linked or created.</summary>
    private async Task<Dictionary<string, string>> KnownCustomersAsync(int connectionId, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var r in await _db.RemoteEntities.AsNoTracking()
                     .Where(r => r.ConnectionId == connectionId && r.Kind == SyncKind.Customer)
                     .Select(r => new { r.RemoteId, r.Name }).ToListAsync(ct))
            result.TryAdd(r.RemoteId, r.Name);

        foreach (var m in await _db.EntityMaps.AsNoTracking()
                     .Where(m => m.ConnectionId == connectionId && m.Kind == SyncKind.Customer && m.RemoteId != null)
                     .Select(m => new { m.RemoteId, m.RemoteName, m.DisplayLabel }).ToListAsync(ct))
            result.TryAdd(m.RemoteId!, m.RemoteName ?? m.DisplayLabel);

        return result;
    }

    private async Task<IntegrationConnection> RequireConnectionAsync(CancellationToken ct) =>
        await _accessor.GetCurrentAsync(ct)
        ?? throw new ConflictException("Connect QuickBooks first — defaults belong to a connected company.");

    private static object Snapshot(IEnumerable<DocumentOverride> rows) =>
        rows.OrderBy(o => o.DocumentType, StringComparer.Ordinal).ThenBy(o => o.SettingKey, StringComparer.Ordinal)
            .Select(o => new { o.DocumentType, o.SettingKey, o.SettingValue }).ToList();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>
/// Plan D-2: the name a record gets in QuickBooks. QuickBooks requires a DisplayName to be unique
/// across customers, vendors and employees, and an item name to be unique across items; callers do
/// not enforce either. The name is never truncated — a clash gets a disambiguating suffix instead.
/// <list type="bullet">
/// <item>Customer: its name. If another customer/vendor map already holds that name in QuickBooks → <c>Name (Code)</c>.</item>
/// <item>Vendor: its name if free, otherwise <c>Name (Code)</c>. A customer of the same name has priority,
/// even one not pushed yet (a partner that is both is the common case).</item>
/// <item>Item: <c>Name</c> or <c>Name - Variant</c>; on a clash with another item → <c>… (Sku)</c>.</item>
/// </list>
/// A name already used for this record is kept while it is still one of its valid forms, so a
/// record is not renamed back and forth.
/// </summary>
internal interface IRemoteNameResolver
{
    Task<string> ResolvePartyNameAsync(EntityMap map, PartyPayload payload, CancellationToken ct = default);
    Task<string> ResolveItemNameAsync(EntityMap map, ItemPayload payload, CancellationToken ct = default);
}

internal sealed class RemoteNameResolver : IRemoteNameResolver
{
    public const int MaxNameLength = 100;

    private readonly IntegrationDbContext _db;

    public RemoteNameResolver(IntegrationDbContext db) => _db = db;

    public static string PartyBaseName(PartyPayload p) => (p.DisplayName ?? string.Empty).Trim();

    public static string ItemBaseName(ItemPayload p)
    {
        var name = (p.Name ?? string.Empty).Trim();
        var variant = p.VariantName?.Trim();
        return string.IsNullOrEmpty(variant) ? name : $"{name} - {variant}";
    }

    public static string ShortId(string externalId)
    {
        var id = (externalId ?? string.Empty).Trim();
        return id.Length <= 8 ? id : id[..8];
    }

    public async Task<string> ResolvePartyNameAsync(EntityMap map, PartyPayload payload, CancellationToken ct = default)
    {
        var baseName = PartyBaseName(payload);
        var code     = string.IsNullOrWhiteSpace(payload.Code) ? ShortId(map.ExternalId) : payload.Code.Trim();
        var suffixed = $"{baseName} ({code})";

        return await ChooseAsync(map, baseName, suffixed, name => PartyClashAsync(map, name, ct));
    }

    public async Task<string> ResolveItemNameAsync(EntityMap map, ItemPayload payload, CancellationToken ct = default)
    {
        var baseName = ItemBaseName(payload);
        var sku      = string.IsNullOrWhiteSpace(payload.Sku) ? ShortId(map.ExternalId) : payload.Sku.Trim();
        var suffixed = $"{baseName} ({sku})";

        return await ChooseAsync(map, baseName, suffixed, name => ItemClashAsync(map, name, ct));
    }

    private static async Task<string> ChooseAsync(EntityMap map, string baseName, string suffixed, Func<string, Task<bool>> clashes)
    {
        if (string.IsNullOrEmpty(baseName)) return baseName;

        // Keep the name this record already has in QuickBooks while it is still a valid form of it.
        var current = map.RemoteName?.Trim();
        if (!string.IsNullOrEmpty(current)
            && (Same(current, baseName) || Same(current, suffixed))
            && !await clashes(current))
            return current;

        return await clashes(baseName) ? suffixed : baseName;
    }

    private async Task<bool> PartyClashAsync(EntityMap map, string name, CancellationToken ct)
    {
        var upper = name.Trim().ToUpperInvariant();

        // Any other customer/vendor already holding this name in QuickBooks.
        var taken = await _db.EntityMaps.AnyAsync(m =>
            m.ConnectionId == map.ConnectionId
            && m.Id != map.Id
            && (m.Kind == SyncKind.Customer || m.Kind == SyncKind.Vendor)
            && m.RemoteName != null
            && m.RemoteName.ToUpper() == upper, ct);
        if (taken) return true;

        // A vendor yields to a customer of the same name even before that customer is pushed.
        if (map.Kind == SyncKind.Vendor)
            return await _db.EntityMaps.AnyAsync(m =>
                m.ConnectionId == map.ConnectionId
                && m.Kind == SyncKind.Customer
                && m.RemoteName == null
                && m.PayloadJson != null
                && m.DisplayLabel.ToUpper() == upper, ct);

        return false;
    }

    private Task<bool> ItemClashAsync(EntityMap map, string name, CancellationToken ct)
    {
        var upper = name.Trim().ToUpperInvariant();
        return _db.EntityMaps.AnyAsync(m =>
            m.ConnectionId == map.ConnectionId
            && m.Id != map.Id
            && m.Kind == SyncKind.Item
            && m.RemoteName != null
            && m.RemoteName.ToUpper() == upper, ct);
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

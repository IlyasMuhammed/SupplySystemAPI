using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>An item a sales document uses that QuickBooks holds without an income account, and the account to add (D-55a).</summary>
internal sealed record ItemIncomeFix(int ItemMapId, string ItemRemoteId, string ItemLabel, string IncomeAccountId);

/// <summary>
/// A44 D-55a (QA-1, found live): SCM sends a product that is not saleable with <c>IsSold = false</c>, so the gateway creates
/// its QuickBooks item without an income account — and QuickBooks then refuses every invoice / receipt / credit using it
/// (2390). The item payload is not changed (that would re-send every item, D-3). Instead a sales document that uses
/// such an item first adds the income account to it with a sparse item update (the executor runs it, once per item), or
/// is refused with <see cref="NotSaleableCode"/> when there is no income account to add.
/// <para>
/// "Without an income account" is known only for items SCM created: the stored payload says not sold and the item was not
/// adopted from the accountant's records (an adopted item's accounts are QuickBooks' own, unknown here).
/// </para>
/// </summary>
internal static class SaleableItems
{
    public const string NotSaleableCode = "ITEM_NOT_SALEABLE";

    /// <summary>The item map's warning after the fix-up; its presence means "already fixed" (a later item sync may clear it, then it is fixed again — harmless).</summary>
    public const string FixedMarker = "income account added in QuickBooks";

    public static string FixedWarning(string documentNumber) => $"Sold on {documentNumber}; {FixedMarker}.";

    public static string NotSaleableMessage(string itemLabel) =>
        $"Product {itemLabel} is not set up for sale in QuickBooks: set a default income account or mark the product saleable.";

    /// <summary>The effective income account: the settings default, else the A42 mapping of DEFAULT_REVENUE (D-16).</summary>
    public static string? IncomeAccount(IntegrationSettings settings, DocumentDefaults defaults) =>
        string.IsNullOrWhiteSpace(settings.DefaultIncomeAccountId) ? defaults.MappedAccount("DEFAULT_REVENUE") : settings.DefaultIncomeAccountId.Trim();

    /// <summary>
    /// The SCM-created items among <paramref name="itemExternalIds"/> whose stored payload is not for sale (whether or not
    /// they are in QuickBooks yet), with their labels.
    /// </summary>
    public static async Task<IReadOnlyList<(int MapId, string? RemoteId, string Label, string? Warning)>> NotForSaleAsync(
        IntegrationDbContext db, EntityMap owner, IEnumerable<string?> itemExternalIds, CancellationToken ct)
    {
        var ids = itemExternalIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!.Trim()).Distinct().ToList();
        if (ids.Count == 0) return [];

        var rows = await db.EntityMaps.AsNoTracking()
            .Where(m => m.ConnectionId == owner.ConnectionId && m.SourceSystem == owner.SourceSystem && m.Kind == SyncKind.Item
                     && ids.Contains(m.ExternalId) && m.PayloadJson != null && m.LinkOrigin != LinkOrigin.Adopted)
            .Select(m => new { m.Id, m.RemoteId, m.DisplayLabel, m.ExternalId, m.PayloadJson, m.Warning })
            .ToListAsync(ct);

        var result = new List<(int, string?, string, string?)>();
        foreach (var row in rows)
        {
            ItemPayload? item;
            try
            {
                item = SyncPayloads.Deserialize(SyncKind.Item, row.PayloadJson!) as ItemPayload;
            }
            catch (JsonException)
            {
                continue;
            }
            if (item is null || item.IsSold) continue;
            result.Add((row.Id, row.RemoteId, string.IsNullOrWhiteSpace(row.DisplayLabel) ? row.ExternalId : row.DisplayLabel!, row.Warning));
        }
        return result;
    }
}

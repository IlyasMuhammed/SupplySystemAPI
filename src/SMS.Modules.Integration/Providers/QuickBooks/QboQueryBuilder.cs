using System.Globalization;
using System.Text;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Providers.QuickBooks;

/// <summary>
/// Builds QuickBooks Online query-language strings. Every value that came from outside passes through
/// <see cref="Literal"/> — nothing is ever concatenated into a query unescaped.
/// <para>
/// QBO strings are single-quoted; a quote inside one is written <c>\'</c> and, because the backslash is
/// the escape character, a literal backslash is written <c>\\</c>. Control characters cannot be matched
/// meaningfully and are removed. The query travels as the POST body (UTF-8, <c>application/text</c>), so
/// no URL encoding is involved.
/// </para>
/// </summary>
internal sealed class QboQueryBuilder
{
    /// <summary>QuickBooks' own ceiling for MAXRESULTS.</summary>
    public const int MaxResultsCap = 1000;

    /// <summary>Entities <see cref="SelectPage"/> may read — reference data only, never a caller-chosen name.</summary>
    private static readonly HashSet<string> ReferenceEntities = new(StringComparer.Ordinal)
    {
        "Account", "TaxCode", "TaxRate", "Term", "CompanyCurrency"
    };

    /// <summary>Customer / Vendor by DisplayName, Item by Name — active and inactive.</summary>
    public string FindByName(SyncKind kind, string name) => kind switch
    {
        SyncKind.Customer => $"select * from Customer where DisplayName = {Literal(name)} and Active IN (true, false)",
        SyncKind.Vendor   => $"select * from Vendor where DisplayName = {Literal(name)} and Active IN (true, false)",
        SyncKind.Item     => $"select * from Item where Name = {Literal(name)} and Active IN (true, false)",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only customers, vendors and items are found by name.")
    };

    public string FindItemBySku(string sku) =>
        $"select * from Item where Sku = {Literal(sku)} and Active IN (true, false)";

    public string FindInvoiceByDocNumber(string docNumber) =>
        $"select * from Invoice where DocNumber = {Literal(docNumber)}";

    /// <summary>
    /// Bills by DocNumber only. The vendor is filtered on the results by the provider, so the lookup does
    /// not depend on VendorRef being a filterable field.
    /// </summary>
    public string FindBillByDocNumber(string docNumber) =>
        $"select * from Bill where DocNumber = {Literal(docNumber)}";

    /// <summary>One page of Customers / Vendors / Items, active and inactive. <paramref name="startPosition"/> is 1-based.</summary>
    public string ListPage(SyncKind kind, int startPosition, int maxResults)
    {
        var entity = kind switch
        {
            SyncKind.Customer => "Customer",
            SyncKind.Vendor   => "Vendor",
            SyncKind.Item     => "Item",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only customers, vendors and items can be listed.")
        };
        return $"select * from {entity} where Active IN (true, false){Page(startPosition, maxResults)}";
    }

    /// <summary>One page of a reference entity (Account, TaxCode, TaxRate, Term, CompanyCurrency).</summary>
    public string SelectPage(string entityName, int startPosition, int maxResults)
    {
        if (!ReferenceEntities.Contains(entityName))
            throw new ArgumentOutOfRangeException(nameof(entityName), entityName, "Not a reference entity this builder reads.");
        return $"select * from {entityName}{Page(startPosition, maxResults)}";
    }

    public static int ClampStart(int startPosition) => startPosition < 1 ? 1 : startPosition;

    public static int ClampMaxResults(int maxResults) => maxResults < 1 ? 1 : Math.Min(maxResults, MaxResultsCap);

    private static string Page(int startPosition, int maxResults) =>
        string.Create(CultureInfo.InvariantCulture, $" STARTPOSITION {ClampStart(startPosition)} MAXRESULTS {ClampMaxResults(maxResults)}");

    /// <summary>
    /// A quoted QBO string literal: control characters removed, <c>\</c> → <c>\\</c>, <c>'</c> → <c>\'</c>.
    /// Everything else (including unicode) is kept as is.
    /// </summary>
    public static string Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var sb = new StringBuilder(value.Length + 8);
        sb.Append('\'');
        foreach (var c in value)
        {
            if (char.IsControl(c)) continue;
            if (c == '\\') sb.Append(@"\\");
            else if (c == '\'') sb.Append(@"\'");
            else sb.Append(c);
        }
        sb.Append('\'');
        return sb.ToString();
    }
}

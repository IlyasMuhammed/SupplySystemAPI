using System.Text;
using SMS.Shared.Common;

namespace SMS.Modules.Import.Services;

/// <summary>One column's automatic match.</summary>
internal sealed record AutoMatch(int ColumnIndex, string? FieldName, decimal? Confidence);

/// <summary>
/// A40 C6 — four-tier automatic column mapping: 1.00 exact field or display name (case-insensitive), 0.95 normalized
/// (lower case, letters and digits only), 0.90 alias (the field's own aliases + <see cref="BuiltInAliases"/>), fuzzy
/// Levenshtein distance ≤ 2 scored 1 − distance / length capped at 0.89; below 0.70 the column stays unmapped. Each field
/// and each column is used once, best score first.
/// </summary>
internal static class ImportAutoMapper
{
    public const decimal Exact = 1.00m, Normalized = 0.95m, Alias = 0.90m, FuzzyCap = 0.89m, Threshold = 0.70m;

    /// <summary>
    /// Built-in aliases by NORMALIZED field name — common legacy / SAP-style column names (MATNR → sku, MAKTX → name …).
    /// Applied to any profile whose field has that name.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string[]> BuiltInAliases = new Dictionary<string, string[]>
    {
        ["externalid"]    = ["ext_id", "extid", "legacy_id", "legacyid", "source_id", "sourceid", "external_ref", "external_reference", "old_id", "legacy_code"],
        ["sku"]           = ["matnr", "material", "material_no", "material_number", "item_code", "item_no", "item_number", "article_number", "part_number", "part_no", "product_code"],
        ["name"]          = ["maktx", "name1", "item_name", "product_name", "material_description", "full_name"],
        ["description"]   = ["maktx_long", "long_description", "desc", "item_description"],
        ["code"]          = ["kunnr", "lifnr", "customer_number", "vendor_number", "supplier_number"],
        ["customercode"]  = ["kunnr", "customer_number", "customer_no", "customer_id", "debtor"],
        ["suppliercode"]  = ["lifnr", "vendor_code", "vendor_number", "vendor_no", "vendor_id", "supplier_number", "creditor"],
        ["barcode"]       = ["ean", "ean11", "upc", "gtin"],
        ["uom"]           = ["meins", "base_unit", "unit_of_measure", "uom_code", "unit"],
        ["uomcode"]       = ["meins", "base_unit", "unit_of_measure", "uom", "unit"],
        ["unitcode"]      = ["meins", "base_unit", "unit_of_measure", "uom"],
        ["categorycode"]  = ["matkl", "material_group", "product_group", "category"],
        ["email"]         = ["e_mail", "email_address", "smtp_addr", "mail"],
        ["phone"]         = ["telephone", "tel", "telf1", "phone_number", "mobile", "contact_number"],
        ["taxcode"]       = ["mwskz", "tax", "vat_code"],
        ["currency"]      = ["waers", "curr", "currency_code"],
        ["currencycode"]  = ["waers", "curr", "currency"],
        ["quantity"]      = ["qty", "menge", "quantity_on_hand", "opening_qty", "on_hand", "stock"],
        ["unitcost"]      = ["cost", "stprs", "verpr", "standard_price", "cost_price", "average_cost"],
        ["sellingprice"]  = ["netpr", "sales_price", "list_price", "price", "retail_price"],
        ["purchaseprice"] = ["purchase_cost", "buying_price", "buy_price"],
        ["price"]         = ["netpr", "sales_price", "list_price", "unit_price"],
        ["warehousecode"] = ["werks", "lgort", "plant", "storage_location", "warehouse"],
        ["city"]          = ["ort01", "town"],
        ["country"]       = ["land1", "country_code"],
        ["address"]       = ["stras", "street", "address_line1", "address1"],
        ["postalcode"]    = ["pstlz", "zip", "zip_code", "postcode"],
        ["amount"]        = ["balance", "opening_balance", "outstanding", "dmbtr", "wrbtr"],
    };

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    public static IReadOnlyList<AutoMatch> Map(IReadOnlyList<string> headers, IReadOnlyList<ImportFieldDefinition> fields)
    {
        var candidates = new List<(int Col, ImportFieldDefinition Field, decimal Score)>();
        for (var col = 0; col < headers.Count; col++)
        {
            var header = headers[col]?.Trim() ?? string.Empty;
            if (header.Length == 0) continue;
            foreach (var field in fields)
                if (Score(header, field) is { } score && score >= Threshold)
                    candidates.Add((col, field, score));
        }

        var usedCols = new HashSet<int>();
        var usedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chosen = new Dictionary<int, (string Field, decimal Score)>();
        foreach (var c in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Col).ThenBy(c => c.Field.SortOrder))
        {
            if (usedCols.Contains(c.Col) || usedFields.Contains(c.Field.FieldName)) continue;
            usedCols.Add(c.Col);
            usedFields.Add(c.Field.FieldName);
            chosen[c.Col] = (c.Field.FieldName, c.Score);
        }

        return Enumerable.Range(0, headers.Count)
            .Select(i => chosen.TryGetValue(i, out var m) ? new AutoMatch(i, m.Field, m.Score) : new AutoMatch(i, null, null))
            .ToList();
    }

    /// <summary>The best tier score of a header against one field, or null.</summary>
    public static decimal? Score(string header, ImportFieldDefinition field)
    {
        var h = header.Trim();
        if (h.Equals(field.FieldName, StringComparison.OrdinalIgnoreCase) || h.Equals(field.DisplayName, StringComparison.OrdinalIgnoreCase))
            return Exact;

        var nh = Normalize(h);
        if (nh.Length == 0) return null;
        var names = new[] { Normalize(field.FieldName), Normalize(field.DisplayName) };
        if (names.Contains(nh)) return Normalized;

        var aliases = AliasesOf(field);
        if (aliases.Contains(nh)) return Alias;

        decimal? best = null;
        foreach (var candidate in names.Concat(aliases).Where(n => n.Length > 0).Distinct())
        {
            var distance = Levenshtein(nh, candidate, 2);
            if (distance > 2) continue;
            var score = Math.Min(FuzzyCap, Math.Round(1m - (decimal)distance / Math.Max(nh.Length, candidate.Length), 2));
            if (best is null || score > best) best = score;
        }
        return best;
    }

    private static List<string> AliasesOf(ImportFieldDefinition field)
    {
        var list = (field.Aliases ?? []).Select(Normalize).ToList();
        if (BuiltInAliases.TryGetValue(Normalize(field.FieldName), out var builtIn)) list.AddRange(builtIn.Select(Normalize));
        return list.Where(a => a.Length > 0).Distinct().ToList();
    }

    /// <summary>Levenshtein distance, giving up (returning max+1) once it must exceed <paramref name="max"/>.</summary>
    public static int Levenshtein(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            var rowMin = curr[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                rowMin = Math.Min(rowMin, curr[j]);
            }
            if (rowMin > max) return max + 1;
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}

using Microsoft.EntityFrameworkCore;
using SMS.Modules.Lookups.Domain;

namespace SMS.Modules.Lookups.Data;

internal sealed class LookupsDataSeeder
{
    private readonly LookupsDbContext _db;

    public LookupsDataSeeder(LookupsDbContext db) => _db = db;

    public async Task SeedAsync()
    {
        await _db.Database.EnsureCreatedAsync();

        var types = new[]
        {
            (Slug: "supplier-type",      Name: "Supplier Type",              Description: "Classification of supplier by business model"),
            (Slug: "industry-category",  Name: "Industry Category",          Description: "Industry sector of the supplier"),
            (Slug: "supplier-status",    Name: "Supplier Status",            Description: "Operational status of a supplier"),
            (Slug: "payment-terms",      Name: "Payment Terms",              Description: "Agreed payment schedule between buyer and supplier"),
            (Slug: "currency",           Name: "Currency",                   Description: "Currencies used in transactions"),
            (Slug: "country",            Name: "Country",                    Description: "Countries for address and origin"),
            (Slug: "uom",               Name: "Unit of Measure",            Description: "Units used to quantify goods"),
            (Slug: "priority",           Name: "Priority",                   Description: "Urgency level for requisitions and orders"),
            (Slug: "pr-type",            Name: "Purchase Requisition Type",  Description: "Category of purchase requisition"),
            (Slug: "po-type",            Name: "Purchase Order Type",        Description: "Category of purchase order"),
            (Slug: "tax-code",           Name: "Tax Code",                   Description: "Tax codes applicable in Pakistan"),
            (Slug: "amendment-type",     Name: "Amendment Type",             Description: "Reason for amending a purchase order"),
        };

        // seed lookup types idempotently by slug
        foreach (var (slug, name, desc) in types)
        {
            if (!_db.LookupTypes.Any(t => t.Slug == slug))
            {
                _db.LookupTypes.Add(new LookupType
                {
                    Id = Guid.NewGuid(),
                    Slug = slug,
                    Name = name,
                    Description = desc,
                    IsActive = true
                });
            }
        }
        await _db.SaveChangesAsync();

        // seed values per type
        await SeedValuesAsync("supplier-type", new[]
        {
            "Local Vendor", "International Supplier", "Manufacturer", "Distributor", "Service Provider"
        });

        await SeedValuesAsync("industry-category", new[]
        {
            "Manufacturing", "Construction", "Healthcare", "IT & Technology",
            "Agriculture", "Retail & Wholesale", "Food & Beverage", "Logistics & Transport"
        });

        await SeedValuesAsync("supplier-status", new[]
        {
            "Active", "Inactive", "Blacklisted", "Under Review", "Pending Approval"
        });

        await SeedValuesAsync("payment-terms", new[]
        {
            "Net 15", "Net 30", "Net 45", "Net 60",
            "Cash on Delivery", "Advance Payment", "50% Advance", "2/10 Net 30"
        });

        await SeedValuesAsync("currency", new[]
        {
            "Pakistani Rupee (PKR)", "US Dollar (USD)", "Euro (EUR)", "British Pound (GBP)",
            "UAE Dirham (AED)", "Saudi Riyal (SAR)", "Chinese Yuan (CNY)", "Japanese Yen (JPY)"
        });

        await SeedValuesAsync("country", new[]
        {
            "Pakistan", "United States", "United Kingdom", "China",
            "United Arab Emirates", "Saudi Arabia", "Germany", "France", "Japan", "India"
        });

        await SeedValuesAsync("uom", new[]
        {
            "Piece (PCS)", "Kilogram (KG)", "Gram (G)", "Liter (L)", "Meter (M)",
            "Box (BOX)", "Carton (CTN)", "Set (SET)", "Pair (PR)", "Dozen (DOZ)"
        });

        await SeedValuesAsync("priority", new[]
        {
            "Critical", "High", "Medium", "Low", "Routine"
        });

        await SeedValuesAsync("pr-type", new[]
        {
            "Standard", "Emergency", "Capital Expenditure (CapEx)", "Recurring", "Project-Based"
        });

        await SeedValuesAsync("po-type", new[]
        {
            "Standard Purchase Order", "Blanket Purchase Order",
            "Contract Purchase Order", "Emergency Purchase Order"
        });

        await SeedValuesAsync("tax-code", new[]
        {
            "GST 17%", "GST 0% (Exempt)", "WHT 5%", "WHT 10%",
            "SED (Special Excise Duty)", "FED (Federal Excise Duty)"
        });

        await SeedValuesAsync("amendment-type", new[]
        {
            "Quantity Change", "Price Change", "Delivery Date Change", "Scope Change", "Cancellation"
        });

        await SeedCurrenciesAsync();
    }

    /// <summary>A35 §2.3 — the seed codes every organization's currency list is built from (D-1).</summary>
    internal static readonly (string Code, string Name, string Symbol)[] SeedCurrencies =
    [
        ("PKR", "Pakistani Rupee", "₨"), ("USD", "US Dollar", "$"), ("EUR", "Euro", "€"), ("GBP", "British Pound", "£"),
        ("SAR", "Saudi Riyal", "﷼"), ("AED", "UAE Dirham", "د.إ"), ("CNY", "Chinese Yuan", "¥"), ("JPY", "Japanese Yen", "¥"),
        ("BHD", "Bahraini Dinar", "BD"), ("OMR", "Omani Rial", "OMR"), ("CAD", "Canadian Dollar", "C$"),
        ("AUD", "Australian Dollar", "A$"), ("INR", "Indian Rupee", "₹"), ("TRY", "Turkish Lira", "₺"),
        ("MYR", "Malaysian Ringgit", "RM"), ("KWD", "Kuwaiti Dinar", "KD"), ("QAR", "Qatari Riyal", "QR"), ("CHF", "Swiss Franc", "CHF"),
    ];

    /// <summary>
    /// A35 P1-13 (D-1) — adds whichever seed codes the global catalog lacks (rows only, no schema change; Lookups has no
    /// migrations at startup). Existing rows are never changed, except that a row with the seed's exact name and no code
    /// gets the code (instead of a second row of that name). Idempotent.
    /// </summary>
    internal async Task SeedCurrenciesAsync()
    {
        var existing = await _db.Currencies.ToListAsync();
        var codes    = existing.Where(c => !string.IsNullOrWhiteSpace(c.Code))
            .Select(c => c.Code!.Trim().ToUpperInvariant()).ToHashSet();

        foreach (var (code, name, symbol) in SeedCurrencies)
        {
            if (codes.Contains(code)) continue;

            var sameName = existing.FirstOrDefault(c =>
                string.IsNullOrWhiteSpace(c.Code) && string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
            if (sameName is not null)
            {
                sameName.Code = code;
                sameName.Symbol ??= symbol;
            }
            else if (existing.Any(c => string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            {
                // A same-named row under another code: add ours with the code in the name to keep names distinct.
                _db.Currencies.Add(new Currency { Id = Guid.NewGuid(), Name = $"{name} ({code})", Code = code, Symbol = symbol });
            }
            else
            {
                _db.Currencies.Add(new Currency { Id = Guid.NewGuid(), Name = name, Code = code, Symbol = symbol });
            }
            codes.Add(code);
        }
        await _db.SaveChangesAsync();
    }

    private async Task SeedValuesAsync(string typeSlug, string[] displayNames)
    {
        var type = _db.LookupTypes.FirstOrDefault(t => t.Slug == typeSlug);
        if (type == null) return;

        for (var i = 0; i < displayNames.Length; i++)
        {
            var name = displayNames[i];
            if (!_db.LookupValues.Any(v => v.TypeId == type.Id && v.DisplayName == name))
            {
                _db.LookupValues.Add(new LookupValue
                {
                    Id = Guid.NewGuid(),
                    TypeId = type.Id,
                    DisplayName = name,
                    IsActive = true,
                    SortOrder = i + 1,
                    IsGlobal = true,
                    OrganizationId = null
                });
            }
        }

        await _db.SaveChangesAsync();
    }
}

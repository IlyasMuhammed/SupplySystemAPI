namespace SMS.Modules.Onboarding.SampleData;

// A50 D-27: the sample data API shapes (docs/onboarding/SAMPLE-DATA.md §1.1).

/// <summary>DemoRecords.EntityType values written by the contributors (D-70). Contributors use the same literals: they
/// cannot reference this module.</summary>
public static class DemoEntityTypes
{
    public const string Customer       = "Customer";
    public const string Vendor         = "Vendor";
    public const string Product        = "Product";
    public const string ProductVariant = "ProductVariant";
    public const string SaleOrder      = "SaleOrder";
    public const string PurchaseOrder  = "PurchaseOrder";
    public const string OpeningStock   = "OpeningStock";

    /// <summary>D-73: a ref of this type is a message from a contributor (something skipped), never stored.</summary>
    public const string Note           = "Note";

    /// <summary>The counts' JSON keys, in display order. ProductVariant is recorded but not counted.</summary>
    internal static readonly IReadOnlyList<(string EntityType, string Key)> Counted =
    [
        (Customer, "customers"), (Vendor, "vendors"), (Product, "products"),
        (SaleOrder, "saleOrders"), (PurchaseOrder, "purchaseOrders"), (OpeningStock, "openingStock"),
    ];

    internal static Dictionary<string, int> EmptyCounts() => Counted.ToDictionary(c => c.Key, _ => 0);

    internal static string? KeyOf(string entityType) =>
        Counted.FirstOrDefault(c => string.Equals(c.EntityType, entityType, StringComparison.Ordinal)).Key;
}

public sealed class SampleDataStatusModel
{
    public bool HasDemo { get; set; }
    public bool CanLoad { get; set; }
    public string? Reason { get; set; }
    public DateTime? LoadedAt { get; set; }
    /// <summary>What a load would create.</summary>
    public Dictionary<string, int> Counts { get; set; } = DemoEntityTypes.EmptyCounts();
    /// <summary>The demo records the organization has now.</summary>
    public Dictionary<string, int> Current { get; set; } = DemoEntityTypes.EmptyCounts();
    public List<string> Notes { get; set; } = new();
}

public sealed class SampleDataLoadResult
{
    public Dictionary<string, int> Created { get; set; } = DemoEntityTypes.EmptyCounts();
    public int Total { get; set; }
    public List<string> Notes { get; set; } = new();
}

public sealed class KeptDemoRecordModel
{
    public string EntityType { get; set; } = string.Empty;
    public string EntityRef { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Reason { get; set; }
}

public sealed class SampleDataCleanupResult
{
    public Dictionary<string, int> Deleted { get; set; } = DemoEntityTypes.EmptyCounts();
    public int Total { get; set; }
    public List<KeptDemoRecordModel> Kept { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

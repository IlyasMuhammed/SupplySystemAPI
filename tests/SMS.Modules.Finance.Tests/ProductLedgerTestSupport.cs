using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Tests;

/// <summary>Inventory's side of the product ledger: which product each variant belongs to.</summary>
internal sealed class FakeVariants : IProductVariantResolver
{
    public Dictionary<Guid, Guid> Products { get; } = [];
    /// <summary>What Inventory calls each product; "A Product" for one not named here.</summary>
    public Dictionary<Guid, string> ProductNames { get; } = [];
    public int Calls { get; private set; }

    /// <summary>A new variant of a (new or given) product, known to Inventory.</summary>
    public (Guid Variant, Guid Product) New(Guid? product = null)
    {
        var variant = Guid.NewGuid();
        var owner   = product ?? Guid.NewGuid();
        Products[variant] = owner;
        return (variant, owner);
    }

    public Task<IReadOnlyDictionary<Guid, DefaultVariantResult>> ResolveDefaultVariantsAsync(IReadOnlyList<Guid> productUuids) =>
        throw new NotSupportedException();

    public Task<IReadOnlyDictionary<Guid, VariantDescription>> DescribeVariantsAsync(IReadOnlyList<Guid> variantUuids)
    {
        Calls++;
        IReadOnlyDictionary<Guid, VariantDescription> found = variantUuids.Where(Products.ContainsKey).ToDictionary(
            id => id, id => new VariantDescription(
                id, Products[id], "SKU-1", "Default", ProductNames.GetValueOrDefault(Products[id]) ?? "A Product", true, null));
        return Task.FromResult(found);
    }
}

/// <summary>
/// A product ledger that takes no part: for the tests of what an invoice does that have nothing to say
/// about cost. It hands back an entry that is never tracked, so nothing is saved for it and nothing
/// can be refused by it.
/// </summary>
internal sealed class NoProductLedger : IProductLedgerWriter
{
    public Task<ProductLedgerEntry> TrackEntryAsync(ProductLedgerPosting posting) =>
        Task.FromResult(new ProductLedgerEntry
        {
            UUID = Guid.NewGuid(), VariantUuid = posting.VariantUuid, ProductUuid = Guid.NewGuid(), SequenceNo = 1,
            EntryType = posting.EntryType, Direction = posting.Direction, Quantity = posting.Quantity
        });

    public Task<ProductLedgerPosted> AppendEntryAsync(ProductLedgerPosting posting, System.Data.Common.DbTransaction? transaction = null) =>
        throw new NotSupportedException();
}

/// <summary>
/// The product ledger over an in-memory database shared by every scope opened from it — each
/// <see cref="Open"/> is a fresh context, as each request is, and one organization unless asked otherwise.
/// </summary>
internal sealed class ProductLedgerRig
{
    internal const int User = 42;

    public string       DbName   { get; } = Guid.NewGuid().ToString();
    public Guid         Org      { get; } = Guid.NewGuid();
    public TestClock    Clock    { get; } = new();
    public FakeVariants Variants { get; } = new();

    public (FinanceDbContext Db, ProductLedgerService Service) Open(IInterceptor? interceptor = null, Guid? org = null)
    {
        var db = Receivables.Db(org ?? Org, DbName, interceptor);
        return (db, new ProductLedgerService(db, Variants, Clock));
    }

    /// <summary>A service on a fresh context, for a test that only wants to write.</summary>
    public ProductLedgerService Service(Guid? org = null) => Open(null, org).Service;

    /// <summary>Every entry of a variant, in the order they were written.</summary>
    public async Task<List<ProductLedgerEntry>> EntriesAsync(Guid variant, Guid? org = null)
    {
        await using var db = Receivables.Db(org ?? Org, DbName);
        return await db.ProductLedgerEntries.AsNoTracking().Where(e => e.VariantUuid == variant).OrderBy(e => e.SequenceNo).ToListAsync();
    }

    public static ProductLedgerPosting Buy(Guid variant, decimal qty, decimal cost, string number = "GRN-20260920-0001") =>
        new(variant, ProductLedgerEntryTypes.Purchase, ProductLedgerDirections.In, qty, cost,
            "GRN", Guid.NewGuid(), number, User);

    public static ProductLedgerPosting Sell(Guid variant, decimal qty, string number = "SINV-20260920-0001") =>
        new(variant, ProductLedgerEntryTypes.Sale, ProductLedgerDirections.Out, qty, null,
            "SalesInvoice", Guid.NewGuid(), number, User);
}

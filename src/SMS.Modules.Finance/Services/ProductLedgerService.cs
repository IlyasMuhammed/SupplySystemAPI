using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// The product ledger's writer, as Finance's own code sees it (A29 §11). It adds the one thing the
/// public contract cannot offer: tracking an entry on the caller's own context <b>without saving</b>,
/// so that a business action inside Finance — an invoice being issued — commits its status change and
/// its ledger entry in one <c>SaveChanges</c>, or neither (§17.1).
/// </summary>
internal interface IProductLedgerWriter : IProductLedgerService
{
    /// <summary>
    /// Builds the variant's next entry and tracks it, unsaved, on the shared context; its
    /// <see cref="ProductLedgerEntry.TotalCost"/> is what an OUT entry booked as cost of goods sold. The
    /// caller owns the save and the retry: on a failed save it must detach the entry it was handed, and
    /// ask again.
    /// </summary>
    Task<ProductLedgerEntry> TrackEntryAsync(ProductLedgerPosting posting);
}

/// <summary>
/// The per-variant product ledger (A29 §11) — append-only, with each entry's running quantity and running
/// value derived from the one before it at post time (see <see cref="WeightedAverageCosting"/>), and a
/// per-variant <c>SequenceNo</c> whose unique index is the concurrency guard: two writers racing for the
/// next entry both compute the same number, one loses on the index and retries against the fresh last row.
/// There is no update and no delete; a correction is a new, offsetting entry.
/// <para>
/// There are two ways to write, and which one a caller wants depends on what else must commit with it.
/// </para>
/// <list type="bullet">
/// <item><see cref="TrackEntryAsync"/> — the entry is one half of an action inside Finance. It adds the
/// entry to the caller's own context and does not save.</item>
/// <item><see cref="AppendEntryAsync"/> — the public contract. It saves, and retries a lost sequence race
/// itself. Given the caller's transaction it enlists its context in it first, the way
/// <see cref="MasterProductLedgerService"/> does, so a GRN approval in another module and its ledger entry
/// are one atomic write. As with the customer ledger, that save also commits anything else tracked on
/// the same context.</item>
/// </list>
/// </summary>
internal sealed class ProductLedgerService : IProductLedgerWriter
{
    private const int MaxAttempts = 5;

    private readonly FinanceDbContext        _db;
    private readonly IProductVariantResolver _variants;
    private readonly TimeProvider            _clock;

    public ProductLedgerService(FinanceDbContext db, IProductVariantResolver variants, TimeProvider? clock = null)
    {
        _db       = db;
        _variants = variants;
        _clock    = clock ?? TimeProvider.System;
    }

    // ── Write ────────────────────────────────────────────────────────────────

    public async Task<ProductLedgerEntry> TrackEntryAsync(ProductLedgerPosting posting)
    {
        ArgumentNullException.ThrowIfNull(posting);
        Validate(posting);

        var last    = await LastEntryAsync(posting.VariantUuid);
        var prevQty = last?.RunningQty ?? 0m;
        var prevVal = last?.RunningValue ?? 0m;

        // Checked before anything is looked up in Inventory: there is no cost to take goods out at
        // unless the ledger holds them.
        if (posting.Direction == ProductLedgerDirections.Out && posting.Quantity > prevQty)
            throw new ConflictException(
                $"Cannot take {posting.Quantity:0.####} out of variant {posting.VariantUuid}'s product ledger: " +
                $"it holds {prevQty:0.####}. An outgoing entry is costed at the weighted-average cost of what is " +
                "on the ledger, so the stock has to be there first.");

        var productUuid = await ResolveProductAsync(posting, last);

        var step = posting.Direction == ProductLedgerDirections.In
            ? WeightedAverageCosting.In(prevQty, prevVal, posting.Quantity, posting.UnitCost!.Value)
            : WeightedAverageCosting.Out(prevQty, prevVal, posting.Quantity);

        var now = _clock.GetUtcNow().UtcDateTime;

        var entry = new ProductLedgerEntry
        {
            UUID            = Guid.NewGuid(),
            VariantUuid     = posting.VariantUuid,
            ProductUuid     = productUuid,
            SequenceNo      = (last?.SequenceNo ?? 0) + 1,
            EntryDate       = posting.EntryDate ?? now,
            EntryType       = posting.EntryType,
            ReferenceType   = posting.ReferenceType,
            ReferenceId     = posting.ReferenceId,
            ReferenceNumber = posting.ReferenceNumber,
            PartnerId       = posting.PartnerId,
            Quantity        = posting.Quantity,
            UnitCost        = step.UnitCost,
            TotalCost       = step.TotalCost,
            Direction       = posting.Direction,
            RunningQty      = step.RunningQty,
            RunningValue    = step.RunningValue,
            Narration       = posting.Narration,
            CreatedBy       = posting.CreatedBy,
            CreatedDate     = now
        };

        _db.ProductLedgerEntries.Add(entry);
        return entry;
    }

    public async Task<ProductLedgerPosted> AppendEntryAsync(ProductLedgerPosting posting, DbTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(posting);

        if (transaction is not null)
            await EnlistAsync(transaction);

        for (var attempt = 1; ; attempt++)
        {
            // Rebuilt on every pass: the running totals and the sequence both come from the variant's
            // last entry, which is exactly what a lost race changed.
            var entry = await TrackEntryAsync(posting);

            try
            {
                await _db.SaveChangesAsync();
                return ToPosted(entry);
            }
            catch (DbUpdateException)
            {
                // Another writer committed this variant's next SequenceNo first. Detach only the
                // losing entry, so anything else the caller has tracked survives for the retry — and
                // so that, when the last attempt fails too, the entry is not left tracked for some
                // later save on this context to commit against totals that have moved on.
                _db.Entry(entry).State = EntityState.Detached;
                if (attempt >= MaxAttempts) throw;
            }
        }
    }

    /// <summary>
    /// Puts this context on the caller's connection and transaction. Same technique as
    /// <see cref="MasterProductLedgerService.PostMovementAsync"/>; not being the owner of the connection,
    /// this context never closes it. A caller that posts several entries in one transaction enlists once.
    /// </summary>
    private async Task EnlistAsync(DbTransaction transaction)
    {
        var current = _db.Database.CurrentTransaction?.GetDbTransaction();
        if (ReferenceEquals(current, transaction)) return;

        // Still attached to an earlier transaction of this request: let go of it before joining the next.
        if (current is not null) await _db.Database.UseTransactionAsync(null);

        _db.Database.SetDbConnection(transaction.Connection!, contextOwnsConnection: false);
        await _db.Database.UseTransactionAsync(transaction);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The variant's product: as the caller said, else as the variant's earlier entries say — a variant
    /// never changes product — else from Inventory, the first time only.
    /// </summary>
    private async Task<Guid> ResolveProductAsync(ProductLedgerPosting posting, ProductLedgerEntry? last)
    {
        if (posting.ProductUuid is { } given)
        {
            if (last is not null && last.ProductUuid != given)
                throw new ArgumentException(
                    $"Variant {posting.VariantUuid} is on the ledger under product {last.ProductUuid}, not {given}.",
                    nameof(posting));
            return given;
        }

        if (last is not null) return last.ProductUuid;

        var described = await _variants.DescribeVariantsAsync([posting.VariantUuid]);
        return described.TryGetValue(posting.VariantUuid, out var variant)
            ? variant.ProductUuid
            : throw new NotFoundException("ProductVariant", posting.VariantUuid);
    }

    /// <summary>
    /// The variant's latest entry — including one this context has already tracked but not saved, so
    /// two entries built for the same variant in one unit of work chain rather than collide.
    /// </summary>
    private async Task<ProductLedgerEntry?> LastEntryAsync(Guid variantUuid)
    {
        var saved = await _db.ProductLedgerEntries
            .Where(e => e.VariantUuid == variantUuid)
            .OrderByDescending(e => e.SequenceNo)
            .FirstOrDefaultAsync();

        var pending = _db.ChangeTracker.Entries<ProductLedgerEntry>()
            .Where(e => e.State == EntityState.Added && e.Entity.VariantUuid == variantUuid)
            .Select(e => e.Entity)
            .OrderByDescending(e => e.SequenceNo)
            .FirstOrDefault();

        return pending is not null && pending.SequenceNo > (saved?.SequenceNo ?? 0) ? pending : saved;
    }

    private static void Validate(ProductLedgerPosting p)
    {
        if (p.VariantUuid == Guid.Empty)
            throw new ArgumentException("A product ledger entry belongs to a variant.", nameof(p));

        if (!ProductLedgerEntryTypes.All.Contains(p.EntryType))
            throw new ArgumentException(
                $"'{p.EntryType}' is not a product ledger entry type. Valid: {string.Join(", ", ProductLedgerEntryTypes.All)}.", nameof(p));

        if (!ProductLedgerDirections.All.Contains(p.Direction))
            throw new ArgumentException(
                $"'{p.Direction}' is not a direction. Valid: {string.Join(", ", ProductLedgerDirections.All)}.", nameof(p));

        // §11.2 fixes which way each kind of entry goes; only an adjustment can go either.
        var fixedDirection = p.EntryType switch
        {
            ProductLedgerEntryTypes.Purchase or ProductLedgerEntryTypes.ReturnIn => ProductLedgerDirections.In,
            ProductLedgerEntryTypes.Sale or ProductLedgerEntryTypes.ReturnOut or ProductLedgerEntryTypes.WriteOff => ProductLedgerDirections.Out,
            _ => null
        };
        if (fixedDirection is not null && p.Direction != fixedDirection)
            throw new ArgumentException($"A {p.EntryType} entry is always {fixedDirection}, not {p.Direction}.", nameof(p));

        if (p.Quantity <= 0m)
            throw new ArgumentException("A product ledger quantity is always above zero; the direction says which way it moved.", nameof(p));
        if (decimal.Round(p.Quantity, 4) != p.Quantity)
            throw new ArgumentException("A product ledger quantity has more than four decimal places.", nameof(p));

        if (p.Direction == ProductLedgerDirections.In)
        {
            if (p.UnitCost is not { } cost)
                throw new ArgumentException("An incoming entry needs the unit cost the goods came in at.", nameof(p));
            if (cost < 0m)
                throw new ArgumentException("A unit cost is never negative.", nameof(p));
            if (decimal.Round(cost, 4) != cost)
                throw new ArgumentException("A unit cost has more than four decimal places.", nameof(p));
        }
        else if (p.UnitCost is not null)
        {
            throw new ArgumentException(
                "An outgoing entry is costed at the variant's weighted-average cost; it does not take a unit cost.", nameof(p));
        }

        if (p.ProductUuid == Guid.Empty) throw new ArgumentException("The product, when given, is not empty.", nameof(p));
        if (p.PartnerId == Guid.Empty)   throw new ArgumentException("The partner, when given, is not empty.", nameof(p));
        if (p.ReferenceId == Guid.Empty) throw new ArgumentException("A product ledger entry needs the id of the document behind it.", nameof(p));

        Required(p.ReferenceType,   30, "reference type");
        Required(p.ReferenceNumber, 50, "reference number");

        if (p.Narration is { Length: > 500 })
            throw new ArgumentException("A product ledger narration is longer than 500 characters.", nameof(p));
    }

    private static void Required(string? value, int max, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"A product ledger entry needs a {what}.");
        if (value.Length > max)
            throw new ArgumentException($"The product ledger entry's {what} is longer than {max} characters.");
    }

    private static ProductLedgerPosted ToPosted(ProductLedgerEntry e) => new(
        e.UUID, e.SequenceNo, e.VariantUuid, e.ProductUuid, e.Direction, e.Quantity, e.UnitCost, e.TotalCost,
        e.RunningQty, e.RunningValue, WeightedAverageCosting.Wac(e.RunningQty, e.RunningValue));
}

using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Repositories;

internal interface IPackageRepository
{
    Task<Guid> PackAsync(Guid deliveryUuid, PackRequest req, int userId);
    Task<PackageModel?> GetByUuidAsync(Guid uuid);
    Task<DeliveryPackingModel?> GetForDeliveryAsync(Guid deliveryUuid);
    Task<bool> PatchAsync(Guid uuid, PatchPackageRequest req, int userId);
    Task<bool> VoidAsync(Guid uuid, DeliveryReasonRequest req, int userId);
}

/// <summary>
/// Layer B — putting picked goods into cartons.
/// <para>
/// A package is a <b>handling unit</b>: the thing a label is stuck to, a courier scans, and
/// dimensional weight is computed from. Its barcode is the identity of a physical box, which is
/// why a package is never deleted once created — only voided. The label may already be on a carton
/// on a dock, and reissuing that number would make two boxes indistinguishable.
/// </para>
/// <para>
/// The rule that ties this layer to the one before it: <b>you can only pack what was picked.</b>
/// Packing more would put units in a box that no reservation was consumed for, and the goods issue
/// that follows would go out of step with the stock ledger.
/// </para>
/// </summary>
internal sealed class PackageRepository : IPackageRepository
{
    private readonly LogisticsDbContext       _db;
    private readonly IDocumentNumberGenerator _numbers;

    public PackageRepository(LogisticsDbContext db, IDocumentNumberGenerator numbers)
    {
        _db      = db;
        _numbers = numbers;
    }

    // ── Pack ──────────────────────────────────────────────────────────────────

    public async Task<Guid> PackAsync(Guid deliveryUuid, PackRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var delivery = await LoadForPackingAsync(deliveryUuid)
            ?? throw new NotFoundException("Delivery not found.");

        EnsurePackable(delivery);

        if (req.Contents.Count == 0)
            throw new BadRequestException(
                "A package must contain something. An empty carton has nothing to label, nothing " +
                "to rate and nothing to receive.");

        var duplicate = req.Contents.GroupBy(c => c.DeliveryLineUuid).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new BadRequestException(
                $"Delivery line {duplicate.Key} appears more than once in the same package. " +
                "Combine the quantities into one entry.");

        var now = DateTime.UtcNow;

        var package = new ShipmentPackage
        {
            UUID            = Guid.NewGuid(),
            OrganizationId  = delivery.OrganizationId,
            DeliveryOrderId = delivery.Id,
            PackageBarcode  = await ResolveBarcodeAsync(req.PackageBarcode, delivery.OrganizationId, now),
            PackageType     = ParsePackageType(req.PackageType),
            LengthCm        = Positive(req.LengthCm,      "Length"),
            WidthCm         = Positive(req.WidthCm,       "Width"),
            HeightCm        = Positive(req.HeightCm,      "Height"),
            GrossWeightKg   = Positive(req.GrossWeightKg, "Gross weight"),
            NetWeightKg     = Positive(req.NetWeightKg,   "Net weight"),
            DeclaredValue   = Positive(req.DeclaredValue, "Declared value"),
            SealNumber      = Trim(req.SealNumber),
            ParentPackageId = await ResolveParentAsync(req.ParentPackageUuid, delivery),
            CreatedBy       = userId,
            CreatedDate     = now
        };

        EnsureWeightsAgree(package);

        foreach (var content in req.Contents)
            package.Contents.Add(await BuildContentAsync(delivery, content, userId, now));

        _db.ShipmentPackages.Add(package);

        // Roll the new contents onto the delivery lines, and close the delivery out to PACKED if
        // this carton was the last one needed.
        RollUp(delivery, extra: package);
        Complete(delivery, userId, now);

        await _db.SaveChangesAsync();
        return package.UUID;
    }

    /// <summary>
    /// A package's contents, validated against what the delivery line actually has spare.
    /// </summary>
    private async Task<PackageContent> BuildContentAsync(
        DeliveryOrder delivery, PackContentRequest req, int userId, DateTime now)
    {
        var line = delivery.Lines.FirstOrDefault(l => l.UUID == req.DeliveryLineUuid)
            ?? throw new BadRequestException(
                $"Line {req.DeliveryLineUuid} does not belong to delivery {delivery.DeliveryNumber}.");

        if (req.Qty <= 0)
            throw new BadRequestException(
                $"Line {line.LineNo} ({line.ItemDescription}): a packed quantity must be more than zero.");

        // Only what was picked can be packed, less whatever is already in other cartons. Packing
        // more would box units no reservation was consumed for, and the goods issue that follows
        // would disagree with the stock ledger.
        var alreadyPacked = line.PackageContents
            .Where(c => !c.ShipmentPackage.IsVoided && !c.ShipmentPackage.IsDelete)
            .Sum(c => c.Qty);

        var spare = line.QtyPicked - alreadyPacked;

        if (req.Qty > spare)
            throw new ConflictException(
                $"Line {line.LineNo} ({line.ItemDescription}): {req.Qty:0.###} to pack but only " +
                $"{spare:0.###} is picked and unpacked ({line.QtyPicked:0.###} picked, " +
                $"{alreadyPacked:0.###} already in a carton).");

        return new PackageContent
        {
            UUID                = Guid.NewGuid(),
            OrganizationId      = delivery.OrganizationId,
            DeliveryOrderLineId = line.Id,
            Qty                 = req.Qty,
            BatchNumber         = Trim(req.BatchNumber)  ?? await InheritedBatchAsync(line),
            SerialNumber        = Trim(req.SerialNumber),
            CreatedBy           = userId,
            CreatedDate         = now
        };
    }

    /// <summary>
    /// The batch this line was picked from, when there is exactly one.
    /// <para>
    /// A packing list has to print batch numbers, and the picker already recorded them (T-24/T-25).
    /// Asking the packer to retype what the system knows invites a typo on a regulated field.
    /// Inherited <b>only</b> when the pick was unambiguous — a line picked from three batches and
    /// split across cartons is a question only the packer can answer, so it is left for them.
    /// </para>
    /// </summary>
    private async Task<string?> InheritedBatchAsync(DeliveryOrderLine line)
    {
        var batches = await _db.PickListLines
            .Where(l => l.DeliveryOrderLineId == line.Id && l.QtyPicked > 0)
            .Where(l => l.BatchNumber != null)
            .Select(l => l.BatchNumber!)
            .Distinct()
            .Take(2)
            .ToListAsync();

        return batches.Count == 1 ? batches[0] : null;
    }

    private async Task<string> ResolveBarcodeAsync(string? supplied, Guid organizationId, DateTime now)
    {
        var barcode = Trim(supplied);

        if (barcode is null)
            return await _numbers.NextAsync(DocumentNumberPrefix.HandlingUnit, now, organizationId);

        if (barcode.Length > 50)
            throw new BadRequestException("A package barcode cannot be longer than 50 characters.");

        // Checked here rather than left to the unique index so the caller gets an explanation
        // instead of a constraint violation — and because a voided package still owns its barcode.
        var taken = await _db.ShipmentPackages.AnyAsync(p => p.PackageBarcode == barcode);

        if (taken)
            throw new ConflictException(
                $"Barcode '{barcode}' is already on another package. Handling-unit barcodes " +
                "identify a physical carton, so two cannot share one — voided packages keep " +
                "theirs for the same reason.");

        return barcode;
    }

    /// <summary>
    /// Validates the pallet a carton is being loaded onto.
    /// <para>
    /// <b>One level of nesting only</b> — cartons on a pallet. Deeper nesting is what a courier
    /// integration never needs and is also how cycles get in: refusing a parent that is itself on
    /// something makes a cycle impossible by construction rather than by a graph walk that has to
    /// be right every time.
    /// </para>
    /// </summary>
    private async Task<int?> ResolveParentAsync(Guid? parentUuid, DeliveryOrder delivery)
    {
        if (parentUuid is not { } uuid) return null;

        var parent = await _db.ShipmentPackages
            .FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete)
            ?? throw new BadRequestException("The package it is being loaded onto does not exist.");

        if (parent.DeliveryOrderId != delivery.Id)
            throw new ConflictException(
                $"Package '{parent.PackageBarcode}' belongs to another delivery. A carton and the " +
                "pallet under it have to travel together.");

        if (parent.IsVoided)
            throw new ConflictException(
                $"Package '{parent.PackageBarcode}' is voided and cannot carry anything.");

        if (parent.ParentPackageId is not null)
            throw new ConflictException(
                $"Package '{parent.PackageBarcode}' is already loaded onto something else. " +
                "Nesting goes one level deep: cartons onto a pallet, not pallets onto pallets.");

        return parent.Id;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<PackageModel?> GetByUuidAsync(Guid uuid)
    {
        var package = await PackageQuery().FirstOrDefaultAsync(p => p.UUID == uuid);
        return package is null ? null : ToModel(package);
    }

    public async Task<DeliveryPackingModel?> GetForDeliveryAsync(Guid deliveryUuid)
    {
        var delivery = await _db.DeliveryOrders
            .Include(d => d.Lines)
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.UUID == deliveryUuid && !d.IsDelete);

        if (delivery is null) return null;

        var packages = await PackageQuery()
            .Where(p => p.DeliveryOrderId == delivery.Id)
            .OrderBy(p => p.Id)
            .ToListAsync();

        var live = packages.Where(p => !p.IsVoided).ToList();

        var model = new DeliveryPackingModel
        {
            DeliveryUuid       = delivery.UUID,
            DeliveryNumber     = delivery.DeliveryNumber,
            Status             = delivery.Status,
            TotalGrossWeightKg = live.Sum(p => p.GrossWeightKg ?? 0m),
            Packages           = [.. packages.Select(ToModel)]
        };

        foreach (var line in delivery.Lines.OrderBy(l => l.LineNo))
        {
            var packed = live
                .SelectMany(p => p.Contents)
                .Where(c => c.DeliveryOrderLineId == line.Id)
                .Sum(c => c.Qty);

            model.Lines.Add(new PackingLineModel
            {
                DeliveryLineUuid = line.UUID,
                LineNo           = line.LineNo,
                VariantUuid      = line.VariantUuid,
                ItemDescription  = line.ItemDescription,
                UnitOfMeasure    = line.UnitOfMeasure,
                QtyPicked        = line.QtyPicked,
                QtyPacked        = packed,
                QtyToPack        = Math.Max(0m, line.QtyPicked - packed)
            });
        }

        model.QtyPicked   = model.Lines.Sum(l => l.QtyPicked);
        model.QtyPacked   = model.Lines.Sum(l => l.QtyPacked);
        model.QtyUnpacked = model.Lines.Sum(l => l.QtyToPack);

        // Nothing picked is not "fully packed" — it is a delivery that has not been picked.
        model.IsFullyPacked = model.QtyPicked > 0 && model.QtyUnpacked == 0;

        return model;
    }

    private IQueryable<ShipmentPackage> PackageQuery() =>
        _db.ShipmentPackages
            .Include(p => p.DeliveryOrder)
            .Include(p => p.ParentPackage)
            .Include(p => p.ChildPackages)
            .Include(p => p.Contents).ThenInclude(c => c.DeliveryOrderLine)
            .AsNoTracking()
            .Where(p => !p.IsDelete);

    private static PackageModel ToModel(ShipmentPackage p) => new()
    {
        UUID                 = p.UUID,
        PackageBarcode       = p.PackageBarcode,
        PackageType          = p.PackageType,
        DeliveryUuid         = p.DeliveryOrder.UUID,
        DeliveryNumber       = p.DeliveryOrder.DeliveryNumber,
        LengthCm             = p.LengthCm,
        WidthCm              = p.WidthCm,
        HeightCm             = p.HeightCm,
        GrossWeightKg        = p.GrossWeightKg,
        NetWeightKg          = p.NetWeightKg,
        DimWeightKg          = p.DimWeightKg,
        VolumeM3             = Volume(p),
        DeclaredValue        = p.DeclaredValue,
        SealNumber           = p.SealNumber,
        ParentPackageUuid    = p.ParentPackage?.UUID,
        ParentPackageBarcode = p.ParentPackage?.PackageBarcode,
        ChildPackageBarcodes = [.. p.ChildPackages.Where(c => !c.IsDelete)
                                                  .OrderBy(c => c.Id)
                                                  .Select(c => c.PackageBarcode)],
        IsVoided             = p.IsVoided,
        VoidReason           = p.VoidReason,
        CreatedDate          = p.CreatedDate,
        Contents             = [.. p.Contents.OrderBy(c => c.DeliveryOrderLine.LineNo).Select(c => new PackageContentModel
        {
            UUID             = c.UUID,
            DeliveryLineUuid = c.DeliveryOrderLine.UUID,
            DeliveryLineNo   = c.DeliveryOrderLine.LineNo,
            VariantUuid      = c.DeliveryOrderLine.VariantUuid,
            ItemDescription  = c.DeliveryOrderLine.ItemDescription,
            UnitOfMeasure    = c.DeliveryOrderLine.UnitOfMeasure,
            Qty              = c.Qty,
            BatchNumber      = c.BatchNumber,
            SerialNumber     = c.SerialNumber
        })]
    };

    /// <summary>Cubic metres, from centimetres. Null unless all three dimensions are known.</summary>
    private static decimal? Volume(ShipmentPackage p) =>
        p.LengthCm is { } l && p.WidthCm is { } w && p.HeightCm is { } h
            ? decimal.Round(l * w * h / 1_000_000m, 6)
            : null;

    // ── Update ────────────────────────────────────────────────────────────────

    public async Task<bool> PatchAsync(Guid uuid, PatchPackageRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var package = await _db.ShipmentPackages
            .Include(p => p.DeliveryOrder)
            .FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete);

        if (package is null) return false;

        EnsureAmendable(package);

        if (req.PackageType is not null) package.PackageType = ParsePackageType(req.PackageType);

        if (req.LengthCm      is not null) package.LengthCm      = Positive(req.LengthCm,      "Length");
        if (req.WidthCm       is not null) package.WidthCm       = Positive(req.WidthCm,       "Width");
        if (req.HeightCm      is not null) package.HeightCm      = Positive(req.HeightCm,      "Height");
        if (req.GrossWeightKg is not null) package.GrossWeightKg = Positive(req.GrossWeightKg, "Gross weight");
        if (req.NetWeightKg   is not null) package.NetWeightKg   = Positive(req.NetWeightKg,   "Net weight");
        if (req.DeclaredValue is not null) package.DeclaredValue = Positive(req.DeclaredValue, "Declared value");
        if (req.SealNumber    is not null) package.SealNumber    = Trim(req.SealNumber);

        EnsureWeightsAgree(package);

        if (req.ClearParent)
        {
            package.ParentPackageId = null;
        }
        else if (req.ParentPackageUuid is not null)
        {
            if (req.ParentPackageUuid == package.UUID)
                throw new ConflictException("A package cannot be loaded onto itself.");

            if (package.ChildPackages.Count > 0 || await HasChildrenAsync(package.Id))
                throw new ConflictException(
                    $"Package '{package.PackageBarcode}' already carries other packages, so it " +
                    "cannot be loaded onto one. Nesting goes one level deep.");

            package.ParentPackageId = await ResolveParentAsync(req.ParentPackageUuid, package.DeliveryOrder);
        }

        package.ModifiedBy   = userId;
        package.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    private Task<bool> HasChildrenAsync(int packageId) =>
        _db.ShipmentPackages.AnyAsync(p => p.ParentPackageId == packageId && !p.IsDelete);

    /// <summary>
    /// Takes a carton back out of the picture without destroying its identity.
    /// <para>
    /// The row stays. Its barcode may already be on a label stuck to a real box, and reissuing it
    /// would make two cartons indistinguishable on a dock — which is the failure this whole layer
    /// exists to prevent.
    /// </para>
    /// </summary>
    public async Task<bool> VoidAsync(Guid uuid, DeliveryReasonRequest req, int userId)
    {
        if (string.IsNullOrWhiteSpace(req?.Reason))
            throw new BadRequestException("A reason is required to void a package.");

        // DeliveryOrder is included because EnsureAmendable reads its status. Without it the
        // navigation is only ever populated by change-tracker fixup from some earlier load in the
        // same request — which holds in a test that packed first, and does not hold for a request
        // that opens by voiding.
        var package = await _db.ShipmentPackages
            .Include(p => p.DeliveryOrder)
            .Include(p => p.ChildPackages)
            .FirstOrDefaultAsync(p => p.UUID == uuid && !p.IsDelete);

        if (package is null) return false;

        if (package.IsVoided)
            throw new ConflictException($"Package '{package.PackageBarcode}' is already voided.");

        EnsureAmendable(package);

        var children = package.ChildPackages.Where(c => !c.IsDelete && !c.IsVoided).ToList();
        if (children.Count > 0)
            throw new ConflictException(
                $"Package '{package.PackageBarcode}' still carries {children.Count} package(s). " +
                "Take them off it first — they physically exist and need somewhere to go.");

        var delivery = await LoadForPackingAsync(package.DeliveryOrderId)
            ?? throw new ConflictException("The delivery this package belongs to no longer exists.");

        var now = DateTime.UtcNow;

        package.IsVoided     = true;
        package.VoidReason   = req.Reason.Trim();
        package.ModifiedBy   = userId;
        package.ModifiedDate = now;

        // Its contents go back to being unpacked, and the delivery steps back from PACKED if this
        // was what completed it.
        RollUp(delivery, voided: package.Id);
        Reopen(delivery, userId, now);

        await _db.SaveChangesAsync();
        return true;
    }

    // ── Shared rules ──────────────────────────────────────────────────────────

    private Task<DeliveryOrder?> LoadForPackingAsync(Guid uuid) =>
        PackingQuery().FirstOrDefaultAsync(d => d.UUID == uuid && !d.IsDelete);

    private Task<DeliveryOrder?> LoadForPackingAsync(int id) =>
        PackingQuery().FirstOrDefaultAsync(d => d.Id == id && !d.IsDelete);

    private IQueryable<DeliveryOrder> PackingQuery() =>
        _db.DeliveryOrders
            .Include(d => d.Lines).ThenInclude(l => l.PackageContents).ThenInclude(c => c.ShipmentPackage);

    /// <summary>
    /// Packing starts once goods are off the shelf and stops once they have left the building.
    /// </summary>
    private static void EnsurePackable(DeliveryOrder delivery)
    {
        var status = LogisticsCode.Parse<DeliveryStatus>(delivery.Status);

        // PACKED is included so a carton can still be added or replaced after the delivery has
        // completed packing — repacking before dispatch is routine.
        if (status is not (DeliveryStatus.Picked or DeliveryStatus.Packed))
            throw new ConflictException(
                $"A delivery in {delivery.Status} cannot be packed. Pack once it is PICKED — " +
                "there is nothing in front of the packer before that.");
    }

    private static void EnsureAmendable(ShipmentPackage package)
    {
        var status = LogisticsCode.Parse<DeliveryStatus>(package.DeliveryOrder.Status);

        // Once staged or issued the carton is on a vehicle or off the books. Changing its weight
        // or contents then would rewrite what was already declared to a carrier.
        if (status is not (DeliveryStatus.Picked or DeliveryStatus.Packed))
            throw new ConflictException(
                $"Delivery {package.DeliveryOrder.DeliveryNumber} is {package.DeliveryOrder.Status}, " +
                "so its packages can no longer be changed.");
    }

    /// <summary>
    /// Recomputes every line's packed quantity from the cartons that actually exist.
    /// <para>
    /// Recomputed rather than incremented: an increment is a second source of truth that drifts
    /// the first time a void, a retry or a concurrent pack lands out of order.
    /// </para>
    /// </summary>
    private static void RollUp(DeliveryOrder delivery, ShipmentPackage? extra = null, int? voided = null)
    {
        foreach (var line in delivery.Lines)
        {
            // Concatenated then de-duplicated by reference, because adding a package to the
            // context makes EF fix up the line's navigation to include its contents — so the new
            // rows may already be in here. Counting them from both sources doubled every
            // just-packed quantity, which is exactly the kind of bug an incrementing counter
            // would have hidden.
            var contents = extra is null
                ? line.PackageContents.AsEnumerable()
                : line.PackageContents.Concat(
                      extra.Contents.Where(c => c.DeliveryOrderLineId == line.Id));

            line.QtyPacked = contents
                .Distinct()
                .Where(c => IsLive(c.ShipmentPackage) && c.ShipmentPackageId != voided)
                .Sum(c => c.Qty);
        }
    }

    /// <summary>
    /// Whether a content row still counts. A package not yet saved has no navigation loaded, and
    /// it is by definition live.
    /// </summary>
    private static bool IsLive(ShipmentPackage? package) =>
        package is null || (!package.IsDelete && !package.IsVoided);

    private static void Complete(DeliveryOrder delivery, int userId, DateTime now)
    {
        if (LogisticsCode.Parse<DeliveryStatus>(delivery.Status) != DeliveryStatus.Picked) return;

        // Everything that came off the shelf is in a box. Under-packing leaves the delivery in
        // PICKED, from where it can be short-closed — packing less than was picked is a shortfall,
        // not a finished job, and short-close is the document that says so.
        if (delivery.Lines.Any(l => l.QtyPacked < l.QtyPicked)) return;
        if (delivery.Lines.All(l => l.QtyPacked == 0)) return;

        DeliveryStateMachine.Instance.EnsureCanTransition(DeliveryStatus.Picked, DeliveryStatus.Packed);

        delivery.Status       = LogisticsCode.Of(DeliveryStatus.Packed);
        delivery.ModifiedBy   = userId;
        delivery.ModifiedDate = now;
    }

    private static void Reopen(DeliveryOrder delivery, int userId, DateTime now)
    {
        if (LogisticsCode.Parse<DeliveryStatus>(delivery.Status) != DeliveryStatus.Packed) return;
        if (delivery.Lines.All(l => l.QtyPacked >= l.QtyPicked)) return;

        delivery.Status       = LogisticsCode.Of(DeliveryStatus.Picked);
        delivery.ModifiedBy   = userId;
        delivery.ModifiedDate = now;
    }

    private static void EnsureWeightsAgree(ShipmentPackage package)
    {
        if (package.NetWeightKg is { } net && package.GrossWeightKg is { } gross && net > gross)
            throw new BadRequestException(
                $"Net weight ({net:0.###} kg) cannot exceed gross weight ({gross:0.###} kg) — " +
                "gross includes the packaging.");
    }

    private static string ParsePackageType(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return LogisticsCode.Of(PackageType.Box);

        return LogisticsCode.TryParse<PackageType>(code, out var type)
            ? LogisticsCode.Of(type)
            : throw new BadRequestException(
                $"'{code}' is not a valid package type. Valid values: " +
                $"{string.Join(", ", LogisticsCode.Codes<PackageType>())}.");
    }

    private static decimal? Positive(decimal? value, string what)
    {
        if (value is null) return null;

        return value > 0
            ? value
            : throw new BadRequestException($"{what} must be greater than zero when it is given.");
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

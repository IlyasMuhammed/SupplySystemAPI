using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Demand.Data;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Material.Tests;

/// <summary>
/// An MIR line can only be for a variant whose "Available For MIR/MIV" checkbox is checked — the
/// write-side enforcement behind the picker's own MIR_MIV channel filter (mir-create and
/// mir-detail's "add a line" both use it), since the picker is only ever a suggestion until the
/// server refuses anything that slips past it. MIV lines are never picked independently — a
/// voucher only ever consumes what an already-checked MIR line reserved — so this one check covers
/// both halves of "MIR/MIV" as the checkbox groups them.
/// </summary>
public class MirVariantAvailabilityTests
{
    private static MirRepository NewRepo(out InventoryDbContext inventory)
    {
        var tenant = new StaticTenantContext();
        var material = new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options, tenant);
        var demand = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        return new MirRepository(material, inventory, demand, NullLogger<MirRepository>.Instance, new StockReservationService(inventory));
    }

    private static Guid SeedVariant(InventoryDbContext db, string name, bool availableForMirMiv)
    {
        var variantUuid = Guid.NewGuid();
        var sku = $"SKU-{variantUuid:N}"[..10];
        var product = new Product { Uuid = Guid.NewGuid(), Sku = sku, Name = name, UomCode = "PC", IsActive = true };
        product.Variants.Add(new ProductVariant
        {
            Uuid = variantUuid, Sku = $"{sku}-DEFAULT", VariantName = name, IsDefault = true, IsActive = true,
            PurchasePrice = 1000m, CreatedDate = DateTime.UtcNow, IsAvailableForMirMiv = availableForMirMiv
        });
        db.Products.Add(product);
        db.SaveChanges();
        return variantUuid;
    }

    private static CreateMirRequest DeptRequest(params CreateMirLineRequest[] lines) => new()
    {
        RequestType = "DEPARTMENT", Department = "IT", Priority = "MEDIUM", Lines = [.. lines]
    };

    private static CreateMirLineRequest Line(Guid variantUuid, decimal qty = 2m) =>
        new() { VariantUuid = variantUuid, RequestedQty = qty };

    [Fact]
    public async Task ALineForAMirMivEligibleVariant_IsAccepted()
    {
        var repo = NewRepo(out var inventory);
        var variant = SeedVariant(inventory, "Cable Reel", availableForMirMiv: true);

        var uuid = await repo.CreateAsync(DeptRequest(Line(variant)), createdBy: 1);

        uuid.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task ALineForAVariantNotCheckedForMirMiv_IsRefused_NamingTheVariant()
    {
        var repo = NewRepo(out var inventory);
        var variant = SeedVariant(inventory, "Showroom Sofa", availableForMirMiv: false);

        var act = () => repo.CreateAsync(DeptRequest(Line(variant)), createdBy: 1);

        var thrown = await act.Should().ThrowAsync<BadRequestException>();
        thrown.Which.Message.Should().Contain("Showroom Sofa").And.Contain("not available for MIR/MIV");
    }

    [Fact]
    public async Task OneIneligibleLineAmongSeveral_RefusesTheWholeRequest()
    {
        var repo = NewRepo(out var inventory);
        var eligible = SeedVariant(inventory, "Cable Reel", availableForMirMiv: true);
        var ineligible = SeedVariant(inventory, "Showroom Sofa", availableForMirMiv: false);

        var act = () => repo.CreateAsync(DeptRequest(Line(eligible), Line(ineligible)), createdBy: 1);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task TheSameCheckAppliesWhenAddingALineToAnExistingDraftMir()
    {
        var repo = NewRepo(out var inventory);
        var eligible = SeedVariant(inventory, "Cable Reel", availableForMirMiv: true);
        var uuid = await repo.CreateAsync(DeptRequest(Line(eligible)), createdBy: 1);
        var ineligible = SeedVariant(inventory, "Showroom Sofa", availableForMirMiv: false);

        var act = () => repo.PatchAsync(uuid, new PatchMirRequest { Lines = [Line(eligible), Line(ineligible)] }, modifiedBy: 1);

        await act.Should().ThrowAsync<BadRequestException>();
    }
}

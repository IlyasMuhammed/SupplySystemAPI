using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Material.Domain;

namespace SMS.Modules.Material.Data.Maps;

internal sealed class ProductionOrderMap : IEntityTypeConfiguration<ProductionOrder>
{
    public void Configure(EntityTypeBuilder<ProductionOrder> b)
    {
        b.ToTable("production_orders");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.TraceId).IsRequired();
        b.HasIndex(x => x.TraceId);
        b.Property(x => x.ProductionNumber).HasMaxLength(50).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.ProductionNumber }).IsUnique();
        b.Property(x => x.PlannedQuantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.ProducedQuantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.AcceptedQuantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.RejectedQuantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.ScrappedQuantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.SourceType).HasMaxLength(30).IsRequired();
        b.Property(x => x.SourceReference).HasMaxLength(50);
        b.Property(x => x.Status).HasMaxLength(30).IsRequired();
        b.Property(x => x.MaterialReadiness).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        // §27.3 IX_ProdOrder_Status and IX_ProdOrder_Source.
        b.HasIndex(x => new { x.OrganizationId, x.Status, x.RequiredDate });
        b.HasIndex(x => new { x.OrganizationId, x.SourceType, x.SourceUuid });
        b.HasIndex(x => new { x.OrganizationId, x.ProductUuid });

        b.HasOne(x => x.Bom).WithMany()
         .HasForeignKey(x => x.BomId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Parent).WithMany()
         .HasForeignKey(x => x.ParentProductionOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Materials).WithOne(x => x.ProductionOrder)
         .HasForeignKey(x => x.ProductionOrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ProductionMaterialRequirementMap : IEntityTypeConfiguration<ProductionMaterialRequirement>
{
    public void Configure(EntityTypeBuilder<ProductionMaterialRequirement> b)
    {
        b.ToTable("production_material_requirements");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        foreach (var quantity in new[]
        {
            nameof(ProductionMaterialRequirement.NetQuantity), nameof(ProductionMaterialRequirement.ScrapAllowance),
            nameof(ProductionMaterialRequirement.RequiredQuantity), nameof(ProductionMaterialRequirement.ReservedQuantity),
            nameof(ProductionMaterialRequirement.PlannedQuantity), nameof(ProductionMaterialRequirement.IssuedQuantity),
            nameof(ProductionMaterialRequirement.ReturnedQuantity), nameof(ProductionMaterialRequirement.WastageQuantity),
            nameof(ProductionMaterialRequirement.ConsumedQuantity), nameof(ProductionMaterialRequirement.ShortageQuantity)
        })
            b.Property(quantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.Uom).HasMaxLength(20).IsRequired();
        b.Property(x => x.Status).HasMaxLength(30).IsRequired();
        b.Property(x => x.OrganizationId).IsRequired();
        b.Ignore(x => x.Outstanding);
        b.HasIndex(x => x.OrganizationId);
        // §27.3 IX_PMR_ProdOrder and IX_PMR_Shortage.
        b.HasIndex(x => new { x.ProductionOrderId, x.Status });
        b.HasIndex(x => new { x.OrganizationId, x.MaterialVariantUuid, x.WarehouseUuid, x.ShortageQuantity });
        b.HasIndex(x => x.AllocationDemandUuid);
    }
}

internal sealed class SupplyRequirementMap : IEntityTypeConfiguration<SupplyRequirement>
{
    public void Configure(EntityTypeBuilder<SupplyRequirement> b)
    {
        b.ToTable("supply_requirements");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.TraceId).IsRequired();
        b.HasIndex(x => x.TraceId);
        b.Property(x => x.SupplyNumber).HasMaxLength(50).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.SupplyNumber }).IsUnique();
        b.Property(x => x.QuantityRequired).HasColumnType("decimal(18,4)");
        b.Property(x => x.QuantityOrdered).HasColumnType("decimal(18,4)");
        b.Property(x => x.QuantityReceived).HasColumnType("decimal(18,4)");
        b.Property(x => x.DemandSourceType).HasMaxLength(30).IsRequired();
        b.Property(x => x.DemandReference).HasMaxLength(50);
        b.Property(x => x.SupplyMethod).HasMaxLength(20).IsRequired();
        b.Property(x => x.SupplySourceType).HasMaxLength(30);
        b.Property(x => x.SupplySourceReference).HasMaxLength(50);
        b.Property(x => x.Status).HasMaxLength(30).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Property(x => x.OrganizationId).IsRequired();
        b.Ignore(x => x.QuantityOutstanding);
        b.HasIndex(x => x.OrganizationId);
        // §27.3 IX_SR_Product_Status, plus what the readiness listener looks up by.
        b.HasIndex(x => new { x.OrganizationId, x.VariantUuid, x.Status });
        b.HasIndex(x => new { x.OrganizationId, x.DemandSourceType, x.DemandSourceUuid });
        b.HasIndex(x => x.SupplySourceLineUuid);
    }
}

internal sealed class PurchaseRequiredAcknowledgementMap : IEntityTypeConfiguration<PurchaseRequiredAcknowledgement>
{
    public void Configure(EntityTypeBuilder<PurchaseRequiredAcknowledgement> b)
    {
        b.ToTable("purchase_required_acknowledgements");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Uuid).IsRequired();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.OrganizationId).IsRequired();
        // One live acknowledgement per variant — a second "handled manually" click updates it, not duplicates it.
        b.HasIndex(x => new { x.OrganizationId, x.VariantUuid }).IsUnique();
    }
}

internal sealed class ProductionMaterialIssueMap : IEntityTypeConfiguration<ProductionMaterialIssue>
{
    public void Configure(EntityTypeBuilder<ProductionMaterialIssue> b)
    {
        b.ToTable("production_material_issues");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.IssueNumber).HasMaxLength(50).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.IssueNumber }).IsUnique();
        b.Property(x => x.IssueType).HasMaxLength(20).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasIndex(x => new { x.ProductionOrderId, x.Status });

        b.HasOne(x => x.ProductionOrder).WithMany()
         .HasForeignKey(x => x.ProductionOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne(x => x.Issue)
         .HasForeignKey(x => x.IssueId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ProductionMaterialIssueLineMap : IEntityTypeConfiguration<ProductionMaterialIssueLine>
{
    public void Configure(EntityTypeBuilder<ProductionMaterialIssueLine> b)
    {
        b.ToTable("production_material_issue_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.UnitCost).HasColumnType("decimal(18,4)");
        b.Property(x => x.Uom).HasMaxLength(20).IsRequired();
        b.Property(x => x.BatchNumber).HasMaxLength(50);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        b.HasOne(x => x.Requirement).WithMany()
         .HasForeignKey(x => x.RequirementId).OnDelete(DeleteBehavior.Restrict);
    }
}

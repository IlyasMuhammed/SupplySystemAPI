using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Material.Domain;

namespace SMS.Modules.Material.Data.Maps;

// A36 M3/M4/M7 + service issues (D-5..D-9). Cross-module references are Guid scalars without FKs (D-1).

internal sealed class ServiceOrderMap : IEntityTypeConfiguration<ServiceOrder>
{
    public void Configure(EntityTypeBuilder<ServiceOrder> b)
    {
        b.ToTable("service_orders");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.TraceId).IsRequired();
        b.HasIndex(x => x.TraceId);
        b.Property(x => x.ServiceNumber).HasMaxLength(50).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.ServiceNumber }).IsUnique();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.EstimatedHours).HasColumnType("decimal(8,2)");
        b.Property(x => x.ActualHours).HasColumnType("decimal(8,2)");
        b.Property(x => x.ScheduledDate).HasColumnType("date");
        b.Property(x => x.ScheduledTime).HasColumnType("time(0)");
        b.Property(x => x.SourceType).HasMaxLength(30).IsRequired();
        b.Property(x => x.SourceReference).HasMaxLength(50);
        b.Property(x => x.InvoicingPolicy).HasMaxLength(30).IsRequired();
        b.Property(x => x.BillingModel).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasMaxLength(30).IsRequired();
        b.Property(x => x.MaterialReadiness).HasMaxLength(20).IsRequired();
        b.Property(x => x.CompletionNotes).HasMaxLength(2000);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        // FSD §4.3 — status board, customer, assignee, sale order source, product.
        b.HasIndex(x => new { x.OrganizationId, x.Status, x.ScheduledDate });
        b.HasIndex(x => new { x.OrganizationId, x.CustomerUuid });
        b.HasIndex(x => x.AssignedUserId).HasFilter("[AssignedUserId] IS NOT NULL");
        b.HasIndex(x => new { x.OrganizationId, x.SourceType, x.SourceUuid });
        b.HasIndex(x => new { x.OrganizationId, x.ServiceProductUuid });

        b.HasOne(x => x.Bom).WithMany()
         .HasForeignKey(x => x.BomId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Materials).WithOne(x => x.ServiceOrder)
         .HasForeignKey(x => x.ServiceOrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ServiceMaterialRequirementMap : IEntityTypeConfiguration<ServiceMaterialRequirement>
{
    public void Configure(EntityTypeBuilder<ServiceMaterialRequirement> b)
    {
        b.ToTable("service_material_requirements");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        foreach (var quantity in new[]
        {
            nameof(ServiceMaterialRequirement.NetQuantity), nameof(ServiceMaterialRequirement.ScrapAllowance),
            nameof(ServiceMaterialRequirement.RequiredQuantity), nameof(ServiceMaterialRequirement.ReservedQuantity),
            nameof(ServiceMaterialRequirement.IssuedQuantity), nameof(ServiceMaterialRequirement.ConsumedQuantity),
            nameof(ServiceMaterialRequirement.ReturnedQuantity), nameof(ServiceMaterialRequirement.ShortageQuantity)
        })
            b.Property(quantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.SourceType).HasMaxLength(20).IsRequired();
        b.Property(x => x.Uom).HasMaxLength(20).IsRequired();
        b.Property(x => x.Status).HasMaxLength(30).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.OrganizationId).IsRequired();
        b.Ignore(x => x.Outstanding);
        b.Ignore(x => x.IsStock);
        b.HasIndex(x => x.OrganizationId);
        // FSD §6.3 — per order, the shortage board, per product.
        b.HasIndex(x => new { x.ServiceOrderId, x.Status });
        b.HasIndex(x => new { x.OrganizationId, x.MaterialVariantUuid, x.WarehouseUuid, x.ShortageQuantity })
         .HasFilter("[ShortageQuantity] > 0");
        b.HasIndex(x => new { x.MaterialProductUuid, x.MaterialVariantUuid });
        b.HasIndex(x => x.AllocationDemandUuid);
    }
}

internal sealed class ServiceMaterialIssueMap : IEntityTypeConfiguration<ServiceMaterialIssue>
{
    public void Configure(EntityTypeBuilder<ServiceMaterialIssue> b)
    {
        b.ToTable("service_material_issues");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.IssueNumber).HasMaxLength(50).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.IssueNumber }).IsUnique();
        b.Property(x => x.IssueType).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasIndex(x => new { x.ServiceOrderId, x.IssueType });

        b.HasOne(x => x.ServiceOrder).WithMany()
         .HasForeignKey(x => x.ServiceOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne(x => x.Issue)
         .HasForeignKey(x => x.IssueId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ServiceMaterialIssueLineMap : IEntityTypeConfiguration<ServiceMaterialIssueLine>
{
    public void Configure(EntityTypeBuilder<ServiceMaterialIssueLine> b)
    {
        b.ToTable("service_material_issue_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.UnitCost).HasColumnType("decimal(18,4)");
        b.Property(x => x.Uom).HasMaxLength(20).IsRequired();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        b.HasOne(x => x.Requirement).WithMany()
         .HasForeignKey(x => x.RequirementId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ServiceLedgerEntryMap : IEntityTypeConfiguration<ServiceLedgerEntry>
{
    public void Configure(EntityTypeBuilder<ServiceLedgerEntry> b)
    {
        b.ToTable("service_ledger_entries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.TraceId).IsRequired();
        b.Property(x => x.ServiceNumber).HasMaxLength(50).IsRequired();
        b.Property(x => x.EntryType).HasMaxLength(10).IsRequired();
        b.Property(x => x.ProductName).HasMaxLength(300).IsRequired();
        b.Property(x => x.ProductType).HasMaxLength(30).IsRequired();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.Uom).HasMaxLength(20).IsRequired();
        b.Property(x => x.WarehouseName).HasMaxLength(200).IsRequired();
        b.Property(x => x.SourceDocumentType).HasMaxLength(20).IsRequired();
        b.Property(x => x.SourceDocumentNumber).HasMaxLength(50).IsRequired();
        b.Property(x => x.MovementType).HasMaxLength(30).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        // FSD §9.3 — per order chronologically, per product across orders (TS-26, TS-27).
        b.HasIndex(x => new { x.ServiceOrderId, x.EntryType, x.TransactionDate });
        b.HasIndex(x => new { x.OrganizationId, x.ProductUuid, x.EntryType, x.TransactionDate });

        b.HasOne<ServiceOrder>().WithMany()
         .HasForeignKey(x => x.ServiceOrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

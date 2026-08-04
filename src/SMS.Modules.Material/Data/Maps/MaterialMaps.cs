using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Material.Domain;

namespace SMS.Modules.Material.Data.Maps;

internal sealed class ProjectMap : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> b)
    {
        b.ToTable("projects");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.ProjectCode).HasMaxLength(30).IsRequired();
        // Composite, not global — each org assigns its own project codes.
        b.HasIndex(x => new { x.OrganizationId, x.ProjectCode }).IsUnique();
        b.Property(x => x.ProjectName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("ACTIVE");
        b.Property(x => x.BudgetAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
    }
}

internal sealed class MaterialIssueRequestMap : IEntityTypeConfiguration<MaterialIssueRequest>
{
    public void Configure(EntityTypeBuilder<MaterialIssueRequest> b)
    {
        b.ToTable("material_issue_requests");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.TraceId).IsRequired().ValueGeneratedOnAdd().HasDefaultValueSql("NEWSEQUENTIALID()");
        b.HasIndex(x => x.TraceId);
        b.Property(x => x.RequestNo).HasMaxLength(20).IsRequired();
        // Composite, not global — each org generates its own MIR request number sequence.
        b.HasIndex(x => new { x.OrganizationId, x.RequestNo }).IsUnique();
        b.Property(x => x.RequestType).HasMaxLength(20).IsRequired();
        b.Property(x => x.Department).HasMaxLength(100);
        b.Property(x => x.MaintenanceRef).HasMaxLength(100);
        b.Property(x => x.Priority).HasMaxLength(10).HasDefaultValue("MEDIUM");
        b.Property(x => x.Purpose).HasMaxLength(500);
        b.Property(x => x.Status).HasMaxLength(30).HasDefaultValue("DRAFT");
        b.Property(x => x.EstimatedValue).HasColumnType("decimal(18,2)");
        b.Property(x => x.RejectionReason).HasMaxLength(500);
        b.Property(x => x.ApproverRemarks).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        b.HasOne(x => x.Project)
         .WithMany()
         .HasForeignKey(x => x.ProjectId)
         .OnDelete(DeleteBehavior.Restrict)
         .IsRequired(false);

        b.HasMany(x => x.Lines)
         .WithOne(x => x.MaterialIssueRequest)
         .HasForeignKey(x => x.MaterialIssueRequestId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MaterialIssueRequestDetailMap : IEntityTypeConfiguration<MaterialIssueRequestDetail>
{
    public void Configure(EntityTypeBuilder<MaterialIssueRequestDetail> b)
    {
        b.ToTable("material_issue_request_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.ItemDescription).HasMaxLength(300).IsRequired();
        b.Property(x => x.UnitOfMeasure).HasMaxLength(20);
        b.Property(x => x.RequestedQty).HasColumnType("decimal(18,4)");
        b.Property(x => x.UnitCost).HasColumnType("decimal(18,2)");
        b.Property(x => x.EstimatedLineValue).HasColumnType("decimal(18,2)");
        b.Property(x => x.Purpose).HasMaxLength(300);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.WarehouseId);
        b.Property(x => x.WarehouseName).HasMaxLength(150);
        b.Property(x => x.PrLineId);
        b.HasIndex(x => x.PrLineId);
        b.Property(x => x.IssuedQty).HasColumnType("decimal(18,4)").HasDefaultValue(0m);
        b.Property(x => x.PendingQty).HasColumnType("decimal(18,4)").HasDefaultValue(0m);
        b.Property(x => x.LineStatus).HasMaxLength(20).HasDefaultValue("PENDING");
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        b.HasOne(x => x.MaterialIssueRequest)
         .WithMany(x => x.Lines)
         .HasForeignKey(x => x.MaterialIssueRequestId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

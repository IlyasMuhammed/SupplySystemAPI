using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

/// <summary>A33 PA-01/PA-02 — logistics.fulfillment_routes. No FK leaves this module (variants and SO lines hold a bare uuid).</summary>
internal sealed class FulfillmentRouteMap : IEntityTypeConfiguration<FulfillmentRoute>
{
    public void Configure(EntityTypeBuilder<FulfillmentRoute> b)
    {
        // A34 C1 — the CHECK accepts the reserved BUY / DROPSHIP too (D-7: the service refuses them, the schema is ready).
        b.ToTable("fulfillment_routes", t => t.HasCheckConstraint(
            "CK_fulfillment_routes_RouteCategory",
            "[RouteCategory] IN ('STOCK','MANUFACTURE','BUY','DROPSHIP')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.RouteCategory).HasMaxLength(20).IsRequired().HasDefaultValue(SMS.Shared.Common.FulfillmentRouteCategory.Stock);

        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);

        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.RowVersion).IsRowVersion();

        // BR-C1-01 — the service checks first for a clear message; this is what holds under a race.
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique();

        // BR-C1-02 as L-1 reads it: at most one default route with SHIP and one without, per organization. Unique and
        // filtered, so the database refuses a second default even when two set-default calls race.
        b.HasIndex(x => new { x.OrganizationId, x.RequiresShipping })
         .IsUnique()
         .HasFilter("[IsDefault] = 1")
         .HasDatabaseName("UX_fulfillment_routes_OrganizationId_RequiresShipping_Default");

        b.HasMany(x => x.Steps)
         .WithOne(x => x.FulfillmentRoute)
         .HasForeignKey(x => x.FulfillmentRouteId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class FulfillmentRouteStepMap : IEntityTypeConfiguration<FulfillmentRouteStep>
{
    public void Configure(EntityTypeBuilder<FulfillmentRouteStep> b)
    {
        b.ToTable("fulfillment_route_steps");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.StepCode).HasMaxLength(20).IsRequired();
        b.Property(x => x.Description).HasMaxLength(200);
        b.Property(x => x.IsMandatory).HasDefaultValue(true);

        b.HasIndex(x => new { x.FulfillmentRouteId, x.StepOrder }).IsUnique();
        b.HasIndex(x => new { x.FulfillmentRouteId, x.StepCode }).IsUnique();
    }
}

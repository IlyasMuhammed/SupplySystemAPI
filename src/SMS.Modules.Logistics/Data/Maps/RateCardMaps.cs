using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class RateCardMap : IEntityTypeConfiguration<RateCard>
{
    public void Configure(EntityTypeBuilder<RateCard> b)
    {
        b.ToTable("rate_cards");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.Name).HasMaxLength(150).IsRequired();
        b.Property(x => x.ServiceCode).HasMaxLength(50);
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();

        b.Property(x => x.MinimumCharge).HasColumnType("decimal(18,2)");
        b.Property(x => x.FuelSurchargePercent).HasColumnType("decimal(9,4)");
        b.Property(x => x.CodFeePercent).HasColumnType("decimal(9,4)");
        b.Property(x => x.CodFeeMinimum).HasColumnType("decimal(18,2)");

        // "Which cards could price this consignment on this date" — the only read that matters, and
        // the one the overlap check runs on every write.
        b.HasIndex(x => new { x.OrganizationId, x.CarrierId, x.ServiceCode, x.EffectiveFrom });

        b.Property(x => x.IsActive).HasDefaultValue(true);

        // Restrict: a carrier with a tariff configured is one somebody is shipping on.
        b.HasOne(x => x.Carrier)
         .WithMany()
         .HasForeignKey(x => x.CarrierId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Lanes)
         .WithOne(x => x.RateCard)
         .HasForeignKey(x => x.RateCardId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RateCardLaneMap : IEntityTypeConfiguration<RateCardLane>
{
    public void Configure(EntityTypeBuilder<RateCardLane> b)
    {
        b.ToTable("rate_card_lanes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.Name).HasMaxLength(150);
        b.Property(x => x.OriginCountryIso).HasMaxLength(2);
        b.Property(x => x.DestinationCountryIso).HasMaxLength(2);
        b.Property(x => x.OriginPostcodePrefix).HasMaxLength(10);
        b.Property(x => x.DestinationPostcodePrefix).HasMaxLength(10);

        // Two lanes matching on identical criteria would make the tariff depend on row order — the
        // same ambiguity carrier service codes (T-43) exist to end.
        b.HasIndex(x => new
        {
            x.RateCardId, x.OriginCountryIso, x.OriginPostcodePrefix,
            x.DestinationCountryIso, x.DestinationPostcodePrefix
        }).IsUnique();

        b.HasMany(x => x.Breaks)
         .WithOne(x => x.RateCardLane)
         .HasForeignKey(x => x.RateCardLaneId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RateCardBreakMap : IEntityTypeConfiguration<RateCardBreak>
{
    public void Configure(EntityTypeBuilder<RateCardBreak> b)
    {
        b.ToTable("rate_card_breaks");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.FromWeightKg).HasColumnType("decimal(18,3)");
        b.Property(x => x.Amount).HasColumnType("decimal(18,4)");
        b.Property(x => x.Basis).HasMaxLength(20).IsRequired();

        // One rate per weight break per lane.
        b.HasIndex(x => new { x.RateCardLaneId, x.FromWeightKg }).IsUnique();
    }
}

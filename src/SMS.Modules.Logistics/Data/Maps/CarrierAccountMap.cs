using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class CarrierAccountMap : IEntityTypeConfiguration<CarrierAccount>
{
    public void Configure(EntityTypeBuilder<CarrierAccount> b)
    {
        b.ToTable("carrier_accounts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.AccountName).HasMaxLength(100).IsRequired();
        b.Property(x => x.AccountNumber).HasMaxLength(100);
        b.Property(x => x.DefaultServiceCode).HasMaxLength(50);
        b.Property(x => x.Notes).HasMaxLength(500);

        b.Property(x => x.IsActive).HasDefaultValue(true);

        // Two accounts of one carrier sharing a name is how somebody picks the wrong one from a
        // dropdown and books on the wrong contract.
        b.HasIndex(x => new { x.OrganizationId, x.CarrierId, x.AccountName }).IsUnique();

        // "The accounts for this carrier" — the only query the booking path makes.
        b.HasIndex(x => new { x.OrganizationId, x.CarrierId, x.IsDefault });

        // Restrict, not cascade: deleting a carrier out from under its accounts would take the
        // configuration with it, and a carrier with live consignments should not be deletable
        // anyway.
        b.HasOne(x => x.Carrier)
         .WithMany()
         .HasForeignKey(x => x.CarrierId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}

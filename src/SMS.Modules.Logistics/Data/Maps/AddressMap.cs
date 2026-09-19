using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class AddressMap : IEntityTypeConfiguration<Address>
{
    public void Configure(EntityTypeBuilder<Address> b)
    {
        b.ToTable("addresses");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.Line1).HasMaxLength(200).IsRequired();
        b.Property(x => x.Line2).HasMaxLength(200);

        b.Property(x => x.CityName).HasMaxLength(100).IsRequired();
        b.Property(x => x.State).HasMaxLength(100);
        b.Property(x => x.PostalCode).HasMaxLength(20);

        b.Property(x => x.CountryName).HasMaxLength(100).IsRequired();
        b.Property(x => x.CountryIsoCode).HasMaxLength(2).IsFixedLength();

        b.Property(x => x.ContactName).HasMaxLength(100);
        b.Property(x => x.ContactPhone).HasMaxLength(30);
        b.Property(x => x.ContactPhoneE164).HasMaxLength(20);
        b.Property(x => x.ContactEmail).HasMaxLength(100);

        // ~11 cm of precision at the equator, which is far finer than any delivery needs.
        b.Property(x => x.Latitude).HasColumnType("decimal(9,6)");
        b.Property(x => x.Longitude).HasColumnType("decimal(9,6)");

        b.Property(x => x.AddressType).HasMaxLength(20).IsRequired();
        b.Property(x => x.ValidationStatus).HasMaxLength(20).IsRequired();
        b.Property(x => x.ValidationNotes).HasMaxLength(500);

        b.Property(x => x.IsActive).HasDefaultValue(true);

        // The operations queue that fixes bad addresses reads exactly this.
        b.HasIndex(x => new { x.OrganizationId, x.ValidationStatus });

        // Resolving "every address in this city" for serviceability checks later.
        b.HasIndex(x => new { x.OrganizationId, x.CityId });
    }
}

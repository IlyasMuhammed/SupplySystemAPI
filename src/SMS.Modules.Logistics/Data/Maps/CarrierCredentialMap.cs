using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class CarrierCredentialMap : IEntityTypeConfiguration<CarrierCredential>
{
    public void Configure(EntityTypeBuilder<CarrierCredential> b)
    {
        b.ToTable("carrier_credentials");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.CredentialKey).HasMaxLength(100).IsRequired();

        // Ciphertext is base64 of nonce + tag + payload, and some carriers issue long tokens.
        b.Property(x => x.EncryptedValue).HasMaxLength(4000).IsRequired();

        b.Property(x => x.Description).HasMaxLength(300);

        // One value per key per account. Two rows for "ApiKey" would make which one authenticates
        // depend on row order — and the wrong one means every booking fails.
        b.HasIndex(x => new { x.CarrierAccountId, x.CredentialKey }).IsUnique();

        // Cascade, unusually: a credential has no meaning without its account, and leaving
        // orphaned secrets behind is worse than losing them with the thing they belonged to.
        b.HasOne(x => x.CarrierAccount)
         .WithMany()
         .HasForeignKey(x => x.CarrierAccountId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

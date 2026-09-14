using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Auth.Domain;

namespace SMS.Modules.Auth.Data.Maps;

internal sealed class UserAccountMap : IEntityTypeConfiguration<UserAccount>
{
    public void Configure(EntityTypeBuilder<UserAccount> builder)
    {
        builder.ToTable("UserAccounts");
        builder.HasKey(x => x.UserID);
        builder.Property(x => x.UserID).ValueGeneratedOnAdd();
        builder.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.MiddleName).HasMaxLength(100);
        builder.Property(x => x.LastName).HasMaxLength(100);
        builder.Property(x => x.Email).HasMaxLength(256).IsRequired();
        builder.HasIndex(x => x.Email).IsUnique();
        builder.Property(x => x.Password).HasMaxLength(512).IsRequired();
        builder.Property(x => x.Phone).HasMaxLength(30);
        builder.Property(x => x.Address).HasMaxLength(500);
        builder.Property(x => x.ZipCode).HasMaxLength(20);
        builder.Property(x => x.AccountActivationToken).HasMaxLength(256);
        builder.Property(x => x.PasswordResetToken).HasMaxLength(50);
        builder.Property(x => x.ProfilePictureUrl).HasMaxLength(512);
        builder.Property(x => x.Department).HasMaxLength(100);
        builder.Property(x => x.DepartmentId);
        builder.Property(x => x.SupervisorId);
        // Self-referential FK — no cascade (would create cycles)
        builder.HasOne<UserAccount>()
               .WithMany()
               .HasForeignKey(x => x.SupervisorId)
               .OnDelete(DeleteBehavior.Restrict)
               .IsRequired(false);
        builder.Property(x => x.LastLoginAt);
        builder.Property(x => x.FailedLoginAttempts).HasDefaultValue(0).IsRequired();
        builder.Property(x => x.LastFailedAt);
        builder.Property(x => x.LockedUntil);
        builder.Property(x => x.OrganizationId).IsRequired();
        builder.HasIndex(x => x.OrganizationId);
        builder.Property(x => x.InviteToken).HasMaxLength(64);
        builder.Property(x => x.InviteTokenExpiresAt);
        builder.Property(x => x.SupplierType).HasMaxLength(20).HasDefaultValue("INTERNAL").IsRequired();
    }
}

internal sealed class DepartmentMap : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> builder)
    {
        builder.ToTable("Departments");
        builder.HasKey(x => x.DepartmentId);
        builder.Property(x => x.DepartmentId).ValueGeneratedOnAdd();
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Code).HasMaxLength(20);
        // Composite, not global — each org names its own departments independently.
        builder.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
        builder.HasOne<UserAccount>()
               .WithMany()
               .HasForeignKey(x => x.HeadUserId)
               .OnDelete(DeleteBehavior.SetNull)
               .IsRequired(false);
        builder.Property(x => x.OrganizationId).IsRequired();
        builder.HasIndex(x => x.OrganizationId);
    }
}

internal sealed class UserSessionMap : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("UserSessions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => x.ExpiresAt);
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("GETUTCDATE()").IsRequired();
        builder.HasOne<UserAccount>()
               .WithMany()
               .HasForeignKey(x => x.UserID)
               .OnDelete(DeleteBehavior.Cascade);
        builder.Property(x => x.OrganizationId).IsRequired();
        builder.HasIndex(x => x.OrganizationId);
    }
}

internal sealed class PermissionMap : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permissions");
        builder.HasKey(x => x.PermissionID);
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Code).HasMaxLength(100).IsRequired();
        builder.HasIndex(x => x.Code).IsUnique();
        builder.Property(x => x.Description).HasMaxLength(500);
    }
}

internal sealed class RoleMap : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Roles");
        builder.HasKey(x => x.RoleID);
        builder.Property(x => x.RoleID).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(100).IsRequired();
        builder.Property(x => x.RoleCode).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500);
        builder.Property(x => x.IsActive).HasDefaultValue(true).IsRequired();
        builder.Property(x => x.IsGlobal).HasDefaultValue(true).IsRequired();
        // No longer globally unique — RoleCode only needs to be unique within what a caller can
        // see (app-level CodeExistsAsync, correctly scoped by the query filter this interface
        // adds), since two different orgs' custom roles may legitimately share a code.
        builder.HasIndex(x => x.OrganizationId);
    }
}

internal sealed class RolePermissionMap : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("RolePermissions");
        builder.HasKey(x => x.RolePermissionID);
        builder.Property(x => x.RolePermissionID).ValueGeneratedOnAdd();
        builder.Property(x => x.OrganizationId).IsRequired();
        builder.HasIndex(x => x.OrganizationId);
    }
}

internal sealed class UserPermissionMap : IEntityTypeConfiguration<UserPermission>
{
    public void Configure(EntityTypeBuilder<UserPermission> builder)
    {
        builder.ToTable("UserPermissions");
        builder.HasKey(x => x.UserPermissionID);
        builder.Property(x => x.UserPermissionID).ValueGeneratedOnAdd();
        builder.Property(x => x.OrganizationId).IsRequired();
        builder.HasIndex(x => x.OrganizationId);
    }
}

internal sealed class UserSupplierAccessMap : IEntityTypeConfiguration<UserSupplierAccess>
{
    public void Configure(EntityTypeBuilder<UserSupplierAccess> builder)
    {
        builder.ToTable("UserSupplierAccess");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.AssignedAt).IsRequired();
        builder.HasIndex(x => new { x.UserID, x.SupplierId }).IsUnique();
        builder.Property(x => x.OrganizationId).IsRequired();
        builder.HasIndex(x => x.OrganizationId);
    }
}

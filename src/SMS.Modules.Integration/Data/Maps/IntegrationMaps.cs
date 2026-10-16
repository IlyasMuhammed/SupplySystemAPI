using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Data.Maps;

internal sealed class IntegrationConnectionMap : IEntityTypeConfiguration<IntegrationConnection>
{
    public void Configure(EntityTypeBuilder<IntegrationConnection> b)
    {
        b.ToTable("Connections");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        // One SCM organization ↔ one QuickBooks company.
        b.HasIndex(x => new { x.OrganizationId, x.ProviderKey }).IsUnique();
        b.Property(x => x.ProviderKey).HasMaxLength(20).IsRequired();
        b.Property(x => x.RealmId).HasMaxLength(50);
        b.Property(x => x.CompanyName).HasMaxLength(200);
        b.Property(x => x.Environment).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.EncryptedAccessToken).HasMaxLength(8000);
        b.Property(x => x.EncryptedRefreshToken).HasMaxLength(2000);
        b.Property(x => x.HomeCurrencyCode).HasMaxLength(10);
        b.Property(x => x.Country).HasMaxLength(10);
        b.Property(x => x.LastError).HasMaxLength(1000);
        b.Property(x => x.AppClientId).HasMaxLength(200);
        b.Property(x => x.AppSource).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal sealed class OAuthStateTokenMap : IEntityTypeConfiguration<OAuthStateToken>
{
    public void Configure(EntityTypeBuilder<OAuthStateToken> b)
    {
        b.ToTable("OAuthStateTokens");
        b.HasKey(x => x.Id);
        b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.Property(x => x.AppClientId).HasMaxLength(200);
    }
}

internal sealed class QuickBooksAppCredentialMap : IEntityTypeConfiguration<QuickBooksAppCredential>
{
    public void Configure(EntityTypeBuilder<QuickBooksAppCredential> b)
    {
        b.ToTable("QuickBooksAppCredentials");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        // D-1: SQL Server lets a unique index hold one NULL — exactly one platform row, one row per organization.
        b.HasIndex(x => x.OrganizationId).IsUnique().HasFilter(null);
        b.Property(x => x.ClientId).HasMaxLength(200).IsRequired();
        b.Property(x => x.EncryptedClientSecret).HasMaxLength(2000).IsRequired();
        b.Property(x => x.Environment).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RedirectUri).HasMaxLength(500);
        b.Property(x => x.FrontendReturnUrl).HasMaxLength(500);
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal sealed class ApiClientMap : IEntityTypeConfiguration<ApiClient>
{
    public void Configure(EntityTypeBuilder<ApiClient> b)
    {
        b.ToTable("ApiClients");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Scopes).HasMaxLength(500);
        b.HasMany(x => x.Keys).WithOne(k => k.ApiClient).HasForeignKey(k => k.ApiClientId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ApiClientKeyMap : IEntityTypeConfiguration<ApiClientKey>
{
    public void Configure(EntityTypeBuilder<ApiClientKey> b)
    {
        b.ToTable("ApiClientKeys");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        // Looked up by prefix before any tenant is known (the caller is not authenticated yet).
        b.HasIndex(x => x.KeyPrefix).IsUnique();
        b.Property(x => x.KeyPrefix).HasMaxLength(16).IsRequired();
        b.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
    }
}

internal sealed class IntegrationSettingsMap : IEntityTypeConfiguration<IntegrationSettings>
{
    public void Configure(EntityTypeBuilder<IntegrationSettings> b)
    {
        b.ToTable("Settings");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.ConnectionId).IsUnique();
        b.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ItemTypeDefault).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.PartnerScope).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.DefaultIncomeAccountId).HasMaxLength(50);
        b.Property(x => x.DefaultExpenseAccountId).HasMaxLength(50);
        b.Property(x => x.FreightExpenseAccountId).HasMaxLength(50);
        b.Property(x => x.DiscountAccountId).HasMaxLength(50);
        b.Property(x => x.DefaultPurchaseTaxCodeId).HasMaxLength(50);
    }
}

internal sealed class SettingsAuditEntryMap : IEntityTypeConfiguration<SettingsAuditEntry>
{
    public void Configure(EntityTypeBuilder<SettingsAuditEntry> b)
    {
        b.ToTable("SettingsAudit");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.ConnectionId, x.CreatedAt });
        b.Property(x => x.Area).HasMaxLength(50).IsRequired();
        b.Property(x => x.Action).HasMaxLength(100).IsRequired();
    }
}

internal sealed class TaxCodeMappingMap : IEntityTypeConfiguration<TaxCodeMapping>
{
    public void Configure(EntityTypeBuilder<TaxCodeMapping> b)
    {
        b.ToTable("TaxCodeMappings");
        b.HasKey(x => x.Id);
        b.Property(x => x.TaxPercent).HasPrecision(9, 4);
        b.Property(x => x.SourceTaxCode).HasMaxLength(20);
        // Two kinds of row: a caller's tax CODE → QBO code (preferred), or, for lines that carry only
        // a rate (records from before tax codes, other systems), a bare PERCENT → QBO code.
        b.HasIndex(x => new { x.ConnectionId, x.TaxPercent }).IsUnique().HasFilter("[SourceTaxCode] IS NULL");
        b.HasIndex(x => new { x.ConnectionId, x.SourceTaxCode }).IsUnique().HasFilter("[SourceTaxCode] IS NOT NULL");
        b.Property(x => x.QboTaxCodeId).HasMaxLength(50).IsRequired();
    }
}

internal sealed class PaymentTermMappingMap : IEntityTypeConfiguration<PaymentTermMapping>
{
    public void Configure(EntityTypeBuilder<PaymentTermMapping> b)
    {
        b.ToTable("PaymentTermMappings");
        b.HasKey(x => x.Id);
        b.Property(x => x.PaymentTermExternalId).HasMaxLength(100).IsRequired();
        b.Property(x => x.PaymentTermName).HasMaxLength(200);
        b.Property(x => x.QboTermId).HasMaxLength(50).IsRequired();
        b.HasIndex(x => new { x.ConnectionId, x.PaymentTermExternalId }).IsUnique();
    }
}

internal sealed class ReferenceSnapshotMap : IEntityTypeConfiguration<ReferenceSnapshot>
{
    public void Configure(EntityTypeBuilder<ReferenceSnapshot> b)
    {
        b.ToTable("ReferenceSnapshots");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasMaxLength(30).IsRequired();
        b.HasIndex(x => new { x.ConnectionId, x.Kind }).IsUnique();
    }
}

internal sealed class EntityMapMap : IEntityTypeConfiguration<EntityMap>
{
    public void Configure(EntityTypeBuilder<EntityMap> b)
    {
        b.ToTable("EntityMaps");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.HasIndex(x => new { x.ConnectionId, x.SourceSystem, x.Kind, x.ExternalId }).IsUnique();
        // Dashboard filters, and "is this remote record already linked to something else?".
        b.HasIndex(x => new { x.ConnectionId, x.Kind, x.State });
        b.HasIndex(x => new { x.ConnectionId, x.Kind, x.RemoteId });
        b.Property(x => x.SourceSystem).HasMaxLength(100).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ExternalId).HasMaxLength(100).IsRequired();
        b.Property(x => x.DisplayLabel).HasMaxLength(300);
        b.Property(x => x.PayloadFingerprint).HasMaxLength(64);
        b.Property(x => x.LastPushedFingerprint).HasMaxLength(64);
        b.Property(x => x.RemoteId).HasMaxLength(50);
        b.Property(x => x.RemoteSyncToken).HasMaxLength(20);
        b.Property(x => x.RemoteDocNumber).HasMaxLength(50);
        b.Property(x => x.RemoteName).HasMaxLength(200);
        b.Property(x => x.LinkOrigin).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.LastErrorCode).HasMaxLength(100);
        b.Property(x => x.LastError).HasMaxLength(2000);
        b.Property(x => x.Warning).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

internal sealed class SyncOutboxEntryMap : IEntityTypeConfiguration<SyncOutboxEntry>
{
    public void Configure(EntityTypeBuilder<SyncOutboxEntry> b)
    {
        b.ToTable("Outbox");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        // The job's pick query: due, queued entries per connection.
        b.HasIndex(x => new { x.ConnectionId, x.Status, x.NextAttemptAt });
        b.HasIndex(x => x.EntityMapId);
        b.Property(x => x.Operation).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.BlockedReason).HasMaxLength(1000);
        b.HasOne(x => x.EntityMap).WithMany().HasForeignKey(x => x.EntityMapId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SyncCommandClaimMap : IEntityTypeConfiguration<SyncCommandClaim>
{
    public void Configure(EntityTypeBuilder<SyncCommandClaim> b)
    {
        b.ToTable("CommandClaims");
        b.HasKey(x => x.Id);
        // The database, not the code, is what stops two workers claiming one command.
        b.HasIndex(x => new { x.OrganizationId, x.CommandKey }).IsUnique();
        b.HasIndex(x => new { x.Status, x.LeaseExpiresAt });
        // Every push reads the claims of its own map.
        b.HasIndex(x => new { x.OrganizationId, x.EntityMapId });
        b.Property(x => x.CommandKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RemoteId).HasMaxLength(50);
        b.Property(x => x.ErrorCode).HasMaxLength(100);
    }
}

internal sealed class SyncLogEntryMap : IEntityTypeConfiguration<SyncLogEntry>
{
    public void Configure(EntityTypeBuilder<SyncLogEntry> b)
    {
        b.ToTable("SyncLog");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.HasIndex(x => new { x.EntityMapId, x.CreatedAt });
        b.HasIndex(x => x.CreatedAt);
        // The dashboard's "last run" per connection.
        b.HasIndex(x => new { x.ConnectionId, x.CreatedAt });
        b.Property(x => x.Operation).HasMaxLength(30).IsRequired();
        b.Property(x => x.Outcome).HasMaxLength(30).IsRequired();
        b.Property(x => x.ErrorCode).HasMaxLength(100);
        b.Property(x => x.Message).HasMaxLength(2000);
        b.Property(x => x.IntuitTid).HasMaxLength(100);
    }
}

internal sealed class SyncControlConfigMap : IEntityTypeConfiguration<SyncControlConfig>
{
    public void Configure(EntityTypeBuilder<SyncControlConfig> b)
    {
        b.ToTable("SyncControlConfigs");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.ConnectionId, x.DocumentType }).IsUnique();
        b.Property(x => x.DocumentType).HasMaxLength(50).IsRequired();
        b.Property(x => x.DisplayLabel).HasMaxLength(100).IsRequired();
    }
}

internal sealed class ProviderDefaultMap : IEntityTypeConfiguration<ProviderDefault>
{
    public void Configure(EntityTypeBuilder<ProviderDefault> b)
    {
        b.ToTable("ProviderDefaults");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.ConnectionId, x.SettingKey }).IsUnique();
        b.Property(x => x.SettingKey).HasMaxLength(100).IsRequired();
        b.Property(x => x.SettingValue).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(300);
    }
}

internal sealed class DocumentOverrideMap : IEntityTypeConfiguration<DocumentOverride>
{
    public void Configure(EntityTypeBuilder<DocumentOverride> b)
    {
        b.ToTable("DocumentOverrides");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.ConnectionId, x.DocumentType, x.SettingKey }).IsUnique();
        b.Property(x => x.DocumentType).HasMaxLength(50).IsRequired();
        b.Property(x => x.SettingKey).HasMaxLength(100).IsRequired();
        b.Property(x => x.SettingValue).HasMaxLength(200).IsRequired();
    }
}

internal sealed class RemoteEntityRecordMap : IEntityTypeConfiguration<RemoteEntityRecord>
{
    public void Configure(EntityTypeBuilder<RemoteEntityRecord> b)
    {
        b.ToTable("RemoteEntities");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.ConnectionId, x.Kind, x.RemoteId }).IsUnique();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RemoteId).HasMaxLength(50).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.CompanyName).HasMaxLength(200);
        b.Property(x => x.Email).HasMaxLength(200);
        b.Property(x => x.TaxId).HasMaxLength(50);
        b.Property(x => x.AccountNumber).HasMaxLength(100);
        b.Property(x => x.Sku).HasMaxLength(100);
        b.Property(x => x.CurrencyCode).HasMaxLength(10);
    }
}

internal sealed class MatchCandidateMap : IEntityTypeConfiguration<MatchCandidate>
{
    public void Configure(EntityTypeBuilder<MatchCandidate> b)
    {
        b.ToTable("MatchCandidates");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.HasIndex(x => new { x.ConnectionId, x.Kind, x.Decision });
        b.HasIndex(x => x.EntityMapId);
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.RemoteId).HasMaxLength(50);
        b.Property(x => x.RemoteName).HasMaxLength(200);
        b.Property(x => x.Confidence).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Reason).HasMaxLength(200);
        b.Property(x => x.Decision).HasConversion<string>().HasMaxLength(20);
    }
}

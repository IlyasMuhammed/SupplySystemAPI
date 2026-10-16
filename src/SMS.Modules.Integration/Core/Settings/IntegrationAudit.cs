using System.Text.Json;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Core.Settings;

/// <summary>Values of <see cref="SettingsAuditEntry.Area"/>.</summary>
internal static class AuditAreas
{
    public const string Connection  = "Connection";
    public const string Settings    = "Settings";
    public const string TaxMapping  = "TaxMapping";
    public const string TermMapping = "TermMapping";
    public const string Mode        = "Mode";
    public const string Matching    = "Matching";
    public const string ApiClient   = "ApiClient";
    /// <summary>The organization's own Intuit app keys (docs/quickbooks/APP-CREDENTIALS.md D-10) — never the secret.</summary>
    public const string AppKeys     = "AppKeys";
    // A44 — sync control (D-14), provider defaults / overrides (D-15), account mappings (D-16), unlink (D-17).
    public const string SyncControl      = "SyncControl";
    public const string ProviderDefaults = "ProviderDefaults";
    public const string AccountMapping   = "AccountMapping";
    public const string Link             = "Link";
}

/// <summary>
/// Writes <see cref="SettingsAuditEntry"/> rows. Adds to the context without saving, so an audit row is
/// committed in the same <c>SaveChanges</c> as the change it describes — never one without the other.
/// <para>
/// Callers pass <b>projections</b> (anonymous objects of the fields that matter), never entities: an
/// entity would drag token ciphertexts or key hashes into the audit table.
/// </para>
/// </summary>
internal static class IntegrationAudit
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Add(
        IntegrationDbContext db, int connectionId, string area, string action,
        object? before, object? after, int userId, Guid? organizationId = null)
    {
        db.SettingsAudit.Add(new SettingsAuditEntry
        {
            // Normally stamped from the tenant. Explicit where the caller already knows it (the anonymous
            // callback), so an audit row can never land under a fallback organization.
            OrganizationId = organizationId ?? Guid.Empty,
            ConnectionId   = connectionId,
            Area           = area,
            Action         = action.Length <= 100 ? action : action[..100],
            BeforeJson     = before is null ? null : JsonSerializer.Serialize(before, Json),
            AfterJson      = after  is null ? null : JsonSerializer.Serialize(after, Json),
            UserId         = userId,
            CreatedAt      = DateTime.UtcNow
        });
    }
}

namespace SMS.Modules.Integration.Models;

// docs/quickbooks/APP-CREDENTIALS.md — the Intuit app keys screens. The client secret is write-only: no model here
// carries it back (D-4). Change together with SupplyChainFrontend/src/app/models/quickbooks-integration.models.ts.

/// <summary>System Administration › QuickBooks App (super admin).</summary>
public sealed class PlatformAppKeysModel
{
    /// <summary>True when the platform has usable keys (database row, else configuration).</summary>
    public bool      IsConfigured      { get; set; }
    /// <summary>Platform (saved here) | Configuration (environment variables / user-secrets) | null.</summary>
    public string?   Source            { get; set; }
    public string?   ClientId          { get; set; }
    public bool      HasSecret         { get; set; }
    /// <summary>Sandbox | Production</summary>
    public string    Environment       { get; set; } = "Sandbox";
    /// <summary>The callback URL to register on every Intuit app (the platform's and organizations' own).</summary>
    public string?   RedirectUri       { get; set; }
    public string?   FrontendReturnUrl { get; set; }
    /// <summary>True when the keys above are the saved row (false: they are the configuration fallback).</summary>
    public bool      SavedInDatabase   { get; set; }
    /// <summary>The configuration fallback, for the "used when nothing is saved" line. Never the secret.</summary>
    public bool      ConfigurationAvailable { get; set; }
    public string?   ConfigurationClientId  { get; set; }
    /// <summary>Live connections that use the current platform keys — what a Client ID change would disconnect.</summary>
    public int       ConnectionsUsingPlatformKeys { get; set; }
    /// <summary>Organizations that have saved their own Intuit app keys.</summary>
    public int       OrganizationsWithOwnKeys     { get; set; }
    public DateTime? ModifiedAt        { get; set; }
    public int?      ModifiedBy        { get; set; }
}

public sealed class SavePlatformAppKeysRequest
{
    public string?  ClientId          { get; set; }
    /// <summary>Blank keeps the stored secret.</summary>
    public string?  ClientSecret      { get; set; }
    /// <summary>Sandbox | Production</summary>
    public string?  Environment       { get; set; }
    public string?  RedirectUri       { get; set; }
    public string?  FrontendReturnUrl { get; set; }
    /// <summary>D-8: required when live connections use keys this change replaces (they must reconnect).</summary>
    public bool     ConfirmBreakingChange { get; set; }
}

/// <summary>The organization's own Intuit app card on the QuickBooks page.</summary>
public sealed class TenantAppKeysModel
{
    /// <summary>FEATURE_QBO_OWN_APP is licensed and on — the keys can be saved.</summary>
    public bool      FeatureEnabled        { get; set; }
    public bool      HasOwnKeys            { get; set; }
    public string?   ClientId              { get; set; }
    public bool      HasSecret             { get; set; }
    /// <summary>Sandbox | Production</summary>
    public string    Environment           { get; set; } = "Sandbox";
    /// <summary>The callback URL the organization must register on its Intuit app (D-5).</summary>
    public string?   RedirectUriToRegister { get; set; }
    /// <summary>Which keys the next Connect would use: Tenant | Platform | Configuration | null.</summary>
    public string?   NewConnectionsUse     { get; set; }
    /// <summary>The current connection was made with the organization's own keys (changing them needs a disconnect).</summary>
    public bool      ConnectedWithOwnKeys  { get; set; }
    public DateTime? ModifiedAt            { get; set; }
}

public sealed class SaveTenantAppKeysRequest
{
    public string? ClientId     { get; set; }
    /// <summary>Blank keeps the stored secret.</summary>
    public string? ClientSecret { get; set; }
    /// <summary>Sandbox | Production</summary>
    public string? Environment  { get; set; }
}

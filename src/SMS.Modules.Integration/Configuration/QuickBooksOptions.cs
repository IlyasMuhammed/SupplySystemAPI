namespace SMS.Modules.Integration.Configuration;

/// <summary>
/// Bound from the <c>QuickBooks</c> configuration section. ClientId and ClientSecret identify SCM as a
/// product to Intuit — the same for every tenant — so they live in configuration / the secret store,
/// never in the per-organization vault (which holds only each company's tokens).
/// </summary>
public sealed class QuickBooksOptions
{
    public const string SectionName = "QuickBooks";

    public string  ClientId          { get; set; } = string.Empty;
    public string  ClientSecret      { get; set; } = string.Empty;
    /// <summary>Must match a redirect URI registered on the Intuit app exactly.</summary>
    public string  RedirectUri       { get; set; } = string.Empty;
    /// <summary><c>Sandbox</c> or <c>Production</c>.</summary>
    public string  Environment       { get; set; } = "Sandbox";
    /// <summary>Pinned: Intuit changes default behaviour between minor versions.</summary>
    public string  MinorVersion      { get; set; } = "75";
    /// <summary>The Angular page the callback redirects back to, e.g. http://localhost:4200/portal/pages/integrations/quickbooks.</summary>
    public string  FrontendReturnUrl { get; set; } = string.Empty;
    /// <summary>Minutes an OAuth state token stays valid.</summary>
    public int     StateTokenMinutes { get; set; } = 10;

    public bool IsProduction => string.Equals(Environment, "Production", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the app keys needed to start a connection are present.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && !string.IsNullOrWhiteSpace(RedirectUri);

    /// <summary>Where "View in QuickBooks" deep links point.</summary>
    public string AppBaseUrl => IsProduction ? "https://app.qbo.intuit.com" : "https://app.sandbox.qbo.intuit.com";
}

/// <summary>Bound from <c>Integration:Jobs</c>. Defaults are what the plan specifies.</summary>
public sealed class IntegrationJobOptions
{
    public const string SectionName = "Integration:Jobs";

    public string OutboxCron          { get; set; } = "*/1 * * * *";
    public string SweepCron           { get; set; } = "*/5 * * * *";
    public string ReconciliationCron  { get; set; } = "0 * * * *";
    public string TokenRefreshCron    { get; set; } = "0 3 * * *";
    public string ReferenceRefreshCron { get; set; } = "30 3 * * *";
    public string CleanupCron         { get; set; } = "0 4 * * *";

    public int    OutboxBatchSize     { get; set; } = 25;
    public int    MaxAttempts         { get; set; } = 8;
    public int    LeaseMinutes        { get; set; } = 5;
    public int    LeaseGraceMinutes   { get; set; } = 2;
    public int    DependencyWaitDays  { get; set; } = 7;
    public int    SyncLogRetentionDays { get; set; } = 90;
    /// <summary>Refresh the access token when it expires within this many minutes.</summary>
    public int    AccessTokenRefreshSkewMinutes { get; set; } = 5;
    /// <summary>Warn (notification + UI) when the refresh token's hard expiry is closer than this.</summary>
    public int    ReconnectWarningDays { get; set; } = 30;
}

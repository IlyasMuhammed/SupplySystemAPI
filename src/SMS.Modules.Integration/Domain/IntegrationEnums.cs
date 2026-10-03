namespace SMS.Modules.Integration.Domain;

// All persisted as their names (HasConversion<string>), so the tables read without a lookup.

/// <summary>A connection is a state machine, not a boolean (plan Part B.3 of the Phase 1 file).</summary>
internal enum ConnectionStatus
{
    NotConnected,
    /// <summary>A state token was issued; waiting for Intuit's callback.</summary>
    Connecting,
    Connected,
    /// <summary>Connected, but mappings / matching are not confirmed yet. Nothing is pushed.</summary>
    NeedsSetup,
    /// <summary>Syncing (in the mode the settings say — DryRun or Live).</summary>
    Live,
    /// <summary>The customer disconnected our app inside QuickBooks, or the grant was refused. Reconnect needed.</summary>
    Revoked,
    /// <summary>The refresh token ran out (idle too long, or its hard maximum lifetime). Reconnect needed.</summary>
    Expired
}

internal enum IntegrationEnvironment
{
    Sandbox,
    Production
}

internal enum SyncMode
{
    /// <summary>Build and validate every payload, log it, send nothing.</summary>
    DryRun,
    Live
}

internal enum PartnerScope
{
    /// <summary>A customer / vendor / item is pushed the first time a document references it, or when pushed explicitly.</summary>
    OnlyWhenReferenced,
    AllActive
}

internal enum ItemTypeDefault
{
    NonInventory,
    Service
}

internal enum LinkOrigin
{
    /// <summary>We created the QuickBooks record.</summary>
    Created,
    /// <summary>Matched to a record the accountant had already entered.</summary>
    Adopted
}

internal enum OutboxOperation
{
    Upsert,
    Void
}

internal enum OutboxStatus
{
    Queued,
    Running,
    Done,
    Failed,
    Blocked,
    WaitingOnDependency,
    /// <summary>The connection was revoked or expired; resumes on reconnect.</summary>
    Suspended
}

internal enum ClaimStatus
{
    InFlight,
    Succeeded,
    Refused,
    /// <summary>The call's outcome is not known (timeout, 5xx, lost lease).</summary>
    Unknown
}

internal enum MatchConfidence
{
    Exact,
    Probable,
    None
}

internal enum MatchDecision
{
    Pending,
    Link,
    CreateNew,
    Skip
}

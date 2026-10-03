using System.Text.Json.Serialization;

namespace SMS.Shared.Integration.QuickBooks;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GatewayOutcome
{
    /// <summary>Stored, and queued to send (or already in sync).</summary>
    Accepted,
    /// <summary>This organization has no QuickBooks connection. In-process callers ignore this.</summary>
    NotConnected,
    /// <summary>Connected, but syncing of this kind is switched off or out of scope. Nothing to do.</summary>
    Disabled,
    /// <summary>Refused by validation. See <see cref="GatewayResult.Errors"/>.</summary>
    Invalid,
    /// <summary>Stored, but it references records that are not in QuickBooks yet.</summary>
    WaitingOnDependency
}

public sealed record GatewayError(string Field, string Code, string Message);

public sealed record GatewayDependency(SyncKind Kind, string ExternalId);

public sealed class GatewayResult
{
    public GatewayOutcome                    Outcome             { get; init; }
    public SyncState?                        State               { get; init; }
    public IReadOnlyList<GatewayError>       Errors              { get; init; } = [];
    public IReadOnlyList<GatewayDependency>  MissingDependencies { get; init; } = [];

    public static GatewayResult Accepted(SyncState state) => new() { Outcome = GatewayOutcome.Accepted, State = state };
    public static GatewayResult NotConnected()            => new() { Outcome = GatewayOutcome.NotConnected };
    public static GatewayResult Disabled()                => new() { Outcome = GatewayOutcome.Disabled };

    public static GatewayResult Invalid(IReadOnlyList<GatewayError> errors) =>
        new() { Outcome = GatewayOutcome.Invalid, State = SyncState.Blocked, Errors = errors };

    public static GatewayResult Waiting(IReadOnlyList<GatewayDependency> missing) =>
        new() { Outcome = GatewayOutcome.WaitingOnDependency, State = SyncState.WaitingOnDependency, MissingDependencies = missing };
}

/// <summary>One record's sync status, for badges and for callers polling after a 202.</summary>
public sealed record SyncStatus(
    SyncKind  Kind,
    string    ExternalId,
    SyncState State,
    string?   RemoteId,
    string?   RemoteDocNumber,
    string?   LastErrorCode,
    string?   LastError,
    DateTime? LastSyncedAt,
    string?   DeepLink);

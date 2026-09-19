using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Domain.StateMachines;

/// <summary>The outcome of checking one transition.</summary>
/// <param name="IsAllowed">Whether the move is legal.</param>
/// <param name="Reason">Why it was refused. Null when allowed.</param>
internal readonly record struct TransitionResult(bool IsAllowed, string? Reason)
{
    internal static TransitionResult Allowed() => new(true, null);
    internal static TransitionResult Refused(string reason) => new(false, reason);
}

internal interface IStateMachine<TStatus> where TStatus : struct, Enum
{
    /// <summary>Every status this machine knows.</summary>
    IReadOnlyCollection<TStatus> AllStatuses { get; }

    /// <summary>The statuses reachable in one step from <paramref name="status"/>.</summary>
    IReadOnlyCollection<TStatus> From(TStatus status);

    /// <summary>True when the status has no outgoing transitions.</summary>
    bool IsTerminal(TStatus status);

    bool CanTransition(TStatus from, TStatus to);

    TransitionResult Validate(TStatus from, TStatus to);

    /// <summary>Validates, throwing <see cref="ConflictException"/> when refused.</summary>
    void EnsureCanTransition(TStatus from, TStatus to);
}

/// <summary>
/// Table-driven status transitions.
/// <para>
/// The table is the specification — a status missing from it has no outgoing transitions, and a
/// pair absent from a status's row is refused. Nothing is implicit: there is no "any status can
/// be cancelled" escape hatch, because that is exactly the kind of rule that quietly lets a
/// delivery be cancelled after its stock has already left the building.
/// </para>
/// <para>
/// Self-transitions are never declared, so <c>X → X</c> is always refused. That matters most for
/// <c>BOOKING → BOOKING</c>: re-entering an in-flight carrier booking is how a shipment gets
/// booked twice and a second parcel gets paid for.
/// </para>
/// </summary>
internal abstract class StateMachine<TStatus> : IStateMachine<TStatus> where TStatus : struct, Enum
{
    private readonly IReadOnlyDictionary<TStatus, IReadOnlyCollection<TStatus>> _transitions;

    /// <summary>What the document is called in refusal messages, e.g. "delivery".</summary>
    protected abstract string DocumentName { get; }

    protected StateMachine(IReadOnlyDictionary<TStatus, TStatus[]> transitions)
    {
        var table = new Dictionary<TStatus, IReadOnlyCollection<TStatus>>();

        foreach (var status in Enum.GetValues<TStatus>())
        {
            var next = transitions.TryGetValue(status, out var declared) ? declared : [];

            if (next.Contains(status))
                throw new InvalidOperationException(
                    $"{typeof(TStatus).Name}.{status} declares a transition to itself. " +
                    "Self-transitions are never legal — re-entering a state hides a repeated action.");

            if (next.Length != next.Distinct().Count())
                throw new InvalidOperationException(
                    $"{typeof(TStatus).Name}.{status} declares the same target more than once.");

            table[status] = next;
        }

        _transitions = table;
    }

    public IReadOnlyCollection<TStatus> AllStatuses => Enum.GetValues<TStatus>();

    public IReadOnlyCollection<TStatus> From(TStatus status) =>
        _transitions.TryGetValue(status, out var next) ? next : [];

    public bool IsTerminal(TStatus status) => From(status).Count == 0;

    public bool CanTransition(TStatus from, TStatus to) => From(from).Contains(to);

    public TransitionResult Validate(TStatus from, TStatus to)
    {
        if (CanTransition(from, to)) return TransitionResult.Allowed();

        var fromCode = LogisticsCode.Of(from);
        var toCode   = LogisticsCode.Of(to);

        if (IsTerminal(from))
            return TransitionResult.Refused(
                $"This {DocumentName} is {fromCode}, which is final. It cannot move to {toCode}.");

        var allowed = string.Join(", ", From(from).Select(LogisticsCode.Of));

        return TransitionResult.Refused(
            $"A {DocumentName} in {fromCode} cannot move to {toCode}. From {fromCode} it can only " +
            $"move to: {allowed}.");
    }

    public void EnsureCanTransition(TStatus from, TStatus to)
    {
        var result = Validate(from, to);
        if (!result.IsAllowed) throw new ConflictException(result.Reason!);
    }
}

namespace SMS.Modules.Logistics.Domain.StateMachines;

/// <summary>
/// The life of one carrier call in the command ledger.
/// <para>
/// The table encodes one idea: <b>an unresolved call is never quietly turned into an answer.</b>
/// UNKNOWN leaves only by the carrier finally answering, by a person confirming what the carrier
/// actually did, or by a retry — and whether a retry is safe is decided by
/// <c>CarrierCommandLedger</c>, not here, because it depends on the carrier rather than the status.
/// </para>
/// </summary>
internal sealed class CarrierCommandStateMachine : StateMachine<CarrierCommandStatus>
{
    internal static readonly CarrierCommandStateMachine Instance = new();

    protected override string DocumentName => "carrier command";

    private CarrierCommandStateMachine() : base(BuildTable()) { }

    private static Dictionary<CarrierCommandStatus, CarrierCommandStatus[]> BuildTable() => new()
    {
        // The call is out. It resolves one of three ways — and a worker that dies mid-call, or
        // whose lease runs out, lands in UNKNOWN too, because that is exactly what it is.
        [CarrierCommandStatus.InFlight] =
        [
            CarrierCommandStatus.Succeeded,
            CarrierCommandStatus.Refused,
            CarrierCommandStatus.Unknown
        ],

        [CarrierCommandStatus.Unknown] =
        [
            // A retry. Only allowed by the ledger when the carrier deduplicates on our key.
            CarrierCommandStatus.InFlight,
            // A late answer from the original call, or a person confirming with the carrier.
            CarrierCommandStatus.Succeeded,
            CarrierCommandStatus.Refused,
            // A person confirmed the carrier has no record of it.
            CarrierCommandStatus.NotPerformed
        ],

        // Terminal. A refusal is an answer — the identical request earns the identical refusal —
        // and a command confirmed never to have happened is retried under a new key, as a new
        // command, so the history of this one stays true.
        [CarrierCommandStatus.Succeeded]    = [],
        [CarrierCommandStatus.Refused]      = [],
        [CarrierCommandStatus.NotPerformed] = []
    };
}

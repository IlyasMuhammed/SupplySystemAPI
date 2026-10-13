using SMS.Modules.Crm.Domain;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Crm.Services;

/// <summary>A41 §6.3 / D-9 — the lead transition matrix (register 1F).</summary>
public interface ILeadStateMachineService
{
    /// <summary>Throws (400, or 409 for a converted lead) when <paramref name="target"/> is not reachable from <paramref name="current"/>.</summary>
    void ValidateTransition(string current, string target);
    IReadOnlyList<string> GetAllowedTransitions(string current);
}

internal sealed class LeadStateMachineService : ILeadStateMachineService
{
    public void ValidateTransition(string current, string target) => LeadStateMachine.Validate(current, target);
    public IReadOnlyList<string> GetAllowedTransitions(string current) => LeadStateMachine.Allowed(current);
}

internal static class LeadStateMachine
{
    private static readonly Dictionary<string, string[]> Matrix = new()
    {
        [LeadStatuses.New]          = [LeadStatuses.Contacted, LeadStatuses.Disqualified],
        [LeadStatuses.Contacted]    = [LeadStatuses.Qualified, LeadStatuses.Disqualified],
        [LeadStatuses.Qualified]    = [LeadStatuses.Converted, LeadStatuses.Disqualified],
        [LeadStatuses.Disqualified] = [LeadStatuses.New],
        [LeadStatuses.Converted]    = [],
    };

    public static IReadOnlyList<string> Allowed(string current) => Matrix.TryGetValue(current, out var next) ? next : [];

    public static bool CanMove(string current, string target) => Allowed(current).Contains(target);

    public static void Validate(string current, string target)
    {
        if (!LeadStatuses.All.Contains(target))
            throw new BadRequestException($"Status must be one of {string.Join(", ", LeadStatuses.All)}.");
        if (current == LeadStatuses.Converted)
            throw new ConflictException("A converted lead is closed: work on its opportunity instead.");
        if (current == target)
            throw new BadRequestException($"The lead is already {target}.");
        if (CanMove(current, target)) return;
        throw new BadRequestException((current, target) switch
        {
            (LeadStatuses.New, LeadStatuses.Qualified) => "A lead must be contacted before it can be qualified.",
            (_, LeadStatuses.Converted)                => "Only a qualified lead can be converted.",
            (LeadStatuses.Disqualified, _)             => "A disqualified lead can only be re-opened to New.",
            (_, LeadStatuses.New)                      => "Only a disqualified lead can be re-opened.",
            _                                          => $"A {current} lead cannot be moved to {target}."
        });
    }
}

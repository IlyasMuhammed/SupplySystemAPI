namespace SMS.Shared.Finance.Posting;

/// <summary>
/// A48 (AP, D-AP-12) — asks the posting side, before a document is created, whether the account roles its posting will
/// name are mapped for every <b>active</b> posting target that resolves roles to accounts (the internal GL). A target that
/// is not active, or that does not use roles (QuickBooks takes the document payload), never makes a role "missing".
/// Implemented by SMS.Modules.Accounting; hosts without it get <see cref="NullPostingRoleReadiness"/> (nothing missing).
/// </summary>
public interface IPostingRoleReadiness
{
    /// <summary>The roles of <paramref name="roles"/> (DEFAULT_* keys) an active target needs but cannot resolve, for the current organization.</summary>
    Task<IReadOnlyList<string>> MissingRolesAsync(IReadOnlyCollection<string> roles, CancellationToken ct = default);
}

public sealed class NullPostingRoleReadiness : IPostingRoleReadiness
{
    public Task<IReadOnlyList<string>> MissingRolesAsync(IReadOnlyCollection<string> roles, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

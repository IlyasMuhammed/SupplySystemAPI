namespace SMS.Shared.Finance.Posting;

// A44 D-16 — the A42 chart-of-accounts ↔ external-account mappings (finance.ExternalSystemMappings), readable and
// writable by the Integration module without referencing Accounting. Implemented by SMS.Modules.Accounting.

public static class ExternalSystemTypes
{
    public const string QuickBooksOnline = "QUICKBOOKS_ONLINE";
}

public sealed record ChartAccountRef(Guid AccountUuid, string Code, string Name, string? AccountType, bool IsActive);

public sealed record ExternalAccountMapEntry(Guid AccountUuid, string AccountCode, string AccountName, string ExternalAccountCode, string? ExternalAccountName);

public sealed class ExternalAccountMap
{
    /// <summary>Leaf account code → mapping.</summary>
    public IReadOnlyDictionary<string, ExternalAccountMapEntry> ByAccountCode { get; init; } =
        new Dictionary<string, ExternalAccountMapEntry>(StringComparer.OrdinalIgnoreCase);
    /// <summary>A42 default key (e.g. DEFAULT_REVENUE) → the external account of the account assigned to it.</summary>
    public IReadOnlyDictionary<string, string> ByDefaultKey { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record AccountMappingUpsert(Guid AccountUuid, string? ExternalAccountCode, string? ExternalAccountName, string? Note);

public interface IExternalAccountMapReader
{
    /// <summary>The ambient organization's mappings for <paramref name="systemType"/> (empty when the GL is not set up).</summary>
    Task<ExternalAccountMap> GetAsync(string systemType, CancellationToken ct = default);
    /// <summary>Active and inactive leaf accounts of the ambient organization's chart.</summary>
    Task<IReadOnlyList<ChartAccountRef>> ListLeafAccountsAsync(CancellationToken ct = default);
    /// <summary>Create/update (null ExternalAccountCode = delete) mappings; returns how many rows changed.</summary>
    Task<int> UpsertMappingsAsync(string systemType, IReadOnlyList<AccountMappingUpsert> rows, CancellationToken ct = default);
}

public sealed class NullExternalAccountMapReader : IExternalAccountMapReader
{
    public Task<ExternalAccountMap> GetAsync(string systemType, CancellationToken ct = default) => Task.FromResult(new ExternalAccountMap());
    public Task<IReadOnlyList<ChartAccountRef>> ListLeafAccountsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ChartAccountRef>>([]);
    public Task<int> UpsertMappingsAsync(string systemType, IReadOnlyList<AccountMappingUpsert> rows, CancellationToken ct = default) =>
        Task.FromResult(0);
}

namespace SMS.Modules.Integration.Core.Providers;

/// <summary>
/// Resolves an <see cref="IAccountingProvider"/> by its <see cref="IAccountingProvider.ProviderKey"/>
/// (case-insensitive). Two providers claiming the same key is a registration bug and fails at construction,
/// not on the first sync.
/// </summary>
internal sealed class AccountingProviderRegistry : IAccountingProviderRegistry
{
    private readonly Dictionary<string, IAccountingProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public AccountingProviderRegistry(IEnumerable<IAccountingProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        foreach (var provider in providers)
        {
            if (provider is null) continue;
            var key = provider.ProviderKey;
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException($"Accounting provider {provider.GetType().Name} has no ProviderKey.");
            if (!_providers.TryAdd(key, provider))
                throw new InvalidOperationException(
                    $"Two accounting providers are registered for key '{key}': " +
                    $"{_providers[key].GetType().Name} and {provider.GetType().Name}. Register each provider once.");
        }
    }

    public IReadOnlyCollection<string> Keys => _providers.Keys;

    public IAccountingProvider Get(string providerKey)
    {
        if (!string.IsNullOrWhiteSpace(providerKey) && _providers.TryGetValue(providerKey, out var provider))
            return provider;

        var known = _providers.Count == 0 ? "none" : string.Join(", ", _providers.Keys.Order(StringComparer.OrdinalIgnoreCase));
        throw new InvalidOperationException(
            $"No accounting provider is registered for key '{providerKey}'. Registered: {known}.");
    }
}

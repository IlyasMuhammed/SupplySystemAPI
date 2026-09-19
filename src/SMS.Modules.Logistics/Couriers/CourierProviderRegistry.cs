using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Couriers;

public interface ICourierProviderRegistry
{
    /// <summary>The provider for a key, or null when nothing claims it.</summary>
    ICourierProvider? Find(string? providerKey);

    /// <summary>
    /// The provider for a key, or an explanation naming the key and what is actually registered.
    /// </summary>
    ICourierProvider Require(string? providerKey);

    /// <summary>Every registered provider, ordered by key, for an admin screen to offer.</summary>
    IReadOnlyList<ICourierProvider> All { get; }
}

/// <summary>
/// Resolves a carrier's <c>ProviderKey</c> to the adapter that implements it.
/// <para>
/// Built once from everything registered in DI, so adding a carrier integration is a registration
/// and nothing else — no switch statement anywhere gains a case.
/// </para>
/// <para>
/// <b>Keys are matched case-insensitively but must be unique case-insensitively too.</b> Two
/// adapters registered as <c>DHL</c> and <c>dhl</c> would make resolution depend on registration
/// order, and the carrier rows pointing at them would work or not according to which assembly
/// loaded first. That is caught here, at startup, rather than at the first booking.
/// </para>
/// </summary>
internal sealed class CourierProviderRegistry : ICourierProviderRegistry
{
    private readonly Dictionary<string, ICourierProvider> _byKey;

    public CourierProviderRegistry(IEnumerable<ICourierProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _byKey = new Dictionary<string, ICourierProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Key))
                throw new InvalidOperationException(
                    $"{provider.GetType().Name} has no provider key. The key is what carrier rows " +
                    "are configured against, so an adapter without one can never be reached.");

            var key = provider.Key.Trim();

            if (_byKey.TryGetValue(key, out var existing))
                throw new InvalidOperationException(
                    $"Two courier providers claim the key '{key}': {existing.GetType().Name} and " +
                    $"{provider.GetType().Name}. Keys are matched case-insensitively, so which one " +
                    "won would depend on registration order.");

            _byKey[key] = provider;
        }

        All = [.. _byKey.Values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)];
    }

    public IReadOnlyList<ICourierProvider> All { get; }

    public ICourierProvider? Find(string? providerKey) =>
        string.IsNullOrWhiteSpace(providerKey)
            ? null
            : _byKey.GetValueOrDefault(providerKey.Trim());

    public ICourierProvider Require(string? providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
            throw new ConflictException(
                "This carrier has no courier provider configured, so there is no adapter to book " +
                "through. Set its provider, or set it to manual integration and key the airway " +
                "bill in by hand.");

        return Find(providerKey)
            ?? throw new ConflictException(
                $"No courier provider is registered for '{providerKey.Trim()}'. " +
                $"Registered: {(All.Count == 0 ? "none" : string.Join(", ", All.Select(p => p.Key)))}.");
    }
}

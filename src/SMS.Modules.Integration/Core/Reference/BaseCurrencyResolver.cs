using Microsoft.Extensions.DependencyInjection;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Core.Reference;

/// <param name="Configured">The organization has a base currency set (Tenancy's <c>Organization.BaseCurrency</c>).</param>
/// <param name="Code">Its ISO code, when this module can find it out.</param>
internal sealed record BaseCurrencyInfo(bool Configured, string? Code);

/// <summary>
/// SCM's base currency for an organization, as an ISO code — what preflight compares with QuickBooks'
/// home currency (plan D-1).
/// </summary>
internal interface IBaseCurrencyResolver
{
    Task<BaseCurrencyInfo> ResolveAsync(Guid organizationId, CancellationToken ct = default);
}

/// <summary>
/// Reads the base currency's id through <see cref="IOrganizationCurrencyService"/> (Tenancy) and turns it
/// into a code through <see cref="ICurrencyCodeLookup"/> (Lookups) — both SMS.Shared contracts, so this
/// module still references neither. Either may be absent in a host without those modules (tests);
/// the code is then null and preflight falls back to a warning to confirm by eye.
/// </summary>
internal sealed class BaseCurrencyResolver : IBaseCurrencyResolver
{
    private readonly IServiceProvider _services;

    public BaseCurrencyResolver(IServiceProvider services) => _services = services;

    public async Task<BaseCurrencyInfo> ResolveAsync(Guid organizationId, CancellationToken ct = default)
    {
        var currencies = _services.GetService<IOrganizationCurrencyService>();
        if (currencies is null) return new BaseCurrencyInfo(false, null);

        var id = await currencies.GetBaseCurrencyIdAsync(organizationId);
        if (id is null) return new BaseCurrencyInfo(false, null);

        var codes = _services.GetService<ICurrencyCodeLookup>();
        var code  = codes is null ? null : await codes.GetCodeAsync(id.Value, ct);
        return new BaseCurrencyInfo(true, code);
    }
}

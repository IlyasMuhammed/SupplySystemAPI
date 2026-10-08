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

    /// <summary>
    /// A35 D-19 — the organization's base currency for one domain (D-7). The sale base is <see cref="ResolveAsync(Guid, CancellationToken)"/>
    /// (Organization.BaseCurrency is kept equal to it); the default for any other domain is "not known", so a resolver that
    /// knows only one base never claims a purchase base it cannot see.
    /// </summary>
    Task<BaseCurrencyInfo> ResolveAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default) =>
        domain == TransactionDomain.Sale
            ? ResolveAsync(organizationId, ct)
            : Task.FromResult(new BaseCurrencyInfo(false, null));
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

    /// <summary>
    /// A35 D-7 — a domain's base through Finance's <see cref="ICurrencyService"/> (which applies the "no settings row reads
    /// as Organization.BaseCurrency ?? PKR" fallback). Without it (a host without Finance) the sale base is the old answer and
    /// any other domain is unknown.
    /// </summary>
    public async Task<BaseCurrencyInfo> ResolveAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default)
    {
        var currency = _services.GetService<ICurrencyService>();
        if (currency is null)
            return domain == TransactionDomain.Sale ? await ResolveAsync(organizationId, ct) : new BaseCurrencyInfo(false, null);

        var id    = await currency.GetBaseCurrencyIdAsync(organizationId, domain, ct);
        var codes = _services.GetService<ICurrencyCodeLookup>();
        var code  = codes is null ? null : await codes.GetCodeAsync(id, ct);
        return new BaseCurrencyInfo(true, code);
    }
}

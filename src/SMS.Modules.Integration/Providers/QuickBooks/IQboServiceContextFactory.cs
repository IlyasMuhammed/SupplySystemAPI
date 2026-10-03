using Intuit.Ipp.Core;
using SMS.Modules.Integration.Core.Providers;

namespace SMS.Modules.Integration.Providers.QuickBooks;

/// <summary>
/// Builds an SDK <see cref="ServiceContext"/> for one company, with a fresh access token (refreshed
/// first when close to expiry), the environment's base URL, the pinned minor version, and the SDK's
/// own request logging and retries switched off — logging would write tokens to disk, and SDK retries
/// would bypass the sync ledger and create duplicates.
/// </summary>
internal interface IQboServiceContextFactory
{
    /// <exception cref="Core.Connections.ConnectionUnavailableException">The connection cannot be used.</exception>
    Task<ServiceContext> CreateAsync(ProviderContext ctx, CancellationToken ct = default);
}

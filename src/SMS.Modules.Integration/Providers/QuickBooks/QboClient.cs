using Intuit.Ipp.Core;
using Intuit.Ipp.Data;
using Intuit.Ipp.DataService;
using Intuit.Ipp.QueryFilter;

namespace SMS.Modules.Integration.Providers.QuickBooks;

/// <summary>
/// The only SDK calls the provider makes, behind a seam so the provider can be exercised offline.
/// All methods are synchronous (the SDK is) and throw the SDK's own exceptions, which
/// <see cref="QboErrorTranslator"/> classifies.
/// <para>
/// Typed as <see cref="IEntity"/> because that is the SDK's constraint: every concrete entity (Customer,
/// Invoice, Preferences, …) implements it, but the <see cref="IntuitEntity"/> base class does not. The SDK
/// derives the REST resource from the entity's runtime type, so passing it as <see cref="IEntity"/> is exact.
/// </para>
/// </summary>
internal interface IQboClient
{
    /// <summary><c>DataService.Add</c> — POST /{entity}.</summary>
    IEntity Add(IEntity entity);

    /// <summary><c>DataService.Update</c> — POST /{entity}; sparse when the entity's <c>sparse</c> flag is set.</summary>
    IEntity Update(IEntity entity);

    /// <summary><c>DataService.FindById</c> — GET /{entity}/{id} (Preferences: GET /preferences). Null when QuickBooks returned nothing.</summary>
    IEntity? FindById(IEntity entity);

    /// <summary><c>DataService.Void</c> — POST /{entity}?operation=void. Invoice, SalesReceipt and Payment only.</summary>
    IEntity Void(IEntity entity);

    /// <summary><c>QueryService&lt;T&gt;.ExecuteIdsQuery</c> — POST /query with the query as the body.</summary>
    IReadOnlyList<T> Query<T>(string query) where T : class, IEntity;
}

internal interface IQboClientFactory
{
    IQboClient Create(ServiceContext context);
}

internal sealed class SdkQboClientFactory : IQboClientFactory
{
    public IQboClient Create(ServiceContext context) => new SdkQboClient(context);
}

/// <summary>
/// Thin pass-through to <see cref="DataService"/> and <see cref="QueryService{T}"/>. The DataService is
/// created lazily so a construction failure surfaces inside the provider's error handling.
/// </summary>
internal sealed class SdkQboClient : IQboClient
{
    private readonly ServiceContext _context;
    private DataService? _dataService;

    public SdkQboClient(ServiceContext context) => _context = context ?? throw new ArgumentNullException(nameof(context));

    private DataService Data => _dataService ??= new DataService(_context);

    public IEntity Add(IEntity entity) => Data.Add(entity);

    public IEntity Update(IEntity entity) => Data.Update(entity);

    public IEntity? FindById(IEntity entity) => Data.FindById(entity);

    public IEntity Void(IEntity entity) => Data.Void(entity);

    public IReadOnlyList<T> Query<T>(string query) where T : class, IEntity =>
        new QueryService<T>(_context).ExecuteIdsQuery(query);
}

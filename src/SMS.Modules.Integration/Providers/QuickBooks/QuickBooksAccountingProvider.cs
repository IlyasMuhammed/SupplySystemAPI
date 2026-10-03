using System.Diagnostics;
using Intuit.Ipp.Core;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using SMS.Modules.Integration.Core.Logging;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Providers.QuickBooks.Mapping;
using SMS.Shared.Integration.QuickBooks;
using Qbo = Intuit.Ipp.Data;

namespace SMS.Modules.Integration.Providers.QuickBooks;

/// <summary>
/// <see cref="IAccountingProvider"/> for QuickBooks Online through Intuit's .NET SDK — the only place
/// provider-neutral Remote* objects become SDK calls.
/// <para>Every call:</para>
/// <list type="number">
/// <item>gets a <see cref="ServiceContext"/> from <see cref="IQboServiceContextFactory"/> (a connection that
/// cannot be used comes back as <see cref="ProviderOutcomeKind.AuthRevoked"/>, not an exception);</item>
/// <item>honours the cancellation token up to the moment the (synchronous, uncancellable) SDK call starts;</item>
/// <item>times the SDK call into <c>DurationMs</c>;</item>
/// <item>logs the outgoing SDK object and the response as JSON, both passed through <see cref="Redactor"/>;</item>
/// <item>turns every SDK / network exception into a result via <see cref="QboErrorTranslator"/>.</item>
/// </list>
/// It throws only for caller mistakes (null / blank arguments, an unsupported kind) and for the
/// caller's own cancellation.
/// </summary>
internal sealed class QuickBooksAccountingProvider : IAccountingProvider
{
    public const string ProductionAppBaseUrl = "https://app.qbo.intuit.com";
    public const string SandboxAppBaseUrl    = "https://app.sandbox.qbo.intuit.com";

    /// <summary>Safety stop for reference-data paging (1000 rows a page).</summary>
    public const int MaxReferencePages = 50;

    private readonly IQboServiceContextFactory _contexts;
    private readonly IQboClientFactory _clients;
    private readonly QboQueryBuilder _queries;
    private readonly QboErrorTranslator _errors;
    private readonly ILogger<QuickBooksAccountingProvider> _logger;

    public QuickBooksAccountingProvider(
        IQboServiceContextFactory contexts,
        IQboClientFactory clients,
        QboQueryBuilder queries,
        QboErrorTranslator errors,
        ILogger<QuickBooksAccountingProvider> logger)
    {
        _contexts = contexts;
        _clients  = clients;
        _queries  = queries;
        _errors   = errors;
        _logger   = logger;
    }

    public string ProviderKey => ProviderKeys.QuickBooksOnline;

    // ── Writes ──────────────────────────────────────────────────────────────────────────────

    public Task<ProviderResult<RemoteRecord>> CreateAsync(ProviderContext ctx, RemoteEntity entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var sdk = QboEntityMapper.ToSdk(entity);
        return WriteAsync(ctx, entity.Kind, sdk, QboOperation.Create, (client, e) => client.Add(e), ct);
    }

    /// <summary>Sparse: Id + SyncToken + <c>sparse = true</c>, and only the fields that have values.</summary>
    public Task<ProviderResult<RemoteRecord>> UpdateAsync(
        ProviderContext ctx, RemoteEntity entity, string remoteId, string syncToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var sdk = QboMap.AsSparseUpdate(QboEntityMapper.ToSdk(entity), remoteId, syncToken);
        return WriteAsync(ctx, entity.Kind, sdk, QboOperation.Update, (client, e) => client.Update(e), ct);
    }

    /// <summary>POST invoice?operation=void with Id + SyncToken. The returned record is marked inactive.</summary>
    public Task<ProviderResult<RemoteRecord>> VoidInvoiceAsync(ProviderContext ctx, string remoteId, string syncToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(syncToken);

        var invoice = new Qbo.Invoice { Id = remoteId, SyncToken = syncToken };
        return ExecuteAsync(ctx, QboOperation.Void, QboJson.Entity(invoice), (client, _) =>
        {
            var voided = client.Void(invoice);
            var record = QboEntityMapper.ToRecord(SyncKind.SalesInvoice, voided) with { Active = false };
            return Reply<RemoteRecord>.Ok(record, QboJson.Entity(voided));
        }, ct);
    }

    private Task<ProviderResult<RemoteRecord>> WriteAsync(
        ProviderContext ctx, SyncKind kind, Qbo.IntuitEntity sdk, QboOperation operation,
        Func<IQboClient, Qbo.IEntity, Qbo.IEntity?> write, CancellationToken ct) =>
        ExecuteAsync(ctx, operation, QboJson.Entity(sdk), (client, _) =>
        {
            var saved = write(client, QboEntityMapper.AsEntity(sdk));
            return Reply<RemoteRecord>.Ok(QboEntityMapper.ToRecord(kind, saved), QboJson.Entity(saved));
        }, ct);

    // ── Reads ───────────────────────────────────────────────────────────────────────────────

    public Task<ProviderResult<RemoteRecord>> GetByIdAsync(ProviderContext ctx, SyncKind kind, string remoteId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);

        var probe = QboEntityMapper.NewOfKind(kind);
        probe.Id = remoteId;
        var entityName = probe.GetType().Name;

        return ExecuteAsync(ctx, QboOperation.Read, QboJson.FindById(entityName, remoteId), (client, _) =>
        {
            var found = client.FindById(QboEntityMapper.AsEntity(probe));
            return found is null
                ? Reply<RemoteRecord>.Fail(ProviderOutcomeKind.NotFound, QboErrorTranslator.ObjectNotFound,
                    $"QuickBooks returned no {entityName} with Id {remoteId}.", null)
                : Reply<RemoteRecord>.Ok(QboEntityMapper.ToRecord(kind, found), QboJson.Entity(found));
        }, ct);
    }

    /// <summary>
    /// Customer / Vendor by DisplayName, Item by Name (else Sku), invoice by DocNumber, bill by DocNumber and —
    /// when given — vendor (filtered on the results). Inactive name-list records are included. When several
    /// match, an active one wins. Null value when nothing matches.
    /// </summary>
    /// <exception cref="ArgumentException">The lookup carries nothing to search by for this kind.</exception>
    public Task<ProviderResult<RemoteRecord?>> FindAsync(ProviderContext ctx, SyncKind kind, RemoteLookup lookup, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var query = BuildFindQuery(kind, lookup);

        return ExecuteAsync(ctx, QboOperation.Query, QboJson.Query(query), (client, _) =>
        {
            IReadOnlyList<Qbo.IntuitEntity> returned = kind switch
            {
                SyncKind.Customer     => client.Query<Qbo.Customer>(query),
                SyncKind.Vendor       => client.Query<Qbo.Vendor>(query),
                SyncKind.Item         => client.Query<Qbo.Item>(query),
                SyncKind.SalesInvoice => client.Query<Qbo.Invoice>(query),
                SyncKind.Bill         => client.Query<Qbo.Bill>(query),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown sync kind.")
            };

            IEnumerable<Qbo.IntuitEntity> candidates = returned;
            if (kind == SyncKind.Bill && QboMap.Has(lookup.VendorRemoteId))
                candidates = returned.OfType<Qbo.Bill>()
                    .Where(b => string.Equals(b.VendorRef?.Value, lookup.VendorRemoteId, StringComparison.Ordinal));

            var match = candidates
                .Select(r => QboEntityMapper.ToRecord(kind, r))
                .OrderByDescending(r => r.Active)
                .FirstOrDefault();
            return Reply<RemoteRecord?>.Ok(match, QboJson.List(returned));
        }, ct);
    }

    private string BuildFindQuery(SyncKind kind, RemoteLookup lookup)
    {
        switch (kind)
        {
            case SyncKind.Customer:
            case SyncKind.Vendor:
                return QboMap.Has(lookup.Name)
                    ? _queries.FindByName(kind, lookup.Name!)
                    : throw new ArgumentException($"A {kind} is found by display name; the lookup has none.", nameof(lookup));
            case SyncKind.Item:
                if (QboMap.Has(lookup.Name)) return _queries.FindByName(kind, lookup.Name!);
                if (QboMap.Has(lookup.Sku)) return _queries.FindItemBySku(lookup.Sku!);
                throw new ArgumentException("An item is found by name (or SKU); the lookup has neither.", nameof(lookup));
            case SyncKind.SalesInvoice:
                return QboMap.Has(lookup.DocNumber)
                    ? _queries.FindInvoiceByDocNumber(lookup.DocNumber!)
                    : throw new ArgumentException("An invoice is found by document number; the lookup has none.", nameof(lookup));
            case SyncKind.Bill:
                return QboMap.Has(lookup.DocNumber)
                    ? _queries.FindBillByDocNumber(lookup.DocNumber!)
                    : throw new ArgumentException("A bill is found by document number; the lookup has none.", nameof(lookup));
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown sync kind.");
        }
    }

    /// <summary>One page of Customers / Vendors / Items (active and inactive). maxResults is capped at 1000.</summary>
    public Task<ProviderResult<IReadOnlyList<RemoteListEntry>>> ListAsync(
        ProviderContext ctx, SyncKind kind, int startPosition, int maxResults, CancellationToken ct = default)
    {
        var query = _queries.ListPage(kind, startPosition, maxResults);

        return ExecuteAsync<IReadOnlyList<RemoteListEntry>>(ctx, QboOperation.Query, QboJson.Query(query), (client, _) =>
        {
            switch (kind)
            {
                case SyncKind.Customer:
                {
                    var rows = client.Query<Qbo.Customer>(query);
                    return Reply<IReadOnlyList<RemoteListEntry>>.Ok(rows.Select(QboCustomerMapper.ToListEntry).ToList(), QboJson.List(rows));
                }
                case SyncKind.Vendor:
                {
                    var rows = client.Query<Qbo.Vendor>(query);
                    return Reply<IReadOnlyList<RemoteListEntry>>.Ok(rows.Select(QboVendorMapper.ToListEntry).ToList(), QboJson.List(rows));
                }
                default:
                {
                    var rows = client.Query<Qbo.Item>(query);
                    return Reply<IReadOnlyList<RemoteListEntry>>.Ok(rows.Select(QboItemMapper.ToListEntry).ToList(), QboJson.List(rows));
                }
            }
        }, ct);
    }

    public Task<ProviderResult<RemoteCompanyInfo>> GetCompanyInfoAsync(ProviderContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var probe = new Qbo.CompanyInfo { Id = ctx.RealmId };

        return ExecuteAsync(ctx, QboOperation.Read, QboJson.FindById(nameof(Qbo.CompanyInfo), ctx.RealmId), (client, _) =>
        {
            var info = client.FindById(probe) as Qbo.CompanyInfo
                ?? throw new InvalidOperationException("QuickBooks returned no CompanyInfo.");
            return Reply<RemoteCompanyInfo>.Ok(QboReferenceMapper.ToCompanyInfo(info), QboJson.Entity(info));
        }, ct);
    }

    /// <summary>
    /// Preferences, CompanyInfo, Accounts, TaxCodes and Terms are required — a failure of any fails the call
    /// (the message names the part). Tax rates (only used to fill a tax code's RatePercent) and currencies
    /// (only read when multicurrency is on) are optional: a failure there is logged and leaves them empty.
    /// </summary>
    public Task<ProviderResult<RemoteReferenceData>> GetReferenceDataAsync(ProviderContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var page1 = new Func<string, string>(entity => _queries.SelectPage(entity, 1, QboQueryBuilder.MaxResultsCap));
        var request = QboJson.Describe(new JObject
        {
            ["operations"] = new JArray(
                "GET preferences",
                $"GET companyinfo/{ctx.RealmId}",
                page1("Account"),
                page1("TaxCode"),
                page1("Term"),
                page1("TaxRate") + " (optional)",
                page1("CompanyCurrency") + " (optional, only when multicurrency is on)")
        });

        return ExecuteAsync(ctx, QboOperation.Query, request, (client, token) =>
        {
            var prefs = Step("Preferences", token, () =>
                client.FindById(new Qbo.Preferences()) as Qbo.Preferences
                ?? throw new InvalidOperationException("QuickBooks returned no Preferences."));
            var company = Step("CompanyInfo", token, () =>
                client.FindById(new Qbo.CompanyInfo { Id = ctx.RealmId }) as Qbo.CompanyInfo
                ?? throw new InvalidOperationException("QuickBooks returned no CompanyInfo."));
            var accounts = Step("Accounts", token, () => QueryAll<Qbo.Account>(client, "Account", token));
            var taxCodes = Step("TaxCodes", token, () => QueryAll<Qbo.TaxCode>(client, "TaxCode", token));
            var terms    = Step("Terms", token, () => QueryAll<Qbo.Term>(client, "Term", token));

            var preferences = QboReferenceMapper.ToPreferences(prefs);

            JToken ratesNote;
            IReadOnlyDictionary<string, decimal> rates;
            try
            {
                var taxRates = QueryAll<Qbo.TaxRate>(client, "TaxRate", token);
                rates     = QboReferenceMapper.ToRateLookup(taxRates);
                ratesNote = taxRates.Count;
            }
            catch (Exception ex) when (!IsCallerCancellation(ex, token))
            {
                rates     = new Dictionary<string, decimal>();
                ratesNote = OptionalFailed(ctx, "tax rates", ex);
            }

            JToken currenciesNote = "skipped: multicurrency is off";
            IReadOnlyList<RemoteCurrency> currencies = [];
            if (preferences.MultiCurrencyEnabled)
            {
                try
                {
                    var rows = QueryAll<Qbo.CompanyCurrency>(client, "CompanyCurrency", token);
                    currencies     = rows.Where(QboReferenceMapper.IsActive).Select(QboReferenceMapper.ToCurrency).ToList();
                    currenciesNote = rows.Count;
                }
                catch (Exception ex) when (!IsCallerCancellation(ex, token))
                {
                    currenciesNote = OptionalFailed(ctx, "currencies", ex);
                }
            }

            var data = new RemoteReferenceData
            {
                Accounts    = accounts.Select(QboReferenceMapper.ToAccount).ToList(),
                TaxCodes    = taxCodes.Select(code => QboReferenceMapper.ToTaxCode(code, rates)).ToList(),
                Terms       = terms.Select(QboReferenceMapper.ToTerm).ToList(),
                Currencies  = currencies,
                Preferences = preferences,
                CompanyInfo = QboReferenceMapper.ToCompanyInfo(company)
            };

            var response = QboJson.Describe(new JObject
            {
                ["accounts"]    = accounts.Count,
                ["taxCodes"]    = taxCodes.Count,
                ["terms"]       = terms.Count,
                ["taxRates"]    = ratesNote,
                ["currencies"]  = currenciesNote,
                ["preferences"] = new JRaw(QboJson.Entity(prefs) ?? "null"),
                ["companyInfo"] = new JRaw(QboJson.Entity(company) ?? "null")
            });
            return Reply<RemoteReferenceData>.Ok(data, response);
        }, ct);
    }

    private List<T> QueryAll<T>(IQboClient client, string entityName, CancellationToken ct) where T : class, Qbo.IEntity
    {
        var all = new List<T>();
        var start = 1;
        for (var page = 0; page < MaxReferencePages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var rows = client.Query<T>(_queries.SelectPage(entityName, start, QboQueryBuilder.MaxResultsCap));
            all.AddRange(rows);
            if (rows.Count < QboQueryBuilder.MaxResultsCap) break;
            start += QboQueryBuilder.MaxResultsCap;
        }
        return all;
    }

    private JToken OptionalFailed(ProviderContext ctx, string part, Exception ex)
    {
        var failure = _errors.Classify(ex, QboOperation.Query);
        _logger.LogWarning(
            "QuickBooks reference data for connection {ConnectionId}: reading {Part} failed ({Outcome} {Code}); continuing without it.",
            ctx.ConnectionId, part, failure.Outcome, failure.Code);
        return $"failed: {failure.Outcome} {failure.Code}";
    }

    private static T Step<T>(string part, CancellationToken ct, Func<T> read)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return read();
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            throw new ReferencePartException(part, ex);
        }
    }

    /// <summary>Carries which required part of the reference data failed, so the message can say so.</summary>
    private sealed class ReferencePartException : Exception
    {
        public string Part { get; }
        public ReferencePartException(string part, Exception inner) : base($"Reading {part} failed.", inner) => Part = part;
    }

    // ── Deep links ──────────────────────────────────────────────────────────────────────────

    public string? BuildDeepLink(IntegrationEnvironment environment, SyncKind kind, string remoteId)
    {
        if (string.IsNullOrWhiteSpace(remoteId)) return null;

        var path = kind switch
        {
            SyncKind.Customer     => "/app/customerdetail?nameId=",
            SyncKind.Vendor       => "/app/vendordetail?nameId=",
            SyncKind.SalesInvoice => "/app/invoice?txnId=",
            SyncKind.Bill         => "/app/bill?txnId=",
            _                     => null   // Items have no detail page.
        };
        if (path is null) return null;

        var baseUrl = environment == IntegrationEnvironment.Production ? ProductionAppBaseUrl : SandboxAppBaseUrl;
        return baseUrl + path + Uri.EscapeDataString(remoteId.Trim());
    }

    // ── The one place calls are made ────────────────────────────────────────────────────────

    private readonly record struct Reply<T>(ProviderOutcomeKind Outcome, T? Value, string? ResponseJson, string? Code, string? Message)
    {
        public static Reply<T> Ok(T value, string? responseJson) => new(ProviderOutcomeKind.Succeeded, value, responseJson, null, null);

        public static Reply<T> Fail(ProviderOutcomeKind outcome, string code, string message, string? responseJson) =>
            new(outcome, default, responseJson, code, message);
    }

    private async Task<ProviderResult<T>> ExecuteAsync<T>(
        ProviderContext ctx, QboOperation operation, string? requestJson,
        Func<IQboClient, CancellationToken, Reply<T>> call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();

        var request = Redactor.Redact(requestJson);

        ServiceContext context;
        try
        {
            context = await _contexts.CreateAsync(ctx, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            // Nothing reached QuickBooks. A ConnectionUnavailableException becomes AuthRevoked; a failed
            // token refresh (network) stays Transient.
            return Finish(ctx, operation, _errors.Translate<T>(ex, operation), request, durationMs: 0, ex);
        }

        ct.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var client = _clients.Create(context);
            var reply  = call(client, ct);
            stopwatch.Stop();

            var result = new ProviderResult<T>
            {
                Outcome      = reply.Outcome,
                Value        = reply.Value,
                ErrorCode    = reply.Code,
                Message      = reply.Message,
                RequestJson  = request,
                ResponseJson = Redactor.Redact(reply.ResponseJson),
                DurationMs   = Milliseconds(stopwatch)
            };
            if (!result.IsSuccess) Log(ctx, operation, result, null);
            return result;
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            stopwatch.Stop();
            if (ex is ReferencePartException part)
            {
                var inner = _errors.Translate<T>(part.InnerException!, operation);
                return Finish(ctx, operation, inner, request, Milliseconds(stopwatch), part.InnerException, $"{part.Part}: ");
            }
            return Finish(ctx, operation, _errors.Translate<T>(ex, operation), request, Milliseconds(stopwatch), ex);
        }
    }

    private ProviderResult<T> Finish<T>(
        ProviderContext ctx, QboOperation operation, ProviderResult<T> failure, string? request, int durationMs,
        Exception? exception, string? messagePrefix = null)
    {
        var result = new ProviderResult<T>
        {
            Outcome      = failure.Outcome,
            Value        = failure.Value,
            ErrorCode    = failure.ErrorCode,
            ErrorField   = failure.ErrorField,
            Message      = messagePrefix is null ? failure.Message : Truncate(messagePrefix + failure.Message, QboErrorTranslator.MaxMessageLength),
            IntuitTid    = failure.IntuitTid,
            RequestJson  = request,
            ResponseJson = failure.ResponseJson,
            DurationMs   = durationMs
        };
        Log(ctx, operation, result, exception);
        return result;
    }

    private void Log<T>(ProviderContext ctx, QboOperation operation, ProviderResult<T> result, Exception? exception)
    {
        // Codes and ids only — payloads can carry personal data and live in the (redacted) sync log instead.
        var unexpected = result.Outcome == ProviderOutcomeKind.Transient && exception is not null
                         && exception is not Intuit.Ipp.Exception.IdsException;
        _logger.Log(unexpected ? LogLevel.Warning : LogLevel.Information, unexpected ? exception : null,
            "QuickBooks {Operation} for connection {ConnectionId} (realm {RealmId}): {Outcome} {Code} field={Field} tid={IntuitTid} in {DurationMs} ms",
            operation, ctx.ConnectionId, ctx.RealmId, result.Outcome, result.ErrorCode, result.ErrorField, result.IntuitTid, result.DurationMs);
    }

    private static bool IsCallerCancellation(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && ct.IsCancellationRequested;

    private static int Milliseconds(Stopwatch stopwatch) =>
        (int)Math.Min(int.MaxValue, Math.Max(0, stopwatch.ElapsedMilliseconds));

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

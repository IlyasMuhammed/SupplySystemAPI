using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Repositories;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

public sealed record RevaluationSkip(string DocumentType, string? DocumentNo, string Reason);
public sealed record RevaluationTotal(string BaseCurrencyCode, decimal Gain, decimal Loss, decimal Net);

/// <summary>API-CONTRACT §7.2 — what one revaluation run did.</summary>
public sealed record ExchangeRevaluationResult(
    DateOnly RevaluationDate,
    int ReceivablesRevalued,
    int PayablesRevalued,
    int RowsWritten,
    int RowsReplaced,
    IReadOnlyList<RevaluationTotal> Totals,
    IReadOnlyList<RevaluationSkip> Skipped);

public interface IExchangeRevaluationService
{
    /// <summary>Revalues the organization's open foreign-currency receivables and payables on <paramref name="revaluationDate"/>.</summary>
    Task<ExchangeRevaluationResult> RunAsync(Guid organizationId, DateOnly revaluationDate, int? userId, CancellationToken ct = default);
}

/// <summary>
/// A35 C7 §8.2 / BR-C7-04 / D-15 — unrealized exchange differences. Each open foreign-currency document is revalued at the rate
/// on the revaluation date against the rate it was booked (locked) at, on what is still open, <b>into its own stored base</b>
/// (never the organization's current base): one UNREALIZED row per document per date.
/// <list type="bullet">
/// <item><b>Receivables</b>: issued sales invoices (ISSUED, PARTIALLY_PAID, OVERDUE) with a balance due.</item>
/// <item><b>Payables</b>: approved supplier invoices with TotalAmount − PaidAmount still open.</item>
/// </list>
/// Same-currency documents are skipped (BR-C7-05), as are documents never locked (no rate). A document whose currency has
/// no rate on the date is listed as skipped and the run goes on. <b>Idempotent</b> per (organization, date): the date's
/// earlier rows are replaced, under a per-(organization, date) application lock and one transaction on SQL Server.
/// Explicit organization throughout (query filters off): safe from a Hangfire job and for a super admin.
/// </summary>
internal sealed class ExchangeRevaluationService : IExchangeRevaluationService
{
    private static readonly string[] OpenReceivable =
        [SalesInvoiceStatuses.Issued, SalesInvoiceStatuses.PartiallyPaid, SalesInvoiceStatuses.Overdue];

    private readonly FinanceDbContext         _db;
    private readonly ICurrencyService         _currency;
    private readonly ExchangeDifferenceWriter _writer;
    private readonly IOrgCurrencyLookup?      _orgCurrencies;
    private readonly ICurrencyCodeLookup?     _codes;
    private readonly TimeProvider             _clock;

    public ExchangeRevaluationService(
        FinanceDbContext db, ICurrencyService currency, ExchangeDifferenceWriter writer,
        IOrgCurrencyLookup? orgCurrencies = null, ICurrencyCodeLookup? codes = null, TimeProvider? clock = null)
    {
        _db            = db;
        _currency      = currency;
        _writer        = writer;
        _orgCurrencies = orgCurrencies;
        _codes         = codes;
        _clock         = clock ?? TimeProvider.System;
    }

    private sealed record Open(
        string Side, string DocumentType, int Id, Guid Uuid, string No, Guid? PartnerId,
        Guid CurrencyId, string CurrencyCode, decimal Outstanding, decimal BookedRate, Guid BaseCurrencyId, string? BaseCurrencyCode);

    public async Task<ExchangeRevaluationResult> RunAsync(Guid organizationId, DateOnly revaluationDate, int? userId, CancellationToken ct = default)
    {
        var receivables = await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == organizationId && !i.IsDelete && OpenReceivable.Contains(i.Status) && i.BalanceDue > 0m
                     && i.ExchangeRate != null && i.CurrencyId != null && i.BaseCurrencyId != null && i.CurrencyId != i.BaseCurrencyId)
            .Select(i => new Open(ExchangeDifferenceSides.Receivable, ExchangeDifferenceRefs.SalesInvoice, i.Id, i.UUID, i.InvoiceNumber,
                i.PartnerId, i.CurrencyId!.Value, i.CurrencyCode, i.BalanceDue, i.ExchangeRate!.Value, i.BaseCurrencyId!.Value, i.BaseCurrencyCode))
            .ToListAsync(ct);

        var payables = await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == organizationId && !i.IsDelete && i.MatchStatus == InvoiceMatchStatus.Approved
                     && i.TotalAmount - i.PaidAmount > 0m
                     && i.ExchangeRate != null && i.CurrencyId != null && i.BaseCurrencyId != null && i.CurrencyId != i.BaseCurrencyId)
            .Select(i => new Open(ExchangeDifferenceSides.Payable, ExchangeDifferenceRefs.SupplierInvoice, i.Id, i.UUID, i.InvoiceNumber,
                i.SupplierId, i.CurrencyId!.Value, i.Currency, i.TotalAmount - i.PaidAmount, i.ExchangeRate!.Value, i.BaseCurrencyId!.Value, i.BaseCurrencyCode))
            .ToListAsync(ct);

        var accounts = await _writer.AccountsAsync(organizationId, realized: false, ct);
        var now      = _clock.GetUtcNow().UtcDateTime;
        var rows     = new List<ExchangeDifference>();
        var skipped  = new List<RevaluationSkip>();
        int revaluedReceivables = 0, revaluedPayables = 0;
        var rates    = new Dictionary<(Guid, Guid), decimal>();
        var decimals = new Dictionary<Guid, int>();

        foreach (var doc in receivables.Concat(payables))
        {
            decimal rate;
            try
            {
                if (!rates.TryGetValue((doc.CurrencyId, doc.BaseCurrencyId), out rate))
                {
                    // Into the document's OWN base, whatever the organization's base is today.
                    rate = (await _currency.ConvertAsync(organizationId, 1m, doc.CurrencyId, doc.BaseCurrencyId, revaluationDate, ct)).RateUsed;
                    rates[(doc.CurrencyId, doc.BaseCurrencyId)] = rate;
                }
            }
            catch (CurrencyRateNotFoundException ex)
            {
                skipped.Add(new RevaluationSkip(doc.DocumentType, doc.No, ex.Message));
                continue;
            }

            if (!decimals.TryGetValue(doc.BaseCurrencyId, out var dp))
                decimals[doc.BaseCurrencyId] = dp = _orgCurrencies is null
                    ? CurrencyConventions.DefaultDecimalPlaces
                    : await _orgCurrencies.GetDecimalPlacesAsync(organizationId, doc.BaseCurrencyId, ct);

            if (doc.Side == ExchangeDifferenceSides.Receivable) revaluedReceivables++; else revaluedPayables++;

            var amounts = ExchangeDifferenceMath.Compute(doc.Outstanding, doc.BookedRate, rate, dp, doc.Side);
            if (amounts.Difference == 0m) continue;

            rows.Add(new ExchangeDifference
            {
                Uuid = Guid.NewGuid(), OrganizationId = organizationId,
                Kind = ExchangeDifferenceKinds.Unrealized, Side = doc.Side,
                DocumentType = doc.DocumentType, DocumentId = doc.Id, DocumentUuid = doc.Uuid, DocumentNo = doc.No,
                PartnerId = doc.PartnerId,
                CurrencyId = doc.CurrencyId, CurrencyCode = doc.CurrencyCode, AmountCurrency = doc.Outstanding,
                BookedRate = doc.BookedRate, SettlementRate = rate,
                BaseCurrencyId = doc.BaseCurrencyId,
                BaseCurrencyCode = doc.BaseCurrencyCode ?? (_codes is null ? null : await _codes.GetCodeAsync(doc.BaseCurrencyId, ct)) ?? string.Empty,
                BookedAmountBase = amounts.BookedBase, SettledAmountBase = amounts.SettledBase, DifferenceBase = amounts.Difference,
                AccountCode = amounts.IsGain ? accounts.Gain : accounts.Loss,
                PostedAt = now, RevaluationDate = revaluationDate, CreatedBy = userId, CreatedDate = now
            });
        }

        var replaced = await InvoiceRowLocks.InTransactionAsync(_db, async () =>
        {
            if (_db.Database.IsRelational())
                await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"EXEC sp_getapplock @Resource = {$"fx-revaluation:{organizationId}:{revaluationDate:yyyy-MM-dd}"}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 60000", ct);

            var previous = await _db.ExchangeDifferences.IgnoreQueryFilters()
                .Where(d => d.OrganizationId == organizationId && d.Kind == ExchangeDifferenceKinds.Unrealized && d.RevaluationDate == revaluationDate)
                .ToListAsync(ct);
            _db.ExchangeDifferences.RemoveRange(previous);
            await _db.SaveChangesAsync(ct);

            _db.ExchangeDifferences.AddRange(rows);
            await _db.SaveChangesAsync(ct);
            return previous.Count;
        });

        var totals = rows.GroupBy(r => r.BaseCurrencyCode)
            .Select(g => new RevaluationTotal(g.Key,
                g.Where(r => r.DifferenceBase > 0m).Sum(r => r.DifferenceBase),
                g.Where(r => r.DifferenceBase < 0m).Sum(r => r.DifferenceBase),
                g.Sum(r => r.DifferenceBase)))
            .OrderBy(t => t.BaseCurrencyCode, StringComparer.Ordinal)
            .ToList();

        return new ExchangeRevaluationResult(revaluationDate, revaluedReceivables, revaluedPayables, rows.Count, replaced, totals, skipped);
    }
}

/// <summary>
/// A35 P4-02 — the monthly revaluation for every organization (last day of the month, 23:00 UTC), as of that day. A bare
/// recurring job has no tenant: each organization runs under <see cref="HangfireTenantScope"/>, explicitly. One failing
/// organization is logged and does not stop the others.
/// </summary>
internal sealed class ExchangeRevaluationJob
{
    internal const string RecurringJobId = "finance-exchange-revaluation-monthly";

    private readonly IOrganizationDirectory?      _organizations;
    private readonly IExchangeRevaluationService  _revaluation;
    private readonly ILogger<ExchangeRevaluationJob> _log;
    private readonly TimeProvider                 _clock;
    private readonly IModuleGate?                 _gate;

    public ExchangeRevaluationJob(
        IExchangeRevaluationService revaluation, ILogger<ExchangeRevaluationJob> log,
        IOrganizationDirectory? organizations = null, TimeProvider? clock = null, IModuleGate? gate = null)
    {
        _gate          = gate;
        _revaluation   = revaluation;
        _log           = log;
        _organizations = organizations;
        _clock         = clock ?? TimeProvider.System;
    }

    /// <summary>Last day of every month at 23:00 UTC (Cronos "L").</summary>
    internal static void Schedule() =>
        RecurringJob.AddOrUpdate<ExchangeRevaluationJob>(RecurringJobId, job => job.RunAsync(), "0 23 L * *");

    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync()
    {
        if (_organizations is null) return;

        var date = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var organizations = await _organizations.GetOrganizationIdsAsync();
        // A37 D-9 — not for organizations without MODULE_FINANCE (grace counts as off).
        var skipped = await _gate.SkippedAmongAsync(organizations, ModuleCodes.Finance, _log, nameof(ExchangeRevaluationJob));
        foreach (var org in organizations.Where(o => !skipped.Contains(o)))
        {
            HangfireTenantScope.OrganizationId = org;
            try
            {
                var result = await _revaluation.RunAsync(org, date, userId: null);
                if (result.RowsWritten > 0 || result.Skipped.Count > 0)
                    _log.LogInformation(
                        "Exchange revaluation {Date} for organization {Org}: {Rows} row(s), {Skipped} document(s) skipped.",
                        date, org, result.RowsWritten, result.Skipped.Count);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Exchange revaluation {Date} failed for organization {Org}.", date, org);
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }
    }
}

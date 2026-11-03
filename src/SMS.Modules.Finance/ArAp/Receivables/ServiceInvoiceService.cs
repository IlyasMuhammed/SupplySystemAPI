using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.ArAp.Receivables;

// A49 E-13 (A-14, §11A.5, API-CONTRACT §10.9): income for an organization with no sales document. A DRAFT sales invoice of non-stock lines,
// no sale order, no delivery. From there it is an ordinary invoice: issued through api/sales-invoices/{uuid}/issue (currency lock, customer
// ledger, A44 posting to DEFAULT_REVENUE), paid, aged and dunned like any other.

public sealed class CreateServiceInvoiceRequest
{
    public Guid CustomerUuid { get; set; }
    public DateOnly? InvoiceDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public string? CurrencyCode { get; set; }
    public string? Notes { get; set; }
    public List<ServiceInvoiceLineRequest> Lines { get; set; } = [];
}

public sealed class ServiceInvoiceLineRequest
{
    public string? Description { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    /// <summary>A leaf Revenue / OtherIncome account of the chart. Null: DEFAULT_REVENUE.</summary>
    public string? IncomeAccountCode { get; set; }
    public string? TaxCode { get; set; }
}

public interface IServiceInvoiceService
{
    Task<SalesInvoiceDetailModel> CreateAsync(CreateServiceInvoiceRequest request, int userId, CancellationToken ct = default);
}

internal sealed class ServiceInvoiceService : IServiceInvoiceService
{
    private const int MaxAttempts = 5;
    private const string NumberPrefix = "SINV";
    private const int MaxDescription = 300;

    private readonly FinanceDbContext _db;
    private readonly ISalesInvoiceService _invoices;
    private readonly ArDocumentCurrency _currency;
    private readonly IPartnerLookupService? _partners;
    private readonly TimeProvider _clock;
    private readonly IExternalAccountMapReader? _accounts;

    public ServiceInvoiceService(FinanceDbContext db, ISalesInvoiceService invoices, ArDocumentCurrency currency,
        IPartnerLookupService? partners = null, TimeProvider? clock = null, IExternalAccountMapReader? accounts = null)
    {
        _accounts = accounts;
        _db = db;
        _invoices = invoices;
        _currency = currency;
        _partners = partners;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<SalesInvoiceDetailModel> CreateAsync(CreateServiceInvoiceRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var org = _db.TenantContext.OrganizationId;
        var now = _clock.GetUtcNow().UtcDateTime;

        if (request.Lines is not { Count: > 0 }) throw new BadRequestException("A service invoice needs at least one line.");
        foreach (var line in request.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Description)) throw new BadRequestException("Every line needs a description.");
            if (line.Quantity <= 0) throw new BadRequestException($"'{line.Description}': the quantity must be more than zero.");
            if (line.UnitPrice < 0) throw new BadRequestException($"'{line.Description}': the unit price cannot be negative.");
        }
        await CheckIncomeAccountsAsync(request.Lines, ct);

        var partner = _partners is null ? null : await _partners.GetAsync(org, request.CustomerUuid, PartnerRoles.Customer, ct);
        if (partner is null) throw new NotFoundException("Customer", request.CustomerUuid);

        var invoiceDate = (request.InvoiceDate?.ToDateTime(TimeOnly.MinValue) ?? now).Date;
        var dueDate = request.DueDate?.ToDateTime(TimeOnly.MinValue) ?? invoiceDate.AddDays(SalesInvoiceService.DefaultPaymentTermDays);
        if (dueDate < invoiceDate) throw new BadRequestException("The due date cannot be before the invoice date.");

        var rate = await _currency.LockAsync(org, request.CurrencyCode, DateOnly.FromDateTime(invoiceDate), ct);
        var currencyCode = request.CurrencyCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(currencyCode))
            currencyCode = rate?.CurrencyCode ?? throw new BadRequestException("The currency is required.");

        var taxes = await TaxesAsync(org, request.Lines, ct);

        for (var attempt = 1; ; attempt++)
        {
            var invoice = Build(org, partner.Name, request, invoiceDate, dueDate, currencyCode, rate?.CurrencyId, taxes, userId, now);
            invoice.InvoiceNumber = await ArDocumentNumbers.NextAsync(_db, org, NumberPrefix, invoiceDate, ct);
            _db.SalesInvoices.Add(invoice);
            try
            {
                await _db.SaveChangesAsync(ct);
                return await _invoices.GetAsync(invoice.UUID) ?? throw new NotFoundException("SalesInvoice", invoice.UUID);
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                // Another invoice took the number; the next pass reads the highest again.
                _db.Entry(invoice).State = EntityState.Detached;
                foreach (var line in invoice.Lines) _db.Entry(line).State = EntityState.Detached;
            }
        }
    }

    private async Task CheckIncomeAccountsAsync(IEnumerable<ServiceInvoiceLineRequest> lines, CancellationToken ct)
    {
        var codes = lines.Select(l => l.IncomeAccountCode?.Trim()).Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (codes.Count == 0) return;
        var chart = _accounts is null ? [] : await _accounts.ListLeafAccountsAsync(ct);
        foreach (var code in codes)
        {
            var account = chart.FirstOrDefault(a => string.Equals(a.Code, code, StringComparison.OrdinalIgnoreCase));
            if (account is null || !account.IsActive)
                throw new BadRequestException($"SERVICE_INCOME_ACCOUNT_INVALID: Account {code} is not an active leaf account of the chart.");
            if (account.AccountType is not ("Revenue" or "OtherIncome"))
                throw new BadRequestException($"SERVICE_INCOME_ACCOUNT_INVALID: Account {code} is {account.AccountType}, not an income account.");
        }
    }

    private async Task<Dictionary<string, TaxCode>> TaxesAsync(Guid org, IEnumerable<ServiceInvoiceLineRequest> lines, CancellationToken ct)
    {
        var codes = lines.Select(l => l.TaxCode?.Trim().ToUpperInvariant()).OfType<string>().Where(c => c.Length > 0).Distinct().ToList();
        if (codes.Count == 0) return [];
        var rows = await _db.TaxCodes.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.OrganizationId == org && codes.Contains(t.Code) && t.IsActive)
            .ToDictionaryAsync(t => t.Code, StringComparer.OrdinalIgnoreCase, ct);
        var missing = codes.FirstOrDefault(c => !rows.ContainsKey(c));
        if (missing is not null) throw new BadRequestException($"Tax code {missing} does not exist or is inactive.");
        return rows;
    }

    private static SalesInvoice Build(
        Guid org, string partnerName, CreateServiceInvoiceRequest request, DateTime invoiceDate, DateTime dueDate, string currencyCode, Guid? currencyId,
        IReadOnlyDictionary<string, TaxCode> taxes, int userId, DateTime now)
    {
        var lines = request.Lines.Select((l, i) =>
        {
            var tax = string.IsNullOrWhiteSpace(l.TaxCode) ? null : taxes[l.TaxCode.Trim().ToUpperInvariant()];
            var percent = tax?.RatePercent ?? 0m;
            return new SalesInvoiceLine
            {
                UUID = Guid.NewGuid(), OrganizationId = org, LineNo = i + 1,
                VariantUuid = Guid.Empty, // Non-stock: no variant, nothing on the product ledger.
                Description = l.Description!.Trim() is { Length: > MaxDescription } d ? d[..MaxDescription] : l.Description!.Trim(),
                Quantity = l.Quantity, UnitPrice = l.UnitPrice, DiscountPercent = 0m, TaxPercent = percent,
                TaxCodeUuid = tax?.Uuid, TaxCode = tax?.Code,
                IncomeAccountCode = string.IsNullOrWhiteSpace(l.IncomeAccountCode) ? null : l.IncomeAccountCode.Trim(),
                LineTotal = SalesInvoiceTotals.LineTotal(l.Quantity, l.UnitPrice, 0m, percent)
            };
        }).ToList();
        var (subtotal, discount, taxAmount, grand) = SalesInvoiceTotals.Header(lines.Select(l => (l.Quantity, l.UnitPrice, l.DiscountPercent, l.TaxPercent)));

        return new SalesInvoice
        {
            UUID = Guid.NewGuid(), OrganizationId = org, TraceId = Guid.NewGuid(),
            SourceType = SalesInvoiceSources.ServiceManual,
            PartnerId = request.CustomerUuid, PartnerName = partnerName,
            InvoiceDate = invoiceDate, DueDate = dueDate,
            Subtotal = subtotal, DiscountAmount = discount, TaxAmount = taxAmount, GrandTotal = grand, AmountPaid = 0m, BalanceDue = grand,
            Status = SalesInvoiceStatuses.Draft, CurrencyCode = currencyCode, CurrencyId = currencyId,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            CreatedBy = userId, CreatedDate = now,
            Lines = lines
        };
    }
}

[ApiController]
[Route("api/sales-invoices/service")]
[RequiresFeature("MODULE_FINANCE")]
public sealed class ServiceInvoicesController : ControllerBase
{
    private readonly IServiceInvoiceService _svc;
    public ServiceInvoicesController(IServiceInvoiceService svc) => _svc = svc;

    [HttpPost, RequirePermission(PermissionCodes.SALES_INVOICE_MANAGE)]
    [Microsoft.AspNetCore.Http.EndpointSummary("Create a draft manual service invoice (no sale order, no delivery)")]
    public async Task<IActionResult> Create([FromBody] CreateServiceInvoiceRequest request, CancellationToken ct) =>
        Ok(ApiResponse<SalesInvoiceDetailModel>.Ok(await _svc.CreateAsync(request, User.GetUserId(), ct), "Service invoice created."));
}

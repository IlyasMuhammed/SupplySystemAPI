using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.ArAp.Reporting;

/// <summary>Who is generating / sending a statement (the log's GeneratedBy).</summary>
public sealed record StatementActor(int? UserId, string? UserName);

/// <summary>A generated statement: the data, and the PDF when it was asked for.</summary>
public sealed record StatementOutput(StatementResult Statement, byte[]? Pdf, string FileName);

/// <summary>A48 RPT C4 / C13 — customer and vendor statements (JSON / PDF), email, batch, the log.</summary>
public interface IStatementService
{
    Task<StatementOutput> GenerateCustomerStatementAsync(Guid customerId, StatementRequest request, StatementActor actor, CancellationToken ct = default);
    Task<StatementOutput> GenerateVendorStatementAsync(Guid vendorId, StatementRequest request, StatementActor actor, CancellationToken ct = default);
    Task<StatementEmailResult> EmailCustomerStatementAsync(Guid customerId, EmailStatementRequest request, StatementActor actor, CancellationToken ct = default);
    Task<BatchStatementResult> BatchCustomerStatementsAsync(BatchStatementRequest request, StatementActor actor, CancellationToken ct = default);
    Task<PaginatedResponse<StatementLogModel>> GetLogAsync(StatementLogFilter filter, CancellationToken ct = default);
}

/// <summary>A48 RPT C13 — SMS open items vs what the vendor says it is owed.</summary>
public interface IVendorReconciliationService
{
    Task<VendorReconciliationResult> ReconcileAsync(Guid vendorId, VendorReconciliationRequest request, CancellationToken ct = default);
}

/// <summary>
/// A48 RPT (BR-ST-001..005, D-11, D-13, D-RPT-7..9).
/// <list type="bullet">
/// <item><b>Activity</b> — the partner's ledger entries dated in [From, To] by business date (posting order within a day),
/// one section per currency: opening = Σ entries before From (BR-ST-003), running balance, closing = opening + debits −
/// credits (BR-ST-004). Customer ledger rows carry their currency; supplier ledger rows take their document's.</item>
/// <item><b>OpenItems</b> — what is still open as of To (BR-ST-002), per currency, debits (invoices) and credits
/// (unapplied payments, credit notes, advances), running from 0 to the open balance.</item>
/// <item>Aging per section (due-date basis) of the open debit items as of To.</item>
/// <item>One <c>statement_log</c> row per generation (CurrencyCode null when several currency sections; balances then in
/// base), and the same row is updated by its email (Sent / Failed).</item>
/// <item>PDF generated on request, never stored, rendered through <see cref="PdfRenderGate"/>.</item>
/// </list>
/// </summary>
internal sealed class StatementService : IStatementService, IVendorReconciliationService
{
    /// <summary>The most partners one batch generates statements for.</summary>
    internal const int MaxBatchPartners = 1000;
    /// <summary>The most lines one statement carries.</summary>
    internal const int MaxLines = 20_000;

    private readonly FinanceDbContext            _db;
    private readonly ArApReportingService        _reports;
    private readonly IEmailSender?               _email;
    private readonly IPoDocumentTemplateService? _templates;
    private readonly SMS.Shared.Onboarding.IDocumentBrandingProvider? _branding;
    private readonly SMS.Shared.Onboarding.ITemplatedDocumentRenderer? _templated;

    public StatementService(
        FinanceDbContext db, ArApReportingService reports, IEmailSender? email = null, IPoDocumentTemplateService? templates = null,
        SMS.Shared.Onboarding.IDocumentBrandingProvider? branding = null, SMS.Shared.Onboarding.ITemplatedDocumentRenderer? templated = null)
    {
        _db        = db;
        _reports   = reports;
        _email     = email;
        _templates = templates;
        _branding  = branding;
        _templated = templated;
    }

    // ══ Generation ══════════════════════════════════════════════════════════

    public Task<StatementOutput> GenerateCustomerStatementAsync(
        Guid customerId, StatementRequest request, StatementActor actor, CancellationToken ct = default) =>
        GenerateAsync(SubledgerSide.Receivable, customerId, request, actor, batchUuid: null, ct);

    public Task<StatementOutput> GenerateVendorStatementAsync(
        Guid vendorId, StatementRequest request, StatementActor actor, CancellationToken ct = default) =>
        GenerateAsync(SubledgerSide.Payable, vendorId, request, actor, batchUuid: null, ct);

    private async Task<StatementOutput> GenerateAsync(
        SubledgerSide side, Guid partnerId, StatementRequest request, StatementActor actor, Guid? batchUuid, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var format = NormalizeFormat(request.Format);
        var (statement, _) = await BuildAsync(side, partnerId, request.From, request.To, request.Type, request.CurrencyCode,
            request.IncludeAging, request.Buckets, ct);

        var pdf = format == "PDF" ? await RenderAsync(statement) : null;
        var log = await LogAsync(statement, format, actor, batchUuid, ct);
        statement.StatementId = log.Uuid;
        return new StatementOutput(statement, pdf, FileName(statement));
    }

    // A50 (D-25) — customer statements take the org's CustomerStatement template; vendor statements keep the house look.
    private async Task<byte[]> RenderAsync(StatementResult statement) =>
        StatementPdfRenderer.Render(statement, _templates is null ? null : await _templates.GetActiveAsync(),
            _branding is null || statement.PartnerType == "Vendor"
                ? null : await _branding.GetAsync(SMS.Shared.Onboarding.DocumentTemplateTypes.CustomerStatement),
            _templated);

    internal static string FileName(StatementResult s)
    {
        var who = new string((s.PartnerCode ?? s.PartnerName ?? s.PartnerId.ToString("N")[..8])
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        return $"Statement-{who}-{s.ToDate:yyyyMMdd}.pdf";
    }

    /// <summary>The statement data (no log, no PDF). Also returns the open items as of To (the batch filter reuses them).</summary>
    internal async Task<(StatementResult Statement, List<OpenItemRow> OpenAtEnd)> BuildAsync(
        SubledgerSide side, Guid partnerId, DateOnly? fromInput, DateOnly? toInput, string? typeInput, string? currency,
        bool includeAging, string? bucketText, CancellationToken ct)
    {
        var type    = NormalizeType(typeInput);
        var to      = toInput ?? ArApReportingService.D(_reports.Today);
        var from    = fromInput ?? new DateOnly(to.Year, to.Month, 1);
        if (to < from) throw new BadRequestException("The date range ends before it starts.");
        var buckets = AgingBucketSet.Parse(bucketText);

        var name     = await _reports.RequirePartnerAsync(side, partnerId, ct);
        var partner  = (await _reports.PartnersAsync([partnerId], ct)).GetValueOrDefault(partnerId);
        var baseCode = await _reports.BaseCodeAsync(side, ct);
        var toTime   = ArApReportingService.T(to);
        var fromTime = ArApReportingService.T(from);

        var openAtEnd = await _reports.LoadAsync(side, toTime, partnerId, includeCredits: true, currency, ct);
        var openBefore = type == StatementTypes.Activity
            ? await _reports.LoadAsync(side, fromTime.AddDays(-1), partnerId, includeCredits: true, currency, ct)
            : [];

        var sections = type == StatementTypes.Activity
            ? await ActivitySectionsAsync(side, partnerId, fromTime, toTime.AddDays(1), currency, openAtEnd, ct)
            : OpenItemSections(openAtEnd, toTime);

        if (includeAging)
            foreach (var s in sections)
            {
                var aging = new decimal[buckets.Count];
                foreach (var r in openAtEnd.Where(r => !r.IsCredit && r.CurrencyCode == s.CurrencyCode))
                    aging[buckets.IndexOf(AgingBucketSet.AgeOf(r.DocumentDate, r.DueDate, toTime, AgingBucketSet.DueDateBasis))] += r.Open;
                s.Aging = aging;
            }

        var lineCount = sections.Sum(s => s.Lines.Count);
        if (lineCount > MaxLines)
            throw new BadRequestException($"{lineCount} lines fall in that statement, more than the {MaxLines} one statement carries. Narrow the date range.");

        var statement = new StatementResult
        {
            PartnerType        = ArApReportingService.RoleOf(side),
            PartnerId          = partnerId,
            PartnerName        = name ?? partner?.Name,
            PartnerCode        = partner?.Code,
            PartnerEmail       = partner?.Email,
            PartnerAddress     = partner?.Address,
            CompanyName        = await CompanyNameAsync(),
            FromDate           = from,
            ToDate             = to,
            Type               = type,
            BaseCurrencyCode   = baseCode,
            Sections           = sections,
            OpeningBalanceBase = openBefore.Sum(r => r.SignedOpenBase),
            ClosingBalanceBase = openAtEnd.Sum(r => r.SignedOpenBase),
            LineCount          = lineCount,
            AgingLabels        = includeAging ? buckets.Labels : [],
            GeneratedAt        = _reports.GeneratedAt
        };
        return (statement, openAtEnd);
    }

    private async Task<List<StatementCurrencySection>> ActivitySectionsAsync(
        SubledgerSide side, Guid partnerId, DateTime from, DateTime endExclusive, string? currency,
        List<OpenItemRow> openAtEnd, CancellationToken ct)
    {
        List<PartnerTransactionDto> entries;
        if (side == SubledgerSide.Receivable)
        {
            var org = _db.TenantContext.OrganizationId;
            var q = _db.CustomerLedgerEntries.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.OrganizationId == org && e.PartnerId == partnerId && e.EntryDate < endExclusive);
            if (!string.IsNullOrWhiteSpace(currency)) q = q.Where(e => e.CurrencyCode == currency);
            entries = await q.Select(e => new PartnerTransactionDto
                {
                    SequenceNo = e.SequenceNo, EntryDate = e.EntryDate, EntryType = e.EntryType, ReferenceType = e.ReferenceType,
                    ReferenceId = e.ReferenceId, ReferenceNumber = e.ReferenceNumber, Narration = e.Narration,
                    CurrencyCode = e.CurrencyCode, Debit = e.DebitAmount, Credit = e.CreditAmount
                })
                .ToListAsync(ct);
        }
        else
        {
            entries = (await _reports.VendorLedgerAsync(partnerId, ct)).Where(e => e.EntryDate < endExclusive).ToList();
            if (!string.IsNullOrWhiteSpace(currency))
                entries = entries.Where(e => string.Equals(e.CurrencyCode, currency.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var inRange = ArApReportingService.WithRunningBalance(entries, from, null, out var opening);
        var open    = openAtEnd.GroupBy(r => r.DocumentUuid).ToDictionary(g => g.Key, g => g.First());
        var dueDates = await DueDatesAsync(side, inRange.Select(e => e.ReferenceId).Distinct().ToList(), ct);

        return opening.Keys.Concat(inRange.Select(e => e.CurrencyCode))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(code =>
            {
                var mine = inRange.Where(e => e.CurrencyCode == code).ToList();
                var openingBalance = opening.GetValueOrDefault(code);
                return new StatementCurrencySection
                {
                    CurrencyCode   = code,
                    OpeningBalance = openingBalance,
                    TotalDebits    = mine.Sum(e => e.Debit),
                    TotalCredits   = mine.Sum(e => e.Credit),
                    ClosingBalance = openingBalance + mine.Sum(e => e.Debit) - mine.Sum(e => e.Credit),
                    Lines = mine.Select(e => new StatementLine
                    {
                        Date           = ArApReportingService.D(e.EntryDate),
                        DocumentType   = e.ReferenceType,
                        EntryNumber    = e.ReferenceNumber,
                        Reference      = e.EntryType,
                        Description    = string.IsNullOrWhiteSpace(e.Narration) ? Describe(e.EntryType) : e.Narration!,
                        DueDate        = dueDates.TryGetValue(e.ReferenceId, out var due) && e.Debit > 0m ? due : null,
                        Debit          = e.Debit > 0m ? e.Debit : null,
                        Credit         = e.Credit > 0m ? e.Credit : null,
                        RunningBalance = e.RunningBalance,
                        Status         = StatusOf(e, open)
                    }).ToList()
                };
            })
            // A currency settled to nothing before the range and untouched in it is not a section.
            .Where(s => s.OpeningBalance != 0m || s.Lines.Count > 0)
            .ToList();
    }

    private async Task<Dictionary<Guid, DateOnly>> DueDatesAsync(SubledgerSide side, List<Guid> ids, CancellationToken ct)
    {
        var org = _db.TenantContext.OrganizationId;
        var map = new Dictionary<Guid, DateOnly>();
        foreach (var chunk in ids.Chunk(1000))
        {
            var rows = side == SubledgerSide.Receivable
                ? await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
                    .Where(i => i.OrganizationId == org && chunk.Contains(i.UUID)).Select(i => new { i.UUID, i.DueDate }).ToListAsync(ct)
                : await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
                    .Where(i => i.OrganizationId == org && chunk.Contains(i.UUID)).Select(i => new { i.UUID, i.DueDate }).ToListAsync(ct);
            foreach (var r in rows) map[r.UUID] = ArApReportingService.D(r.DueDate);
        }
        return map;
    }

    /// <summary>Open / Partially Paid / Paid for an invoice entry; Unapplied / Partially Applied / Applied for a credit.</summary>
    internal static string? StatusOf(PartnerTransactionDto e, Dictionary<Guid, OpenItemRow> open)
    {
        var isInvoice = e.ReferenceType is "SalesInvoice" or "Invoice"
                     && e.EntryType is CustomerLedgerEntryTypes.Invoice or CustomerLedgerEntryTypes.OpeningBal or "INVOICE_APPROVED" or "OPENING_BALANCE";
        if (isInvoice)
            return open.TryGetValue(e.ReferenceId, out var r)
                ? r.Status == "PARTIALLY_PAID" ? "Partially Paid" : "Open"
                : "Paid";

        var isCredit = e.Credit > 0m && e.ReferenceType is "CustomerPayment" or CounterSaleReferences.CreditNote or "SupplierPayment" or "CreditNote" or "DebitNote";
        if (isCredit)
            return open.TryGetValue(e.ReferenceId, out var c)
                ? c.Status == "PARTIALLY_APPLIED" || c.Open < c.Original ? "Partially Applied" : "Unapplied"
                : "Applied";
        return null;
    }

    private static string Describe(string entryType) => entryType switch
    {
        CustomerLedgerEntryTypes.Invoice or "INVOICE_APPROVED"     => "Invoice",
        CustomerLedgerEntryTypes.Payment or "PAYMENT_POSTED"       => "Payment",
        "PAYMENT_BOUNCED"                                          => "Payment bounced",
        CustomerLedgerEntryTypes.CreditNote or "CREDIT_NOTE_APPROVED" => "Credit note",
        CustomerLedgerEntryTypes.DebitNote or "DEBIT_NOTE_APPROVED"   => "Debit note",
        CustomerLedgerEntryTypes.Refund                            => "Refund",
        CustomerLedgerEntryTypes.OpeningBal or "OPENING_BALANCE"   => "Opening balance",
        "INVOICE_REVERSED"                                         => "Invoice reversed",
        "BAD_DEBT_WRITEOFF"                                        => "Write-off",
        _ => entryType
    };

    private static List<StatementCurrencySection> OpenItemSections(List<OpenItemRow> open, DateTime asOf) =>
        open.GroupBy(r => r.CurrencyCode)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var running = 0m;
                var lines = g.OrderBy(r => r.DocumentDate).ThenBy(r => r.IsCredit).ThenBy(r => r.DocumentNumber, StringComparer.Ordinal)
                    .Select(r =>
                    {
                        running += r.SignedOpen;
                        return new StatementLine
                        {
                            Date           = ArApReportingService.D(r.DocumentDate),
                            DocumentType   = r.Type,
                            EntryNumber    = r.DocumentNumber,
                            Reference      = r.Reference,
                            Description    = OpenItemDescription(r, asOf),
                            DueDate        = r.IsCredit ? null : ArApReportingService.D(r.DueDate),
                            Debit          = r.IsCredit ? null : r.Open,
                            Credit         = r.IsCredit ? r.Open : null,
                            RunningBalance = running,
                            Status         = r.IsCredit
                                ? (r.Open < r.Original ? "Partially Applied" : "Unapplied")
                                : (r.Status == "PARTIALLY_PAID" ? "Partially Paid" : "Open")
                        };
                    })
                    .ToList();
                return new StatementCurrencySection
                {
                    CurrencyCode   = g.Key,
                    OpeningBalance = 0m,
                    TotalDebits    = g.Where(r => !r.IsCredit).Sum(r => r.Open),
                    TotalCredits   = g.Where(r => r.IsCredit).Sum(r => r.Open),
                    ClosingBalance = g.Sum(r => r.SignedOpen),
                    Lines          = lines
                };
            })
            .ToList();

    private static string OpenItemDescription(OpenItemRow r, DateTime asOf)
    {
        var kind = r.Type switch
        {
            OpenItemTypes.SalesInvoice or OpenItemTypes.SupplierInvoice => "Invoice",
            OpenItemTypes.CustomerPayment => "Unapplied payment",
            OpenItemTypes.SalesCreditNote or OpenItemTypes.SupplierCredit => "Credit note",
            OpenItemTypes.SupplierDebit => "Debit note",
            OpenItemTypes.SupplierAdvance => "Advance payment",
            OpenItemTypes.WriteOff => "Write-off",
            _ => r.Type
        };
        if (r.IsCredit) return kind;
        var overdue = (asOf.Date - r.DueDate.Date).Days;
        return overdue > 0 ? $"{kind} — {overdue} days overdue" : kind;
    }

    // ══ Email ═══════════════════════════════════════════════════════════════

    public async Task<StatementEmailResult> EmailCustomerStatementAsync(
        Guid customerId, EmailStatementRequest request, StatementActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (statement, _) = await BuildAsync(SubledgerSide.Receivable, customerId, request.From, request.To, request.Type,
            request.CurrencyCode, request.IncludeAging, null, ct);

        var to = string.IsNullOrWhiteSpace(request.EmailTo) ? statement.PartnerEmail : request.EmailTo.Trim();
        if (string.IsNullOrWhiteSpace(to))
            throw new BadRequestException($"{statement.PartnerName ?? "The customer"} has no email address on file; give one in emailTo.");
        if (!LooksLikeEmail(to))
            throw new BadRequestException($"'{to}' is not an email address.");

        var log = await LogAsync(statement, "PDF", actor, null, ct);
        statement.StatementId = log.Uuid;
        await SendAsync(statement, log, to, request.Subject, request.Message, ct);

        if (log.EmailStatus != ArApEnums.StatementEmailStatus.Sent)
            throw new ConflictException($"The statement could not be emailed: {log.EmailError}");

        return new StatementEmailResult
        {
            StatementId = log.Uuid, EmailedTo = to, EmailedAt = log.EmailedAt!.Value, ClosingBalanceBase = statement.ClosingBalanceBase
        };
    }

    /// <summary>Renders the PDF and sends it; records Sent / Failed on the log row (never throws for a send failure).</summary>
    private async Task SendAsync(
        StatementResult statement, StatementLog log, string to, string? subject, string? message, CancellationToken ct)
    {
        try
        {
            if (_email is null) throw new InvalidOperationException("Email is not configured on this server.");

            var pdf = await RenderAsync(statement);
            // REV-1: the result-returning send — a delivery the sender could not make comes back as Failed, not "Sent".
            var sent = await _email.SendWithResultAsync(
                to,
                string.IsNullOrWhiteSpace(subject)
                    ? $"Statement of account — {statement.CompanyName ?? "our company"} — {statement.ToDate:dd MMM yyyy}"
                    : subject.Trim(),
                EmailBody(statement, message),
                [new EmailAttachment(FileName(statement), pdf, "application/pdf")],
                ct);
            if (sent is not { Success: true })
                throw new InvalidOperationException(sent?.Error ?? "The email sender gave no result.");

            log.EmailStatus = ArApEnums.StatementEmailStatus.Sent;
            log.EmailedTo   = Truncate(to, 200);
            log.EmailedAt   = _reports.GeneratedAt;
            log.EmailError  = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.EmailStatus = ArApEnums.StatementEmailStatus.Failed;
            log.EmailedTo   = Truncate(to, 200);
            log.EmailError  = Truncate(ex.Message, 1000);
        }
        await _db.SaveChangesAsync(ct);
    }

    internal static string EmailBody(StatementResult s, string? message)
    {
        var html = new StringBuilder();
        html.Append("<p>Dear ").Append(WebUtility.HtmlEncode(s.PartnerName ?? "customer")).Append(",</p>");
        if (!string.IsNullOrWhiteSpace(message))
            html.Append("<p>").Append(WebUtility.HtmlEncode(message.Trim())).Append("</p>");
        html.Append("<p>Please find attached your statement of account for ")
            .Append($"{s.FromDate:dd MMM yyyy} to {s.ToDate:dd MMM yyyy}.</p><ul>");
        foreach (var sec in s.Sections)
            html.Append("<li>").Append(WebUtility.HtmlEncode(sec.CurrencyCode)).Append(": balance ")
                .Append(sec.ClosingBalance.ToString("N2", System.Globalization.CultureInfo.InvariantCulture)).Append("</li>");
        html.Append("</ul><p>Kind regards,<br/>").Append(WebUtility.HtmlEncode(s.CompanyName ?? "Accounts")).Append("</p>");
        return html.ToString();
    }

    // ══ Batch ═══════════════════════════════════════════════════════════════

    public async Task<BatchStatementResult> BatchCustomerStatementsAsync(
        BatchStatementRequest request, StatementActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        NormalizeType(request.Type);
        if (request.MinimumBalance is < 0m) throw new BadRequestException("The minimum balance cannot be negative.");

        var to       = request.To ?? ArApReportingService.D(_reports.Today);
        var org      = _db.TenantContext.OrganizationId;
        var batch    = Guid.NewGuid();

        // Every customer's net open balance as of To, in one pass (BR-ST-005 compares the base closing balance).
        var open = await _reports.LoadAsync(SubledgerSide.Receivable, ArApReportingService.T(to), null, includeCredits: true, null, ct);
        var net  = open.GroupBy(r => r.PartnerId).ToDictionary(g => g.Key, g => g.Sum(r => r.SignedOpenBase));

        List<Guid> candidates;
        if (request.PartnerIds is { Count: > 0 } named)
            candidates = named.Distinct().ToList();
        else
        {
            var end = ArApReportingService.T(to).AddDays(1);
            candidates = (await _db.CustomerLedgerEntries.IgnoreQueryFilters().AsNoTracking()
                    .Where(e => e.OrganizationId == org && e.EntryDate < end)
                    .Select(e => e.PartnerId).Distinct().ToListAsync(ct))
                .Union(net.Keys).ToList();
        }

        var result   = new BatchStatementResult { Considered = candidates.Count };
        var selected = candidates.Where(p =>
        {
            var balance = net.GetValueOrDefault(p);
            return request.MinimumBalance is { } min ? balance >= min : balance != 0m;
        }).ToList();
        result.Skipped = candidates.Count - selected.Count;

        if (selected.Count > MaxBatchPartners)
            throw new BadRequestException(
                $"{selected.Count} customers qualify, more than the {MaxBatchPartners} one batch handles. Raise the minimum balance.");

        foreach (var partnerId in selected)
        {
            var item = new BatchStatementItem { PartnerId = partnerId, ClosingBalanceBase = net.GetValueOrDefault(partnerId) };
            result.Items.Add(item);
            try
            {
                var (statement, _) = await BuildAsync(SubledgerSide.Receivable, partnerId, request.From, to, request.Type, null,
                    request.IncludeAging, null, ct);
                item.PartnerName = statement.PartnerName;

                var log = await LogAsync(statement, request.SendEmail ? "PDF" : "JSON", actor, batch, ct);
                item.StatementId = log.Uuid;
                result.Generated++;
                item.Outcome = "GENERATED";

                if (!request.SendEmail) continue;
                if (string.IsNullOrWhiteSpace(statement.PartnerEmail) || !LooksLikeEmail(statement.PartnerEmail))
                {
                    item.Outcome = "NO_EMAIL";
                    result.NoEmail++;
                    continue;
                }

                statement.StatementId = log.Uuid;
                await SendAsync(statement, log, statement.PartnerEmail, null, null, ct);
                if (log.EmailStatus == ArApEnums.StatementEmailStatus.Sent)
                {
                    item.Emailed = true; item.EmailedTo = log.EmailedTo; item.Outcome = "EMAILED"; result.Emailed++;
                }
                else
                {
                    item.Outcome = "FAILED"; item.Error = log.EmailError; result.Failed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                item.Outcome = "FAILED";
                item.Error   = ex.Message;
                result.Failed++;
            }
        }
        return result;
    }

    // ══ Log ═════════════════════════════════════════════════════════════════

    private async Task<StatementLog> LogAsync(
        StatementResult s, string format, StatementActor actor, Guid? batchUuid, CancellationToken ct)
    {
        var single = s.Sections.Count == 1 ? s.Sections[0] : null;
        var log = new StatementLog
        {
            Uuid            = Guid.NewGuid(),
            OrganizationId  = _db.TenantContext.OrganizationId,
            PartnerUuid     = s.PartnerId,
            PartnerName     = Truncate(s.PartnerName, 200),
            PartnerRole     = s.PartnerType,
            StatementType   = s.Type,
            FromDate        = s.FromDate,
            ToDate          = s.ToDate,
            // One currency → its own figures; several (or none) → null and the base figures (D-RPT-8).
            CurrencyCode    = single?.CurrencyCode,
            OpeningBalance  = Math.Round(single?.OpeningBalance ?? s.OpeningBalanceBase, 2, MidpointRounding.AwayFromZero),
            ClosingBalance  = Math.Round(single?.ClosingBalance ?? s.ClosingBalanceBase, 2, MidpointRounding.AwayFromZero),
            LineCount       = s.LineCount,
            Format          = format,
            EmailStatus     = ArApEnums.StatementEmailStatus.NotSent,
            BatchUuid       = batchUuid,
            GeneratedAt     = _reports.GeneratedAt,
            GeneratedBy     = actor.UserId,
            GeneratedByName = Truncate(actor.UserName, 200)
        };
        _db.StatementLogs.Add(log);
        await _db.SaveChangesAsync(ct);
        return log;
    }

    public async Task<PaginatedResponse<StatementLogModel>> GetLogAsync(StatementLogFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var role = NormalizeRole(filter.Role);
        var org  = _db.TenantContext.OrganizationId;
        var (page, pageSize) = ArApReportingService.Clamp(filter.Page, filter.PageSize);

        var q = _db.StatementLogs.IgnoreQueryFilters().AsNoTracking().Where(l => l.OrganizationId == org && l.PartnerRole == role);
        if (filter.PartnerUuid is { } p) q = q.Where(l => l.PartnerUuid == p);

        var total = await q.CountAsync(ct);
        var data = await q.OrderByDescending(l => l.GeneratedAt).ThenByDescending(l => l.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(l => new StatementLogModel
            {
                Uuid = l.Uuid, PartnerUuid = l.PartnerUuid, PartnerName = l.PartnerName, PartnerRole = l.PartnerRole,
                StatementType = l.StatementType, FromDate = l.FromDate, ToDate = l.ToDate, CurrencyCode = l.CurrencyCode,
                OpeningBalance = l.OpeningBalance, ClosingBalance = l.ClosingBalance, LineCount = l.LineCount, Format = l.Format,
                EmailStatus = l.EmailStatus, EmailedTo = l.EmailedTo, EmailedAt = l.EmailedAt, EmailError = l.EmailError,
                BatchUuid = l.BatchUuid, GeneratedAt = l.GeneratedAt, GeneratedByName = l.GeneratedByName
            })
            .ToListAsync(ct);

        return new PaginatedResponse<StatementLogModel>
        {
            Data = data, TotalRecords = total, Page = page, PageSize = pageSize, TotalPages = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    internal static string NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) throw new BadRequestException("role is required: Customer or Vendor.");
        return ArApEnums.PartnerRole.All.FirstOrDefault(r => string.Equals(r, role.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new BadRequestException($"Unknown role '{role}'. Allowed: Customer, Vendor.");
    }

    // ══ Vendor reconciliation (C13) ═════════════════════════════════════════

    /// <summary>
    /// D-RPT-9 — SMS's open items for the vendor as of the day (invoices positive; advances, carried-forward notes and
    /// write-offs negative), in one currency, against the vendor's claimed balance and, optionally, its item list. Items
    /// match by reference (case / space-insensitive): the vendor's own invoice number first, then SMS's document number.
    /// Unmatched SMS items → InSMSNotVendor; unmatched vendor items → InVendorNotSMS; matched with |Δ| above the tolerance →
    /// AmountDifference. Reconciled when the balances agree within the tolerance and no item differs.
    /// </summary>
    public async Task<VendorReconciliationResult> ReconcileAsync(
        Guid vendorId, VendorReconciliationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tolerance = request.Tolerance ?? 0.01m;
        if (tolerance < 0m) throw new BadRequestException("The tolerance cannot be negative.");
        var asOf  = request.AsOfDate is { } a ? ArApReportingService.T(a) : _reports.Today;
        var name  = await _reports.RequirePartnerAsync(SubledgerSide.Payable, vendorId, ct);
        var all   = await _reports.LoadAsync(SubledgerSide.Payable, asOf, vendorId, includeCredits: true, null, ct);

        var currency = string.IsNullOrWhiteSpace(request.CurrencyCode)
            ? (all.Select(r => r.CurrencyCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is { Count: 1 } only
                ? only[0]
                : await _reports.BaseCodeAsync(SubledgerSide.Payable, ct))
            : request.CurrencyCode.Trim().ToUpperInvariant();
        var mine = all.Where(r => string.Equals(r.CurrencyCode, currency, StringComparison.OrdinalIgnoreCase)).ToList();

        var result = new VendorReconciliationResult
        {
            VendorId             = vendorId,
            VendorName           = name,
            AsOfDate             = ArApReportingService.D(asOf),
            CurrencyCode         = currency,
            SmsBalance           = mine.Sum(r => r.SignedOpen),
            VendorClaimedBalance = request.VendorClaimedBalance
        };
        result.Difference = result.SmsBalance - result.VendorClaimedBalance;

        if (request.VendorItems is { } claimed)
        {
            var unmatched = mine.ToList();
            foreach (var item in claimed)
            {
                if (string.IsNullOrWhiteSpace(item.Reference))
                    throw new BadRequestException("Every vendor item needs a reference.");
                var key   = Key(item.Reference);
                var match = unmatched.FirstOrDefault(r => r.Reference is not null && Key(r.Reference) == key)
                         ?? unmatched.FirstOrDefault(r => Key(r.DocumentNumber) == key);

                if (match is null)
                {
                    result.Differences.Add(new ReconciliationDifference
                    {
                        Type = ReconciliationDifferenceTypes.InVendorNotSms, Reference = item.Reference.Trim(), Date = item.Date,
                        VendorAmount = item.Amount, Difference = -item.Amount
                    });
                    continue;
                }

                unmatched.Remove(match);
                result.MatchedCount++;
                var delta = match.SignedOpen - item.Amount;
                if (Math.Abs(delta) > tolerance)
                    result.Differences.Add(new ReconciliationDifference
                    {
                        Type = ReconciliationDifferenceTypes.AmountDifference, DocumentType = match.Type,
                        EntryNumber = match.DocumentNumber, Reference = match.Reference ?? item.Reference.Trim(),
                        Date = ArApReportingService.D(match.DocumentDate), SmsAmount = match.SignedOpen, VendorAmount = item.Amount,
                        Difference = delta
                    });
            }

            foreach (var r in unmatched.OrderBy(r => r.DocumentDate))
                result.Differences.Add(new ReconciliationDifference
                {
                    Type = ReconciliationDifferenceTypes.InSmsNotVendor, DocumentType = r.Type, EntryNumber = r.DocumentNumber,
                    Reference = r.Reference, Date = ArApReportingService.D(r.DocumentDate), SmsAmount = r.SignedOpen,
                    Difference = r.SignedOpen
                });
        }

        result.IsReconciled = Math.Abs(result.Difference) <= tolerance && result.Differences.Count == 0;
        return result;
    }

    private static string Key(string reference) =>
        new string(reference.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    // ══ Helpers ═════════════════════════════════════════════════════════════

    internal static string NormalizeType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return StatementTypes.Activity;
        return ArApEnums.StatementType.All.FirstOrDefault(t => string.Equals(t, type.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new BadRequestException($"Unknown type '{type}'. Allowed: Activity, OpenItems.");
    }

    internal static string NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return "JSON";
        return format.Trim().ToUpperInvariant() switch
        {
            "JSON" => "JSON",
            "PDF"  => "PDF",
            _      => throw new BadRequestException($"Unknown format '{format}'. Allowed: json, pdf.")
        };
    }

    private async Task<string?> CompanyNameAsync()
    {
        if (_templates is null) return null;
        var template = await _templates.GetActiveAsync();
        return string.IsNullOrWhiteSpace(template?.CompanyName) ? null : template.CompanyName.Trim();
    }

    /// <summary>
    /// REV-16 — exactly one plain address: it parses as a <see cref="System.Net.Mail.MailAddress"/> whose address is the
    /// whole text (no display name, no list separators, no CR/LF), so a statement never goes to several recipients.
    /// </summary>
    internal static bool LooksLikeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.Length > 254 || text.IndexOfAny([',', ';', ' ', '\r', '\n', '\t', '<', '>', '"']) >= 0) return false;
        return System.Net.Mail.MailAddress.TryCreate(text, out var parsed)
            && string.Equals(parsed.Address, text, StringComparison.OrdinalIgnoreCase)
            && parsed.Host.Contains('.');
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}

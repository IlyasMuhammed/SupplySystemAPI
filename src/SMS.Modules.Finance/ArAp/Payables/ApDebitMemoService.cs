using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration.Posting;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.ArAp.Payables;

/// <summary>API-CONTRACT §9 — <c>POST api/finance/ap/debit-memo</c>.</summary>
public sealed class CreateApDebitMemoRequest
{
    public Guid      VendorUuid          { get; set; }
    /// <summary>The vendor invoice the memo is against; deducted at once when it is an approved payable still owing at least the memo.</summary>
    public Guid?     OriginalInvoiceUuid { get; set; }
    /// <summary>Defaults to today.</summary>
    public DateOnly? Date                { get; set; }
    /// <summary>A DebitReason code (e.g. PRICE_DIFFERENCE, SHORT_SUPPLY, REBATE).</summary>
    public string?   Reason              { get; set; }
    public string?   ReasonDetail        { get; set; }
    public decimal   Amount              { get; set; }
    public string?   Notes               { get; set; }
}

public sealed class DebitApplicationRequest
{
    public Guid    InvoiceUuid { get; set; }
    public decimal Amount      { get; set; }
}

public sealed class ApplyApDebitMemoRequest
{
    public List<DebitApplicationRequest> Applications { get; set; } = [];
}

public sealed record ApDebitMemoModel(
    Guid Uuid, string DebitNoteNumber, string Origin, Guid VendorUuid, string VendorName, decimal Amount,
    string DebitReason, string? DebitReasonDetail, Guid? OriginalInvoiceUuid, string? OriginalInvoiceNumber,
    string Status, string ApplicationStatus, decimal RemainingAmount, DateTime? IssuedAt,
    IReadOnlyList<SupplierNoteApplicationModel> Applications)
{
    /// <summary>FE-10 / D-AP-21 — the note's currency: its original invoice's, else the purchase base (null when unknown).</summary>
    public string? CurrencyCode { get; init; }
}

public interface IApDebitMemoService
{
    Task<ApDebitMemoModel> CreateAsync(CreateApDebitMemoRequest request, ApActor actor, CancellationToken ct = default);
    Task<ApDebitMemoModel> ApplyAsync(Guid debitNoteUuid, ApplyApDebitMemoRequest request, ApActor actor, CancellationToken ct = default);
    Task<IReadOnlyList<SupplierNoteApplicationModel>> ApplicationsAsync(Guid debitNoteUuid, CancellationToken ct = default);
    /// <summary>FE-8 — one debit note (memo or SRO note) with its remaining amount and applications.</summary>
    Task<ApDebitMemoModel> GetAsync(Guid debitNoteUuid, CancellationToken ct = default);
    /// <summary>FE-8 — a vendor's debit notes, newest first; <paramref name="openOnly"/> = carried forward with an amount left to apply.</summary>
    Task<PaginatedResponse<ApDebitMemoModel>> ListByVendorAsync(Guid vendorUuid, bool openOnly, int? page, int? pageSize, CancellationToken ct = default);
}

/// <summary>
/// A48 C14 (tasks 610–612; D-6) — the AP debit memo is the existing supplier <see cref="DebitNote"/>, raised without a
/// supplier return (<c>Origin = MEMO</c>, <c>SroUuid = Guid.Empty</c>): a price difference, a short-shipment claim, an
/// agreed rebate. Everything else is the existing note's: the supplier-ledger credit DEBIT_NOTE_APPROVED in the same save,
/// deduction from the named invoice when it is an approved payable still owing at least the memo (otherwise carried
/// forward), and, after the commit, the A44 vendor-credit posting (Dr PAYABLE / Cr PURCHASE — TS-CM-004) through
/// <see cref="SupplierNotePostings"/>. No raw journal (D-6).
/// <para>
/// Apply (TS-CM-005): a carried-forward note — memo or SRO note — applied to one or more open invoices of the same
/// supplier, in part or in full; one <see cref="SupplierNoteApplication"/> per invoice; each invoice reduced the way
/// notes always reduce it (<see cref="SupplierNotes.DeductAsync"/>). The ledger / GL effect stays on the note's own
/// posting. Lock order: the invoices (ascending Id), then the note's row.
/// </para>
/// </summary>
internal sealed class ApDebitMemoService : IApDebitMemoService
{
    internal const int ReasonMaxLength       = 50;
    internal const int ReasonDetailMaxLength = 500;
    internal const int NotesMaxLength        = 300;

    private static readonly string[] LiveStatuses = ["ISSUED", "ACKNOWLEDGED", "DISPUTED", "SETTLED"];

    private readonly FinanceDbContext            _fin;
    private readonly ISupplierLedgerService      _ledger;
    private readonly ISupplierNameLookupService? _names;
    private readonly SupplierNotePostings?       _postings;
    private readonly IPaymentScheduleSync?       _schedules;
    private readonly ICurrencyService?           _currency;
    private readonly SMS.Modules.Lookups.Services.ILookupsService? _lookups;

    public ApDebitMemoService(
        FinanceDbContext fin, ISupplierLedgerService ledger,
        ISupplierNameLookupService? names = null, SupplierNotePostings? postings = null, IPaymentScheduleSync? schedules = null,
        ICurrencyService? currency = null, SMS.Modules.Lookups.Services.ILookupsService? lookups = null)
    {
        _currency  = currency;
        _lookups   = lookups;
        _fin       = fin;
        _ledger    = ledger;
        _names     = names;
        _postings  = postings;
        _schedules = schedules;
    }

    private Guid Org => _fin.TenantContext.OrganizationId;

    private IQueryable<DebitNote> OwnNotes() =>
        _fin.DebitNotes.Where(d => d.OrganizationId == Org && d.IsActive && !d.IsDelete);

    // ── Create ───────────────────────────────────────────────────────────────

    public async Task<ApDebitMemoModel> CreateAsync(CreateApDebitMemoRequest request, ApActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.VendorUuid == Guid.Empty) throw new BadRequestException("vendorUuid is required.");
        var reason = request.Reason?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(reason)) throw new BadRequestException("A reason is required.");
        if (reason.Length > ReasonMaxLength) throw new BadRequestException($"The reason can be at most {ReasonMaxLength} characters.");
        var amount = ApSupport.Money(request.Amount);
        if (amount <= 0m) throw new BadRequestException("The debit memo amount must be greater than zero.");
        var detail = request.ReasonDetail?.Trim();
        if (detail is { Length: > ReasonDetailMaxLength }) throw new BadRequestException($"The reason detail can be at most {ReasonDetailMaxLength} characters.");
        var notes = request.Notes?.Trim();
        if (notes is { Length: > NotesMaxLength }) throw new BadRequestException($"Notes can be at most {NotesMaxLength} characters.");

        var issuedAt = request.Date is { } d ? d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) : DateTime.UtcNow;

        var memo = await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            Invoice? invoice = null;
            if (request.OriginalInvoiceUuid is { } invoiceUuid && invoiceUuid != Guid.Empty)
            {
                if (!await InvoiceRowLocks.LockOneAsync(_fin, invoiceUuid)) throw new NotFoundException("Invoice", invoiceUuid);
                invoice = await InvoiceRowLocks.Own(_fin).FirstAsync(i => i.UUID == invoiceUuid, ct);
                if (invoice.SupplierId != request.VendorUuid)
                    throw new BadRequestException($"Invoice {invoice.InvoiceNumber} belongs to another supplier.");
                if (InvoiceMatchStatus.Is(invoice.MatchStatus, InvoiceMatchStatus.Rejected) || InvoiceMatchStatus.Is(invoice.MatchStatus, InvoiceMatchStatus.Reversed))
                    throw new ConflictException($"Invoice {invoice.InvoiceNumber} is {invoice.MatchStatus}; nothing is owed on it.");
            }

            var vendorName = invoice?.SupplierName ?? await VendorNameAsync(request.VendorUuid);
            var now = DateTime.UtcNow;

            var note = new DebitNote
            {
                UUID              = Guid.NewGuid(),
                Origin            = ArApEnums.DebitNoteOrigin.Memo,
                SroUuid           = Guid.Empty,
                SroNumber         = string.Empty,
                SupplierId        = request.VendorUuid,
                SupplierName      = vendorName,
                DebitReason       = reason,
                DebitReasonDetail = string.IsNullOrEmpty(detail) ? null : detail,
                DebitAmount       = amount,
                InvoiceUuid       = invoice?.UUID,
                InvoiceNumber     = invoice?.InvoiceNumber,
                Status            = "ISSUED",
                IssuedAt          = issuedAt,
                Notes             = string.IsNullOrEmpty(notes) ? null : notes,
                IsActive          = true,
                CreatedBy         = actor.UserId ?? 0,
                CreatedDate       = now
            };

            if (invoice is not null && await SupplierNotes.DeductibleAtCreationAsync(_fin, invoice, amount))
            {
                await SupplierNotes.DeductAsync(_fin, invoice, amount, now);
                note.ApplicationStatus      = "APPLIED_TO_INVOICE";
                note.AppliedToInvoiceUuid   = invoice.UUID;
                note.AppliedToInvoiceNumber = invoice.InvoiceNumber;
                note.AppliedAt              = now;
            }
            else
            {
                note.ApplicationStatus    = SupplierNotes.CarriedForward;
                note.CarriedForwardAmount = amount;
            }

            // Last: the numbering lock is held to the commit (as the existing debit note does).
            note.DebitNoteNumber = await NextNumberAsync(now.Year);
            _fin.DebitNotes.Add(note);
            if (note.AppliedToInvoiceUuid is not null)
                _fin.Set<SupplierNoteApplication>().Add(Application(note, invoice!, amount, actor, now));

            // The existing note's single save: a credit to the supplier ledger (a debit note reduces what we owe).
            await _ledger.PostEntryAsync(
                note.SupplierId, "DEBIT_NOTE_APPROVED", "DebitNote", note.UUID, note.DebitNoteNumber,
                debitAmount: 0m, creditAmount: amount,
                narration: Trim($"Debit memo {note.DebitNoteNumber} — {reason}" + (invoice is null ? "" : $" (invoice {invoice.InvoiceNumber})"), 500),
                createdBy: actor.UserId ?? 0, supplierName: vendorName);
            return note;
        });

        if (_postings is not null) await _postings.PublishDebitNoteAsync(memo.UUID, ct);
        if (_schedules is not null && memo.AppliedToInvoiceUuid is { } applied) await _schedules.SyncAsync([applied], ct);
        return await ModelAsync(memo.UUID, ct);
    }

    // ── Apply ────────────────────────────────────────────────────────────────

    public async Task<ApDebitMemoModel> ApplyAsync(Guid debitNoteUuid, ApplyApDebitMemoRequest request, ApActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var applications = request.Applications ?? [];
        if (applications.Count == 0) throw new BadRequestException("Name at least one invoice to apply the debit memo to.");
        if (applications.Any(a => a.InvoiceUuid == Guid.Empty)) throw new BadRequestException("invoiceUuid is required.");
        if (applications.Any(a => a.Amount <= 0m)) throw new BadRequestException("Every application needs an amount greater than zero.");
        if (applications.Select(a => a.InvoiceUuid).Distinct().Count() != applications.Count)
            throw new BadRequestException("Each invoice can appear only once.");

        // The 404 first, before any lock.
        if (!await OwnNotes().AnyAsync(n => n.UUID == debitNoteUuid, ct)) throw new NotFoundException("DebitNote", debitNoteUuid);

        await InvoiceRowLocks.InTransactionAsync(_fin, async () =>
        {
            await InvoiceRowLocks.LockAsync(_fin, applications.Select(a => a.InvoiceUuid));
            var note = await LockNoteAsync(debitNoteUuid, ct) ?? throw new NotFoundException("DebitNote", debitNoteUuid);

            if (!LiveStatuses.Contains(note.Status) || note.ApplicationStatus != SupplierNotes.CarriedForward)
                throw new ConflictException($"{note.DebitNoteNumber} is not an issued debit note with an amount left to apply.");

            var remaining = note.CarriedForwardAmount ?? note.DebitAmount;
            var asked     = applications.Sum(a => ApSupport.Money(a.Amount));
            if (asked > remaining)
                throw new BadRequestException($"{note.DebitNoteNumber} has only {remaining:N2} left to apply.");

            // REV-6 (D-AP-21): a note has the currency of the invoice it was raised against, else the purchase base (as its
            // A44 vendor credit is posted); it is applied only to invoices in that currency. Unknown base: the invoices of one
            // application must at least share one currency.
            var noteCurrency = note.InvoiceUuid is { } original
                ? await InvoiceRowLocks.Own(_fin).AsNoTracking().Where(i => i.UUID == original).Select(i => i.Currency).FirstOrDefaultAsync(ct)
                : await ApSupport.PurchaseBaseCodeAsync(_currency, _lookups, Org, ct);
            noteCurrency = noteCurrency?.Trim().ToUpperInvariant();

            var now = DateTime.UtcNow;
            Invoice? last = null;
            foreach (var a in applications)
            {
                var amount  = ApSupport.Money(a.Amount);
                var invoice = await InvoiceRowLocks.Own(_fin).FirstOrDefaultAsync(i => i.UUID == a.InvoiceUuid, ct)
                    ?? throw new NotFoundException("Invoice", a.InvoiceUuid);
                if (invoice.SupplierId != note.SupplierId)
                    throw new BadRequestException($"Invoice {invoice.InvoiceNumber} belongs to another supplier.");
                var invoiceCurrency = (invoice.Currency ?? "").Trim().ToUpperInvariant();
                noteCurrency ??= invoiceCurrency;
                if (invoiceCurrency != noteCurrency)
                    throw new BadRequestException($"{note.DebitNoteNumber} is in {noteCurrency}; invoice {invoice.InvoiceNumber} is in {invoiceCurrency}.");
                if (!InvoiceMatchStatus.Is(invoice.MatchStatus, InvoiceMatchStatus.Approved))
                    throw new ConflictException($"Invoice {invoice.InvoiceNumber} is {invoice.MatchStatus}; a note can be applied only to an approved invoice.");

                var open = Math.Max(0m, await InvoiceSettlement.AvailableAsync(_fin, invoice));
                if (amount > open)
                    throw new BadRequestException($"Invoice {invoice.InvoiceNumber} has only {open:N2} open.");

                await SupplierNotes.DeductAsync(_fin, invoice, amount, now);
                _fin.Set<SupplierNoteApplication>().Add(Application(note, invoice, amount, actor, now));
                last = invoice;
            }

            note.CarriedForwardAmount = remaining - asked;
            if (note.CarriedForwardAmount == 0m)
            {
                note.ApplicationStatus      = "APPLIED";
                note.AppliedToInvoiceUuid   = last!.UUID;
                note.AppliedToInvoiceNumber = last.InvoiceNumber;
                note.AppliedAt              = now;
            }
            note.ModifiedBy   = actor.UserId;
            note.ModifiedDate = now;

            await _fin.SaveChangesAsync(ct);
            return true;
        });

        if (_schedules is not null) await _schedules.SyncAsync(applications.Select(a => a.InvoiceUuid).ToList(), ct);
        return await ModelAsync(debitNoteUuid, ct);
    }

    public async Task<IReadOnlyList<SupplierNoteApplicationModel>> ApplicationsAsync(Guid debitNoteUuid, CancellationToken ct = default)
    {
        if (!await OwnNotes().AnyAsync(n => n.UUID == debitNoteUuid, ct)) throw new NotFoundException("DebitNote", debitNoteUuid);
        return await ApplicationRowsAsync(debitNoteUuid, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<List<SupplierNoteApplicationModel>> ApplicationRowsAsync(Guid noteUuid, CancellationToken ct) =>
        await _fin.Set<SupplierNoteApplication>().AsNoTracking()
            .Where(a => a.OrganizationId == Org && a.NoteUuid == noteUuid && a.NoteType == ArApEnums.SupplierNoteType.DebitNote && a.ReversedAt == null)
            .OrderBy(a => a.AppliedAt).ThenBy(a => a.Id)
            .Select(a => new SupplierNoteApplicationModel(a.Uuid, a.NoteUuid, a.NoteNumber, a.InvoiceUuid, a.InvoiceNumber, a.Amount, a.AppliedAt, a.AppliedByName))
            .ToListAsync(ct);

    public async Task<ApDebitMemoModel> GetAsync(Guid debitNoteUuid, CancellationToken ct = default)
    {
        if (!await OwnNotes().AnyAsync(n => n.UUID == debitNoteUuid, ct)) throw new NotFoundException("DebitNote", debitNoteUuid);
        return await ModelAsync(debitNoteUuid, ct);
    }

    public async Task<PaginatedResponse<ApDebitMemoModel>> ListByVendorAsync(Guid vendorUuid, bool openOnly, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (p, size) = ApSupport.Paging(page, pageSize);
        var q = OwnNotes().AsNoTracking().Where(n => n.SupplierId == vendorUuid);
        if (openOnly)
            q = q.Where(n => n.ApplicationStatus == SupplierNotes.CarriedForward && LiveStatuses.Contains(n.Status)
                          && (n.CarriedForwardAmount ?? n.DebitAmount) > 0m);

        var total = await q.CountAsync(ct);
        var notes = await q.OrderByDescending(n => n.CreatedDate).ThenByDescending(n => n.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var uuids = notes.Select(n => n.UUID).ToList();
        var apps = (await _fin.Set<SupplierNoteApplication>().AsNoTracking()
                .Where(a => a.OrganizationId == Org && uuids.Contains(a.NoteUuid) && a.NoteType == ArApEnums.SupplierNoteType.DebitNote && a.ReversedAt == null)
                .OrderBy(a => a.AppliedAt).ThenBy(a => a.Id).ToListAsync(ct))
            .GroupBy(a => a.NoteUuid)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SupplierNoteApplicationModel>)g.Select(a =>
                new SupplierNoteApplicationModel(a.Uuid, a.NoteUuid, a.NoteNumber, a.InvoiceUuid, a.InvoiceNumber, a.Amount, a.AppliedAt, a.AppliedByName)).ToList());

        var currencies = await CurrenciesAsync(notes, ct);
        return ApSupport.Page(notes.Select(n => Model(n, apps.GetValueOrDefault(n.UUID) ?? []) with { CurrencyCode = currencies(n) }).ToList(), total, p, size);
    }

    private async Task<ApDebitMemoModel> ModelAsync(Guid uuid, CancellationToken ct)
    {
        var n = await OwnNotes().AsNoTracking().FirstAsync(x => x.UUID == uuid, ct);
        var currencies = await CurrenciesAsync([n], ct);
        return Model(n, await ApplicationRowsAsync(uuid, ct)) with { CurrencyCode = currencies(n) };
    }

    /// <summary>FE-10 — the notes' currencies (D-AP-21): the original invoice's, else the purchase base; two queries at most.</summary>
    private async Task<Func<DebitNote, string?>> CurrenciesAsync(IReadOnlyCollection<DebitNote> notes, CancellationToken ct)
    {
        var invoiceUuids = notes.Where(n => n.InvoiceUuid != null).Select(n => n.InvoiceUuid!.Value).Distinct().ToList();
        var byInvoice = invoiceUuids.Count == 0 ? new Dictionary<Guid, string>()
            : await InvoiceRowLocks.Own(_fin).AsNoTracking().Where(i => invoiceUuids.Contains(i.UUID))
                .ToDictionaryAsync(i => i.UUID, i => (i.Currency ?? "").Trim().ToUpperInvariant(), ct);
        var baseCode = notes.Any(n => n.InvoiceUuid is null || !byInvoice.ContainsKey(n.InvoiceUuid.Value))
            ? await ApSupport.PurchaseBaseCodeAsync(_currency, _lookups, Org, ct) : null;
        return n => n.InvoiceUuid is { } u && byInvoice.TryGetValue(u, out var c) ? c : baseCode;
    }

    private static ApDebitMemoModel Model(DebitNote n, IReadOnlyList<SupplierNoteApplicationModel> applications)
    {
        var remaining = n.ApplicationStatus == SupplierNotes.CarriedForward ? n.CarriedForwardAmount ?? n.DebitAmount : 0m;
        return new ApDebitMemoModel(n.UUID, n.DebitNoteNumber, n.Origin, n.SupplierId, n.SupplierName, n.DebitAmount,
            n.DebitReason, n.DebitReasonDetail, n.InvoiceUuid, n.InvoiceNumber, n.Status, n.ApplicationStatus, remaining, n.IssuedAt,
            applications);
    }

    private SupplierNoteApplication Application(DebitNote note, Invoice invoice, decimal amount, ApActor actor, DateTime now) => new()
    {
        OrganizationId = Org,
        NoteType       = ArApEnums.SupplierNoteType.DebitNote,
        NoteUuid       = note.UUID,
        NoteNumber     = note.DebitNoteNumber,
        InvoiceId      = invoice.Id,
        InvoiceUuid    = invoice.UUID,
        InvoiceNumber  = invoice.InvoiceNumber,
        VendorUuid     = note.SupplierId,
        Amount         = amount,
        AppliedAt      = now,
        AppliedBy      = actor.UserId,
        AppliedByName  = actor.UserName
    };

    /// <summary>The note's row, locked (after the invoices'), then read as committed now. Null when it is not this organization's.</summary>
    private async Task<DebitNote?> LockNoteAsync(Guid uuid, CancellationToken ct)
    {
        if (_fin.Database.IsRelational()
            && await OwnNotes().Where(x => x.UUID == uuid)
                   .ExecuteUpdateAsync(s => s.SetProperty(x => x.ApplicationStatus, x => x.ApplicationStatus), ct) == 0)
            return null;

        foreach (var stale in _fin.ChangeTracker.Entries<DebitNote>().Where(e => e.Entity.UUID == uuid).ToList())
            stale.State = EntityState.Detached;

        return await OwnNotes().FirstOrDefaultAsync(x => x.UUID == uuid, ct);
    }

    private async Task<string> VendorNameAsync(Guid vendorId)
    {
        if (_names is not null)
        {
            var names = await _names.GetNamesAsync([vendorId]);
            if (names.TryGetValue(vendorId, out var name) && !string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        var known = await InvoiceRowLocks.Own(_fin).Where(i => i.SupplierId == vendorId)
            .OrderByDescending(i => i.Id).Select(i => i.SupplierName).FirstOrDefaultAsync();
        return !string.IsNullOrWhiteSpace(known) ? known
            : throw new NotFoundException("Business partner", vendorId);
    }

    /// <summary>DN-YYYY-NNNNN, the same numbering (and the same lock) as <see cref="DebitNoteRepository"/>.</summary>
    private async Task<string> NextNumberAsync(int year)
    {
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = yearStart.AddYears(1);
        var own       = _fin.DebitNotes.IgnoreQueryFilters().Where(d => d.OrganizationId == Org);

        await DocumentNumbers.SerializeAsync(_fin, "finance.debit-note-number", Org);
        var count = await own.CountAsync(d => d.CreatedDate >= yearStart && d.CreatedDate < yearEnd);
        return await DocumentNumbers.NextFreeAsync(count, n => $"DN-{year}-{n:D5}",
            number => own.AnyAsync(d => d.DebitNoteNumber == number));
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}

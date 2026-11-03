using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Gateway.JournalEntries;

/// <summary>A transaction that must never reach QuickBooks as it stands. The posting fails; nothing is stored or sent.</summary>
internal sealed class JournalEntryBuildException : Exception
{
    public string Code { get; }
    public JournalEntryBuildException(string code, string message) : base(message) => Code = code;
}

/// <summary>
/// A49 G-1: a gateway transaction → the journal entries to store. Lines are the transaction's summarized lines (never the
/// transaction's text): balanced (BR-G1-002), description = the cost center's name only and a privacy safety net over everything
/// written (G-11), a line without a cost center on the payroll cost center (BR-G5-003), more than <see cref="MaxLinesPerEntry"/>
/// lines split into entries that each balance (G-16).
/// </summary>
internal interface IQuickBooksJournalEntryBuilder
{
    Task<IReadOnlyList<JournalEntryPayload>> BuildAsync(Guid organizationId, FinancialTransaction transaction, CancellationToken ct = default);

    /// <summary>G-12: the opposite entry of one already built, under its own key. Date: the void's, else the original's (retries stay identical).</summary>
    JournalEntryPayload BuildReversal(JournalEntryPayload original, DateTime? voidDate);
}

internal sealed class QuickBooksJournalEntryBuilder : IQuickBooksJournalEntryBuilder
{
    /// <summary>G-16: below QuickBooks' documented cap of about 1000.</summary>
    public const int MaxLinesPerEntry = 900;
    private const int MaxDocNumber = 21;
    private const int MaxPrivateNote = 4000;
    private const string VoidSuffix = ":void";

    private readonly IServiceProvider _services;

    public QuickBooksJournalEntryBuilder(IServiceProvider services) => _services = services;

    public async Task<IReadOnlyList<JournalEntryPayload>> BuildAsync(Guid organizationId, FinancialTransaction tx, CancellationToken ct = default)
    {
        var costCenters = _services.GetService<IHrmCostCenterSource>() is { } source
            ? await source.ListAsync(organizationId, ct)
            : [];
        var byCode = costCenters.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        var payroll = costCenters.FirstOrDefault(c => c.IsPayrollCostCenter);

        var lines = new List<JournalEntryPayloadLine>(tx.Lines.Count);
        foreach (var line in tx.Lines.OrderBy(l => l.LineNumber))
            if (ToLine(line, byCode, payroll) is { } mapped) lines.Add(mapped);
        if (lines.Count == 0)
            throw new JournalEntryBuildException("JE_EMPTY", "The transaction has no amounts to post.");

        CheckBalanced(lines, tx.SourceDocumentNumber);

        var root = tx.SourceDocumentId.ToString();
        var docType = tx.DocumentType.ToString();
        var note = PrivateNote(tx);
        var docNumber = (tx.SourceDocumentNumber ?? string.Empty).Trim();

        var batches = Split(lines, payroll?.Code, MaxLinesPerEntry);
        var payloads = new List<JournalEntryPayload>(batches.Count);
        for (var i = 0; i < batches.Count; i++)
        {
            int? batch = batches.Count == 1 ? null : i + 1;
            CheckBalanced(batches[i], tx.SourceDocumentNumber); // BR-G16-002: never trust the split.
            payloads.Add(new JournalEntryPayload
            {
                ExternalId     = root + BatchSuffix(batch),
                IdempotencyKey = $"{PostingSourceModules.Hrm}:{docType}:{root}{BatchSuffix(batch)}",
                DocNumber      = WithSuffix(docNumber, batch is { } n ? $"-B{n}" : string.Empty),
                TxnDate        = tx.TransactionDate.Date,
                CurrencyCode   = (tx.CurrencyCode ?? string.Empty).Trim().ToUpperInvariant(),
                ExchangeRate   = tx.ExchangeRate,
                SourceType     = docType,
                PrivateNote    = note,
                BatchNumber    = batch,
                Lines          = batches[i]
            });
        }

        await CheckPrivacyAsync(organizationId, payloads, ct);
        return payloads;
    }

    public JournalEntryPayload BuildReversal(JournalEntryPayload original, DateTime? voidDate)
    {
        var (root, batch) = SplitExternalId(original.ExternalId);
        var key = original.IdempotencyKey;
        var batchSuffix = BatchSuffix(batch);
        var keyRoot = batchSuffix.Length > 0 && key.EndsWith(batchSuffix, StringComparison.Ordinal) ? key[..^batchSuffix.Length] : key;
        return new JournalEntryPayload
        {
            ExternalId     = root + VoidSuffix + batchSuffix,
            IdempotencyKey = keyRoot + VoidSuffix + batchSuffix,
            DocNumber      = WithSuffix(original.DocNumber, "-R"),
            TxnDate        = (voidDate ?? original.TxnDate).Date,
            CurrencyCode   = original.CurrencyCode,
            ExchangeRate   = original.ExchangeRate,
            SourceType     = original.SourceType,
            PrivateNote    = Clip($"Reversal of {original.DocNumber}. {original.PrivateNote}".Trim(), MaxPrivateNote),
            BatchNumber    = batch,
            IsReversal     = true,
            Lines          = original.Lines.Select(l => new JournalEntryPayloadLine
            {
                PostingType    = l.PostingType == JournalEntryPostingTypes.Debit ? JournalEntryPostingTypes.Credit : JournalEntryPostingTypes.Debit,
                Amount         = l.Amount,
                AccountRole    = l.AccountRole,
                AccountCode    = l.AccountCode,
                CostCenterCode = l.CostCenterCode,
                CostCenterName = l.CostCenterName
            }).ToList()
        };
    }

    // ── Lines ────────────────────────────────────────────────────────────────

    private static JournalEntryPayloadLine? ToLine(
        FinancialTransactionLine line, IReadOnlyDictionary<string, HrmCostCenterRef> byCode, HrmCostCenterRef? payroll)
    {
        if (line.Debit < 0 || line.Credit < 0 || (line.Debit != 0 && line.Credit != 0))
            throw new JournalEntryBuildException("JE_LINE_INVALID", $"Line {line.LineNumber} must be a debit or a credit, and not negative.");
        var amount = Money(line.Debit != 0 ? line.Debit : line.Credit);
        if (amount == 0) return null;
        if (string.IsNullOrWhiteSpace(line.AccountRole) && string.IsNullOrWhiteSpace(line.AccountCode))
            throw new JournalEntryBuildException("JE_LINE_INVALID", $"Line {line.LineNumber} names no account.");

        // BR-G5-003: a line with no cost center of its own (the liabilities) belongs to the payroll cost center.
        var code = string.IsNullOrWhiteSpace(line.CostCenter) ? payroll?.Code : line.CostCenter.Trim();
        if (code is null)
            throw new JournalEntryBuildException("JE_NO_COST_CENTER", $"Line {line.LineNumber} has no cost center, and there is no payroll cost center to post it on.");

        return new JournalEntryPayloadLine
        {
            PostingType    = line.Debit != 0 ? JournalEntryPostingTypes.Debit : JournalEntryPostingTypes.Credit,
            Amount         = amount,
            AccountRole    = string.IsNullOrWhiteSpace(line.AccountRole) ? null : line.AccountRole.Trim(),
            AccountCode    = string.IsNullOrWhiteSpace(line.AccountCode) ? null : line.AccountCode.Trim(),
            CostCenterCode = code,
            // G-11: the master's name, whatever the line's own label says. The code stands in only when the master has no row.
            CostCenterName = byCode.TryGetValue(code, out var master) ? master.Name : code
        };
    }

    private static void CheckBalanced(IReadOnlyCollection<JournalEntryPayloadLine> lines, string? number)
    {
        var debit  = lines.Where(l => l.PostingType == JournalEntryPostingTypes.Debit).Sum(l => l.Amount);
        var credit = lines.Where(l => l.PostingType == JournalEntryPostingTypes.Credit).Sum(l => l.Amount);
        if (debit != credit)
            throw new JournalEntryBuildException("JE_UNBALANCED", $"{number}: debits {debit:0.00} and credits {credit:0.00} do not balance.");
    }

    // ── G-16 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lines of one cost center stay together. The pool (the opposite side of the payroll cost center: its credits, or its debits in a
    /// negative document, which are the liabilities) is shared out over the batches in proportion to what each batch needs, with
    /// the last pool line taking the remainder per batch, so every batch balances to the cent and every account's total is unchanged.
    /// </summary>
    internal static IReadOnlyList<List<JournalEntryPayloadLine>> Split(List<JournalEntryPayloadLine> lines, string? payrollCode, int max)
    {
        if (lines.Count <= max) return [lines];

        var pool = lines.Where(l => l.PostingType == JournalEntryPostingTypes.Credit
                                    && string.Equals(l.CostCenterCode, payrollCode, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(l => l.Amount).ToList();
        if (pool.Count == 0 || pool.Count >= max / 2)
            throw new JournalEntryBuildException("JE_CANNOT_SPLIT",
                "This entry is over the QuickBooks line limit and its liabilities are not on the payroll cost center, so it cannot be split into balanced entries.");

        var capacity = max - pool.Count;
        var batches = new List<List<JournalEntryPayloadLine>>();
        var current = new List<JournalEntryPayloadLine>();
        foreach (var group in lines.Except(pool).GroupBy(l => l.CostCenterCode, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        foreach (var chunk in group.Chunk(capacity))
        {
            if (current.Count + chunk.Length > capacity && current.Count > 0)
            {
                batches.Add(current);
                current = [];
            }
            current.AddRange(chunk);
        }
        if (current.Count > 0) batches.Add(current);

        decimal Need(List<JournalEntryPayloadLine> batch) => batch.Sum(l => l.PostingType == JournalEntryPostingTypes.Debit ? l.Amount : -l.Amount);
        var needs = batches.Select(Need).ToList();
        var total = needs.Sum();
        if (total <= 0 || needs.Any(n => n <= 0))
            throw new JournalEntryBuildException("JE_CANNOT_SPLIT", "This entry is over the QuickBooks line limit and a part of it has no debit to balance against, so it cannot be split.");

        // Every pool line but the largest, in proportion; the largest then closes each batch exactly.
        var allocated = new decimal[batches.Count];
        for (var p = 0; p < pool.Count; p++)
        {
            var line = pool[p];
            var left = line.Amount;
            for (var b = 0; b < batches.Count; b++)
            {
                decimal share;
                if (p == pool.Count - 1) share = needs[b] - allocated[b];
                else share = b == batches.Count - 1 ? left : Money(line.Amount * needs[b] / total);
                if (share < 0)
                    throw new JournalEntryBuildException("JE_CANNOT_SPLIT", "The liabilities could not be shared over the split entries to the cent.");
                left -= share;
                allocated[b] += share;
                if (share == 0) continue;
                batches[b].Add(new JournalEntryPayloadLine
                {
                    PostingType = line.PostingType, Amount = share, AccountRole = line.AccountRole, AccountCode = line.AccountCode,
                    CostCenterCode = line.CostCenterCode, CostCenterName = line.CostCenterName
                });
            }
        }
        return batches;
    }

    // ── G-11 ─────────────────────────────────────────────────────────────────

    private static string? PrivateNote(FinancialTransaction tx)
    {
        var number = tx.SourceDocumentNumber?.Trim();
        var props = tx.Metadata?.Properties;
        string? text = props is not null && props.TryGetValue(HrmPostingProperties.Period, out var period) && !string.IsNullOrWhiteSpace(period)
                       && props.TryGetValue(HrmPostingProperties.Headcount, out var count) && !string.IsNullOrWhiteSpace(count)
            ? $"{number}, {period.Trim()}, {count.Trim()} employees"
            : string.IsNullOrWhiteSpace(tx.Description) ? number : $"{number}, {tx.Description.Trim()}";
        return Clip(text, MaxPrivateNote);
    }

    private async Task CheckPrivacyAsync(Guid organizationId, IReadOnlyList<JournalEntryPayload> payloads, CancellationToken ct)
    {
        var source = _services.GetService<IHrmPersonalIdentifierSource>();
        if (source is null) return;
        var identifiers = await source.GetAsync(organizationId, ct);
        if (identifiers.Count == 0) return;

        foreach (var payload in payloads)
        {
            var texts = payload.Lines.Select(l => l.CostCenterName).Distinct(StringComparer.OrdinalIgnoreCase)
                .Append(payload.PrivateNote).Append(payload.DocNumber);
            foreach (var text in texts)
                if (ContainsAny(text, identifiers))
                    // Which employee stays out of the message on purpose: it ends up in the posting log.
                    throw new JournalEntryBuildException("JE_PERSONAL_DATA",
                        $"{payload.DocNumber}: the QuickBooks entry would carry an employee's name or number, so it was not sent. Correct the document's description or the cost center's name.");
        }
    }

    /// <summary>A whole-word match: "Ali" must not hit "Salaries".</summary>
    internal static bool ContainsAny(string? text, IEnumerable<string> identifiers)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var identifier in identifiers)
        {
            if (string.IsNullOrWhiteSpace(identifier)) continue;
            var value = identifier.Trim();
            for (var at = text.IndexOf(value, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = at + 1 < text.Length ? text.IndexOf(value, at + 1, StringComparison.OrdinalIgnoreCase) : -1)
            {
                var before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
                var end = at + value.Length;
                var after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (before && after) return true;
            }
        }
        return false;
    }

    // ── Keys ─────────────────────────────────────────────────────────────────

    private static string BatchSuffix(int? batch) => batch is { } n ? ":batch" + n.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static (string Root, int? Batch) SplitExternalId(string externalId)
    {
        var at = externalId.LastIndexOf(":batch", StringComparison.Ordinal);
        return at >= 0 && int.TryParse(externalId[(at + 6)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? (externalId[..at], n)
            : (externalId, null);
    }

    /// <summary>QuickBooks' DocNumber limit, keeping the suffix that tells the pieces apart.</summary>
    private static string WithSuffix(string number, string suffix)
    {
        if (suffix.Length == 0) return number; // A number too long as it stands is validation's to refuse, never cut.
        var room = MaxDocNumber - suffix.Length;
        return (number.Length > room ? number[..room] : number) + suffix;
    }

    private static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string? Clip(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

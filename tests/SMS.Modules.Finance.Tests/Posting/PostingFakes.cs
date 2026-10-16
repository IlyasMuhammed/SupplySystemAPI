using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Finance.Tests.Posting;

/// <summary>Stands in for SMS.Modules.Accounting's posting gateway: records every call; can throw.</summary>
internal sealed class RecordingPostingGateway : IFinancialPostingGateway
{
    public List<FinancialTransaction> Posts    { get; } = [];
    public List<FinancialTransaction> Credits  { get; } = [];
    public List<VoidRequest>          Voids    { get; } = [];
    public List<VoidRequest>          Reverses { get; } = [];

    /// <summary>Every call, in order: "Post:{key}", "Credit:{key}", "Void:{key}", "Reverse:{key}".</summary>
    public List<string> Calls { get; } = [];

    public Exception? Throw { get; set; }

    /// <summary>Everything posted, credit documents included.</summary>
    public IEnumerable<FinancialTransaction> All => Posts.Concat(Credits);

    public Task<PostingResult> PostAsync(FinancialTransaction t, CancellationToken ct = default) => Record(Posts, t, "Post", t.IdempotencyKey, t.SourceDocumentId, t.DocumentType);
    public Task<PostingResult> IssueCreditAsync(FinancialTransaction t, CancellationToken ct = default) => Record(Credits, t, "Credit", t.IdempotencyKey, t.SourceDocumentId, t.DocumentType);
    public Task<PostingResult> VoidAsync(VoidRequest r, CancellationToken ct = default) => Record(Voids, r, "Void", r.IdempotencyKey, r.SourceDocumentId, r.DocumentType);
    public Task<PostingResult> ReverseAsync(VoidRequest r, CancellationToken ct = default) => Record(Reverses, r, "Reverse", r.IdempotencyKey, r.SourceDocumentId, r.DocumentType);

    private Task<PostingResult> Record<T>(List<T> list, T item, string verb, string key, Guid id, FinDocType type)
    {
        Calls.Add($"{verb}:{key}");
        if (Throw is not null) throw Throw;
        list.Add(item);
        return Task.FromResult(new PostingResult { Status = PostingStatus.Success, SourceDocumentId = id, DocumentType = type });
    }
}

internal static class PostingAssert
{
    /// <summary>Σ debit = Σ credit, every line one-sided and positive, numbered from 1.</summary>
    public static void Balanced(FinancialTransaction tx)
    {
        var lines = tx.Lines;
        if (lines.Count == 0) throw new Xunit.Sdk.XunitException($"{tx.IdempotencyKey} has no lines.");
        if (lines.Sum(l => l.Debit) != lines.Sum(l => l.Credit))
            throw new Xunit.Sdk.XunitException($"{tx.IdempotencyKey}: debits {lines.Sum(l => l.Debit)} ≠ credits {lines.Sum(l => l.Credit)}.");
        if (lines.Any(l => l.Debit < 0m || l.Credit < 0m || (l.Debit > 0m) == (l.Credit > 0m)))
            throw new Xunit.Sdk.XunitException($"{tx.IdempotencyKey}: a line is not one positive debit or credit.");
        if (!lines.Select(l => l.LineNumber).SequenceEqual(Enumerable.Range(1, lines.Count)))
            throw new Xunit.Sdk.XunitException($"{tx.IdempotencyKey}: lines are not numbered from 1.");
    }

    public static decimal Dr(FinancialTransaction tx, string role) => tx.Lines.Where(l => l.AccountRole == role).Sum(l => l.Debit);
    public static decimal Cr(FinancialTransaction tx, string role) => tx.Lines.Where(l => l.AccountRole == role).Sum(l => l.Credit);
}

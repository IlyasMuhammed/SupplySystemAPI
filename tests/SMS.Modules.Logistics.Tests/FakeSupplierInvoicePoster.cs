using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Tests;

/// <summary>
/// Stands in for Finance, which Logistics reaches through <see cref="ISupplierInvoicePoster"/>
/// rather than a project reference (decision G10).
/// <para>
/// Records every posting rather than merely counting them, because the figure that matters is
/// <em>what</em> was posted — a bill approved at less than it was billed must reach Finance at the
/// approved figure, and only the posting itself proves that.
/// </para>
/// </summary>
internal sealed class FakeSupplierInvoicePoster : ISupplierInvoicePoster
{
    internal List<SupplierInvoicePosting> Postings { get; } = [];

    /// <summary>Set to make the next post behave as though the payable already existed.</summary>
    internal bool AlreadyPosted { get; set; }

    /// <summary>Set to make Finance refuse, so the caller's handling of that can be tested.</summary>
    internal Exception? Throws { get; set; }

    internal SupplierInvoicePosting? Last => Postings.Count > 0 ? Postings[^1] : null;

    public Task<SupplierInvoicePostingResult> PostAsync(
        SupplierInvoicePosting posting, int userId, CancellationToken ct = default)
    {
        if (Throws is not null) throw Throws;

        Postings.Add(posting);

        return Task.FromResult(new SupplierInvoicePostingResult(
            Guid.NewGuid(), $"INV-2026-{Postings.Count:D5}", AlreadyPosted));
    }
}

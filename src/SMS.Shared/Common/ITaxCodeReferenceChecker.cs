namespace SMS.Shared.Common;

/// <summary>
/// Implemented by a module that keeps a Finance tax code by its <b>text</b> rather than its id — today
/// Integration, whose QuickBooks tax mappings find a document line's code by text (SAP alignment S-11).
/// Finance's tax-code rename asks every implementation, purely through DI
/// (IEnumerable&lt;ITaxCodeReferenceChecker&gt;), before it lets a code's text change: renaming a mapped code
/// would leave every line carrying the new text unmapped, and each such invoice held back. Same reasoning as
/// <see cref="ISupplierReferenceChecker"/>: no project reference in either direction.
/// </summary>
public interface ITaxCodeReferenceChecker
{
    /// <summary>
    /// Where the organization keeps <paramref name="code"/> by its text, as words that read after "is mapped
    /// in" (e.g. "the QuickBooks tax mappings"), or null when it does not.
    /// </summary>
    Task<string?> FindCodeReferenceAsync(Guid organizationId, string code, CancellationToken ct = default);
}

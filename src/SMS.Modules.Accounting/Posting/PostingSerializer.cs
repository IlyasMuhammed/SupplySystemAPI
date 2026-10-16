using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Accounting.Posting;

/// <summary>
/// D-13 / D-42 — how the posting log stores what a caller sent, and the payload fingerprint. The transaction is serialized
/// without <c>Metadata.Document</c> ([JsonIgnore] on the DTO); the document is serialized on its own with its concrete type
/// and its type name is stored. Only concrete <see cref="IAccountingDocument"/> classes of SMS.Shared are accepted when
/// reading back (allowlist) — the stored name is never used to load an arbitrary type.
/// </summary>
internal static class PostingSerializer
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Concrete Shared payload types by name (names that occur twice are left out rather than guessed).</summary>
    private static readonly IReadOnlyDictionary<string, Type> DocumentTypes = typeof(IAccountingDocument).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IAccountingDocument).IsAssignableFrom(t))
        .GroupBy(t => t.Name, StringComparer.Ordinal)
        .Where(g => g.Count() == 1)
        .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);

    public static string Serialize(FinancialTransaction tx) => JsonSerializer.Serialize(tx, Json);
    public static string Serialize(VoidRequest request) => JsonSerializer.Serialize(request, Json);

    /// <summary>(json, type name), or (null, null) when there is no document. A document of a type outside the allowlist
    /// (a caller's own class) is still stored for the log, under a null type name, and is not rebuilt on retry.</summary>
    public static (string? Json, string? TypeName) SerializeDocument(IAccountingDocument? document)
    {
        if (document is null) return (null, null);
        var type = document.GetType();
        var json = JsonSerializer.Serialize(document, type, Json);
        return (json, DocumentTypes.TryGetValue(type.Name, out var known) && known == type ? type.Name : null);
    }

    public static IAccountingDocument? DeserializeDocument(string? json, string? typeName)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(typeName) || !DocumentTypes.TryGetValue(typeName, out var type)) return null;
        return (IAccountingDocument?)JsonSerializer.Deserialize(json, type, Json);
    }

    /// <summary>The transaction as the caller sent it, with its document put back into the metadata.</summary>
    public static FinancialTransaction DeserializeTransaction(string json, IAccountingDocument? document)
    {
        var tx = JsonSerializer.Deserialize<FinancialTransaction>(json, Json)
                 ?? throw new InvalidOperationException("The stored transaction could not be read.");
        if (document is null) return tx;
        var m = tx.Metadata;
        return new FinancialTransaction
        {
            DocumentType = tx.DocumentType, SourceDocumentId = tx.SourceDocumentId, SourceDocumentNumber = tx.SourceDocumentNumber,
            SourceModule = tx.SourceModule, TransactionDate = tx.TransactionDate, PartnerId = tx.PartnerId, PartnerType = tx.PartnerType,
            PartnerName = tx.PartnerName, CurrencyCode = tx.CurrencyCode, ExchangeRate = tx.ExchangeRate, Description = tx.Description,
            Lines = tx.Lines,
            Metadata = new FinancialDocumentMetadata
            {
                DueDate = m?.DueDate, PaymentTerms = m?.PaymentTerms, Reference = m?.Reference, WarehouseCode = m?.WarehouseCode,
                LinkedDocumentId = m?.LinkedDocumentId, Properties = m?.Properties, Document = document,
            },
        };
    }

    public static VoidRequest DeserializeVoid(string json) =>
        JsonSerializer.Deserialize<VoidRequest>(json, Json) ?? throw new InvalidOperationException("The stored void request could not be read.");

    /// <summary>D-13 — SHA-256 hex over the transaction JSON, the document type and the document JSON.</summary>
    public static string Fingerprint(string transactionJson, string? documentJson, string? documentType)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{transactionJson}\n{documentType}\n{documentJson}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Everything the log stores about a transaction, and its fingerprint.</summary>
    public static PostingSnapshot Snapshot(FinancialTransaction tx)
    {
        var json = Serialize(tx);
        var (docJson, docType) = SerializeDocument(tx.Metadata?.Document);
        return new PostingSnapshot(json, docJson, docType, Fingerprint(json, docJson, docType ?? tx.Metadata?.Document?.GetType().Name));
    }
}

internal sealed record PostingSnapshot(string TransactionJson, string? DocumentJson, string? DocumentType, string Fingerprint);

using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;
using static SMS.Modules.Integration.Providers.QuickBooks.Mapping.QboMap;

namespace SMS.Modules.Integration.Providers.QuickBooks.Mapping;

/// <summary>A49 G-1: RemoteJournalEntry ↔ SDK <see cref="JournalEntry"/>. One JournalEntryLineDetail per line, with account, class and location.</summary>
internal static class QboJournalEntryMapper
{
    public static JournalEntry ToSdk(RemoteJournalEntry source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new JournalEntry
        {
            DocNumber        = Value(source.DocNumber),
            TxnDate          = Date(source.TxnDate),
            TxnDateSpecified = true,
            CurrencyRef      = Ref(source.CurrencyCode),
            PrivateNote      = Value(source.PrivateNote),
            Line             = source.Lines.Select(ToLine).ToArray()
        };
    }

    private static Line ToLine(RemoteJournalEntryLine line) => new()
    {
        Amount              = line.Amount,
        AmountSpecified     = true,
        Description         = Value(line.Description),
        DetailType          = LineDetailTypeEnum.JournalEntryLineDetail,
        DetailTypeSpecified = true,
        AnyIntuitObject     = new JournalEntryLineDetail
        {
            PostingType          = line.IsDebit ? PostingTypeEnum.Debit : PostingTypeEnum.Credit,
            PostingTypeSpecified = true,
            AccountRef           = new ReferenceType { Value = line.AccountId },
            ClassRef             = Ref(line.ClassId),
            DepartmentRef        = Ref(line.DepartmentId)
        }
    };

    public static RemoteRecord ToRecord(JournalEntry entry) =>
        new(RequireId(entry, "journal entry"),
            entry.SyncToken ?? string.Empty,
            Name: null,
            entry.DocNumber,
            Read(entry.TotalAmt, entry.TotalAmtSpecified),
            null,
            ReadTxnActive(entry));
}

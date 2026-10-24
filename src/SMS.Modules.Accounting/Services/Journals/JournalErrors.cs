using System.Globalization;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// A47 task 462 — the journal engine's error catalog (docs/journal-engine/API-CONTRACT.md "Errors"). The codes travel as
/// <c>result.errorCode</c> (and per line in <c>result.errors[]</c>); the texts are the spec's (§19) where it gives one.
/// JE-015 is reserved for the reversal rules (RR).
/// </summary>
public static class JournalErrorCodes
{
    public const string NotBalanced = "JE-001", TooFewLines = "JE-002", BothSides = "JE-003", NoAmount = "JE-004",
        NoPeriod = "JE-005", PeriodClosed = "JE-006", PeriodLocked = "JE-007", AdjustmentOnly = "JE-008",
        AccountNotPostable = "JE-009", AccountInactive = "JE-010", PartnerRequired = "JE-011", ExchangeRateRequired = "JE-012",
        JournalTypeInvalid = "JE-013", NotDraft = "JE-014", Reversal = "JE-015";
}

/// <summary>A47 — the exact user-facing texts (the contract repeats them).</summary>
public static class JournalMessages
{
    public const string NotBalanced          = "Entry is not balanced";
    public const string TooFewLines          = "Minimum 2 lines required";
    public const string BothSides            = "Line cannot have both debit and credit";
    public const string NoAmount             = "Line must have a debit or a credit amount";
    public const string Negative             = "Amounts cannot be negative";
    public const string NoPeriod             = "No fiscal period covers this date";
    public const string PeriodClosed         = "Period is closed";
    public const string PeriodLocked         = "Period is locked";
    public const string AdjustmentOnly       = "Period accepts only adjustment entries";
    public const string YearClosed           = "Fiscal year is closed";
    public const string AccountNotFound      = "Account not found";
    public const string AccountNotPostable   = "Account is not postable";
    public const string AccountInactive      = "Account is inactive";
    public const string PartnerTypeInvalid   = "Partner type must be Customer or Vendor";
    public const string PartnerNotFound      = "Partner not found";
    public const string ExchangeRateRequired = "Exchange rate required for foreign currency";
    public const string ExchangeRateInvalid  = "Exchange rate must be greater than zero";
    public const string CurrencyInvalid      = "Currency must be a 3-letter currency code";
    public const string JournalTypeNotFound  = "Journal type not found";
    public const string JournalTypeInactive  = "Journal type is inactive";
    public const string OpeningAndClosing    = "An entry cannot be both an opening and a closing entry";
    public const string TooManyLines         = "An entry can have at most 500 lines";
    public const string AlreadyPosted        = "Entry is already posted";
    public const string AlreadyReversed      = "Entry has been reversed";
    public const string PostedImmutable      = "Posted entries cannot be modified";
    public const string PostedNotDeletable   = "Posted entries cannot be deleted";
    public const string SourceDraft          = "This draft was created from a source document: post it, or change the source document instead";
    public const string EntryNotFound        = "Journal entry not found";

    /// <summary>TS-JE-011 — "Sales entries require a partner" (Purchase / Cash … for the other kinds).</summary>
    public static string PartnerRequired(string kind) => kind switch
    {
        Domain.JournalTypeKinds.Sale          => "Sales entries require a partner",
        Domain.JournalTypeKinds.Purchase      => "Purchase entries require a partner",
        Domain.JournalTypeKinds.Miscellaneous => "Miscellaneous entries require a partner",
        _                                     => $"{kind} entries require a partner",
    };

    /// <summary>BR-JE-001 detail, e.g. "Entry is not balanced: debits 1,000.00, credits 999.00".</summary>
    public static string NotBalancedDetail(decimal debit, decimal credit, string? currency = null) =>
        $"{NotBalanced}: debits {Money(debit)}, credits {Money(credit)}{(currency is null ? "" : $" ({currency})")}";

    public static string Line(int lineNumber, string message) => $"Line {lineNumber}: {message}";

    internal static string Money(decimal value) => value.ToString("#,##0.00", CultureInfo.InvariantCulture);
}

/// <summary>One refused rule (a line number when it is about a line).</summary>
public sealed record JournalError(string ErrorCode, string Message, int? LineNumber = null);

/// <summary>
/// A47 — a journal request broke a rule: 400 with <c>result: { errorCode, errors[] }</c> (see <see cref="JournalErrorFilterAttribute"/>);
/// the message is the first error's. Without the filter the global middleware still answers 400 with the message.
/// </summary>
public sealed class JournalValidationException : BadRequestException, IJournalCodedError
{
    public string ErrorCode { get; }
    public IReadOnlyList<JournalError> Errors { get; }

    public JournalValidationException(IReadOnlyList<JournalError> errors) : base(errors[0].LineNumber is { } n ? JournalMessages.Line(n, errors[0].Message) : errors[0].Message)
    {
        ErrorCode = errors[0].ErrorCode;
        Errors = errors;
    }

    public JournalValidationException(string errorCode, string message, int? lineNumber = null) : this([new JournalError(errorCode, message, lineNumber)]) { }
}

/// <summary>A47 — the entry's state refuses the action (already posted, reversed …): 409 with <c>result: { errorCode }</c>.</summary>
public sealed class JournalConflictException : ConflictException, IJournalCodedError
{
    public string ErrorCode { get; }
    public IReadOnlyList<JournalError> Errors { get; }

    public JournalConflictException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
        Errors = [new JournalError(errorCode, message)];
    }
}

public interface IJournalCodedError
{
    string ErrorCode { get; }
    IReadOnlyList<JournalError> Errors { get; }
}

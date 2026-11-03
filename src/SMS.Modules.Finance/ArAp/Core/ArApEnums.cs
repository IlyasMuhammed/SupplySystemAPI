using SMS.Shared.Common;

namespace SMS.Modules.Finance.ArAp;

/// <summary>A48 enum spellings (API-CONTRACT §0.1) — persisted as-is and enforced by CHECK constraints.</summary>
internal static class ArApEnums
{
    internal static class PartnerRole
    {
        public const string Customer = "Customer", Vendor = "Vendor";
        public static readonly IReadOnlyList<string> All = [Customer, Vendor];
    }

    internal static class RiskTier
    {
        public const string Low = "Low", Medium = "Medium", High = "High";
        public static readonly IReadOnlyList<string> All = [Low, Medium, High];
    }

    internal static class PaymentPriority
    {
        public const string High = "High", Normal = "Normal", Low = "Low";
        public static readonly IReadOnlyList<string> All = [High, Normal, Low];
        /// <summary>1 = High … 3 = Low, for ordering.</summary>
        public static int Rank(string? p) => p == High ? 1 : p == Low ? 3 : 2;
    }

    internal static class CreditControlMode
    {
        public const string Off = CreditControlModes.Off, Warn = CreditControlModes.Warn, Block = CreditControlModes.Block;
        public static readonly IReadOnlyList<string> All = [Off, Warn, Block];
    }

    internal static class CreditHoldAction
    {
        public const string Hold = "Hold", Release = "Release", TempIncrease = "TempIncrease", TempExpired = "TempExpired",
            RiskTier = "RiskTier", Override = "Override", DunningHold = "DunningHold", DunningReset = "DunningReset";
        public static readonly IReadOnlyList<string> All = [Hold, Release, TempIncrease, TempExpired, RiskTier, Override, DunningHold, DunningReset];
    }

    internal static class CreditHoldRequestStatus
    {
        public const string Pending = "Pending", Completed = "Completed", Rejected = "Rejected";
        public static readonly IReadOnlyList<string> All = [Pending, Completed, Rejected];
    }

    internal static class StatementType
    {
        public const string Activity = "Activity", OpenItems = "OpenItems";
        public static readonly IReadOnlyList<string> All = [Activity, OpenItems];
    }

    internal static class StatementEmailStatus
    {
        public const string NotSent = "NotSent", Sent = "Sent", Failed = "Failed";
        public static readonly IReadOnlyList<string> All = [NotSent, Sent, Failed];
    }

    internal static class DunningActionType
    {
        public const string Email = "Email", Letter = "Letter", Manual = "Manual";
        public static readonly IReadOnlyList<string> All = [Email, Letter, Manual];
    }

    internal static class DunningRunStatus
    {
        public const string Draft = "Draft", Processing = "Processing", Completed = "Completed", Cancelled = "Cancelled";
        public static readonly IReadOnlyList<string> All = [Draft, Processing, Completed, Cancelled];
    }

    internal static class DunningRunItemStatus
    {
        public const string Pending = "Pending", Sent = "Sent", Skipped = "Skipped", Failed = "Failed", Manual = "Manual";
        public static readonly IReadOnlyList<string> All = [Pending, Sent, Skipped, Failed, Manual];
    }

    internal static class MatchType
    {
        public const string ThreeWay = "ThreeWay", TwoWay = "TwoWay";
        public static readonly IReadOnlyList<string> All = [ThreeWay, TwoWay];
    }

    internal static class LineMatchResult
    {
        public const string Matched = "Matched", PartialMatch = "PartialMatch", Mismatch = "Mismatch";
        public static readonly IReadOnlyList<string> All = [Matched, PartialMatch, Mismatch];
    }

    internal static class MatchLineStatus
    {
        public const string Pending = "Pending", Matched = "Matched", QuantityMismatch = "QuantityMismatch",
            PriceMismatch = "PriceMismatch", Unmatched = "Unmatched", Overridden = "Overridden";
        public static readonly IReadOnlyList<string> All = [Pending, Matched, QuantityMismatch, PriceMismatch, Unmatched, Overridden];
    }

    internal static class ScheduleStatus
    {
        public const string Open = "Open", Blocked = "Blocked", Scheduled = "Scheduled", PartiallyPaid = "PartiallyPaid",
            Paid = "Paid", Cancelled = "Cancelled";
        public static readonly IReadOnlyList<string> All = [Open, Blocked, Scheduled, PartiallyPaid, Paid, Cancelled];
    }

    internal static class PaymentRunStatus
    {
        public const string Draft = "Draft", Proposed = "Proposed", Reviewing = "Reviewing", Approved = "Approved",
            Executing = "Executing", Completed = "Completed", Cancelled = "Cancelled";
        public static readonly IReadOnlyList<string> All = [Draft, Proposed, Reviewing, Approved, Executing, Completed, Cancelled];
    }

    internal static class PaymentRunItemStatus
    {
        public const string Proposed = "Proposed", Approved = "Approved", Removed = "Removed", Paid = "Paid", Failed = "Failed";
        public static readonly IReadOnlyList<string> All = [Proposed, Approved, Removed, Paid, Failed];
    }

    internal static class PaymentRunBatchStatus
    {
        public const string Pending = "Pending", Paid = "Paid", Failed = "Failed";
        public static readonly IReadOnlyList<string> All = [Pending, Paid, Failed];
    }

    internal static class SupplierNoteType
    {
        public const string DebitNote = "DebitNote", CreditNote = "CreditNote";
        public static readonly IReadOnlyList<string> All = [DebitNote, CreditNote];
    }

    internal static class DebitNoteOrigin
    {
        public const string Sro = "SRO", Memo = "MEMO";
        public static readonly IReadOnlyList<string> All = [Sro, Memo];
    }

    internal static class AgingBasis
    {
        public const string DueDate = "DueDate", DocumentDate = "DocumentDate";
        public static readonly IReadOnlyList<string> All = [DueDate, DocumentDate];
    }

    /// <summary>SupplierPayment.PaymentMethod codes (the AP methods).</summary>
    internal static readonly IReadOnlyList<string> ApPaymentMethods = ["BANK_TRANSFER", "ONLINE_WIRE", "CHEQUE", "CASH"];

    /// <summary>"Unknown {field} '{value}'. Allowed: {list}." (API-CONTRACT §0).</summary>
    internal static string UnknownText(string field, string? value, IEnumerable<string> allowed) =>
        $"Unknown {field} '{value}'. Allowed: {string.Join(", ", allowed)}.";

    /// <summary>The canonical spelling of <paramref name="value"/> in <paramref name="allowed"/> (case-insensitive), or null.</summary>
    internal static string? Canonical(string? value, IEnumerable<string> allowed) =>
        value is null ? null : allowed.FirstOrDefault(a => string.Equals(a, value.Trim(), StringComparison.OrdinalIgnoreCase));
}

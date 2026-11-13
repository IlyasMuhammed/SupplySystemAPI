using System.Net.Mail;

namespace SMS.Shared.Onboarding;

/// <summary>A50 TPL (D-26) — POST api/sales-invoices|sale-quotations|purchase-orders/{uuid}/email.</summary>
public sealed class DocumentEmailRequest
{
    /// <summary>One or more addresses separated by "," or ";". Empty = the customer's / supplier's email.</summary>
    public string? To { get; set; }
    public string? Cc { get; set; }
    /// <summary>Fills {Message}; HTML-encoded, line breaks kept.</summary>
    public string? Message { get; set; }
}

public sealed record DocumentEmailFailure(string Email, string Error);

public sealed class DocumentEmailResult
{
    /// <summary>Every recipient (to and cc) was sent to.</summary>
    public bool Sent { get; init; }
    public List<string> SentTo { get; init; } = new();
    public List<string> Cc { get; init; } = new();
    public string Subject { get; init; } = string.Empty;
    public List<DocumentEmailFailure> Failed { get; init; } = new();
}

/// <summary>Recipient parsing shared by the document email actions and the template send-test.</summary>
public static class DocumentEmailAddresses
{
    public const int MaxRecipients = 20;

    /// <summary>Exactly one plain address (no display name, no list separators, no CR/LF) — the StatementService rule.</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.Length > 254 || text.IndexOfAny([',', ';', ' ', '\r', '\n', '\t', '<', '>', '"']) >= 0) return false;
        return MailAddress.TryCreate(text, out var parsed)
            && string.Equals(parsed.Address, text, StringComparison.OrdinalIgnoreCase)
            && parsed.Host.Contains('.');
    }

    /// <summary>The addresses of a "a@x.com; b@y.com" list, distinct; the invalid ones in <paramref name="invalid"/>.</summary>
    public static List<string> Parse(string? list, out List<string> invalid)
    {
        invalid = new List<string>();
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(list)) return result;
        foreach (var part in list.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsValid(part)) { invalid.Add(part); continue; }
            if (!result.Contains(part, StringComparer.OrdinalIgnoreCase)) result.Add(part);
        }
        return result;
    }
}

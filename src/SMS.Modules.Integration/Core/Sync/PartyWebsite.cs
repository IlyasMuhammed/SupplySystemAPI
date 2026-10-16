namespace SMS.Modules.Integration.Core.Sync;

/// <summary>
/// A customer's or vendor's website as QuickBooks accepts it. QuickBooks refuses the whole record (error 2200,
/// "Malformed Web Site Address format") for an address without a scheme, which is how people type them
/// ("www.example.com"). So the scheme is added, and a value that still is not a web address is left off — the
/// validator warns about that — instead of costing the customer or vendor its sync. Found live against a sandbox
/// company on 2026-10-13.
/// </summary>
internal static class PartyWebsite
{
    /// <summary>QuickBooks' WebAddr.URI limit.</summary>
    public const int MaxLength = 1000;

    /// <summary>The address to send, or null when there is none or it is not a usable web address.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Any(char.IsWhiteSpace)) return null;

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        if (text.Length > MaxLength) return null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        // A real host name: dotted, no empty labels ("www..test.com"), each label letters/digits/hyphens.
        var labels = uri.IdnHost.Split('.');
        if (labels.Length < 2 || labels.Any(l => l.Length == 0 || l.Length > 63 || l.StartsWith('-') || l.EndsWith('-')
                                            || !l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
            return null;

        return text;
    }

    /// <summary>True when a website was given but cannot be sent.</summary>
    public static bool IsDropped(string? value) => !string.IsNullOrWhiteSpace(value) && Normalize(value) is null;
}

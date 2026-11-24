namespace SMS.Shared.Common;

// A51 P7 (A51-D12) — a health read of the e-mail channel for the Super Admin: is it configured, and how did the last send go.
// Never carries credentials, host names, user names or keys; the error text is the sender's own (trimmed).

public sealed record EmailLastSend(DateTime At, bool Succeeded, string? Error);

public sealed class EmailStatusModel
{
    /// <summary>"SMTP" (Environment 0) or "SendGrid" (any other).</summary>
    public string Provider { get; set; } = string.Empty;
    /// <summary>The channel has what it needs to try a send (SMTP host + user + password, or a SendGrid key) and a sender address.</summary>
    public bool Configured { get; set; }
    public string? Hint { get; set; }
    /// <summary>The last send attempt of this API process (null since the last restart until a mail is sent).</summary>
    public EmailLastSend? LastSend { get; set; }
}

public interface IEmailStatusProvider
{
    EmailStatusModel GetStatus();
    bool IsConfigured { get; }
}

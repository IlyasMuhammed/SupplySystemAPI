using Microsoft.Extensions.Options;
using SMS.Shared.Common;

namespace SMS.Modules.Auth.Services;

/// <summary>A51 P7 — the last e-mail send of this process (success flag + a trimmed error), recorded by the senders. In memory.</summary>
internal sealed class EmailSendTracker
{
    private volatile EmailLastSend? _last;

    public EmailLastSend? Last => _last;

    public void Record(bool succeeded, string? error) =>
        _last = new EmailLastSend(DateTime.UtcNow, succeeded, succeeded || string.IsNullOrWhiteSpace(error) ? null : Trim(error));

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];
}

/// <summary>A51 P7 — GET api/system/email/status: configured? last send result? Reads the settings only to say yes / no, never to show them.</summary>
internal sealed class EmailStatusProvider : IEmailStatusProvider
{
    private readonly AppSettings _settings;
    private readonly EmailSendTracker _tracker;

    public EmailStatusProvider(IOptions<AppSettings> settings, EmailSendTracker tracker)
    {
        _settings = settings.Value;
        _tracker = tracker;
    }

    private bool Smtp => _settings.Environment == 0;

    private bool HasSender => !string.IsNullOrWhiteSpace(_settings.SmtpFromEmail) || !string.IsNullOrWhiteSpace(_settings.AppSupportEmail);

    public bool IsConfigured => HasSender && (Smtp
        ? !string.IsNullOrWhiteSpace(_settings.SmtpHost) && !string.IsNullOrWhiteSpace(_settings.SmtpUser) && !string.IsNullOrWhiteSpace(_settings.SmtpPass)
        : !string.IsNullOrWhiteSpace(_settings.SendGridApiKey));

    public EmailStatusModel GetStatus() => new()
    {
        Provider = Smtp ? "SMTP" : "SendGrid",
        Configured = IsConfigured,
        Hint = IsConfigured ? null : Smtp
            ? "SMTP is not fully configured (host, user, password and a sender address are needed); invitation e-mails cannot be sent."
            : "SendGrid is not fully configured (an API key and a sender address are needed); invitation e-mails cannot be sent.",
        LastSend = _tracker.Last
    };
}

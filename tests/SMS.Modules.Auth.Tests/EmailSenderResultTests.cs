using System.Net;
using System.Net.Mail;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;
using SMS.Modules.Auth.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Auth.Tests;

/// <summary>
/// A48 REV-1 — <see cref="IEmailSender.SendWithResultAsync"/> tells the caller whether the mail went out (SMTP and SendGrid
/// failures, 4xx/5xx, missing key, bad address, CR/LF subject), while <see cref="IEmailSender.SendAsync"/> still swallows
/// everything exactly as before.
/// </summary>
public class EmailSenderResultTests
{
    private static EmailSender Smtp(Func<MailMessage, Task> transport) =>
        new(Options.Create(new AppSettings { Environment = 0, SmtpFromEmail = "noreply@sms.test", SmtpHost = "invalid.invalid" }),
            NullLogger<EmailSender>.Instance) { SmtpTransport = transport };

    private static EmailSender SendGrid(HttpStatusCode status, string? key = "SG.key") =>
        new(Options.Create(new AppSettings { Environment = 1, SmtpFromEmail = "noreply@sms.test", SendGridApiKey = key }),
            NullLogger<EmailSender>.Instance) { SendGridTransport = _ => Task.FromResult(new Response(status, null, null)) };

    [Fact]
    public async Task Smtp_success_is_sent_and_the_message_carries_subject_body_and_attachments()
    {
        (string Subject, string Body, bool Html, int Attachments)? seen = null;
        var sender = Smtp(m => { seen = (m.Subject, m.Body, m.IsBodyHtml, m.Attachments.Count); return Task.CompletedTask; });

        var r = await sender.SendWithResultAsync("ar@acme.test", "Statement", "<p>Hi</p>", [new EmailAttachment("s.pdf", [1, 2], "application/pdf")]);

        r.Should().Be(EmailSendResult.Sent);
        seen.Should().Be(("Statement", "<p>Hi</p>", true, 1), "the message is disposed after the send, so it is read inside the transport");
    }

    [Fact]
    public async Task Smtp_failure_is_reported_by_the_result_path_and_swallowed_by_SendAsync()
    {
        var sender = Smtp(_ => throw new SmtpException("Mailbox unavailable"));

        var r = await sender.SendWithResultAsync("ar@acme.test", "Statement", "<p>Hi</p>");
        (r.Success, r.Error).Should().Be((false, "SMTP send failed: Mailbox unavailable"));

        await FluentActions.Awaiting(() => sender.SendAsync("ar@acme.test", "Statement", "<p>Hi</p>")).Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted, true, null)]
    [InlineData(HttpStatusCode.BadRequest, false, "SendGrid refused the email (400 BadRequest).")]
    [InlineData(HttpStatusCode.InternalServerError, false, "SendGrid refused the email (500 InternalServerError).")]
    public async Task SendGrid_status_decides_the_outcome(HttpStatusCode status, bool success, string? error)
    {
        var r = await SendGrid(status).SendWithResultAsync("ar@acme.test", "Statement", "<p>Hi</p>");
        (r.Success, r.Error).Should().Be((success, error));
    }

    [Fact]
    public async Task A_missing_SendGrid_key_is_a_failure()
    {
        var sender = new EmailSender(Options.Create(new AppSettings { Environment = 1, SendGridApiKey = "" }), NullLogger<EmailSender>.Instance);
        var r = await sender.SendWithResultAsync("ar@acme.test", "Statement", "<p>Hi</p>");
        (r.Success, r.Error).Should().Be((false, "Email is not configured (no SendGrid API key)."));
        await FluentActions.Awaiting(() => sender.SendAsync("ar@acme.test", "Statement", "<p>Hi</p>")).Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("", "Statement", "'' is not a valid email address.")]
    [InlineData("not-an-address", "Statement", "'not-an-address' is not a valid email address.")]
    [InlineData("ar@acme.test", "Statement\r\nBcc: x@evil.test", "The email subject contains a line break.")]
    public async Task Bad_address_or_header_injection_is_refused_before_any_send(string to, string subject, string error)
    {
        var calls = 0;
        var sender = Smtp(_ => { calls++; return Task.CompletedTask; });
        var r = await sender.SendWithResultAsync(to, subject, "<p>Hi</p>");
        (r.Success, r.Error, calls).Should().Be((false, error, 0));
    }

    [Fact]
    public async Task A_sender_without_its_own_result_path_reports_SendAsync_through_the_default()
    {
        IEmailSender ok = new LegacySender(fail: false), bad = new LegacySender(fail: true);
        (await ok.SendWithResultAsync("a@b.test", "s", "b")).Should().Be(EmailSendResult.Sent);
        (await bad.SendWithResultAsync("a@b.test", "s", "b")).Should().Be(EmailSendResult.Failed("relay down"));
    }

    private sealed class LegacySender(bool fail) : IEmailSender
    {
        public Task SendAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null) =>
            fail ? throw new InvalidOperationException("relay down") : Task.CompletedTask;
    }
}

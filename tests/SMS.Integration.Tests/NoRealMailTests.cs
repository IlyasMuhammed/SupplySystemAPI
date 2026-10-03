using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Integration.Tests;

/// <summary>
/// <see cref="NoRealMail"/> reaches a real host: the settings every mail sender reads are the disarmed ones, and
/// a send fails (or is swallowed) at once — while the message is built, before any connection is opened.
/// </summary>
public sealed class NoRealMailTests : IClassFixture<SapWebApplicationFactory>
{
    private static readonly TimeSpan NoNetwork = TimeSpan.FromSeconds(5);

    private readonly SapWebApplicationFactory _f;

    public NoRealMailTests(SapWebApplicationFactory factory) => _f = factory;

    [Fact]
    public void The_host_reads_the_disarmed_mail_settings()
    {
        var settings = _f.Services.GetRequiredService<IOptions<AppSettings>>().Value;

        settings.Environment.Should().Be(0, "mail goes the SMTP way, never to SendGrid");
        settings.SmtpHost.Should().Be(NoRealMail.Host);
        settings.SmtpFromEmail.Should().Be(NoRealMail.Sender);
    }

    [Fact]
    public async Task A_notification_email_fails_while_the_message_is_built()
    {
        using var scope = _f.Services.CreateScope();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var watch = Stopwatch.StartNew();
        var send  = () => notifications.SendEmailAsync("someone@example.com", "Test", "<p>Test</p>");

        await send.Should().ThrowAsync<FormatException>("the sender is not an e-mail address");
        watch.Elapsed.Should().BeLessThan(NoNetwork);
    }

    [Fact]
    public async Task The_document_email_sender_gives_up_at_once()
    {
        using var scope = _f.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        var watch = Stopwatch.StartNew();
        await sender.SendAsync("someone@example.com", "Test", "<p>Test</p>"); // logs the failure, never throws

        watch.Elapsed.Should().BeLessThan(NoNetwork);
    }
}

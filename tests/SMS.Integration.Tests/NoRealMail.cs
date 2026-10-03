using System.Runtime.CompilerServices;

namespace SMS.Integration.Tests;

/// <summary>
/// No test in this assembly may send real e-mail. Every host here boots the real Program.cs, whose configuration
/// (appsettings.json, user secrets) names a real SMTP server with real credentials, and three senders read it
/// directly — Auth's EmailService and EmailSender, and NotificationService — so replacing IEmailService, as some
/// factories do, stops only one of them.
/// <para>
/// Environment variables override appsettings.json and user secrets in every WebApplicationFactory host, so
/// setting them once, when this assembly loads, covers every factory — including the plain
/// WebApplicationFactory&lt;Program&gt; fixtures that configure nothing. Mail goes the SMTP way (Environment 0,
/// never SendGrid), and the sender is not an e-mail address, so every send fails while the message is being built,
/// before any connection is made; the host is in the reserved <c>.invalid</c> domain besides.
/// </para>
/// </summary>
internal static class NoRealMail
{
    internal const string Host   = "smtp.invalid";
    internal const string Sender = "mail-disabled-in-tests";

#pragma warning disable CA2255 // a module initializer is exactly right in a test assembly
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Disable()
    {
        Environment.SetEnvironmentVariable("AppSettings__Environment",   "0");
        Environment.SetEnvironmentVariable("AppSettings__SmtpHost",      Host);
        Environment.SetEnvironmentVariable("AppSettings__SmtpFromEmail", Sender);
    }
}

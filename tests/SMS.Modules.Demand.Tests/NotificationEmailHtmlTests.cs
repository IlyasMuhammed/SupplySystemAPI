using FluentAssertions;
using SMS.Modules.Notifications.Services;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// Security audit F3: a notification's title and message are plain text that can carry user input (a rejection
/// or reversal reason, a partner's name), so the e-mail built from them must not let that text turn into markup
/// or a link of the sender's choosing.
/// </summary>
public sealed class NotificationEmailHtmlTests
{
    [Fact]
    public void Markup_in_the_title_and_message_is_shown_as_text()
    {
        var html = NotificationService.BuildEmailHtml(
            "Invoice <b>INV-1</b> rejected",
            "Reason: <a href=\"https://evil.example\">click here</a><script>alert(1)</script>",
            null);

        html.Should().NotContain("<b>INV-1</b>");
        html.Should().NotContain("<script>");
        html.Should().NotContain("<a href=\"https://evil.example\">");
        html.Should().Contain("Invoice &lt;b&gt;INV-1&lt;/b&gt; rejected");
        html.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
    }

    [Fact]
    public void A_quote_in_the_link_cannot_break_out_of_the_href()
    {
        var html = NotificationService.BuildEmailHtml(
            "Title", "Body", "https://sms.example/portal/x\" onclick=\"steal()");

        html.Should().NotContain("\" onclick=\"steal()");
        html.Should().Contain("href=\"https://sms.example/portal/x&quot; onclick=&quot;steal()\"");
    }

    [Fact]
    public void Ordinary_text_and_link_come_through_unchanged()
    {
        var html = NotificationService.BuildEmailHtml(
            "PO-0042 approved", "Your purchase order was approved.", "https://sms.example/portal/pages/po/42");

        html.Should().Contain(">PO-0042 approved</h2>");
        html.Should().Contain(">Your purchase order was approved.</p>");
        html.Should().Contain("href=\"https://sms.example/portal/pages/po/42\"");
        html.Should().Contain("View Details");
    }

    [Fact]
    public void No_link_means_no_button()
    {
        var html = NotificationService.BuildEmailHtml("Title", "Body", null);

        html.Should().NotContain("View Details");
    }
}

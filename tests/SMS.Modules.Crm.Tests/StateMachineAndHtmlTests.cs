using FluentAssertions;
using SMS.Modules.Crm.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Crm.Tests;

/// <summary>Register 1F — the lead transition matrix (§6.3); D-21 — the activity notes sanitizer (BR-19).</summary>
public sealed class StateMachineAndHtmlTests
{
    private static readonly ILeadStateMachineService Machine = new LeadStateMachineService();

    [Theory]
    [InlineData("NEW", "CONTACTED")]
    [InlineData("NEW", "DISQUALIFIED")]
    [InlineData("CONTACTED", "QUALIFIED")]
    [InlineData("CONTACTED", "DISQUALIFIED")]
    [InlineData("QUALIFIED", "CONVERTED")]
    [InlineData("QUALIFIED", "DISQUALIFIED")]
    [InlineData("DISQUALIFIED", "NEW")]
    public void Valid_transitions_pass(string from, string to)
    {
        Machine.Invoking(m => m.ValidateTransition(from, to)).Should().NotThrow();
        Machine.GetAllowedTransitions(from).Should().Contain(to);
    }

    [Theory]
    [InlineData("NEW", "QUALIFIED", "*must be contacted*")]
    [InlineData("NEW", "CONVERTED", "*Only a qualified lead*")]
    [InlineData("CONTACTED", "CONVERTED", "*Only a qualified lead*")]
    [InlineData("CONTACTED", "NEW", "*Only a disqualified lead*")]
    [InlineData("QUALIFIED", "CONTACTED", "*cannot be moved*")]
    [InlineData("QUALIFIED", "NEW", "*Only a disqualified lead*")]
    [InlineData("DISQUALIFIED", "CONTACTED", "*only be re-opened to New*")]
    [InlineData("DISQUALIFIED", "QUALIFIED", "*only be re-opened to New*")]
    [InlineData("NEW", "NEW", "*already NEW*")]
    [InlineData("NEW", "WON", "*Status must be one of*")]
    public void Invalid_transitions_are_refused_with_a_message(string from, string to, string message) =>
        Machine.Invoking(m => m.ValidateTransition(from, to)).Should().Throw<BadRequestException>().WithMessage(message);

    [Theory]
    [InlineData("NEW")]
    [InlineData("QUALIFIED")]
    [InlineData("DISQUALIFIED")]
    public void A_converted_lead_is_terminal(string to)
    {
        Machine.Invoking(m => m.ValidateTransition("CONVERTED", to)).Should().Throw<ConflictException>();
        Machine.GetAllowedTransitions("CONVERTED").Should().BeEmpty();
    }

    // ── D-21 sanitizer ──

    [Fact]
    public void Formatting_tags_survive_without_attributes()
    {
        CrmHtml.Sanitize("<p class=\"x\" onclick=\"alert(1)\">Customer <b>confirmed</b> <em>specs</em></p><ul><li>AC</li><li>Ovens</li></ul>")
            .Should().Be("<p>Customer <b>confirmed</b> <em>specs</em></p><ul><li>AC</li><li>Ovens</li></ul>");
    }

    [Fact]
    public void Scripts_styles_links_and_images_are_removed()
    {
        var html = "<p>Hi<script>alert('x')</script></p><style>p{color:red}</style><a href=\"javascript:x\">link</a><img src=x onerror=alert(1)><iframe src=\"//evil\"></iframe>";
        var clean = CrmHtml.Sanitize(html)!;
        clean.Should().Be("<p>Hi</p>link");
        clean.Should().NotContain("script").And.NotContain("onerror").And.NotContain("href");
    }

    [Fact]
    public void Text_is_encoded_and_entities_kept_once()
    {
        CrmHtml.Sanitize("Price < 5 &amp; > 3 & \"quoted\"").Should().Be("Price &lt; 5 &amp; &gt; 3 &amp; &quot;quoted&quot;");
        CrmHtml.Sanitize("line one<br/>line two").Should().Be("line one<br>line two");
    }

    [Fact]
    public void Empty_markup_is_no_notes_and_the_plain_text_reads_well()
    {
        CrmHtml.Sanitize("<p><br></p>").Should().BeNull();
        CrmHtml.Sanitize("   ").Should().BeNull();
        CrmHtml.PlainText("<p>First</p><ul><li>One</li><li>Two</li></ul>").Should().Be("First\n• One\n• Two");
    }

    [Fact]
    public void Notes_over_one_megabyte_are_refused()
    {
        var huge = new string('x', CrmHtml.MaxLength + 1);
        FluentActions.Invoking(() => CrmHtml.Sanitize(huge)).Should().Throw<BadRequestException>();
    }
}

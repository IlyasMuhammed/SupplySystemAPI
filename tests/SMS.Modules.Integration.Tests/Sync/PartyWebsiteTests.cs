using FluentAssertions;
using SMS.Modules.Integration.Core.Sync;

namespace SMS.Modules.Integration.Tests.Sync;

/// <summary>
/// QuickBooks refused vendors with "www.prime.com" / "www..test.com" (error 2200, Malformed Web Site Address) in a live
/// sandbox run on 2026-10-13. The scheme is now added, and a value that is still not a web address is left off.
/// </summary>
public class PartyWebsiteTests
{
    [Theory]
    [InlineData("www.prime.com", "https://www.prime.com")]
    [InlineData("  example.co.uk  ", "https://example.co.uk")]
    [InlineData("http://shop.example.com/path?q=1", "http://shop.example.com/path?q=1")]
    [InlineData("https://www.example.com", "https://www.example.com")]
    [InlineData("my-company.pk", "https://my-company.pk")]
    public void A_usable_address_is_sent_with_a_scheme(string input, string expected) =>
        PartyWebsite.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("www..test.com")]
    [InlineData("localhost")]
    [InlineData("not a site")]
    [InlineData("ftp://files.example.com")]
    [InlineData("-bad-.example.com")]
    [InlineData("exa_mple.com")]
    public void A_value_that_is_not_a_web_address_is_left_off(string input)
    {
        PartyWebsite.Normalize(input).Should().BeNull();
        PartyWebsite.IsDropped(input).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_website_is_neither_sent_nor_warned_about(string? input)
    {
        PartyWebsite.Normalize(input).Should().BeNull();
        PartyWebsite.IsDropped(input).Should().BeFalse();
    }
}

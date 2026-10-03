using FluentAssertions;
using Moq;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Tests.QuickBooks;

public class AccountingProviderRegistryTests
{
    private static IAccountingProvider ProviderWithKey(string key)
    {
        var mock = new Mock<IAccountingProvider>();
        mock.SetupGet(p => p.ProviderKey).Returns(key);
        return mock.Object;
    }

    [Theory]
    [InlineData("QBO")]
    [InlineData("qbo")]
    [InlineData("Qbo")]
    public void Resolves_a_provider_by_key_case_insensitively(string requested)
    {
        var qbo = ProviderWithKey(ProviderKeys.QuickBooksOnline);
        var registry = new AccountingProviderRegistry([qbo, ProviderWithKey("XERO")]);

        registry.Get(requested).Should().BeSameAs(qbo);
    }

    [Fact]
    public void Resolves_each_of_several_providers()
    {
        var qbo  = ProviderWithKey("QBO");
        var xero = ProviderWithKey("XERO");
        var registry = new AccountingProviderRegistry([qbo, xero]);

        registry.Get("XERO").Should().BeSameAs(xero);
        registry.Get("QBO").Should().BeSameAs(qbo);
        registry.Keys.Should().BeEquivalentTo("QBO", "XERO");
    }

    [Fact]
    public void Unknown_key_throws_a_clear_InvalidOperationException_naming_the_registered_keys()
    {
        var registry = new AccountingProviderRegistry([ProviderWithKey("QBO")]);

        var act = () => registry.Get("SAGE");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'SAGE'*")
            .And.Message.Should().Contain("QBO");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_key_is_unknown(string? key)
    {
        var registry = new AccountingProviderRegistry([ProviderWithKey("QBO")]);

        var act = () => registry.Get(key!);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Empty_registry_says_nothing_is_registered()
    {
        var registry = new AccountingProviderRegistry([]);

        var act = () => registry.Get("QBO");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Registered: none*");
    }

    [Fact]
    public void Duplicate_registration_fails_at_construction()
    {
        var act = () => new AccountingProviderRegistry([ProviderWithKey("QBO"), ProviderWithKey("QBO")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Two accounting providers*'QBO'*");
    }

    [Fact]
    public void Duplicate_detection_ignores_case()
    {
        var act = () => new AccountingProviderRegistry([ProviderWithKey("QBO"), ProviderWithKey("qbo")]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_provider_without_a_key_is_rejected_at_construction()
    {
        var act = () => new AccountingProviderRegistry([ProviderWithKey(" ")]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*no ProviderKey*");
    }
}

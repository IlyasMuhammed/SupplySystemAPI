using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Lookups.Data;
using SMS.Modules.Lookups.Domain;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Repositories;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Lookups.Tests;

/// <summary>
/// SAP alignment, work package A — a currency other records use can be neither deleted (it is a hard
/// delete) nor given a new code (Finance documents and exchange rates keep the code). Who uses it is what
/// the registered ILookupReferenceCheckers answer; Lookups only asks.
/// </summary>
public class CurrencyReferenceGuardTests
{
    private readonly LookupsDbContext _db =
        new(new DbContextOptionsBuilder<LookupsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());

    private readonly Guid _usd = Guid.NewGuid();

    public CurrencyReferenceGuardTests()
    {
        _db.Currencies.Add(new Currency { Id = _usd, Name = "US Dollar", Code = "USD", Symbol = "$" });
        _db.SaveChanges();
    }

    private LookupsService Service() => new(new LookupsRepository(_db));

    private static ILookupReferenceChecker Checker(Guid referencedId)
    {
        var checker = new Mock<ILookupReferenceChecker>();
        checker.Setup(c => c.IsValueReferenced(It.IsAny<Guid>())).Returns((Guid id) => id == referencedId);
        return checker.Object;
    }

    private static ILookupReferenceChecker Nobody() => Checker(Guid.NewGuid());

    private Currency Stored()
    {
        _db.ChangeTracker.Clear();
        return _db.Currencies.Single(c => c.Id == _usd);
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_referenced_currency_cannot_be_deleted_and_stays()
    {
        var act = () => Service().DeleteCurrency(_usd, [Nobody(), Checker(_usd)]);

        act.Should().Throw<ConflictException>().WithMessage("US Dollar (USD) is used by existing records and cannot be deleted.");
        Stored().Should().NotBeNull();
    }

    [Fact]
    public void An_unreferenced_currency_is_deleted()
    {
        Service().DeleteCurrency(_usd, [Nobody()]).Should().BeTrue();

        _db.ChangeTracker.Clear();
        _db.Currencies.Any(c => c.Id == _usd).Should().BeFalse();
    }

    [Fact]
    public void Deleting_an_unknown_currency_is_not_found_and_asks_nobody()
    {
        var checker = new Mock<ILookupReferenceChecker>(MockBehavior.Strict);

        Service().DeleteCurrency(Guid.NewGuid(), [checker.Object]).Should().BeFalse();
    }

    [Fact]
    public void With_no_checkers_registered_a_delete_goes_ahead_as_before()
    {
        Service().DeleteCurrency(_usd, []).Should().BeTrue();
    }

    // ── Changing the code ────────────────────────────────────────────────────

    [Fact]
    public void A_referenced_currency_keeps_its_code()
    {
        var act = () => Service().UpdateCurrency(_usd, new CreateCurrencyRequest { Name = "US Dollar", Code = "USN", Symbol = "$" }, [Checker(_usd)]);

        act.Should().Throw<ConflictException>().WithMessage("The code of US Dollar (USD) cannot change*keep the code USD*name and symbol can still change*");
        Stored().Code.Should().Be("USD");
    }

    [Fact]
    public void A_referenced_currency_can_still_change_its_name_symbol_and_letter_case_of_its_code()
    {
        var ok = Service().UpdateCurrency(_usd, new CreateCurrencyRequest { Name = "United States Dollar", Code = " usd ", Symbol = "US$" }, [Checker(_usd)]);

        ok.Should().BeTrue();
        var stored = Stored();
        stored.Name.Should().Be("United States Dollar");
        stored.Symbol.Should().Be("US$");
        stored.Code.Should().Be("usd");
    }

    [Fact]
    public void An_unreferenced_currency_can_change_its_code()
    {
        Service().UpdateCurrency(_usd, new CreateCurrencyRequest { Name = "US Dollar", Code = "USN" }, [Nobody()]).Should().BeTrue();

        Stored().Code.Should().Be("USN");
    }

    [Fact]
    public void A_currency_that_had_no_code_may_be_given_one_even_when_referenced()
    {
        var bare = Guid.NewGuid();
        _db.Currencies.Add(new Currency { Id = bare, Name = "Bare", Code = null });
        _db.SaveChanges();

        Service().UpdateCurrency(bare, new CreateCurrencyRequest { Name = "Bare", Code = "BAR" }, [Checker(bare)]).Should().BeTrue();
    }

    [Fact]
    public void The_existing_validation_still_comes_first_for_an_unknown_currency()
    {
        Service().UpdateCurrency(Guid.NewGuid(), new CreateCurrencyRequest { Name = "X", Code = "XXX" }, [Nobody()]).Should().BeFalse();

        var empty = () => Service().UpdateCurrency(_usd, new CreateCurrencyRequest { Name = "US Dollar", Code = "" }, [Nobody()]);
        empty.Should().Throw<BadRequestException>();

        var emptyWhileUsed = () => Service().UpdateCurrency(_usd, new CreateCurrencyRequest { Name = "US Dollar", Code = "  " }, [Checker(_usd)]);
        emptyWhileUsed.Should().Throw<BadRequestException>("a blank code is bad input whoever uses the currency");
    }
}

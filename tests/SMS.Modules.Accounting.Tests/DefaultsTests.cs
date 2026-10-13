using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Accounting.Tests;

/// <summary>A42 §8 / D-24 / D-30 / D-52 — accounting defaults.</summary>
public class DefaultsTests
{
    [Fact]
    public async Task Get_groups_by_category_with_counts_and_locks_the_opening_balance_default_to_3900()
    {
        using var h = new AccountingHarness();
        var empty = await h.Defaults().GetAsync(h.Viewer());
        empty.RequiredCount.Should().Be(8);
        empty.AssignedRequiredCount.Should().Be(0);
        empty.Categories.Select(c => c.Category).Should().Equal(DefaultCategories.All);
        empty.Categories.SelectMany(c => c.Items).Should().HaveCount(16);
        empty.Categories.Single(c => c.Category == DefaultCategories.Revenue).Items.Select(i => i.DefaultKey)
            .Should().Equal(DefaultKeys.Revenue, DefaultKeys.DiscountGiven, DefaultKeys.ExchangeGainLoss, DefaultKeys.Rounding);

        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        var model = await h.Defaults().GetAsync(h.Viewer());

        var obe = model.Categories.SelectMany(c => c.Items).Single(i => i.DefaultKey == DefaultKeys.OpeningBalance);
        obe.IsLocked.Should().BeTrue();
        obe.Account!.AccountCode.Should().Be("3900");
        model.AssignedRequiredCount.Should().Be(1);
        h.Events.Of<AccountingDefaultAssignedEvent>().Should().ContainSingle().Which.AccountId.Should().Be(chart["3900"].Uuid);

        await ((Func<Task>)(() => h.Defaults().AssignAsync(h.Admin(), DefaultKeys.OpeningBalance, chart["3111"].Uuid)))
            .Should().ThrowAsync<BadRequestException>().WithMessage(DefaultsService.LockedMessage);
    }

    [Fact]
    public async Task An_assignment_must_be_an_active_leaf_of_the_same_organization()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        var other = await h.SeedChartAsync(Guid.NewGuid(), ("1111", RootTypes.Asset, AccountTypes.Cash));
        chart["1112"].IsActive = false;
        await h.Db.SaveChangesAsync();
        var defaults = h.Defaults();

        await ((Func<Task>)(() => defaults.AssignAsync(h.Admin(), DefaultKeys.Cash, chart["111"].Uuid)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Only Level 4 accounts can be assigned as defaults.");
        await ((Func<Task>)(() => defaults.AssignAsync(h.Admin(), DefaultKeys.Cash, chart["1112"].Uuid)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The default account must be active.");
        await ((Func<Task>)(() => defaults.AssignAsync(h.Admin(), DefaultKeys.Cash, other["1111"].Uuid)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Account not found.");
        await ((Func<Task>)(() => defaults.AssignAsync(h.Admin(), "DEFAULT_NOPE", chart["1111"].Uuid)))
            .Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => defaults.AssignAsync(h.Viewer(), DefaultKeys.Cash, chart["1111"].Uuid)))
            .Should().ThrowAsync<ForbiddenException>();

        var cash = await defaults.AssignAsync(h.Admin(), "default_cash", chart["1111"].Uuid);
        cash.Account!.AccountCode.Should().Be("1111");
        cash.Warning.Should().BeNull();
        h.Events.Of<AccountingDefaultAssignedEvent>().Should().Contain(e => e.DefaultKey == DefaultKeys.Cash && e.AccountCode == "1111");
    }

    [Fact]
    public async Task An_unexpected_account_type_is_only_a_warning()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);

        var bank = await h.Defaults().AssignAsync(h.Admin(), DefaultKeys.Bank, chart["1111"].Uuid); // a Cash account
        bank.Account!.AccountCode.Should().Be("1111");
        bank.Warning.Should().Be("Account 1111 is of type Cash; Bank is expected.");

        var check = await h.Defaults().ValidateAsync(h.Viewer());
        check.IsValid.Should().BeFalse();
        check.Warnings.Should().ContainSingle(w => w.DefaultKey == DefaultKeys.Bank);
        check.Problems.Should().BeEmpty();
        check.MissingRequired.Should().BeEquivalentTo(DefaultKeys.Receivable, DefaultKeys.Payable, DefaultKeys.Cash, DefaultKeys.Revenue,
            DefaultKeys.Cogs, DefaultKeys.RetainedEarnings);
    }

    [Fact]
    public async Task Save_all_assigns_everything_in_one_go_and_moves_the_status()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        await h.AddFiscalYearAsync();
        var items = new (string Key, string Code)[]
        {
            (DefaultKeys.Receivable, "1113"), (DefaultKeys.Payable, "2111"), (DefaultKeys.Cash, "1111"), (DefaultKeys.Bank, "1112"),
            (DefaultKeys.Revenue, "4111"), (DefaultKeys.Cogs, "5111"), (DefaultKeys.RetainedEarnings, "3111"), (DefaultKeys.OpeningBalance, "3900"),
        };
        var request = new SaveDefaultsRequest { Items = items.Select(i => new SaveDefaultItem { DefaultKey = i.Key, AccountUuid = chart[i.Code].Uuid }).ToList() };
        request.Items.Add(new SaveDefaultItem { DefaultKey = DefaultKeys.Rounding, AccountUuid = null });

        var model = await h.Defaults().SaveAllAsync(h.Admin(), request);

        model.AssignedRequiredCount.Should().Be(8);
        (await h.Defaults().ValidateAsync(h.Viewer())).IsValid.Should().BeTrue();
        (await h.Setup.GetAsync(h.Viewer())).Status.Should().Be(SetupStatuses.DefaultsSet);
        h.Events.Of<AccountingDefaultAssignedEvent>().Should().HaveCount(8);
    }

    [Fact]
    public async Task Save_all_reports_every_problem_and_saves_nothing()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        var request = new SaveDefaultsRequest
        {
            Items =
            [
                new() { DefaultKey = DefaultKeys.Cash, AccountUuid = chart["1111"].Uuid },
                new() { DefaultKey = DefaultKeys.Bank, AccountUuid = chart["11"].Uuid },
                new() { DefaultKey = "DEFAULT_WHAT", AccountUuid = chart["1111"].Uuid },
                new() { DefaultKey = DefaultKeys.OpeningBalance, AccountUuid = chart["3111"].Uuid },
                new() { DefaultKey = DefaultKeys.Cash, AccountUuid = chart["1112"].Uuid },
            ],
        };

        var ex = (await ((Func<Task>)(() => h.Defaults().SaveAllAsync(h.Admin(), request))).Should().ThrowAsync<DefaultsValidationException>()).Which;

        ex.Problems.Select(p => p.DefaultKey).Should().Equal(DefaultKeys.Bank, "DEFAULT_WHAT", DefaultKeys.OpeningBalance, DefaultKeys.Cash);
        (await h.Db.AccountingDefaults.CountAsync(d => d.DefaultKey == DefaultKeys.Cash && d.AccountId != null)).Should().Be(0);
    }

    [Fact]
    public async Task After_complete_a_required_default_cannot_be_cleared()
    {
        using var h = new AccountingHarness();
        var chart = await h.ReadyToCompleteAsync();
        await h.Setup.CompleteAsync(h.Admin());

        await ((Func<Task>)(() => h.Defaults().AssignAsync(h.Admin(), DefaultKeys.Cash, null)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("A required default cannot be cleared after the setup is complete.");
        await ((Func<Task>)(() => h.Defaults().SaveAllAsync(h.Admin(), new SaveDefaultsRequest { Items = [new() { DefaultKey = DefaultKeys.Payable }] })))
            .Should().ThrowAsync<DefaultsValidationException>();

        // Re-pointing a required default, or clearing an optional one, stays allowed.
        (await h.Defaults().AssignAsync(h.Admin(), DefaultKeys.Cash, chart["1112"].Uuid)).Account!.AccountCode.Should().Be("1112");
        (await h.Defaults().AssignAsync(h.Admin(), DefaultKeys.Rounding, null)).Account.Should().BeNull();
    }

    [Fact]
    public async Task An_assignment_that_became_invalid_is_reported_as_a_problem()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        await h.AssignRequiredDefaultsAsync(chart);
        chart["1113"].IsActive = false;
        await h.Db.SaveChangesAsync();

        var check = await h.Defaults().ValidateAsync(h.Viewer());

        check.IsValid.Should().BeFalse();
        check.MissingRequired.Should().Equal(DefaultKeys.Receivable);
        check.Problems.Should().ContainSingle().Which.Message.Should().Be("Account 1113 is inactive.");
    }

    [Fact]
    public async Task Defaults_are_per_organization()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        await h.Defaults().AssignAsync(h.Admin(), DefaultKeys.Cash, chart["1111"].Uuid);
        var other = Guid.NewGuid();

        var theirs = await h.Defaults().GetAsync(h.Viewer(other));

        theirs.Categories.SelectMany(c => c.Items).Should().OnlyContain(i => i.Account == null);
        (await h.Db.AccountingDefaults.IgnoreQueryFilters().CountAsync(d => d.OrganizationId == other)).Should().Be(16);
    }
}

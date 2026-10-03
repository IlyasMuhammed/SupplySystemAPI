using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Connections;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Integration.Tests.Setup;

public class ModeAndMatchingTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static ConnectionsHarness Harness() =>
        ConnectionsHarness.Create(extra: s =>
        {
            s.RemoveAll<IBaseCurrencyResolver>();
            s.AddScoped<IBaseCurrencyResolver>(_ => new FixedBaseCurrency(true, "PKR"));
        });

    private static async Task<T> As<T>(ConnectionsHarness h, Func<IIntegrationSettingsService, Task<T>> act)
    {
        await using var scope = h.Scope(Org);
        return await act(scope.ServiceProvider.GetRequiredService<IIntegrationSettingsService>());
    }

    private static async Task<IntegrationConnection> SeedAsync(
        ConnectionsHarness h, ConnectionStatus status = ConnectionStatus.NeedsSetup, bool accounts = true, bool matching = true, bool reference = true)
    {
        var c = await h.SeedConnectionAsync(Org, status);
        if (reference) await SetupTestKit.SeedReferenceAsync(h, c);
        await h.SeedSettingsAsync(c, s =>
        {
            if (accounts) { s.DefaultIncomeAccountId = "1"; s.DefaultExpenseAccountId = "3"; }
            if (matching) s.MatchingConfirmedAt = DateTime.UtcNow.AddDays(-1);
        });
        return c;
    }

    // ── Mode ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dry_run_is_always_allowed_and_starts_syncing_a_connection_in_setup()
    {
        await using var h = Harness();
        var c = await SeedAsync(h, accounts: false, matching: false, reference: false);

        var model = await As(h, s => s.SetModeAsync(new SetModeRequest { Mode = "DryRun" }, 7));

        model.Mode.Should().Be("DryRun");
        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(ConnectionStatus.Live, "Live status means syncing, in whatever mode");
        await using var db = h.OpenAs(Org);
        (await db.SettingsAudit.SingleAsync()).Should().Match<SettingsAuditEntry>(a => a.Area == "Mode" && a.Action == "Set DryRun");
    }

    [Theory]
    [InlineData("Revoked")]
    [InlineData("NotConnected")]
    public async Task Choosing_a_mode_does_not_revive_an_unusable_connection(string statusName)
    {
        var status = Enum.Parse<ConnectionStatus>(statusName);
        await using var h = Harness();
        var c = await SeedAsync(h, status);

        await As(h, s => s.SetModeAsync(new SetModeRequest { Mode = "DryRun" }, 7));

        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(status);
    }

    [Fact]
    public async Task Live_is_allowed_when_preflight_passes_and_matching_is_confirmed()
    {
        await using var h = Harness();
        var c = await SeedAsync(h);

        var model = await As(h, s => s.SetModeAsync(new SetModeRequest { Mode = "live" }, 7));

        model.Mode.Should().Be("Live");
        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(ConnectionStatus.Live);
    }

    [Fact]
    public async Task Live_is_refused_with_every_failing_check_listed()
    {
        await using var h = Harness();
        var c = await SeedAsync(h, accounts: false, matching: false);
        await using (var db = h.OpenAs(Org))
        {
            db.Connections.Single().Country = "US";
            await db.SaveChangesAsync();
        }

        var ex = (await FluentActions.Awaiting(() => As(h, s => s.SetModeAsync(new SetModeRequest { Mode = "Live" }, 7)))
            .Should().ThrowAsync<BadRequestException>()).Which;

        ex.Message.Should().StartWith("Live cannot be switched on yet.")
          .And.Contain("Default accounts")
          .And.Contain("Company country")
          .And.Contain("confirm matching");
        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(ConnectionStatus.NeedsSetup);
        await using var check = h.OpenAs(Org);
        (await check.Settings.SingleAsync()).Mode.Should().Be(SyncMode.DryRun);
    }

    [Fact]
    public async Task Live_is_refused_until_matching_is_confirmed_even_when_preflight_passes()
    {
        await using var h = Harness();
        await SeedAsync(h, matching: false);

        await FluentActions.Awaiting(() => As(h, s => s.SetModeAsync(new SetModeRequest { Mode = "Live" }, 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*confirm matching*");
    }

    [Theory]
    [InlineData("Paused")]
    [InlineData("1")]
    [InlineData("")]
    public async Task An_unknown_mode_is_refused(string mode)
    {
        await using var h = Harness();
        await SeedAsync(h);

        await FluentActions.Awaiting(() => As(h, s => s.SetModeAsync(new SetModeRequest { Mode = mode }, 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Mode*");
    }

    [Fact]
    public async Task Mode_needs_a_connection()
    {
        await using var h = Harness();

        await FluentActions.Awaiting(() => As(h, s => s.SetModeAsync(new SetModeRequest { Mode = "DryRun" }, 7)))
            .Should().ThrowAsync<ConflictException>();
    }

    // ── Matching sign-off ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirming_matching_records_when_and_keeps_the_first_confirmation()
    {
        await using var h = Harness();
        await SeedAsync(h, matching: false);

        var first  = await As(h, s => s.ConfirmMatchingAsync(new ConfirmMatchingCompleteRequest { Confirmed = true }, 7));
        var second = await As(h, s => s.ConfirmMatchingAsync(new ConfirmMatchingCompleteRequest { Confirmed = true }, 7));

        first.MatchingConfirmedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        second.MatchingConfirmedAt.Should().Be(first.MatchingConfirmedAt);
        await using var db = h.OpenAs(Org);
        (await db.SettingsAudit.CountAsync(a => a.Area == "Matching" && a.Action == "Confirmed")).Should().Be(2);
    }

    [Fact]
    public async Task Matching_can_be_reopened_in_dry_run()
    {
        await using var h = Harness();
        await SeedAsync(h);

        var model = await As(h, s => s.ConfirmMatchingAsync(new ConfirmMatchingCompleteRequest { Confirmed = false }, 7));

        model.MatchingConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task Matching_cannot_be_reopened_while_Live()
    {
        await using var h = Harness();
        await SeedAsync(h);
        await As(h, s => s.SetModeAsync(new SetModeRequest { Mode = "Live" }, 7));

        await FluentActions.Awaiting(() => As(h, s => s.ConfirmMatchingAsync(new ConfirmMatchingCompleteRequest { Confirmed = false }, 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*Switch to dry run first*");
    }
}

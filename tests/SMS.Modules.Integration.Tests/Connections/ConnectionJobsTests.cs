using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Jobs;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Tests.Connections;

/// <summary>
/// The recurring jobs, run through the real <c>TenantContext</c> with no HttpContext — exactly how
/// Hangfire runs them — so each connection's work happens under <see cref="HangfireTenantScope"/>.
/// </summary>
public class ConnectionJobsTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private static readonly Guid OrgC = Guid.NewGuid();

    private static async Task<T> RunJobAsync<TJob, T>(ConnectionsHarness h, Func<TJob, Task<T>> run) where TJob : notnull
    {
        await using var scope = h.Root.CreateAsyncScope();
        return await run(scope.ServiceProvider.GetRequiredService<TJob>());
    }

    // ── Token refresh ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_usable_connection_is_refreshed_under_its_own_organization()
    {
        var notifications = new Mock<INotificationService>();
        await using var h = ConnectionsHarness.Create(realTenant: true, extra: s => s.AddSingleton(notifications.Object));
        var a = await h.SeedConnectionAsync(OrgA, refresh: "RT-A");
        var b = await h.SeedConnectionAsync(OrgB, ConnectionStatus.NeedsSetup, refresh: "RT-B");
        await h.SeedConnectionAsync(OrgC, ConnectionStatus.NotConnected, refresh: "RT-C");

        var seen = new List<Guid?>();
        h.Auth.Refresh = (rt, _) =>
        {
            lock (seen) seen.Add(HangfireTenantScope.OrganizationId);
            return Task.FromResult(FakeAuthProvider.Grant("AT-new-" + rt, "RT-new-" + rt));
        };

        var summary = await RunJobAsync<TokenRefreshJob, TokenRefreshSummary>(h, j => j.RefreshAllAsync());

        summary.Refreshed.Should().Be(2);
        summary.Failed.Should().Be(0);
        h.Auth.RefreshedWith.Should().BeEquivalentTo(new[] { "RT-A", "RT-B" }, "a disconnected organization is not refreshed");
        seen.Should().BeEquivalentTo(new Guid?[] { OrgA, OrgB });
        HangfireTenantScope.OrganizationId.Should().BeNull();

        var vault = new CredentialVault(TestEncryption.Instance);
        vault.ReadRefreshToken(await h.ReloadConnectionAsync(a.Id)).Should().Be("RT-new-RT-A");
        vault.ReadRefreshToken(await h.ReloadConnectionAsync(b.Id)).Should().Be("RT-new-RT-B");
    }

    [Fact]
    public async Task One_organizations_dead_grant_does_not_stop_the_others()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var a = await h.SeedConnectionAsync(OrgA, refresh: "RT-A");
        var b = await h.SeedConnectionAsync(OrgB, refresh: "RT-B");
        h.Auth.Refresh = (rt, _) => rt == "RT-A"
            ? throw new AuthorizationRevokedException("invalid_grant")
            : Task.FromResult(FakeAuthProvider.Grant("AT-ok", "RT-ok"));

        var summary = await RunJobAsync<TokenRefreshJob, TokenRefreshSummary>(h, j => j.RefreshAllAsync());

        summary.Refreshed.Should().Be(1);
        summary.Failed.Should().Be(1);
        (await h.ReloadConnectionAsync(a.Id)).Status.Should().Be(ConnectionStatus.Revoked);
        (await h.ReloadConnectionAsync(b.Id)).Status.Should().Be(ConnectionStatus.Live);
        h.Outbox.Suspended.Select(s => s.ConnectionId).Should().Equal(a.Id);
    }

    [Fact]
    public async Task A_transient_failure_is_counted_and_changes_nothing()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var a = await h.SeedConnectionAsync(OrgA);
        h.Auth.Refresh = (_, _) => throw new HttpRequestException("timeout");

        var summary = await RunJobAsync<TokenRefreshJob, TokenRefreshSummary>(h, j => j.RefreshAllAsync());

        summary.Failed.Should().Be(0, "the current token was still valid, so the manager handed it out");
        (await h.ReloadConnectionAsync(a.Id)).Status.Should().Be(ConnectionStatus.Live);
    }

    [Fact]
    public async Task A_grant_nearing_its_hard_expiry_warns_whoever_connected_it_once_a_week()
    {
        var notifications = new Mock<INotificationService>();
        await using var h = ConnectionsHarness.Create(realTenant: true, jobs: j => j.ReconnectWarningDays = 30,
            extra: s => s.AddSingleton(notifications.Object));
        var a = await h.SeedConnectionAsync(OrgA, connectedBy: 42);
        // Intuit keeps answering with a lifetime inside the warning window — a hard expiry refresh cannot extend.
        h.Auth.Refresh = (_, _) => Task.FromResult(FakeAuthProvider.Grant("AT", "RT", refreshLife: TimeSpan.FromDays(20)));

        var first = await RunJobAsync<TokenRefreshJob, TokenRefreshSummary>(h, j => j.RefreshAllAsync());
        var again = await RunJobAsync<TokenRefreshJob, TokenRefreshSummary>(h, j => j.RefreshAllAsync());

        first.Warned.Should().Be(1);
        again.Warned.Should().Be(0, "warned less than a week ago");
        notifications.Verify(n => n.TryCreateAsync(It.Is<NotificationRequest>(r =>
            r.UserId == 42 && r.Title == "Reconnect QuickBooks" && r.Message.Contains("reconnected before"))), Times.Once);
        (await h.ReloadConnectionAsync(a.Id)).ReconnectWarnedAt.Should().NotBeNull();

        var weekLater = await RunJobAsync<TokenRefreshJob, TokenRefreshSummary>(h, j => j.RefreshAllAsync(DateTime.UtcNow.AddDays(8)));
        weekLater.Warned.Should().Be(1, "a week on, it is said again");
    }

    [Fact]
    public async Task With_nobody_to_notify_the_warning_goes_on_the_connection()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true, jobs: j => j.ReconnectWarningDays = 30);
        var a = await h.SeedConnectionAsync(OrgA, connectedBy: null);
        h.Auth.Refresh = (_, _) => Task.FromResult(FakeAuthProvider.Grant("AT", "RT", refreshLife: TimeSpan.FromDays(10)));

        var summary = await RunJobAsync<TokenRefreshJob, TokenRefreshSummary>(h, j => j.RefreshAllAsync());

        summary.Warned.Should().Be(1);
        var stored = await h.ReloadConnectionAsync(a.Id);
        stored.LastError.Should().Contain("must be reconnected before");
        stored.ReconnectWarnedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_grant_far_from_expiry_is_not_warned_about()
    {
        var notifications = new Mock<INotificationService>();
        await using var h = ConnectionsHarness.Create(realTenant: true, extra: s => s.AddSingleton(notifications.Object));
        await h.SeedConnectionAsync(OrgA);

        (await RunJobAsync<TokenRefreshJob, TokenRefreshSummary>(h, j => j.RefreshAllAsync())).Warned.Should().Be(0);
        notifications.VerifyNoOtherCalls();
    }

    // ── Reference refresh ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reference_data_is_refreshed_per_organization_and_a_failure_is_contained()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var a = await h.SeedConnectionAsync(OrgA);
        var b = await h.SeedConnectionAsync(OrgB, realmId: "REALM-B");
        await h.SeedConnectionAsync(OrgC, ConnectionStatus.Revoked);

        var seen = new List<(Guid Org, Guid? Scope)>();
        h.Provider.Setup(p => p.GetReferenceDataAsync(It.IsAny<ProviderContext>(), It.IsAny<CancellationToken>()))
                  .Returns<ProviderContext, CancellationToken>((ctx, _) =>
                  {
                      lock (seen) seen.Add((ctx.OrganizationId, HangfireTenantScope.OrganizationId));
                      if (ctx.RealmId == "REALM-B") throw new HttpRequestException("down");
                      return Task.FromResult(ProviderResult<RemoteReferenceData>.Ok(SampleReference.Data()));
                  });

        var summary = await RunJobAsync<ReferenceRefreshJob, ReferenceRefreshSummary>(h, j => j.RefreshAllAsync());

        summary.Refreshed.Should().Be(1);
        summary.Failed.Should().Be(1);
        seen.Should().OnlyContain(s => s.Org == s.Scope, "each fetch runs under its own organization");
        seen.Select(s => s.Org).Should().BeEquivalentTo(new[] { OrgA, OrgB }, "a revoked connection is skipped");

        await using var db = h.OpenAll();
        (await db.ReferenceSnapshots.IgnoreQueryFilters().CountAsync(s => s.ConnectionId == a.Id)).Should().Be(6);
        (await db.ReferenceSnapshots.IgnoreQueryFilters().CountAsync(s => s.ConnectionId == b.Id)).Should().Be(0);
    }

    // ── State-token cleanup ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Old_state_tokens_are_deleted_and_abandoned_connects_reset_across_organizations()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var abandoned = await h.SeedConnectionAsync(OrgA, ConnectionStatus.Connecting, realmId: null, withTokens: false);
        var pending   = await h.SeedConnectionAsync(OrgB, ConnectionStatus.Connecting, realmId: null, withTokens: false);
        var now       = DateTime.UtcNow;

        await using (var db = h.OpenAll())
        {
            OAuthStateToken Token(Guid org, string hashChar, DateTime expires, DateTime? used) => new()
            {
                OrganizationId = org, TokenHash = new string(hashChar[0], 64), UserId = 1, ExpiresAt = expires, UsedAt = used
            };
            db.OAuthStateTokens.AddRange(
                Token(OrgA, "a", now.AddDays(-2), null),            // expired long ago → deleted
                Token(OrgA, "b", now.AddDays(-3), now.AddDays(-3)), // used long ago → deleted
                Token(OrgA, "c", now.AddMinutes(-5), null),         // expired recently → kept for a day
                Token(OrgB, "d", now.AddMinutes(5), null));         // live → kept, and keeps OrgB Connecting
            await db.SaveChangesAsync();
        }

        var summary = await RunJobAsync<StateTokenCleanupJob, StateCleanupSummary>(h, j => j.CleanAsync(now));

        summary.TokensDeleted.Should().Be(2);
        summary.ConnectionsReset.Should().Be(1);
        (await h.ReloadConnectionAsync(abandoned.Id)).Status.Should().Be(ConnectionStatus.NotConnected);
        (await h.ReloadConnectionAsync(pending.Id)).Status.Should().Be(ConnectionStatus.Connecting);

        await using var check = h.OpenAll();
        (await check.OAuthStateTokens.IgnoreQueryFilters().Select(t => t.TokenHash.Substring(0, 1)).ToListAsync())
            .Should().BeEquivalentTo(new[] { "c", "d" });
    }

    [Fact]
    public async Task The_job_registrations_resolve_from_the_container()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        await using var scope = h.Root.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<TokenRefreshJob>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<ReferenceRefreshJob>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<StateTokenCleanupJob>().Should().NotBeNull();
        TokenRefreshJob.RecurringJobId.Should().Be("integration-token-refresh");
    }
}

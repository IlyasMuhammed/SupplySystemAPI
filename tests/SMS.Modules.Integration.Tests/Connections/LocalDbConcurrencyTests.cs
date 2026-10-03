using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Tests.Connections;

/// <summary>
/// What the in-memory provider cannot show: the single-use state token under a real race, and the
/// token refresh's row-version guard. Each test creates and drops its own LocalDB database.
/// </summary>
public class LocalDbConcurrencyTests
{
    private static readonly Guid Org = Guid.NewGuid();

    // ── State token ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_concurrent_callbacks_with_the_same_state_cannot_both_succeed()
    {
        await using var sql = await LocalDb.CreateAsync();
        var options = Options.Create(new QuickBooksOptions());

        for (var round = 0; round < 10; round++)
        {
            string raw;
            await using (var db = sql.OpenAs(Org))
            {
                raw = new OAuthStateService(db, options).Issue(Org, 1);
                await db.SaveChangesAsync();
            }

            using var start = new ManualResetEventSlim(false);
            var racers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                await using var db = sql.OpenAs(Guid.Empty, superAdmin: true);
                start.Wait();
                return await new OAuthStateService(db, options).RedeemAsync(raw);
            })).ToList();

            start.Set();
            var results = await Task.WhenAll(racers);

            results.Count(r => r.Status == StateRedemptionStatus.Redeemed).Should().Be(1, $"round {round}: exactly one callback may win");
            results.Where(r => !r.Succeeded).Should().OnlyContain(r => r.Status == StateRedemptionStatus.AlreadyUsed);
        }
    }

    // ── Token refresh ───────────────────────────────────────────────────────────────────────

    private static async Task<int> SeedAsync(LocalDb sql, DateTime accessExpiresAt)
    {
        await using var db = sql.OpenAs(Org);
        var connection = new IntegrationConnection
        {
            OrganizationId = Org, RealmId = "123", Status = ConnectionStatus.Live, CompanyName = "Race Co"
        };
        new CredentialVault(TestEncryption.Instance).Store(connection,
            new TokenGrant("AT-old", "RT-old", accessExpiresAt, DateTime.UtcNow.AddDays(90)));
        db.Connections.Add(connection);
        await db.SaveChangesAsync();
        return connection.Id;
    }

    private static TokenManager Manager(Core.Providers.IAccountingAuthProvider auth, IntegrationDbContextHolder holder, RecordingOutbox? outbox = null)
    {
        var services = new ServiceCollection();
        if (outbox is not null) services.AddSingleton<IOutboxControl>(outbox);
        return new TokenManager(holder.Db, new CredentialVault(TestEncryption.Instance), [auth],
            Options.Create(new IntegrationJobOptions()), services.BuildServiceProvider(), NullLogger<TokenManager>.Instance);
    }

    [Fact]
    public async Task A_refresh_that_loses_the_race_uses_the_winners_token_and_never_overwrites_it()
    {
        await using var sql = await LocalDb.CreateAsync();
        var id = await SeedAsync(sql, DateTime.UtcNow.AddMinutes(1));   // inside the refresh window

        // Worker B refreshes and commits while worker A is still waiting on Intuit.
        var authB = new FakeAuthProvider { Refresh = (_, _) => Task.FromResult(FakeAuthProvider.Grant("AT-B", "RT-B")) };
        var authA = new FakeAuthProvider();
        authA.Refresh = async (_, ct) =>
        {
            await using var holderB = new IntegrationDbContextHolder(sql.OpenAs(Org));
            (await Manager(authB, holderB).GetValidAccessTokenAsync(id, ct)).Should().Be("AT-B");
            return FakeAuthProvider.Grant("AT-A", "RT-A");
        };

        await using var holderA = new IntegrationDbContextHolder(sql.OpenAs(Org));
        var token = await Manager(authA, holderA).GetValidAccessTokenAsync(id);

        token.Should().Be("AT-B", "the loser adopts the token the winner stored");

        await using var check = sql.OpenAs(Org);
        var stored = await check.Connections.SingleAsync(c => c.Id == id);
        var vault  = new CredentialVault(TestEncryption.Instance);
        vault.ReadRefreshToken(stored).Should().Be("RT-B", "the winner's rotated refresh token is the one kept");
        vault.ReadAccessToken(stored).Should().Be("AT-B");
        stored.Status.Should().Be(ConnectionStatus.Live);
    }

    [Fact]
    public async Task Parallel_refreshes_persist_exactly_one_rotation()
    {
        await using var sql = await LocalDb.CreateAsync();
        var id = await SeedAsync(sql, DateTime.UtcNow.AddMinutes(-1));   // already expired

        // Both workers are inside Intuit's refresh at the same time, then both try to save.
        using var bothInside = new Barrier(2);
        var counter = 0;
        var auth = new FakeAuthProvider
        {
            Refresh = (_, _) =>
            {
                var n = Interlocked.Increment(ref counter);
                bothInside.SignalAndWait(TimeSpan.FromSeconds(10));
                return Task.FromResult(FakeAuthProvider.Grant($"AT-{n}", $"RT-{n}"));
            }
        };

        var workers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await using var holder = new IntegrationDbContextHolder(sql.OpenAs(Org));
            return await Manager(auth, holder).GetValidAccessTokenAsync(id);
        })).ToList();

        var tokens = await Task.WhenAll(workers);

        counter.Should().Be(2, "both really called Intuit");
        tokens.Distinct().Should().ContainSingle("the loser returns the winner's token");

        await using var check = sql.OpenAs(Org);
        var stored = await check.Connections.SingleAsync(c => c.Id == id);
        var vault  = new CredentialVault(TestEncryption.Instance);
        vault.ReadAccessToken(stored).Should().Be(tokens[0]);
        vault.ReadRefreshToken(stored).Should().Be(tokens[0].Replace("AT-", "RT-"), "access and refresh token come from the same grant");
    }

    [Fact]
    public async Task An_invalid_grant_caused_by_a_concurrent_rotation_is_not_mistaken_for_a_revocation()
    {
        await using var sql = await LocalDb.CreateAsync();
        var id     = await SeedAsync(sql, DateTime.UtcNow.AddMinutes(-1));
        var outbox = new RecordingOutbox();

        // A's refresh token is rotated away by B before Intuit answers A — so Intuit says invalid_grant.
        var authB = new FakeAuthProvider { Refresh = (_, _) => Task.FromResult(FakeAuthProvider.Grant("AT-B", "RT-B")) };
        var authA = new FakeAuthProvider();
        authA.Refresh = async (_, ct) =>
        {
            await using var holderB = new IntegrationDbContextHolder(sql.OpenAs(Org));
            await Manager(authB, holderB).GetValidAccessTokenAsync(id, ct);
            throw new AuthorizationRevokedException("invalid_grant");
        };

        await using var holderA = new IntegrationDbContextHolder(sql.OpenAs(Org));
        var token = await Manager(authA, holderA, outbox).GetValidAccessTokenAsync(id);

        token.Should().Be("AT-B");
        outbox.Suspended.Should().BeEmpty();
        await using var check = sql.OpenAs(Org);
        (await check.Connections.SingleAsync(c => c.Id == id)).Status.Should().Be(ConnectionStatus.Live);
    }

    [Fact]
    public async Task A_status_change_racing_a_refresh_is_replayed_not_lost()
    {
        await using var sql = await LocalDb.CreateAsync();
        var id = await SeedAsync(sql, DateTime.UtcNow.AddHours(1));

        await using var admin = sql.OpenAs(Org);
        var connection = await admin.Connections.SingleAsync(c => c.Id == id);   // admin reads...

        await using (var refresher = new IntegrationDbContextHolder(sql.OpenAs(Org)))   // ...a refresh commits...
            await Manager(new FakeAuthProvider(), refresher).ForceRefreshAsync(id);

        // ...then the admin's write lands on a stale row version and is replayed onto the fresh row.
        await ConnectionPersistence.SaveWithRetryAsync(admin, connection, c => c.LastError = "set by admin", default);

        await using var check = sql.OpenAs(Org);
        var stored = await check.Connections.SingleAsync(c => c.Id == id);
        stored.LastError.Should().Be("set by admin");
        new CredentialVault(TestEncryption.Instance).ReadAccessToken(stored).Should().Be("AT-refreshed", "the refresh is not undone");
    }
}

/// <summary>Owns a context for a TokenManager built by hand.</summary>
internal sealed class IntegrationDbContextHolder : IAsyncDisposable
{
    public Data.IntegrationDbContext Db { get; }
    public IntegrationDbContextHolder(Data.IntegrationDbContext db) => Db = db;
    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

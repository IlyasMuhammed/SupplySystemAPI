using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Connections;

namespace SMS.Modules.Integration.Tests.Connections;

public class OAuthStateServiceTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private readonly string _dbName = Guid.NewGuid().ToString();

    private OAuthStateService Service(Guid org, bool superAdmin = false, int minutes = 10) =>
        new(TestDb.OpenAs(_dbName, org, superAdmin), Options.Create(new QuickBooksOptions { StateTokenMinutes = minutes }));

    private async Task<string> IssueAsync(Guid org, int userId = 42, DateTime? now = null, int minutes = 10)
    {
        await using var db = TestDb.OpenAs(_dbName, org);
        var service = new OAuthStateService(db, Options.Create(new QuickBooksOptions { StateTokenMinutes = minutes }));
        var raw = service.Issue(org, userId, now);
        await db.SaveChangesAsync();
        return raw;
    }

    [Fact]
    public async Task Issue_returns_a_256_bit_url_safe_token_and_stores_only_its_hash()
    {
        var now = DateTime.UtcNow;
        var raw = await IssueAsync(OrgA, 42, now);

        raw.Should().HaveLength(43).And.MatchRegex("^[A-Za-z0-9_-]+$");

        await using var db = TestDb.OpenAs(_dbName, OrgA);
        var row = await db.OAuthStateTokens.SingleAsync();
        row.TokenHash.Should().Be(OAuthStateService.Hash(raw)).And.HaveLength(64).And.NotContain(raw);
        row.OrganizationId.Should().Be(OrgA);
        row.UserId.Should().Be(42);
        row.ExpiresAt.Should().BeCloseTo(now.AddMinutes(10), TimeSpan.FromSeconds(1));
        row.UsedAt.Should().BeNull();
    }

    [Fact]
    public async Task Issue_does_not_save_on_its_own()
    {
        await using var db = TestDb.OpenAs(_dbName, OrgA);
        new OAuthStateService(db, Options.Create(new QuickBooksOptions())).Issue(OrgA, 1);

        await using var other = TestDb.OpenAs(_dbName, OrgA);
        (await other.OAuthStateTokens.CountAsync()).Should().Be(0, "the caller commits it together with the connection's move to Connecting");
    }

    [Fact]
    public async Task Tokens_are_unique()
    {
        await using var db = TestDb.OpenAs(_dbName, OrgA);
        var service = new OAuthStateService(db, Options.Create(new QuickBooksOptions()));

        var tokens = Enumerable.Range(0, 50).Select(_ => service.Issue(OrgA, 1)).ToList();

        tokens.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Redeem_returns_the_organization_and_user_and_burns_the_token()
    {
        var raw = await IssueAsync(OrgA, 42);

        var result = await Service(Guid.Empty, superAdmin: true).RedeemAsync(raw);

        result.Status.Should().Be(StateRedemptionStatus.Redeemed);
        result.OrganizationId.Should().Be(OrgA);
        result.UserId.Should().Be(42);

        await using var db = TestDb.OpenAs(_dbName, OrgA);
        (await db.OAuthStateTokens.SingleAsync()).UsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_replayed_token_is_refused()
    {
        var raw = await IssueAsync(OrgA);
        (await Service(Guid.Empty, true).RedeemAsync(raw)).Succeeded.Should().BeTrue();

        var replay = await Service(Guid.Empty, true).RedeemAsync(raw);

        replay.Status.Should().Be(StateRedemptionStatus.AlreadyUsed);
        replay.OrganizationId.Should().Be(Guid.Empty, "a refused redemption names no organization");
    }

    [Fact]
    public async Task An_expired_token_is_refused_and_left_unused()
    {
        var issuedAt = DateTime.UtcNow.AddMinutes(-11);
        var raw      = await IssueAsync(OrgA, now: issuedAt);

        var result = await Service(Guid.Empty, true).RedeemAsync(raw);

        result.Status.Should().Be(StateRedemptionStatus.Expired);
        await using var db = TestDb.OpenAs(_dbName, OrgA);
        (await db.OAuthStateTokens.SingleAsync()).UsedAt.Should().BeNull();
    }

    [Fact]
    public async Task The_ttl_comes_from_options()
    {
        var raw = await IssueAsync(OrgA, minutes: 2);

        (await Service(Guid.Empty, true).RedeemAsync(raw, DateTime.UtcNow.AddMinutes(3))).Status
            .Should().Be(StateRedemptionStatus.Expired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token-we-issued")]
    public async Task Unknown_or_empty_state_is_invalid(string? state)
    {
        await IssueAsync(OrgA);

        (await Service(Guid.Empty, true).RedeemAsync(state)).Status.Should().Be(StateRedemptionStatus.Invalid);
    }

    [Fact]
    public async Task An_absurdly_long_state_is_invalid_without_a_lookup()
    {
        (await Service(Guid.Empty, true).RedeemAsync(new string('a', 5000))).Status.Should().Be(StateRedemptionStatus.Invalid);
    }

    [Fact]
    public async Task Redeem_finds_the_token_whatever_tenant_the_context_carries()
    {
        // The callback is anonymous: the token is the only thing that knows the organization, so the
        // lookup must not depend on the ambient tenant — even a non-bypassing context of another org.
        var raw = await IssueAsync(OrgA, 5);

        var result = await Service(OrgB, superAdmin: false).RedeemAsync(raw);

        result.Status.Should().Be(StateRedemptionStatus.Redeemed);
        result.OrganizationId.Should().Be(OrgA);
    }
}

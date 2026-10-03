using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Auth;
using SMS.Modules.Integration.Core.ApiClients;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Connections;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Integration.Tests.Auth;

public class ApiClientServiceTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static async Task<T> As<T>(ConnectionsHarness h, Guid org, Func<IApiClientService, Task<T>> act)
    {
        await using var scope = h.Scope(org);
        return await act(scope.ServiceProvider.GetRequiredService<IApiClientService>());
    }

    private static CreateApiClientRequest Create(string name = "Point of sale", params string[] scopes) =>
        new() { Name = name, Scopes = scopes.Length == 0 ? ["customers:write", "status:read"] : scopes.ToList() };

    // ── Create ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_client_is_created_with_canonical_scopes_and_audited()
    {
        await using var h = ConnectionsHarness.Create();
        var connection = await h.SeedConnectionAsync(OrgA);

        var client = await As(h, OrgA, s => s.CreateAsync(Create("  Point of sale  ", "STATUS:READ", "customers:write", "status:read"), 7));

        client.Name.Should().Be("Point of sale");
        client.Scopes.Should().Equal("customers:write", "status:read");
        client.IsActive.Should().BeTrue();
        client.Keys.Should().BeEmpty();

        await using var db = h.OpenAs(OrgA);
        var row = await db.ApiClients.SingleAsync();
        row.OrganizationId.Should().Be(OrgA);
        row.CreatedBy.Should().Be(7);
        var audit = await db.SettingsAudit.SingleAsync();
        (audit.Area, audit.Action, audit.ConnectionId).Should().Be(("ApiClient", "Created", connection.Id));
    }

    [Fact]
    public async Task A_client_can_be_created_before_QuickBooks_is_connected()
    {
        await using var h = ConnectionsHarness.Create();

        await As(h, OrgA, s => s.CreateAsync(Create(), 7));

        await using var db = h.OpenAs(OrgA);
        (await db.SettingsAudit.SingleAsync()).ConnectionId.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_name_is_required(string name)
    {
        await using var h = ConnectionsHarness.Create();

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.CreateAsync(Create(name), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*needs a name*");
    }

    [Fact]
    public async Task A_name_longer_than_a_hundred_characters_is_refused()
    {
        await using var h = ConnectionsHarness.Create();

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.CreateAsync(Create(new string('n', 101)), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*at most 100*");
        (await As(h, OrgA, s => s.CreateAsync(Create(new string('n', 100)), 7))).Name.Should().HaveLength(100);
    }

    [Theory]
    [InlineData("SCM")]
    [InlineData(" scm ")]
    public async Task The_name_SCM_is_reserved_for_SCMs_own_records(string name)
    {
        await using var h = ConnectionsHarness.Create();

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.CreateAsync(Create(name), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*reserved*");
    }

    [Fact]
    public async Task Names_are_unique_per_organization_ignoring_case()
    {
        await using var h = ConnectionsHarness.Create();
        await As(h, OrgA, s => s.CreateAsync(Create("Web Shop"), 7));

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.CreateAsync(Create("web shop"), 7)))
            .Should().ThrowAsync<ConflictException>();

        (await As(h, OrgB, s => s.CreateAsync(Create("Web Shop"), 7))).Name.Should().Be("Web Shop", "another organization may use the name");
    }

    [Fact]
    public async Task Unknown_scopes_are_refused()
    {
        await using var h = ConnectionsHarness.Create();

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.CreateAsync(Create("x", "customers:write", "payments:write"), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*'payments:write' is not an API scope*");
    }

    [Fact]
    public async Task At_least_one_scope_is_required()
    {
        await using var h = ConnectionsHarness.Create();

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.CreateAsync(new CreateApiClientRequest { Name = "x", Scopes = [] }, 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*at least one scope*");
    }

    // ── Keys ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_issued_key_is_shown_once_and_only_its_hash_is_stored()
    {
        await using var h = ConnectionsHarness.Create();
        var client = await As(h, OrgA, s => s.CreateAsync(Create(), 7));
        var expiry = DateTime.UtcNow.AddDays(90);

        var issued = await As(h, OrgA, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest { ExpiresAt = expiry }, 7));

        issued.ApiKey.Should().MatchRegex("^sqb_[A-Za-z0-9_-]{40}$");
        issued.KeyPrefix.Should().Be(issued.ApiKey[..12]);
        issued.TenantId.Should().Be(OrgA);
        issued.ExpiresAt.Should().Be(expiry);

        await using var db = h.OpenAs(OrgA);
        var row = await db.ApiClientKeys.SingleAsync();
        row.KeyHash.Should().Be(ApiKeyGenerator.Hash(issued.ApiKey));
        row.KeyPrefix.Should().Be(issued.KeyPrefix);
        row.Uuid.Should().Be(issued.KeyId);

        // The key itself is nowhere in the database, audit included.
        var everything = string.Join("|", (await db.SettingsAudit.ToListAsync()).Select(a => a.BeforeJson + a.AfterJson))
                       + row.KeyHash + row.KeyPrefix;
        everything.Should().NotContain(issued.ApiKey);

        var listed = (await As(h, OrgA, s => s.ListAsync())).Single();
        listed.Keys.Should().ContainSingle().Which.IsActive.Should().BeTrue();
        System.Text.Json.JsonSerializer.Serialize(listed).Should().NotContain(issued.ApiKey).And.NotContain(row.KeyHash);
    }

    [Fact]
    public async Task At_most_two_keys_are_active_at_once()
    {
        await using var h = ConnectionsHarness.Create();
        var client = await As(h, OrgA, s => s.CreateAsync(Create(), 7));
        var first  = await As(h, OrgA, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest(), 7));
        await As(h, OrgA, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest(), 7));

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest(), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*already has 2 active keys*");

        // Rotation: revoke the old one, and a new one can be issued.
        await As(h, OrgA, s => s.RevokeKeyAsync(client.Id, first.KeyId, 7));
        (await As(h, OrgA, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest(), 7))).ApiKey.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task An_expired_key_does_not_count_against_the_limit()
    {
        await using var h = ConnectionsHarness.Create();
        var client = await As(h, OrgA, s => s.CreateAsync(Create(), 7));
        await As(h, OrgA, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest(), 7));
        await As(h, OrgA, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest(), 7));
        await using (var db = h.OpenAs(OrgA))
        {
            (await db.ApiClientKeys.FirstAsync()).ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        (await As(h, OrgA, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest(), 7))).ApiKey.Should().StartWith("sqb_");
    }

    [Fact]
    public async Task An_expiry_in_the_past_is_refused()
    {
        await using var h = ConnectionsHarness.Create();
        var client = await As(h, OrgA, s => s.CreateAsync(Create(), 7));

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest { ExpiresAt = DateTime.UtcNow.AddMinutes(-1) }, 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*must be in the future*");
    }

    [Fact]
    public async Task Revoking_a_key_is_recorded_and_idempotent()
    {
        await using var h = ConnectionsHarness.Create();
        var client = await As(h, OrgA, s => s.CreateAsync(Create(), 7));
        var key    = await As(h, OrgA, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest(), 7));

        var after = await As(h, OrgA, s => s.RevokeKeyAsync(client.Id, key.KeyId, 7));
        var again = await As(h, OrgA, s => s.RevokeKeyAsync(client.Id, key.KeyId, 7));

        after.Keys.Single().IsActive.Should().BeFalse();
        after.Keys.Single().RevokedAt.Should().NotBeNull();
        again.Keys.Single().RevokedAt.Should().Be(after.Keys.Single().RevokedAt);

        await using var db = h.OpenAs(OrgA);
        (await db.SettingsAudit.CountAsync(a => a.Action == "KeyRevoked")).Should().Be(1);
    }

    [Fact]
    public async Task Revoking_an_unknown_key_is_not_found()
    {
        await using var h = ConnectionsHarness.Create();
        var client = await As(h, OrgA, s => s.CreateAsync(Create(), 7));

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.RevokeKeyAsync(client.Id, Guid.NewGuid(), 7)))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Deactivating_a_client_revokes_all_its_keys_and_blocks_new_ones()
    {
        await using var h = ConnectionsHarness.Create();
        var client = await As(h, OrgA, s => s.CreateAsync(Create(), 7));
        await As(h, OrgA, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest(), 7));
        await As(h, OrgA, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest(), 7));

        var deactivated = await As(h, OrgA, s => s.DeactivateAsync(client.Id, 7));

        deactivated.IsActive.Should().BeFalse();
        deactivated.Keys.Should().HaveCount(2).And.OnlyContain(k => !k.IsActive && k.RevokedAt != null);
        await FluentActions.Awaiting(() => As(h, OrgA, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest(), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*deactivated*");
    }

    // ── Isolation ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_organizations_clients_are_invisible_and_untouchable()
    {
        await using var h = ConnectionsHarness.Create();
        var client = await As(h, OrgA, s => s.CreateAsync(Create(), 7));
        var key    = await As(h, OrgA, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest(), 7));

        (await As(h, OrgB, s => s.ListAsync())).Should().BeEmpty();
        await FluentActions.Awaiting(() => As(h, OrgB, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest(), 7)))
            .Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => As(h, OrgB, s => s.RevokeKeyAsync(client.Id, key.KeyId, 7)))
            .Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => As(h, OrgB, s => s.DeactivateAsync(client.Id, 7)))
            .Should().ThrowAsync<NotFoundException>();

        (await As(h, OrgA, s => s.ListAsync())).Single().IsActive.Should().BeTrue();
    }
}

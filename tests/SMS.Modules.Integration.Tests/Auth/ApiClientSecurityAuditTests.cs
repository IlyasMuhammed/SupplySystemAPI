using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.ApiClients;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Connections;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Integration.Tests.Auth;

/// <summary>
/// Cross-cutting security audit: API clients and keys as a platform super admin acting in organization B reaches
/// them. A super admin's request bypasses the tenant query filter, so every by-id or listing query in
/// ApiClientService that relies on the filter alone reaches organization A's clients — and a key minted for one
/// of them authenticates as organization A (ApiClientKey.OrganizationId is copied from the client). Every action
/// must answer for the caller's own organization only: another organization's client is "not found".
/// </summary>
public class ApiClientSecurityAuditTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static async Task<T> As<T>(ConnectionsHarness h, Guid org, Func<IApiClientService, Task<T>> act, bool superAdmin = false)
    {
        await using var scope = h.Scope(org);
        scope.ServiceProvider.GetRequiredService<StaticTenantContext>().IsSuperAdmin = superAdmin;
        return await act(scope.ServiceProvider.GetRequiredService<IApiClientService>());
    }

    private static CreateApiClientRequest Create(string name = "Point of sale") =>
        new() { Name = name, Scopes = ["customers:write", "status:read"] };

    [Fact]
    public async Task SecurityAudit_a_super_admin_in_another_org_cannot_list_or_mint_keys_for_or_revoke_another_orgs_api_client()
    {
        await using var h = ConnectionsHarness.Create();
        var client = await As(h, OrgA, s => s.CreateAsync(Create(), 7));
        var key    = await As(h, OrgA, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest(), 7));

        // Listing: org A's client (its key prefixes and last use) is not org B's business.
        (await As(h, OrgB, s => s.ListAsync(), superAdmin: true)).Should().BeEmpty(
            "a super admin working in org B must see org B's API clients only");

        // Minting a key for org A's client would hand org B's super admin a working credential for org A's data.
        await FluentActions.Awaiting(() => As(h, OrgB, s => s.IssueKeyAsync(client.Id, new IssueApiKeyRequest(), 1), superAdmin: true))
            .Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => As(h, OrgB, s => s.RevokeKeyAsync(client.Id, key.KeyId, 1), superAdmin: true))
            .Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => As(h, OrgB, s => s.DeactivateAsync(client.Id, 1), superAdmin: true))
            .Should().ThrowAsync<NotFoundException>();

        await using var all = h.OpenAll();
        (await all.ApiClientKeys.CountAsync()).Should().Be(1, "no key was minted for org A's client from org B");
        (await all.ApiClientKeys.SingleAsync()).RevokedAt.Should().BeNull();
        (await all.ApiClients.SingleAsync()).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task SecurityAudit_a_super_admin_in_another_org_may_reuse_a_name_another_org_has()
    {
        // Names are unique per organization (the index is (OrganizationId, Name)). A 409 here would both wrongly
        // refuse the name and tell org B's super admin which names org A's integrations use.
        await using var h = ConnectionsHarness.Create();
        await As(h, OrgA, s => s.CreateAsync(Create("Web Shop"), 7));

        (await As(h, OrgB, s => s.CreateAsync(Create("Web Shop"), 1), superAdmin: true)).Name.Should().Be("Web Shop");
    }
}

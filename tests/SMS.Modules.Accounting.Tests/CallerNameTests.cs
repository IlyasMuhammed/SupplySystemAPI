using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Accounting.Controllers;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Accounting.Tests;

/// <summary>
/// A47 QA-1 — the caller's display name comes from the organization's user directory by user id (the SMS token has no
/// name claim), else the claim; a missing or failing directory never fails the request. The controller filter puts it
/// where every Accounting action's caller (and so every name snapshot) reads it.
/// </summary>
public sealed class CallerNameTests
{
    private sealed class FakeUsers : IOrgUserDirectory
    {
        public Dictionary<(Guid, int), string> Names { get; } = [];
        public bool Throw { get; set; }
        public Task<IReadOnlyList<OrgUserInfo>> GetUsersAsync(Guid organizationId, IReadOnlyCollection<int> userIds, CancellationToken ct = default) =>
            Throw ? throw new InvalidOperationException("auth down")
                : Task.FromResult<IReadOnlyList<OrgUserInfo>>(userIds.Where(id => Names.ContainsKey((organizationId, id)))
                    .Select(id => new OrgUserInfo(id, Names[(organizationId, id)], "x@y", true, [])).ToList());
        public Task<IReadOnlyList<OrgUserInfo>> SearchAsync(Guid organizationId, string? search, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OrgUserInfo>>([]);
    }

    private static ClaimsPrincipal User(int id, string? nameClaim = null)
    {
        var claims = new List<Claim> { new("sub", id.ToString()) };
        if (nameClaim is not null) claims.Add(new Claim("name", nameClaim));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task The_directory_name_wins_and_the_claim_is_the_fallback()
    {
        var org = Guid.NewGuid();
        var users = new FakeUsers();
        users.Names[(org, 7)] = " Cleo Clerk ";
        (await AccountingCaller.ResolveNameAsync(User(7), org, users)).Should().Be("Cleo Clerk", "the token has no name claim (QA-1)");
        (await AccountingCaller.ResolveNameAsync(User(8, "Claimed"), org, users)).Should().Be("Claimed", "not in the directory: the claim");
        (await AccountingCaller.ResolveNameAsync(User(7), Guid.NewGuid(), users)).Should().BeNull("another organization's user is absent");
        users.Throw = true;
        (await AccountingCaller.ResolveNameAsync(User(7, "Claimed"), org, users)).Should().Be("Claimed", "a failing directory never fails");
        (await AccountingCaller.ResolveNameAsync(User(7), org, null)).Should().BeNull();
    }

    [Fact]
    public async Task The_controller_filter_hands_the_name_to_the_caller()
    {
        var org = Guid.NewGuid();
        var users = new FakeUsers();
        users.Names[(org, 7)] = "Cleo Clerk";
        var services = new ServiceCollection()
            .AddSingleton<ITenantContext>(new StaticTenantContext { OrganizationId = org })
            .AddSingleton<IOrgUserDirectory>(users)
            .BuildServiceProvider();
        var http = new DefaultHttpContext { User = User(7), RequestServices = services };
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var executing = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), controller: new object());
        var ran = false;
        await new ResolveAccountingCallerNameAttribute().OnActionExecutionAsync(executing, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(action, [], new object()));
        });
        ran.Should().BeTrue();
        var name = http.Items[ResolveAccountingCallerNameAttribute.ItemKey] as string;
        name.Should().Be("Cleo Clerk");
        AccountingCaller.From(http.User, new StaticTenantContext { OrganizationId = org }, name).UserName.Should().Be("Cleo Clerk");
        typeof(JournalEntriesController).GetCustomAttributes(typeof(ResolveAccountingCallerNameAttribute), inherit: true).Should().NotBeEmpty(
            "every A47 controller derives from AccountingControllerBase");
    }

    [Fact]
    public async Task REV_14_the_name_is_cached_per_organization_and_user()
    {
        var org = Guid.NewGuid();
        var users = new CountingUsers();
        users.Inner.Names[(org, 7)] = "Cleo Clerk";
        var services = new ServiceCollection()
            .AddMemoryCache()
            .AddSingleton<ITenantContext>(new StaticTenantContext { OrganizationId = org })
            .AddSingleton<IOrgUserDirectory>(users)
            .BuildServiceProvider();
        for (var i = 0; i < 3; i++)
        {
            var http = new DefaultHttpContext { User = User(7), RequestServices = services };
            var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
            await new ResolveAccountingCallerNameAttribute().OnActionExecutionAsync(
                new ActionExecutingContext(action, [], new Dictionary<string, object?>(), new object()),
                () => Task.FromResult(new ActionExecutedContext(action, [], new object())));
            http.Items[ResolveAccountingCallerNameAttribute.ItemKey].Should().Be("Cleo Clerk");
        }
        users.Calls.Should().Be(1, "later requests read the cache");
    }

    private sealed class CountingUsers : IOrgUserDirectory
    {
        public FakeUsers Inner { get; } = new();
        public int Calls { get; private set; }
        public Task<IReadOnlyList<OrgUserInfo>> GetUsersAsync(Guid organizationId, IReadOnlyCollection<int> userIds, CancellationToken ct = default)
        {
            Calls++;
            return Inner.GetUsersAsync(organizationId, userIds, ct);
        }
        public Task<IReadOnlyList<OrgUserInfo>> SearchAsync(Guid organizationId, string? search, int limit, CancellationToken ct = default) =>
            Inner.SearchAsync(organizationId, search, limit, ct);
    }
}

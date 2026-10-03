using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;

namespace SMS.Modules.Integration.Tests.Connections;

/// <summary>
/// A whole connection lifecycle — connect, callback, refresh, a revoked grant, reconnect, disconnect —
/// with distinctive token values, then a search for them everywhere they could leak: every log line
/// (with exceptions), every model returned, every audit row, every stored column other than the two
/// ciphertexts.
/// </summary>
public class TokenRedactionTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private const string Access1  = "AT-LEAKCHECK-1111";
    private const string Refresh1 = "RT-LEAKCHECK-2222";
    private const string Access2  = "AT-LEAKCHECK-3333";
    private const string Refresh2 = "RT-LEAKCHECK-4444";

    [Fact]
    public async Task No_token_value_reaches_a_log_a_model_or_an_audit_row()
    {
        var logs = new CapturingLoggerProvider();
        await using var h = ConnectionsHarness.Create(realTenant: true, extra: s => s.AddSingleton<ILoggerProvider>(logs));
        var returned = new List<object>();

        h.Auth.Exchange = (_, _) => Task.FromResult(FakeAuthProvider.Grant(Access1, Refresh1, TimeSpan.FromMinutes(1)));
        var refreshes = 0;
        h.Auth.Refresh = (_, _) => ++refreshes == 1
            ? Task.FromResult(FakeAuthProvider.Grant(Access2, Refresh2, TimeSpan.FromMinutes(1)))
            : throw new AuthorizationRevokedException("Intuit refused the refresh token: invalid_grant.");
        h.Auth.Revoke = (_, _) => throw new AccountingAuthException("Intuit did not confirm the revocation (invalid_token, HTTP 400).");

        async Task<string> ConnectAndCallbackAsync()
        {
            await using (var scope = h.Scope(Org))
                returned.Add(await scope.ServiceProvider.GetRequiredService<IQuickBooksConnectionService>().ConnectAsync(7));
            await using (var anonymous = h.AnonymousScope())
                return await anonymous.ServiceProvider.GetRequiredService<IQuickBooksCallbackService>()
                    .HandleCallbackAsync("code", h.Auth.ConsentStates.Last(), "9130000000000001", null);
        }

        (await ConnectAndCallbackAsync()).Should().EndWith("result=connected");

        await using (var scope = h.Scope(Org))
        {
            var tokens = scope.ServiceProvider.GetRequiredService<ITokenManager>();
            var id     = (await h.OpenAs(Org).Connections.SingleAsync()).Id;

            (await tokens.GetValidAccessTokenAsync(id)).Should().Be(Access2, "the first token was inside the refresh window");
        }

        await using (var scope = h.Scope(Org))
        {
            var tokens = scope.ServiceProvider.GetRequiredService<ITokenManager>();
            var id     = (await h.OpenAs(Org).Connections.SingleAsync()).Id;
            var ex = await FluentActions.Awaiting(() => tokens.GetValidAccessTokenAsync(id)).Should().ThrowAsync<ConnectionUnavailableException>();
            returned.Add(ex.Which.ToString());
        }

        h.Auth.Exchange = (_, _) => Task.FromResult(FakeAuthProvider.Grant(Access1, Refresh1));
        (await ConnectAndCallbackAsync()).Should().EndWith("result=connected");

        await using (var scope = h.Scope(Org))
        {
            var service = scope.ServiceProvider.GetRequiredService<IQuickBooksConnectionService>();
            returned.Add(await service.GetStatusAsync());
            returned.Add(await service.TestAsync());
            returned.Add(await service.DisconnectAsync(7));
        }

        // Everywhere a token could have gone.
        var everything = new List<string> { logs.All };
        everything.AddRange(returned.Select(r => r as string ?? System.Text.Json.JsonSerializer.Serialize(r)));
        await using (var db = h.OpenAll())
        {
            everything.AddRange(await db.SettingsAudit.IgnoreQueryFilters().Select(a => a.BeforeJson + " " + a.AfterJson + " " + a.Action).ToListAsync());
            everything.AddRange(await db.Connections.IgnoreQueryFilters().Select(c => c.LastError + " " + c.CompanyName).ToListAsync());
            everything.AddRange(await db.ReferenceSnapshots.IgnoreQueryFilters().Select(s => s.Json).ToListAsync());
        }

        logs.Lines.Should().NotBeEmpty("the lifecycle logs — which is what makes this check meaningful");
        var joined = string.Join("\n", everything);
        foreach (var secret in new[] { Access1, Refresh1, Access2, Refresh2, "LEAKCHECK" })
            joined.Should().NotContain(secret);
    }
}

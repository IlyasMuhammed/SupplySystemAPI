using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Connections;

/// <summary>
/// The anonymous callback, run through the real <c>TenantContext</c>: no user on the request, so the
/// tenant must come from the state token and nowhere else.
/// </summary>
public class OAuthCallbackTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private const string Return = "http://localhost:4200/portal/pages/integrations/quickbooks";
    private const string Realm  = "9130000000000001";

    private static async Task<string> StartConnectAsync(ConnectionsHarness h, Guid org, int userId = 7)
    {
        await using var scope = h.Scope(org);
        await scope.ServiceProvider.GetRequiredService<IQuickBooksConnectionService>().ConnectAsync(userId);
        return h.Auth.ConsentStates.Last();
    }

    private static async Task<string> CallbackAsync(
        ConnectionsHarness h, string? state, string? code = "auth-code", string? realmId = Realm, string? error = null)
    {
        await using var scope = h.AnonymousScope();
        return await scope.ServiceProvider.GetRequiredService<IQuickBooksCallbackService>()
            .HandleCallbackAsync(code, state, realmId, error);
    }

    private static async Task<IntegrationConnection> ConnectionOf(ConnectionsHarness h, Guid org)
    {
        await using var db = h.OpenAll();
        return await db.Connections.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.OrganizationId == org);
    }

    [Fact]
    public async Task A_valid_callback_connects_the_state_tokens_organization()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var state = await StartConnectAsync(h, OrgA);

        var redirect = await CallbackAsync(h, state, code: "the-code");

        redirect.Should().Be(Return + "?result=connected");
        h.Auth.ExchangedCodes.Should().Equal("the-code");

        var c = await ConnectionOf(h, OrgA);
        c.Status.Should().Be(ConnectionStatus.NeedsSetup, "matching has not been confirmed for this company yet");
        c.RealmId.Should().Be(Realm);
        c.Environment.Should().Be(IntegrationEnvironment.Sandbox);
        c.ConnectedByUserId.Should().Be(7);
        c.ConnectedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        c.EncryptedAccessToken.Should().NotBeNull().And.NotContain("AT-the-code");
        var vault = new CredentialVault(TestEncryption.Instance);
        vault.ReadAccessToken(c).Should().Be("AT-the-code");
        vault.ReadRefreshToken(c).Should().Be("RT-the-code");
    }

    [Fact]
    public async Task The_callback_runs_as_the_state_tokens_organization_and_not_as_a_super_admin()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var state = await StartConnectAsync(h, OrgA);

        // Capture the tenant the reference fetch runs under — it happens inside the callback.
        Guid? seenOrg = null; bool? seenSuper = null;

        await using var scope = h.AnonymousScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.IsSuperAdmin.Should().BeTrue("an anonymous request bypasses the tenant filter — which is the danger");

        h.Provider.Setup(p => p.GetReferenceDataAsync(It.IsAny<ProviderContext>(), It.IsAny<CancellationToken>()))
                  .Returns<ProviderContext, CancellationToken>((_, _) =>
                  {
                      seenOrg   = tenant.OrganizationId;
                      seenSuper = tenant.IsSuperAdmin;
                      return Task.FromResult(ProviderResult<RemoteReferenceData>.Ok(SampleReference.Data()));
                  });

        var redirect = await scope.ServiceProvider.GetRequiredService<IQuickBooksCallbackService>()
            .HandleCallbackAsync("code", state, Realm, null);

        redirect.Should().EndWith("result=connected");
        seenOrg.Should().Be(OrgA);
        seenSuper.Should().BeFalse();
        HangfireTenantScope.OrganizationId.Should().BeNull("the scope is cleared when the callback ends");
        tenant.IsSuperAdmin.Should().BeTrue("and the request is anonymous again afterwards");

        await using var db = h.OpenAll();
        (await db.SettingsAudit.IgnoreQueryFilters().Where(a => a.Action == "Connected").SingleAsync())
            .OrganizationId.Should().Be(OrgA);
        (await db.ReferenceSnapshots.IgnoreQueryFilters().Select(s => s.OrganizationId).Distinct().ToListAsync())
            .Should().Equal(OrgA);
    }

    [Fact]
    public async Task Another_organizations_connection_is_never_touched()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var b     = await h.SeedConnectionAsync(OrgB, ConnectionStatus.Revoked, realmId: "9130000000000002", access: "AT-B");
        var state = await StartConnectAsync(h, OrgA);

        await CallbackAsync(h, state);

        var storedB = await h.ReloadConnectionAsync(b.Id);
        storedB.Status.Should().Be(ConnectionStatus.Revoked);
        storedB.RealmId.Should().Be("9130000000000002");
        new CredentialVault(TestEncryption.Instance).ReadAccessToken(storedB).Should().Be("AT-B");
        (await ConnectionOf(h, OrgA)).RealmId.Should().Be(Realm);
    }

    [Fact]
    public async Task A_company_already_connected_to_another_organization_is_refused_before_any_token_exists()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        // Organization B holds the company (even Revoked counts — its records are mapped there).
        await h.SeedConnectionAsync(OrgB, ConnectionStatus.Revoked, realmId: Realm, access: "AT-B");
        var state = await StartConnectAsync(h, OrgA);

        var redirect = await CallbackAsync(h, state);

        redirect.Should().Be(Return + "?result=error&reason=realm_in_use");
        h.Auth.ExchangedCodes.Should().BeEmpty("the code is never exchanged for a company that is taken");
        var a = await ConnectionOf(h, OrgA);
        a.RealmId.Should().BeNull();
        a.EncryptedAccessToken.Should().BeNull();
        a.Status.Should().Be(ConnectionStatus.NotConnected);
        a.LastError.Should().Contain("another organization");
    }

    [Fact]
    public async Task A_company_another_organization_disconnected_without_mappings_can_be_connected()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        await h.SeedConnectionAsync(OrgB, ConnectionStatus.NotConnected, realmId: Realm, access: "AT-B");
        var state = await StartConnectAsync(h, OrgA);

        (await CallbackAsync(h, state)).Should().EndWith("result=connected");
    }

    [Fact]
    public async Task Reference_data_and_company_facts_are_loaded_straight_away()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var state = await StartConnectAsync(h, OrgA);

        await CallbackAsync(h, state);

        var c = await ConnectionOf(h, OrgA);
        c.CompanyName.Should().Be("Sandbox Company PK");
        c.HomeCurrencyCode.Should().Be("PKR");
        c.Country.Should().Be("PK");
        c.MultiCurrencyEnabled.Should().BeFalse();
        await using var db = h.OpenAll();
        (await db.ReferenceSnapshots.IgnoreQueryFilters().CountAsync(s => s.ConnectionId == c.Id)).Should().Be(6);
    }

    [Fact]
    public async Task A_failed_reference_load_does_not_fail_the_connection()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        h.Provider.Setup(p => p.GetReferenceDataAsync(It.IsAny<ProviderContext>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new InvalidOperationException("boom"));
        var state = await StartConnectAsync(h, OrgA);

        var redirect = await CallbackAsync(h, state);

        redirect.Should().EndWith("result=connected");
        (await ConnectionOf(h, OrgA)).Status.Should().Be(ConnectionStatus.NeedsSetup);
    }

    [Fact]
    public async Task Reconnecting_a_company_whose_matching_was_confirmed_goes_straight_back_to_Live_and_resumes()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var c = await h.SeedConnectionAsync(OrgA, ConnectionStatus.Revoked, withTokens: false);
        await h.SeedSettingsAsync(c, s => { s.MatchingConfirmedAt = DateTime.UtcNow.AddDays(-5); s.Mode = SyncMode.Live; });
        await using (var db = h.OpenAs(OrgA))
        {
            db.EntityMaps.Add(new EntityMap { OrganizationId = OrgA, ConnectionId = c.Id, Kind = SyncKind.Item, ExternalId = "v-1", RemoteId = "9" });
            await db.SaveChangesAsync();
        }
        var state = await StartConnectAsync(h, OrgA);

        var redirect = await CallbackAsync(h, state, realmId: "9130000000000001");

        redirect.Should().EndWith("result=connected");
        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(ConnectionStatus.Live);
        h.Outbox.Resumed.Should().Equal(c.Id);
    }

    [Fact]
    public async Task A_different_company_is_refused_once_records_are_mapped_and_nothing_changes()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var c = await h.SeedConnectionAsync(OrgA, ConnectionStatus.NotConnected, realmId: "OLD-REALM", withTokens: false);
        await using (var db = h.OpenAs(OrgA))
        {
            db.EntityMaps.Add(new EntityMap { OrganizationId = OrgA, ConnectionId = c.Id, Kind = SyncKind.Customer, ExternalId = "c-1", RemoteId = "1" });
            await db.SaveChangesAsync();
        }
        var state = await StartConnectAsync(h, OrgA);

        var redirect = await CallbackAsync(h, state, realmId: "NEW-REALM");

        redirect.Should().Be(Return + "?result=error&reason=realm_mismatch");
        h.Auth.ExchangedCodes.Should().BeEmpty("a refused company never even yields tokens");
        var stored = await h.ReloadConnectionAsync(c.Id);
        stored.RealmId.Should().Be("OLD-REALM");
        stored.EncryptedAccessToken.Should().BeNull();
        stored.Status.Should().Be(ConnectionStatus.NotConnected);
        stored.LastError.Should().Contain("NEW-REALM").And.Contain("OLD-REALM");
    }

    [Fact]
    public async Task A_different_company_with_nothing_mapped_is_accepted_and_starts_setup_from_scratch()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var c = await h.SeedConnectionAsync(OrgA, ConnectionStatus.NotConnected, realmId: "OLD-REALM", withTokens: false);
        await h.SeedSettingsAsync(c, s =>
        {
            s.MatchingConfirmedAt    = DateTime.UtcNow.AddDays(-1);
            s.Mode                   = SyncMode.Live;
            s.DefaultIncomeAccountId = "1";
        });
        await using (var db = h.OpenAs(OrgA))
        {
            db.TaxCodeMappings.Add(new TaxCodeMapping { OrganizationId = OrgA, ConnectionId = c.Id, TaxPercent = 17, QboTaxCodeId = "10" });
            db.PaymentTermMappings.Add(new PaymentTermMapping { OrganizationId = OrgA, ConnectionId = c.Id, PaymentTermExternalId = "t", QboTermId = "20" });
            await db.SaveChangesAsync();
        }
        var state = await StartConnectAsync(h, OrgA);

        var redirect = await CallbackAsync(h, state, realmId: "NEW-REALM");

        redirect.Should().EndWith("result=connected");
        var stored = await h.ReloadConnectionAsync(c.Id);
        stored.RealmId.Should().Be("NEW-REALM");
        stored.Status.Should().Be(ConnectionStatus.NeedsSetup, "the old company's matching sign-off does not carry over");

        await using var check = h.OpenAs(OrgA);
        var settings = await check.Settings.SingleAsync();
        settings.MatchingConfirmedAt.Should().BeNull();
        settings.Mode.Should().Be(SyncMode.DryRun);
        settings.DefaultIncomeAccountId.Should().BeNull();
        (await check.TaxCodeMappings.CountAsync()).Should().Be(0);
        (await check.PaymentTermMappings.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(null,       "state_invalid")]
    [InlineData("",         "state_invalid")]
    [InlineData("forged",   "state_invalid")]
    public async Task A_bad_state_is_refused(string? state, string reason)
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        await StartConnectAsync(h, OrgA);

        (await CallbackAsync(h, state)).Should().Be($"{Return}?result=error&reason={reason}");
        h.Auth.ExchangedCodes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_replayed_state_is_refused_and_the_first_connection_stands()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var state = await StartConnectAsync(h, OrgA);
        (await CallbackAsync(h, state, code: "first")).Should().EndWith("result=connected");

        var replay = await CallbackAsync(h, state, code: "second", realmId: "ATTACKER-REALM");

        replay.Should().EndWith("reason=state_used");
        h.Auth.ExchangedCodes.Should().Equal("first");
        (await ConnectionOf(h, OrgA)).RealmId.Should().Be(Realm);
    }

    [Fact]
    public async Task An_expired_state_is_refused()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true, qbo: o => o.StateTokenMinutes = 10);
        var state = await StartConnectAsync(h, OrgA);
        await using (var db = h.OpenAs(OrgA))
        {
            (await db.OAuthStateTokens.SingleAsync()).ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        (await CallbackAsync(h, state)).Should().EndWith("reason=state_expired");
        h.Auth.ExchangedCodes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_declined_consent_is_access_denied_burns_the_state_and_undoes_Connecting()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var state = await StartConnectAsync(h, OrgA);

        var redirect = await CallbackAsync(h, state, code: null, realmId: null, error: "access_denied");

        redirect.Should().EndWith("reason=access_denied");
        (await ConnectionOf(h, OrgA)).Status.Should().Be(ConnectionStatus.NotConnected);
        (await CallbackAsync(h, state)).Should().EndWith("reason=state_used", "a declined consent's state cannot be reused");
    }

    [Fact]
    public async Task Other_Intuit_errors_are_exchange_failed()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var state = await StartConnectAsync(h, OrgA);

        (await CallbackAsync(h, state, error: "invalid_scope")).Should().EndWith("reason=exchange_failed");
    }

    [Fact]
    public async Task A_declined_consent_with_a_stale_state_still_says_access_denied()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);

        (await CallbackAsync(h, "stale", code: null, error: "access_denied")).Should().EndWith("reason=access_denied");
    }

    [Fact]
    public async Task A_failed_code_exchange_is_reported_and_leaves_no_tokens()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        h.Auth.Exchange = (_, _) => throw new AccountingAuthException("Intuit did not exchange the authorization code: invalid_grant.");
        var state = await StartConnectAsync(h, OrgA);

        var redirect = await CallbackAsync(h, state);

        redirect.Should().EndWith("reason=exchange_failed");
        var c = await ConnectionOf(h, OrgA);
        c.Status.Should().Be(ConnectionStatus.NotConnected);
        c.EncryptedAccessToken.Should().BeNull();
        c.LastError.Should().Contain("invalid_grant");
    }

    [Theory]
    [InlineData(null, Realm)]
    [InlineData("code", null)]
    [InlineData("code", " ")]
    public async Task A_redirect_without_code_or_company_is_exchange_failed(string? code, string? realmId)
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var state = await StartConnectAsync(h, OrgA);

        (await CallbackAsync(h, state, code, realmId)).Should().EndWith("reason=exchange_failed");
        h.Auth.ExchangedCodes.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_app_keys_the_callback_is_not_configured()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var state = await StartConnectAsync(h, OrgA);

        // Keys removed between Connect and the callback.
        await using var scope = h.Root.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Configuration.QuickBooksOptions>>().Value;
        options.ClientSecret = "";

        (await CallbackAsync(h, state)).Should().EndWith("reason=not_configured");
        h.Auth.ExchangedCodes.Should().BeEmpty();
    }

    [Fact]
    public async Task An_organization_switched_off_meanwhile_cannot_complete()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var state = await StartConnectAsync(h, OrgA);
        h.Snapshots.Set(OrgA, new TenantSnapshot(true, new HashSet<string>()));

        (await CallbackAsync(h, state)).Should().EndWith("reason=state_invalid");
        h.Auth.ExchangedCodes.Should().BeEmpty();
        (await ConnectionOf(h, OrgA)).Status.Should().Be(ConnectionStatus.NotConnected);
    }

    [Fact]
    public async Task A_return_url_that_already_has_a_query_gets_an_ampersand()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true, qbo: o => o.FrontendReturnUrl = "http://x/qb?tab=connection");

        (await CallbackAsync(h, "bad")).Should().Be("http://x/qb?tab=connection&result=error&reason=state_invalid");
    }

    [Fact]
    public async Task An_unexpected_failure_still_redirects()
    {
        await using var h = ConnectionsHarness.Create(realTenant: true);
        var state = await StartConnectAsync(h, OrgA);
        h.Snapshots.ThrowOnGet = new InvalidOperationException("Tenancy is down");

        var redirect = await CallbackAsync(h, state);

        redirect.Should().StartWith(Return).And.Contain("result=error");
        HangfireTenantScope.OrganizationId.Should().BeNull();
    }
}

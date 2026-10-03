using FluentAssertions;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Logging;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Tests;

public class HarnessTests
{
    [Fact]
    public async Task Connections_are_isolated_by_tenant()
    {
        var (db, tenant, dbName) = IntegrationTestDb.New();
        await IntegrationTestDb.SeedConnectionAsync(db);

        using var other = IntegrationTestDb.OpenAs(dbName, Guid.NewGuid());

        (await new ConnectionAccessor(db).GetCurrentAsync()).Should().NotBeNull();
        (await new ConnectionAccessor(other).GetCurrentAsync()).Should().BeNull();
        db.Connections.Single().OrganizationId.Should().Be(tenant.OrganizationId);
    }

    [Fact]
    public async Task A_super_admin_gets_the_connection_of_the_organization_they_act_in_not_any()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var (dbA, _, dbName) = IntegrationTestDb.New(orgA);
        await IntegrationTestDb.SeedConnectionAsync(dbA, realmId: "A-REALM");
        using (var dbB = IntegrationTestDb.OpenAs(dbName, orgB))
            await IntegrationTestDb.SeedConnectionAsync(dbB, realmId: "B-REALM");

        // Super admin acting in B: the tenant filter is off, so only the explicit org check picks B.
        using var superInB = IntegrationTestDb.OpenAs(dbName, orgB, isSuperAdmin: true);
        (await new ConnectionAccessor(superInB).GetCurrentAsync())!.RealmId.Should().Be("B-REALM");

        // And an organization with no connection gets none, super admin or not.
        using var superInC = IntegrationTestDb.OpenAs(dbName, Guid.NewGuid(), isSuperAdmin: true);
        (await new ConnectionAccessor(superInC).GetCurrentAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Settings_are_created_with_safe_defaults()
    {
        var (db, _, _) = IntegrationTestDb.New();
        var connection = await IntegrationTestDb.SeedConnectionAsync(db);

        var settings = await new ConnectionAccessor(db).GetOrCreateSettingsAsync(connection);

        settings.Mode.Should().Be(SyncMode.DryRun, "nothing may reach a real company before someone switches it on");
        settings.PartnerScope.Should().Be(PartnerScope.OnlyWhenReferenced);
        settings.ItemTypeDefault.Should().Be(ItemTypeDefault.NonInventory);
    }

    [Theory]
    [InlineData("{\"access_token\":\"abc.def\",\"x\":1}", "abc.def")]
    [InlineData("{\"refresh_token\": \"RT-123\"}", "RT-123")]
    [InlineData("Authorization: Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.sig", "eyJhbGciOiJSUzI1NiJ9")]
    [InlineData("Authorization: Basic QUJDMTIzOnNlY3JldHZhbHVl", "QUJDMTIzOnNlY3JldHZhbHVl")]
    [InlineData("grant_type=refresh_token&refresh_token=RT-9&client_secret=S3", "RT-9")]
    [InlineData("grant_type=authorization_code&code=AB11-oauth-code&redirect_uri=x", "AB11-oauth-code")]
    [InlineData("key sqb_ABCDEFGH12345678 used", "sqb_ABCDEFGH12345678")]
    [InlineData("{\"body\":\"{\\\"access_token\\\":\\\"nested-secret-1\\\"}\"}", "nested-secret-1")]
    public void Redactor_removes_secrets(string input, string secret)
    {
        Redactor.Redact(input).Should().NotContain(secret);
    }

    [Theory]
    [InlineData("{\"Fault\":{\"Error\":[{\"code\":\"6240\",\"Message\":\"Duplicate Name Exists Error\"}]}}", "6240")]
    [InlineData("{\"Code\":\"USD\",\"Name\":\"US Dollar\"}", "USD")]
    [InlineData("{\"DisplayName\":\"Basic Supplies Ltd\"}", "Basic Supplies Ltd")]
    [InlineData("{\"DisplayName\":\"Bearer Logistics\"}", "Bearer Logistics")]
    public void Redactor_keeps_business_data(string input, string kept)
    {
        Redactor.Redact(input).Should().Contain(kept);
    }
}

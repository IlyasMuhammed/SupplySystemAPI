using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Tests.Settings;

/// <summary>
/// What the Integration module tells Finance before a tax code is renamed: whether the organization has a
/// QuickBooks mapping for the code's text (S-11 matches a line's code by text, so a rename would leave the
/// lines unmapped). Only the asking organization's mappings count, whoever asks — a super admin bypasses
/// the tenant filter — and percent-only mappings are not code mappings.
/// </summary>
public class IntegrationTaxCodeReferenceCheckerTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private readonly string _dbName = Guid.NewGuid().ToString();

    private IntegrationTaxCodeReferenceChecker Checker(Guid tenant, bool superAdmin = false) =>
        new(IntegrationTestDb.OpenAs(_dbName, tenant, superAdmin));

    private async Task SeedAsync(Guid org, string? sourceTaxCode, decimal percent = 17m)
    {
        await using var db = IntegrationTestDb.OpenAs(_dbName, org);
        db.TaxCodeMappings.Add(new TaxCodeMapping
        {
            OrganizationId = org, ConnectionId = 1, SourceTaxCode = sourceTaxCode, TaxPercent = percent, QboTaxCodeId = "5"
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("GST17")]
    [InlineData("gst17")]
    [InlineData(" GST17 ")]
    public async Task A_mapped_code_is_referenced_whatever_the_case_or_padding_asked(string asked)
    {
        await SeedAsync(OrgA, "GST17");

        (await Checker(OrgA).FindCodeReferenceAsync(OrgA, asked)).Should().Be("the QuickBooks tax mappings");
    }

    [Fact]
    public async Task An_unmapped_code_is_not_referenced()
    {
        await SeedAsync(OrgA, "GST17");

        (await Checker(OrgA).FindCodeReferenceAsync(OrgA, "GST16")).Should().BeNull();
    }

    [Fact]
    public async Task A_percent_only_mapping_is_not_a_code_mapping()
    {
        await SeedAsync(OrgA, null, 17m);

        (await Checker(OrgA).FindCodeReferenceAsync(OrgA, "GST17")).Should().BeNull();
        (await Checker(OrgA).FindCodeReferenceAsync(OrgA, "  ")).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_mapping_of_the_same_code_does_not_count_even_for_a_super_admin()
    {
        await SeedAsync(OrgB, "GST17");

        (await Checker(OrgA).FindCodeReferenceAsync(OrgA, "GST17")).Should().BeNull();
        (await Checker(OrgA, superAdmin: true).FindCodeReferenceAsync(OrgA, "GST17")).Should().BeNull();
    }

    [Fact]
    public async Task The_organization_asked_about_is_used_not_the_callers_tenant()
    {
        await SeedAsync(OrgA, "GST17");

        (await Checker(OrgB, superAdmin: true).FindCodeReferenceAsync(OrgA, "GST17")).Should().NotBeNull();
        (await Checker(OrgB).FindCodeReferenceAsync(OrgA, "GST17")).Should().NotBeNull(
            "the tenant filter is ignored; the organization passed in is the only limit");
    }

    [Fact]
    public void The_module_registers_the_checker_for_finance_and_the_container_can_build_it()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Data:mainOrg"] = @"Server=(localdb)\MSSQLLocalDB;Database=SMS_QBI_never_opened;Trusted_Connection=True"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new StaticTenantContext());

        services.AddIntegrationModule(configuration);

        services.Should().Contain(d => d.ServiceType == typeof(ITaxCodeReferenceChecker)
                                    && d.ImplementationType == typeof(IntegrationTaxCodeReferenceChecker)
                                    && d.Lifetime == ServiceLifetime.Scoped);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetServices<ITaxCodeReferenceChecker>()
            .Should().ContainSingle(c => c is IntegrationTaxCodeReferenceChecker);
    }
}

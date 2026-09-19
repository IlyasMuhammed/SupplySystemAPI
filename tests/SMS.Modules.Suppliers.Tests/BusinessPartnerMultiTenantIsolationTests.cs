using AutoMapper;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Repositories;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

// P1-07 (Addendum 29 §1.1/§1.7, TC-12). Genuinely new coverage — no test anywhere in this module
// exercised cross-organization isolation for the (old Supplier or new BusinessPartner) entity
// before this task, for any entity. Mirrors Logistics.Tests.HarnessTests' TC-01.4/TC-01.5 pattern
// exactly: two contexts over the SAME in-memory database with DIFFERENT tenant contexts is the
// only way to actually exercise the global query filter rather than assume it.
//
// TC-12's own scenario ("create a customer in Org A, log in as Org B, cannot access it, cannot
// reference it in an SO") is tested here up through "cannot access" — the SO half doesn't exist
// yet (Sale Orders are a later phase of this same addendum) and isn't fabricated.
file sealed class UnrestrictedAccess : IUserSupplierAccessService
{
    public Task<bool> IsRestrictedAsync() => Task.FromResult(false);
    public Task<IReadOnlySet<Guid>> GetAllowedSupplierIdsAsync() =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    public Task<bool> CanAccessSupplierAsync(Guid supplierUuid) => Task.FromResult(true);
}

file static class RepoBuild
{
    private static IMapper Mapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<BusinessPartnerMappingProfile>()).CreateMapper();

    internal static BusinessPartnerRepository For(SuppliersDbContext db) =>
        new(db, new UnrestrictedAccess(), Mapper());
}

public class BusinessPartner_Organization_Isolation_Tests
{
    [Fact]
    public async Task A_partner_created_in_one_org_is_invisible_to_another_org()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var (dbA, _, dbName) = SuppliersTestDb.New(orgA);
        var repoA = RepoBuild.For(dbA);
        var uuid = await repoA.CreateAsync(
            new BusinessPartnerModel { CompanyName = "Org A Customer", PartnerCode = "OAC001", IsCustomer = true },
            createdBy: 1);

        await using var dbB = SuppliersTestDb.OpenAs(dbName, orgB);
        var repoB = RepoBuild.For(dbB);

        (await repoB.GetByIdAsync(uuid)).Should().BeNull("Org B must not be able to read Org A's partner by id");
        (await repoB.GetCustomersAsync()).Should().BeEmpty("Org B's customer list must not include Org A's rows");
    }

    [Fact]
    public async Task Each_org_only_sees_its_own_partners_in_the_list_filters()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var (dbA, _, dbName) = SuppliersTestDb.New(orgA);

        await RepoBuild.For(dbA).CreateAsync(
            new BusinessPartnerModel { CompanyName = "A Vendor", PartnerCode = "AV001", IsVendor = true }, 1);

        await using var dbB = SuppliersTestDb.OpenAs(dbName, orgB);
        await RepoBuild.For(dbB).CreateAsync(
            new BusinessPartnerModel { CompanyName = "B Vendor", PartnerCode = "BV001", IsVendor = true }, 1);

        (await RepoBuild.For(dbA).GetVendorsAsync()).Should().ContainSingle().Which.CompanyName.Should().Be("A Vendor");
        (await RepoBuild.For(dbB).GetVendorsAsync()).Should().ContainSingle().Which.CompanyName.Should().Be("B Vendor");
    }

    [Fact]
    public async Task Super_admin_sees_across_organizations()
    {
        // TC-01.5's own reasoning, applied here: deliberate (anonymous paths and login rely on
        // it), so pinned by a test rather than left as an undocumented surprise for this entity.
        var orgA = Guid.NewGuid();
        var (dbA, _, dbName) = SuppliersTestDb.New(orgA);
        await RepoBuild.For(dbA).CreateAsync(
            new BusinessPartnerModel { CompanyName = "A Vendor", PartnerCode = "AV002", IsVendor = true }, 1);

        await using var superAdminDb = SuppliersTestDb.OpenAs(dbName, Guid.NewGuid(), isSuperAdmin: true);

        (await RepoBuild.For(superAdminDb).GetVendorsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task An_unset_OrganizationId_is_stamped_with_the_ambient_tenant_on_save()
    {
        var (db, tenant, _) = SuppliersTestDb.New();

        var partner = new BusinessPartner
        {
            UUID = Guid.NewGuid(), SupplierName = "Stamped Co", SupplierCode = "STP001",
            CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        partner.OrganizationId.Should().Be(Guid.Empty, "nothing set it explicitly");

        db.BusinessPartners.Add(partner);
        await db.SaveChangesAsync();

        partner.OrganizationId.Should().Be(tenant.OrganizationId);
    }
}

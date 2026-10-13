using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

/// <summary>A41 D-19 / D-20 — the CRM's accounts and contact persons over BusinessPartners / SupplierContacts.</summary>
public class CrmAccountGatewayTests
{
    private static (CrmAccountGateway Gateway, SuppliersDbContext Db, Guid Org) New()
    {
        var (db, tenant, _) = SuppliersTestDb.New();
        var customers = new CustomerService(db, new WalkInCustomerSeeder(db));
        return (new CrmAccountGateway(db, customers), db, tenant.OrganizationId);
    }

    private static BusinessPartner Partner(Guid org, string name, bool customer = true, bool vendor = false, string? phone = null, string? email = null,
        bool active = true, bool deleted = false, bool system = false) => new()
    {
        UUID = Guid.NewGuid(), OrganizationId = org, SupplierName = name, SupplierCode = name[..Math.Min(name.Length, 8)].ToUpperInvariant() + Guid.NewGuid().ToString()[..4],
        IsCustomer = customer, IsVendor = vendor, PartnerType = PartnerCode.Of(PartnerCode.FromFlags(vendor, customer, false, false)),
        Phone = phone, Email = email, IsActive = active, IsDelete = deleted, IsSystem = system, Status = "ACTIVE",
        CustomerType = system ? CustomerTypes.WalkIn : customer ? CustomerTypes.Company : null, CreatedDate = DateTime.UtcNow
    };

    [Fact]
    public async Task Matches_are_by_name_email_or_phone_tail_exact_name_first_and_never_another_org_or_the_walk_in()
    {
        var (g, db, org) = New();
        db.BusinessPartners.AddRange(
            Partner(org, "PakElectro Pvt Ltd", phone: "+92-42-35761234"),
            Partner(org, "PakElectro"),
            Partner(org, "Unrelated", email: "ahmed@pakelec.com"),
            Partner(org, "By Phone", phone: "0321 7654321"),
            Partner(org, "Gone PakElectro", deleted: true),
            Partner(org, "Walk-in PakElectro", system: true),
            Partner(Guid.NewGuid(), "PakElectro"));
        await db.SaveChangesAsync();

        var matches = await g.FindMatchesAsync(org, "pakelectro", "AHMED@pakelec.com", "+92-321-7654321", 10);
        matches.Select(m => m.Name).Should().Equal("PakElectro", "By Phone", "PakElectro Pvt Ltd", "Unrelated");
        (await g.FindMatchesAsync(org, null, null, null, 5)).Should().BeEmpty();
        (await g.SearchAsync(org, "elect", 10)).Select(a => a.Name).Should().BeEquivalentTo(["PakElectro Pvt Ltd", "PakElectro"]);
    }

    [Fact]
    public async Task A_new_customer_is_made_by_the_customer_master_with_its_rules_and_the_website_kept()
    {
        var (g, db, org) = New();
        var uuid = await g.CreateCustomerAsync(org, new CrmNewAccount("PakElectro", "COMPANY", "+92-300-1234567", "+92 321 765 4321 ext 22",
            "ahmed@pakelec.com", "https://pakelec.com", "123 Industrial Area", null, "Lahore", "Punjab", "54000", "Pakistan"), 7);

        var p = await db.BusinessPartners.SingleAsync(x => x.UUID == uuid);
        p.SupplierCode.Should().StartWith("C-");
        p.IsCustomer.Should().BeTrue();
        p.IsVendor.Should().BeFalse();
        p.CustomerType.Should().Be(CustomerTypes.Company);
        p.Phone.Should().Be("+92-300-1234567");
        p.Mobile.Should().Be("+923217654321ext22", "spaces are dropped to fit the 20-character column");
        p.Website.Should().Be("https://pakelec.com");
        p.City.Should().Be("Lahore");
        p.CreatedBy.Should().Be(7);

        var individual = await g.CreateCustomerAsync(org, new CrmNewAccount("Sarah Malik", "INDIVIDUAL", null, null, null, null, null, null, null, null, null, null), 7);
        (await db.BusinessPartners.SingleAsync(x => x.UUID == individual)).CustomerType.Should().Be(CustomerTypes.Individual);

        await g.Invoking(x => x.CreateCustomerAsync(Guid.NewGuid(), new CrmNewAccount("X", "COMPANY", null, null, null, null, null, null, null, null, null, null), 7))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_vendor_linked_by_a_lead_becomes_BOTH_and_an_inactive_one_is_refused()
    {
        var (g, db, org) = New();
        var vendor = Partner(org, "Supplier Co", customer: false, vendor: true);
        var inactive = Partner(org, "Closed Co", customer: false, vendor: true, active: false);
        db.BusinessPartners.AddRange(vendor, inactive);
        await db.SaveChangesAsync();

        await g.EnsureCustomerAsync(org, vendor.UUID, 3);
        var after = await db.BusinessPartners.AsNoTracking().SingleAsync(p => p.UUID == vendor.UUID);
        after.IsCustomer.Should().BeTrue();
        after.PartnerType.Should().Be("BOTH");
        after.CustomerType.Should().Be(CustomerTypes.Company);

        await g.Invoking(x => x.EnsureCustomerAsync(org, inactive.UUID, 3)).Should().ThrowAsync<BadRequestException>();
        await g.Invoking(x => x.EnsureCustomerAsync(Guid.NewGuid(), vendor.UUID, 3)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Contacts_belong_to_their_account_the_first_is_primary_and_another_orgs_are_absent()
    {
        var (g, db, org) = New();
        var a = Partner(org, "Company A");
        var b = Partner(org, "Company B");
        db.BusinessPartners.AddRange(a, b);
        await db.SaveChangesAsync();

        var ahmedA = await g.CreateContactAsync(org, a.UUID, new CrmNewContact("Mr Ahmed Khan", "Procurement Manager", "+92-321-7654321", "ahmed@a.test"));
        var farhan = await g.CreateContactAsync(org, a.UUID, new CrmNewContact("Farhan Ali", "Director", null, null));
        var ahmedB = await g.CreateContactAsync(org, b.UUID, new CrmNewContact("Ahmed Khan", null, null, null));

        var atA = await g.ListContactsAsync(org, a.UUID);
        atA.Select(c => (c.Name, c.IsPrimary)).Should().Equal(("Mr Ahmed Khan", true), ("Farhan Ali", false));
        atA.Should().OnlyContain(c => c.AccountUuid == a.UUID);

        var byId = await g.GetContactsAsync(org, [ahmedA, ahmedB, farhan]);
        byId[ahmedB].AccountUuid.Should().Be(b.UUID, "the same person at another company is a separate contact (BR-23)");
        byId[ahmedA].Title.Should().Be("Procurement Manager");
        (await g.GetContactsAsync(Guid.NewGuid(), [ahmedA])).Should().BeEmpty();
        (await g.GetAccountsAsync(Guid.NewGuid(), [a.UUID])).Should().BeEmpty();
        await g.Invoking(x => x.CreateContactAsync(org, Guid.NewGuid(), new CrmNewContact("X", null, null, null))).Should().ThrowAsync<NotFoundException>();
    }
}

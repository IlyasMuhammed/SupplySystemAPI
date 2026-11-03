using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

/// <summary>
/// A48 CORE — tasks 031–042 / D-3: the partner lookup Finance's sub-ledgers display with. Explicit organization (super admin
/// and jobs included), batch with a per-request cache, missing → null, invalid role → ArgumentException, bank details decrypted.
/// </summary>
public class PartnerLookupServiceTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static SuppliersDbContext Db(string name, Guid? tenantOrg = null, bool superAdmin = false) => new(
        new DbContextOptionsBuilder<SuppliersDbContext>().UseInMemoryDatabase(name).Options,
        new StaticTenantContext { OrganizationId = tenantOrg ?? Org, IsSuperAdmin = superAdmin });

    private static BusinessPartner Partner(Guid org, string name, bool customer, bool vendor, bool deleted = false) => new()
    {
        UUID = Guid.NewGuid(), OrganizationId = org, SupplierName = name, SupplierCode = name[..3].ToUpperInvariant() + "-1",
        PartnerType = customer ? "CUSTOMER" : "VENDOR", IsCustomer = customer, IsVendor = vendor, IsActive = true, IsDelete = deleted,
        Status = "ACTIVE", CreatedBy = 1, CreatedDate = DateTime.UtcNow,
    };

    [Fact]
    public async Task T038_T039_resolves_a_customer_and_a_vendor_with_contact_address_limit_and_terms()
    {
        var name = Guid.NewGuid().ToString();
        var customer = Partner(Org, "Acme Retail", customer: true, vendor: false);
        customer.Email = null; customer.PrimaryContactName = "Sara"; customer.PrimaryContactEmail = " sara@acme.test ";
        customer.AddressLine1 = "1 Mall Road"; customer.City = "Lahore"; customer.Country = "Pakistan"; customer.Mobile = "0300";
        customer.CreditLimit = 50_000m; customer.PaymentTermsDays = 30; customer.CustomerType = "COMPANY";
        var vendor = Partner(Org, "Steel Co", customer: false, vendor: true);
        vendor.Email = "ap@steel.test"; vendor.Phone = "042-1";
        await using (var seed = Db(name)) { seed.BusinessPartners.AddRange(customer, vendor); await seed.SaveChangesAsync(); }

        await using var db = Db(name);
        var lookup = new PartnerLookupService(db);

        var c = await lookup.GetAsync(Org, customer.UUID, PartnerRoles.Customer);
        c.Should().Be(new PartnerInfo(customer.UUID, "ACM-1", "Acme Retail", true, false, true, "sara@acme.test", "0300",
            "1 Mall Road, Lahore, Pakistan", "Sara", "sara@acme.test", 50_000m, 30, "COMPANY"));
        var v = await lookup.GetAsync(Org, vendor.UUID, PartnerRoles.Vendor);
        (v!.Name, v.Email, v.Phone, v.Address).Should().Be(("Steel Co", "ap@steel.test", "042-1", (string?)null));

        (await lookup.GetAsync(Org, vendor.UUID, PartnerRoles.Customer)).Should().BeNull("a vendor is not a customer");
        (await lookup.GetAsync(Org, vendor.UUID)).Should().NotBeNull("no role = any partner");
    }

    [Fact]
    public async Task T040_batch_of_fifty_is_one_query_and_the_cache_answers_repeats()
    {
        var name = Guid.NewGuid().ToString();
        var partners = Enumerable.Range(1, 50).Select(i => Partner(Org, $"Customer {i:00}", true, false)).ToList();
        await using (var seed = Db(name)) { seed.BusinessPartners.AddRange(partners); await seed.SaveChangesAsync(); }

        await using var db = Db(name);
        var lookup = new PartnerLookupService(db);
        var uuids = partners.Select(p => p.UUID).Append(Guid.NewGuid()).Append(Guid.Empty).ToList();

        var found = await lookup.GetManyAsync(Org, uuids, PartnerRoles.Customer);
        found.Should().HaveCount(50);
        lookup.QueryCount.Should().Be(1);

        await lookup.GetManyAsync(Org, uuids);
        (await lookup.GetAsync(Org, partners[7].UUID))!.Name.Should().Be("Customer 08");
        lookup.QueryCount.Should().Be(1, "everything asked again comes from the per-request cache, misses included");
    }

    [Fact]
    public async Task T041_missing_deleted_and_other_organizations_partners_are_absent_even_for_a_super_admin()
    {
        var name = Guid.NewGuid().ToString();
        var other = Guid.NewGuid();
        var deleted = Partner(Org, "Gone Ltd", true, false, deleted: true);
        var foreign = Partner(other, "Elsewhere", true, false);
        await using (var seed = Db(name, superAdmin: true)) { seed.BusinessPartners.AddRange(deleted, foreign); await seed.SaveChangesAsync(); }

        await using var db = Db(name, superAdmin: true);
        var lookup = new PartnerLookupService(db);
        (await lookup.GetAsync(Org, Guid.NewGuid())).Should().BeNull();
        (await lookup.GetAsync(Org, deleted.UUID)).Should().BeNull();
        (await lookup.GetAsync(Org, foreign.UUID)).Should().BeNull("another organization's partner is absent");
        (await lookup.GetAsync(other, foreign.UUID))!.Name.Should().Be("Elsewhere", "the organization is explicit, not the tenant's");
        (await lookup.GetManyAsync(Org, [deleted.UUID, foreign.UUID])).Should().BeEmpty();
    }

    [Fact]
    public async Task T042_an_invalid_role_throws_ArgumentException()
    {
        await using var db = Db(Guid.NewGuid().ToString());
        var lookup = new PartnerLookupService(db);
        await FluentActions.Awaiting(() => lookup.GetAsync(Org, Guid.NewGuid(), "Supplier")).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => lookup.GetManyAsync(Org, [Guid.NewGuid()], "customer")).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => lookup.SearchAsync(Org, "x", "Carrier")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Search_matches_name_or_code_within_the_role()
    {
        var name = Guid.NewGuid().ToString();
        await using (var seed = Db(name))
        {
            seed.BusinessPartners.AddRange(Partner(Org, "Zed Traders", true, false), Partner(Org, "Abc Stores", true, false),
                Partner(Org, "Zed Metals", false, true));
            await seed.SaveChangesAsync();
        }
        await using var db = Db(name);
        var lookup = new PartnerLookupService(db);

        (await lookup.SearchAsync(Org, "Zed", PartnerRoles.Customer)).Select(p => p.Name).Should().Equal("Zed Traders");
        (await lookup.SearchAsync(Org, "Zed")).Select(p => p.Name).Should().Equal("Zed Metals", "Zed Traders");
        (await lookup.SearchAsync(Org, "ABC-", PartnerRoles.Customer)).Select(p => p.Name).Should().Equal("Abc Stores");
    }

    [Fact]
    public async Task Bank_details_come_back_decrypted_and_an_undecryptable_value_is_left_out()
    {
        var name = Guid.NewGuid().ToString();
        var vendor = Partner(Org, "Steel Co", false, true);
        vendor.BankDetail = new SupplierBankDetail
        {
            OrganizationId = Org, BankName = "HBL", BankAccountNo = "enc:12345678", BankIban = "garbage", BankSwift = "enc:HABBPKKA",
            CreatedBy = 1, CreatedAt = DateTime.UtcNow,
        };
        var plain = Partner(Org, "No Bank", false, true);
        await using (var seed = Db(name)) { seed.BusinessPartners.AddRange(vendor, plain); await seed.SaveChangesAsync(); }

        await using var db = Db(name);
        var lookup = new PartnerLookupService(db, new PrefixEncryption());
        (await lookup.GetBankDetailsAsync(Org, vendor.UUID)).Should().Be(new PartnerBankInfo(vendor.UUID, "HBL", "12345678", null, "HABBPKKA"));
        (await lookup.GetBankDetailsAsync(Org, plain.UUID)).Should().BeNull();
        (await lookup.GetBankDetailsAsync(Guid.NewGuid(), vendor.UUID)).Should().BeNull();
    }

    private sealed class PrefixEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => "enc:" + plaintext;
        public string Decrypt(string ciphertext) =>
            ciphertext.StartsWith("enc:", StringComparison.Ordinal) ? ciphertext[4..] : throw new FormatException("not encrypted");
    }
}

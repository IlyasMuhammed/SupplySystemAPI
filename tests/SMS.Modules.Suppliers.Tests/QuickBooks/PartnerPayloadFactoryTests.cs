using FluentAssertions;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Integration;
using Xunit;

namespace SMS.Modules.Suppliers.Tests.QuickBooks;

/// <summary>QuickBooks plan §4 — BusinessPartner → CustomerPayload / VendorPayload, field by field.</summary>
public class PartnerPayloadFactoryTests
{
    private static readonly Guid Terms = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static BusinessPartner Full() => new()
    {
        UUID = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        SupplierName = "  Karachi Steel Works  ", SupplierCode = "KSW-001",
        TaxId = "NTN-1234567", RegistrationNo = "REG-9",
        AddressLine1 = "Plot 7, Sector 12", AddressLine2 = "Korangi Industrial Area",
        City = "Karachi", ProvinceState = "Sindh", PostalCode = "74900", Country = "Pakistan",
        Phone = "+92 21 111 222 333", Fax = "+92 21 111 222 334",
        Email = "accounts@ksw.example", Website = "https://ksw.example",
        PrimaryContactEmail = "ali@ksw.example", PrimaryContactPhone = "+92 300 1234567",
        PreferredPaymentTerms = Terms, PreferredCurrency = Guid.NewGuid(),
        Notes = "Pays by cheque.", IsVendor = true, IsCustomer = true,
        Status = "ACTIVE", IsActive = true
    };

    [Fact]
    public void A_customer_carries_every_mapped_field()
    {
        var p = Full();

        var c = PartnerPayloadFactory.BuildCustomer(p, " pkr ");

        c.ExternalId.Should().Be("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        c.DisplayName.Should().Be("Karachi Steel Works", "trimmed, otherwise untouched — QuickBooks' rules are the gateway's");
        c.CompanyName.Should().Be("Karachi Steel Works");
        c.Code.Should().Be("KSW-001");
        c.Email.Should().Be("accounts@ksw.example", "the partner's own email wins over the primary contact's");
        c.Phone.Should().Be("+92 21 111 222 333");
        c.Fax.Should().Be("+92 21 111 222 334");
        c.Website.Should().Be("https://ksw.example");
        c.TaxId.Should().Be("NTN-1234567");
        c.BillingAddress.Should().NotBeNull();
        c.BillingAddress!.Line1.Should().Be("Plot 7, Sector 12");
        c.BillingAddress.Line2.Should().Be("Korangi Industrial Area");
        c.BillingAddress.City.Should().Be("Karachi");
        c.BillingAddress.Region.Should().Be("Sindh", "ProvinceState is the region");
        c.BillingAddress.PostalCode.Should().Be("74900");
        c.BillingAddress.Country.Should().Be("Pakistan");
        c.CurrencyCode.Should().Be("PKR", "trimmed and upper-cased ISO code");
        c.PaymentTermExternalId.Should().Be(Terms.ToString());
        c.Notes.Should().Be("Pays by cheque.");
        c.IsActive.Should().BeTrue();
    }

    [Fact]
    public void A_vendor_carries_the_same_fields_plus_the_code_as_its_account_number()
    {
        var p = Full();

        var v = PartnerPayloadFactory.BuildVendor(p, "PKR");
        var c = PartnerPayloadFactory.BuildCustomer(p, "PKR");

        v.AccountNumber.Should().Be("KSW-001");
        v.Should().BeEquivalentTo(c, o => o.ExcludingMissingMembers(),
            "a partner that is both is the same party in both roles — one ExternalId, one set of details");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void The_primary_contact_fills_a_missing_email_and_phone(string? own)
    {
        var p = Full();
        p.Email = own;
        p.Phone = own;

        var c = PartnerPayloadFactory.BuildCustomer(p, null);

        c.Email.Should().Be("ali@ksw.example");
        c.Phone.Should().Be("+92 300 1234567");
    }

    [Fact]
    public void A_bare_partner_sends_nulls_not_empty_strings_and_no_address()
    {
        var p = new BusinessPartner
        {
            UUID = Guid.NewGuid(), SupplierName = "Bare", SupplierCode = "B1",
            Status = "PENDING", IsActive = true, IsCustomer = true
        };

        var c = PartnerPayloadFactory.BuildCustomer(p, null);

        c.DisplayName.Should().Be("Bare");
        c.Email.Should().BeNull();
        c.Phone.Should().BeNull();
        c.Fax.Should().BeNull();
        c.Website.Should().BeNull();
        c.TaxId.Should().BeNull();
        c.Notes.Should().BeNull();
        c.BillingAddress.Should().BeNull("an address of six nulls is no address");
        c.CurrencyCode.Should().BeNull("no preferred currency (or one with no code) is sent as none");
        c.PaymentTermExternalId.Should().BeNull();
        c.IsActive.Should().BeTrue("a pending partner is usable");
    }

    [Fact]
    public void A_partial_address_sends_only_what_is_known()
    {
        var p = new BusinessPartner { UUID = Guid.NewGuid(), SupplierName = "X", SupplierCode = "X", City = " Lahore ", Status = "ACTIVE", IsActive = true };

        var c = PartnerPayloadFactory.BuildCustomer(p, "   ");

        c.BillingAddress.Should().BeEquivalentTo(new { Line1 = (string?)null, Line2 = (string?)null, City = "Lahore", Region = (string?)null, PostalCode = (string?)null, Country = (string?)null });
        c.CurrencyCode.Should().BeNull("a blank code is no code");
    }

    [Theory]
    [InlineData(true,  false, "ACTIVE",      true)]
    [InlineData(true,  false, "PENDING",     true)]
    [InlineData(true,  false, "SUSPENDED",   true)]   // temporary — approved bills against it must still post
    [InlineData(true,  false, "BLACKLISTED", false)]
    [InlineData(true,  false, "blacklisted", false)]
    [InlineData(true,  false, "REJECTED",    false)]
    [InlineData(true,  false, "INACTIVE",    false)]
    [InlineData(false, false, "ACTIVE",      false)]
    [InlineData(true,  true,  "PENDING",     false)]
    public void Active_in_QuickBooks_means_switched_on_not_deleted_and_not_barred(bool isActive, bool isDelete, string status, bool expected)
    {
        var p = Full();
        p.IsActive = isActive;
        p.IsDelete = isDelete;
        p.Status = status;

        PartnerPayloadFactory.BuildCustomer(p, null).IsActive.Should().Be(expected);
        PartnerPayloadFactory.BuildVendor(p, null).IsActive.Should().Be(expected);
    }

    [Theory]
    [InlineData(true,  false, false, false, true,  false)]
    [InlineData(false, true,  false, false, false, true)]
    [InlineData(true,  true,  false, false, true,  true)]
    [InlineData(false, false, true,  false, true,  false)]   // a carrier is paid — a vendor in QuickBooks
    [InlineData(false, false, false, true,  true,  false)]   // so is a service provider
    public void Roles_follow_the_flags(bool vendor, bool customer, bool carrier, bool serviceProvider, bool expectVendor, bool expectCustomer)
    {
        var p = new BusinessPartner { IsVendor = vendor, IsCustomer = customer, IsCarrier = carrier, IsServiceProvider = serviceProvider };

        PartnerPayloadFactory.IsVendorRole(p).Should().Be(expectVendor);
        PartnerPayloadFactory.IsCustomerRole(p).Should().Be(expectCustomer);
    }
}

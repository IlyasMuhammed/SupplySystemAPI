using FluentAssertions;
using Intuit.Ipp.Data;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Providers.QuickBooks.Mapping;
using SMS.Modules.Integration.Tests.QuickBooks.Support;

namespace SMS.Modules.Integration.Tests.QuickBooks;

public class QboPartyMapperTests
{
    // ── Customer → SDK ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Customer_maps_every_field()
    {
        var c = QboCustomerMapper.ToSdk(QboTestKit.FullCustomer());

        c.DisplayName.Should().Be("King's Groceries");
        c.CompanyName.Should().Be("King's Groceries (Pvt) Ltd");
        c.PrimaryEmailAddr!.Address.Should().Be("accounts@kings.example");
        c.PrimaryPhone!.FreeFormNumber.Should().Be("+92 42 111 222 333");
        c.Fax!.FreeFormNumber.Should().Be("+92 42 111 222 334");
        c.WebAddr!.URI.Should().Be("https://kings.example");
        c.PrimaryTaxIdentifier.Should().Be("3520212345671");
        c.CurrencyRef!.Value.Should().Be("PKR");
        c.SalesTermRef!.Value.Should().Be("3");
        c.Notes.Should().Be("Deliver before noon");
        c.Active.Should().BeTrue();
        c.ActiveSpecified.Should().BeTrue();
        AssertFullAddress(c.BillAddr);
    }

    [Fact]
    public void Customer_inactive_is_sent_as_Active_false_with_the_flag_set()
    {
        var source = QboTestKit.FullCustomer();
        source.Active = false;

        var c = QboCustomerMapper.ToSdk(source);

        c.Active.Should().BeFalse();
        c.ActiveSpecified.Should().BeTrue("otherwise the SDK would drop Active=false and the customer would stay active");
    }

    [Fact]
    public void Customer_with_only_a_name_leaves_every_optional_field_unset()
    {
        var c = QboCustomerMapper.ToSdk(new RemoteCustomer { DisplayName = "Bare" });

        c.DisplayName.Should().Be("Bare");
        c.CompanyName.Should().BeNull();
        c.PrimaryEmailAddr.Should().BeNull();
        c.PrimaryPhone.Should().BeNull();
        c.Fax.Should().BeNull();
        c.WebAddr.Should().BeNull();
        c.BillAddr.Should().BeNull();
        c.CurrencyRef.Should().BeNull();
        c.SalesTermRef.Should().BeNull();
        c.Notes.Should().BeNull();
        c.PrimaryTaxIdentifier.Should().BeNull();
        c.Id.Should().BeNull();
        c.SyncToken.Should().BeNull();
        c.sparseSpecified.Should().BeFalse();
        c.TaxableSpecified.Should().BeFalse("tax fields are deliberately not sent in Phase 1");
        c.BalanceSpecified.Should().BeFalse();
    }

    [Fact]
    public void Customer_blank_strings_count_as_no_value()
    {
        var c = QboCustomerMapper.ToSdk(new RemoteCustomer
        {
            DisplayName = "Blank", CompanyName = " ", Email = "", Phone = "  ", Fax = "\t", Website = "",
            TaxId = " ", CurrencyCode = "", TermId = " ", Notes = "",
            BillAddress = new RemoteAddress { Line1 = " ", City = "" }
        });

        c.CompanyName.Should().BeNull();
        c.PrimaryEmailAddr.Should().BeNull();
        c.PrimaryPhone.Should().BeNull();
        c.Fax.Should().BeNull();
        c.WebAddr.Should().BeNull();
        c.PrimaryTaxIdentifier.Should().BeNull();
        c.CurrencyRef.Should().BeNull();
        c.SalesTermRef.Should().BeNull();
        c.Notes.Should().BeNull();
        c.BillAddr.Should().BeNull("an address with no non-blank line is not sent at all");
    }

    [Fact]
    public void Partial_address_sends_only_the_lines_that_have_values()
    {
        var c = QboCustomerMapper.ToSdk(new RemoteCustomer
        {
            DisplayName = "Partial",
            BillAddress = new RemoteAddress { City = "Karachi", Region = "SD" }
        });

        c.BillAddr!.City.Should().Be("Karachi");
        c.BillAddr.CountrySubDivisionCode.Should().Be("SD", "Region maps to CountrySubDivisionCode");
        c.BillAddr.Line1.Should().BeNull();
        c.BillAddr.Line2.Should().BeNull();
        c.BillAddr.PostalCode.Should().BeNull();
        c.BillAddr.Country.Should().BeNull();
    }

    [Fact]
    public void Customer_record_reads_id_token_name_and_active()
    {
        var record = QboCustomerMapper.ToRecord(QboTestKit.SavedCustomer("58", "4", "King's", active: false));

        record.Should().Be(new RemoteRecord("58", "4", "King's", null, null, null, false));
    }

    [Fact]
    public void Customer_record_is_active_when_QuickBooks_omits_the_flag()
    {
        QboCustomerMapper.ToRecord(QboTestKit.SavedCustomer(active: null)).Active.Should().BeTrue();
    }

    [Fact]
    public void Customer_record_without_an_id_is_an_error()
    {
        var act = () => QboCustomerMapper.ToRecord(new Customer { SyncToken = "0" });
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Customer_list_entry_reads_match_fields()
    {
        var customer = QboTestKit.SavedCustomer("9", "1", "Amy's Bird Sanctuary", active: false);
        customer.CompanyName = "Amy's Birds";
        customer.PrimaryEmailAddr = new EmailAddress { Address = "birds@example.com" };
        customer.PrimaryTaxIdentifier = "XXXXXX1234";
        customer.AcctNum = "should-not-be-read";
        customer.CurrencyRef = new ReferenceType { Value = "USD" };

        var entry = QboCustomerMapper.ToListEntry(customer);

        entry.Should().Be(new RemoteListEntry("9", "Amy's Bird Sanctuary", "Amy's Birds", "birds@example.com", "XXXXXX1234",
            null, null, "USD", false));
    }

    // ── Vendor → SDK ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Vendor_maps_every_field_with_vendor_specific_names()
    {
        var v = QboVendorMapper.ToSdk(QboTestKit.FullVendor());

        v.DisplayName.Should().Be("Acme Supplies");
        v.CompanyName.Should().Be("Acme Supplies Ltd");
        v.PrimaryEmailAddr!.Address.Should().Be("ap@acme.example");
        v.PrimaryPhone!.FreeFormNumber.Should().Be("+92 21 555 0000");
        v.Fax!.FreeFormNumber.Should().Be("+92 21 555 0001");
        v.WebAddr!.URI.Should().Be("https://acme.example");
        v.TaxIdentifier.Should().Be("NTN-7788", "vendors carry the tax id in TaxIdentifier");
        v.AcctNum.Should().Be("ACC-0042");
        v.CurrencyRef!.Value.Should().Be("PKR");
        v.TermRef!.Value.Should().Be("4", "vendors use TermRef, not SalesTermRef");
        v.Notes.Should().Be("Main supplier");
        v.Active.Should().BeTrue();
        v.ActiveSpecified.Should().BeTrue();
        AssertFullAddress(v.BillAddr);
    }

    [Fact]
    public void Vendor_with_only_a_name_leaves_every_optional_field_unset()
    {
        var v = QboVendorMapper.ToSdk(new RemoteVendor { DisplayName = "Bare" });

        v.CompanyName.Should().BeNull();
        v.PrimaryEmailAddr.Should().BeNull();
        v.PrimaryPhone.Should().BeNull();
        v.Fax.Should().BeNull();
        v.WebAddr.Should().BeNull();
        v.BillAddr.Should().BeNull();
        v.CurrencyRef.Should().BeNull();
        v.TermRef.Should().BeNull();
        v.Notes.Should().BeNull();
        v.TaxIdentifier.Should().BeNull();
        v.AcctNum.Should().BeNull();
        v.Vendor1099Specified.Should().BeFalse();
        v.ActiveSpecified.Should().BeTrue();
    }

    [Fact]
    public void Vendor_record_and_list_entry()
    {
        var vendor = new Vendor
        {
            Id = "31", SyncToken = "2", DisplayName = "Acme", CompanyName = "Acme Ltd",
            PrimaryEmailAddr = new EmailAddress { Address = "ap@acme.example" },
            TaxIdentifier = "XXXX7788", AcctNum = "ACC-0042", CurrencyRef = new ReferenceType { Value = "PKR" },
            Active = true, ActiveSpecified = true
        };

        QboVendorMapper.ToRecord(vendor).Should().Be(new RemoteRecord("31", "2", "Acme", null, null, null, true));
        QboVendorMapper.ToListEntry(vendor).Should().Be(
            new RemoteListEntry("31", "Acme", "Acme Ltd", "ap@acme.example", "XXXX7788", "ACC-0042", null, "PKR", true));
    }

    private static void AssertFullAddress(PhysicalAddress? a)
    {
        a.Should().NotBeNull();
        a!.Line1.Should().Be("12 Mall Road");
        a.Line2.Should().Be("Floor 3");
        a.City.Should().Be("Lahore");
        a.CountrySubDivisionCode.Should().Be("PB");
        a.PostalCode.Should().Be("54000");
        a.Country.Should().Be("Pakistan");
    }
}

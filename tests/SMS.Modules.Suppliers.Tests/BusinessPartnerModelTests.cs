using AutoMapper;
using FluentAssertions;
using FluentValidation.TestHelper;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Models;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

// P1-03 — PartnerCode (the enum <-> string <-> flags conversion), the AutoMapper profile, and the
// FluentValidation rule that partner_type must match the flag combination.

public class PartnerCode_Tests
{
    [Theory]
    [InlineData(true,  false, false, false, "VENDOR")]
    [InlineData(false, true,  false, false, "CUSTOMER")]
    [InlineData(false, false, true,  false, "CARRIER")]
    [InlineData(false, false, false, true,  "SERVICE_PROVIDER")]
    [InlineData(true,  true,  false, false, "BOTH")]
    [InlineData(true,  false, true,  false, "VENDOR_CARRIER")]
    [InlineData(true,  false, false, true,  "VENDOR_SERVICE")]
    [InlineData(true,  true,  true,  true,  "FULL_PARTNER")]
    public void FromFlags_matches_every_combination_the_FSD_table_defines(
        bool vendor, bool customer, bool carrier, bool serviceProvider, string code)
    {
        // PartnerType itself is internal, so it can't appear in a public xUnit theory's
        // signature — routed through the public string codes instead, the same way the DB and
        // every DTO see it.
        PartnerCode.TryParse(code, out var expected).Should().BeTrue();
        PartnerCode.FromFlags(vendor, customer, carrier, serviceProvider).Should().Be(expected);
        PartnerCode.Of(expected).Should().Be(code);
    }

    [Theory]
    [InlineData(false, false, false, false)] // nothing — not a partner at all
    [InlineData(false, true,  true,  false)] // customer+carrier, no vendor — not in the FSD's table
    [InlineData(false, false, true,  true)]  // carrier+service, no vendor — not in the FSD's table
    public void FromFlags_rejects_combinations_the_FSD_table_does_not_define(
        bool vendor, bool customer, bool carrier, bool serviceProvider)
    {
        var act = () => PartnerCode.FromFlags(vendor, customer, carrier, serviceProvider);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TryParse_never_guesses_on_unknown_or_blank_input()
    {
        PartnerCode.TryParse("NOT_A_CODE", out _).Should().BeFalse();
        PartnerCode.TryParse(null, out _).Should().BeFalse();
        PartnerCode.TryParse("", out _).Should().BeFalse();
        PartnerCode.TryParse("vendor", out _).Should().BeFalse("codes are case-sensitive, matching LogisticsCode");
    }
}

public class BusinessPartnerMappingProfile_Tests
{
    private static IMapper Mapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<BusinessPartnerMappingProfile>())
            .CreateMapper();

    [Fact]
    public void Maps_the_entity_to_the_model_including_the_renamed_fields()
    {
        var entity = new BusinessPartner
        {
            UUID = Guid.NewGuid(),
            SupplierCode = "SUP001",
            SupplierName = "Acme Corp",
            PartnerType = "VENDOR",
            IsVendor = true,
            VehicleTypes = "Truck,Van",
            IsActive = true
        };

        var model = Mapper().Map<BusinessPartnerModel>(entity);

        model.Uuid.Should().Be(entity.UUID);
        model.PartnerCode.Should().Be("SUP001");
        model.CompanyName.Should().Be("Acme Corp");
        model.PartnerType.Should().Be("VENDOR");
        model.IsVendor.Should().BeTrue();
        model.VehicleTypes.Should().Be("Truck,Van");
    }

    [Fact]
    public void Maps_the_model_back_to_the_entity_without_touching_audit_or_identity_fields()
    {
        var model = new BusinessPartnerModel
        {
            Uuid = Guid.NewGuid(),
            PartnerCode = "SUP002",
            CompanyName = "Beta Ltd",
            IsCarrier = true
        };

        var entity = Mapper().Map<BusinessPartner>(model);

        entity.UUID.Should().Be(model.Uuid);
        entity.SupplierCode.Should().Be("SUP002");
        entity.SupplierName.Should().Be("Beta Ltd");
        entity.IsCarrier.Should().BeTrue();
        entity.Id.Should().Be(0);
        entity.OrganizationId.Should().Be(Guid.Empty);
        entity.CreatedBy.Should().Be(0);
    }
}

public class BusinessPartnerModelValidator_Tests
{
    private static BusinessPartnerModel Valid() => new()
    {
        CompanyName = "Acme Corp",
        PartnerCode = "ACM001",
        IsVendor = true,
        PartnerType = "VENDOR"
    };

    [Fact]
    public void A_valid_vendor_only_partner_passes()
    {
        new BusinessPartnerModelValidator().TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void A_stale_PartnerType_from_a_prior_read_does_not_block_an_update_that_changes_only_flags()
    {
        // P1-07 — the real bug this test now pins: a caller doing a normal read-modify-write
        // (GET, flip a flag, PUT the whole object back) naturally carries the OLD computed
        // PartnerType alongside the NEW flags. That must not fail validation — the repository
        // (P1-04) recomputes PartnerType unconditionally regardless of what arrives, so rejecting
        // a "mismatch" here only broke requests that were always going to be handled correctly.
        var m = Valid();
        m.PartnerType = "VENDOR"; // stale — as if just read back before the flags below changed
        m.IsCustomer = true;      // vendor -> both; PartnerType was never updated to match

        new BusinessPartnerModelValidator().TestValidate(m).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void An_undefined_flag_combination_fails_even_with_no_PartnerType_supplied()
    {
        var m = Valid();
        m.PartnerType = "";
        m.IsVendor = false;
        m.IsCustomer = true;
        m.IsCarrier = true; // customer+carrier, no vendor — not one of the FSD's eight

        new BusinessPartnerModelValidator().TestValidate(m)
            .ShouldHaveAnyValidationError();
    }

    [Fact]
    public void PartnerType_left_blank_is_fine_as_long_as_the_flags_are_valid()
    {
        var m = Valid();
        m.PartnerType = "";

        new BusinessPartnerModelValidator().TestValidate(m).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void CompanyName_and_PartnerCode_are_required()
    {
        var m = Valid();
        m.CompanyName = "";
        m.PartnerCode = "";

        var result = new BusinessPartnerModelValidator().TestValidate(m);
        result.ShouldHaveValidationErrorFor(x => x.CompanyName);
        result.ShouldHaveValidationErrorFor(x => x.PartnerCode);
    }
}

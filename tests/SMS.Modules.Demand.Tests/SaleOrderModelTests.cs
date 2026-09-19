using AutoMapper;
using FluentAssertions;
using FluentValidation.TestHelper;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using Xunit;

namespace SMS.Modules.Demand.Tests;

// A29-P3-05 — the [Code]-attributed enums (EnumCode<T> conversion), the AutoMapper profile, and
// the FluentValidation rules for SaleOrder/SaleOrderLine.

public class EnumCode_Tests
{
    // Internal enums can't appear in a public [Theory]'s signature (xUnit theory methods must be
    // public), so every case is routed through the public string code instead — the same way the
    // DB and every DTO actually see these values, matching PartnerCode_Tests' approach.

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("CONFIRMED")]
    [InlineData("PARTIALLY_FULFILLED")]
    [InlineData("FULFILLED")]
    [InlineData("INVOICED")]
    [InlineData("CLOSED")]
    [InlineData("CANCELLED")]
    public void Every_sale_order_status_round_trips_through_its_code(string code)
    {
        EnumCode<SaleOrderStatus>.TryParse(code, out var status).Should().BeTrue();
        EnumCode<SaleOrderStatus>.Of(status).Should().Be(code);
    }

    [Theory]
    [InlineData("SHIP")]
    [InlineData("SELF_PICKUP")]
    public void Every_delivery_mode_round_trips_through_its_code(string code)
    {
        EnumCode<DeliveryMode>.TryParse(code, out var mode).Should().BeTrue();
        EnumCode<DeliveryMode>.Of(mode).Should().Be(code);
    }

    [Theory]
    [InlineData("IN_STOCK")]
    [InlineData("BACK_TO_BACK")]
    [InlineData("DROP_SHIP")]
    [InlineData("SPLIT")]
    public void Every_line_fulfillment_mode_round_trips_through_its_code(string code)
    {
        EnumCode<SaleOrderLineFulfillmentMode>.TryParse(code, out var mode).Should().BeTrue();
        EnumCode<SaleOrderLineFulfillmentMode>.Of(mode).Should().Be(code);
    }

    [Theory]
    [InlineData("OPEN")]
    [InlineData("RESERVED")]
    [InlineData("PARTIALLY_FULFILLED")]
    [InlineData("FULFILLED")]
    [InlineData("INVOICED")]
    [InlineData("CANCELLED")]
    public void Every_line_status_round_trips_through_its_code(string code)
    {
        EnumCode<SaleOrderLineStatus>.TryParse(code, out var status).Should().BeTrue();
        EnumCode<SaleOrderLineStatus>.Of(status).Should().Be(code);
    }

    [Fact]
    public void TryParse_never_guesses_on_unknown_null_or_blank_input()
    {
        EnumCode<SaleOrderStatus>.TryParse("NOT_A_CODE", out _).Should().BeFalse();
        EnumCode<SaleOrderStatus>.TryParse(null, out _).Should().BeFalse();
        EnumCode<SaleOrderStatus>.TryParse("", out _).Should().BeFalse();
        EnumCode<SaleOrderStatus>.TryParse("draft", out _).Should().BeFalse("codes are case-sensitive, matching PartnerCode/LogisticsCode");
    }
}

public class SaleOrderMappingProfile_Tests
{
    private static IMapper Mapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<SaleOrderMappingProfile>()).CreateMapper();

    [Fact]
    public void Maps_a_sale_order_and_its_lines_to_the_model()
    {
        var entity = new SaleOrder
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), SoNumber = "SO-20260919-0001",
            PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow, CurrencyId = Guid.NewGuid(),
            Subtotal = 100m, TaxAmount = 10m, DiscountAmount = 0m, GrandTotal = 110m,
            Status = "DRAFT", DeliveryMode = "SHIP", ShippingAddressId = Guid.NewGuid(),
            Lines =
            [
                new SaleOrderLine { UUID = Guid.NewGuid(), VariantUuid = Guid.NewGuid(), Quantity = 5, UnitPrice = 22m, LineTotal = 110m, Status = "OPEN" }
            ]
        };

        var model = Mapper().Map<SaleOrderModel>(entity);

        model.Uuid.Should().Be(entity.UUID);
        model.SoNumber.Should().Be("SO-20260919-0001");
        model.GrandTotal.Should().Be(110m);
        model.Lines.Should().ContainSingle();
        model.Lines[0].Uuid.Should().Be(entity.Lines.First().UUID);
        model.Lines[0].Quantity.Should().Be(5);
    }
}

public class SaleOrderValidator_Tests
{
    private static SaleOrderModel ValidOrder() => new()
    {
        SoNumber = "SO-20260919-0001", PartnerId = Guid.NewGuid(), CurrencyId = Guid.NewGuid(),
        OrderDate = DateTime.UtcNow, Status = "DRAFT", DeliveryMode = "SELF_PICKUP",
        Subtotal = 100m, TaxAmount = 10m, DiscountAmount = 0m, GrandTotal = 110m
    };

    [Fact]
    public void A_well_formed_order_passes()
    {
        new SaleOrderValidator().TestValidate(ValidOrder()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void An_unknown_status_code_fails()
    {
        var order = ValidOrder();
        order.Status = "BOGUS";
        new SaleOrderValidator().TestValidate(order).ShouldHaveValidationErrorFor(x => x.Status);
    }

    [Fact]
    public void An_unknown_delivery_mode_fails()
    {
        var order = ValidOrder();
        order.DeliveryMode = "BOGUS";
        new SaleOrderValidator().TestValidate(order).ShouldHaveValidationErrorFor(x => x.DeliveryMode);
    }

    [Fact]
    public void Ship_with_no_shipping_address_fails()
    {
        var order = ValidOrder();
        order.DeliveryMode = "SHIP";
        order.ShippingAddressId = null;
        new SaleOrderValidator().TestValidate(order).ShouldHaveValidationErrorFor(x => x.ShippingAddressId);
    }

    [Fact]
    public void Ship_with_a_shipping_address_passes()
    {
        var order = ValidOrder();
        order.DeliveryMode = "SHIP";
        order.ShippingAddressId = Guid.NewGuid();
        new SaleOrderValidator().TestValidate(order).ShouldNotHaveValidationErrorFor(x => x.ShippingAddressId);
    }

    [Fact]
    public void Self_pickup_with_no_shipping_address_passes()
    {
        var order = ValidOrder();
        order.DeliveryMode = "SELF_PICKUP";
        order.ShippingAddressId = null;
        new SaleOrderValidator().TestValidate(order).ShouldNotHaveValidationErrorFor(x => x.ShippingAddressId);
    }

    [Fact]
    public void A_grand_total_that_does_not_match_subtotal_plus_tax_minus_discount_fails()
    {
        var order = ValidOrder();
        order.GrandTotal = 999m;
        var result = new SaleOrderValidator().TestValidate(order);
        result.ShouldHaveAnyValidationError();
    }

    [Fact]
    public void An_empty_partner_id_fails()
    {
        var order = ValidOrder();
        order.PartnerId = Guid.Empty;
        new SaleOrderValidator().TestValidate(order).ShouldHaveValidationErrorFor(x => x.PartnerId);
    }
}

public class SaleOrderLineValidator_Tests
{
    private static SaleOrderLineModel ValidLine() => new()
    {
        VariantUuid = Guid.NewGuid(), Quantity = 10, UnitPrice = 11m,
        DiscountPercent = 0, TaxPercent = 0, LineTotal = 110m, Status = "OPEN"
    };

    [Fact]
    public void A_well_formed_line_passes()
    {
        new SaleOrderLineValidator().TestValidate(ValidLine()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Zero_quantity_fails()
    {
        var line = ValidLine();
        line.Quantity = 0;
        new SaleOrderLineValidator().TestValidate(line).ShouldHaveValidationErrorFor(x => x.Quantity);
    }

    [Fact]
    public void A_null_fulfillment_mode_is_fine_a_draft_line_has_not_been_confirmed_yet()
    {
        var line = ValidLine();
        line.FulfillmentMode = null;
        new SaleOrderLineValidator().TestValidate(line).ShouldNotHaveValidationErrorFor(x => x.FulfillmentMode);
    }

    [Fact]
    public void An_unknown_fulfillment_mode_fails()
    {
        var line = ValidLine();
        line.FulfillmentMode = "BOGUS";
        new SaleOrderLineValidator().TestValidate(line).ShouldHaveValidationErrorFor(x => x.FulfillmentMode);
    }

    [Theory]
    [InlineData("IN_STOCK")]
    [InlineData("BACK_TO_BACK")]
    [InlineData("DROP_SHIP")]
    [InlineData("SPLIT")]
    public void Every_real_fulfillment_mode_code_passes(string code)
    {
        var line = ValidLine();
        line.FulfillmentMode = code;
        new SaleOrderLineValidator().TestValidate(line).ShouldNotHaveValidationErrorFor(x => x.FulfillmentMode);
    }

    [Fact]
    public void A_line_total_that_does_not_match_the_formula_fails()
    {
        var line = ValidLine();
        line.LineTotal = 1m;
        new SaleOrderLineValidator().TestValidate(line).ShouldHaveAnyValidationError();
    }

    [Fact]
    public void Line_total_with_discount_and_tax_is_computed_correctly()
    {
        var line = ValidLine();
        line.Quantity = 10; line.UnitPrice = 100m; line.DiscountPercent = 10; line.TaxPercent = 5;
        // 10 * 100 * (1 - 0.10) * (1 + 0.05) = 945
        line.LineTotal = 945m;
        new SaleOrderLineValidator().TestValidate(line).ShouldNotHaveAnyValidationErrors();
    }
}

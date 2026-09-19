using FluentValidation;
using SMS.Modules.Demand.Domain;

namespace SMS.Modules.Demand.Models;

// A29-P3-05. Mirrors BusinessPartnerModelValidator's AbstractValidator + AddTransient<TValidator>
// pattern (Suppliers, P1-03) — the closed vocabularies (Status, DeliveryMode) are checked against
// the [Code]-attributed enums in SaleOrderEnums.cs rather than a hardcoded string list, so a
// future member added there is enforced here automatically.
internal sealed class SaleOrderValidator : AbstractValidator<SaleOrderModel>
{
    public SaleOrderValidator()
    {
        RuleFor(x => x.SoNumber).NotEmpty().MaximumLength(20);
        RuleFor(x => x.PartnerId).NotEmpty().WithMessage("A sale order must be for a partner.");
        RuleFor(x => x.CurrencyId).NotEmpty();
        RuleFor(x => x.OrderDate).NotEqual(default(DateTime));

        RuleFor(x => x.Status)
            .Must(s => EnumCode<SaleOrderStatus>.TryParse(s, out _))
            .WithMessage(x => $"'{x.Status}' is not a valid sale order status.");

        RuleFor(x => x.DeliveryMode)
            .Must(s => EnumCode<Domain.DeliveryMode>.TryParse(s, out _))
            .WithMessage(x => $"'{x.DeliveryMode}' is not a valid delivery mode.");

        // §4.1 — "NULL if self-pickup." A SHIP order with no shipping address has nowhere to go.
        RuleFor(x => x.ShippingAddressId)
            .NotNull()
            .When(x => EnumCode<Domain.DeliveryMode>.TryParse(x.DeliveryMode, out var mode) && mode == Domain.DeliveryMode.Ship)
            .WithMessage("A shipping address is required when the delivery mode is SHIP.");

        RuleFor(x => x.Subtotal).GreaterThanOrEqualTo(0);
        RuleFor(x => x.TaxAmount).GreaterThanOrEqualTo(0);
        RuleFor(x => x.DiscountAmount).GreaterThanOrEqualTo(0);

        // §4.1 — "grand_total (= subtotal + tax - discount)."
        RuleFor(x => x)
            .Must(x => Math.Abs(x.GrandTotal - (x.Subtotal + x.TaxAmount - x.DiscountAmount)) < 0.01m)
            .WithMessage("GrandTotal must equal Subtotal + TaxAmount - DiscountAmount.");

        RuleForEach(x => x.Lines).SetValidator(new SaleOrderLineValidator());
    }
}

internal sealed class SaleOrderLineValidator : AbstractValidator<SaleOrderLineModel>
{
    public SaleOrderLineValidator()
    {
        RuleFor(x => x.VariantUuid).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0);
        RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
        RuleFor(x => x.DiscountPercent).InclusiveBetween(0, 100);
        RuleFor(x => x.TaxPercent).GreaterThanOrEqualTo(0);

        RuleFor(x => x.Status)
            .Must(s => EnumCode<SaleOrderLineStatus>.TryParse(s, out _))
            .WithMessage(x => $"'{x.Status}' is not a valid sale order line status.");

        RuleFor(x => x.FulfillmentMode)
            .Must(s => EnumCode<SaleOrderLineFulfillmentMode>.TryParse(s, out _))
            .When(x => x.FulfillmentMode is not null)
            .WithMessage(x => $"'{x.FulfillmentMode}' is not a valid fulfillment mode.");

        // §4.2 — "line_total = qty * price * (1 - disc%) * (1 + tax%)."
        RuleFor(x => x)
            .Must(x => Math.Abs(x.LineTotal -
                x.Quantity * x.UnitPrice * (1 - x.DiscountPercent / 100m) * (1 + x.TaxPercent / 100m)) < 0.01m)
            .WithMessage("LineTotal must equal Quantity * UnitPrice * (1 - DiscountPercent%) * (1 + TaxPercent%).");
    }
}

using FluentValidation;
using SMS.Modules.Suppliers.Domain;

namespace SMS.Modules.Suppliers.Models;

// P1-03 (Addendum 29 §1.2/§1.3). Mirrors SMS.WorkflowEngine.Validation's AbstractValidator +
// AddTransient<TValidator> registration pattern — the only other place in the codebase using
// FluentValidation as a standalone validator class (there are none yet in Suppliers).
//
// "partner_type must match flag combination" — enforced as "the flags form one of the FSD's eight
// defined combinations" (PartnerCode.FromFlags throws for anything else). It does NOT also check a
// caller-supplied PartnerType string against the flags — P1-07's integration tests found that this
// broke the ordinary read-modify-write update flow: GET returns the entity with its OLD computed
// PartnerType, a caller changes only a flag and PUTs the object back, and the still-stale
// PartnerType field would then "disagree" with the new flags and fail validation, even though
// BusinessPartnerRepository.CreateAsync/UpdateAsync (P1-04) already discard whatever PartnerType
// arrives and recompute it unconditionally. Validating a field's *input* consistency when the
// field is never actually trusted as input is worse than not validating it — it only rejects
// requests that were always going to be handled correctly anyway.
internal sealed class BusinessPartnerModelValidator : AbstractValidator<BusinessPartnerModel>
{
    public BusinessPartnerModelValidator()
    {
        RuleFor(x => x.CompanyName)
            .NotEmpty().WithMessage("CompanyName is required.")
            .MaximumLength(200);

        RuleFor(x => x.PartnerCode)
            .NotEmpty().WithMessage("PartnerCode is required.")
            .MaximumLength(10);

        RuleFor(x => x)
            .Must(HaveAValidFlagCombination)
            .WithMessage(x => InvalidCombinationMessage(x));
    }

    private static bool HaveAValidFlagCombination(BusinessPartnerModel m) =>
        TryComputeType(m, out _);

    private static bool TryComputeType(BusinessPartnerModel m, out PartnerType type)
    {
        try
        {
            type = PartnerCode.FromFlags(m.IsVendor, m.IsCustomer, m.IsCarrier, m.IsServiceProvider);
            return true;
        }
        catch (InvalidOperationException)
        {
            type = default;
            return false;
        }
    }

    private static string InvalidCombinationMessage(BusinessPartnerModel m) =>
        $"is_vendor={m.IsVendor}, is_customer={m.IsCustomer}, is_carrier={m.IsCarrier}, " +
        $"is_service_provider={m.IsServiceProvider} is not one of the eight partner-type " +
        "combinations the FSD defines (§1.2).";
}

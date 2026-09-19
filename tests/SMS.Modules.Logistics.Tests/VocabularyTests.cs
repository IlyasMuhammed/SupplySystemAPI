using System.Reflection;
using FluentAssertions;
using SMS.Modules.Logistics.Constants;
using SMS.Modules.Logistics.Domain;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-04 — the module's closed vocabularies.
public class VocabularyTests
{
    // ── TC-04.1 — the double-deduction rule ───────────────────────────────────
    //
    // The single most consequential table in the module. MIV and SRO already deduct stock in
    // their own modules; a delivery created from either must not deduct it again. If this test
    // ever goes red, stock accuracy is at stake — do not "fix" it by changing the expectation.

    // Source types are named by their persisted code rather than the enum member: the enums are
    // internal to the module, and an xUnit theory method has to be public.
    [Theory]
    [InlineData("PO",       false)] // GRN posts on receipt
    [InlineData("SRO",      false)] // SroRepository posts RETURN_DISPATCH at dispatch
    [InlineData("MIV",      false)] // MIV posts on POSTED
    [InlineData("TRANSFER", true)]  // nothing else posts it
    [InlineData("MANUAL",   true)]  // nothing else posts it
    public void Source_type_declares_whether_it_posts_goods_issue(string sourceCode, bool expected) =>
        DeliverySourceTypeInfo
            .PostsGoodsIssue(LogisticsCode.Parse<DeliverySourceType>(sourceCode))
            .Should().Be(expected);

    [Fact]
    public void Every_source_type_declares_its_posting_rule()
    {
        // A source type with no entry would fall through to a default, and the safe-looking
        // default (false) silently loses stock movements while the unsafe one double-counts.
        // Neither is acceptable, so the lookup throws — this proves none is missing.
        foreach (var source in Enum.GetValues<DeliverySourceType>())
        {
            var act = () => DeliverySourceTypeInfo.For(source);
            act.Should().NotThrow($"{source} must declare its rules");
        }
    }

    // ── TC-04.4 — direction ───────────────────────────────────────────────────

    [Theory]
    [InlineData("PO",       "INBOUND")]
    [InlineData("SRO",      "OUTBOUND")]
    [InlineData("MIV",      "OUTBOUND")]
    [InlineData("TRANSFER", "TRANSFER")]
    public void Source_type_implies_a_direction(string sourceCode, string expectedDirection)
    {
        var source = LogisticsCode.Parse<DeliverySourceType>(sourceCode);

        DeliverySourceTypeInfo.TryGetDefaultDirection(source, out var direction).Should().BeTrue();
        LogisticsCode.Of(direction).Should().Be(expectedDirection);
    }

    [Fact]
    public void Manual_deliveries_have_no_implied_direction()
    {
        // An ad-hoc delivery can go either way, so the caller has to say. Inferring one here
        // would mean guessing, and a wrongly-inferred direction points the stock movement the
        // wrong way.
        DeliverySourceTypeInfo.TryGetDefaultDirection(DeliverySourceType.Manual, out _)
            .Should().BeFalse();
    }

    // ── TC-04.2 — the normalized milestone set ────────────────────────────────

    [Fact]
    public void Tracking_milestones_are_exactly_the_twelve_documented_codes()
    {
        // Every courier adapter maps its carrier's own codes into this set, and the tracking UI,
        // SLA calculation and reporting read only these. A thirteenth member would silently
        // leave every existing adapter unable to produce it.
        string[] expected =
        [
            "INFO_RECEIVED", "PICKED_UP", "IN_TRANSIT", "ARRIVED_AT_HUB", "DEPARTED_HUB",
            "CUSTOMS_HOLD", "OUT_FOR_DELIVERY", "DELIVERY_ATTEMPTED", "DELIVERED", "EXCEPTION",
            "RETURN_INITIATED", "RETURNED"
        ];

        LogisticsCode.Codes<TrackingMilestone>().Should().BeEquivalentTo(expected);
        Enum.GetValues<TrackingMilestone>().Should().HaveCount(12);
    }

    // ── TC-04.3 — codes round-trip, and the constants agree with the enums ────

    // Generic rather than a [Theory] over typeof(...): the enums are internal, so a public
    // theory method cannot take them, and going through reflection to work around that hid a
    // null MethodInfo behind a NullReferenceException instead of a useful failure.
    private static void AssertCodesRoundTrip<TEnum>() where TEnum : struct, Enum
    {
        var name = typeof(TEnum).Name;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in Enum.GetValues<TEnum>())
        {
            // Throws if the member has no [Code].
            var code = LogisticsCode.Of(member);

            code.Should().NotBeNullOrWhiteSpace($"{name}.{member} must declare a code");
            seen.Add(code).Should().BeTrue($"{name} declares '{code}' more than once");

            LogisticsCode.TryParse<TEnum>(code, out var parsed)
                .Should().BeTrue($"'{code}' must parse back to {name}.{member}");
            parsed.Should().Be(member);
        }
    }

    [Fact]
    public void Every_member_of_every_persisted_enum_has_a_unique_code_that_round_trips()
    {
        AssertCodesRoundTrip<DeliveryDirection>();
        AssertCodesRoundTrip<DeliverySourceType>();
        AssertCodesRoundTrip<DeliveryStatus>();
        AssertCodesRoundTrip<DeliveryPriority>();
        AssertCodesRoundTrip<PickListStatus>();
        AssertCodesRoundTrip<PickShortReason>();
        AssertCodesRoundTrip<ShipmentStatus>();
        AssertCodesRoundTrip<ShipmentMode>();
        AssertCodesRoundTrip<FreightTerms>();
        AssertCodesRoundTrip<PackageType>();
        AssertCodesRoundTrip<StopType>();
        AssertCodesRoundTrip<TrackingMilestone>();
        AssertCodesRoundTrip<DeliveryExceptionType>();
        AssertCodesRoundTrip<AddressValidationStatus>();
        AssertCodesRoundTrip<AddressType>();
        AssertCodesRoundTrip<CarrierIntegrationMode>();
        AssertCodesRoundTrip<CarrierCommandType>();
        AssertCodesRoundTrip<CarrierCommandStatus>();
        AssertCodesRoundTrip<ConsignmentLabelSource>();
        AssertCodesRoundTrip<TrackingEventSource>();
        AssertCodesRoundTrip<WebhookDeliveryStatus>();
        AssertCodesRoundTrip<ChargeableWeightBasis>();
        AssertCodesRoundTrip<RateBasis>();
        AssertCodesRoundTrip<RateSource>();
        AssertCodesRoundTrip<FreightAccrualStatus>();
        AssertCodesRoundTrip<CarrierInvoiceStatus>();
        AssertCodesRoundTrip<InvoiceLineMatchStatus>();
        AssertCodesRoundTrip<InvoiceLineMatchMethod>();
        AssertCodesRoundTrip<VarianceReason>();
        AssertCodesRoundTrip<CodStatus>();
        AssertCodesRoundTrip<DisputeOutcome>();
        AssertCodesRoundTrip<ExceptionStatus>();
        AssertCodesRoundTrip<ExceptionSeverity>();
        AssertCodesRoundTrip<ExceptionSource>();
        AssertCodesRoundTrip<ProofSource>();
        AssertCodesRoundTrip<ProofFileKind>();
    }

    [Fact]
    public void Every_persisted_enum_in_the_module_is_covered_by_the_round_trip_test()
    {
        // The list above is hand-maintained, and three enums added after it was written
        // (AddressType, DeliveryPriority, StopType) silently went unchecked until this test
        // existed. Now adding an enum without listing it fails here.
        var declared = typeof(LogisticsCode).Assembly
            .GetTypes()
            .Where(t => t.IsEnum && t.Namespace == typeof(DeliveryStatus).Namespace)
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        string[] covered =
        [
            nameof(DeliveryDirection), nameof(DeliverySourceType), nameof(DeliveryStatus),
            nameof(DeliveryPriority), nameof(PickListStatus), nameof(PickShortReason),
            nameof(ShipmentStatus), nameof(ShipmentMode),
            nameof(FreightTerms), nameof(PackageType), nameof(StopType),
            nameof(TrackingMilestone), nameof(DeliveryExceptionType),
            nameof(AddressValidationStatus), nameof(AddressType), nameof(CarrierIntegrationMode),
            nameof(CarrierCommandType), nameof(CarrierCommandStatus), nameof(ConsignmentLabelSource),
            nameof(TrackingEventSource), nameof(WebhookDeliveryStatus), nameof(ChargeableWeightBasis),
            nameof(RateBasis), nameof(RateSource), nameof(FreightAccrualStatus),
            nameof(CarrierInvoiceStatus), nameof(InvoiceLineMatchStatus), nameof(InvoiceLineMatchMethod),
            nameof(VarianceReason), nameof(CodStatus), nameof(DisputeOutcome),
            nameof(ExceptionStatus), nameof(ExceptionSeverity), nameof(ExceptionSource),
            nameof(ProofSource), nameof(ProofFileKind)
        ];

        declared.Should().BeEquivalentTo(covered,
            "every enum in the domain namespace must be listed in "
          + nameof(Every_member_of_every_persisted_enum_has_a_unique_code_that_round_trips));
    }

    // Each nested class in LogisticsStatuses mirrors one enum. The duplication is what makes
    // compile-time constants possible for EF LINQ; this is what keeps it honest.
    private static void AssertConstantsMatch<TEnum>(Type constantsClass) where TEnum : struct, Enum
    {
        var constants = constantsClass
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        var codes = Enum.GetValues<TEnum>().Select(LogisticsCode.Of).ToList();

        constants.Should().BeEquivalentTo(codes,
            $"{constantsClass.Name} and {typeof(TEnum).Name} must not drift apart");
    }

    [Fact]
    public void Constants_and_enum_codes_agree_exactly()
    {
        AssertConstantsMatch<DeliveryStatus>(typeof(LogisticsStatuses.Delivery));
        AssertConstantsMatch<ShipmentStatus>(typeof(LogisticsStatuses.Shipment));
        AssertConstantsMatch<DeliveryDirection>(typeof(LogisticsStatuses.Direction));
        AssertConstantsMatch<DeliverySourceType>(typeof(LogisticsStatuses.SourceType));
        AssertConstantsMatch<TrackingMilestone>(typeof(LogisticsStatuses.Milestone));
        AssertConstantsMatch<CarrierCommandStatus>(typeof(LogisticsStatuses.CarrierCommand));
    }

    // ── TC-04.5 — parsing never guesses ───────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NOT_A_STATUS")]
    [InlineData("Draft")]        // member name, not the code
    [InlineData("draft")]        // wrong case
    [InlineData("in_transit")]   // wrong case
    [InlineData("0")]            // ordinal
    public void Unknown_codes_fail_to_parse(string? code) =>
        LogisticsCode.TryParse<DeliveryStatus>(code, out _).Should().BeFalse();

    [Fact]
    public void Strict_parsing_rejects_what_the_built_in_enum_parser_would_accept()
    {
        // The trap this exists to avoid: Enum.TryParse accepts an ordinal and a member name, so
        // a corrupt or legacy value read out of the database would land on a real status — most
        // likely the first one, which is DRAFT — and look entirely plausible.
        Enum.TryParse<DeliveryStatus>("0", out var builtIn).Should().BeTrue();
        builtIn.Should().Be(DeliveryStatus.Draft);

        LogisticsCode.TryParse<DeliveryStatus>("0", out _).Should().BeFalse();
    }

    [Fact]
    public void Parse_names_the_valid_codes_when_it_fails()
    {
        var act = () => LogisticsCode.Parse<DeliveryDirection>("SIDEWAYS");

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*SIDEWAYS*")
           .WithMessage("*INBOUND*OUTBOUND*TRANSFER*");
    }

    [Fact]
    public void Codes_survive_a_member_rename()
    {
        // Pinned literals, not nameof — the point of the [Code] attribute is that renaming a
        // member must not change what is already in the database.
        LogisticsCode.Of(DeliveryStatus.PendingApproval).Should().Be("PENDING_APPROVAL");
        LogisticsCode.Of(DeliverySourceType.Po).Should().Be("PO");
        LogisticsCode.Of(ShipmentMode.Ltl).Should().Be("LTL");
        LogisticsCode.Of(ShipmentStatus.Booking).Should().Be("BOOKING");
        LogisticsCode.Of(DeliveryExceptionType.CodMismatch).Should().Be("COD_MISMATCH");
    }
}

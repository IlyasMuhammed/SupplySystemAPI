using FluentAssertions;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Domain;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Couriers;

/// <summary>
/// What implementing <see cref="ICourierProvider"/> actually means.
/// <para>
/// The interface only says what compiles. This says what the booking flow, the retry ledger and
/// the tracking poll are entitled to assume — and since those are written once for every carrier,
/// an adapter that quietly breaks one of these assumptions breaks them for carriers it has never
/// heard of.
/// </para>
/// <para>
/// <b>Every adapter derives from this and adds its own tests on top.</b> A real carrier adapter
/// arriving with sandbox credentials should be a small job precisely because this suite already
/// says what it has to do; the adapter's own tests then cover only what is peculiar to it.
/// </para>
/// <para>
/// Lives in this test project because every Phase 2 adapter does too. When a real carrier gets its
/// own project, this moves to a small shared testing library — the shape is already right for it.
/// </para>
/// </summary>
public abstract class CourierProviderContractTests
{
    /// <summary>A fresh provider. Called per test, so no test can leak state into another.</summary>
    protected abstract ICourierProvider CreateProvider();

    /// <summary>
    /// Credentials this provider considers valid. Empty for adapters that need none.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> ValidCredentials() =>
        new Dictionary<string, string>();

    /// <summary>
    /// A request this provider should accept. Override to supply anything carrier-specific — a
    /// serviceable postcode, a real service code.
    /// </summary>
    protected virtual CourierBookingRequest BookableRequest(string? idempotencyKey = null) =>
        new(
            IdempotencyKey:    idempotencyKey ?? Guid.NewGuid().ToString("N"),
            ConsignmentNumber: "SHP-2026-00001",
            ServiceCode:       null,
            ShipFrom:          new CourierAddress(
                                   "Central Warehouse", "+922111222333", null,
                                   "12 Dock Road", null, "Karachi", "Sindh", "74000", "PK"),
            ShipTo:            new CourierAddress(
                                   "Acme Supplies Ltd", "+922199888777", null,
                                   "4 Industrial Estate", null, "Lahore", "Punjab", "54000", "PK"),
            Packages:          [new CourierPackage("HU-2026-00001", "BOX", 40m, 30m, 20m, 12.5m, 1500m)],
            FreightTerms:      "PREPAID",
            CodAmount:         null,
            CodCurrency:       null,
            PickupWindowStart: null,
            PickupWindowEnd:   null,
            ReferenceNumbers:  ["DLV-2026-00001"],
            Credentials:       ValidCredentials());

    /// <summary>
    /// A request this provider should <b>refuse</b> — the carrier answers, and the answer is no.
    /// Return null when the adapter has no such case, and the refusal tests are skipped.
    /// </summary>
    protected virtual CourierBookingRequest? RefusableRequest() => null;

    /// <summary>
    /// A rate request this provider should answer. Derived from <see cref="BookableRequest"/> so
    /// the two describe the same consignment, which is what makes "quoted then booked" comparable.
    /// </summary>
    protected virtual CourierRateRequest RateableRequest(string? serviceCode = null)
    {
        var booking = BookableRequest();

        return new CourierRateRequest(
            ConsignmentNumber: booking.ConsignmentNumber,
            ServiceCode:       serviceCode,
            ShipFrom:          booking.ShipFrom,
            ShipTo:            booking.ShipTo,
            Packages:          booking.Packages,
            FreightTerms:      booking.FreightTerms,
            CodAmount:         null,
            CodCurrency:       null,
            ShipDate:          null,
            Credentials:       ValidCredentials());
    }

    // ── Identity ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_key_is_present_and_has_no_surprises_in_it()
    {
        // The key is persisted on carrier rows and matched case-insensitively by the registry.
        // Whitespace in one is a configuration bug waiting to happen.
        var provider = CreateProvider();

        provider.Key.Should().NotBeNullOrWhiteSpace();
        provider.Key.Should().Be(provider.Key.Trim());
        provider.DisplayName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_key_is_stable_across_instances()
    {
        // Carrier rows point at it. A key derived from anything per-instance would strand every
        // carrier configured for the old value.
        CreateProvider().Key.Should().Be(CreateProvider().Key);
    }

    [Fact]
    public void Capabilities_are_declared_and_amount_to_something()
    {
        // Most of this suite is capability-gated, which is what lets a tracking-only integration
        // and a full one share it. The hole that leaves is an adapter declaring everything false
        // and passing by doing nothing — so the one thing not gated is that it claims *some* job.
        var capabilities = CreateProvider().Capabilities;

        capabilities.Should().NotBeNull();

        (capabilities.SupportsBooking || capabilities.SupportsTracking
      || capabilities.SupportsLabels  || capabilities.SupportsRating)
            .Should().BeTrue(
                "a provider that neither books, tracks, labels nor rates has no reason to be registered");
    }

    [Fact]
    public void Capabilities_do_not_promise_what_they_cannot_reach()
    {
        // Cancelling and labelling both act on a booking. Claiming them without booking describes
        // an adapter that can only operate on airway bills it has no way of obtaining.
        var capabilities = CreateProvider().Capabilities;

        if (capabilities.SupportsCancellation || capabilities.SupportsLabels)
            capabilities.SupportsBooking.Should().BeTrue();

        if (capabilities.HonoursIdempotencyKey)
            capabilities.SupportsBooking.Should().BeTrue();
    }

    // ── Rating (T-45) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Rating_returns_priced_options_or_says_it_cannot_rate()
    {
        var provider = CreateProvider();

        var result = await provider.RateAsync(RateableRequest());

        result.ProviderKey.Should().Be(provider.Key);

        if (!provider.Capabilities.SupportsRating)
        {
            // Never a silent empty success: a caller told "succeeded, no options" would conclude
            // the carrier priced the consignment at nothing.
            result.Outcome.Should().Be(CourierOutcome.Unsupported);
            result.Options.Should().BeEmpty();
            result.Message.Should().NotBeNullOrWhiteSpace(
                "an adapter that cannot rate has to say where the price does come from");
            return;
        }

        result.Outcome.Should().Be(CourierOutcome.Succeeded);
        result.Options.Should().NotBeEmpty("a successful rate call with no options priced nothing");
    }

    [Fact]
    public async Task Every_quoted_option_is_a_price_somebody_could_act_on()
    {
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsRating) return;

        var result = await provider.RateAsync(RateableRequest());

        foreach (var option in result.Options)
        {
            option.ServiceCode.Should().NotBeNullOrWhiteSpace(
                "a price with no service on it cannot be booked");
            option.TotalAmount.Should().BeGreaterThan(0m, "free carriage is not a quote");
            // Three-letter ISO. A total with no currency is a number, not a price — and Phase 4
            // compares it against an invoice that certainly has one.
            option.Currency.Should().MatchRegex("^[A-Z]{3}$");
        }
    }

    [Fact]
    public async Task No_two_options_quote_the_same_service()
    {
        // Two prices for one service make "which option won" depend on row order — the same
        // ambiguity carrier services (T-43) exist to end.
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsRating) return;

        var result = await provider.RateAsync(RateableRequest());

        result.Options.Select(o => o.ServiceCode.ToUpperInvariant())
              .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_quote_adds_up_to_its_own_total()
    {
        // The classic adapter defect: surcharges parsed and then not included, or included twice.
        // Phase 4's three-way match compares an itemised invoice against this, so a quote whose
        // parts do not reconcile to its total is worse than one with no parts at all.
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsRating) return;

        var result = await provider.RateAsync(RateableRequest());

        foreach (var option in result.Options)
        {
            if (option.BaseAmount is not { } baseAmount) continue;

            var parts = baseAmount + (option.Surcharges?.Sum(s => s.Amount) ?? 0m);

            parts.Should().BeApproximately(option.TotalAmount, 0.01m,
                $"'{option.ServiceCode}' quotes {option.TotalAmount} and its parts come to {parts}");
        }
    }

    [Fact]
    public async Task Every_surcharge_is_named()
    {
        // "Other: 1,240" is not a line anybody can dispute with a carrier.
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsRating) return;

        var result = await provider.RateAsync(RateableRequest());

        foreach (var surcharge in result.Options.SelectMany(o => o.Surcharges ?? []))
            surcharge.Code.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Naming_a_service_quotes_that_service_and_no_other()
    {
        // What rating a booked consignment asks for: one service, its price. Returning the whole
        // list would leave the caller guessing which one it was told about.
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsRating) return;

        var all = await provider.RateAsync(RateableRequest());
        if (all.Options.Count == 0) return;

        var wanted = all.Options[0].ServiceCode;

        var one = await provider.RateAsync(RateableRequest(wanted));

        one.Succeeded.Should().BeTrue();
        one.Options.Should().ContainSingle();
        one.Options[0].ServiceCode.Should().Be(wanted);
    }

    [Fact]
    public async Task Rating_nothing_is_not_a_success()
    {
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsRating) return;

        var result = await provider.RateAsync(RateableRequest() with { Packages = [] });

        result.Outcome.Should().NotBe(CourierOutcome.Succeeded);
        result.Options.Should().BeEmpty();
    }

    [Fact]
    public async Task Rating_is_a_read_and_can_be_repeated()
    {
        // There is no idempotency key on a rate request, and deliberately so. An adapter that
        // consumed something, rate-limited itself, or cached a first answer into a later wrong one
        // would have made a read into a write.
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsRating) return;

        var first  = await provider.RateAsync(RateableRequest());
        var second = await provider.RateAsync(RateableRequest());

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeTrue();
        second.Options.Select(o => o.ServiceCode)
              .Should().BeEquivalentTo(first.Options.Select(o => o.ServiceCode));
    }

    [Fact]
    public async Task A_cancelled_token_is_honoured_while_rating_too()
    {
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsRating) return;

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await provider.RateAsync(RateableRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Booking ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Booking_returns_an_airway_bill_and_names_the_provider()
    {
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsBooking) return;

        var result = await provider.BookAsync(BookableRequest());

        result.Outcome.Should().Be(CourierOutcome.Succeeded);
        // Without an AWB the consignment is BOOKED with nothing to track and nothing to prove it.
        result.AwbNumber.Should().NotBeNullOrWhiteSpace();
        // A result that does not name its provider makes a mis-registered adapter undetectable.
        result.ProviderKey.Should().Be(provider.Key);
    }

    [Fact]
    public async Task A_refused_booking_is_an_answer_not_an_exception()
    {
        // The whole reason CourierOutcome exists. A refusal is final and must be distinguishable
        // from a call that came apart, which may have created a real parcel.
        var request = RefusableRequest();
        if (request is null) return;

        var provider = CreateProvider();

        var result = await provider.BookAsync(request);

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.AwbNumber.Should().BeNullOrWhiteSpace("nothing was booked");
        result.Message.Should().NotBeNullOrWhiteSpace("a refusal somebody cannot act on is not an answer");
        result.ProviderKey.Should().Be(provider.Key);
    }

    [Fact]
    public async Task A_provider_that_honours_idempotency_returns_the_same_booking_twice()
    {
        // The guarantee the retry ledger leans on where it exists. Where it does not, T-36's
        // ledger is the only thing standing between a retry and a second real parcel — which is
        // why this is capability-gated rather than assumed.
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsBooking) return;
        if (!provider.Capabilities.HonoursIdempotencyKey) return;

        var key = Guid.NewGuid().ToString("N");

        var first  = await provider.BookAsync(BookableRequest(key));
        var second = await provider.BookAsync(BookableRequest(key));

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeTrue();
        second.AwbNumber.Should().Be(first.AwbNumber, "the same key must not book a second parcel");
    }

    [Fact]
    public async Task Different_idempotency_keys_are_different_bookings()
    {
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsBooking) return;

        var first  = await provider.BookAsync(BookableRequest());
        var second = await provider.BookAsync(BookableRequest());

        first.AwbNumber.Should().NotBe(second.AwbNumber);
    }

    [Fact]
    public async Task A_multi_piece_booking_is_accepted_when_the_provider_claims_to_support_it()
    {
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsBooking) return;
        if (!provider.Capabilities.SupportsMultiPiece) return;

        var single = BookableRequest();
        var request = single with
        {
            Packages =
            [
                .. single.Packages,
                new CourierPackage("HU-2026-00002", "BOX", 20m, 20m, 20m, 4m, 100m)
            ]
        };

        (await provider.BookAsync(request)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_booking_with_no_packages_is_refused_rather_than_accepted()
    {
        // An empty consignment has nothing to label, nothing to weigh and nothing to hand over.
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsBooking) return;

        var result = await provider.BookAsync(BookableRequest() with { Packages = [] });

        result.Outcome.Should().NotBe(CourierOutcome.Succeeded);
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_a_booking_succeeds_or_says_it_is_unsupported()
    {
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsBooking) return;

        var booking = await provider.BookAsync(BookableRequest());

        var result = await provider.CancelAsync(new CourierCancelRequest(
            Guid.NewGuid().ToString("N"), "SHP-2026-00001", booking.AwbNumber!, ValidCredentials()));

        result.ProviderKey.Should().Be(provider.Key);

        // Either it does the job, or it says plainly that it cannot — never a silent no-op that
        // leaves the caller believing a real parcel was stopped.
        if (provider.Capabilities.SupportsCancellation)
            result.Outcome.Should().Be(CourierOutcome.Succeeded);
        else
            result.Outcome.Should().Be(CourierOutcome.Unsupported);
    }

    [Fact]
    public async Task Cancelling_something_that_was_never_booked_does_not_throw()
    {
        var provider = CreateProvider();

        var act = async () => await provider.CancelAsync(new CourierCancelRequest(
            Guid.NewGuid().ToString("N"), "SHP-NOPE", "AWB-NEVER-EXISTED", ValidCredentials()));

        var result = await act.Should().NotThrowAsync();
        result.Subject.Outcome.Should().NotBe(CourierOutcome.Succeeded);
    }

    // ── Labels ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_label_comes_back_as_bytes_with_a_real_media_type()
    {
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsBooking) return;

        var booking = await provider.BookAsync(BookableRequest());
        var result  = await provider.GetLabelAsync(booking.AwbNumber!, ValidCredentials());

        result.ProviderKey.Should().Be(provider.Key);

        if (!provider.Capabilities.SupportsLabels)
        {
            result.Outcome.Should().Be(CourierOutcome.Unsupported);
            return;
        }

        result.Outcome.Should().Be(CourierOutcome.Succeeded);
        result.Label.Should().NotBeNull();
        result.Label!.Content.Should().NotBeEmpty();
        // Served straight to a browser, so a made-up type means a label nobody can open.
        result.Label.ContentType.Should().Contain("/");
    }

    [Fact]
    public async Task A_label_for_an_unknown_airway_bill_is_not_a_success()
    {
        var provider = CreateProvider();

        var result = await provider.GetLabelAsync("AWB-NEVER-EXISTED", ValidCredentials());

        result.Outcome.Should().NotBe(CourierOutcome.Succeeded);
        result.Label.Should().BeNull();
    }

    // ── Tracking ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tracking_events_come_back_oldest_first()
    {
        // The tracking poll folds these into the consignment in order. Out of order, a delivered
        // parcel can end up displayed as still in transit.
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsBooking) return;

        var booking = await provider.BookAsync(BookableRequest());

        var result = await provider.TrackAsync(new CourierTrackingRequest(
            booking.AwbNumber!, "SHP-2026-00001", ValidCredentials()));

        result.ProviderKey.Should().Be(provider.Key);

        if (!provider.Capabilities.SupportsTracking)
        {
            result.Outcome.Should().Be(CourierOutcome.Unsupported);
            return;
        }

        result.Outcome.Should().Be(CourierOutcome.Succeeded);
        result.Events.Should().BeInAscendingOrder(e => e.OccurredAt);
    }

    [Fact]
    public async Task Every_tracking_event_carries_a_milestone_we_recognise()
    {
        // An adapter that passes the carrier's own vocabulary through as a milestone puts strings
        // nothing maps to into the timeline. CarrierStatus is where those belong.
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsBooking) return;
        if (!provider.Capabilities.SupportsTracking) return;

        var booking = await provider.BookAsync(BookableRequest());

        var result = await provider.TrackAsync(new CourierTrackingRequest(
            booking.AwbNumber!, "SHP-2026-00001", ValidCredentials()));

        var known = LogisticsCode.Codes<TrackingMilestone>().ToList();

        foreach (var evt in result.Events)
            known.Should().Contain(evt.Milestone,
                $"'{evt.Milestone}' is not a TrackingMilestone this system knows");
    }

    [Fact]
    public async Task Tracking_an_unknown_airway_bill_is_not_a_success()
    {
        var provider = CreateProvider();

        var result = await provider.TrackAsync(new CourierTrackingRequest(
            "AWB-NEVER-EXISTED", null, ValidCredentials()));

        result.Outcome.Should().NotBe(CourierOutcome.Succeeded);
        result.Events.Should().BeEmpty();
    }

    // ── Cancellation token ────────────────────────────────────────────────────

    [Fact]
    public async Task A_cancelled_token_is_honoured_rather_than_ignored()
    {
        // These are outbound HTTP calls behind a job with a timeout. An adapter that ignores the
        // token holds a worker open for as long as the carrier feels like taking.
        var provider = CreateProvider();
        if (!provider.Capabilities.SupportsBooking) return;

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await provider.BookAsync(BookableRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

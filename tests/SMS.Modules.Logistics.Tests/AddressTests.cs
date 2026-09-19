using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// Stands in for SMS.Modules.Lookups. Logistics only knows the SMS.Shared contract, so the tests
// never need the Lookups database.
internal sealed class FakeCityLookup : ICityLookupService
{
    private readonly Dictionary<Guid, CityLookupResult> _cities = [];

    internal Guid Add(string cityName, string countryName, string? countryCode)
    {
        var id = Guid.NewGuid();
        _cities[id] = new CityLookupResult(id, cityName, Guid.NewGuid(), countryName, countryCode);
        return id;
    }

    public Task<CityLookupResult?> FindAsync(Guid cityId) =>
        Task.FromResult(_cities.GetValueOrDefault(cityId));
}

// T-06 — structured addresses.
public class AddressTests
{
    private static readonly string Unvalidated = LogisticsCode.Of(AddressValidationStatus.Unvalidated);
    private static readonly string Valid       = LogisticsCode.Of(AddressValidationStatus.Valid);

    private static Address NewAddress(
        string? line1    = "Plot 12, Korangi Industrial Area",
        string? city     = "Karachi",
        string? country  = "Pakistan",
        string? isoCode  = "PK",
        string? phone    = null) => new()
        {
            UUID           = Guid.NewGuid(),
            Line1          = line1!,
            CityName       = city!,
            CountryName    = country!,
            CountryIsoCode = isoCode,
            ContactPhone   = phone,
            CreatedBy      = 1,
            CreatedDate    = DateTime.UtcNow
        };

    private static AddressNormalizer Normalizer(ICityLookupService? cities = null) =>
        new(cities ?? new FakeCityLookup());

    // ── TC-06.1 — the three fields an address is meaningless without ──────────

    [Theory]
    [InlineData(null, "Karachi", "Pakistan", "Address line 1")]
    [InlineData("",   "Karachi", "Pakistan", "Address line 1")]
    [InlineData("  ", "Karachi", "Pakistan", "Address line 1")]
    [InlineData("Plot 12", null, "Pakistan", "City")]
    [InlineData("Plot 12", "",   "Pakistan", "City")]
    [InlineData("Plot 12", "Karachi", null,  "Country")]
    [InlineData("Plot 12", "Karachi", "",    "Country")]
    public async Task A_structurally_meaningless_address_is_rejected_by_field_name(
        string? line1, string? city, string? country, string expectedField)
    {
        var act = async () => await Normalizer()
            .NormalizeAsync(NewAddress(line1: line1, city: city, country: country));

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage($"{expectedField} is required.");
    }

    [Fact]
    public async Task Whitespace_is_trimmed_from_every_text_field()
    {
        var address = NewAddress(line1: "  Plot 12  ", city: " Karachi ", country: " Pakistan ");
        address.Line2 = "   ";
        address.State = "  Sindh  ";

        await Normalizer().NormalizeAsync(address);

        address.Line1.Should().Be("Plot 12");
        address.CityName.Should().Be("Karachi");
        address.CountryName.Should().Be("Pakistan");
        address.State.Should().Be("Sindh");
        address.Line2.Should().BeNull("a blank optional field is null, not an empty string");
    }

    // ── TC-06.2 / TC-06.3 — phone normalization ──────────────────────────────

    [Theory]
    [InlineData("03001234567")]      // local, no separators
    [InlineData("0300-1234567")]     // local, hyphenated
    [InlineData("0300 1234567")]     // local, spaced
    [InlineData("+92 300 1234567")]  // international, spaced
    [InlineData("+92-300-1234567")]  // international, hyphenated
    [InlineData("+923001234567")]    // already E.164
    [InlineData("923001234567")]     // calling code, no plus
    public async Task A_pakistani_mobile_normalizes_to_one_e164_form(string raw)
    {
        var address = NewAddress(phone: raw);

        await Normalizer().NormalizeAsync(address);

        address.ContactPhoneE164.Should().Be("+923001234567");
        address.ValidationStatus.Should().Be(Valid);
    }

    [Fact]
    public async Task Normalizing_an_already_normalized_phone_changes_nothing()
    {
        var first = NewAddress(phone: "0300-1234567");
        await Normalizer().NormalizeAsync(first);

        var second = NewAddress(phone: first.ContactPhoneE164);
        await Normalizer().NormalizeAsync(second);

        second.ContactPhoneE164.Should().Be(first.ContactPhoneE164);
    }

    [Fact]
    public async Task The_phone_as_entered_is_never_overwritten()
    {
        // The raw value is what a human can recognise and correct. Rewriting it in place would
        // destroy the only evidence of what was actually typed.
        var address = NewAddress(phone: "0300-1234567");

        await Normalizer().NormalizeAsync(address);

        address.ContactPhone.Should().Be("0300-1234567");
        address.ContactPhoneE164.Should().Be("+923001234567");
    }

    [Fact]
    public async Task A_fixed_line_consignee_number_is_accepted()
    {
        // Deliberately more permissive than the supplier-contact rule, which is mobile-only for
        // WhatsApp. A consignee is often an office reception; rejecting landlines would push
        // deliverable addresses into the exception queue for no reason.
        var address = NewAddress(phone: "+92 21 35061111");

        await Normalizer().NormalizeAsync(address);

        address.ContactPhoneE164.Should().Be("+922135061111");
        address.ValidationStatus.Should().Be(Valid);
    }

    // ── TC-06.4 — a bad phone downgrades, it does not throw ──────────────────

    [Theory]
    [InlineData("not a phone")]
    [InlineData("12")]
    [InlineData("+99999999999999")]
    public async Task An_unreadable_phone_downgrades_the_address_instead_of_failing(string raw)
    {
        var address = NewAddress(phone: raw);

        var act = async () => await Normalizer().NormalizeAsync(address);
        await act.Should().NotThrowAsync();

        address.ContactPhoneE164.Should().BeNull();
        address.ValidationStatus.Should().Be(Unvalidated);
        address.ValidationNotes.Should().Contain(raw);
        address.ContactPhone.Should().Be(raw, "the raw value is kept so it can be corrected");
    }

    [Fact]
    public async Task A_local_number_without_a_country_code_says_what_is_missing()
    {
        var address = NewAddress(isoCode: null, phone: "0300-1234567");

        await Normalizer().NormalizeAsync(address);

        address.ValidationStatus.Should().Be(Unvalidated);
        address.ValidationNotes.Should().Contain("country code");
    }

    [Fact]
    public async Task An_address_with_no_phone_at_all_is_still_valid()
    {
        var address = NewAddress(phone: null);

        await Normalizer().NormalizeAsync(address);

        address.ValidationStatus.Should().Be(Valid);
        address.ContactPhoneE164.Should().BeNull();
    }

    // ── TC-06.5 / TC-06.6 — the city catalogue ───────────────────────────────

    [Fact]
    public async Task A_known_city_fills_in_its_country()
    {
        var catalogue = new FakeCityLookup();
        var cityId    = catalogue.Add("Lahore", "Pakistan", "PK");

        var address = NewAddress(city: "lahore", country: "pakistan", isoCode: null);
        address.CityId = cityId;

        await Normalizer(catalogue).NormalizeAsync(address);

        address.CityName.Should().Be("Lahore", "the catalogue spelling wins over what was typed");
        address.CountryName.Should().Be("Pakistan");
        address.CountryId.Should().NotBeNull();
        address.CountryIsoCode.Should().Be("PK");
        address.ValidationStatus.Should().Be(Valid);
    }

    [Fact]
    public async Task An_unknown_city_id_downgrades_but_keeps_the_typed_text()
    {
        var address = NewAddress(city: "Gwadar");
        address.CityId = Guid.NewGuid(); // never added to the catalogue

        await Normalizer().NormalizeAsync(address);

        address.ValidationStatus.Should().Be(Unvalidated);
        address.CityName.Should().Be("Gwadar",
            "a city removed from the catalogue must not erase where the goods were going");
        address.CityId.Should().BeNull("the dangling reference is cleared, the text is not");
        address.ValidationNotes.Should().Contain("Gwadar");
    }

    [Fact]
    public async Task An_address_with_no_city_id_is_valid_on_its_text_alone()
    {
        // This is the shape every legacy address backfilled in T-16 will have.
        var address = NewAddress();
        address.CityId = null;

        await Normalizer().NormalizeAsync(address);

        address.ValidationStatus.Should().Be(Valid);
    }

    [Fact]
    public async Task A_catalogue_country_code_that_is_not_iso_is_ignored()
    {
        // Country.Code is free text in the Lookups admin screen — only uniqueness is enforced —
        // so it can be "PAK", "92" or a typo. Adopting it blindly would silently misparse phones.
        var catalogue = new FakeCityLookup();
        var cityId    = catalogue.Add("Karachi", "Pakistan", "PAK");

        var address = NewAddress(isoCode: null, phone: "+923001234567");
        address.CityId = cityId;

        await Normalizer(catalogue).NormalizeAsync(address);

        address.CountryIsoCode.Should().BeNull();
        address.ContactPhoneE164.Should().Be("+923001234567",
            "an international number is self-describing and needs no region");
    }

    [Fact]
    public async Task A_country_code_that_is_not_a_real_region_is_rejected_and_reported()
    {
        var address = NewAddress(isoCode: "XX");

        await Normalizer().NormalizeAsync(address);

        address.CountryIsoCode.Should().BeNull();
        address.ValidationStatus.Should().Be(Unvalidated);
        address.ValidationNotes.Should().Contain("XX");
    }

    [Fact]
    public async Task A_lowercase_country_code_is_accepted_and_upcased()
    {
        var address = NewAddress(isoCode: "pk", phone: "0300-1234567");

        await Normalizer().NormalizeAsync(address);

        address.CountryIsoCode.Should().Be("PK");
        address.ContactPhoneE164.Should().Be("+923001234567");
    }

    [Fact]
    public async Task Every_problem_is_reported_not_just_the_first()
    {
        // Whoever works the exception queue should be able to fix an address in one pass.
        var address = NewAddress(isoCode: "XX", phone: "nonsense");
        address.CityId = Guid.NewGuid();

        await Normalizer().NormalizeAsync(address);

        address.ValidationNotes.Should().Contain("XX").And.Contain("nonsense").And.Contain("catalogue");
    }

    // ── Persistence: TC-06.7 / TC-06.8 ───────────────────────────────────────

    [Fact]
    public async Task An_address_round_trips_and_is_tenant_scoped()
    {
        var orgA = Guid.NewGuid();
        var (db, _, dbName) = LogisticsTestDb.New(orgA);

        var address = NewAddress(phone: "0300-1234567");
        await Normalizer().NormalizeAsync(address);

        db.Addresses.Add(address);
        await db.SaveChangesAsync();

        (await db.Addresses.SingleAsync()).ContactPhoneE164.Should().Be("+923001234567");

        await using var other = LogisticsTestDb.OpenAs(dbName, Guid.NewGuid());
        (await other.Addresses.ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_address_saved_twice_produces_two_rows()
    {
        // Addresses are snapshots, not master data. De-duplicating would let editing one row
        // silently rewrite where an already-shipped delivery went.
        var (db, _, _) = LogisticsTestDb.New();
        var normalizer = Normalizer();

        foreach (var _ in Enumerable.Range(0, 2))
        {
            var address = NewAddress();
            await normalizer.NormalizeAsync(address);
            db.Addresses.Add(address);
        }

        await db.SaveChangesAsync();

        (await db.Addresses.ToListAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_new_address_defaults_to_unvalidated_before_normalization()
    {
        // Nothing should be able to create a VALID address by skipping the normalizer.
        var address = NewAddress();

        address.ValidationStatus.Should().Be(Unvalidated);
        address.AddressType.Should().Be(LogisticsCode.Of(AddressType.Other));

        await Normalizer().NormalizeAsync(address);
        address.ValidationStatus.Should().Be(Valid);
    }
}

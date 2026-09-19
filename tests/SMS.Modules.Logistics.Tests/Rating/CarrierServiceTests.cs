using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Rating;

// T-43 — the named products a carrier sells, and the terms that price them.
public class CarrierServiceTests
{
    private const int User = 42;

    private sealed record Harness(LogisticsDbContext Db, CarrierServiceRepository Services);

    private static Harness NewHarness()
    {
        var (db, _, _) = LogisticsTestDb.New();
        return new Harness(db, new CarrierServiceRepository(db));
    }

    private static async Task<(Guid Uuid, int Id)> NewCarrier(Harness h, string name = "Simcourier")
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = name, Code = $"C{Guid.NewGuid():N}"[..6], IsActive = true
        };

        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return (carrier.UUID, carrier.Id);
    }

    private static CreateCarrierServiceRequest NewService(
        Guid carrierUuid, string code = "OVERNIGHT", string name = "Overnight",
        int? divisor = 5000, bool isDefault = false) =>
        new()
        {
            CarrierUuid = carrierUuid, ServiceCode = code, ServiceName = name,
            DimDivisor = divisor, IsDefault = isDefault
        };

    // ── Creating ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_service_carries_the_terms_that_price_it()
    {
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);

        var uuid = await h.Services.CreateAsync(new CreateCarrierServiceRequest
        {
            CarrierUuid           = carrier,
            ServiceCode           = "ECON",
            ServiceName           = "Economy",
            Description           = "Three to five working days",
            DimDivisor            = 6000,
            MinimumChargeableKg   = 0.5m,
            MaxWeightKgPerPackage = 30m,
            MaxLengthCm           = 120m,
            MaxLengthPlusGirthCm  = 300m,
            SupportsCod           = true,
            TransitDays           = 4
        }, User);

        var service = await h.Services.GetByUuidAsync(uuid);

        service!.ServiceCode.Should().Be("ECON");
        service.DimDivisor.Should().Be(6000);
        service.MinimumChargeableKg.Should().Be(0.5m);
        service.MaxLengthPlusGirthCm.Should().Be(300m);
        service.SupportsCod.Should().BeTrue();
        service.SupportsHazardous.Should().BeFalse();
        service.TransitDays.Should().Be(4);
        service.CarrierName.Should().Be("Simcourier");
    }

    [Fact]
    public async Task The_first_service_is_the_default_whether_or_not_anybody_said_so()
    {
        // A carrier whose only service is not the default has nothing to offer a consignment that
        // names none.
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);

        var uuid = await h.Services.CreateAsync(NewService(carrier, isDefault: false), User);

        (await h.Services.GetByUuidAsync(uuid))!.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Making_a_new_service_the_default_clears_the_old_one()
    {
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);

        var first  = await h.Services.CreateAsync(NewService(carrier, "OVERNIGHT", "Overnight"), User);
        var second = await h.Services.CreateAsync(
            NewService(carrier, "ECON", "Economy", isDefault: true), User);

        (await h.Services.GetByUuidAsync(first))!.IsDefault.Should().BeFalse();
        (await h.Services.GetByUuidAsync(second))!.IsDefault.Should().BeTrue();
        (await h.Db.CarrierServices.CountAsync(s => s.IsDefault)).Should().Be(1);
    }

    [Fact]
    public async Task One_carrier_cannot_have_two_services_with_the_same_code()
    {
        // The ambiguity this table exists to end: a consignment names a service by code.
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);

        await h.Services.CreateAsync(NewService(carrier, "ECON"), User);

        var act = async () => await h.Services.CreateAsync(NewService(carrier, "  ECON  ", "Economy 2"), User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*already has a service with code*");
    }

    [Fact]
    public async Task Different_carriers_may_use_the_same_code()
    {
        // ECON means something different at each carrier, and both are that carrier's own code.
        var h = NewHarness();
        var (one, _) = await NewCarrier(h, "Carrier One");
        var (two, _) = await NewCarrier(h, "Carrier Two");

        await h.Services.CreateAsync(NewService(one, "ECON"), User);

        var act = async () => await h.Services.CreateAsync(NewService(two, "ECON"), User);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_service_needs_a_code_a_name_and_an_existing_carrier()
    {
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);

        var noCode = async () => await h.Services.CreateAsync(NewService(carrier, "  "), User);
        (await noCode.Should().ThrowAsync<BadRequestException>()).WithMessage("*service code is required*");

        var noName = async () => await h.Services.CreateAsync(NewService(carrier, "X", "  "), User);
        await noName.Should().ThrowAsync<BadRequestException>();

        var noCarrier = async () => await h.Services.CreateAsync(NewService(Guid.NewGuid()), User);
        await noCarrier.Should().ThrowAsync<NotFoundException>();
    }

    // ── The dim divisor ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-5000)]
    public async Task A_divisor_that_would_break_the_arithmetic_is_refused(int divisor)
    {
        // It is the denominator in L×W×H ÷ divisor, which T-44 will evaluate. Zero throws and a
        // negative returns a negative weight; refusing here is cheaper than defending every use.
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);

        var act = async () => await h.Services.CreateAsync(NewService(carrier, divisor: divisor), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*greater than zero*")
            .WithMessage("*does not charge on volume*");
    }

    [Fact]
    public async Task No_divisor_means_the_service_does_not_price_on_volume()
    {
        // A real arrangement — same-day courier work and much road freight is priced by actual
        // weight — so it is an answer, not missing data, and the model says which.
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);

        var uuid = await h.Services.CreateAsync(NewService(carrier, divisor: null), User);

        var service = await h.Services.GetByUuidAsync(uuid);
        service!.DimDivisor.Should().BeNull();
        service.ChargesVolumetricWeight.Should().BeFalse();
    }

    [Fact]
    public async Task A_divisor_is_reported_as_charging_on_volume()
    {
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);

        var uuid = await h.Services.CreateAsync(NewService(carrier, divisor: 5000), User);

        (await h.Services.GetByUuidAsync(uuid))!.ChargesVolumetricWeight.Should().BeTrue();
    }

    [Fact]
    public async Task Limits_must_be_positive_when_they_are_given()
    {
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);

        var act = async () => await h.Services.CreateAsync(new CreateCarrierServiceRequest
        {
            CarrierUuid = carrier, ServiceCode = "X", ServiceName = "X", MaxWeightKgPerPackage = 0m
        }, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*Maximum weight*");
    }

    [Fact]
    public async Task Transit_days_may_be_zero_but_not_negative()
    {
        // Zero is same-day, which is a real service.
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);

        var sameDay = await h.Services.CreateAsync(new CreateCarrierServiceRequest
        {
            CarrierUuid = carrier, ServiceCode = "SAMEDAY", ServiceName = "Same day", TransitDays = 0
        }, User);
        (await h.Services.GetByUuidAsync(sameDay))!.TransitDays.Should().Be(0);

        var act = async () => await h.Services.CreateAsync(new CreateCarrierServiceRequest
        {
            CarrierUuid = carrier, ServiceCode = "NEG", ServiceName = "Negative", TransitDays = -1
        }, User);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    // ── Resolving what a consignment names ────────────────────────────────────

    [Fact]
    public async Task A_code_resolves_to_its_service()
    {
        var h = NewHarness();
        var (carrier, carrierId) = await NewCarrier(h);
        await h.Services.CreateAsync(NewService(carrier, "ECON", "Economy"), User);
        await h.Services.CreateAsync(NewService(carrier, "OVERNIGHT", "Overnight"), User);

        (await h.Services.ResolveAsync(carrierId, "OVERNIGHT"))!.ServiceName.Should().Be("Overnight");
    }

    [Theory]
    [InlineData("econ")]
    [InlineData("Econ")]
    [InlineData("  ECON  ")]
    public async Task A_code_resolves_however_it_was_typed(string typed)
    {
        // On a consignment the code may have been typed years before this table existed.
        var h = NewHarness();
        var (carrier, carrierId) = await NewCarrier(h);
        await h.Services.CreateAsync(NewService(carrier, "ECON", "Economy"), User);

        (await h.Services.ResolveAsync(carrierId, typed))!.ServiceCode.Should().Be("ECON");
    }

    [Fact]
    public async Task No_code_resolves_to_the_default()
    {
        var h = NewHarness();
        var (carrier, carrierId) = await NewCarrier(h);
        await h.Services.CreateAsync(NewService(carrier, "ECON", "Economy"), User);
        await h.Services.CreateAsync(NewService(carrier, "OVERNIGHT", "Overnight", isDefault: true), User);

        (await h.Services.ResolveAsync(carrierId, null))!.ServiceCode.Should().Be("OVERNIGHT");
    }

    [Fact]
    public async Task An_unknown_code_resolves_to_nothing_rather_than_throwing()
    {
        // Consignments already carry codes from before this table existed, and one that matches
        // nothing must still read — it simply cannot be explained.
        var h = NewHarness();
        var (carrier, carrierId) = await NewCarrier(h);
        await h.Services.CreateAsync(NewService(carrier, "ECON"), User);

        (await h.Services.ResolveAsync(carrierId, "NOT_A_SERVICE")).Should().BeNull();
    }

    [Fact]
    public async Task A_carrier_with_no_services_resolves_to_nothing()
    {
        var h = NewHarness();
        var (_, carrierId) = await NewCarrier(h);

        (await h.Services.ResolveAsync(carrierId, "ANY")).Should().BeNull();
        (await h.Services.ResolveAsync(carrierId, null)).Should().BeNull();
    }

    [Fact]
    public async Task An_inactive_service_is_not_resolved()
    {
        var h = NewHarness();
        var (carrier, carrierId) = await NewCarrier(h);
        await h.Services.CreateAsync(NewService(carrier, "KEEP", "Keep", isDefault: true), User);
        var retired = await h.Services.CreateAsync(NewService(carrier, "OLD", "Old"), User);

        await h.Services.PatchAsync(retired, new PatchCarrierServiceRequest { IsActive = false }, User);

        (await h.Services.ResolveAsync(carrierId, "OLD")).Should().BeNull();
    }

    // ── Editing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_limit_can_be_cleared_back_to_no_limit()
    {
        // A null on a patch means "leave alone", so removing a limit needs its own instruction.
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);

        var uuid = await h.Services.CreateAsync(new CreateCarrierServiceRequest
        {
            CarrierUuid = carrier, ServiceCode = "X", ServiceName = "X",
            DimDivisor = 5000, MaxWeightKgPerPackage = 30m
        }, User);

        await h.Services.PatchAsync(uuid, new PatchCarrierServiceRequest
        {
            ClearLimits = ["max_weight", "DIM_DIVISOR"]
        }, User);

        var service = await h.Services.GetByUuidAsync(uuid);
        service!.MaxWeightKgPerPackage.Should().BeNull();
        service.DimDivisor.Should().BeNull();
        service.ChargesVolumetricWeight.Should().BeFalse();
    }

    [Fact]
    public async Task Clearing_something_that_is_not_a_limit_is_refused()
    {
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);
        var uuid = await h.Services.CreateAsync(NewService(carrier), User);

        var act = async () => await h.Services.PatchAsync(
            uuid, new PatchCarrierServiceRequest { ClearLimits = ["COLOUR"] }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*COLOUR*")
            .WithMessage("*DIM_DIVISOR*");
    }

    [Fact]
    public async Task A_patched_divisor_is_validated_like_a_created_one()
    {
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);
        var uuid = await h.Services.CreateAsync(NewService(carrier), User);

        var act = async () => await h.Services.PatchAsync(
            uuid, new PatchCarrierServiceRequest { DimDivisor = 0 }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task The_default_service_cannot_be_deactivated_or_removed_while_others_exist()
    {
        // A consignment naming no service would have nothing to use.
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);

        var primary = await h.Services.CreateAsync(NewService(carrier, "PRIMARY", "Primary"), User);
        await h.Services.CreateAsync(NewService(carrier, "SPARE", "Spare"), User);

        var deactivate = async () => await h.Services.PatchAsync(
            primary, new PatchCarrierServiceRequest { IsActive = false }, User);
        (await deactivate.Should().ThrowAsync<ConflictException>()).WithMessage("*default service*");

        var remove = async () => await h.Services.DeleteAsync(primary, User);
        (await remove.Should().ThrowAsync<ConflictException>()).WithMessage("*default service*");
    }

    [Fact]
    public async Task Unsetting_a_default_directly_is_refused_and_says_what_to_do_instead()
    {
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);
        var only = await h.Services.CreateAsync(NewService(carrier), User);

        var act = async () => await h.Services.PatchAsync(
            only, new PatchCarrierServiceRequest { IsDefault = false }, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*Make another the default instead*");
    }

    [Fact]
    public async Task A_removed_service_is_kept_so_old_consignments_still_read()
    {
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);
        var only = await h.Services.CreateAsync(NewService(carrier), User);

        (await h.Services.DeleteAsync(only, User)).Should().BeTrue();

        (await h.Services.GetForCarrierAsync(carrier)).Should().BeEmpty();
        // Soft, not gone: a consignment booked on it still names its code.
        (await h.Db.CarrierServices.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_removed_services_code_can_be_used_again()
    {
        // The repository has always allowed this; until T-49 the unique index did not, so the
        // database would have refused a service the application had already accepted. The index is
        // now filtered on IsDelete and the two agree.
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);

        var first = await h.Services.CreateAsync(NewService(carrier, "ECON", "Economy"), User);
        await h.Services.DeleteAsync(first, User);

        var act = async () => await h.Services.CreateAsync(
            NewService(carrier, "ECON", "Economy, renegotiated"), User);

        await act.Should().NotThrowAsync();

        (await h.Services.GetForCarrierAsync(carrier)).Should().ContainSingle()
            .Which.ServiceName.Should().Be("Economy, renegotiated");
    }

    [Fact]
    public async Task An_unknown_service_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.Services.GetByUuidAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Services.PatchAsync(Guid.NewGuid(), new PatchCarrierServiceRequest(), User)).Should().BeFalse();
        (await h.Services.DeleteAsync(Guid.NewGuid(), User)).Should().BeFalse();
        (await h.Services.GetForCarrierAsync(Guid.NewGuid())).Should().BeEmpty();
    }

    [Fact]
    public async Task Services_list_with_the_default_first()
    {
        var h = NewHarness();
        var (carrier, _) = await NewCarrier(h);

        await h.Services.CreateAsync(NewService(carrier, "AAA", "Aaa"), User);
        await h.Services.CreateAsync(NewService(carrier, "ZZZ", "Zzz", isDefault: true), User);

        (await h.Services.GetForCarrierAsync(carrier)).Select(s => s.ServiceName)
            .Should().Equal(["Zzz", "Aaa"]);
    }

    [Fact]
    public async Task Another_organizations_service_does_not_exist_here()
    {
        var (db, _, dbName) = LogisticsTestDb.New();
        var h = new Harness(db, new CarrierServiceRepository(db));

        var (carrier, _) = await NewCarrier(h);
        var uuid = await h.Services.CreateAsync(NewService(carrier), User);

        var otherDb = LogisticsTestDb.OpenAs(dbName, Guid.NewGuid());
        var other = new CarrierServiceRepository(otherDb);

        (await other.GetByUuidAsync(uuid)).Should().BeNull();
        (await other.GetForCarrierAsync(carrier)).Should().BeEmpty();
    }
}

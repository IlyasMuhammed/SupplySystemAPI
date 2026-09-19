using AutoMapper;
using FluentAssertions;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Repositories;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Modules.Suppliers.Data;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

// P1-07 (Addendum 29 §1.1/§1.7). Every earlier BusinessPartnerService test (P1-04) used a FakeRepo
// — a deliberate, correct choice for unit-isolating the reference-checker/validation logic, but it
// means the real Service -> Repository -> EF -> AutoMapper -> FluentValidation chain was never
// actually exercised end to end. This file is that: the real BusinessPartnerService wired to the
// real BusinessPartnerRepository, the real BusinessPartnerModelValidator, a real (in-memory) DB and
// the real AutoMapper profile — nothing faked except IUserSupplierAccessService (unrestricted) and
// ISupplierReferenceChecker (none registered, i.e. what a request with no other module's checkers
// wired up would see — the individual checker behavior itself is each owning module's own test).
file sealed class UnrestrictedAccess : IUserSupplierAccessService
{
    public Task<bool> IsRestrictedAsync() => Task.FromResult(false);
    public Task<IReadOnlySet<Guid>> GetAllowedSupplierIdsAsync() =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    public Task<bool> CanAccessSupplierAsync(Guid supplierUuid) => Task.FromResult(true);
}

file static class Stack
{
    internal static (IBusinessPartnerService service, SuppliersDbContext db) New()
    {
        var (db, _, _) = SuppliersTestDb.New();
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile<BusinessPartnerMappingProfile>()).CreateMapper();
        var repo = new BusinessPartnerRepository(db, new UnrestrictedAccess(), mapper);
        var service = new BusinessPartnerService(repo, new BusinessPartnerModelValidator(), referenceCheckers: []);
        return (service, db);
    }
}

public class BusinessPartner_EndToEnd_Tests
{
    [Fact]
    public async Task Full_lifecycle_create_read_update_delete_through_the_real_stack()
    {
        var (service, _) = Stack.New();

        // CRUD, TC-01 — create.
        var uuid = await service.CreateAsync(new BusinessPartnerModel
        {
            CompanyName = "Full Stack Vendor",
            PartnerCode = "FSV001",
            IsVendor = true
        }, createdBy: 1);
        uuid.Should().NotBeEmpty();

        // CRUD — read, and the auto-computed PartnerType survived the real save + real mapping.
        var created = await service.GetByIdAsync(uuid);
        created.Should().NotBeNull();
        created!.CompanyName.Should().Be("Full Stack Vendor");
        created.PartnerType.Should().Be("VENDOR");

        // CRUD — update, with a flag change the real stack must recompute PartnerType for.
        created.IsCustomer = true; // vendor -> both
        var updated = await service.UpdateAsync(uuid, created, modifiedBy: 2);
        updated.Should().BeTrue();

        var afterUpdate = await service.GetByIdAsync(uuid);
        afterUpdate!.PartnerType.Should().Be("BOTH");

        // CRUD — delete (soft), through the real reference-checker guard (none registered, so it
        // succeeds), and the real repository's IsDelete filter on GetByIdAsync.
        var deleted = await service.DeleteAsync(uuid, deletedBy: 3);
        deleted.Should().BeTrue();
        (await service.GetByIdAsync(uuid)).Should().BeNull();
    }

    [Fact]
    public async Task Validation_runs_for_real_and_blocks_an_invalid_flag_combination_before_any_save()
    {
        var (service, _) = Stack.New();

        var invalid = new BusinessPartnerModel
        {
            CompanyName = "Bad Combo Co",
            PartnerCode = "BAD001",
            IsCustomer = true,
            IsCarrier = true // no vendor flag — not one of the FSD's eight combinations
        };

        var act = () => service.CreateAsync(invalid, createdBy: 1);

        await act.Should().ThrowAsync<BadRequestException>();

        // And nothing was actually persisted — the validator ran before the repository did.
        var all = await service.GetAllAsync(new BusinessPartnerFilter());
        all.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Duplicate_partner_code_is_rejected_by_the_real_repository_through_the_real_service()
    {
        var (service, _) = Stack.New();
        await service.CreateAsync(
            new BusinessPartnerModel { CompanyName = "First", PartnerCode = "DUP001", IsVendor = true }, 1);

        var act = () => service.CreateAsync(
            new BusinessPartnerModel { CompanyName = "Second", PartnerCode = "DUP001", IsVendor = true }, 1);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Creating_each_of_the_eight_FSD_combinations_lands_in_the_matching_filter_view()
    {
        // TC-02/TC-03 — carrier and service-provider creation, end to end (not just the flag
        // computation PartnerCode_Tests already covers in isolation).
        var (service, _) = Stack.New();

        await service.CreateAsync(new BusinessPartnerModel
        {
            CompanyName = "Fast Freight", PartnerCode = "CAR001", IsCarrier = true
        }, 1);
        await service.CreateAsync(new BusinessPartnerModel
        {
            CompanyName = "Cleaning Co", PartnerCode = "SVC001", IsServiceProvider = true
        }, 1);

        (await service.GetCarriersAsync()).Should().ContainSingle().Which.CompanyName.Should().Be("Fast Freight");
        (await service.GetServiceProvidersAsync()).Should().ContainSingle().Which.CompanyName.Should().Be("Cleaning Co");
        (await service.GetVendorsAsync()).Should().BeEmpty("neither partner created here is a vendor");
    }

    [Fact]
    public async Task GetAllAsync_filters_and_paginates_through_the_real_stack()
    {
        var (service, _) = Stack.New();
        for (var i = 0; i < 5; i++)
            await service.CreateAsync(new BusinessPartnerModel
            {
                CompanyName = $"Vendor {i:D2}", PartnerCode = $"V{i:D3}", IsVendor = true
            }, 1);
        await service.CreateAsync(new BusinessPartnerModel
        {
            CompanyName = "Lone Customer", PartnerCode = "CUS001", IsCustomer = true
        }, 1);

        var page = await service.GetAllAsync(new BusinessPartnerFilter { IsVendor = true, PageSize = 3 });

        page.TotalRecords.Should().Be(5);
        page.Data.Should().HaveCount(3);
    }
}

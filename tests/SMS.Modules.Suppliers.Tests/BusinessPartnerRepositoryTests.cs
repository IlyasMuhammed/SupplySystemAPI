using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

// P1-04 — IBusinessPartnerRepository/BusinessPartnerRepository: auto-compute on save, the four
// capability filters, soft delete.

file sealed class UnrestrictedAccess : IUserSupplierAccessService
{
    public Task<bool> IsRestrictedAsync() => Task.FromResult(false);
    public Task<IReadOnlySet<Guid>> GetAllowedSupplierIdsAsync() =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    public Task<bool> CanAccessSupplierAsync(Guid supplierUuid) => Task.FromResult(true);
}

file sealed class RestrictedAccess : IUserSupplierAccessService
{
    private readonly HashSet<Guid> _allowed;
    public RestrictedAccess(params Guid[] allowed) => _allowed = new HashSet<Guid>(allowed);
    public Task<bool> IsRestrictedAsync() => Task.FromResult(true);
    public Task<IReadOnlySet<Guid>> GetAllowedSupplierIdsAsync() =>
        Task.FromResult<IReadOnlySet<Guid>>(_allowed);
    public Task<bool> CanAccessSupplierAsync(Guid supplierUuid) => Task.FromResult(_allowed.Contains(supplierUuid));
}

file static class Build
{
    private static AutoMapper.IMapper Mapper() =>
        new AutoMapper.MapperConfiguration(cfg => cfg.AddProfile<BusinessPartnerMappingProfile>())
            .CreateMapper();

    internal static (BusinessPartnerRepository repo, SuppliersDbContext db) New(
        IUserSupplierAccessService? access = null, Action<SuppliersDbContext>? seed = null)
    {
        var opts = new DbContextOptionsBuilder<SuppliersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new SuppliersDbContext(opts, new StaticTenantContext());
        seed?.Invoke(db);
        db.SaveChanges();
        return (new BusinessPartnerRepository(db, access ?? new UnrestrictedAccess(), Mapper()), db);
    }

    internal static BusinessPartner Vendor(SuppliersDbContext db, string name = "Test Vendor", string code = "V001")
    {
        var p = new BusinessPartner
        {
            UUID = Guid.NewGuid(), SupplierName = name, SupplierCode = code,
            PartnerType = "VENDOR", IsVendor = true,
            Status = "ACTIVE", IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        db.BusinessPartners.Add(p);
        return p;
    }

    internal static BusinessPartnerModel VendorModel(string name = "Acme Corp", string code = "ACM001") => new()
    {
        CompanyName = name, PartnerCode = code, IsVendor = true
    };
}

public class CreateAsync_Tests
{
    [Fact]
    public async Task Computes_PartnerType_from_the_flags_regardless_of_what_was_supplied()
    {
        var (repo, db) = Build.New();
        var model = Build.VendorModel();
        model.PartnerType = "GARBAGE"; // must be ignored — the flags are the source of truth

        var uuid = await repo.CreateAsync(model, createdBy: 1);

        var saved = await db.BusinessPartners.SingleAsync(p => p.UUID == uuid);
        saved.PartnerType.Should().Be("VENDOR");
    }

    [Fact]
    public async Task A_carrier_only_partner_computes_to_CARRIER()
    {
        var (repo, db) = Build.New();
        var model = Build.VendorModel();
        model.IsVendor = false;
        model.IsCarrier = true;

        var uuid = await repo.CreateAsync(model, createdBy: 1);

        (await db.BusinessPartners.SingleAsync(p => p.UUID == uuid)).PartnerType.Should().Be("CARRIER");
    }

    [Fact]
    public async Task Rejects_a_duplicate_partner_code()
    {
        var (repo, db) = Build.New(seed: ctx => Build.Vendor(ctx, code: "DUP001"));

        var act = () => repo.CreateAsync(Build.VendorModel(code: "DUP001"), createdBy: 1);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task An_undefined_flag_combination_throws_rather_than_silently_saving()
    {
        var (repo, _) = Build.New();
        var model = Build.VendorModel();
        model.IsVendor = false;
        model.IsCustomer = true;
        model.IsCarrier = true; // not one of the FSD's eight combinations

        var act = () => repo.CreateAsync(model, createdBy: 1);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task New_partners_start_active_and_not_deleted()
    {
        var (repo, db) = Build.New();

        var uuid = await repo.CreateAsync(Build.VendorModel(), createdBy: 1);

        var saved = await db.BusinessPartners.SingleAsync(p => p.UUID == uuid);
        saved.IsActive.Should().BeTrue();
        saved.IsDelete.Should().BeFalse();
    }
}

public class UpdateAsync_Tests
{
    [Fact]
    public async Task Recomputes_PartnerType_when_flags_change()
    {
        var (repo, db) = Build.New(seed: ctx => Build.Vendor(ctx, code: "UPD001"));
        var uuid = db.BusinessPartners.Single().UUID;

        var model = Build.VendorModel(code: "UPD001");
        model.IsVendor = true;
        model.IsCustomer = true; // vendor -> both

        await repo.UpdateAsync(uuid, model, modifiedBy: 2);

        var saved = await db.BusinessPartners.SingleAsync(p => p.UUID == uuid);
        saved.PartnerType.Should().Be("BOTH");
        saved.ModifiedBy.Should().Be(2);
    }

    [Fact]
    public async Task Unknown_uuid_throws_NotFound()
    {
        var (repo, _) = Build.New();

        var act = () => repo.UpdateAsync(Guid.NewGuid(), Build.VendorModel(), modifiedBy: 1);

        await act.Should().ThrowAsync<NotFoundException>();
    }
}

public class DeleteAsync_Tests
{
    [Fact]
    public async Task Soft_deletes_rather_than_removing_the_row()
    {
        var (repo, db) = Build.New(seed: ctx => Build.Vendor(ctx, code: "DEL001"));
        var uuid = db.BusinessPartners.Single().UUID;

        await repo.DeleteAsync(uuid, deletedBy: 3);

        var raw = await db.BusinessPartners.IgnoreQueryFilters().SingleAsync(p => p.UUID == uuid);
        raw.IsDelete.Should().BeTrue();
        (await repo.GetByIdAsync(uuid)).Should().BeNull("a deleted partner is not a gettable one");
    }
}

public class FilterMethods_Tests
{
    [Fact]
    public async Task GetVendorsAsync_returns_only_vendors()
    {
        var (repo, db) = Build.New(seed: ctx =>
        {
            Build.Vendor(ctx, "V", "V001");
            var customer = Build.Vendor(ctx, "C", "C001");
            customer.IsVendor = false; customer.IsCustomer = true; customer.PartnerType = "CUSTOMER";
        });

        var vendors = await repo.GetVendorsAsync();

        vendors.Should().ContainSingle().Which.CompanyName.Should().Be("V");
    }

    [Fact]
    public async Task GetCarriersAsync_returns_only_carriers()
    {
        var (repo, db) = Build.New(seed: ctx =>
        {
            var carrier = Build.Vendor(ctx, "Fast Freight", "CAR001");
            carrier.IsVendor = false; carrier.IsCarrier = true; carrier.PartnerType = "CARRIER";
            Build.Vendor(ctx, "Plain Vendor", "V002");
        });

        var carriers = await repo.GetCarriersAsync();

        carriers.Should().ContainSingle().Which.CompanyName.Should().Be("Fast Freight");
    }

    [Fact]
    public async Task Soft_deleted_partners_are_excluded_from_every_filter()
    {
        var (repo, db) = Build.New(seed: ctx =>
        {
            var deleted = Build.Vendor(ctx, "Gone", "GONE01");
            deleted.IsDelete = true;
        });

        (await repo.GetVendorsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_restricted_caller_only_sees_their_allowed_partners()
    {
        Guid allowedId = default;
        var (_, seedDb) = Build.New(seed: ctx =>
        {
            var allowed = Build.Vendor(ctx, "Allowed", "ALW001");
            Build.Vendor(ctx, "Blocked", "BLK001");
            allowedId = allowed.UUID;
        });

        var restrictedRepo = new BusinessPartnerRepository(
            seedDb, new RestrictedAccess(allowedId),
            new AutoMapper.MapperConfiguration(cfg => cfg.AddProfile<BusinessPartnerMappingProfile>()).CreateMapper());

        var vendors = await restrictedRepo.GetVendorsAsync();

        vendors.Should().ContainSingle().Which.CompanyName.Should().Be("Allowed");
    }
}

// P1-05 — GetAllAsync, the general filtered/paginated list GET /api/partners itself reads from.
public class GetAllAsync_Tests
{
    [Fact]
    public async Task FilterByType_matches_the_computed_PartnerType_exactly()
    {
        var (repo, _) = Build.New(seed: ctx =>
        {
            Build.Vendor(ctx, "Vendor Co", "V001");
            var carrier = Build.Vendor(ctx, "Carrier Co", "C001");
            carrier.IsVendor = false; carrier.IsCarrier = true; carrier.PartnerType = "CARRIER";
        });

        var result = await repo.GetAllAsync(new BusinessPartnerFilter { Type = "CARRIER" });

        result.Data.Should().ContainSingle().Which.CompanyName.Should().Be("Carrier Co");
    }

    [Fact]
    public async Task FilterByIsVendor_true_excludes_non_vendors()
    {
        var (repo, _) = Build.New(seed: ctx =>
        {
            Build.Vendor(ctx, "Vendor Co", "V001");
            var customer = Build.Vendor(ctx, "Customer Co", "CU001");
            customer.IsVendor = false; customer.IsCustomer = true; customer.PartnerType = "CUSTOMER";
        });

        var result = await repo.GetAllAsync(new BusinessPartnerFilter { IsVendor = true });

        result.Data.Should().ContainSingle().Which.CompanyName.Should().Be("Vendor Co");
    }

    [Fact]
    public async Task FilterByActive_excludes_inactive_partners()
    {
        var (repo, _) = Build.New(seed: ctx =>
        {
            Build.Vendor(ctx, "Active Co", "ACT01");
            var inactive = Build.Vendor(ctx, "Inactive Co", "INA01");
            inactive.IsActive = false;
        });

        var result = await repo.GetAllAsync(new BusinessPartnerFilter { Active = true });

        result.Data.Should().ContainSingle().Which.CompanyName.Should().Be("Active Co");
    }

    [Fact]
    public async Task Search_matches_name_or_code_case_insensitively()
    {
        var (repo, _) = Build.New(seed: ctx =>
        {
            Build.Vendor(ctx, "MedPlus Pharma", "MED001");
            Build.Vendor(ctx, "Acme Corp", "ACM001");
        });

        var result = await repo.GetAllAsync(new BusinessPartnerFilter { Search = "medplus" });

        result.Data.Should().ContainSingle().Which.CompanyName.Should().Be("MedPlus Pharma");
    }

    [Fact]
    public async Task No_filters_returns_everything_not_deleted_paginated()
    {
        var (repo, _) = Build.New(seed: ctx =>
        {
            for (var i = 0; i < 25; i++) Build.Vendor(ctx, $"Vendor {i:D2}", $"V{i:D3}");
        });

        var page1 = await repo.GetAllAsync(new BusinessPartnerFilter { Page = 1, PageSize = 20 });

        page1.Data.Should().HaveCount(20);
        page1.TotalRecords.Should().Be(25);
        page1.TotalPages.Should().Be(2);
    }

    [Fact]
    public async Task Deleted_partners_never_appear_regardless_of_filters()
    {
        var (repo, _) = Build.New(seed: ctx =>
        {
            var deleted = Build.Vendor(ctx, "Gone", "GONE01");
            deleted.IsDelete = true;
        });

        var result = await repo.GetAllAsync(new BusinessPartnerFilter());

        result.Data.Should().BeEmpty();
        result.TotalRecords.Should().Be(0);
    }
}

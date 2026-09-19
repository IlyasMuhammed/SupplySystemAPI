using FluentAssertions;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Repositories;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

// P1-04 — BusinessPartnerService: the reference-checker delete guard (same rule
// SuppliersService.DeleteSupplierAsync already enforces for the legacy path) and validation
// wired in front of create/update.

file sealed class FakeRepo : IBusinessPartnerRepository
{
    public BusinessPartnerModel? Created;
    public bool DeleteCalled;

    public Task<Guid> CreateAsync(BusinessPartnerModel model, int createdBy)
    {
        Created = model;
        return Task.FromResult(Guid.NewGuid());
    }

    public Task<BusinessPartnerModel?> GetByIdAsync(Guid uuid) => Task.FromResult<BusinessPartnerModel?>(null);
    public Task<bool> UpdateAsync(Guid uuid, BusinessPartnerModel model, int modifiedBy) => Task.FromResult(true);

    public Task<bool> DeleteAsync(Guid uuid, int deletedBy)
    {
        DeleteCalled = true;
        return Task.FromResult(true);
    }

    public Task<PaginatedResponse<BusinessPartnerModel>> GetAllAsync(BusinessPartnerFilter filter) =>
        Task.FromResult(new PaginatedResponse<BusinessPartnerModel>());

    public Task<List<BusinessPartnerModel>> GetVendorsAsync() => Task.FromResult(new List<BusinessPartnerModel>());
    public Task<List<BusinessPartnerModel>> GetCustomersAsync() => Task.FromResult(new List<BusinessPartnerModel>());
    public Task<List<BusinessPartnerModel>> GetCarriersAsync() => Task.FromResult(new List<BusinessPartnerModel>());
    public Task<List<BusinessPartnerModel>> GetServiceProvidersAsync() => Task.FromResult(new List<BusinessPartnerModel>());
}

file sealed class AlwaysReferenced : ISupplierReferenceChecker
{
    public Task<bool> IsSupplierReferencedAsync(Guid supplierId) => Task.FromResult(true);
}

file sealed class NeverReferenced : ISupplierReferenceChecker
{
    public Task<bool> IsSupplierReferencedAsync(Guid supplierId) => Task.FromResult(false);
}

public class BusinessPartnerService_Delete_Tests
{
    [Fact]
    public async Task Refuses_to_delete_a_partner_referenced_by_another_document()
    {
        var repo = new FakeRepo();
        var svc = new BusinessPartnerService(repo, new BusinessPartnerModelValidator(), [new AlwaysReferenced()]);

        var act = () => svc.DeleteAsync(Guid.NewGuid(), deletedBy: 1);

        await act.Should().ThrowAsync<UnprocessableEntityException>();
        repo.DeleteCalled.Should().BeFalse("the repository must never be reached once a reference is found");
    }

    [Fact]
    public async Task Deletes_when_nothing_references_the_partner()
    {
        var repo = new FakeRepo();
        var svc = new BusinessPartnerService(repo, new BusinessPartnerModelValidator(), [new NeverReferenced()]);

        await svc.DeleteAsync(Guid.NewGuid(), deletedBy: 1);

        repo.DeleteCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Checks_every_registered_checker_not_just_the_first()
    {
        var repo = new FakeRepo();
        var svc = new BusinessPartnerService(
            repo, new BusinessPartnerModelValidator(), [new NeverReferenced(), new AlwaysReferenced()]);

        var act = () => svc.DeleteAsync(Guid.NewGuid(), deletedBy: 1);

        await act.Should().ThrowAsync<UnprocessableEntityException>();
    }
}

public class BusinessPartnerService_Validation_Tests
{
    [Fact]
    public async Task CreateAsync_rejects_an_invalid_model_before_touching_the_repository()
    {
        var repo = new FakeRepo();
        var svc = new BusinessPartnerService(repo, new BusinessPartnerModelValidator(), []);

        var invalid = new BusinessPartnerModel { CompanyName = "", PartnerCode = "" };

        var act = () => svc.CreateAsync(invalid, createdBy: 1);

        await act.Should().ThrowAsync<BadRequestException>();
        repo.Created.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_passes_a_valid_model_through()
    {
        var repo = new FakeRepo();
        var svc = new BusinessPartnerService(repo, new BusinessPartnerModelValidator(), []);

        var valid = new BusinessPartnerModel { CompanyName = "Acme", PartnerCode = "ACM001", IsVendor = true };
        await svc.CreateAsync(valid, createdBy: 1);

        repo.Created.Should().NotBeNull();
        repo.Created!.CompanyName.Should().Be("Acme");
    }
}

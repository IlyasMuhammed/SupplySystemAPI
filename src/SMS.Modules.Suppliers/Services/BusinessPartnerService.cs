using FluentValidation;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Suppliers.Services;

internal sealed class BusinessPartnerService : IBusinessPartnerService
{
    private readonly IBusinessPartnerRepository _repo;
    private readonly BusinessPartnerModelValidator _validator;
    private readonly IEnumerable<ISupplierReferenceChecker> _referenceCheckers;

    public BusinessPartnerService(
        IBusinessPartnerRepository repo,
        BusinessPartnerModelValidator validator,
        IEnumerable<ISupplierReferenceChecker> referenceCheckers)
    {
        _repo = repo;
        _validator = validator;
        _referenceCheckers = referenceCheckers;
    }

    public async Task<Guid> CreateAsync(BusinessPartnerModel model, int createdBy)
    {
        await ValidateAsync(model);
        return await _repo.CreateAsync(model, createdBy);
    }

    public Task<BusinessPartnerModel?> GetByIdAsync(Guid uuid) => _repo.GetByIdAsync(uuid);

    public async Task<bool> UpdateAsync(Guid uuid, BusinessPartnerModel model, int modifiedBy)
    {
        await ValidateAsync(model);
        return await _repo.UpdateAsync(uuid, model, modifiedBy);
    }

    public async Task<bool> DeleteAsync(Guid uuid, int deletedBy)
    {
        // Same check SuppliersService.DeleteSupplierAsync already makes before a legacy vendor
        // delete — a BusinessPartner row is the same row a PO, GRN, invoice or payment may
        // reference, whichever surface (old Supplier API or this one) the delete comes through.
        foreach (var checker in _referenceCheckers)
        {
            if (await checker.IsSupplierReferencedAsync(uuid))
                throw new UnprocessableEntityException(
                    "Cannot delete this business partner — it is referenced by an existing purchase order, RFQ response, invoice, payment, GRN, or return order.");
        }

        return await _repo.DeleteAsync(uuid, deletedBy);
    }

    public Task<PaginatedResponse<BusinessPartnerModel>> GetAllAsync(BusinessPartnerFilter filter) =>
        _repo.GetAllAsync(filter);

    public Task<List<BusinessPartnerModel>> GetVendorsAsync() => _repo.GetVendorsAsync();
    public Task<List<BusinessPartnerModel>> GetCustomersAsync() => _repo.GetCustomersAsync();
    public Task<List<BusinessPartnerModel>> GetCarriersAsync() => _repo.GetCarriersAsync();
    public Task<List<BusinessPartnerModel>> GetServiceProvidersAsync() => _repo.GetServiceProvidersAsync();

    private async Task ValidateAsync(BusinessPartnerModel model)
    {
        var result = await _validator.ValidateAsync(model);
        if (!result.IsValid) throw new BadRequestException(result.ToString());
    }
}

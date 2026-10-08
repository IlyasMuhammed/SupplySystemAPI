using FluentValidation;
using SMS.Modules.Suppliers.Integration;
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
    private readonly PartnerQuickBooksPublisher? _quickBooks;
    // A35 BR-C4-01 — default currencies must be active currencies of the organization.
    private readonly PartnerCurrencyRules _currencyRules;

    /// <param name="quickBooks">
    /// Tells the QuickBooks gateway about a partner after each save. Optional so a caller that builds this
    /// service by hand needs nothing extra; the module registers it.
    /// </param>
    public BusinessPartnerService(
        IBusinessPartnerRepository repo,
        BusinessPartnerModelValidator validator,
        IEnumerable<ISupplierReferenceChecker> referenceCheckers,
        PartnerQuickBooksPublisher? quickBooks = null,
        PartnerCurrencyRules? currencyRules = null)
    {
        _currencyRules = currencyRules ?? new PartnerCurrencyRules();
        _repo = repo;
        _validator = validator;
        _referenceCheckers = referenceCheckers;
        _quickBooks = quickBooks;
    }

    /// <summary>After the repository's own save has committed. Never throws (see the publisher).</summary>
    private Task PublishToQuickBooksAsync(Guid uuid) =>
        _quickBooks is null ? Task.CompletedTask : _quickBooks.PublishAsync(uuid);

    public async Task<Guid> CreateAsync(BusinessPartnerModel model, int createdBy)
    {
        await ValidateAsync(model);
        await _currencyRules.ValidateAsync(
            model.DefaultSaleCurrencyId, model.ClearDefaultSaleCurrency,
            PartnerCurrencyRules.Purchase(model.DefaultPurchaseCurrencyId, model.PreferredCurrency), model.ClearDefaultPurchaseCurrency);
        var uuid = await _repo.CreateAsync(model, createdBy);
        await PublishToQuickBooksAsync(uuid);
        return uuid;
    }

    public async Task<BusinessPartnerModel?> GetByIdAsync(Guid uuid)
    {
        var model = await _repo.GetByIdAsync(uuid);
        await _currencyRules.FillCodesAsync(model);
        return model;
    }

    public async Task<bool> UpdateAsync(Guid uuid, BusinessPartnerModel model, int modifiedBy)
    {
        await ValidateAsync(model);
        var current = await _repo.GetByIdAsync(uuid);
        await _currencyRules.ValidateAsync(
            model.DefaultSaleCurrencyId, model.ClearDefaultSaleCurrency,
            PartnerCurrencyRules.Purchase(model.DefaultPurchaseCurrencyId, model.PreferredCurrency), model.ClearDefaultPurchaseCurrency,
            current?.DefaultSaleCurrencyId, current?.DefaultPurchaseCurrencyId);
        var updated = await _repo.UpdateAsync(uuid, model, modifiedBy);
        if (updated) await PublishToQuickBooksAsync(uuid);
        return updated;
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

        var deleted = await _repo.DeleteAsync(uuid, deletedBy);
        // Sent once more, as inactive, so a record QuickBooks already has is retired.
        if (deleted) await PublishToQuickBooksAsync(uuid);
        return deleted;
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

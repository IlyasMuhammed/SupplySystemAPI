using SMS.Modules.Suppliers.Integration;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Suppliers.Services;

internal sealed class SuppliersService : ISuppliersService
{
    private readonly ISuppliersRepository _repo;
    private readonly ISupplierEventPublisher _events;
    private readonly IPhoneNumberValidationService _phoneValidator;
    private readonly IEnumerable<ISupplierReferenceChecker> _referenceCheckers;
    private readonly PartnerQuickBooksPublisher? _quickBooks;
    // A35 BR-C4-01 — default currencies must be active currencies of the organization.
    private readonly PartnerCurrencyRules _currencyRules;

    /// <param name="quickBooks">
    /// Tells the QuickBooks gateway about a partner after each save that changes what it would send.
    /// Optional so a caller that builds this service by hand needs nothing extra; the module registers it.
    /// </param>
    public SuppliersService(
        ISuppliersRepository repo,
        ISupplierEventPublisher events,
        IPhoneNumberValidationService phoneValidator,
        IEnumerable<ISupplierReferenceChecker> referenceCheckers,
        PartnerQuickBooksPublisher? quickBooks = null,
        PartnerCurrencyRules? currencyRules = null)
    {
        _currencyRules = currencyRules ?? new PartnerCurrencyRules();
        _repo = repo;
        _events = events;
        _phoneValidator = phoneValidator;
        _referenceCheckers = referenceCheckers;
        _quickBooks = quickBooks;
    }

    /// <summary>After the repository's own save has committed. Never throws (see the publisher).</summary>
    private Task PublishToQuickBooksAsync(Guid uuid) =>
        _quickBooks is null ? Task.CompletedTask : _quickBooks.PublishAsync(uuid);

    public List<SupplierDropDownEntity> GetSupplierTypes() => _repo.GetSupplierTypes();
    public List<SupplierDropDownEntity> GetCategories() => _repo.GetCategories();
    public Guid CreateSupplierType(CreateSupplierTypeRequest req) => _repo.CreateSupplierType(req);
    public Guid CreateCategory(CreateCategoryRequest req) => _repo.CreateCategory(req);
    public bool UpdateSupplierType(Guid id, CreateSupplierTypeRequest req) => _repo.UpdateSupplierType(id, req);
    public bool UpdateCategory(Guid id, CreateCategoryRequest req) => _repo.UpdateCategory(id, req);
    public bool DeleteSupplierType(Guid id) => _repo.DeleteSupplierType(id);
    public bool DeleteSupplierCategory(Guid id) => _repo.DeleteSupplierCategory(id);

    public async Task<Guid> CreateSupplierAsync(CreateSupplierRequest req, int createdBy)
    {
        await _currencyRules.ValidateAsync(
            req.DefaultSaleCurrencyId, false, PartnerCurrencyRules.Purchase(req.DefaultPurchaseCurrencyId, req.PreferredCurrency), false);
        var uuid = await _repo.CreateSupplierAsync(req, createdBy);
        await PublishToQuickBooksAsync(uuid);
        return uuid;
    }

    public Task<PaginatedResponse<SupplierListItemModel>> GetSuppliersAsync(SupplierListFilter filter) =>
        _repo.GetSuppliersAsync(filter);

    public async Task<SupplierDetailModel?> GetSupplierByIdAsync(Guid uuid)
    {
        var detail = await _repo.GetSupplierByIdAsync(uuid);
        await _currencyRules.FillCodesAsync(detail);
        return detail;
    }

    public async Task<bool> PatchSupplierAsync(Guid uuid, PatchSupplierRequest req, int modifiedBy)
    {
        var current = await _repo.GetSupplierByIdAsync(uuid);
        await _currencyRules.ValidateAsync(
            req.DefaultSaleCurrencyId, req.ClearDefaultSaleCurrency,
            PartnerCurrencyRules.Purchase(req.DefaultPurchaseCurrencyId, req.PreferredCurrency), req.ClearDefaultPurchaseCurrency,
            current?.DefaultSaleCurrencyId, current?.DefaultPurchaseCurrencyId);
        var updated = await _repo.PatchSupplierAsync(uuid, req, modifiedBy);
        if (updated) await PublishToQuickBooksAsync(uuid);
        return updated;
    }

    public Task<int> AddContactAsync(Guid uuid, AddContactRequest req) =>
        _repo.AddContactAsync(uuid, req);

    public Task<bool> UpdateContactAsync(Guid supplierUuid, int contactId, PatchContactRequest req) =>
        _repo.UpdateContactAsync(supplierUuid, contactId, req);

    public async Task<bool> DeleteSupplierAsync(Guid uuid, int deletedBy)
    {
        foreach (var checker in _referenceCheckers)
        {
            if (await checker.IsSupplierReferencedAsync(uuid))
                throw new UnprocessableEntityException(
                    "Cannot delete this supplier — it is referenced by an existing purchase order, RFQ response, invoice, payment, GRN, or return order. Reject or blacklist it instead if it should no longer be used.");
        }

        var deleted = await _repo.DeleteSupplierAsync(uuid, deletedBy);
        // A deleted partner is sent once more, as inactive, so a record QuickBooks already has is retired.
        if (deleted) await PublishToQuickBooksAsync(uuid);
        return deleted;
    }

    public async Task<(bool success, Guid uuid, int id)> ApproveSupplierAsync(Guid uuid, int approvedBy)
    {
        var result = await _repo.ApproveSupplierAsync(uuid, approvedBy);
        if (result.success)
        {
            await _events.PublishSupplierApprovedAsync(result.uuid, result.id);
            await PublishToQuickBooksAsync(result.uuid);
        }
        return result;
    }

    public async Task<(bool success, Guid uuid, int id)> RejectSupplierAsync(Guid uuid, string reason, int changedBy)
    {
        var result = await _repo.RejectSupplierAsync(uuid, reason, changedBy);
        if (result.success)
        {
            await _events.PublishSupplierRejectedAsync(result.uuid, result.id, reason);
            await PublishToQuickBooksAsync(result.uuid);
        }
        return result;
    }

    public async Task BlacklistSupplierAsync(Guid uuid, string reason, int changedBy)
    {
        await _repo.BlacklistSupplierAsync(uuid, reason, changedBy);
        await PublishToQuickBooksAsync(uuid);
    }

    public async Task SuspendSupplierAsync(Guid uuid, string reason, DateTime? reviewDate, int changedBy)
    {
        await _repo.SuspendSupplierAsync(uuid, reason, reviewDate, changedBy);
        await PublishToQuickBooksAsync(uuid);
    }

    public Task UpsertBankDetailAsync(Guid uuid, UpsertBankDetailRequest req, int userId) =>
        _repo.UpsertBankDetailAsync(uuid, req, userId);

    public Task<BankDetailModel?> GetBankDetailAsync(Guid uuid) =>
        _repo.GetBankDetailAsync(uuid);

    public Task<int> AttachDocumentAsync(Guid uuid, AttachDocumentRequest req, int userId) =>
        _repo.AttachDocumentAsync(uuid, req, userId);

    public Task<List<DocumentModel>> GetDocumentsAsync(Guid uuid) =>
        _repo.GetDocumentsAsync(uuid);

    public Task<bool> SoftDeleteDocumentAsync(Guid uuid, int docId) =>
        _repo.SoftDeleteDocumentAsync(uuid, docId);

    public async Task<EligibleContactsResponse?> GetEligibleContactsAsync(Guid supplierUuid)
    {
        var raw = await _repo.GetContactsForEligibilityAsync(supplierUuid);
        if (raw is null) return null;

        var contacts = raw.Contacts.Select(c =>
        {
            var validation = _phoneValidator.NormaliseAndValidate(c.Phone, raw.CountryCode);
            return new EligibleContactModel
            {
                Id               = c.Id,
                ContactName      = c.ContactName,
                Title            = c.Title,
                Phone            = c.Phone,
                Email            = c.Email,
                IsPrimary        = c.IsPrimary,
                IsMobileValid    = validation.IsValid,
                NormalisedMobile = validation.NormalisedNumber,
                SortOrder        = c.IsPrimary ? 1 : 2
            };
        })
        .OrderBy(c => c.SortOrder)
        .ThenBy(c => c.ContactName)
        .ToList();

        return new EligibleContactsResponse
        {
            HasUsableContact = contacts.Any(c => c.IsMobileValid),
            Contacts         = contacts
        };
    }
}

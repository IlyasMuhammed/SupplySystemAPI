using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Suppliers.Repositories;

internal sealed class SuppliersRepository : ISuppliersRepository
{
    private readonly SuppliersDbContext _db;
    private readonly IEncryptionService _enc;
    private readonly IUserSupplierAccessService _supplierAccess;

    private static readonly Dictionary<string, string[]> AllowedTransitions = new()
    {
        ["PENDING"]   = ["ACTIVE", "REJECTED"],
        ["ACTIVE"]    = ["INACTIVE", "BLACKLISTED", "SUSPENDED"],
        ["INACTIVE"]  = ["ACTIVE"],
        ["SUSPENDED"] = ["ACTIVE", "BLACKLISTED"]
    };

    public SuppliersRepository(SuppliersDbContext db, IEncryptionService enc, IUserSupplierAccessService supplierAccess)
    {
        _db = db;
        _enc = enc;
        _supplierAccess = supplierAccess;
    }

    // ── Legacy dropdown methods ──────────────────────────────────────────────

    public List<SupplierDropDownEntity> GetSupplierTypes() =>
        _db.SupplierTypes.Where(x => x.IsActive)
            .Select(x => new SupplierDropDownEntity { Id = x.Id, Name = x.Name }).ToList();

    public List<SupplierDropDownEntity> GetCategories() =>
        _db.SupplierCategories
            .Select(x => new SupplierDropDownEntity { Id = x.Id, Name = x.Name }).ToList();

    public Guid CreateSupplierType(CreateSupplierTypeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new BadRequestException("Supplier type name cannot be empty.");
        var nameNorm = req.Name.Trim();
        if (_db.SupplierTypes.Any(x => x.Name.ToLower() == nameNorm.ToLower()))
            throw new ConflictException($"A supplier type named '{nameNorm}' already exists.");
        var e = new SupplierType { Id = Guid.NewGuid(), Name = nameNorm, Description = req.Description, IsActive = true };
        _db.SupplierTypes.Add(e); _db.SaveChanges(); return e.Id;
    }

    public Guid CreateCategory(CreateCategoryRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new BadRequestException("Category name cannot be empty.");
        var nameNorm = req.Name.Trim();
        if (_db.SupplierCategories.Any(x => x.Name.ToLower() == nameNorm.ToLower()))
            throw new ConflictException($"A category named '{nameNorm}' already exists.");
        var e = new SupplierCategory { Id = Guid.NewGuid(), Name = nameNorm };
        _db.SupplierCategories.Add(e); _db.SaveChanges(); return e.Id;
    }

    public bool UpdateSupplierType(Guid id, CreateSupplierTypeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new BadRequestException("Supplier type name cannot be empty.");
        var nameNorm = req.Name.Trim();
        if (_db.SupplierTypes.Any(x => x.Id != id && x.Name.ToLower() == nameNorm.ToLower()))
            throw new ConflictException($"A supplier type named '{nameNorm}' already exists.");
        var e = _db.SupplierTypes.FirstOrDefault(x => x.Id == id);
        if (e == null) return false;
        e.Name = nameNorm; e.Description = req.Description;
        _db.SaveChanges(); return true;
    }

    public bool UpdateCategory(Guid id, CreateCategoryRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new BadRequestException("Category name cannot be empty.");
        var nameNorm = req.Name.Trim();
        if (_db.SupplierCategories.Any(x => x.Id != id && x.Name.ToLower() == nameNorm.ToLower()))
            throw new ConflictException($"A category named '{nameNorm}' already exists.");
        var e = _db.SupplierCategories.FirstOrDefault(x => x.Id == id);
        if (e == null) return false;
        e.Name = nameNorm; _db.SaveChanges(); return true;
    }

    public bool DeleteSupplierType(Guid id)
    {
        var e = _db.SupplierTypes.FirstOrDefault(x => x.Id == id);
        if (e == null) return false;
        _db.SupplierTypes.Remove(e); _db.SaveChanges(); return true;
    }

    public bool DeleteSupplierCategory(Guid id)
    {
        var e = _db.SupplierCategories.FirstOrDefault(x => x.Id == id);
        if (e == null) return false;
        _db.SupplierCategories.Remove(e); _db.SaveChanges(); return true;
    }

    // ── Supplier CRUD ────────────────────────────────────────────────────────

    public async Task<Guid> CreateSupplierAsync(CreateSupplierRequest req, int createdBy)
    {
        if (await _db.BusinessPartners.AnyAsync(s => s.SupplierCode == req.SupplierCode && !s.IsDelete))
            throw new ConflictException($"A supplier with code '{req.SupplierCode}' already exists.");

        var uuid = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var supplier = new BusinessPartner
        {
            UUID = uuid,
            SupplierName = req.SupplierName,
            SupplierCode = req.SupplierCode,
            RegistrationNo = req.RegistrationNo,
            TaxId = req.TaxId,
            Country = req.Country,
            ProvinceState = req.ProvinceState,
            City = req.City,
            AddressLine1 = req.AddressLine1,
            AddressLine2 = req.AddressLine2,
            PostalCode = req.PostalCode,
            Phone = req.Phone,
            Fax = req.Fax,
            Email = req.Email,
            Website = req.Website,
            PrimaryContactName = req.PrimaryContactName,
            PrimaryContactTitle = req.PrimaryContactTitle,
            PrimaryContactPhone = req.PrimaryContactPhone,
            PrimaryContactEmail = req.PrimaryContactEmail,
            PreferredPaymentTerms = req.PreferredPaymentTerms,
            PreferredCurrency = req.PreferredCurrency,
            CreditLimit = req.CreditLimit,
            LeadTimeDays = req.LeadTimeDays,
            Notes = req.Notes,
            IsPreferredSupplier = req.IsPreferredSupplier,
            Status = "PENDING",
            IsActive = true,
            IsDelete = false,
            CreatedBy = createdBy,
            CreatedDate = now
        };

        _db.BusinessPartners.Add(supplier);
        await _db.SaveChangesAsync();

        foreach (var t in req.SupplierTypeIds.DistinctBy(x => x.LookupValueId))
            _db.SupplierTypeMappings.Add(new SupplierTypeMapping
            {
                SupplierId = supplier.Id, LookupValueId = t.LookupValueId,
                IsPrimary = t.IsPrimary, AssignedBy = createdBy, AssignedAt = now, Notes = t.Notes
            });

        foreach (var i in req.IndustryIds.DistinctBy(x => x.LookupValueId))
            _db.SupplierIndustryMappings.Add(new SupplierIndustryMapping
            {
                SupplierId = supplier.Id, LookupValueId = i.LookupValueId,
                IsPrimary = i.IsPrimary, AssignedBy = createdBy, AssignedAt = now, Notes = i.Notes
            });

        if (req.SupplierTypeIds.Any() || req.IndustryIds.Any())
            await _db.SaveChangesAsync();

        return uuid;
    }

    public async Task<PaginatedResponse<SupplierListItemModel>> GetSuppliersAsync(SupplierListFilter filter)
    {
        var query = _db.BusinessPartners
            .Include(s => s.TypeMappings)
            .Include(s => s.IndustryMappings)
            // P1-05 (Addendum 29 §1.7) — /api/suppliers must keep returning only is_vendor=1 rows.
            // Before P1-02 every row in this table WAS a vendor implicitly; now that
            // IBusinessPartnerRepository.CreateAsync (P1-04) can create pure customers, carriers
            // and service providers in the same table, this filter is what keeps that true rather
            // than it being true by accident of no other row shape existing yet.
            .Where(s => !s.IsDelete && s.IsVendor)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(s => s.Status == filter.Status);

        if (!string.IsNullOrWhiteSpace(filter.Country))
            query = query.Where(s => s.Country == filter.Country);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(s => s.SupplierName.ToLower().Contains(term)
                || s.SupplierCode.ToLower().Contains(term));
        }

        if (filter.SupplierType.HasValue)
            query = query.Where(s => s.TypeMappings.Any(m => m.LookupValueId == filter.SupplierType.Value));

        if (filter.IndustryCategory.HasValue)
            query = query.Where(s => s.IndustryMappings.Any(m => m.LookupValueId == filter.IndustryCategory.Value));

        // REQ-2.x — narrows to the caller's mapped suppliers only when they're actually restricted
        // (see IUserSupplierAccessService: internal users, admins, and unmapped external users all
        // pass through unfiltered). Applied here as an explicit .Where, not a second EF query
        // filter, since the mapping table lives in AuthDbContext — a different DbContext than
        // Supplier's own, which already carries the tenant-scoping query filter.
        if (await _supplierAccess.IsRestrictedAsync())
        {
            var allowedIds = await _supplierAccess.GetAllowedSupplierIdsAsync();
            query = query.Where(s => allowedIds.Contains(s.UUID));
        }

        var totalRecords = await query.CountAsync();
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderBy(s => s.SupplierName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SupplierListItemModel
            {
                Id = s.Id,
                UUID = s.UUID,
                SupplierName = s.SupplierName,
                SupplierCode = s.SupplierCode,
                Status = s.Status,
                Country = s.Country,
                Email = s.Email,
                Phone = s.Phone,
                IsActive = s.IsActive,
                SupplierTypeIds = s.TypeMappings.Select(m => m.LookupValueId).ToList(),
                IndustryIds = s.IndustryMappings.Select(m => m.LookupValueId).ToList()
            })
            .ToListAsync();

        return new PaginatedResponse<SupplierListItemModel>
        {
            Data = items,
            TotalRecords = totalRecords,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling(totalRecords / (double)pageSize)
        };
    }

    public async Task<SupplierDetailModel?> GetSupplierByIdAsync(Guid uuid)
    {
        var s = await _db.BusinessPartners
            .Include(x => x.TypeMappings)
            .Include(x => x.IndustryMappings)
            .Include(x => x.Contacts)
            .FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete);

        if (s == null) return null;

        return new SupplierDetailModel
        {
            Id = s.Id, UUID = s.UUID,
            SupplierName = s.SupplierName, SupplierCode = s.SupplierCode,
            RegistrationNo = s.RegistrationNo, TaxId = s.TaxId,
            Country = s.Country, ProvinceState = s.ProvinceState, City = s.City,
            AddressLine1 = s.AddressLine1, AddressLine2 = s.AddressLine2, PostalCode = s.PostalCode,
            Phone = s.Phone, Fax = s.Fax, Email = s.Email, Website = s.Website,
            PrimaryContactName = s.PrimaryContactName, PrimaryContactTitle = s.PrimaryContactTitle,
            PrimaryContactPhone = s.PrimaryContactPhone, PrimaryContactEmail = s.PrimaryContactEmail,
            PreferredPaymentTerms = s.PreferredPaymentTerms, PreferredCurrency = s.PreferredCurrency,
            CreditLimit = s.CreditLimit, LeadTimeDays = s.LeadTimeDays, Rating = s.Rating,
            Status = s.Status, OnboardingDate = s.OnboardingDate, LastReviewDate = s.LastReviewDate,
            Notes = s.Notes, IsPreferredSupplier = s.IsPreferredSupplier,
            IsActive = s.IsActive, CreatedDate = s.CreatedDate,
            SupplierTypes = s.TypeMappings.Select(m => new SupplierTypeMappingModel
            {
                LookupValueId = m.LookupValueId, IsPrimary = m.IsPrimary,
                Notes = m.Notes, AssignedAt = m.AssignedAt
            }).ToList(),
            Industries = s.IndustryMappings.Select(m => new SupplierTypeMappingModel
            {
                LookupValueId = m.LookupValueId, IsPrimary = m.IsPrimary,
                Notes = m.Notes, AssignedAt = m.AssignedAt
            }).ToList(),
            Contacts = s.Contacts.Where(c => c.IsActive).Select(c => new ContactModel
            {
                Id = c.Id, ContactName = c.ContactName, Title = c.Title,
                Phone = c.Phone, Email = c.Email, IsPrimary = c.IsPrimary, IsActive = c.IsActive
            }).ToList()
        };
    }

    public async Task<bool> PatchSupplierAsync(Guid uuid, PatchSupplierRequest req, int modifiedBy)
    {
        var s = await _db.BusinessPartners
            .Include(x => x.TypeMappings)
            .Include(x => x.IndustryMappings)
            .FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete);

        if (s == null) return false;

        if (req.SupplierName is not null) s.SupplierName = req.SupplierName;
        if (req.RegistrationNo is not null) s.RegistrationNo = req.RegistrationNo;
        if (req.TaxId is not null) s.TaxId = req.TaxId;
        if (req.Country is not null) s.Country = req.Country;
        if (req.ProvinceState is not null) s.ProvinceState = req.ProvinceState;
        if (req.City is not null) s.City = req.City;
        if (req.AddressLine1 is not null) s.AddressLine1 = req.AddressLine1;
        if (req.AddressLine2 is not null) s.AddressLine2 = req.AddressLine2;
        if (req.PostalCode is not null) s.PostalCode = req.PostalCode;
        if (req.Phone is not null) s.Phone = req.Phone;
        if (req.Fax is not null) s.Fax = req.Fax;
        if (req.Email is not null) s.Email = req.Email;
        if (req.Website is not null) s.Website = req.Website;
        if (req.PrimaryContactName is not null) s.PrimaryContactName = req.PrimaryContactName;
        if (req.PrimaryContactTitle is not null) s.PrimaryContactTitle = req.PrimaryContactTitle;
        if (req.PrimaryContactPhone is not null) s.PrimaryContactPhone = req.PrimaryContactPhone;
        if (req.PrimaryContactEmail is not null) s.PrimaryContactEmail = req.PrimaryContactEmail;
        if (req.PreferredPaymentTerms.HasValue) s.PreferredPaymentTerms = req.PreferredPaymentTerms;
        if (req.PreferredCurrency.HasValue) s.PreferredCurrency = req.PreferredCurrency;
        if (req.CreditLimit.HasValue) s.CreditLimit = req.CreditLimit;
        if (req.LeadTimeDays.HasValue) s.LeadTimeDays = req.LeadTimeDays;
        if (req.Notes is not null) s.Notes = req.Notes;
        if (req.IsPreferredSupplier.HasValue) s.IsPreferredSupplier = req.IsPreferredSupplier.Value;
        if (req.IsActive.HasValue) s.IsActive = req.IsActive.Value;
        s.ModifiedBy = modifiedBy;
        s.ModifiedDate = DateTime.UtcNow;

        if (req.SupplierTypeIds is not null)
        {
            var now = DateTime.UtcNow;
            _db.SupplierTypeMappings.RemoveRange(s.TypeMappings);
            foreach (var t in req.SupplierTypeIds.DistinctBy(x => x.LookupValueId))
                _db.SupplierTypeMappings.Add(new SupplierTypeMapping
                {
                    SupplierId = s.Id, LookupValueId = t.LookupValueId,
                    IsPrimary = t.IsPrimary, AssignedBy = modifiedBy, AssignedAt = now, Notes = t.Notes
                });
        }

        if (req.IndustryIds is not null)
        {
            var now = DateTime.UtcNow;
            _db.SupplierIndustryMappings.RemoveRange(s.IndustryMappings);
            foreach (var i in req.IndustryIds.DistinctBy(x => x.LookupValueId))
                _db.SupplierIndustryMappings.Add(new SupplierIndustryMapping
                {
                    SupplierId = s.Id, LookupValueId = i.LookupValueId,
                    IsPrimary = i.IsPrimary, AssignedBy = modifiedBy, AssignedAt = now, Notes = i.Notes
                });
        }

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<int> AddContactAsync(Guid uuid, AddContactRequest req)
    {
        var s = await _db.BusinessPartners.FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete)
            ?? throw new NotFoundException("Supplier", uuid);

        var contact = new SupplierContact
        {
            SupplierId = s.Id, ContactName = req.ContactName, Title = req.Title,
            Phone = req.Phone, Email = req.Email, IsPrimary = req.IsPrimary, IsActive = true
        };
        _db.SupplierContacts.Add(contact);
        await _db.SaveChangesAsync();
        return contact.Id;
    }

    public async Task<bool> UpdateContactAsync(Guid supplierUuid, int contactId, PatchContactRequest req)
    {
        var s = await _db.BusinessPartners.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UUID == supplierUuid && !x.IsDelete);
        if (s is null) return false;

        var contact = await _db.SupplierContacts
            .FirstOrDefaultAsync(c => c.Id == contactId && c.SupplierId == s.Id && c.IsActive);
        if (contact is null) return false;

        if (req.Phone is not null) contact.Phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim();
        if (req.Email is not null) contact.Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
        if (req.Title is not null) contact.Title = string.IsNullOrWhiteSpace(req.Title) ? null : req.Title.Trim();

        await _db.SaveChangesAsync();
        return true;
    }

    // ── Status state machine ─────────────────────────────────────────────────

    private async Task<BusinessPartner> RequireSupplierAsync(Guid uuid) =>
        await _db.BusinessPartners.FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete)
            ?? throw new NotFoundException("Supplier", uuid);

    private static void AssertTransition(string current, string next)
    {
        if (!AllowedTransitions.TryGetValue(current, out var allowed) || !allowed.Contains(next))
            throw new BadRequestException($"Cannot transition supplier from '{current}' to '{next}'.");
    }

    public async Task<bool> DeleteSupplierAsync(Guid uuid, int deletedBy)
    {
        var s = await RequireSupplierAsync(uuid);
        if (s.Status != "PENDING" && s.Status != "REJECTED")
            throw new BadRequestException(
                $"Only PENDING or REJECTED suppliers can be deleted. Current status: {s.Status}.");

        s.IsDelete        = true;
        s.IsActive        = false;
        s.StatusChangedBy = deletedBy;
        s.StatusChangedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<(bool success, Guid uuid, int id)> ApproveSupplierAsync(Guid uuid, int approvedBy)
    {
        var s = await RequireSupplierAsync(uuid);
        AssertTransition(s.Status, "ACTIVE");

        s.Status = "ACTIVE";
        s.ApprovedBy = approvedBy;
        s.OnboardingDate = DateTime.UtcNow;
        s.StatusChangedBy = approvedBy;
        s.StatusChangedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (true, s.UUID, s.Id);
    }

    public async Task<(bool success, Guid uuid, int id)> RejectSupplierAsync(Guid uuid, string reason, int changedBy)
    {
        var s = await RequireSupplierAsync(uuid);
        AssertTransition(s.Status, "REJECTED");

        s.Status = "REJECTED";
        s.RejectedReason = reason;
        s.StatusChangedBy = changedBy;
        s.StatusChangedAt = DateTime.UtcNow;
        s.IsActive = false;
        await _db.SaveChangesAsync();
        return (true, s.UUID, s.Id);
    }

    public async Task BlacklistSupplierAsync(Guid uuid, string reason, int changedBy)
    {
        var s = await RequireSupplierAsync(uuid);
        AssertTransition(s.Status, "BLACKLISTED");

        s.Status = "BLACKLISTED";
        s.BlacklistedReason = reason;
        s.StatusChangedBy = changedBy;
        s.StatusChangedAt = DateTime.UtcNow;
        s.IsActive = false;
        await _db.SaveChangesAsync();
    }

    public async Task SuspendSupplierAsync(Guid uuid, string reason, DateTime? reviewDate, int changedBy)
    {
        var s = await RequireSupplierAsync(uuid);
        AssertTransition(s.Status, "SUSPENDED");

        s.Status = "SUSPENDED";
        s.SuspendedReason = reason;
        s.SuspendedReviewDate = reviewDate;
        s.StatusChangedBy = changedBy;
        s.StatusChangedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ── Bank details ─────────────────────────────────────────────────────────

    public async Task UpsertBankDetailAsync(Guid uuid, UpsertBankDetailRequest req, int userId)
    {
        var s = await _db.BusinessPartners
            .Include(x => x.BankDetail)
            .FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete)
            ?? throw new NotFoundException("Supplier", uuid);

        if (s.BankDetail == null)
        {
            s.BankDetail = new SupplierBankDetail
            {
                SupplierId = s.Id,
                BankName = req.BankName,
                BankAccountNo = req.BankAccountNo is null ? null : _enc.Encrypt(req.BankAccountNo),
                BankIban = req.BankIban is null ? null : _enc.Encrypt(req.BankIban),
                BankSwift = req.BankSwift is null ? null : _enc.Encrypt(req.BankSwift),
                CreatedBy = userId,
                CreatedAt = DateTime.UtcNow
            };
            _db.SupplierBankDetails.Add(s.BankDetail);
        }
        else
        {
            s.BankDetail.BankName = req.BankName;
            s.BankDetail.BankAccountNo = req.BankAccountNo is null ? null : _enc.Encrypt(req.BankAccountNo);
            s.BankDetail.BankIban = req.BankIban is null ? null : _enc.Encrypt(req.BankIban);
            s.BankDetail.BankSwift = req.BankSwift is null ? null : _enc.Encrypt(req.BankSwift);
            s.BankDetail.UpdatedBy = userId;
            s.BankDetail.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
    }

    public async Task<BankDetailModel?> GetBankDetailAsync(Guid uuid)
    {
        var bd = await _db.SupplierBankDetails
            .Include(x => x.Supplier)
            .FirstOrDefaultAsync(x => x.Supplier.UUID == uuid && !x.Supplier.IsDelete);

        if (bd == null) return null;

        return new BankDetailModel
        {
            BankName = bd.BankName,
            BankAccountNo = bd.BankAccountNo is null ? null : _enc.Decrypt(bd.BankAccountNo),
            BankIban = bd.BankIban is null ? null : _enc.Decrypt(bd.BankIban),
            BankSwift = bd.BankSwift is null ? null : _enc.Decrypt(bd.BankSwift),
            CreatedAt = bd.CreatedAt,
            UpdatedAt = bd.UpdatedAt
        };
    }

    // ── Documents ────────────────────────────────────────────────────────────

    public async Task<int> AttachDocumentAsync(Guid uuid, AttachDocumentRequest req, int userId)
    {
        var s = await _db.BusinessPartners.FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete)
            ?? throw new NotFoundException("Supplier", uuid);

        var doc = new SupplierDocument
        {
            SupplierId = s.Id,
            FileName = req.FileName,
            FileUrl = req.FileUrl,
            DocumentType = req.DocumentType,
            UploadedAt = DateTime.UtcNow,
            UploadedBy = userId,
            IsActive = true
        };
        _db.SupplierDocuments.Add(doc);
        await _db.SaveChangesAsync();
        return doc.Id;
    }

    public async Task<List<DocumentModel>> GetDocumentsAsync(Guid uuid)
    {
        var s = await _db.BusinessPartners.FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete)
            ?? throw new NotFoundException("Supplier", uuid);

        return await _db.SupplierDocuments
            .Where(d => d.SupplierId == s.Id && d.IsActive)
            .OrderByDescending(d => d.UploadedAt)
            .Select(d => new DocumentModel
            {
                Id = d.Id, FileName = d.FileName, FileUrl = d.FileUrl,
                DocumentType = d.DocumentType, UploadedAt = d.UploadedAt, IsActive = d.IsActive
            })
            .ToListAsync();
    }

    public async Task<bool> SoftDeleteDocumentAsync(Guid uuid, int docId)
    {
        var s = await _db.BusinessPartners.FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete)
            ?? throw new NotFoundException("Supplier", uuid);

        var doc = await _db.SupplierDocuments
            .FirstOrDefaultAsync(d => d.Id == docId && d.SupplierId == s.Id && d.IsActive);
        if (doc == null) return false;

        doc.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    // ── RFQ-001: Eligible contacts ───────────────────────────────────────────

    public async Task<SupplierContactsRaw?> GetContactsForEligibilityAsync(Guid supplierUuid)
    {
        var supplier = await _db.BusinessPartners
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UUID == supplierUuid && !s.IsDelete);

        if (supplier is null) return null;

        var contacts = await _db.SupplierContacts
            .AsNoTracking()
            .Where(c => c.SupplierId == supplier.Id && c.IsActive)
            .OrderByDescending(c => c.IsPrimary)
            .Select(c => new RawContactEntry(c.Id, c.ContactName, c.Title, c.Phone, c.Email, c.IsPrimary))
            .ToListAsync();

        // Include PrimaryContactPhone and main Phone as synthetic entries (Id=0/-1) so that
        // numbers entered in the supplier form fields are visible in the "Send to Supplier" dialog.
        if (!string.IsNullOrWhiteSpace(supplier.PrimaryContactPhone))
        {
            var name = string.IsNullOrWhiteSpace(supplier.PrimaryContactName)
                ? supplier.SupplierName
                : supplier.PrimaryContactName;
            contacts.Add(new RawContactEntry(0, name, supplier.PrimaryContactTitle, supplier.PrimaryContactPhone, supplier.PrimaryContactEmail, true));
        }

        if (!string.IsNullOrWhiteSpace(supplier.Phone)
            && supplier.Phone != supplier.PrimaryContactPhone)
        {
            contacts.Add(new RawContactEntry(-1, supplier.SupplierName, null, supplier.Phone, supplier.Email, false));
        }

        return new SupplierContactsRaw(supplier.Country, contacts);
    }
}

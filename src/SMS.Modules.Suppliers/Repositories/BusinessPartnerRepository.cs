using AutoMapper;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Suppliers.Repositories;

internal sealed class BusinessPartnerRepository : IBusinessPartnerRepository
{
    private readonly SuppliersDbContext _db;
    private readonly IUserSupplierAccessService _supplierAccess;
    private readonly IMapper _mapper;

    public BusinessPartnerRepository(SuppliersDbContext db, IUserSupplierAccessService supplierAccess, IMapper mapper)
    {
        _db = db;
        _supplierAccess = supplierAccess;
        _mapper = mapper;
    }

    public async Task<Guid> CreateAsync(BusinessPartnerModel model, int createdBy)
    {
        if (await _db.BusinessPartners.AnyAsync(p => p.SupplierCode == model.PartnerCode && !p.IsDelete))
            throw new ConflictException($"A business partner with code '{model.PartnerCode}' already exists.");

        var uuid = Guid.NewGuid();
        var partner = new BusinessPartner
        {
            UUID = uuid,
            SupplierCode = model.PartnerCode,
            SupplierName = model.CompanyName,
            // §1.2 — partner_type is derived, never trusted from the caller: whatever the flags
            // say wins, on every save, not just when they first disagree with a supplied value.
            PartnerType = PartnerCode.Of(PartnerCode.FromFlags(
                model.IsVendor, model.IsCustomer, model.IsCarrier, model.IsServiceProvider)),
            IsVendor = model.IsVendor,
            IsCustomer = model.IsCustomer,
            IsCarrier = model.IsCarrier,
            IsServiceProvider = model.IsServiceProvider,
            VehicleTypes = model.VehicleTypes,
            ServiceCategories = model.ServiceCategories,
            Status = "PENDING",
            IsActive = true,
            IsDelete = false,
            CreatedBy = createdBy,
            CreatedDate = DateTime.UtcNow
        };

        _db.BusinessPartners.Add(partner);
        await _db.SaveChangesAsync();
        return uuid;
    }

    public async Task<BusinessPartnerModel?> GetByIdAsync(Guid uuid)
    {
        var p = await _db.BusinessPartners.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete);

        return p is null ? null : _mapper.Map<BusinessPartnerModel>(p);
    }

    public async Task<bool> UpdateAsync(Guid uuid, BusinessPartnerModel model, int modifiedBy)
    {
        var p = await _db.BusinessPartners.FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete)
            ?? throw new NotFoundException("BusinessPartner", uuid);

        p.SupplierName = model.CompanyName;
        p.IsVendor = model.IsVendor;
        p.IsCustomer = model.IsCustomer;
        p.IsCarrier = model.IsCarrier;
        p.IsServiceProvider = model.IsServiceProvider;
        // Recomputed on every update, same as on create — a stale PartnerType from before an
        // edit to the flags is exactly what "auto-compute... on save" (§1.3) exists to prevent.
        p.PartnerType = PartnerCode.Of(PartnerCode.FromFlags(
            p.IsVendor, p.IsCustomer, p.IsCarrier, p.IsServiceProvider));
        p.VehicleTypes = model.VehicleTypes;
        p.ServiceCategories = model.ServiceCategories;
        p.ModifiedBy = modifiedBy;
        p.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(Guid uuid, int deletedBy)
    {
        var p = await _db.BusinessPartners.FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete)
            ?? throw new NotFoundException("BusinessPartner", uuid);

        p.IsDelete = true;
        p.ModifiedBy = deletedBy;
        p.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<PaginatedResponse<BusinessPartnerModel>> GetAllAsync(BusinessPartnerFilter filter)
    {
        var query = _db.BusinessPartners.AsNoTracking().Where(p => !p.IsDelete).AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Type))
            query = query.Where(p => p.PartnerType == filter.Type);
        if (filter.IsVendor.HasValue)
            query = query.Where(p => p.IsVendor == filter.IsVendor.Value);
        if (filter.IsCustomer.HasValue)
            query = query.Where(p => p.IsCustomer == filter.IsCustomer.Value);
        if (filter.IsCarrier.HasValue)
            query = query.Where(p => p.IsCarrier == filter.IsCarrier.Value);
        if (filter.IsServiceProvider.HasValue)
            query = query.Where(p => p.IsServiceProvider == filter.IsServiceProvider.Value);
        if (filter.Active.HasValue)
            query = query.Where(p => p.IsActive == filter.Active.Value);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // Same two fields SuppliersRepository.GetSuppliersAsync searches, same case-insensitive
            // Contains — one search behavior across both surfaces for the same underlying rows.
            var term = filter.Search.ToLower();
            query = query.Where(p => p.SupplierName.ToLower().Contains(term)
                || p.SupplierCode.ToLower().Contains(term));
        }

        if (await _supplierAccess.IsRestrictedAsync())
        {
            var allowedIds = await _supplierAccess.GetAllowedSupplierIdsAsync();
            query = query.Where(p => allowedIds.Contains(p.UUID));
        }

        var totalRecords = await query.CountAsync();
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderBy(p => p.SupplierName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PaginatedResponse<BusinessPartnerModel>
        {
            Data = _mapper.Map<List<BusinessPartnerModel>>(items),
            TotalRecords = totalRecords,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling(totalRecords / (double)pageSize)
        };
    }

    public Task<List<BusinessPartnerModel>> GetVendorsAsync() =>
        ListByFlagAsync(p => p.IsVendor);

    public Task<List<BusinessPartnerModel>> GetCustomersAsync() =>
        ListByFlagAsync(p => p.IsCustomer);

    public Task<List<BusinessPartnerModel>> GetCarriersAsync() =>
        ListByFlagAsync(p => p.IsCarrier);

    public Task<List<BusinessPartnerModel>> GetServiceProvidersAsync() =>
        ListByFlagAsync(p => p.IsServiceProvider);

    private async Task<List<BusinessPartnerModel>> ListByFlagAsync(
        System.Linq.Expressions.Expression<Func<BusinessPartner, bool>> flag)
    {
        var query = _db.BusinessPartners.AsNoTracking()
            .Where(p => !p.IsDelete && p.IsActive)
            .Where(flag);

        // Same restriction GetSuppliersAsync already applies (REQ-2.x) — a caller with mapped
        // access is scoped equally whichever surface (legacy Supplier list, or this one) they use.
        // See SuppliersRepository.GetSuppliersAsync for the fuller note on why this is an explicit
        // .Where rather than a second EF query filter.
        if (await _supplierAccess.IsRestrictedAsync())
        {
            var allowedIds = await _supplierAccess.GetAllowedSupplierIdsAsync();
            query = query.Where(p => allowedIds.Contains(p.UUID));
        }

        var partners = await query.OrderBy(p => p.SupplierName).ToListAsync();
        return _mapper.Map<List<BusinessPartnerModel>>(partners);
    }
}

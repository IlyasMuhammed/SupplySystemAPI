using SMS.Modules.Warehouse.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Warehouse.Repositories;

internal interface IGrnRepository
{
    Task<Guid> CreateAsync(CreateGrnRequest req, int createdBy);
    Task UpdateAsync(Guid uuid, PatchGrnRequest req, int modifiedBy);
    Task DeleteAsync(Guid uuid, int deletedBy);
    Task UpdateLineAsync(Guid grnUuid, Guid lineUuid, UpdateGrnLineRequest req, int modifiedBy);
    Task LinkLineVariantAsync(Guid grnUuid, Guid lineUuid, Guid variantUuid, int modifiedBy);
    Task InspectLineAsync(Guid grnUuid, Guid lineUuid, InspectGrnLineRequest req, int inspectedBy);
    Task SubmitAsync(Guid grnUuid, int modifiedBy);
    Task<PaginatedResponse<GrnListItemModel>> GetListAsync(GrnListFilter filter);
    Task<GrnDetailModel?> GetByIdAsync(Guid uuid);
}

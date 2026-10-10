using SMS.Modules.Material.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Repositories;

internal interface IBomRepository
{
    Task<Guid>                                CreateAsync(CreateBomRequest req, int userId);
    Task<PaginatedResponse<BomListItemModel>> GetListAsync(BomListFilter filter);
    Task<BomDetailModel?>                     GetByUuidAsync(Guid uuid);
    Task<IReadOnlyList<BomVersionModel>>      GetVersionsAsync(Guid productUuid, Guid? productVariantUuid);
    Task                                      UpdateAsync(Guid uuid, UpdateBomRequest req, int userId);
    Task                                      DeleteAsync(Guid uuid, int userId);
    Task                                      SubmitAsync(Guid uuid, int userId);
    Task                                      ApproveAsync(Guid uuid, int userId);
    Task                                      RejectAsync(Guid uuid, int userId, string reason);
    Task                                      ActivateAsync(Guid uuid, int userId);
    Task                                      ObsoleteAsync(Guid uuid, int userId, string? reason);
    Task<Guid>                                NewVersionAsync(Guid uuid, int userId);
    Task<BomComparisonModel>                  CompareAsync(Guid leftUuid, Guid rightUuid);
    Task                                      SetUsageAsync(Guid uuid, string bomUsage, int userId);
}

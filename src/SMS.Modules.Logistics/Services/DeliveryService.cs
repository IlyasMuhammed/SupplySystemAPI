using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Services;

// Public like ICarrierService and IShipmentService: the controller is public, so its constructor
// parameters must be too. The implementation stays internal.
public interface IDeliveryService
{
    Task<Guid> CreateAsync(CreateDeliveryRequest req, int createdBy);
    Task<Guid> CreateFromSourceAsync(CreateDeliveryFromSourceRequest req, int createdBy);
    Task<PaginatedResponse<DeliveryListItemModel>> GetListAsync(DeliveryFilter filter);
    Task<DeliveryDetailModel?> GetByUuidAsync(Guid uuid);
    Task<bool> PatchAsync(Guid uuid, PatchDeliveryRequest req, int modifiedBy);
    Task<bool> DeleteAsync(Guid uuid);

    /// <summary>Commits the delivery and hard-reserves its stock.</summary>
    Task<bool> ReleaseAsync(Guid uuid, ReleaseDeliveryRequest? req, int userId);

    /// <summary>What this delivery could be released against right now, line by line.</summary>
    Task<DeliveryAvailabilityModel?> GetAvailabilityAsync(Guid uuid);
    /// <summary>Moves a packed delivery to the dock, ready to be issued.</summary>
    Task<bool> StageAsync(Guid uuid, int userId);

    /// <summary>Takes the stock off the books — or records that another document already did.</summary>
    Task<GoodsIssueResultModel?> IssueAsync(Guid uuid, int userId);

    Task<bool> HoldAsync(Guid uuid, DeliveryReasonRequest req, int userId);
    Task<bool> ResumeAsync(Guid uuid, int userId);
    Task<bool> CancelAsync(Guid uuid, DeliveryReasonRequest req, int userId);
    Task<bool> ShortCloseAsync(Guid uuid, DeliveryReasonRequest req, int userId);
}

internal sealed class DeliveryService : IDeliveryService
{
    private readonly IDeliveryRepository           _repo;
    private readonly IDeliveryFromSourceRepository _fromSource;
    private readonly IDeliveryStatusRepository     _status;
    private readonly IDeliveryReleaseRepository    _release;
    private readonly IGoodsIssueRepository         _goodsIssue;

    public DeliveryService(
        IDeliveryRepository repo,
        IDeliveryFromSourceRepository fromSource,
        IDeliveryStatusRepository status,
        IDeliveryReleaseRepository release,
        IGoodsIssueRepository goodsIssue)
    {
        _repo       = repo;
        _fromSource = fromSource;
        _status     = status;
        _release    = release;
        _goodsIssue = goodsIssue;
    }

    public Task<bool> StageAsync(Guid uuid, int userId) => _goodsIssue.StageAsync(uuid, userId);

    public Task<GoodsIssueResultModel?> IssueAsync(Guid uuid, int userId) =>
        _goodsIssue.IssueAsync(uuid, userId);

    public Task<bool> ReleaseAsync(Guid uuid, ReleaseDeliveryRequest? req, int userId) =>
        _release.ReleaseAsync(uuid, req, userId);

    public Task<DeliveryAvailabilityModel?> GetAvailabilityAsync(Guid uuid) =>
        _release.GetAvailabilityAsync(uuid);

    public Task<bool> HoldAsync(Guid uuid, DeliveryReasonRequest req, int userId)       => _status.HoldAsync(uuid, req, userId);
    public Task<bool> ResumeAsync(Guid uuid, int userId)                                 => _status.ResumeAsync(uuid, userId);
    public Task<bool> CancelAsync(Guid uuid, DeliveryReasonRequest req, int userId)     => _status.CancelAsync(uuid, req, userId);
    public Task<bool> ShortCloseAsync(Guid uuid, DeliveryReasonRequest req, int userId) => _status.ShortCloseAsync(uuid, req, userId);

    public Task<Guid>                                    CreateAsync(CreateDeliveryRequest req, int createdBy)    => _repo.CreateAsync(req, createdBy);
    public Task<Guid>                                    CreateFromSourceAsync(CreateDeliveryFromSourceRequest req, int createdBy) => _fromSource.CreateFromSourceAsync(req, createdBy);
    public Task<PaginatedResponse<DeliveryListItemModel>> GetListAsync(DeliveryFilter filter)                     => _repo.GetListAsync(filter);
    public Task<DeliveryDetailModel?>                    GetByUuidAsync(Guid uuid)                               => _repo.GetByUuidAsync(uuid);
    public Task<bool>                                    PatchAsync(Guid uuid, PatchDeliveryRequest req, int mod) => _repo.PatchAsync(uuid, req, mod);
    public Task<bool>                                    DeleteAsync(Guid uuid)                                  => _repo.DeleteAsync(uuid);
}

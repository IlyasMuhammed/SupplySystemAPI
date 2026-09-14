using SMS.Modules.Warehouse.Models;

namespace SMS.Modules.Warehouse.Services;

public interface ISroAckSubmissionService
{
    Task<SroAcknowledgeResult> SubmitAsync(string rawToken, SroAcknowledgeRequest request, string clientIp);
}

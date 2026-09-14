namespace SMS.Modules.Warehouse.Services;

// REQ-3.x — generates the login-free supplier acknowledgment link issued on SRO dispatch.
// Mirrors SMS.Modules.Demand's IRfqLinkTokenService (same proven design for the same problem).
public interface ISroAckTokenService
{
    Task<(string RawToken, int LinkId, DateTime ExpiresAt)> GenerateTokenAsync(
        int returnOrderId,
        Guid supplierId,
        int createdBy,
        string? supplierEmail,
        string portalBaseUrl,
        Guid organizationId);
}

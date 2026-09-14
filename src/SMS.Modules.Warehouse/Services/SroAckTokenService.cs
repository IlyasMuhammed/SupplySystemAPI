using System.Security.Cryptography;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Warehouse.Services;

internal sealed class SroAckTokenService : ISroAckTokenService
{
    private readonly WarehouseDbContext _wh;
    private readonly IOrganizationSettingsService _orgSettings;

    public SroAckTokenService(WarehouseDbContext wh, IOrganizationSettingsService orgSettings)
    {
        _wh          = wh;
        _orgSettings = orgSettings;
    }

    public async Task<(string RawToken, int LinkId, DateTime ExpiresAt)> GenerateTokenAsync(
        int returnOrderId, Guid supplierId, int createdBy, string? supplierEmail, string portalBaseUrl, Guid organizationId)
    {
        // 256 bits of cryptographic randomness
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);

        // Base64Url-encode (URL-safe, no padding) for the link
        var rawToken = Base64UrlEncode(bytes);

        // Store only the SHA-256 hash — raw token is never persisted
        var hashBytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken));
        var tokenHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        var expiryDays  = await _orgSettings.GetAckLinkExpiryDaysAsync(organizationId);
        var generatedAt = DateTime.UtcNow;
        var expiresAt   = generatedAt.AddDays(expiryDays);

        var portalLinkUrl = $"{portalBaseUrl.TrimEnd('/')}/supplier-portal/sro-ack/{rawToken}";

        var link = new SroAcknowledgmentLink
        {
            ReturnOrderId = returnOrderId,
            SupplierId    = supplierId,
            TokenHash     = tokenHash,
            Status        = "PENDING",
            GeneratedAt   = generatedAt,
            ExpiresAt     = expiresAt,
            AccessCount   = 0,
            CreatedBy     = createdBy,
            SupplierEmail = supplierEmail,
            PortalLinkUrl = portalLinkUrl
        };

        _wh.SroAcknowledgmentLinks.Add(link);
        await _wh.SaveChangesAsync();

        return (rawToken, link.Id, expiresAt);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

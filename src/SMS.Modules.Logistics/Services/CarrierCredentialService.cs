using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Models;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// The credential surface the outside world gets: set, remove, and list without values.
/// <para>
/// Public because the controller is. Note what is <b>not</b> on it — there is no read. The vault
/// can decrypt, but only the booking path can reach the vault.
/// </para>
/// </summary>
public interface ICarrierCredentialService
{
    Task<IReadOnlyList<CarrierCredentialModel>> ListAsync(Guid accountUuid);
    Task<Guid> SetAsync(Guid accountUuid, SetCarrierCredentialRequest req, int userId);
    Task<bool> RemoveAsync(Guid accountUuid, string key, int userId);
}

internal sealed class CarrierCredentialService : ICarrierCredentialService
{
    private readonly ICarrierCredentialVault _vault;
    public CarrierCredentialService(ICarrierCredentialVault vault) => _vault = vault;

    public async Task<IReadOnlyList<CarrierCredentialModel>> ListAsync(Guid accountUuid)
    {
        var credentials = await _vault.ListAsync(accountUuid);

        return [.. credentials.Select(c => new CarrierCredentialModel
        {
            UUID        = c.Uuid,
            Key         = c.CredentialKey,
            Description = c.Description,
            ExpiresAt   = c.ExpiresAt,
            IsExpired   = c.IsExpired,
            SetAt       = c.SetAt,
            SetBy       = c.SetBy
        })];
    }

    public Task<Guid> SetAsync(Guid accountUuid, SetCarrierCredentialRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);
        return _vault.SetAsync(accountUuid, req.Key, req.Value, req.Description, req.ExpiresAt, userId);
    }

    public Task<bool> RemoveAsync(Guid accountUuid, string key, int userId) =>
        _vault.RemoveAsync(accountUuid, key, userId);
}

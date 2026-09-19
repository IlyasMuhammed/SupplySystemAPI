using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Couriers;

/// <summary>What a credential is, minus the thing that makes it one.</summary>
/// <param name="IsExpired">Computed on read: a lapsed credential takes every booking with it.</param>
internal sealed record CarrierCredentialSummary(
    Guid      Uuid,
    string    CredentialKey,
    string?   Description,
    DateTime? ExpiresAt,
    bool      IsExpired,
    DateTime  SetAt,
    int       SetBy);

internal interface ICarrierCredentialVault
{
    /// <summary>
    /// The decrypted credentials for an account, ready to hand to an adapter.
    /// <para>
    /// The <b>only</b> method that returns plaintext, and nothing above the booking path calls it.
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetForAccountAsync(int accountId, CancellationToken ct = default);

    /// <summary>
    /// The same, for code with no tenant of its own — the anonymous webhook endpoint — scoped
    /// explicitly to the organization that owns the account instead of relying on the query filter.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetForAccountAsync(
        int accountId, Guid organizationId, CancellationToken ct = default);

    /// <summary>What is configured, without the values. This is what screens get.</summary>
    Task<IReadOnlyList<CarrierCredentialSummary>> ListAsync(Guid accountUuid, CancellationToken ct = default);

    /// <summary>Sets or replaces one credential. Returns its id.</summary>
    Task<Guid> SetAsync(
        Guid accountUuid, string key, string value, string? description, DateTime? expiresAt,
        int userId, CancellationToken ct = default);

    Task<bool> RemoveAsync(Guid accountUuid, string key, int userId, CancellationToken ct = default);
}

/// <summary>
/// The only code that encrypts, decrypts or stores a carrier secret.
/// <para>
/// <b>Nothing here ever returns a stored value to a caller above the booking path.</b> There is no
/// "reveal" operation and no masked preview — a masked secret is still a leak of its length and
/// shape, and the request for one is always "so an administrator can check it", which is a check
/// better served by attempting a booking. A credential that cannot be read back can only be
/// replaced, and that is the correct affordance.
/// </para>
/// <para>
/// Values are encrypted on the way in and decrypted only in <see cref="GetForAccountAsync"/>, so
/// plaintext exists in exactly two places: the request that set it, and the adapter call that
/// uses it.
/// </para>
/// </summary>
internal sealed class CarrierCredentialVault : ICarrierCredentialVault
{
    private readonly LogisticsDbContext _db;
    private readonly IEncryptionService _encryption;

    public CarrierCredentialVault(LogisticsDbContext db, IEncryptionService encryption)
    {
        _db         = db;
        _encryption = encryption;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetForAccountAsync(
        int accountId, CancellationToken ct = default) =>
        Decrypt(await _db.CarrierCredentials
            .AsNoTracking()
            .Where(c => c.CarrierAccountId == accountId && !c.IsDelete)
            .ToListAsync(ct));

    public async Task<IReadOnlyDictionary<string, string>> GetForAccountAsync(
        int accountId, Guid organizationId, CancellationToken ct = default) =>
        Decrypt(await _db.CarrierCredentials
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.CarrierAccountId == accountId && c.OrganizationId == organizationId && !c.IsDelete)
            .ToListAsync(ct));

    private IReadOnlyDictionary<string, string> Decrypt(List<CarrierCredential> rows)
    {
        var credentials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            try
            {
                credentials[row.CredentialKey] = _encryption.Decrypt(row.EncryptedValue);
            }
            catch (Exception ex)
            {
                // Either the encryption key has changed under the data, or the ciphertext has been
                // altered — which the authenticated format is there to detect. Booking with the
                // rest of the credentials would authenticate as somebody or fail confusingly, so
                // it stops here and says which key is unreadable. The value itself never appears.
                throw new ConflictException(
                    $"The stored credential '{row.CredentialKey}' cannot be decrypted ({ex.GetType().Name}). "
                  + "Either the encryption key has changed since it was saved, or the value has been "
                  + "altered. Set it again.");
            }
        }

        return credentials;
    }

    public async Task<IReadOnlyList<CarrierCredentialSummary>> ListAsync(
        Guid accountUuid, CancellationToken ct = default)
    {
        var accountId = await AccountIdAsync(accountUuid, ct);
        if (accountId is null) return [];

        var now = DateTime.UtcNow;

        return await _db.CarrierCredentials
            .AsNoTracking()
            .Where(c => c.CarrierAccountId == accountId.Value && !c.IsDelete)
            .OrderBy(c => c.CredentialKey)
            // Projected rather than mapped from the entity, so EncryptedValue is not even loaded.
            .Select(c => new CarrierCredentialSummary(
                c.UUID,
                c.CredentialKey,
                c.Description,
                c.ExpiresAt,
                c.ExpiresAt != null && c.ExpiresAt < now,
                c.ModifiedDate ?? c.CreatedDate,
                c.ModifiedBy ?? c.CreatedBy))
            .ToListAsync(ct);
    }

    public async Task<Guid> SetAsync(
        Guid accountUuid, string key, string value, string? description, DateTime? expiresAt,
        int userId, CancellationToken ct = default)
    {
        var accountId = await AccountIdAsync(accountUuid, ct)
            ?? throw new NotFoundException("Carrier account", accountUuid);

        if (string.IsNullOrWhiteSpace(key))
            throw new BadRequestException("A credential needs a key — the name the adapter asks for it by.");

        // Whitespace only would be indistinguishable from unset, and a credential set to nothing
        // fails at the carrier rather than here, which is a long way from the cause.
        if (string.IsNullOrWhiteSpace(value))
            throw new BadRequestException(
                "A credential needs a value. Remove it instead of setting it to nothing.");

        var trimmedKey = key.Trim();
        var now        = DateTime.UtcNow;

        var existing = await _db.CarrierCredentials
            .FirstOrDefaultAsync(c => c.CarrierAccountId == accountId
                                   && c.CredentialKey == trimmedKey, ct);

        if (existing is not null)
        {
            // Replacing, including where a previous one was soft-deleted: the unique index is on
            // (account, key) regardless of IsDelete, so reviving the row is the only way to set a
            // key that was once removed.
            existing.EncryptedValue = _encryption.Encrypt(value);
            existing.Description    = Trim(description);
            existing.ExpiresAt      = expiresAt;
            existing.IsDelete       = false;
            existing.ModifiedBy     = userId;
            existing.ModifiedDate   = now;

            await _db.SaveChangesAsync(ct);
            return existing.UUID;
        }

        var credential = new CarrierCredential
        {
            UUID             = Guid.NewGuid(),
            CarrierAccountId = accountId,
            CredentialKey    = trimmedKey,
            EncryptedValue   = _encryption.Encrypt(value),
            Description      = Trim(description),
            ExpiresAt        = expiresAt,
            CreatedBy        = userId,
            CreatedDate      = now
        };

        _db.CarrierCredentials.Add(credential);
        await _db.SaveChangesAsync(ct);

        return credential.UUID;
    }

    public async Task<bool> RemoveAsync(
        Guid accountUuid, string key, int userId, CancellationToken ct = default)
    {
        var accountId = await AccountIdAsync(accountUuid, ct);
        if (accountId is null) return false;

        var trimmedKey = (key ?? string.Empty).Trim();

        var credential = await _db.CarrierCredentials
            .FirstOrDefaultAsync(c => c.CarrierAccountId == accountId.Value
                                   && c.CredentialKey == trimmedKey && !c.IsDelete, ct);

        if (credential is null) return false;

        // Soft-deleted, and the ciphertext cleared with it. Keeping the row preserves who removed
        // what and when; keeping the secret would mean a deleted credential is still a secret
        // sitting in the database.
        credential.IsDelete       = true;
        credential.EncryptedValue = string.Empty;
        credential.ModifiedBy     = userId;
        credential.ModifiedDate   = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<int?> AccountIdAsync(Guid accountUuid, CancellationToken ct) =>
        await _db.CarrierAccounts
            .Where(a => a.UUID == accountUuid && !a.IsDelete)
            .Select(a => (int?)a.Id)
            .FirstOrDefaultAsync(ct);

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

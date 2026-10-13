using SMS.Modules.Crm.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Crm.Services;

/// <summary>
/// Names for the ids the CRM stores (users, accounts, contacts), resolved in batches through the Shared seams. Each seam is
/// optional: without it the names stay empty and checks that need it are skipped (a host without Auth / Suppliers still
/// starts — the A38 convention).
/// </summary>
internal sealed class CrmDirectory
{
    private readonly IOrgUserDirectory? _users;
    private readonly ICrmAccountGateway? _accounts;

    public CrmDirectory(IOrgUserDirectory? users = null, ICrmAccountGateway? accounts = null)
    {
        _users = users;
        _accounts = accounts;
    }

    public ICrmAccountGateway? Accounts => _accounts;

    public async Task<Dictionary<int, OrgUserInfo>> UsersAsync(Guid org, IEnumerable<int?> ids, CancellationToken ct = default)
    {
        var list = ids.Where(i => i is > 0).Select(i => i!.Value).Distinct().ToList();
        if (_users is null || list.Count == 0) return [];
        return (await _users.GetUsersAsync(org, list, ct)).ToDictionary(u => u.UserId);
    }

    public async Task<string?> UserNameAsync(Guid org, int? id, CancellationToken ct = default) =>
        id is > 0 && (await UsersAsync(org, [id], ct)).TryGetValue(id.Value, out var u) ? u.Name : null;

    /// <summary>The user must be an active user of the organization (D-5 assignment, performed-by).</summary>
    public async Task RequireActiveUserAsync(Guid org, int userId, string what, CancellationToken ct = default)
    {
        if (_users is null) return;
        var found = (await _users.GetUsersAsync(org, [userId], ct)).FirstOrDefault();
        if (found is null || !found.IsActive)
            throw new SMS.Shared.Exceptions.BadRequestException($"The {what} must be an active user of the organization.");
    }

    public async Task<Dictionary<Guid, CrmAccountInfo>> AccountsAsync(Guid org, IEnumerable<Guid?> ids, CancellationToken ct = default)
    {
        var list = ids.Where(i => i is { } g && g != Guid.Empty).Select(i => i!.Value).Distinct().ToList();
        if (_accounts is null || list.Count == 0) return [];
        return (await _accounts.GetAccountsAsync(org, list, ct)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public async Task<Dictionary<int, CrmContactInfo>> ContactsAsync(Guid org, IEnumerable<int?> ids, CancellationToken ct = default)
    {
        var list = ids.Where(i => i is > 0).Select(i => i!.Value).Distinct().ToList();
        if (_accounts is null || list.Count == 0) return [];
        return (await _accounts.GetContactsAsync(org, list, ct)).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public static CrmAccountModel ToModel(CrmAccountInfo a) => new()
    {
        Uuid = a.Uuid, Code = a.Code, Name = a.Name, IsCustomer = a.IsCustomer, IsVendor = a.IsVendor, IsActive = a.IsActive,
        Phone = a.Phone, Email = a.Email, CustomerType = a.CustomerType, CreatedAt = a.CreatedAt
    };

    public static CrmContactModel ToModel(CrmContactInfo c) => new()
    {
        Id = c.Id, AccountUuid = c.AccountUuid, Name = c.Name, Title = c.Title, Phone = c.Phone, Email = c.Email,
        IsPrimary = c.IsPrimary, IsActive = c.IsActive
    };
}

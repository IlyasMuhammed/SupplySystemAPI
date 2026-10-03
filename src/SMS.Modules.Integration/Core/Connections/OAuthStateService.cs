using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Core.Connections;

internal enum StateRedemptionStatus
{
    Redeemed,
    /// <summary>Missing, malformed, or not a token we issued.</summary>
    Invalid,
    Expired,
    /// <summary>Already redeemed once — a replay, or a second browser tab racing the first.</summary>
    AlreadyUsed
}

internal sealed record StateRedemption(StateRedemptionStatus Status, Guid OrganizationId = default, int UserId = 0)
{
    public bool Succeeded => Status == StateRedemptionStatus.Redeemed;
}

/// <summary>
/// The OAuth <c>state</c> parameter (plan QBI-08). It is the <b>only</b> thing that tells the anonymous
/// callback which organization is connecting — Intuit's redirect carries no authentication of ours —
/// so it is random, short-lived, org-bound, stored only as a hash, and redeemable exactly once.
/// </summary>
internal interface IOAuthStateService
{
    /// <summary>
    /// Mints a token for this organization and user and adds its row to the context <b>without saving</b>,
    /// so the caller commits it together with the connection's move to Connecting. Returns the raw token,
    /// which from here on exists only in the consent URL.
    /// </summary>
    string Issue(Guid organizationId, int userId, DateTime? utcNow = null);

    /// <summary>
    /// Validates and burns a token. Looked up by its hash across every organization — the caller is
    /// anonymous, so there is no tenant yet — and marked used atomically, so two concurrent callbacks
    /// with the same token cannot both succeed.
    /// </summary>
    Task<StateRedemption> RedeemAsync(string? rawState, DateTime? utcNow = null, CancellationToken ct = default);
}

internal sealed class OAuthStateService : IOAuthStateService
{
    /// <summary>A token we issue is 43 characters; anything far longer is not one of ours.</summary>
    private const int MaxRawLength = 200;

    private readonly IntegrationDbContext _db;
    private readonly QuickBooksOptions    _options;

    public OAuthStateService(IntegrationDbContext db, IOptions<QuickBooksOptions> options)
    {
        _db      = db;
        _options = options.Value;
    }

    public string Issue(Guid organizationId, int userId, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;

        // 256 bits: guessing a live token inside its ten-minute window is not a thing that happens.
        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));

        _db.OAuthStateTokens.Add(new OAuthStateToken
        {
            OrganizationId = organizationId,
            UserId         = userId,
            TokenHash      = Hash(raw),
            CreatedAt      = now,
            ExpiresAt      = now.AddMinutes(Math.Max(1, _options.StateTokenMinutes))
        });

        return raw;
    }

    public async Task<StateRedemption> RedeemAsync(string? rawState, DateTime? utcNow = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawState) || rawState.Length > MaxRawLength)
            return new StateRedemption(StateRedemptionStatus.Invalid);

        var now  = utcNow ?? DateTime.UtcNow;
        var hash = Hash(rawState.Trim());

        // Explicitly across tenants: the callback is anonymous, so the org is what we are finding out.
        // One row by its unique hash — never a listing.
        var row = await _db.OAuthStateTokens
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => new { t.Id, t.OrganizationId, t.UserId, t.ExpiresAt, t.UsedAt })
            .FirstOrDefaultAsync(ct);

        if (row is null)            return new StateRedemption(StateRedemptionStatus.Invalid);
        if (row.UsedAt is not null) return new StateRedemption(StateRedemptionStatus.AlreadyUsed);
        if (row.ExpiresAt <= now)   return new StateRedemption(StateRedemptionStatus.Expired);

        return await ClaimAsync(row.Id, now, ct)
            ? new StateRedemption(StateRedemptionStatus.Redeemed, row.OrganizationId, row.UserId)
            : new StateRedemption(StateRedemptionStatus.AlreadyUsed);
    }

    /// <summary>
    /// The single-use guarantee. On SQL Server it is one conditional UPDATE — "set UsedAt where it is
    /// still null" — so of two racing callbacks the database lets exactly one through. The in-memory
    /// provider used by unit tests cannot run set-based updates, so it gets the equivalent tracked write;
    /// the race itself is proven against LocalDB.
    /// </summary>
    private async Task<bool> ClaimAsync(int id, DateTime now, CancellationToken ct)
    {
        if (_db.Database.IsRelational())
        {
            var affected = await _db.OAuthStateTokens
                .IgnoreQueryFilters()
                .Where(t => t.Id == id && t.UsedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, (DateTime?)now), ct);

            return affected == 1;
        }

        var token = await _db.OAuthStateTokens.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (token is null || token.UsedAt is not null) return false;

        token.UsedAt = now;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    internal static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

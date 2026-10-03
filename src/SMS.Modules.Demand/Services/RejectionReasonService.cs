using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A32 PA-03/PA-04 — the organization's rejection reasons (§7). Every query filters explicitly on the caller's own
/// organization: the EF tenant filter is off for super admins, and another organization's reason must read as absent
/// to them too. Seeding takes the organization it is given and ignores the filter, because whoever provisions an
/// organization (a super admin) is not in it.
/// </summary>
internal sealed partial class RejectionReasonService : IRejectionReasonService
{
    /// <summary>§7.3, in the spec's order; DisplayOrder 10, 20 … 100 leaves room to slot custom reasons between them.</summary>
    internal static readonly IReadOnlyList<(string Code, string Description)> SeedReasons =
    [
        ("NIP", "Not in product catalog"),
        ("OOS", "Out of stock — no replenishment planned"),
        ("DIS", "Product discontinued"),
        ("MOQ", "Below minimum order quantity"),
        ("GEO", "Cannot deliver to requested region"),
        ("REG", "Regulatory restriction on this product"),
        ("CAP", "Insufficient production capacity"),
        ("CRD", "Customer credit hold"),
        ("PRC", "Cannot meet requested price"),
        ("OTH", "Other (see notes)"),
    ];

    internal const int CodeMaxLength        = 10;
    internal const int DescriptionMaxLength = 200;

    [GeneratedRegex("^[A-Z0-9_]+$")]
    private static partial Regex CodePattern();

    private readonly DemandDbContext _db;
    private readonly ITenantContext _tenant;

    public RejectionReasonService(DemandDbContext db, ITenantContext tenant)
    {
        _db     = db;
        _tenant = tenant;
    }

    private IQueryable<RejectionReason> Own() =>
        _db.RejectionReasons.IgnoreQueryFilters().Where(r => r.OrganizationId == _tenant.OrganizationId);

    // ── Reads ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RejectionReasonModel>> GetListAsync(bool includeInactive)
    {
        var query = Own().AsNoTracking();
        if (!includeInactive) query = query.Where(r => r.IsActive);

        var rows = await query.OrderBy(r => r.DisplayOrder).ThenBy(r => r.Code).ToListAsync();
        return rows.Select(ToModel).ToList();
    }

    public async Task<RejectionReasonModel?> GetByIdAsync(Guid uuid)
    {
        var row = await Own().AsNoTracking().FirstOrDefaultAsync(r => r.UUID == uuid);
        return row is null ? null : ToModel(row);
    }

    // ── Writes ───────────────────────────────────────────────────────────────

    public async Task<RejectionReasonModel> CreateAsync(CreateRejectionReasonRequest req, int userId)
    {
        var code        = NormalizeCode(req.Code);
        var description = NormalizeDescription(req.Description);

        // BR-C5-01. Checked here for a clear message; the unique index (OrganizationId, Code) is what holds under a race.
        if (await Own().AnyAsync(r => r.Code == code))
            throw new ConflictException($"Rejection reason code '{code}' already exists in this organization. Codes must be unique.");

        var displayOrder = req.DisplayOrder
            ?? ((await Own().Select(r => (int?)r.DisplayOrder).MaxAsync()) ?? 0) + 10;

        var row = new RejectionReason
        {
            OrganizationId = _tenant.OrganizationId,
            Code           = code,
            Description    = description,
            IsActive       = true,
            IsSystem       = false,
            DisplayOrder   = displayOrder,
            CreatedBy      = userId,
            CreatedDate    = DateTime.UtcNow
        };
        _db.RejectionReasons.Add(row);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost the race to another request creating the same code.
            throw new ConflictException($"Rejection reason code '{code}' already exists in this organization. Codes must be unique.");
        }

        return ToModel(row);
    }

    public async Task<RejectionReasonModel?> UpdateAsync(Guid uuid, UpdateRejectionReasonRequest req, int userId)
    {
        var row = await Own().FirstOrDefaultAsync(r => r.UUID == uuid);
        if (row is null) return null;

        row.Description  = NormalizeDescription(req.Description);
        row.DisplayOrder = req.DisplayOrder;
        row.ModifiedBy   = userId;
        row.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return ToModel(row);
    }

    public async Task<RejectionReasonModel?> SetActiveAsync(Guid uuid, bool isActive, int userId)
    {
        var row = await Own().FirstOrDefaultAsync(r => r.UUID == uuid);
        if (row is null) return null;

        if (row.IsActive != isActive)
        {
            row.IsActive     = isActive;
            row.ModifiedBy   = userId;
            row.ModifiedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return ToModel(row);
    }

    public async Task<bool> DeleteAsync(Guid uuid, int userId)
    {
        var row = await Own().FirstOrDefaultAsync(r => r.UUID == uuid);
        if (row is null) return false;

        // BR-C5-02 — a seeded code is the analytics' fixed vocabulary.
        if (row.IsSystem)
            throw new ConflictException($"'{row.Code}' is one of the standard rejection reasons and cannot be deleted. Deactivate it instead.");

        // BR-C5-03 — history keeps its reasons. Checked across organizations on purpose: a line can only ever point at
        // its own organization's reason, and the FK would refuse the delete anyway.
        var inUse = await _db.SaleInquiryLines.IgnoreQueryFilters().AnyAsync(l => l.RejectionReasonId == row.Id)
                 || await _db.SaleQuotationLines.IgnoreQueryFilters().AnyAsync(l => l.RejectionReasonId == row.Id);
        if (inUse)
            throw new ConflictException($"'{row.Code}' is already recorded on inquiry or quotation lines and cannot be deleted. Deactivate it instead.");

        _db.RejectionReasons.Remove(row);
        await _db.SaveChangesAsync();
        return true;
    }

    // ── Seeding (PA-03) ──────────────────────────────────────────────────────

    public Task<int> EnsureSeededAsync(Guid organizationId) => EnsureSeededForAllAsync([organizationId]);

    public async Task<int> EnsureSeededForAllAsync(IReadOnlyList<Guid> organizationIds)
    {
        var orgs = organizationIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (orgs.Count == 0) return 0;

        var seedCodes = SeedReasons.Select(s => s.Code).ToList();
        var existing = (await _db.RejectionReasons.IgnoreQueryFilters().AsNoTracking()
                .Where(r => orgs.Contains(r.OrganizationId) && seedCodes.Contains(r.Code))
                .Select(r => new { r.OrganizationId, r.Code })
                .ToListAsync())
            .Select(x => (x.OrganizationId, x.Code.ToUpperInvariant()))
            .ToHashSet();

        var added = 0;
        foreach (var org in orgs)
        {
            for (var i = 0; i < SeedReasons.Count; i++)
            {
                var (code, description) = SeedReasons[i];
                if (existing.Contains((org, code))) continue;

                _db.RejectionReasons.Add(new RejectionReason
                {
                    OrganizationId = org,
                    Code           = code,
                    Description    = description,
                    IsActive       = true,
                    IsSystem       = true,
                    DisplayOrder   = (i + 1) * 10,
                    CreatedDate    = DateTime.UtcNow
                });
                added++;
            }
        }

        if (added > 0) await _db.SaveChangesAsync();
        return added;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string NormalizeCode(string? code)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length == 0)
            throw new BadRequestException("A rejection reason needs a code.");
        if (normalized.Length > CodeMaxLength)
            throw new BadRequestException($"A rejection reason code can be at most {CodeMaxLength} characters; '{normalized}' has {normalized.Length}.");
        if (!CodePattern().IsMatch(normalized))
            throw new BadRequestException($"A rejection reason code can use only letters, digits and underscores; '{normalized}' does not.");
        return normalized;
    }

    private static string NormalizeDescription(string? description)
    {
        var normalized = (description ?? string.Empty).Trim();
        if (normalized.Length == 0)
            throw new BadRequestException("A rejection reason needs a description.");
        if (normalized.Length > DescriptionMaxLength)
            throw new BadRequestException($"A rejection reason's description can be at most {DescriptionMaxLength} characters.");
        return normalized;
    }

    private static RejectionReasonModel ToModel(RejectionReason r) => new()
    {
        Uuid         = r.UUID,
        Code         = r.Code,
        Description  = r.Description,
        IsActive     = r.IsActive,
        IsSystem     = r.IsSystem,
        DisplayOrder = r.DisplayOrder,
        CreatedDate  = r.CreatedDate,
        ModifiedDate = r.ModifiedDate
    };
}

/// <summary>A32 PA-03 — seeds the ten reasons for an organization the moment Tenancy creates it.</summary>
internal sealed class RejectionReasonProvisioningHandler : IOrganizationProvisionedHandler
{
    private readonly IRejectionReasonService _reasons;
    public RejectionReasonProvisioningHandler(IRejectionReasonService reasons) => _reasons = reasons;

    public Task OnOrganizationProvisionedAsync(Guid organizationId, CancellationToken ct = default) =>
        _reasons.EnsureSeededAsync(organizationId);
}

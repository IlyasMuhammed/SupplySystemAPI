using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.ArAp.Payables;

public interface IToleranceRuleService
{
    Task<IReadOnlyList<ToleranceRuleModel>> ListAsync(CancellationToken ct = default);
    Task<ToleranceRuleModel> CreateAsync(ToleranceRuleRequest request, ApActor actor, CancellationToken ct = default);
    Task<ToleranceRuleModel> UpdateAsync(Guid uuid, ToleranceRuleRequest request, ApActor actor, CancellationToken ct = default);
}

/// <summary>
/// A48 C9 (tasks 525, D-7) — match tolerance rules: one organization default (seeded DEFAULT, D-16), optionally one active
/// rule per vendor. Making a rule the default takes the flag from the previous one; the default cannot be deactivated.
/// </summary>
internal sealed class ToleranceRuleService : IToleranceRuleService
{
    internal const int NameMaxLength = 100;

    private readonly FinanceDbContext            _fin;
    private readonly IPartnerLookupService?      _partners;
    private readonly ISupplierNameLookupService? _names;

    public ToleranceRuleService(FinanceDbContext fin, IPartnerLookupService? partners = null, ISupplierNameLookupService? names = null)
    {
        _fin      = fin;
        _partners = partners;
        _names    = names;
    }

    private Guid Org => _fin.TenantContext.OrganizationId;
    private IQueryable<MatchToleranceRule> Own() => _fin.Set<MatchToleranceRule>().Where(r => r.OrganizationId == Org);

    public async Task<IReadOnlyList<ToleranceRuleModel>> ListAsync(CancellationToken ct = default) =>
        (await Own().AsNoTracking().OrderByDescending(r => r.IsDefault).ThenBy(r => r.VendorUuid != null).ThenBy(r => r.Name).ToListAsync(ct))
            .Select(ToModel).ToList();

    public async Task<ToleranceRuleModel> CreateAsync(ToleranceRuleRequest request, ApActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = Name(request.Name) ?? throw new BadRequestException("Name is required.");
        var rule = new MatchToleranceRule
        {
            OrganizationId           = Org,
            Name                     = name,
            PriceTolerancePercent    = request.PriceTolerancePercent ?? 0m,
            PriceToleranceAmount     = request.PriceToleranceAmount ?? 0m,
            QuantityTolerancePercent = request.QuantityTolerancePercent ?? 0m,
            TotalTolerancePercent    = request.TotalTolerancePercent ?? 0m,
            TotalToleranceAmount     = request.TotalToleranceAmount ?? 0m,
            AutoApproveWithin        = request.AutoApproveWithin ?? false,
            IsDefault                = request.IsDefault ?? false,
            IsActive                 = request.IsActive ?? true,
            CreatedAt                = DateTime.UtcNow,
            CreatedBy                = actor.UserId,
            CreatedByName            = actor.UserName
        };
        if (request.VendorUuid is { } vendor && vendor != Guid.Empty)
        {
            rule.VendorUuid = vendor;
            rule.VendorName = await VendorNameAsync(vendor, ct);
        }

        Validate(rule);
        await UniqueNameAsync(name, null, ct);
        await OneActivePerVendorAsync(rule, ct);
        if (rule.IsDefault) await TakeDefaultAsync(null, ct);

        _fin.Set<MatchToleranceRule>().Add(rule);
        await SaveAsync(name, ct);
        return ToModel(rule);
    }

    public async Task<ToleranceRuleModel> UpdateAsync(Guid uuid, ToleranceRuleRequest request, ApActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rule = await Own().FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Tolerance rule", uuid);
        var wasDefault = rule.IsDefault;

        if (Name(request.Name) is { } name && name != rule.Name)
        {
            await UniqueNameAsync(name, rule.Id, ct);
            rule.Name = name;
        }
        if (request.VendorUuid is { } vendor)
        {
            if (vendor == Guid.Empty) { rule.VendorUuid = null; rule.VendorName = null; }
            else if (vendor != rule.VendorUuid) { rule.VendorUuid = vendor; rule.VendorName = await VendorNameAsync(vendor, ct); }
        }
        if (request.PriceTolerancePercent    is { } a) rule.PriceTolerancePercent    = a;
        if (request.PriceToleranceAmount     is { } b) rule.PriceToleranceAmount     = b;
        if (request.QuantityTolerancePercent is { } c) rule.QuantityTolerancePercent = c;
        if (request.TotalTolerancePercent    is { } d) rule.TotalTolerancePercent    = d;
        if (request.TotalToleranceAmount     is { } e) rule.TotalToleranceAmount     = e;
        if (request.AutoApproveWithin        is { } f) rule.AutoApproveWithin        = f;
        if (request.IsActive                 is { } g) rule.IsActive                 = g;
        if (request.IsDefault                is { } h) rule.IsDefault                = h;

        if (wasDefault && (!rule.IsDefault || !rule.IsActive))
            throw new ConflictException("The default tolerance rule cannot be deactivated; make another rule the default first.");

        Validate(rule);
        await OneActivePerVendorAsync(rule, ct);
        if (rule.IsDefault && !wasDefault) await TakeDefaultAsync(rule.Id, ct);

        rule.ModifiedAt     = DateTime.UtcNow;
        rule.ModifiedBy     = actor.UserId;
        rule.ModifiedByName = actor.UserName;
        await SaveAsync(rule.Name, ct);
        return ToModel(rule);
    }

    private static void Validate(MatchToleranceRule r)
    {
        decimal[] all = [r.PriceTolerancePercent, r.PriceToleranceAmount, r.QuantityTolerancePercent, r.TotalTolerancePercent, r.TotalToleranceAmount];
        if (all.Any(x => x < 0m)) throw new BadRequestException("Tolerances cannot be negative.");
        if (r.PriceTolerancePercent > 100m || r.QuantityTolerancePercent > 100m || r.TotalTolerancePercent > 100m)
            throw new BadRequestException("A percentage cannot exceed 100.");
        if (r.IsDefault && r.VendorUuid is not null)
            throw new BadRequestException("A vendor's tolerance rule cannot be the organization default.");
        if (r.IsDefault && !r.IsActive)
            throw new BadRequestException("The default tolerance rule must be active.");
    }

    private async Task UniqueNameAsync(string name, int? exceptId, CancellationToken ct)
    {
        if (await Own().AnyAsync(r => r.Name == name && (exceptId == null || r.Id != exceptId), ct))
            throw new ConflictException($"A tolerance rule named '{name}' already exists.");
    }

    private async Task OneActivePerVendorAsync(MatchToleranceRule rule, CancellationToken ct)
    {
        if (rule.VendorUuid is not { } vendor || !rule.IsActive) return;
        var other = await Own().Where(r => r.VendorUuid == vendor && r.IsActive && r.Id != rule.Id).Select(r => r.Name).FirstOrDefaultAsync(ct);
        if (other is not null)
            throw new ConflictException($"{rule.VendorName ?? vendor.ToString()} already has an active tolerance rule ({other}).");
    }

    private async Task TakeDefaultAsync(int? exceptId, CancellationToken ct)
    {
        foreach (var r in await Own().Where(r => r.IsDefault && (exceptId == null || r.Id != exceptId)).ToListAsync(ct))
            r.IsDefault = false;
    }

    private async Task<string> VendorNameAsync(Guid vendor, CancellationToken ct)
    {
        if (_partners is not null)
        {
            var p = await _partners.GetAsync(Org, vendor, PartnerRoles.Vendor, ct);
            if (p is not null) return p.Name;
            throw new NotFoundException("Business partner", vendor);
        }
        if (_names is not null && (await _names.GetNamesAsync([vendor])).TryGetValue(vendor, out var name) && !string.IsNullOrWhiteSpace(name))
            return name.Trim();
        throw new NotFoundException("Business partner", vendor);
    }

    private static string? Name(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) return null;
        return n.Length > NameMaxLength ? throw new BadRequestException($"Name can be at most {NameMaxLength} characters.") : n;
    }

    private async Task SaveAsync(string name, CancellationToken ct)
    {
        try { await _fin.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (_fin.Database.IsRelational())
        {
            throw new ConflictException($"A tolerance rule named '{name}' already exists.");
        }
    }

    internal static ToleranceRuleModel ToModel(MatchToleranceRule r) => new(
        r.Uuid, r.Name, r.VendorUuid, r.VendorName, r.PriceTolerancePercent, r.PriceToleranceAmount, r.QuantityTolerancePercent,
        r.TotalTolerancePercent, r.TotalToleranceAmount, r.AutoApproveWithin, r.IsDefault, r.IsActive);
}

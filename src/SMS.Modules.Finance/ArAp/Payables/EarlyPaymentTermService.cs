using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.ArAp.Payables;

public interface IEarlyPaymentTermService
{
    Task<IReadOnlyList<EarlyPaymentTermModel>> ListAsync(CancellationToken ct = default);
    Task<EarlyPaymentTermModel> CreateAsync(EarlyPaymentTermRequest request, ApActor actor, CancellationToken ct = default);
    Task<EarlyPaymentTermModel> UpdateAsync(Guid uuid, EarlyPaymentTermRequest request, ApActor actor, CancellationToken ct = default);
}

/// <summary>
/// A48 C15 (tasks 365–368, BR-EP-004) — early-payment terms ("2/10 Net 30"). A term reaches a schedule through the
/// vendor's profile (new schedules) or <see cref="IPaymentScheduleService.SetEarlyPaymentTermAsync"/>. Changing a term
/// does not rewrite the discounts already on schedules (they were offered on those terms).
/// </summary>
internal sealed class EarlyPaymentTermService : IEarlyPaymentTermService
{
    internal const int CodeMaxLength = 20;
    internal const int NameMaxLength = 100;

    private readonly FinanceDbContext _fin;

    public EarlyPaymentTermService(FinanceDbContext fin) => _fin = fin;

    private Guid Org => _fin.TenantContext.OrganizationId;
    private IQueryable<EarlyPaymentTerm> Own() => _fin.Set<EarlyPaymentTerm>().Where(t => t.OrganizationId == Org);

    public async Task<IReadOnlyList<EarlyPaymentTermModel>> ListAsync(CancellationToken ct = default) =>
        (await Own().AsNoTracking().OrderBy(t => t.Code).ToListAsync(ct)).Select(ToModel).ToList();

    public async Task<EarlyPaymentTermModel> CreateAsync(EarlyPaymentTermRequest request, ApActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Code(request.Code) ?? throw new BadRequestException("Code is required.");
        var name = Name(request.Name) ?? throw new BadRequestException("Name is required.");
        var pct  = request.DiscountPercent ?? throw new BadRequestException("DiscountPercent is required.");
        var days = request.DiscountDays ?? throw new BadRequestException("DiscountDays is required.");
        var net  = request.NetDays ?? throw new BadRequestException("NetDays is required.");
        if (PayablesMath.TermProblem(pct, days, net) is { } problem) throw new BadRequestException(problem);

        await UniqueCodeAsync(code, null, ct);
        var term = new EarlyPaymentTerm
        {
            OrganizationId = Org, Code = code, Name = name, DiscountPercent = pct, DiscountDays = days, NetDays = net,
            IsActive = request.IsActive ?? true, CreatedAt = DateTime.UtcNow, CreatedBy = actor.UserId, CreatedByName = actor.UserName
        };
        _fin.Set<EarlyPaymentTerm>().Add(term);
        await SaveAsync(code, ct);
        return ToModel(term);
    }

    public async Task<EarlyPaymentTermModel> UpdateAsync(Guid uuid, EarlyPaymentTermRequest request, ApActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var term = await Own().FirstOrDefaultAsync(t => t.Uuid == uuid, ct) ?? throw new NotFoundException("Early payment term", uuid);

        if (Code(request.Code) is { } code && code != term.Code)
        {
            await UniqueCodeAsync(code, term.Id, ct);
            term.Code = code;
        }
        if (Name(request.Name) is { } name) term.Name = name;

        var pct  = request.DiscountPercent ?? term.DiscountPercent;
        var days = request.DiscountDays ?? term.DiscountDays;
        var net  = request.NetDays ?? term.NetDays;
        if (PayablesMath.TermProblem(pct, days, net) is { } problem) throw new BadRequestException(problem);
        term.DiscountPercent = pct;
        term.DiscountDays    = days;
        term.NetDays         = net;
        if (request.IsActive is { } active) term.IsActive = active;

        term.ModifiedAt     = DateTime.UtcNow;
        term.ModifiedBy     = actor.UserId;
        term.ModifiedByName = actor.UserName;
        await SaveAsync(term.Code, ct);
        return ToModel(term);
    }

    private static string? Code(string? code)
    {
        var c = code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(c)) return null;
        return c.Length > CodeMaxLength ? throw new BadRequestException($"Code can be at most {CodeMaxLength} characters.") : c;
    }

    private static string? Name(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) return null;
        return n.Length > NameMaxLength ? throw new BadRequestException($"Name can be at most {NameMaxLength} characters.") : n;
    }

    private async Task UniqueCodeAsync(string code, int? exceptId, CancellationToken ct)
    {
        if (await Own().AnyAsync(t => t.Code == code && (exceptId == null || t.Id != exceptId), ct))
            throw new ConflictException($"An early payment term with code {code} already exists.");
    }

    private async Task SaveAsync(string code, CancellationToken ct)
    {
        try { await _fin.SaveChangesAsync(ct); }
        catch (DbUpdateException) when (_fin.Database.IsRelational())
        {
            throw new ConflictException($"An early payment term with code {code} already exists.");
        }
    }

    internal static EarlyPaymentTermModel ToModel(EarlyPaymentTerm t) =>
        new(t.Uuid, t.Code, t.Name, t.DiscountPercent, t.DiscountDays, t.NetDays, t.IsActive);
}

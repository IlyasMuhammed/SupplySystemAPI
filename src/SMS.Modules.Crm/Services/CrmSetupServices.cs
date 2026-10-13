using Microsoft.EntityFrameworkCore;
using SMS.Modules.Crm.Data;
using SMS.Modules.Crm.Domain;
using SMS.Modules.Crm.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Crm.Services;

// A41 C2 / C3 / C5 — lead sources, campaigns, sales teams and pipeline stages (API-CONTRACT §2). Every query filters the
// caller's organization explicitly.

public interface ILeadSourceService
{
    Task<IReadOnlyList<LeadSourceModel>> GetAllAsync(CrmCaller caller, bool includeInactive, CancellationToken ct = default);
    Task<LeadSourceModel> CreateAsync(CrmCaller caller, LeadSourceUpsert req, CancellationToken ct = default);
    Task<LeadSourceModel> UpdateAsync(CrmCaller caller, Guid uuid, LeadSourceUpsert req, CancellationToken ct = default);
}

internal sealed class LeadSourceService : ILeadSourceService
{
    private readonly CrmDbContext _db;
    private readonly ICrmSeeder _seeder;

    public LeadSourceService(CrmDbContext db, ICrmSeeder seeder)
    {
        _db = db;
        _seeder = seeder;
    }

    public async Task<IReadOnlyList<LeadSourceModel>> GetAllAsync(CrmCaller caller, bool includeInactive, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        var q = _db.LeadSources.AsNoTracking().Where(s => s.OrganizationId == caller.OrganizationId);
        if (!includeInactive) q = q.Where(s => s.IsActive);
        return (await q.OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<LeadSourceModel> CreateAsync(CrmCaller caller, LeadSourceUpsert req, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        var code = CrmText.Code(req.Code);
        if (await _db.LeadSources.AnyAsync(s => s.OrganizationId == caller.OrganizationId && s.Code == code, ct))
            throw new ConflictException($"A lead source with code {code} already exists.");
        var max = await _db.LeadSources.Where(s => s.OrganizationId == caller.OrganizationId).MaxAsync(s => (int?)s.SortOrder, ct) ?? 0;
        var source = new CrmLeadSource
        {
            OrganizationId = caller.OrganizationId, Code = code,
            Name = CrmText.Required(req.Name, 100, "Name"), Description = CrmText.Optional(req.Description, 500, "Description"),
            IsActive = req.IsActive ?? true, SortOrder = req.SortOrder ?? max + 10
        };
        _db.LeadSources.Add(source);
        await _db.SaveChangesAsync(ct);
        return Map(source);
    }

    public async Task<LeadSourceModel> UpdateAsync(CrmCaller caller, Guid uuid, LeadSourceUpsert req, CancellationToken ct = default)
    {
        var source = await _db.LeadSources.FirstOrDefaultAsync(s => s.OrganizationId == caller.OrganizationId && s.Uuid == uuid, ct)
            ?? throw new NotFoundException("Lead source not found.");
        if (!string.IsNullOrWhiteSpace(req.Code))
        {
            var code = CrmText.Code(req.Code);
            if (code != source.Code)
            {
                if (source.IsSystem) throw new BadRequestException("The code of a built-in lead source cannot be changed.");
                if (await _db.LeadSources.AnyAsync(s => s.OrganizationId == caller.OrganizationId && s.Code == code && s.Id != source.Id, ct))
                    throw new ConflictException($"A lead source with code {code} already exists.");
                source.Code = code;
            }
        }
        if (req.Name is not null) source.Name = CrmText.Required(req.Name, 100, "Name");
        if (req.Description is not null) source.Description = CrmText.Optional(req.Description, 500, "Description");
        if (req.IsActive is { } active) source.IsActive = active;
        if (req.SortOrder is { } order) source.SortOrder = order;
        await _db.SaveChangesAsync(ct);
        return Map(source);
    }

    internal static LeadSourceModel Map(CrmLeadSource s) => new()
    {
        Uuid = s.Uuid, Code = s.Code, Name = s.Name, Description = s.Description, IsActive = s.IsActive, SortOrder = s.SortOrder, IsSystem = s.IsSystem
    };
}

public interface ICampaignService
{
    Task<PaginatedResponse<CampaignModel>> ListAsync(CrmCaller caller, CampaignListFilter filter, CancellationToken ct = default);
    Task<CampaignModel> GetByIdAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<CampaignModel> CreateAsync(CrmCaller caller, CampaignUpsert req, CancellationToken ct = default);
    Task<CampaignModel> UpdateAsync(CrmCaller caller, Guid uuid, CampaignUpsert req, CancellationToken ct = default);
    Task DeleteAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
}

internal sealed class CampaignService : ICampaignService
{
    private readonly CrmDbContext _db;
    private readonly ICrmSeeder _seeder;
    private readonly CrmCurrency _currency;
    private readonly CrmDirectory _directory;

    public CampaignService(CrmDbContext db, ICrmSeeder seeder, CrmCurrency currency, CrmDirectory directory)
    {
        _db = db;
        _seeder = seeder;
        _currency = currency;
        _directory = directory;
    }

    private IQueryable<CrmCampaign> Own(CrmCaller c) => _db.Campaigns.Where(x => x.OrganizationId == c.OrganizationId && !x.IsDeleted);

    public async Task<PaginatedResponse<CampaignModel>> ListAsync(CrmCaller caller, CampaignListFilter filter, CancellationToken ct = default)
    {
        var q = Own(caller).AsNoTracking().Include(x => x.LeadSource).AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim().ToLower();
            q = q.Where(x => x.Name.ToLower().Contains(s) || x.Code.ToLower().Contains(s));
        }
        if (CrmText.OneOf(filter.Status, CampaignStatuses.All, "Status") is { } status) q = q.Where(x => x.Status == status);
        if (CrmText.OneOf(filter.Type, CampaignTypes.All, "Type") is { } type) q = q.Where(x => x.CampaignType == type);
        var (page, size) = CrmText.Paging(filter.Page, filter.PageSize);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(x => x.StartDate ?? x.CreatedAt).ThenBy(x => x.Name)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var users = await _directory.UsersAsync(caller.OrganizationId, rows.Select(r => (int?)r.CreatedBy), ct);
        return new PaginatedResponse<CampaignModel>
        {
            Data = rows.Select(r => Map(r, users.GetValueOrDefault(r.CreatedBy)?.Name)).ToList(),
            TotalRecords = total, Page = page, PageSize = size, TotalPages = (int)Math.Ceiling(total / (double)size)
        };
    }

    public async Task<CampaignModel> GetByIdAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var c = await Own(caller).AsNoTracking().Include(x => x.LeadSource).FirstOrDefaultAsync(x => x.Uuid == uuid, ct)
            ?? throw new NotFoundException("Campaign not found.");
        var model = Map(c, await _directory.UserNameAsync(caller.OrganizationId, c.CreatedBy, ct));
        model.LeadsByStatus = await _db.Leads.Where(l => l.OrganizationId == caller.OrganizationId && l.CampaignId == c.Id && !l.IsDeleted)
            .GroupBy(l => l.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        model.OpportunityCount = await _db.Opportunities.CountAsync(o => o.OrganizationId == caller.OrganizationId && o.CampaignId == c.Id && !o.IsDeleted, ct);
        return model;
    }

    public async Task<CampaignModel> CreateAsync(CrmCaller caller, CampaignUpsert req, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        var code = CrmText.Code(req.Code);
        if (await _db.Campaigns.AnyAsync(x => x.OrganizationId == caller.OrganizationId && x.Code == code, ct))
            throw new ConflictException($"A campaign with code {code} already exists.");
        var campaign = new CrmCampaign
        {
            OrganizationId = caller.OrganizationId, Code = code, CreatedBy = caller.UserId,
            Status = CrmText.OneOf(req.Status, CampaignStatuses.All, "Status") ?? CampaignStatuses.Draft
        };
        await ApplyAsync(caller, campaign, req, ct);
        _db.Campaigns.Add(campaign);
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(caller, campaign.Uuid, ct);
    }

    public async Task<CampaignModel> UpdateAsync(CrmCaller caller, Guid uuid, CampaignUpsert req, CancellationToken ct = default)
    {
        var campaign = await Own(caller).FirstOrDefaultAsync(x => x.Uuid == uuid, ct) ?? throw new NotFoundException("Campaign not found.");
        if (!string.IsNullOrWhiteSpace(req.Code))
        {
            var code = CrmText.Code(req.Code);
            if (code != campaign.Code && await _db.Campaigns.AnyAsync(x => x.OrganizationId == caller.OrganizationId && x.Code == code && x.Id != campaign.Id, ct))
                throw new ConflictException($"A campaign with code {code} already exists.");
            campaign.Code = code;
        }
        if (CrmText.OneOf(req.Status, CampaignStatuses.All, "Status") is { } status && status != campaign.Status)
        {
            if (!CampaignStatuses.CanMove(campaign.Status, status))
                throw new BadRequestException($"A {campaign.Status} campaign cannot be moved to {status}.");
            campaign.Status = status;
        }
        await ApplyAsync(caller, campaign, req, ct);
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(caller, campaign.Uuid, ct);
    }

    public async Task DeleteAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var campaign = await Own(caller).FirstOrDefaultAsync(x => x.Uuid == uuid, ct) ?? throw new NotFoundException("Campaign not found.");
        campaign.IsDeleted = true;
        await _db.SaveChangesAsync(ct);
    }

    private async Task ApplyAsync(CrmCaller caller, CrmCampaign c, CampaignUpsert r, CancellationToken ct)
    {
        c.Name = CrmText.Required(r.Name ?? c.Name, 200, "Name");
        c.Description = CrmText.Optional(r.Description, 2000, "Description");
        c.CampaignType = CrmText.OneOf(r.CampaignType, CampaignTypes.All, "Campaign type") ?? (string.IsNullOrEmpty(c.CampaignType) ? "GENERAL" : c.CampaignType);
        c.LeadSourceId = r.LeadSourceUuid is { } src
            ? await _db.LeadSources.Where(s => s.OrganizationId == caller.OrganizationId && s.Uuid == src).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct)
              ?? throw new BadRequestException("The lead source was not found.")
            : null;
        c.StartDate = r.StartDate?.Date;
        c.EndDate = r.EndDate?.Date;
        if (c.StartDate is { } s0 && c.EndDate is { } e0 && e0 < s0) throw new BadRequestException("The end date cannot be before the start date.");
        c.BudgetAmount = CrmText.Money(r.BudgetAmount, "Budget");
        c.CurrencyCode = await _currency.ValidateAsync(caller.OrganizationId, r.CurrencyCode, "Currency", ct);
        if (c.BudgetAmount is not null && c.CurrencyCode is null) throw new BadRequestException("Give the budget's currency.");
        if (r.ExpectedLeads is < 0) throw new BadRequestException("Expected leads cannot be negative.");
        c.ExpectedLeads = r.ExpectedLeads;
        c.ExternalReference = CrmText.Optional(r.ExternalReference, 200, "External reference");
        c.Notes = CrmText.Optional(r.Notes, 2000, "Notes");
    }

    internal static CampaignModel Map(CrmCampaign c, string? createdByName) => new()
    {
        Uuid = c.Uuid, Code = c.Code, Name = c.Name, Description = c.Description, CampaignType = c.CampaignType,
        LeadSourceUuid = c.LeadSource?.Uuid, LeadSourceName = c.LeadSource?.Name, Status = c.Status, StartDate = c.StartDate,
        EndDate = c.EndDate, BudgetAmount = c.BudgetAmount, CurrencyCode = c.CurrencyCode, ExpectedLeads = c.ExpectedLeads,
        ActualLeads = c.ActualLeads, ExternalReference = c.ExternalReference, Notes = c.Notes, CreatedBy = c.CreatedBy,
        CreatedByName = createdByName, CreatedAt = c.CreatedAt
    };
}

public interface ISalesTeamService
{
    Task<IReadOnlyList<SalesTeamModel>> ListAsync(CrmCaller caller, bool includeInactive, CancellationToken ct = default);
    Task<SalesTeamModel> GetByIdAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<SalesTeamModel> CreateAsync(CrmCaller caller, SalesTeamUpsert req, CancellationToken ct = default);
    Task<SalesTeamModel> UpdateAsync(CrmCaller caller, Guid uuid, SalesTeamUpsert req, CancellationToken ct = default);
    Task<SalesTeamModel> AddMemberAsync(CrmCaller caller, Guid uuid, SalesTeamMemberRequest req, CancellationToken ct = default);
    Task<SalesTeamModel> RemoveMemberAsync(CrmCaller caller, Guid uuid, int userId, CancellationToken ct = default);
}

internal sealed class SalesTeamService : ISalesTeamService
{
    private readonly CrmDbContext _db;
    private readonly CrmDirectory _directory;

    public SalesTeamService(CrmDbContext db, CrmDirectory directory)
    {
        _db = db;
        _directory = directory;
    }

    private IQueryable<CrmSalesTeam> Own(CrmCaller c) => _db.SalesTeams.Where(t => t.OrganizationId == c.OrganizationId);

    public async Task<IReadOnlyList<SalesTeamModel>> ListAsync(CrmCaller caller, bool includeInactive, CancellationToken ct = default)
    {
        var q = Own(caller).AsNoTracking().Include(t => t.Members).Include(t => t.DefaultCampaign).AsQueryable();
        if (!includeInactive) q = q.Where(t => t.IsActive);
        var teams = await q.OrderBy(t => t.Name).ToListAsync(ct);
        return await MapAsync(caller, teams, ct);
    }

    public async Task<SalesTeamModel> GetByIdAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var team = await Own(caller).AsNoTracking().Include(t => t.Members).Include(t => t.DefaultCampaign)
            .FirstOrDefaultAsync(t => t.Uuid == uuid, ct) ?? throw new NotFoundException("Sales team not found.");
        return (await MapAsync(caller, [team], ct))[0];
    }

    public async Task<SalesTeamModel> CreateAsync(CrmCaller caller, SalesTeamUpsert req, CancellationToken ct = default)
    {
        var code = CrmText.Code(req.Code);
        if (await Own(caller).AnyAsync(t => t.Code == code, ct)) throw new ConflictException($"A sales team with code {code} already exists.");
        var team = new CrmSalesTeam { OrganizationId = caller.OrganizationId, Code = code };
        await ApplyAsync(caller, team, req, ct);
        _db.SalesTeams.Add(team);
        await _db.SaveChangesAsync(ct);
        await SyncLeaderAsync(caller, team, ct);
        return await GetByIdAsync(caller, team.Uuid, ct);
    }

    public async Task<SalesTeamModel> UpdateAsync(CrmCaller caller, Guid uuid, SalesTeamUpsert req, CancellationToken ct = default)
    {
        var team = await Own(caller).FirstOrDefaultAsync(t => t.Uuid == uuid, ct) ?? throw new NotFoundException("Sales team not found.");
        if (!string.IsNullOrWhiteSpace(req.Code))
        {
            var code = CrmText.Code(req.Code);
            if (code != team.Code && await Own(caller).AnyAsync(t => t.Code == code && t.Id != team.Id, ct))
                throw new ConflictException($"A sales team with code {code} already exists.");
            team.Code = code;
        }
        await ApplyAsync(caller, team, req, ct);
        await _db.SaveChangesAsync(ct);
        await SyncLeaderAsync(caller, team, ct);
        return await GetByIdAsync(caller, team.Uuid, ct);
    }

    public async Task<SalesTeamModel> AddMemberAsync(CrmCaller caller, Guid uuid, SalesTeamMemberRequest req, CancellationToken ct = default)
    {
        var team = await Own(caller).Include(t => t.Members).FirstOrDefaultAsync(t => t.Uuid == uuid, ct)
            ?? throw new NotFoundException("Sales team not found.");
        if (req.UserId <= 0) throw new BadRequestException("Choose the user to add.");
        await _directory.RequireActiveUserAsync(caller.OrganizationId, req.UserId, "team member", ct);
        var role = CrmText.OneOf(req.Role, TeamMemberRoles.All, "Role") ?? TeamMemberRoles.Member;
        var member = team.Members.FirstOrDefault(m => m.UserId == req.UserId);
        if (member is not null && member.IsActive && member.Role == role)
            throw new ConflictException("This user is already a member of the team.");
        if (member is null)
        {
            member = new CrmSalesTeamMember { OrganizationId = caller.OrganizationId, UserId = req.UserId };
            team.Members.Add(member);
        }
        member.IsActive = true;
        member.Role = role;
        if (role == TeamMemberRoles.Leader) team.TeamLeaderUserId = req.UserId;
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(caller, uuid, ct);
    }

    public async Task<SalesTeamModel> RemoveMemberAsync(CrmCaller caller, Guid uuid, int userId, CancellationToken ct = default)
    {
        var team = await Own(caller).Include(t => t.Members).FirstOrDefaultAsync(t => t.Uuid == uuid, ct)
            ?? throw new NotFoundException("Sales team not found.");
        var member = team.Members.FirstOrDefault(m => m.UserId == userId && m.IsActive)
            ?? throw new NotFoundException("This user is not a member of the team.");
        member.IsActive = false;
        if (team.TeamLeaderUserId == userId) team.TeamLeaderUserId = null;
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(caller, uuid, ct);
    }

    private async Task ApplyAsync(CrmCaller caller, CrmSalesTeam t, SalesTeamUpsert r, CancellationToken ct)
    {
        t.Name = CrmText.Required(r.Name ?? t.Name, 100, "Name");
        t.Description = CrmText.Optional(r.Description, 500, "Description");
        if (r.IsActive is { } active) t.IsActive = active;
        if (r.TeamLeaderUserId is { } leader && leader > 0)
        {
            if (leader != t.TeamLeaderUserId) await _directory.RequireActiveUserAsync(caller.OrganizationId, leader, "team leader", ct);
            t.TeamLeaderUserId = leader;
        }
        else t.TeamLeaderUserId = null;
        t.DefaultCampaignId = r.DefaultCampaignUuid is { } camp
            ? await _db.Campaigns.Where(c => c.OrganizationId == caller.OrganizationId && c.Uuid == camp && !c.IsDeleted).Select(c => (int?)c.Id).FirstOrDefaultAsync(ct)
              ?? throw new BadRequestException("The default campaign was not found.")
            : null;
    }

    /// <summary>The leader is always an active LEADER member; a former leader stays a member.</summary>
    private async Task SyncLeaderAsync(CrmCaller caller, CrmSalesTeam team, CancellationToken ct)
    {
        var members = await _db.SalesTeamMembers.Where(m => m.OrganizationId == caller.OrganizationId && m.SalesTeamId == team.Id).ToListAsync(ct);
        foreach (var m in members.Where(m => m.Role == TeamMemberRoles.Leader && m.UserId != team.TeamLeaderUserId))
            m.Role = TeamMemberRoles.Member;
        if (team.TeamLeaderUserId is { } leader)
        {
            var m = members.FirstOrDefault(x => x.UserId == leader);
            if (m is null)
                _db.SalesTeamMembers.Add(new CrmSalesTeamMember
                {
                    OrganizationId = caller.OrganizationId, SalesTeamId = team.Id, UserId = leader, Role = TeamMemberRoles.Leader
                });
            else
            {
                m.Role = TeamMemberRoles.Leader;
                m.IsActive = true;
            }
        }
        if (_db.ChangeTracker.HasChanges()) await _db.SaveChangesAsync(ct);
    }

    private async Task<List<SalesTeamModel>> MapAsync(CrmCaller caller, List<CrmSalesTeam> teams, CancellationToken ct)
    {
        var users = await _directory.UsersAsync(caller.OrganizationId,
            teams.SelectMany(t => t.Members.Select(m => (int?)m.UserId).Append(t.TeamLeaderUserId)), ct);
        var ids = teams.Select(t => t.Id).ToList();
        var withStages = await _db.PipelineStages.Where(s => s.OrganizationId == caller.OrganizationId && s.SalesTeamId != null
                && ids.Contains(s.SalesTeamId.Value) && s.IsActive)
            .Select(s => s.SalesTeamId!.Value).Distinct().ToListAsync(ct);
        return teams.Select(t => new SalesTeamModel
        {
            Uuid = t.Uuid, Code = t.Code, Name = t.Name, Description = t.Description, TeamLeaderUserId = t.TeamLeaderUserId,
            TeamLeaderName = t.TeamLeaderUserId is { } l ? users.GetValueOrDefault(l)?.Name : null, IsActive = t.IsActive,
            DefaultCampaignUuid = t.DefaultCampaign?.Uuid, DefaultCampaignName = t.DefaultCampaign?.Name,
            HasOwnStages = withStages.Contains(t.Id),
            Members = t.Members.Where(m => m.IsActive).OrderByDescending(m => m.Role == TeamMemberRoles.Leader)
                .ThenBy(m => users.GetValueOrDefault(m.UserId)?.Name)
                .Select(m => new SalesTeamMemberModel
                {
                    UserId = m.UserId, Name = users.GetValueOrDefault(m.UserId)?.Name ?? $"User {m.UserId}", Role = m.Role,
                    IsActive = users.GetValueOrDefault(m.UserId)?.IsActive ?? true, JoinedAt = m.JoinedAt
                }).ToList()
        }).ToList();
    }
}

public interface IPipelineStageService
{
    /// <summary>The stages a team uses (its own when it has any, else the organization's), in sequence order.</summary>
    Task<IReadOnlyList<PipelineStageModel>> GetStagesAsync(CrmCaller caller, PipelineStageFilter filter, CancellationToken ct = default);
    Task<PipelineStageModel> CreateAsync(CrmCaller caller, PipelineStageUpsert req, CancellationToken ct = default);
    Task<PipelineStageModel> UpdateAsync(CrmCaller caller, Guid uuid, PipelineStageUpsert req, CancellationToken ct = default);
    Task DeactivateAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
}

internal sealed class PipelineStageService : IPipelineStageService
{
    private readonly CrmDbContext _db;
    private readonly ICrmSeeder _seeder;

    public PipelineStageService(CrmDbContext db, ICrmSeeder seeder)
    {
        _db = db;
        _seeder = seeder;
    }

    public async Task<IReadOnlyList<PipelineStageModel>> GetStagesAsync(CrmCaller caller, PipelineStageFilter filter, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        var type = CrmText.OneOf(filter.StageType, StageTypes.All, "Stage type");
        int? teamId = null;
        if (filter.SalesTeamUuid is { } teamUuid)
            teamId = await _db.SalesTeams.Where(t => t.OrganizationId == caller.OrganizationId && t.Uuid == teamUuid).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Sales team not found.");
        var stages = await CrmPipeline.ForTeamAsync(_db, caller.OrganizationId, teamId, type, filter.IncludeInactive, ct);
        return stages.Select(Map).ToList();
    }

    public async Task<PipelineStageModel> CreateAsync(CrmCaller caller, PipelineStageUpsert req, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        var code = CrmText.Code(req.Code);
        var type = CrmText.OneOf(req.StageType, StageTypes.All, "Stage type") ?? throw new BadRequestException("Stage type is required.");
        int? teamId = null;
        if (req.SalesTeamUuid is { } teamUuid)
            teamId = await _db.SalesTeams.Where(t => t.OrganizationId == caller.OrganizationId && t.Uuid == teamUuid).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct)
                ?? throw new BadRequestException("The sales team was not found.");
        if (await _db.PipelineStages.AnyAsync(s => s.OrganizationId == caller.OrganizationId && s.SalesTeamId == teamId && s.Code == code, ct))
            throw new ConflictException($"This pipeline already has a stage with code {code}.");
        var stage = new CrmPipelineStage { OrganizationId = caller.OrganizationId, Code = code, StageType = type, SalesTeamId = teamId };
        Apply(stage, req);
        _db.PipelineStages.Add(stage);
        await _db.SaveChangesAsync(ct);
        return Map(await _db.PipelineStages.AsNoTracking().Include(s => s.SalesTeam).FirstAsync(s => s.Id == stage.Id, ct));
    }

    public async Task<PipelineStageModel> UpdateAsync(CrmCaller caller, Guid uuid, PipelineStageUpsert req, CancellationToken ct = default)
    {
        var stage = await _db.PipelineStages.Include(s => s.SalesTeam)
            .FirstOrDefaultAsync(s => s.OrganizationId == caller.OrganizationId && s.Uuid == uuid, ct) ?? throw new NotFoundException("Pipeline stage not found.");
        if (CrmText.OneOf(req.StageType, StageTypes.All, "Stage type") is { } type && type != stage.StageType)
        {
            if (await InUseAsync(caller, stage, ct)) throw new BadRequestException("The stage's type cannot change while records are in it.");
            stage.StageType = type;
        }
        if (!string.IsNullOrWhiteSpace(req.Code))
        {
            var code = CrmText.Code(req.Code);
            if (code != stage.Code && await _db.PipelineStages.AnyAsync(s => s.OrganizationId == caller.OrganizationId
                    && s.SalesTeamId == stage.SalesTeamId && s.Code == code && s.Id != stage.Id, ct))
                throw new ConflictException($"This pipeline already has a stage with code {code}.");
            stage.Code = code;
        }
        Apply(stage, req);
        stage.IsActive = true;
        await _db.SaveChangesAsync(ct);
        return Map(stage);
    }

    public async Task DeactivateAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var stage = await _db.PipelineStages.FirstOrDefaultAsync(s => s.OrganizationId == caller.OrganizationId && s.Uuid == uuid, ct)
            ?? throw new NotFoundException("Pipeline stage not found.");
        if (await InUseAsync(caller, stage, ct))
            throw new ConflictException("Records are still in this stage. Move them to another stage first.");
        stage.IsActive = false;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Open leads / open opportunities currently in the stage.</summary>
    private async Task<bool> InUseAsync(CrmCaller caller, CrmPipelineStage stage, CancellationToken ct)
    {
        var open = LeadStatuses.Open.ToArray();
        return await _db.Leads.AnyAsync(l => l.OrganizationId == caller.OrganizationId && l.PipelineStageId == stage.Id && !l.IsDeleted
                   && open.Contains(l.Status), ct)
            || await _db.Opportunities.AnyAsync(o => o.OrganizationId == caller.OrganizationId && o.PipelineStageId == stage.Id && !o.IsDeleted
                   && o.Status == OpportunityStatuses.Open, ct);
    }

    private static void Apply(CrmPipelineStage s, PipelineStageUpsert r)
    {
        s.Name = CrmText.Required(r.Name ?? s.Name, 100, "Name");
        s.Description = CrmText.Optional(r.Description, 500, "Description");
        if (r.Sequence is { } seq) s.Sequence = seq;
        if (r.WinProbability is { } p)
        {
            if (p is < 0 or > 100) throw new BadRequestException("Win probability must be between 0 and 100.");
            s.WinProbability = p;
        }
        if (r.IsWon is { } won) s.IsWon = won;
        if (r.IsLost is { } lost) s.IsLost = lost;
        if (s.IsWon && s.IsLost) throw new BadRequestException("A stage cannot be both the won and the lost stage.");
        if ((s.IsWon || s.IsLost) && s.StageType != StageTypes.Opportunity)
            throw new BadRequestException("Only an opportunity stage can be the won or lost stage.");
    }

    internal static PipelineStageModel Map(CrmPipelineStage s) => new()
    {
        Uuid = s.Uuid, Code = s.Code, Name = s.Name, StageType = s.StageType, SalesTeamUuid = s.SalesTeam?.Uuid, SalesTeamName = s.SalesTeam?.Name,
        Sequence = s.Sequence, WinProbability = s.WinProbability, IsWon = s.IsWon, IsLost = s.IsLost, Description = s.Description, IsActive = s.IsActive
    };
}

/// <summary>D-10 / §7.1 — which stages a record uses: its team's own stages of that type when the team has any, else the organization's.</summary>
internal static class CrmPipeline
{
    public static async Task<List<CrmPipelineStage>> ForTeamAsync(CrmDbContext db, Guid org, int? teamId, string? stageType,
        bool includeInactive = false, CancellationToken ct = default)
    {
        var all = await db.PipelineStages.AsNoTracking().Include(s => s.SalesTeam)
            .Where(s => s.OrganizationId == org && (s.SalesTeamId == null || s.SalesTeamId == teamId)
                && (stageType == null || s.StageType == stageType))
            .ToListAsync(ct);
        var active = all.Where(s => includeInactive || s.IsActive).ToList();
        IEnumerable<CrmPipelineStage> chosen;
        if (teamId is null) chosen = active.Where(s => s.SalesTeamId == null);
        else
        {
            // Per type: the team's own set replaces the organization's set of that type.
            chosen = active.GroupBy(s => s.StageType).SelectMany(g =>
                g.Any(s => s.SalesTeamId == teamId && s.IsActive) ? g.Where(s => s.SalesTeamId == teamId) : g.Where(s => s.SalesTeamId == null));
        }
        return chosen.OrderBy(s => s.StageType == StageTypes.Lead ? 0 : 1).ThenBy(s => s.Sequence).ThenBy(s => s.Name).ToList();
    }

    /// <summary>The first open (not won/lost) active stage of the type in the record's pipeline.</summary>
    public static async Task<CrmPipelineStage?> FirstAsync(CrmDbContext db, Guid org, int? teamId, string stageType, CancellationToken ct = default) =>
        (await ForTeamAsync(db, org, teamId, stageType, false, ct)).FirstOrDefault(s => !s.IsWon && !s.IsLost);
}

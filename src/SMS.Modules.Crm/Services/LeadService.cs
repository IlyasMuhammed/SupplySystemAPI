using Microsoft.EntityFrameworkCore;
using SMS.Modules.Crm.Data;
using SMS.Modules.Crm.Domain;
using SMS.Modules.Crm.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Crm.Services;

/// <summary>A41 C4 — leads (API-CONTRACT §3). Conversion is <see cref="ILeadConversionService"/>.</summary>
public interface ILeadService
{
    Task<PaginatedResponse<LeadListItem>> ListAsync(CrmCaller caller, LeadListFilter filter, CancellationToken ct = default);
    Task<LeadDetail> GetByIdAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<LeadDetail> CreateAsync(CrmCaller caller, LeadUpsert req, CancellationToken ct = default);
    Task<LeadDetail> UpdateAsync(CrmCaller caller, Guid uuid, LeadUpsert req, CancellationToken ct = default);
    Task<LeadDetail> AssignAsync(CrmCaller caller, Guid uuid, LeadAssignRequest req, CancellationToken ct = default);
    Task<LeadDetail> ChangeStatusAsync(CrmCaller caller, Guid uuid, string? status, string? reason, CancellationToken ct = default);
    Task<LeadDetail> QualifyAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<LeadDetail> DisqualifyAsync(CrmCaller caller, Guid uuid, string? reason, CancellationToken ct = default);
    Task<LeadDetail> ReopenAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<LeadDetail> MoveStageAsync(CrmCaller caller, Guid uuid, Guid stageUuid, CancellationToken ct = default);
    Task DeleteAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<KanbanBoard<LeadListItem>> GetKanbanAsync(CrmCaller caller, LeadKanbanFilter filter, CancellationToken ct = default);
    Task<LeadDuplicateResult> DuplicateCheckAsync(CrmCaller caller, LeadDuplicateQuery query, CancellationToken ct = default);
    Task<LeadDuplicateResult> DuplicateCheckForLeadAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<CrossReferenceResult> GetCrossReferencesAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
}

internal sealed partial class LeadService : ILeadService
{
    internal const string NumberPrefix = "LD";
    private const int MaxDuplicates = 20;

    private readonly CrmDbContext _db;
    private readonly ICrmSeeder _seeder;
    private readonly IDocumentNumberGenerator _numbers;
    private readonly CrmDirectory _directory;
    private readonly CrmCurrency _currency;
    private readonly ICrmEventPublisher _events;
    private readonly IModuleGate? _gate;

    public LeadService(CrmDbContext db, ICrmSeeder seeder, IDocumentNumberGenerator numbers, CrmDirectory directory, CrmCurrency currency,
        ICrmEventPublisher events, IModuleGate? gate = null)
    {
        _db = db;
        _seeder = seeder;
        _numbers = numbers;
        _directory = directory;
        _currency = currency;
        _events = events;
        _gate = gate;
    }

    private IQueryable<CrmLead> Own(CrmCaller c) => _db.Leads.Where(l => l.OrganizationId == c.OrganizationId && !l.IsDeleted);

    private IQueryable<CrmLead> WithRefs(IQueryable<CrmLead> q) =>
        q.Include(l => l.LeadSource).Include(l => l.Campaign).Include(l => l.SalesTeam).Include(l => l.PipelineStage);

    internal async Task<CrmLead> LoadAsync(CrmCaller caller, Guid uuid, CancellationToken ct) =>
        await WithRefs(Own(caller)).FirstOrDefaultAsync(l => l.Uuid == uuid, ct) ?? throw new NotFoundException("Lead not found.");

    // ── Ownership (D-5) ──────────────────────────────────────────────────────────

    internal static bool Owns(CrmCaller caller, CrmLead lead) =>
        caller.IsManager || lead.AssignedToUserId == caller.UserId || (lead.AssignedToUserId is null && lead.CreatedByUserId == caller.UserId);

    internal static void RequireOwnership(CrmCaller caller, CrmLead lead)
    {
        if (!Owns(caller, lead)) throw new ForbiddenException("You can only change leads assigned to you.");
    }

    internal static void RequireNotConverted(CrmLead lead)
    {
        if (lead.Status == LeadStatuses.Converted)
            throw new ConflictException("A converted lead is closed and cannot be changed: work on its opportunity instead.");
    }

    // ── Reads ───────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<LeadListItem>> ListAsync(CrmCaller caller, LeadListFilter filter, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        var q = WithRefs(Own(caller).AsNoTracking());
        q = await ApplyFilterAsync(caller, q, filter.Search, filter.SalesTeamUuid, filter.AssignedToUserId, filter.Mine, filter.Rating, filter.From, filter.To, ct);
        if (filter.Unassigned) q = q.Where(l => l.AssignedToUserId == null);
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            var statuses = filter.Status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => CrmText.OneOf(s, LeadStatuses.All, "Status")!).ToArray();
            q = q.Where(l => statuses.Contains(l.Status));
        }
        if (filter.LeadSourceUuid is { } src) q = q.Where(l => l.LeadSource != null && l.LeadSource.Uuid == src);
        if (filter.CampaignUuid is { } camp) q = q.Where(l => l.Campaign != null && l.Campaign.Uuid == camp);

        var (page, size) = CrmText.Paging(filter.Page, filter.PageSize);
        var total = await q.CountAsync(ct);
        var desc = filter.SortOrder is null ? true : CrmText.Desc(filter.SortOrder);
        q = filter.SortField?.Trim().ToLowerInvariant() switch
        {
            "leadnumber"         => desc ? q.OrderByDescending(l => l.LeadNumber) : q.OrderBy(l => l.LeadNumber),
            "fullname"           => desc ? q.OrderByDescending(l => l.FullName) : q.OrderBy(l => l.FullName),
            "companyname"        => desc ? q.OrderByDescending(l => l.CompanyName) : q.OrderBy(l => l.CompanyName),
            "status"             => desc ? q.OrderByDescending(l => l.Status) : q.OrderBy(l => l.Status),
            "rating"             => desc ? q.OrderByDescending(l => l.Rating) : q.OrderBy(l => l.Rating),
            "estimatedrevenue"   => desc ? q.OrderByDescending(l => l.EstimatedRevenue) : q.OrderBy(l => l.EstimatedRevenue),
            "estimatedclosedate" => desc ? q.OrderByDescending(l => l.EstimatedCloseDate) : q.OrderBy(l => l.EstimatedCloseDate),
            _                    => desc ? q.OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id) : q.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
        };
        var rows = await q.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PaginatedResponse<LeadListItem>
        {
            Data = await MapListAsync(caller, rows, ct), TotalRecords = total, Page = page, PageSize = size,
            TotalPages = (int)Math.Ceiling(total / (double)size)
        };
    }

    private async Task<IQueryable<CrmLead>> ApplyFilterAsync(CrmCaller caller, IQueryable<CrmLead> q, string? search, Guid? teamUuid,
        int? assignedTo, bool mine, string? rating, DateTime? from, DateTime? to, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            q = q.Where(l => l.FullName.ToLower().Contains(s) || l.CompanyName.ToLower().Contains(s) || l.LeadNumber.ToLower().Contains(s)
                || l.Topic.ToLower().Contains(s) || (l.Email != null && l.Email.ToLower().Contains(s))
                || (l.Phone != null && l.Phone.Contains(s)) || (l.MobilePhone != null && l.MobilePhone.Contains(s)));
        }
        if (teamUuid is { } team)
        {
            var teamId = await _db.SalesTeams.Where(t => t.OrganizationId == caller.OrganizationId && t.Uuid == team).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);
            q = q.Where(l => l.SalesTeamId == teamId);
        }
        if (mine) q = q.Where(l => l.AssignedToUserId == caller.UserId);
        else if (assignedTo is { } user) q = q.Where(l => l.AssignedToUserId == user);
        if (CrmText.OneOf(rating, LeadRatings.All, "Rating") is { } r) q = q.Where(l => l.Rating == r);
        if (from is { } f) q = q.Where(l => l.CreatedAt >= f.Date);
        if (to is { } t) { var end = t.Date.AddDays(1); q = q.Where(l => l.CreatedAt < end); }
        return q;
    }

    public async Task<LeadDetail> GetByIdAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var lead = await WithRefs(Own(caller).AsNoTracking()).FirstOrDefaultAsync(l => l.Uuid == uuid, ct)
            ?? throw new NotFoundException("Lead not found.");
        return await MapDetailAsync(caller, lead, ct);
    }

    // ── Create / update ────────────────────────────────────────────────────────

    public async Task<LeadDetail> CreateAsync(CrmCaller caller, LeadUpsert req, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        var lead = new CrmLead
        {
            OrganizationId = caller.OrganizationId, CreatedByUserId = caller.UserId, UpdatedByUserId = caller.UserId,
            CreatedByName = caller.UserName, Status = LeadStatuses.New
        };
        await ApplyAsync(caller, lead, req, isCreate: true, ct);

        // D-5 — a rep's lead is theirs; someone else needs CRM_ASSIGN_LEAD.
        var assignee = req.AssignedToUserId is > 0 ? req.AssignedToUserId : caller.UserId > 0 ? caller.UserId : null;
        if (assignee is { } a && a != caller.UserId)
        {
            caller.Require(PermissionCodes.CRM_ASSIGN_LEAD, "You can only create leads assigned to yourself.");
            await _directory.RequireActiveUserAsync(caller.OrganizationId, a, "assignee", ct);
        }
        lead.AssignedToUserId = assignee;
        lead.AssignedAt = assignee is null ? null : DateTime.UtcNow;
        if (req.SalesTeamUuid is { } teamUuid) lead.SalesTeamId = await TeamIdAsync(caller, teamUuid, ct);
        lead.PipelineStageId = (await CrmPipeline.FirstAsync(_db, caller.OrganizationId, lead.SalesTeamId, StageTypes.Lead, ct))?.Id;

        lead.LeadNumber = await _numbers.NextAsync(NumberPrefix, DateTime.UtcNow, caller.OrganizationId, ct);
        _db.Leads.Add(lead);
        await _db.SaveChangesAsync(ct);
        AddHistory(caller, lead, HistoryKinds.Created, null, LeadStatuses.New, null);
        if (assignee is not null) AddHistory(caller, lead, HistoryKinds.Assignment, null, assignee.ToString(), null);
        await _db.SaveChangesAsync(ct);

        var sourceUuid = lead.LeadSourceId is { } sid ? await _db.LeadSources.Where(s => s.Id == sid).Select(s => (Guid?)s.Uuid).FirstOrDefaultAsync(ct) : null;
        var campaignUuid = lead.CampaignId is { } cid ? await _db.Campaigns.Where(c => c.Id == cid).Select(c => (Guid?)c.Uuid).FirstOrDefaultAsync(ct) : null;
        await _events.PublishAsync(new LeadCreatedEvent(lead.Uuid, lead.OrganizationId, sourceUuid, campaignUuid), ct);
        if (assignee is { } who && who != caller.UserId)
            await _events.PublishAsync(new LeadAssignedEvent(lead.Uuid, lead.OrganizationId, who, null, lead.LeadNumber, lead.Topic), ct);
        return await GetByIdAsync(caller, lead.Uuid, ct);
    }

    public async Task<LeadDetail> UpdateAsync(CrmCaller caller, Guid uuid, LeadUpsert req, CancellationToken ct = default)
    {
        var lead = await LoadAsync(caller, uuid, ct);
        RequireNotConverted(lead);
        RequireOwnership(caller, lead);
        await ApplyAsync(caller, lead, req, isCreate: false, ct);
        Touch(caller, lead);
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(caller, uuid, ct);
    }

    /// <summary>The editable fields (create + update). Assignment and team are not here after creation (assign).</summary>
    private async Task ApplyAsync(CrmCaller caller, CrmLead lead, LeadUpsert r, bool isCreate, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        lead.Topic = CrmText.Required(r.Topic, 200, "Topic");
        lead.Description = CrmText.Optional(r.Description, 4000, "Description");
        lead.Salutation = CrmText.Optional(r.Salutation, 10, "Salutation");
        lead.SetName(CrmText.Required(r.FirstName, 100, "First name"), CrmText.Required(r.LastName, 100, "Last name"));
        lead.JobTitle = CrmText.Optional(r.JobTitle, 100, "Job title");
        lead.Email = CrmText.Email(r.Email);
        lead.Phone = CrmText.Optional(r.Phone, 30, "Phone");
        lead.MobilePhone = CrmText.Optional(r.MobilePhone, 30, "Mobile phone");
        // BR-03 — an individual's lead carries the person's name as its company.
        lead.CompanyName = CrmText.Optional(r.CompanyName, 200, "Company name") ?? lead.FullName;
        lead.Website = CrmText.Optional(r.Website, 500, "Website");
        lead.AddressLine1 = CrmText.Optional(r.AddressLine1, 200, "Address line 1");
        lead.AddressLine2 = CrmText.Optional(r.AddressLine2, 200, "Address line 2");
        lead.City = CrmText.Optional(r.City, 100, "City");
        lead.StateProvince = CrmText.Optional(r.StateProvince, 100, "State / province");
        lead.PostalCode = CrmText.Optional(r.PostalCode, 20, "Postal code");
        lead.Country = CrmText.Optional(r.Country, 100, "Country");

        lead.LeadSourceId = r.LeadSourceUuid is { } src
            ? await _db.LeadSources.Where(s => s.OrganizationId == org && s.Uuid == src).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct)
              ?? throw new BadRequestException("The lead source was not found.")
            : null;

        var previousCampaign = lead.CampaignId;
        if (r.CampaignUuid is { } campUuid)
        {
            var campaign = await _db.Campaigns.FirstOrDefaultAsync(c => c.OrganizationId == org && c.Uuid == campUuid && !c.IsDeleted, ct)
                ?? throw new BadRequestException("The campaign was not found.");
            if (campaign.Id != previousCampaign)
            {
                await RequireCampaignsEnabledAsync(org, ct);
                campaign.ActualLeads++; // D-? — leads ever attributed to the campaign
            }
            lead.CampaignId = campaign.Id;
            lead.LeadSourceId ??= campaign.LeadSourceId;
        }
        else lead.CampaignId = null;

        lead.SourceMedium = CrmText.Optional(r.SourceMedium, 50, "Source medium");
        lead.SourceReference = CrmText.Optional(r.SourceReference, 200, "Source reference");
        lead.ExternalSourceSystem = CrmText.OneOf(r.ExternalSourceSystem, ExternalSourceSystems.All, "External source system");
        lead.ExternalSourceId = CrmText.Optional(r.ExternalSourceId, 200, "External source id");

        lead.BudgetAmount = CrmText.Money(r.BudgetAmount, "Budget");
        lead.BudgetCurrencyCode = await _currency.ValidateAsync(org, r.BudgetCurrencyCode, "Budget currency", ct);
        lead.BudgetConfirmed = r.BudgetConfirmed ?? (isCreate ? false : lead.BudgetConfirmed);
        lead.AuthorityConfirmed = r.AuthorityConfirmed ?? (isCreate ? false : lead.AuthorityConfirmed);
        lead.NeedIdentified = r.NeedIdentified ?? (isCreate ? false : lead.NeedIdentified);
        lead.PurchaseTimeframe = CrmText.OneOf(r.PurchaseTimeframe, PurchaseTimeframes.All, "Purchase timeframe");
        lead.Rating = CrmText.OneOf(r.Rating, LeadRatings.All, "Rating") ?? (isCreate ? LeadRatings.Warm : lead.Rating);
        lead.Priority = CrmText.OneOf(r.Priority, LeadPriorities.All, "Priority") ?? (isCreate ? LeadPriorities.Normal : lead.Priority);

        lead.EstimatedRevenue = CrmText.Money(r.EstimatedRevenue, "Estimated revenue");
        lead.EstimatedCloseDate = r.EstimatedCloseDate?.Date;
        lead.CurrencyCode = await _currency.ValidateAsync(org, r.CurrencyCode, "Currency", ct);
        if (lead.EstimatedRevenue is not null && lead.CurrencyCode is null)
            throw new BadRequestException("Give the estimated revenue's currency.");
        if (lead.BudgetAmount is not null && lead.BudgetCurrencyCode is null)
            lead.BudgetCurrencyCode = lead.CurrencyCode ?? throw new BadRequestException("Give the budget's currency.");
    }

    internal async Task RequireCampaignsEnabledAsync(Guid org, CancellationToken ct)
    {
        if (_gate is not null && !await _gate.IsEnabledAsync(org, CrmFeatureCodes.CampaignTracking, ct))
            throw new BadRequestException("Campaign tracking is switched off for the organization.");
    }

    private async Task<int> TeamIdAsync(CrmCaller caller, Guid teamUuid, CancellationToken ct) =>
        await _db.SalesTeams.Where(t => t.OrganizationId == caller.OrganizationId && t.Uuid == teamUuid && t.IsActive)
            .Select(t => (int?)t.Id).FirstOrDefaultAsync(ct) ?? throw new BadRequestException("The sales team was not found or is inactive.");

    // ── Assignment ─────────────────────────────────────────────────────────────

    public async Task<LeadDetail> AssignAsync(CrmCaller caller, Guid uuid, LeadAssignRequest req, CancellationToken ct = default)
    {
        var lead = await LoadAsync(caller, uuid, ct);
        RequireNotConverted(lead);
        var previous = lead.AssignedToUserId;
        var newUser = req.UserId is > 0 ? req.UserId : null;
        if (newUser is { } u && u != previous) await _directory.RequireActiveUserAsync(caller.OrganizationId, u, "assignee", ct);

        if (req.SalesTeamUuid is { } teamUuid)
        {
            var teamId = await TeamIdAsync(caller, teamUuid, ct);
            if (teamId != lead.SalesTeamId)
            {
                lead.SalesTeamId = teamId;
                await KeepStageInPipelineAsync(caller, lead, ct);
            }
        }

        if (newUser != previous)
        {
            lead.PreviousOwnerUserId = previous;
            lead.AssignedToUserId = newUser;
            lead.AssignedAt = newUser is null ? null : DateTime.UtcNow;
            AddHistory(caller, lead, HistoryKinds.Assignment, previous?.ToString(), newUser?.ToString(), null);
        }
        Touch(caller, lead);
        await _db.SaveChangesAsync(ct);
        if (newUser != previous)
            await _events.PublishAsync(new LeadAssignedEvent(lead.Uuid, lead.OrganizationId, newUser, previous, lead.LeadNumber, lead.Topic), ct);
        return await GetByIdAsync(caller, uuid, ct);
    }

    /// <summary>After a team change: a stage that is not in the new team's lead pipeline becomes that pipeline's first stage.</summary>
    private async Task KeepStageInPipelineAsync(CrmCaller caller, CrmLead lead, CancellationToken ct)
    {
        var stages = await CrmPipeline.ForTeamAsync(_db, caller.OrganizationId, lead.SalesTeamId, StageTypes.Lead, false, ct);
        if (lead.PipelineStageId is { } id && stages.Any(s => s.Id == id)) return;
        var first = stages.FirstOrDefault();
        if (first is null || first.Id == lead.PipelineStageId) return;
        AddHistory(caller, lead, HistoryKinds.Stage, lead.PipelineStage?.Name, first.Name, "Sales team changed");
        lead.PipelineStageId = first.Id;
    }

    // ── Status (D-9) ───────────────────────────────────────────────────────────

    public async Task<LeadDetail> ChangeStatusAsync(CrmCaller caller, Guid uuid, string? status, string? reason, CancellationToken ct = default)
    {
        var target = CrmText.OneOf(status, LeadStatuses.All, "Status") ?? throw new BadRequestException("Status is required.");
        return target switch
        {
            LeadStatuses.Qualified    => await QualifyAsync(caller, uuid, ct),
            LeadStatuses.Disqualified => await DisqualifyAsync(caller, uuid, reason, ct),
            LeadStatuses.New          => await ReopenAsync(caller, uuid, ct),
            LeadStatuses.Contacted    => await MarkContactedByHandAsync(caller, uuid, reason, ct),
            _                         => throw new BadRequestException("A lead is converted with Convert, which creates its opportunity.")
        };
    }

    private async Task<LeadDetail> MarkContactedByHandAsync(CrmCaller caller, Guid uuid, string? reason, CancellationToken ct)
    {
        var lead = await LoadAsync(caller, uuid, ct);
        RequireOwnership(caller, lead); // D-5 first: someone else's lead is a 403 whatever its status
        LeadStateMachine.Validate(lead.Status, LeadStatuses.Contacted);
        // BR-08 — first contact must be on record.
        if (!await HasContactActivityAsync(lead, ct))
            throw new BadRequestException("Log a call, meeting or email with the lead before marking it contacted.");
        await SetStatusAsync(caller, lead, LeadStatuses.Contacted, CrmText.Optional(reason, 500, "Reason"), ct);
        await _db.SaveChangesAsync(ct);
        await _events.PublishAsync(new LeadStatusChangedEvent(lead.Uuid, lead.OrganizationId, LeadStatuses.New, LeadStatuses.Contacted, reason), ct);
        return await GetByIdAsync(caller, uuid, ct);
    }

    internal async Task<bool> HasContactActivityAsync(CrmLead lead, CancellationToken ct)
    {
        var contact = ActivityTypes.Contact.ToArray();
        return await _db.Activities.AnyAsync(a => a.OrganizationId == lead.OrganizationId && a.LeadId == lead.Id && !a.IsDeleted
            && contact.Contains(a.ActivityType) && (a.ScheduledDate == null || a.IsCompleted), ct);
    }

    /// <summary>
    /// D-10 — BR-08 automatic: a contact activity on a NEW lead makes it CONTACTED. Called by the activity service inside its
    /// own unit of work (the caller saves). Returns the status-changed event to publish after the save, or null.
    /// </summary>
    internal async Task<LeadStatusChangedEvent?> OnContactActivityAsync(CrmCaller caller, CrmLead lead, string subject, CancellationToken ct)
    {
        if (lead.Status != LeadStatuses.New) return null;
        await SetStatusAsync(caller, lead, LeadStatuses.Contacted, $"Activity logged: {subject}", ct);
        return new LeadStatusChangedEvent(lead.Uuid, lead.OrganizationId, LeadStatuses.New, LeadStatuses.Contacted, null);
    }

    public async Task<LeadDetail> QualifyAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        caller.Require(PermissionCodes.CRM_QUALIFY_LEAD, "You do not have permission to qualify leads.");
        var lead = await LoadAsync(caller, uuid, ct);
        RequireOwnership(caller, lead);
        LeadStateMachine.Validate(lead.Status, LeadStatuses.Qualified);
        await SetStatusAsync(caller, lead, LeadStatuses.Qualified, null, ct);
        await _db.SaveChangesAsync(ct);
        await _events.PublishAsync(new LeadStatusChangedEvent(lead.Uuid, lead.OrganizationId, LeadStatuses.Contacted, LeadStatuses.Qualified, null), ct);
        await _events.PublishAsync(new LeadQualifiedEvent(lead.Uuid, lead.OrganizationId), ct);
        return await GetByIdAsync(caller, uuid, ct);
    }

    public async Task<LeadDetail> DisqualifyAsync(CrmCaller caller, Guid uuid, string? reason, CancellationToken ct = default)
    {
        caller.Require(PermissionCodes.CRM_DISQUALIFY_LEAD, "You do not have permission to disqualify leads.");
        var lead = await LoadAsync(caller, uuid, ct);
        RequireOwnership(caller, lead);
        LeadStateMachine.Validate(lead.Status, LeadStatuses.Disqualified);
        var why = CrmText.Required(reason, 500, "Reason");
        var old = lead.Status;
        await SetStatusAsync(caller, lead, LeadStatuses.Disqualified, why, ct);
        await _db.SaveChangesAsync(ct);
        await _events.PublishAsync(new LeadStatusChangedEvent(lead.Uuid, lead.OrganizationId, old, LeadStatuses.Disqualified, why), ct);
        await _events.PublishAsync(new LeadDisqualifiedEvent(lead.Uuid, lead.OrganizationId, why), ct);
        return await GetByIdAsync(caller, uuid, ct);
    }

    public async Task<LeadDetail> ReopenAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        caller.Require(PermissionCodes.CRM_REOPEN_LEAD, "You do not have permission to re-open leads.");
        var lead = await LoadAsync(caller, uuid, ct);
        LeadStateMachine.Validate(lead.Status, LeadStatuses.New);
        await SetStatusAsync(caller, lead, LeadStatuses.New, null, ct);
        await _db.SaveChangesAsync(ct);
        await _events.PublishAsync(new LeadStatusChangedEvent(lead.Uuid, lead.OrganizationId, LeadStatuses.Disqualified, LeadStatuses.New, null), ct);
        return await GetByIdAsync(caller, uuid, ct);
    }

    /// <summary>Applies a validated transition's side effects (§6.3) and its history row; the caller saves.</summary>
    private async Task SetStatusAsync(CrmCaller caller, CrmLead lead, string target, string? reason, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var old = lead.Status;
        lead.Status = target;
        switch (target)
        {
            case LeadStatuses.Contacted:
                await AdvanceStageAsync(caller, lead, "INITIAL_CONTACT", ct);
                break;
            case LeadStatuses.Qualified:
                lead.QualifiedAt = now;
                lead.StatusReason = null;
                await AdvanceStageAsync(caller, lead, "QUALIFICATION", ct);
                break;
            case LeadStatuses.Disqualified:
                lead.DisqualifiedAt = now;
                lead.StatusReason = reason;
                break;
            case LeadStatuses.New:
                lead.DisqualifiedAt = null;
                lead.QualifiedAt = null;
                lead.StatusReason = null;
                var first = await CrmPipeline.FirstAsync(_db, caller.OrganizationId, lead.SalesTeamId, StageTypes.Lead, ct);
                if (first is not null && first.Id != lead.PipelineStageId)
                {
                    AddHistory(caller, lead, HistoryKinds.Stage, lead.PipelineStage?.Name, first.Name, "Re-opened");
                    lead.PipelineStageId = first.Id;
                }
                break;
        }
        AddHistory(caller, lead, HistoryKinds.Status, old, target, reason);
        Touch(caller, lead);
    }

    /// <summary>D-10 — moves the lead forward to the stage with this code, only when it is still at an earlier stage.</summary>
    private async Task AdvanceStageAsync(CrmCaller caller, CrmLead lead, string code, CancellationToken ct)
    {
        var stages = await CrmPipeline.ForTeamAsync(_db, caller.OrganizationId, lead.SalesTeamId, StageTypes.Lead, false, ct);
        var target = stages.FirstOrDefault(s => s.Code == code);
        if (target is null || target.Id == lead.PipelineStageId) return;
        var current = stages.FirstOrDefault(s => s.Id == lead.PipelineStageId);
        if (current is not null && current.Sequence >= target.Sequence) return;
        AddHistory(caller, lead, HistoryKinds.Stage, current?.Name, target.Name, null);
        lead.PipelineStageId = target.Id;
    }

    // ── Stage, delete ──────────────────────────────────────────────────────────

    public async Task<LeadDetail> MoveStageAsync(CrmCaller caller, Guid uuid, Guid stageUuid, CancellationToken ct = default)
    {
        var lead = await LoadAsync(caller, uuid, ct);
        if (!LeadStatuses.IsOpen(lead.Status)) throw new ConflictException($"A {lead.Status} lead is not in the pipeline.");
        RequireOwnership(caller, lead);
        var stages = await CrmPipeline.ForTeamAsync(_db, caller.OrganizationId, lead.SalesTeamId, StageTypes.Lead, false, ct);
        var target = stages.FirstOrDefault(s => s.Uuid == stageUuid)
            ?? throw new BadRequestException("That stage is not an active lead stage of this lead's pipeline.");
        if (target.Id != lead.PipelineStageId)
        {
            AddHistory(caller, lead, HistoryKinds.Stage, lead.PipelineStage?.Name, target.Name, null);
            lead.PipelineStageId = target.Id;
            Touch(caller, lead);
            await _db.SaveChangesAsync(ct);
        }
        return await GetByIdAsync(caller, uuid, ct);
    }

    public async Task DeleteAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var lead = await LoadAsync(caller, uuid, ct);
        if (lead.Status == LeadStatuses.Converted) throw new ConflictException("A converted lead cannot be deleted (BR-06).");
        lead.IsDeleted = true;
        Touch(caller, lead);
        await _db.SaveChangesAsync(ct);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    internal void AddHistory(CrmCaller caller, CrmLead lead, string kind, string? from, string? to, string? reason) =>
        _db.RecordHistory.Add(new CrmRecordHistory
        {
            OrganizationId = lead.OrganizationId, EntityType = HistoryEntities.Lead, EntityId = lead.Id, Kind = kind,
            FromValue = from, ToValue = to, Reason = reason, UserId = caller.UserId, UserName = caller.UserName, At = DateTime.UtcNow
        });

    private static void Touch(CrmCaller caller, CrmLead lead) => lead.UpdatedByUserId = caller.UserId;
}

using Microsoft.EntityFrameworkCore;
using SMS.Modules.Crm.Data;
using SMS.Modules.Crm.Domain;
using SMS.Modules.Crm.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Crm.Services;

/// <summary>What the frontend needs to open the sale order form for an opportunity (D-13: an order needs lines).</summary>
public sealed class SaleOrderPrefillModel
{
    public Guid    AccountUuid           { get; set; }
    public string? AccountName           { get; set; }
    public Guid    OpportunityUuid       { get; set; }
    public string  OpportunityNumber     { get; set; } = string.Empty;
    /// <summary>An ACCEPTED quotation of this opportunity: convert it from its own page instead of typing lines again.</summary>
    public Guid?   AcceptedQuotationUuid { get; set; }
    public string? AcceptedQuotationNumber { get; set; }
}

/// <summary>A41 C7 — opportunities (API-CONTRACT §4). Created only by lead conversion (BR-11).</summary>
public interface IOpportunityService
{
    Task<PaginatedResponse<OpportunityListItem>> ListAsync(CrmCaller caller, OpportunityListFilter filter, CancellationToken ct = default);
    Task<OpportunityDetail> GetByIdAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<OpportunityDetail> UpdateAsync(CrmCaller caller, Guid uuid, OpportunityUpdate req, CancellationToken ct = default);
    Task<OpportunityDetail> ChangeStageAsync(CrmCaller caller, Guid uuid, Guid stageUuid, CancellationToken ct = default);
    Task<OpportunityDetail> MarkWonAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<OpportunityDetail> MarkLostAsync(CrmCaller caller, Guid uuid, string? reason, string? competitor, CancellationToken ct = default);
    Task<OpportunityDetail> ReopenAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<LinkedSalesDocumentModel> CreateInquiryAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<LinkedSalesDocumentModel> CreateQuotationAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<SaleOrderPrefillModel> CreateOrderAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<KanbanBoard<OpportunityListItem>> GetKanbanAsync(CrmCaller caller, OpportunityKanbanFilter filter, CancellationToken ct = default);
}

internal sealed class OpportunityService : IOpportunityService
{
    /// <summary>BR-12 — "status ≥ CONFIRMED" of a sale order.</summary>
    internal static readonly IReadOnlySet<string> ConfirmedOrderStatuses =
        new HashSet<string> { "CONFIRMED", "PARTIALLY_FULFILLED", "FULFILLED", "INVOICED", "CLOSED" };

    private readonly CrmDbContext _db;
    private readonly ICrmSeeder _seeder;
    private readonly CrmDirectory _directory;
    private readonly CrmCurrency _currency;
    private readonly ICrmEventPublisher _events;
    private readonly ICrmSalesDocumentGateway? _sales;
    private readonly IModuleGate? _gate;

    public OpportunityService(CrmDbContext db, ICrmSeeder seeder, CrmDirectory directory, CrmCurrency currency, ICrmEventPublisher events,
        ICrmSalesDocumentGateway? sales = null, IModuleGate? gate = null)
    {
        _db = db;
        _seeder = seeder;
        _directory = directory;
        _currency = currency;
        _events = events;
        _sales = sales;
        _gate = gate;
    }

    private IQueryable<CrmOpportunity> Own(CrmCaller c) => _db.Opportunities.Where(o => o.OrganizationId == c.OrganizationId && !o.IsDeleted);

    private static IQueryable<CrmOpportunity> WithRefs(IQueryable<CrmOpportunity> q) =>
        q.Include(o => o.SourceLead).Include(o => o.Campaign).Include(o => o.PipelineStage).Include(o => o.SalesTeam);

    private async Task<CrmOpportunity> LoadAsync(CrmCaller caller, Guid uuid, CancellationToken ct) =>
        await WithRefs(Own(caller)).FirstOrDefaultAsync(o => o.Uuid == uuid, ct) ?? throw new NotFoundException("Opportunity not found.");

    internal static bool Owns(CrmCaller caller, CrmOpportunity o) =>
        caller.IsManager || o.AssignedToUserId == caller.UserId || (o.AssignedToUserId is null && o.CreatedByUserId == caller.UserId);

    private static void RequireOwnership(CrmCaller caller, CrmOpportunity o)
    {
        if (!Owns(caller, o)) throw new ForbiddenException("You can only change opportunities assigned to you.");
    }

    private static void RequireOpen(CrmOpportunity o)
    {
        if (o.Status != OpportunityStatuses.Open) throw new ConflictException($"The opportunity is {o.Status}.");
    }

    // ── Reads ───────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<OpportunityListItem>> ListAsync(CrmCaller caller, OpportunityListFilter filter, CancellationToken ct = default)
    {
        var q = WithRefs(Own(caller).AsNoTracking());
        q = await ApplyFilterAsync(caller, q, filter.Search, filter.SalesTeamUuid, filter.AssignedToUserId, filter.Mine, filter.CloseFrom, filter.CloseTo, ct);
        if (CrmText.OneOf(filter.Status, OpportunityStatuses.All, "Status") is { } status) q = q.Where(o => o.Status == status);
        if (filter.PipelineStageUuid is { } st) q = q.Where(o => o.PipelineStage != null && o.PipelineStage.Uuid == st);
        if (filter.AccountUuid is { } acc) q = q.Where(o => o.AccountUuid == acc);

        var (page, size) = CrmText.Paging(filter.Page, filter.PageSize);
        var total = await q.CountAsync(ct);
        var desc = filter.SortOrder is null || CrmText.Desc(filter.SortOrder);
        q = filter.SortField?.Trim().ToLowerInvariant() switch
        {
            "opportunitynumber"  => desc ? q.OrderByDescending(o => o.OpportunityNumber) : q.OrderBy(o => o.OpportunityNumber),
            "topic"              => desc ? q.OrderByDescending(o => o.Topic) : q.OrderBy(o => o.Topic),
            "estimatedrevenue"   => desc ? q.OrderByDescending(o => o.EstimatedRevenue) : q.OrderBy(o => o.EstimatedRevenue),
            "estimatedclosedate" => desc ? q.OrderByDescending(o => o.EstimatedCloseDate) : q.OrderBy(o => o.EstimatedCloseDate),
            "status"             => desc ? q.OrderByDescending(o => o.Status) : q.OrderBy(o => o.Status),
            _                    => desc ? q.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id) : q.OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
        };
        var rows = await q.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PaginatedResponse<OpportunityListItem>
        {
            Data = await MapListAsync(caller, rows, ct), TotalRecords = total, Page = page, PageSize = size,
            TotalPages = (int)Math.Ceiling(total / (double)size)
        };
    }

    private async Task<IQueryable<CrmOpportunity>> ApplyFilterAsync(CrmCaller caller, IQueryable<CrmOpportunity> q, string? search, Guid? teamUuid,
        int? assignedTo, bool mine, DateTime? closeFrom, DateTime? closeTo, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            q = q.Where(o => o.Topic.ToLower().Contains(s) || o.OpportunityNumber.ToLower().Contains(s)
                || o.SourceLead.CompanyName.ToLower().Contains(s) || o.SourceLead.FullName.ToLower().Contains(s));
        }
        if (teamUuid is { } team)
        {
            var teamId = await _db.SalesTeams.Where(t => t.OrganizationId == caller.OrganizationId && t.Uuid == team).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);
            q = q.Where(o => o.SalesTeamId == teamId);
        }
        if (mine) q = q.Where(o => o.AssignedToUserId == caller.UserId);
        else if (assignedTo is { } user) q = q.Where(o => o.AssignedToUserId == user);
        if (closeFrom is { } f) q = q.Where(o => o.EstimatedCloseDate >= f.Date);
        if (closeTo is { } t) q = q.Where(o => o.EstimatedCloseDate <= t.Date);
        return q;
    }

    public async Task<OpportunityDetail> GetByIdAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var o = await LoadAsync(caller, uuid, ct);
        var docs = await RefreshLinksAsync(caller, o, ct);
        return await MapDetailAsync(caller, o, docs, ct);
    }

    /// <summary>D-13 — Demand owns the links; the latest document of each kind is cached on the opportunity.</summary>
    private async Task<IReadOnlyList<CrmSalesDocument>> RefreshLinksAsync(CrmCaller caller, CrmOpportunity o, CancellationToken ct)
    {
        if (_sales is null) return [];
        var docs = await _sales.ListForOpportunityAsync(caller.OrganizationId, o.Uuid, ct);
        Guid? Latest(string kind) => docs.Where(d => d.Kind == kind).OrderByDescending(d => d.CreatedAt).Select(d => (Guid?)d.Uuid).FirstOrDefault();
        var inquiry = Latest(CrmSalesDocumentKind.Inquiry) ?? o.SourceInquiryUuid;
        var quotation = Latest(CrmSalesDocumentKind.Quotation) ?? o.SourceQuotationUuid;
        var order = Latest(CrmSalesDocumentKind.Order) ?? o.SourceOrderUuid;
        if (inquiry != o.SourceInquiryUuid || quotation != o.SourceQuotationUuid || order != o.SourceOrderUuid)
        {
            o.SourceInquiryUuid = inquiry;
            o.SourceQuotationUuid = quotation;
            o.SourceOrderUuid = order;
            await _db.SaveChangesAsync(ct);
        }
        return docs;
    }

    // ── Changes ────────────────────────────────────────────────────────────────

    public async Task<OpportunityDetail> UpdateAsync(CrmCaller caller, Guid uuid, OpportunityUpdate req, CancellationToken ct = default)
    {
        var o = await LoadAsync(caller, uuid, ct);
        RequireOpen(o);
        RequireOwnership(caller, o);
        var org = caller.OrganizationId;
        o.Topic = CrmText.Required(req.Topic, 200, "Topic");
        o.Description = CrmText.Optional(req.Description, 4000, "Description");
        if (req.ContactPersonId is { } contactId && contactId != o.ContactPersonId && _directory.Accounts is { } accounts)
        {
            var contact = (await accounts.GetContactsAsync(org, [contactId], ct)).GetValueOrDefault(contactId)
                ?? throw new BadRequestException("The contact was not found.");
            if (contact.AccountUuid != o.AccountUuid) throw new BadRequestException("The contact belongs to another account.");
        }
        o.ContactPersonId = req.ContactPersonId;
        o.EstimatedRevenue = CrmText.Money(req.EstimatedRevenue, "Estimated revenue");
        o.EstimatedCloseDate = req.EstimatedCloseDate?.Date;
        o.CurrencyCode = await _currency.ValidateAsync(org, req.CurrencyCode, "Currency", ct);
        if (o.EstimatedRevenue is not null && o.CurrencyCode is null) throw new BadRequestException("Give the estimated revenue's currency.");

        var newOwner = req.AssignedToUserId is > 0 ? req.AssignedToUserId : null;
        if (newOwner != o.AssignedToUserId)
        {
            caller.Require(PermissionCodes.CRM_ASSIGN_LEAD, "Only a sales manager can give the opportunity to someone else.");
            if (newOwner is { } who) await _directory.RequireActiveUserAsync(org, who, "opportunity owner", ct);
            AddHistory(caller, o, HistoryKinds.Assignment, o.AssignedToUserId?.ToString(), newOwner?.ToString(), null);
            o.AssignedToUserId = newOwner;
        }
        if (req.SalesTeamUuid is { } teamUuid)
        {
            var teamId = await _db.SalesTeams.Where(t => t.OrganizationId == org && t.Uuid == teamUuid && t.IsActive).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct)
                ?? throw new BadRequestException("The sales team was not found or is inactive.");
            if (teamId != o.SalesTeamId)
            {
                o.SalesTeamId = teamId;
                var stages = await CrmPipeline.ForTeamAsync(_db, org, teamId, StageTypes.Opportunity, false, ct);
                if (!stages.Any(s => s.Id == o.PipelineStageId) && stages.FirstOrDefault(s => !s.IsWon && !s.IsLost) is { } first)
                {
                    AddHistory(caller, o, HistoryKinds.Stage, o.PipelineStage?.Name, first.Name, "Sales team changed");
                    o.PipelineStageId = first.Id;
                }
            }
        }
        else o.SalesTeamId = null;
        o.UpdatedByUserId = caller.UserId;
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(caller, uuid, ct);
    }

    public async Task<OpportunityDetail> ChangeStageAsync(CrmCaller caller, Guid uuid, Guid stageUuid, CancellationToken ct = default)
    {
        var o = await LoadAsync(caller, uuid, ct);
        RequireOpen(o);
        RequireOwnership(caller, o);
        var stages = await CrmPipeline.ForTeamAsync(_db, caller.OrganizationId, o.SalesTeamId, StageTypes.Opportunity, false, ct);
        var target = stages.FirstOrDefault(s => s.Uuid == stageUuid)
            ?? throw new BadRequestException("That stage is not an active opportunity stage of this opportunity's pipeline.");
        if (target.IsWon || target.IsLost)
            throw new BadRequestException(target.IsWon ? "Use Won to close the opportunity as won." : "Use Lost to close the opportunity as lost.");
        if (target.Id == o.PipelineStageId) return await GetByIdAsync(caller, uuid, ct);

        var oldStage = o.PipelineStage?.Uuid;
        AddHistory(caller, o, HistoryKinds.Stage, o.PipelineStage?.Name, target.Name, null);
        o.PipelineStageId = target.Id;
        o.UpdatedByUserId = caller.UserId;
        await _db.SaveChangesAsync(ct);
        await _events.PublishAsync(new OpportunityStageChangedEvent(o.Uuid, o.OrganizationId, oldStage, target.Uuid), ct);
        return await GetByIdAsync(caller, uuid, ct);
    }

    public async Task<OpportunityDetail> MarkWonAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var o = await LoadAsync(caller, uuid, ct);
        RequireOpen(o);
        RequireOwnership(caller, o);
        var docs = await RefreshLinksAsync(caller, o, ct);
        // BR-12 — a confirmed sale order of this opportunity; BR-16 — its grand total is the actual revenue.
        var order = docs.Where(d => d.Kind == CrmSalesDocumentKind.Order && ConfirmedOrderStatuses.Contains(d.Status))
            .OrderByDescending(d => d.CreatedAt).FirstOrDefault()
            ?? throw new BadRequestException("An opportunity is won when its sale order is confirmed. Create and confirm the sale order first.");

        o.Status = OpportunityStatuses.Won;
        o.ActualRevenue = order.GrandTotal;
        o.CurrencyCode = order.CurrencyCode ?? o.CurrencyCode;
        o.ActualCloseDate = DateTime.UtcNow.Date;
        o.SourceOrderUuid = order.Uuid;
        o.WinLossReason = null;
        o.CompetitorName = null;
        await MoveToClosingStageAsync(caller, o, won: true, ct);
        AddHistory(caller, o, HistoryKinds.Status, OpportunityStatuses.Open, OpportunityStatuses.Won, order.Number);
        o.UpdatedByUserId = caller.UserId;
        await _db.SaveChangesAsync(ct);
        await _events.PublishAsync(new OpportunityWonEvent(o.Uuid, o.OrganizationId, order.Uuid, order.GrandTotal ?? 0m), ct);
        return await GetByIdAsync(caller, uuid, ct);
    }

    public async Task<OpportunityDetail> MarkLostAsync(CrmCaller caller, Guid uuid, string? reason, string? competitor, CancellationToken ct = default)
    {
        var o = await LoadAsync(caller, uuid, ct);
        RequireOpen(o);
        RequireOwnership(caller, o);
        var why = CrmText.Required(reason, 500, "Reason"); // BR-13
        o.Status = OpportunityStatuses.Lost;
        o.WinLossReason = why;
        o.CompetitorName = CrmText.Optional(competitor, 200, "Competitor");
        o.ActualCloseDate = DateTime.UtcNow.Date;
        await MoveToClosingStageAsync(caller, o, won: false, ct);
        AddHistory(caller, o, HistoryKinds.Status, OpportunityStatuses.Open, OpportunityStatuses.Lost, why);
        o.UpdatedByUserId = caller.UserId;
        await _db.SaveChangesAsync(ct);
        await _events.PublishAsync(new OpportunityLostEvent(o.Uuid, o.OrganizationId, why, o.CompetitorName), ct);
        return await GetByIdAsync(caller, uuid, ct);
    }

    public async Task<OpportunityDetail> ReopenAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        caller.Require(PermissionCodes.CRM_REOPEN_OPPORTUNITY, "You do not have permission to re-open opportunities.");
        var o = await LoadAsync(caller, uuid, ct);
        if (o.Status == OpportunityStatuses.Won) throw new ConflictException("A won opportunity is closed for good.");
        if (o.Status != OpportunityStatuses.Lost) throw new ConflictException("Only a lost opportunity can be re-opened.");
        o.Status = OpportunityStatuses.Open;
        o.ActualCloseDate = null;
        o.WinLossReason = null;
        o.CompetitorName = null;
        var first = await CrmPipeline.FirstAsync(_db, caller.OrganizationId, o.SalesTeamId, StageTypes.Opportunity, ct);
        if (first is not null && first.Id != o.PipelineStageId)
        {
            AddHistory(caller, o, HistoryKinds.Stage, o.PipelineStage?.Name, first.Name, "Re-opened");
            o.PipelineStageId = first.Id;
        }
        AddHistory(caller, o, HistoryKinds.Status, OpportunityStatuses.Lost, OpportunityStatuses.Open, null);
        o.UpdatedByUserId = caller.UserId;
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(caller, uuid, ct);
    }

    private async Task MoveToClosingStageAsync(CrmCaller caller, CrmOpportunity o, bool won, CancellationToken ct)
    {
        var stages = await CrmPipeline.ForTeamAsync(_db, caller.OrganizationId, o.SalesTeamId, StageTypes.Opportunity, false, ct);
        var target = stages.FirstOrDefault(s => won ? s.IsWon : s.IsLost);
        if (target is null || target.Id == o.PipelineStageId) return;
        AddHistory(caller, o, HistoryKinds.Stage, o.PipelineStage?.Name, target.Name, null);
        o.PipelineStageId = target.Id;
    }

    // ── SCM documents (D-13) ───────────────────────────────────────────────────

    private async Task<(CrmOpportunity Opp, ICrmSalesDocumentGateway Sales)> ForScmAsync(CrmCaller caller, Guid uuid, string permission, string what, CancellationToken ct)
    {
        caller.Require(permission, $"You do not have permission to create {what}.");
        var o = await LoadAsync(caller, uuid, ct);
        RequireOpen(o);
        RequireOwnership(caller, o);
        if (_sales is null || (_gate is not null && !await _gate.IsEnabledAsync(caller.OrganizationId, ModuleCodes.Demand, ct)))
            throw new BadRequestException("Sales documents are switched off for the organization.");
        return (o, _sales);
    }

    public async Task<LinkedSalesDocumentModel> CreateInquiryAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var (o, sales) = await ForScmAsync(caller, uuid, PermissionCodes.SALE_INQUIRY_CREATE, "sale inquiries", ct);
        var doc = await sales.CreateInquiryAsync(o.Uuid, o.AccountUuid, o.AssignedToUserId, $"{o.OpportunityNumber} — {o.Topic}", caller.UserId, ct);
        o.SourceInquiryUuid = doc.Uuid;
        await _db.SaveChangesAsync(ct);
        return Map(doc);
    }

    public async Task<LinkedSalesDocumentModel> CreateQuotationAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var (o, sales) = await ForScmAsync(caller, uuid, PermissionCodes.SALE_QUOTATION_CREATE, "sale quotations", ct);
        var docs = await RefreshLinksAsync(caller, o, ct);
        var inquiry = docs.Where(d => d.Kind == CrmSalesDocumentKind.Inquiry && d.Status == "REVIEW_COMPLETE")
            .OrderByDescending(d => d.CreatedAt).Select(d => (Guid?)d.Uuid).FirstOrDefault();
        var doc = await sales.CreateQuotationAsync(o.Uuid, o.AccountUuid, inquiry, $"{o.OpportunityNumber} — {o.Topic}", caller.UserId, ct);
        o.SourceQuotationUuid = doc.Uuid;
        await _db.SaveChangesAsync(ct);
        return Map(doc);
    }

    public async Task<SaleOrderPrefillModel> CreateOrderAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var (o, _) = await ForScmAsync(caller, uuid, PermissionCodes.SALE_ORDER_CREATE, "sale orders", ct);
        var docs = await RefreshLinksAsync(caller, o, ct);
        var accepted = docs.Where(d => d.Kind == CrmSalesDocumentKind.Quotation && d.Status == "ACCEPTED").OrderByDescending(d => d.CreatedAt).FirstOrDefault();
        var account = (await _directory.AccountsAsync(caller.OrganizationId, [o.AccountUuid], ct)).GetValueOrDefault(o.AccountUuid);
        return new SaleOrderPrefillModel
        {
            AccountUuid = o.AccountUuid, AccountName = account?.Name, OpportunityUuid = o.Uuid, OpportunityNumber = o.OpportunityNumber,
            AcceptedQuotationUuid = accepted?.Uuid, AcceptedQuotationNumber = accepted?.Number
        };
    }

    // ── Kanban ────────────────────────────────────────────────────────────────

    public async Task<KanbanBoard<OpportunityListItem>> GetKanbanAsync(CrmCaller caller, OpportunityKanbanFilter filter, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        int? teamId = null;
        if (filter.SalesTeamUuid is { } teamUuid)
            teamId = await _db.SalesTeams.Where(t => t.OrganizationId == caller.OrganizationId && t.Uuid == teamUuid).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Sales team not found.");
        var stages = await CrmPipeline.ForTeamAsync(_db, caller.OrganizationId, teamId, StageTypes.Opportunity, false, ct);

        var q = WithRefs(Own(caller).AsNoTracking());
        if (!filter.IncludeClosed) q = q.Where(o => o.Status == OpportunityStatuses.Open);
        q = await ApplyFilterAsync(caller, q, filter.Search, filter.SalesTeamUuid, filter.AssignedToUserId, filter.Mine, filter.CloseFrom, filter.CloseTo, ct);
        var opps = await q.OrderByDescending(o => o.CreatedAt).Take(2000).ToListAsync(ct);
        var cards = await MapListAsync(caller, opps, ct);
        var byUuid = cards.ToDictionary(c => c.Uuid);

        var board = new KanbanBoard<OpportunityListItem>
        {
            MixedCurrencies = opps.Where(o => o.EstimatedRevenue is not null).Select(o => o.CurrencyCode).Distinct().Count() > 1
        };
        var stageIds = stages.Select(s => s.Id).ToHashSet();
        foreach (var s in stages.Where(s => filter.IncludeClosed || (!s.IsWon && !s.IsLost)))
        {
            var inStage = opps.Where(o => o.PipelineStageId == s.Id).ToList();
            board.Columns.Add(new KanbanColumn<OpportunityListItem>
            {
                StageUuid = s.Uuid, Code = s.Code, Name = s.Name, Sequence = s.Sequence, WinProbability = s.WinProbability,
                IsWon = s.IsWon, IsLost = s.IsLost, Count = inStage.Count,
                TotalRevenue = inStage.Sum(o => o.Status == OpportunityStatuses.Won ? o.ActualRevenue ?? 0m : o.EstimatedRevenue ?? 0m),
                Items = inStage.Select(o => byUuid[o.Uuid]).ToList()
            });
        }
        board.Unstaged = opps.Where(o => o.PipelineStageId is null || !stageIds.Contains(o.PipelineStageId.Value)).Select(o => byUuid[o.Uuid]).ToList();
        return board;
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private async Task<List<OpportunityListItem>> MapListAsync(CrmCaller caller, IReadOnlyList<CrmOpportunity> rows, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        var users = await _directory.UsersAsync(org, rows.Select(r => r.AssignedToUserId), ct);
        var accounts = await _directory.AccountsAsync(org, rows.Select(r => (Guid?)r.AccountUuid), ct);
        var contacts = await _directory.ContactsAsync(org, rows.Select(r => r.ContactPersonId), ct);
        return rows.Select(o => Fill(new OpportunityListItem(), o, users, accounts, contacts)).ToList();
    }

    private static T Fill<T>(T item, CrmOpportunity o, Dictionary<int, OrgUserInfo> users, Dictionary<Guid, CrmAccountInfo> accounts,
        Dictionary<int, CrmContactInfo> contacts) where T : OpportunityListItem
    {
        item.Uuid = o.Uuid; item.OpportunityNumber = o.OpportunityNumber; item.Topic = o.Topic;
        item.AccountUuid = o.AccountUuid; item.AccountName = accounts.GetValueOrDefault(o.AccountUuid)?.Name ?? o.SourceLead?.CompanyName;
        item.ContactPersonId = o.ContactPersonId;
        item.ContactPersonName = o.ContactPersonId is { } c ? contacts.GetValueOrDefault(c)?.Name : null;
        item.Status = o.Status; item.PipelineStageUuid = o.PipelineStage?.Uuid; item.PipelineStageName = o.PipelineStage?.Name;
        item.WinProbability = o.PipelineStage?.WinProbability;
        item.EstimatedRevenue = o.EstimatedRevenue; item.ActualRevenue = o.ActualRevenue; item.CurrencyCode = o.CurrencyCode;
        item.EstimatedCloseDate = o.EstimatedCloseDate; item.ActualCloseDate = o.ActualCloseDate;
        item.AssignedToUserId = o.AssignedToUserId;
        item.AssignedToName = o.AssignedToUserId is { } a ? users.GetValueOrDefault(a)?.Name : null;
        item.SalesTeamUuid = o.SalesTeam?.Uuid; item.SalesTeamName = o.SalesTeam?.Name;
        item.SourceLeadUuid = o.SourceLead?.Uuid ?? Guid.Empty; item.SourceLeadNumber = o.SourceLead?.LeadNumber ?? string.Empty;
        item.CreatedAt = o.CreatedAt;
        return item;
    }

    private async Task<OpportunityDetail> MapDetailAsync(CrmCaller caller, CrmOpportunity o, IReadOnlyList<CrmSalesDocument> docs, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        var users = await _directory.UsersAsync(org, [o.AssignedToUserId, o.CreatedByUserId], ct);
        var accounts = await _directory.AccountsAsync(org, [o.AccountUuid], ct);
        var contacts = await _directory.ContactsAsync(org, [o.ContactPersonId], ct);
        var d = Fill(new OpportunityDetail(), o, users, accounts, contacts);
        d.Description = o.Description;
        d.Account = accounts.TryGetValue(o.AccountUuid, out var acc) ? CrmDirectory.ToModel(acc) : null;
        d.ContactPerson = o.ContactPersonId is { } cid && contacts.TryGetValue(cid, out var con) ? CrmDirectory.ToModel(con) : null;
        d.CampaignUuid = o.Campaign?.Uuid; d.CampaignName = o.Campaign?.Name;
        d.WinLossReason = o.WinLossReason; d.CompetitorName = o.CompetitorName;
        d.IsWonStage = o.PipelineStage?.IsWon ?? false; d.IsLostStage = o.PipelineStage?.IsLost ?? false;
        d.SalesDocuments = docs.OrderBy(x => x.CreatedAt).Select(Map).ToList();
        d.Pipeline = (await CrmPipeline.ForTeamAsync(_db, org, o.SalesTeamId, StageTypes.Opportunity, false, ct)).Select(PipelineStageService.Map).ToList();
        d.CreatedByUserId = o.CreatedByUserId; d.CreatedByName = users.GetValueOrDefault(o.CreatedByUserId)?.Name;
        d.ModifiedAt = o.ModifiedAt;

        var own = Owns(caller, o);
        d.CanEdit = o.Status == OpportunityStatuses.Open && own && caller.Has(PermissionCodes.CRM_EDIT_OPPORTUNITY);
        if (o.Status == OpportunityStatuses.Open)
        {
            if (own && caller.Has(PermissionCodes.CRM_CLOSE_OPPORTUNITY)) { d.AllowedActions.Add("WON"); d.AllowedActions.Add("LOST"); }
            if (own && caller.Has(PermissionCodes.CRM_EDIT_OPPORTUNITY))
            {
                if (caller.Has(PermissionCodes.SALE_INQUIRY_CREATE)) d.AllowedActions.Add("CREATE_INQUIRY");
                if (caller.Has(PermissionCodes.SALE_QUOTATION_CREATE)) d.AllowedActions.Add("CREATE_QUOTATION");
                if (caller.Has(PermissionCodes.SALE_ORDER_CREATE)) d.AllowedActions.Add("CREATE_ORDER");
            }
        }
        if (o.Status == OpportunityStatuses.Lost && caller.Has(PermissionCodes.CRM_REOPEN_OPPORTUNITY)) d.AllowedActions.Add("REOPEN");
        return d;
    }

    private static LinkedSalesDocumentModel Map(CrmSalesDocument d) => new()
    {
        Kind = d.Kind, Uuid = d.Uuid, Number = d.Number, Status = d.Status, CreatedAt = d.CreatedAt, GrandTotal = d.GrandTotal, CurrencyCode = d.CurrencyCode
    };

    private void AddHistory(CrmCaller caller, CrmOpportunity o, string kind, string? from, string? to, string? reason) =>
        _db.RecordHistory.Add(new CrmRecordHistory
        {
            OrganizationId = o.OrganizationId, EntityType = HistoryEntities.Opportunity, EntityId = o.Id, Kind = kind,
            FromValue = from, ToValue = to, Reason = reason, UserId = caller.UserId, UserName = caller.UserName, At = DateTime.UtcNow
        });
}

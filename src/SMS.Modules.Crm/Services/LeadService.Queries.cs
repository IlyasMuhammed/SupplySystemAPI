using Microsoft.EntityFrameworkCore;
using SMS.Modules.Crm.Domain;
using SMS.Modules.Crm.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Crm.Services;

// A41 C4 / C10 — lead mapping, kanban (register 1E/1J), duplicate check (BR-09), cross-references (§12.5, BR-22).
internal sealed partial class LeadService
{
    // ── Mapping ───────────────────────────────────────────────────────────────

    internal async Task<List<LeadListItem>> MapListAsync(CrmCaller caller, IReadOnlyList<CrmLead> rows, CancellationToken ct)
    {
        var users = await _directory.UsersAsync(caller.OrganizationId, rows.Select(r => r.AssignedToUserId), ct);
        return rows.Select(l => Fill(new LeadListItem(), l, users.GetValueOrDefault(l.AssignedToUserId ?? 0)?.Name)).ToList();
    }

    private static T Fill<T>(T item, CrmLead l, string? assigneeName) where T : LeadListItem
    {
        item.Uuid = l.Uuid; item.LeadNumber = l.LeadNumber; item.FullName = l.FullName; item.Topic = l.Topic;
        item.CompanyName = l.CompanyName; item.Email = l.Email; item.Phone = l.Phone; item.MobilePhone = l.MobilePhone;
        item.LeadSourceUuid = l.LeadSource?.Uuid; item.LeadSourceName = l.LeadSource?.Name;
        item.CampaignUuid = l.Campaign?.Uuid; item.CampaignName = l.Campaign?.Name;
        item.Rating = l.Rating; item.Priority = l.Priority; item.Status = l.Status;
        item.PipelineStageUuid = l.PipelineStage?.Uuid; item.PipelineStageName = l.PipelineStage?.Name;
        item.AssignedToUserId = l.AssignedToUserId; item.AssignedToName = assigneeName;
        item.SalesTeamUuid = l.SalesTeam?.Uuid; item.SalesTeamName = l.SalesTeam?.Name;
        item.EstimatedRevenue = l.EstimatedRevenue; item.CurrencyCode = l.CurrencyCode; item.EstimatedCloseDate = l.EstimatedCloseDate;
        item.CreatedAt = l.CreatedAt;
        return item;
    }

    private async Task<LeadDetail> MapDetailAsync(CrmCaller caller, CrmLead l, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        var users = await _directory.UsersAsync(org, [l.AssignedToUserId, l.PreviousOwnerUserId, l.CreatedByUserId], ct);
        var d = Fill(new LeadDetail(), l, l.AssignedToUserId is { } a ? users.GetValueOrDefault(a)?.Name : null);
        d.Description = l.Description; d.Salutation = l.Salutation; d.FirstName = l.FirstName; d.LastName = l.LastName;
        d.JobTitle = l.JobTitle; d.Website = l.Website; d.AddressLine1 = l.AddressLine1; d.AddressLine2 = l.AddressLine2;
        d.City = l.City; d.StateProvince = l.StateProvince; d.PostalCode = l.PostalCode; d.Country = l.Country;
        d.SourceMedium = l.SourceMedium; d.SourceReference = l.SourceReference;
        d.ExternalSourceSystem = l.ExternalSourceSystem; d.ExternalSourceId = l.ExternalSourceId;
        d.BudgetAmount = l.BudgetAmount; d.BudgetCurrencyCode = l.BudgetCurrencyCode; d.BudgetConfirmed = l.BudgetConfirmed;
        d.AuthorityConfirmed = l.AuthorityConfirmed; d.NeedIdentified = l.NeedIdentified; d.PurchaseTimeframe = l.PurchaseTimeframe;
        d.AssignedAt = l.AssignedAt; d.PreviousOwnerUserId = l.PreviousOwnerUserId;
        d.PreviousOwnerName = l.PreviousOwnerUserId is { } p ? users.GetValueOrDefault(p)?.Name : null;
        d.PipelineStageProbability = l.PipelineStage?.WinProbability;
        d.StatusReason = l.StatusReason; d.QualifiedAt = l.QualifiedAt; d.DisqualifiedAt = l.DisqualifiedAt; d.ConvertedAt = l.ConvertedAt;
        d.CreatedByUserId = l.CreatedByUserId;
        d.CreatedByName = l.CreatedByUserId > 0 ? users.GetValueOrDefault(l.CreatedByUserId)?.Name ?? l.CreatedByName : l.CreatedByName;
        d.ModifiedAt = l.ModifiedAt;

        if (l.AccountUuid is { } accountUuid && (await _directory.AccountsAsync(org, [accountUuid], ct)).TryGetValue(accountUuid, out var account))
            d.Account = CrmDirectory.ToModel(account);
        if (l.ContactPersonId is { } contactId && (await _directory.ContactsAsync(org, [contactId], ct)).TryGetValue(contactId, out var contact))
            d.ContactPerson = CrmDirectory.ToModel(contact);
        if (l.ConvertedOpportunityId is { } oppId)
            d.ConvertedOpportunity = await _db.Opportunities.Where(o => o.Id == oppId && o.OrganizationId == org)
                .Select(o => new CrmRef { Uuid = o.Uuid, Number = o.OpportunityNumber, Name = o.Topic }).FirstOrDefaultAsync(ct);

        d.ActivityCount = await _db.Activities.CountAsync(x => x.OrganizationId == org && x.LeadId == l.Id && !x.IsDeleted, ct);
        d.CanEdit = l.Status != LeadStatuses.Converted && caller.Has(PermissionCodes.CRM_EDIT_LEAD) && Owns(caller, l);
        d.AllowedActions = await AllowedActionsAsync(caller, l, ct);
        return d;
    }

    /// <summary>D-9 — what this caller may do with the lead now (the detail page's buttons).</summary>
    private async Task<List<string>> AllowedActionsAsync(CrmCaller caller, CrmLead l, CancellationToken ct)
    {
        var actions = new List<string>();
        var own = Owns(caller, l);
        switch (l.Status)
        {
            case LeadStatuses.New:
                if (own && caller.Has(PermissionCodes.CRM_EDIT_LEAD) && await HasContactActivityAsync(l, ct)) actions.Add("MARK_CONTACTED");
                break;
            case LeadStatuses.Contacted:
                if (own && caller.Has(PermissionCodes.CRM_QUALIFY_LEAD)) actions.Add("QUALIFY");
                break;
            case LeadStatuses.Qualified:
                if (own && caller.Has(PermissionCodes.CRM_CONVERT_LEAD)) actions.Add("CONVERT");
                break;
            case LeadStatuses.Disqualified:
                if (caller.Has(PermissionCodes.CRM_REOPEN_LEAD)) actions.Add("REOPEN");
                break;
        }
        if (LeadStatuses.IsOpen(l.Status) && own && caller.Has(PermissionCodes.CRM_DISQUALIFY_LEAD)) actions.Add("DISQUALIFY");
        if (l.Status != LeadStatuses.Converted && caller.Has(PermissionCodes.CRM_ASSIGN_LEAD)) actions.Add("ASSIGN");
        if (l.Status != LeadStatuses.Converted && caller.Has(PermissionCodes.CRM_DELETE_LEAD)) actions.Add("DELETE");
        return actions;
    }

    // ── Kanban ────────────────────────────────────────────────────────────────

    public async Task<KanbanBoard<LeadListItem>> GetKanbanAsync(CrmCaller caller, LeadKanbanFilter filter, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        int? teamId = null;
        if (filter.SalesTeamUuid is { } teamUuid)
            teamId = await _db.SalesTeams.Where(t => t.OrganizationId == caller.OrganizationId && t.Uuid == teamUuid).Select(t => (int?)t.Id).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("Sales team not found.");
        var stages = await CrmPipeline.ForTeamAsync(_db, caller.OrganizationId, teamId, StageTypes.Lead, false, ct);

        var open = LeadStatuses.Open.ToArray();
        var q = WithRefs(Own(caller).AsNoTracking()).Where(l => open.Contains(l.Status));
        q = await ApplyFilterAsync(caller, q, filter.Search, filter.SalesTeamUuid, filter.AssignedToUserId, filter.Mine, filter.Rating, filter.From, filter.To, ct);
        var leads = await q.OrderByDescending(l => l.CreatedAt).Take(2000).ToListAsync(ct);
        var cards = await MapListAsync(caller, leads, ct);
        var byUuid = cards.ToDictionary(c => c.Uuid);

        var board = new KanbanBoard<LeadListItem>
        {
            MixedCurrencies = leads.Where(l => l.EstimatedRevenue is not null).Select(l => l.CurrencyCode).Distinct().Count() > 1
        };
        var stageIds = stages.Select(s => s.Id).ToHashSet();
        foreach (var s in stages)
        {
            var inStage = leads.Where(l => l.PipelineStageId == s.Id).ToList();
            board.Columns.Add(new KanbanColumn<LeadListItem>
            {
                StageUuid = s.Uuid, Code = s.Code, Name = s.Name, Sequence = s.Sequence, WinProbability = s.WinProbability,
                Count = inStage.Count, TotalRevenue = inStage.Sum(l => l.EstimatedRevenue ?? 0m),
                Items = inStage.Select(l => byUuid[l.Uuid]).ToList()
            });
        }
        board.Unstaged = leads.Where(l => l.PipelineStageId is null || !stageIds.Contains(l.PipelineStageId.Value)).Select(l => byUuid[l.Uuid]).ToList();
        return board;
    }

    // ── Duplicates (BR-09, advisory) ───────────────────────────────────────────

    public async Task<LeadDuplicateResult> DuplicateCheckForLeadAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var lead = await Own(caller).AsNoTracking().FirstOrDefaultAsync(l => l.Uuid == uuid, ct) ?? throw new NotFoundException("Lead not found.");
        return await DuplicateCheckAsync(caller, new LeadDuplicateQuery
        {
            Email = lead.Email, Phone = lead.MobilePhone ?? lead.Phone, CompanyName = lead.CompanyName, ExcludeUuid = lead.Uuid
        }, ct);
    }

    public async Task<LeadDuplicateResult> DuplicateCheckAsync(CrmCaller caller, LeadDuplicateQuery query, CancellationToken ct = default)
    {
        var email = string.IsNullOrWhiteSpace(query.Email) ? null : query.Email.Trim().ToLower();
        var company = string.IsNullOrWhiteSpace(query.CompanyName) ? null : query.CompanyName.Trim().ToLower();
        var phoneKey = CrmText.PhoneKey(query.Phone);
        var tail = phoneKey is null ? null : phoneKey[^Math.Min(7, phoneKey.Length)..];
        var result = new LeadDuplicateResult();
        if (email is null && company is null && phoneKey is null) return result;

        var q = WithRefs(Own(caller).AsNoTracking());
        if (query.ExcludeUuid is { } ex) q = q.Where(l => l.Uuid != ex);
        // Candidates in SQL (phone by its last 7 digits), the exact digit comparison in memory.
        var candidates = await q.Where(l => (email != null && l.Email != null && l.Email.ToLower() == email)
                || (company != null && l.CompanyName.ToLower() == company)
                || (tail != null && ((l.Phone != null && l.Phone.Contains(tail)) || (l.MobilePhone != null && l.MobilePhone.Contains(tail)))))
            .OrderByDescending(l => l.CreatedAt).Take(200).ToListAsync(ct);

        var matches = new List<(CrmLead Lead, List<string> On)>();
        foreach (var l in candidates)
        {
            var on = new List<string>();
            if (email is not null && string.Equals(l.Email, email, StringComparison.OrdinalIgnoreCase)) on.Add("EMAIL");
            if (phoneKey is not null && (CrmText.PhoneKey(l.Phone) == phoneKey || CrmText.PhoneKey(l.MobilePhone) == phoneKey)) on.Add("PHONE");
            if (company is not null && string.Equals(l.CompanyName.Trim(), company, StringComparison.OrdinalIgnoreCase)) on.Add("COMPANY");
            if (on.Count > 0) matches.Add((l, on));
        }
        var top = matches.OrderByDescending(m => m.On.Count).ThenByDescending(m => m.Lead.CreatedAt).Take(MaxDuplicates).ToList();
        var items = await MapListAsync(caller, top.Select(m => m.Lead).ToList(), ct);
        result.Leads = top.Select((m, i) => new LeadDuplicate { Lead = items[i], MatchedOn = m.On }).ToList();

        if (_directory.Accounts is { } accounts)
            result.Accounts = (await accounts.FindMatchesAsync(caller.OrganizationId, query.CompanyName?.Trim(), query.Email?.Trim(), query.Phone?.Trim(), 5, ct))
                .Select(CrmDirectory.ToModel).ToList();
        return result;
    }

    // ── Cross-references (§12.5) ───────────────────────────────────────────────

    public async Task<CrossReferenceResult> GetCrossReferencesAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var lead = await Own(caller).AsNoTracking().FirstOrDefaultAsync(l => l.Uuid == uuid, ct) ?? throw new NotFoundException("Lead not found.");
        var result = new CrossReferenceResult();
        if (lead.AccountUuid is not { } accountUuid) return result; // BR-22 — no account, no cross-references

        var org = caller.OrganizationId;
        if ((await _directory.AccountsAsync(org, [accountUuid], ct)).TryGetValue(accountUuid, out var account))
            result.Account = CrmDirectory.ToModel(account);

        var sameAccount = Own(caller).AsNoTracking().Where(l => l.AccountUuid == accountUuid && l.Id != lead.Id);
        result.AccountLeads = await sameAccount.OrderByDescending(l => l.CreatedAt)
            .Select(l => new CrossReferenceItem { Uuid = l.Uuid, LeadNumber = l.LeadNumber, Topic = l.Topic, Status = l.Status, CreatedAt = l.CreatedAt })
            .Take(100).ToListAsync(ct);

        if (lead.ContactPersonId is { } contactId)
        {
            if ((await _directory.ContactsAsync(org, [contactId], ct)).TryGetValue(contactId, out var contact))
                result.ContactPerson = CrmDirectory.ToModel(contact);
            // CRITICAL (BR-22): same contact AND same account — never another company's leads.
            result.ContactLeads = await sameAccount.Where(l => l.ContactPersonId == contactId).OrderByDescending(l => l.CreatedAt)
                .Select(l => new CrossReferenceItem { Uuid = l.Uuid, LeadNumber = l.LeadNumber, Topic = l.Topic, Status = l.Status, CreatedAt = l.CreatedAt })
                .Take(100).ToListAsync(ct);
        }
        return result;
    }
}

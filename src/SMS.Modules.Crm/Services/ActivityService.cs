using Microsoft.EntityFrameworkCore;
using SMS.Modules.Crm.Data;
using SMS.Modules.Crm.Domain;
using SMS.Modules.Crm.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Files;
using SMS.Shared.Pagination;

namespace SMS.Modules.Crm.Services;

/// <summary>A file a caller attaches (bytes read by the controller, never written to disk — D-14).</summary>
public sealed record CrmUpload(string FileName, string? ContentType, byte[] Content);

/// <summary>A stored document's bytes for download.</summary>
public sealed record CrmFileContent(string FileName, string ContentType, byte[] Content);

/// <summary>A41 C8 / C9 — activities, participants and documents (API-CONTRACT §5).</summary>
public interface IActivityService
{
    Task<PaginatedResponse<ActivityModel>> ListAsync(CrmCaller caller, ActivityListFilter filter, CancellationToken ct = default);
    Task<ActivityModel> GetByIdAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<ActivityModel> CreateAsync(CrmCaller caller, ActivityUpsert req, CancellationToken ct = default);
    Task<ActivityModel> UpdateAsync(CrmCaller caller, Guid uuid, ActivityUpsert req, CancellationToken ct = default);
    Task<ActivityModel> CompleteAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task DeleteAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<IReadOnlyList<PendingActivityItem>> GetPendingFollowUpsAsync(CrmCaller caller, PendingActivityFilter filter, CancellationToken ct = default);
    Task<ActivityDocumentModel> AttachAsync(CrmCaller caller, Guid activityUuid, CrmUpload file, AttachDocumentRequest req, CancellationToken ct = default);
    Task<CrmFileContent> GetDocumentContentAsync(CrmCaller caller, Guid documentUuid, CancellationToken ct = default);
    Task DeleteDocumentAsync(CrmCaller caller, Guid documentUuid, CancellationToken ct = default);
}

internal sealed class ActivityService : IActivityService
{
    private const int MaxParticipants = 50;

    private readonly CrmDbContext _db;
    private readonly LeadService _leads;
    private readonly CrmDirectory _directory;
    private readonly ICrmEventPublisher _events;

    public ActivityService(CrmDbContext db, LeadService leads, CrmDirectory directory, ICrmEventPublisher events)
    {
        _db = db;
        _leads = leads;
        _directory = directory;
        _events = events;
    }

    private IQueryable<CrmActivity> Own(CrmCaller c) => _db.Activities.Where(a => a.OrganizationId == c.OrganizationId && !a.IsDeleted);

    private static IQueryable<CrmActivity> WithRefs(IQueryable<CrmActivity> q) =>
        q.Include(a => a.Lead).Include(a => a.Opportunity).ThenInclude(o => o!.SourceLead).Include(a => a.PipelineStage).Include(a => a.Participants);

    private async Task<CrmActivity> LoadAsync(CrmCaller caller, Guid uuid, CancellationToken ct) =>
        await WithRefs(Own(caller)).FirstOrDefaultAsync(a => a.Uuid == uuid, ct) ?? throw new NotFoundException("Activity not found.");

    private static bool Owns(CrmCaller caller, CrmActivity a) =>
        caller.IsManager || a.PerformedByUserId == caller.UserId || a.CreatedByUserId == caller.UserId;

    private static void RequireOwnership(CrmCaller caller, CrmActivity a)
    {
        if (!Owns(caller, a)) throw new ForbiddenException("You can only change activities you performed or logged.");
    }

    // ── Reads ───────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<ActivityModel>> ListAsync(CrmCaller caller, ActivityListFilter filter, CancellationToken ct = default)
    {
        var q = WithRefs(Own(caller).AsNoTracking());
        if (filter.LeadUuid is { } lead) q = q.Where(a => a.Lead != null && a.Lead.Uuid == lead);
        if (filter.OpportunityUuid is { } opp) q = q.Where(a => a.Opportunity != null && a.Opportunity.Uuid == opp);
        if (filter.Mine) q = q.Where(a => a.PerformedByUserId == caller.UserId);
        else if (filter.PerformedByUserId is { } user) q = q.Where(a => a.PerformedByUserId == user);
        if (CrmText.OneOf(filter.ActivityType, ActivityTypes.All, "Activity type") is { } type) q = q.Where(a => a.ActivityType == type);
        if (filter.From is { } f) q = q.Where(a => a.ActivityDate >= f.Date);
        if (filter.To is { } t) { var end = t.Date.AddDays(1); q = q.Where(a => a.ActivityDate < end); }
        switch (filter.State?.Trim().ToUpperInvariant())
        {
            case "OPEN": q = q.Where(a => !a.IsCompleted); break;
            case "DONE": q = q.Where(a => a.IsCompleted); break;
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim().ToLower();
            q = q.Where(a => a.Subject.ToLower().Contains(s) || (a.Lead != null && (a.Lead.CompanyName.ToLower().Contains(s) || a.Lead.LeadNumber.ToLower().Contains(s)))
                || (a.Opportunity != null && (a.Opportunity.Topic.ToLower().Contains(s) || a.Opportunity.OpportunityNumber.ToLower().Contains(s))));
        }
        var (page, size) = CrmText.Paging(filter.Page, filter.PageSize);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(a => a.ActivityDate).ThenByDescending(a => a.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PaginatedResponse<ActivityModel>
        {
            Data = await MapAsync(caller, rows, includeDocuments: false, ct), TotalRecords = total, Page = page, PageSize = size,
            TotalPages = (int)Math.Ceiling(total / (double)size)
        };
    }

    public async Task<ActivityModel> GetByIdAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var a = await WithRefs(Own(caller).AsNoTracking()).FirstOrDefaultAsync(x => x.Uuid == uuid, ct) ?? throw new NotFoundException("Activity not found.");
        return (await MapAsync(caller, [a], includeDocuments: true, ct))[0];
    }

    public async Task<IReadOnlyList<PendingActivityItem>> GetPendingFollowUpsAsync(CrmCaller caller, PendingActivityFilter filter, CancellationToken ct = default)
    {
        var q = WithRefs(Own(caller).AsNoTracking())
            .Where(a => !a.IsCompleted && (a.FollowUpDate != null || a.DueDate != null || a.ScheduledDate != null));
        if (filter.UserId is { } user) q = q.Where(a => a.PerformedByUserId == user);
        else if (filter.Mine || !caller.IsManager) q = q.Where(a => a.PerformedByUserId == caller.UserId);
        var rows = await q.OrderBy(a => a.FollowUpDate ?? a.DueDate ?? a.ScheduledDate).Take(500).ToListAsync(ct);
        var models = await MapAsync(caller, rows, includeDocuments: false, ct);
        var today = DateTime.UtcNow.Date;
        var items = rows.Select((a, i) =>
        {
            var (reason, due) = a.FollowUpDate is { } fu ? ("FOLLOW_UP", fu) : a.DueDate is { } dd ? ("TASK", dd) : ("SCHEDULED", a.ScheduledDate!.Value);
            return new PendingActivityItem { Activity = models[i], Reason = reason, DueOn = due, Overdue = due.Date < today };
        });
        if (filter.DueBy is { } by) items = items.Where(x => x.DueOn.Date <= by.Date);
        return items.OrderBy(x => x.DueOn).ToList();
    }

    // ── Create / update ────────────────────────────────────────────────────────

    public async Task<ActivityModel> CreateAsync(CrmCaller caller, ActivityUpsert req, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        CrmLead? lead = null;
        CrmOpportunity? opp = null;
        if (req.OpportunityUuid is { } oppUuid)
            opp = await _db.Opportunities.Include(o => o.SourceLead).FirstOrDefaultAsync(o => o.OrganizationId == org && o.Uuid == oppUuid && !o.IsDeleted, ct)
                ?? throw new BadRequestException("The opportunity was not found.");
        if (req.LeadUuid is { } leadUuid)
        {
            lead = await _db.Leads.Include(l => l.PipelineStage).FirstOrDefaultAsync(l => l.OrganizationId == org && l.Uuid == leadUuid && !l.IsDeleted, ct)
                ?? throw new BadRequestException("The lead was not found.");
            if (opp is not null && opp.SourceLeadId != lead.Id) throw new BadRequestException("The lead is not the opportunity's source lead.");
            // §10.1 — after conversion new activities belong to the opportunity.
            if (lead.Status == LeadStatuses.Converted && opp is null && lead.ConvertedOpportunityId is { } convertedId)
            {
                opp = await _db.Opportunities.FirstAsync(o => o.Id == convertedId, ct);
                lead = null;
            }
            else if (opp is not null) lead = null; // one parent per activity; the opportunity shows its lead's history (BR-18)
        }
        if (lead is null && opp is null) throw new BadRequestException("An activity must belong to a lead or an opportunity."); // BR-17

        var activity = new CrmActivity
        {
            OrganizationId = org, LeadId = lead?.Id, OpportunityId = opp?.Id, CreatedByUserId = caller.UserId, UpdatedByUserId = caller.UserId,
            ActivityType = CrmText.OneOf(req.ActivityType, ActivityTypes.All, "Activity type") ?? throw new BadRequestException("Activity type is required."),
            PipelineStageId = lead?.PipelineStageId ?? opp?.PipelineStageId
        };
        var accountUuid = opp?.AccountUuid ?? lead?.AccountUuid;
        await ApplyAsync(caller, activity, req, accountUuid, isCreate: true, ct);
        _db.Activities.Add(activity);

        // D-10 / BR-08 — a conversation with a NEW lead makes it CONTACTED (same save).
        LeadStatusChangedEvent? statusEvent = null;
        if (lead is not null && CountsAsContact(activity))
            statusEvent = await _leads.OnContactActivityAsync(caller, lead, activity.Subject, ct);

        await _db.SaveChangesAsync(ct);
        await _events.PublishAsync(new ActivityCreatedEvent(activity.Uuid, lead?.Uuid, opp?.Uuid, activity.ActivityType, org), ct);
        if (statusEvent is not null) await _events.PublishAsync(statusEvent, ct);
        return await GetByIdAsync(caller, activity.Uuid, ct);
    }

    public async Task<ActivityModel> UpdateAsync(CrmCaller caller, Guid uuid, ActivityUpsert req, CancellationToken ct = default)
    {
        var activity = await LoadAsync(caller, uuid, ct);
        RequireOwnership(caller, activity);
        if (CrmText.OneOf(req.ActivityType, ActivityTypes.All, "Activity type") is { } type) activity.ActivityType = type;
        await ApplyAsync(caller, activity, req, activity.Opportunity?.AccountUuid ?? activity.Lead?.AccountUuid, isCreate: false, ct);
        activity.UpdatedByUserId = caller.UserId;
        var statusEvent = activity.Lead is { } lead && CountsAsContact(activity)
            ? await _leads.OnContactActivityAsync(caller, lead, activity.Subject, ct) : null;
        await _db.SaveChangesAsync(ct);
        if (statusEvent is not null) await _events.PublishAsync(statusEvent, ct);
        return await GetByIdAsync(caller, uuid, ct);
    }

    public async Task<ActivityModel> CompleteAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var activity = await LoadAsync(caller, uuid, ct);
        RequireOwnership(caller, activity);
        if (activity.IsCompleted) throw new ConflictException("The activity is already completed.");
        activity.IsCompleted = true;
        activity.CompletedDate = DateTime.UtcNow;
        activity.UpdatedByUserId = caller.UserId;
        var statusEvent = activity.Lead is { } lead && CountsAsContact(activity)
            ? await _leads.OnContactActivityAsync(caller, lead, activity.Subject, ct) : null;
        await _db.SaveChangesAsync(ct);
        if (statusEvent is not null) await _events.PublishAsync(statusEvent, ct);
        return await GetByIdAsync(caller, uuid, ct);
    }

    public async Task DeleteAsync(CrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var activity = await LoadAsync(caller, uuid, ct);
        activity.IsDeleted = true;
        activity.UpdatedByUserId = caller.UserId;
        foreach (var doc in await _db.ActivityDocuments.Where(d => d.ActivityId == activity.Id && !d.IsDeleted).ToListAsync(ct))
            doc.IsDeleted = true;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>D-10 — a conversation that took place (not a still-open scheduled one).</summary>
    private static bool CountsAsContact(CrmActivity a) =>
        ActivityTypes.Contact.Contains(a.ActivityType) && (a.ScheduledDate is null || a.IsCompleted);

    private async Task ApplyAsync(CrmCaller caller, CrmActivity a, ActivityUpsert r, Guid? accountUuid, bool isCreate, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        a.Subject = CrmText.Required(r.Subject, 200, "Subject");
        a.Description = CrmHtml.Sanitize(r.Description);
        a.Outcome = CrmText.OneOf(r.Outcome, ActivityOutcomes.All, "Outcome");
        a.OutcomeNotes = CrmText.Optional(r.OutcomeNotes, 2000, "Outcome notes");
        a.ActivityDate = r.ActivityDate is { } when ? DateTime.SpecifyKind(when, DateTimeKind.Utc) : (isCreate ? DateTime.UtcNow : a.ActivityDate);
        if (r.DurationMinutes is < 0 or > 10080) throw new BadRequestException("Duration must be between 0 and 10080 minutes.");
        a.DurationMinutes = r.DurationMinutes;
        a.ScheduledDate = r.ScheduledDate;
        a.DueDate = r.DueDate;
        if (r.IsCompleted is { } done && done != a.IsCompleted)
        {
            a.IsCompleted = done;
            a.CompletedDate = done ? DateTime.UtcNow : null;
        }
        a.FollowUpDate = r.FollowUpDate?.Date;
        a.FollowUpType = CrmText.OneOf(r.FollowUpType, ActivityTypes.All, "Follow-up type");
        a.FollowUpNotes = CrmText.Optional(r.FollowUpNotes, 500, "Follow-up notes");
        if (a.FollowUpDate is null && (a.FollowUpType is not null || a.FollowUpNotes is not null))
            throw new BadRequestException("Give the follow-up date.");

        var performer = r.PerformedByUserId is > 0 ? r.PerformedByUserId.Value : (isCreate ? caller.UserId : a.PerformedByUserId);
        if (performer != a.PerformedByUserId)
        {
            if (performer != caller.UserId && !caller.IsManager)
                throw new ForbiddenException("Only a sales manager can log an activity for someone else.");
            if (performer != caller.UserId) await _directory.RequireActiveUserAsync(org, performer, "person who performed the activity", ct);
            a.PerformedByUserId = performer;
        }

        if (r.ContactPersonId is { } contactId && contactId != a.ContactPersonId) await RequireAccountContactAsync(org, contactId, accountUuid, ct);
        a.ContactPersonId = r.ContactPersonId;

        if (r.Participants is { } list)
        {
            if (list.Count > MaxParticipants) throw new BadRequestException($"An activity can have at most {MaxParticipants} participants.");
            a.Participants.Clear();
            foreach (var p in list)
            {
                var type = CrmText.OneOf(p.ParticipantType, ParticipantTypes.All, "Participant type") ?? throw new BadRequestException("Participant type is required.");
                var participant = new CrmActivityParticipant
                {
                    OrganizationId = org, ParticipantType = type, Role = CrmText.OneOf(p.Role, ParticipantRoles.All, "Participant role")
                };
                switch (type)
                {
                    case ParticipantTypes.InternalUser:
                        participant.UserId = p.UserId is > 0 ? p.UserId : throw new BadRequestException("Choose the internal participant.");
                        await _directory.RequireActiveUserAsync(org, participant.UserId.Value, "participant", ct);
                        break;
                    case ParticipantTypes.CustomerContact:
                        participant.ContactPersonId = p.ContactPersonId is > 0 ? p.ContactPersonId : throw new BadRequestException("Choose the customer contact.");
                        await RequireAccountContactAsync(org, participant.ContactPersonId.Value, accountUuid, ct);
                        break;
                    default:
                        participant.ExternalName = CrmText.Required(p.ExternalName, 200, "Participant name");
                        participant.ExternalEmail = CrmText.Email(p.ExternalEmail, "Participant email");
                        break;
                }
                a.Participants.Add(participant);
            }
        }
    }

    /// <summary>BR-22/23 — a contact on an activity is one of the parent's account's contacts.</summary>
    private async Task RequireAccountContactAsync(Guid org, int contactId, Guid? accountUuid, CancellationToken ct)
    {
        if (_directory.Accounts is not { } accounts) return;
        var contact = (await accounts.GetContactsAsync(org, [contactId], ct)).GetValueOrDefault(contactId)
            ?? throw new BadRequestException("The contact was not found.");
        if (accountUuid is null) throw new BadRequestException("The lead is not linked to an account yet, so no customer contact can be chosen.");
        if (contact.AccountUuid != accountUuid) throw new BadRequestException("The contact belongs to another account.");
    }

    // ── Documents (D-14) ───────────────────────────────────────────────────────

    public async Task<ActivityDocumentModel> AttachAsync(CrmCaller caller, Guid activityUuid, CrmUpload file, AttachDocumentRequest req, CancellationToken ct = default)
    {
        var activity = await LoadAsync(caller, activityUuid, ct);
        if (UploadRules.Refusal(file.FileName, file.Content.LongLength, file.ContentType) is { } refusal) throw new BadRequestException(refusal);
        var fileName = UploadRules.DisplayName(file.FileName)!;
        var stored = new CrmActivityFile
        {
            OrganizationId = caller.OrganizationId, FileName = fileName, ContentType = UploadRules.ContentTypeOf(file.FileName),
            SizeBytes = file.Content.LongLength, Content = file.Content, UploadedBy = caller.UserId
        };
        // §11 — the stage the lead / opportunity is in when the document is shared.
        var stageId = activity.LeadId is { } leadId
            ? await _db.Leads.Where(l => l.Id == leadId).Select(l => l.PipelineStageId).FirstOrDefaultAsync(ct)
            : await _db.Opportunities.Where(o => o.Id == activity.OpportunityId).Select(o => o.PipelineStageId).FirstOrDefaultAsync(ct);
        var doc = new CrmActivityDocument
        {
            OrganizationId = caller.OrganizationId, ActivityId = activity.Id, LeadId = activity.LeadId, OpportunityId = activity.OpportunityId,
            DocumentType = CrmText.OneOf(req.DocumentType, DocumentTypes.All, "Document type") ?? "OTHER",
            Direction = CrmText.OneOf(req.Direction, DocumentDirections.All, "Direction") ?? throw new BadRequestException("Direction is required."),
            DocumentName = CrmText.Optional(req.DocumentName, 200, "Document name") ?? UploadRules.DisplayName(file.FileName, 200)!,
            Notes = CrmText.Optional(req.Notes, 500, "Notes"), PipelineStageId = stageId, SharedByUserId = caller.UserId, File = stored
        };
        _db.ActivityDocuments.Add(doc);
        await _db.SaveChangesAsync(ct);
        var model = await DocumentsAsync(caller, [doc.Id], ct);
        return model[0];
    }

    public async Task<CrmFileContent> GetDocumentContentAsync(CrmCaller caller, Guid documentUuid, CancellationToken ct = default)
    {
        var doc = await _db.ActivityDocuments.AsNoTracking().Include(d => d.File)
            .FirstOrDefaultAsync(d => d.OrganizationId == caller.OrganizationId && d.Uuid == documentUuid && !d.IsDeleted, ct)
            ?? throw new NotFoundException("Document not found.");
        return new CrmFileContent(doc.File.FileName, doc.File.ContentType, doc.File.Content);
    }

    public async Task DeleteDocumentAsync(CrmCaller caller, Guid documentUuid, CancellationToken ct = default)
    {
        var doc = await _db.ActivityDocuments.FirstOrDefaultAsync(d => d.OrganizationId == caller.OrganizationId && d.Uuid == documentUuid && !d.IsDeleted, ct)
            ?? throw new NotFoundException("Document not found.");
        if (!caller.IsManager && doc.SharedByUserId != caller.UserId)
            throw new ForbiddenException("You can only remove documents you shared.");
        doc.IsDeleted = true;
        await _db.SaveChangesAsync(ct);
    }

    internal async Task<List<ActivityDocumentModel>> DocumentsAsync(CrmCaller caller, IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        var docs = await _db.ActivityDocuments.AsNoTracking().Include(d => d.Activity).Include(d => d.PipelineStage).Include(d => d.File)
            .Where(d => d.OrganizationId == caller.OrganizationId && ids.Contains(d.Id))
            .Select(d => new { Doc = d, ActivityUuid = d.Activity != null ? (Guid?)d.Activity.Uuid : null, Stage = d.PipelineStage != null ? d.PipelineStage.Name : null,
                d.File.ContentType, d.File.SizeBytes })
            .ToListAsync(ct);
        var users = await _directory.UsersAsync(caller.OrganizationId, docs.Select(d => (int?)d.Doc.SharedByUserId), ct);
        return docs.Select(d => MapDocument(d.Doc, d.ActivityUuid, d.Stage, d.ContentType, d.SizeBytes, users.GetValueOrDefault(d.Doc.SharedByUserId)?.Name)).ToList();
    }

    internal static ActivityDocumentModel MapDocument(CrmActivityDocument d, Guid? activityUuid, string? stage, string contentType, long size, string? sharedBy) => new()
    {
        Uuid = d.Uuid, ActivityUuid = activityUuid, DocumentType = d.DocumentType, DocumentName = d.DocumentName, Direction = d.Direction,
        Notes = d.Notes, ContentType = contentType, SizeBytes = size, SharedByUserId = d.SharedByUserId, SharedByName = sharedBy,
        SharedAt = d.SharedAt, PipelineStageName = stage
    };

    // ── Mapping ───────────────────────────────────────────────────────────────

    private async Task<List<ActivityModel>> MapAsync(CrmCaller caller, IReadOnlyList<CrmActivity> rows, bool includeDocuments, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        var users = await _directory.UsersAsync(org, rows.SelectMany(a => a.Participants.Select(p => p.UserId).Append(a.PerformedByUserId)), ct);
        var contacts = await _directory.ContactsAsync(org, rows.SelectMany(a => a.Participants.Select(p => p.ContactPersonId).Append(a.ContactPersonId)), ct);
        var docsByActivity = new Dictionary<Guid, List<ActivityDocumentModel>>();
        if (includeDocuments)
        {
            var activityIds = rows.Select(r => r.Id).ToList();
            var docIds = await _db.ActivityDocuments.Where(d => d.OrganizationId == org && d.ActivityId != null && activityIds.Contains(d.ActivityId.Value) && !d.IsDeleted)
                .Select(d => d.Id).ToListAsync(ct);
            foreach (var d in await DocumentsAsync(caller, docIds, ct))
                (docsByActivity.TryGetValue(d.ActivityUuid!.Value, out var l) ? l : docsByActivity[d.ActivityUuid!.Value] = []).Add(d);
        }
        return rows.Select(a => new ActivityModel
        {
            Uuid = a.Uuid, LeadUuid = a.Lead?.Uuid, LeadNumber = a.Lead?.LeadNumber,
            OpportunityUuid = a.Opportunity?.Uuid, OpportunityNumber = a.Opportunity?.OpportunityNumber,
            RegardingName = a.Lead?.CompanyName ?? a.Opportunity?.SourceLead?.CompanyName ?? a.Opportunity?.Topic,
            ActivityType = a.ActivityType, Subject = a.Subject, Description = a.Description, Outcome = a.Outcome, OutcomeNotes = a.OutcomeNotes,
            ActivityDate = DateTime.SpecifyKind(a.ActivityDate, DateTimeKind.Utc), DurationMinutes = a.DurationMinutes,
            ScheduledDate = a.ScheduledDate, DueDate = a.DueDate, CompletedDate = a.CompletedDate, IsCompleted = a.IsCompleted,
            PerformedByUserId = a.PerformedByUserId, PerformedByName = users.GetValueOrDefault(a.PerformedByUserId)?.Name,
            ContactPersonId = a.ContactPersonId, ContactPersonName = a.ContactPersonId is { } c ? contacts.GetValueOrDefault(c)?.Name : null,
            FollowUpDate = a.FollowUpDate, FollowUpType = a.FollowUpType, FollowUpNotes = a.FollowUpNotes, PipelineStageName = a.PipelineStage?.Name,
            Participants = a.Participants.OrderBy(p => p.Id).Select(p => new ActivityParticipantModel
            {
                ParticipantType = p.ParticipantType, UserId = p.UserId, ContactPersonId = p.ContactPersonId,
                Name = p.UserId is { } u ? users.GetValueOrDefault(u)?.Name : p.ContactPersonId is { } cp ? contacts.GetValueOrDefault(cp)?.Name : p.ExternalName,
                ExternalName = p.ExternalName, ExternalEmail = p.ExternalEmail, Role = p.Role
            }).ToList(),
            Documents = docsByActivity.GetValueOrDefault(a.Uuid) ?? [],
            CreatedAt = a.CreatedAt,
            CanEdit = caller.Has(PermissionCodes.CRM_EDIT_ACTIVITY) && Owns(caller, a)
        }).ToList();
    }
}

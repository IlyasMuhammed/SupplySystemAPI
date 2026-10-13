using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Crm.Data;
using SMS.Modules.Crm.Domain;
using SMS.Modules.Crm.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Crm.Services;

/// <summary>A41 C10 §12 / D-15 — the unified timeline of a lead or an opportunity, and its Excel export.</summary>
public interface ITimelineService
{
    /// <param name="search">Optional free text: keeps entries whose text (subject, notes, people, documents, values) contains it.</param>
    Task<TimelineResponse> GetTimelineAsync(CrmCaller caller, Guid? leadUuid, Guid? opportunityUuid, int page, int pageSize,
        string? search = null, CancellationToken ct = default);
}

public interface ITimelineExportService
{
    /// <summary>The workbook (Summary, Timeline, Documents) and its file name.</summary>
    Task<(byte[] Content, string FileName)> ExportToExcelAsync(CrmCaller caller, Guid? leadUuid, Guid? opportunityUuid, CancellationToken ct = default);
}

internal sealed class TimelineService : ITimelineService, ITimelineExportService
{
    private readonly CrmDbContext _db;
    private readonly CrmDirectory _directory;
    private readonly ICrmSalesDocumentGateway? _sales;

    public TimelineService(CrmDbContext db, CrmDirectory directory, ICrmSalesDocumentGateway? sales = null)
    {
        _db = db;
        _directory = directory;
        _sales = sales;
    }

    public async Task<TimelineResponse> GetTimelineAsync(CrmCaller caller, Guid? leadUuid, Guid? opportunityUuid, int page, int pageSize,
        string? search = null, CancellationToken ct = default)
    {
        var (entries, _) = await BuildAsync(caller, leadUuid, opportunityUuid, ct);
        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term)) entries = entries.Where(e => Matches(e, term)).ToList();
        var (p, size) = CrmText.Paging(page, pageSize, 200);
        return new TimelineResponse { Timeline = entries.Skip((p - 1) * size).Take(size).ToList(), TotalCount = entries.Count, Page = p, PageSize = size };
    }

    /// <summary>Case-insensitive "contains" over everything the timeline shows as text; rich-text notes are matched as plain text.</summary>
    private static bool Matches(TimelineEntry e, string term)
    {
        IEnumerable<string?> Texts()
        {
            yield return e.Subject;
            yield return CrmHtml.PlainText(e.Description);
            yield return e.OutcomeNotes;
            yield return e.FollowUpNotes;
            yield return e.Reason;
            yield return e.FromValue;
            yield return e.ToValue;
            yield return e.PerformedBy?.Name;
            yield return e.ChangedBy?.Name;
            yield return e.ContactPerson?.Name;
            yield return e.DocumentName;
            yield return e.ScmNumber;
            yield return e.LeadNumber;
            yield return e.ActivityType?.Replace('_', ' ');
            yield return e.Outcome?.Replace('_', ' ');
            yield return e.EntryType.Replace('_', ' ');
            foreach (var d in e.Documents) yield return d.DocumentName;
        }
        return Texts().Any(t => t is not null && t.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record Subject(CrmLead? Lead, CrmOpportunity? Opportunity);

    private async Task<(List<TimelineEntry> Entries, Subject Subject)> BuildAsync(CrmCaller caller, Guid? leadUuid, Guid? opportunityUuid, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        CrmLead? lead = null;
        CrmOpportunity? opp = null;
        if (opportunityUuid is { } ou)
        {
            opp = await _db.Opportunities.AsNoTracking().Include(o => o.SourceLead).ThenInclude(l => l.LeadSource)
                      .Include(o => o.Campaign).Include(o => o.PipelineStage)
                      .FirstOrDefaultAsync(o => o.OrganizationId == org && o.Uuid == ou && !o.IsDeleted, ct)
                  ?? throw new NotFoundException("Opportunity not found.");
            lead = opp.SourceLead;
        }
        else if (leadUuid is { } lu)
            lead = await _db.Leads.AsNoTracking().Include(l => l.LeadSource).Include(l => l.Campaign).Include(l => l.PipelineStage)
                       .FirstOrDefaultAsync(l => l.OrganizationId == org && l.Uuid == lu && !l.IsDeleted, ct)
                   ?? throw new NotFoundException("Lead not found.");
        else throw new BadRequestException("Give a lead or an opportunity.");

        var entries = new List<TimelineEntry>();
        if (opp is not null)
        {
            entries.AddRange(await EntriesAsync(caller, HistoryEntities.Opportunity, opp.Id, fromLead: false, null, ct));
            if (_sales is not null)
                entries.AddRange((await _sales.ListForOpportunityAsync(org, opp.Uuid, ct)).Select(d => new TimelineEntry
                {
                    EntryType = "SCM_DOCUMENT", Date = d.CreatedAt, ScmKind = d.Kind, ScmUuid = d.Uuid, ScmNumber = d.Number,
                    ScmStatus = d.Status, GrandTotal = d.GrandTotal, CurrencyCode = d.CurrencyCode
                }));
            // BR-18 — the source lead's history, below the conversion line.
            if (lead is not null) entries.AddRange(await EntriesAsync(caller, HistoryEntities.Lead, lead.Id, fromLead: true, lead.LeadNumber, ct));
        }
        else entries.AddRange(await EntriesAsync(caller, HistoryEntities.Lead, lead!.Id, fromLead: false, lead.LeadNumber, ct));

        // Newest first; on the same instant the opportunity's own entries come before the lead's.
        return (entries.OrderByDescending(e => e.Date).ThenBy(e => e.FromLead).ToList(), new Subject(lead, opp));
    }

    private async Task<List<TimelineEntry>> EntriesAsync(CrmCaller caller, string entityType, int entityId, bool fromLead, string? leadNumber, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        var isLead = entityType == HistoryEntities.Lead;
        var activities = await _db.Activities.AsNoTracking().Include(a => a.PipelineStage)
            .Where(a => a.OrganizationId == org && !a.IsDeleted && (isLead ? a.LeadId == entityId : a.OpportunityId == entityId))
            .ToListAsync(ct);
        var docs = await _db.ActivityDocuments.AsNoTracking().Include(d => d.PipelineStage).Include(d => d.Activity)
            .Where(d => d.OrganizationId == org && !d.IsDeleted && (isLead ? d.LeadId == entityId : d.OpportunityId == entityId))
            .Select(d => new { Doc = d, ActivityUuid = d.Activity != null ? (Guid?)d.Activity.Uuid : null,
                Stage = d.PipelineStage != null ? d.PipelineStage.Name : null, d.File.ContentType, d.File.SizeBytes })
            .ToListAsync(ct);
        var history = await _db.RecordHistory.AsNoTracking()
            .Where(h => h.OrganizationId == org && h.EntityType == entityType && h.EntityId == entityId).ToListAsync(ct);

        var userIds = activities.Select(a => (int?)a.PerformedByUserId)
            .Concat(docs.Select(d => (int?)d.Doc.SharedByUserId))
            .Concat(history.Select(h => (int?)h.UserId))
            .Concat(history.Where(h => h.Kind == HistoryKinds.Assignment).SelectMany(h => new[] { ParseUser(h.FromValue), ParseUser(h.ToValue) }));
        var users = await _directory.UsersAsync(org, userIds, ct);
        var contacts = await _directory.ContactsAsync(org, activities.Select(a => a.ContactPersonId), ct);
        TimelineUser? U(int? id, string? fallback = null) =>
            id is > 0 ? new TimelineUser { UserId = id.Value, Name = users.GetValueOrDefault(id.Value)?.Name ?? fallback ?? $"User {id}" }
            : fallback is not null ? new TimelineUser { UserId = 0, Name = fallback } : null;

        var docModels = docs.Select(d => (d.Doc.ActivityId, Model: ActivityService.MapDocument(d.Doc, d.ActivityUuid, d.Stage, d.ContentType, d.SizeBytes,
            users.GetValueOrDefault(d.Doc.SharedByUserId)?.Name))).ToList();

        var entries = new List<TimelineEntry>();
        foreach (var a in activities)
        {
            entries.Add(new TimelineEntry
            {
                EntryType = "ACTIVITY", Date = DateTime.SpecifyKind(a.ActivityDate, DateTimeKind.Utc), FromLead = fromLead, LeadNumber = leadNumber,
                ActivityUuid = a.Uuid, ActivityType = a.ActivityType, Subject = a.Subject, Description = a.Description, Outcome = a.Outcome,
                OutcomeNotes = a.OutcomeNotes, IsCompleted = a.IsCompleted, PerformedBy = U(a.PerformedByUserId),
                ContactPerson = a.ContactPersonId is { } c && contacts.TryGetValue(c, out var contact) ? CrmDirectory.ToModel(contact) : null,
                DurationMinutes = a.DurationMinutes, PipelineStage = a.PipelineStage?.Name,
                FollowUpDate = a.FollowUpDate, FollowUpType = a.FollowUpType, FollowUpNotes = a.FollowUpNotes,
                Documents = docModels.Where(d => d.ActivityId == a.Id).Select(d => d.Model).ToList()
            });
        }
        // Documents never filed under an activity (none are today — every upload goes through one) stand on their own.
        foreach (var d in docModels.Where(d => d.ActivityId is null))
            entries.Add(new TimelineEntry
            {
                EntryType = "DOCUMENT", Date = d.Model.SharedAt, FromLead = fromLead, LeadNumber = leadNumber, DocumentUuid = d.Model.Uuid,
                DocumentType = d.Model.DocumentType, DocumentName = d.Model.DocumentName, Direction = d.Model.Direction,
                PerformedBy = U(d.Model.SharedByUserId), PipelineStage = d.Model.PipelineStageName, Documents = [d.Model]
            });
        foreach (var h in history)
        {
            var entry = new TimelineEntry
            {
                Date = DateTime.SpecifyKind(h.At, DateTimeKind.Utc), FromLead = fromLead, LeadNumber = leadNumber, Reason = h.Reason,
                ChangedBy = U(h.UserId, h.UserName), FromValue = h.FromValue, ToValue = h.ToValue,
                EntryType = h.Kind switch
                {
                    HistoryKinds.Created    => "CREATED",
                    HistoryKinds.Status     => "STATUS_CHANGE",
                    HistoryKinds.Stage      => "STAGE_CHANGE",
                    HistoryKinds.Assignment => "ASSIGNMENT",
                    HistoryKinds.Converted  => "CONVERTED",
                    _                       => h.Kind
                }
            };
            if (h.Kind == HistoryKinds.Assignment)
            {
                entry.FromValue = U(ParseUser(h.FromValue))?.Name;
                entry.ToValue = U(ParseUser(h.ToValue))?.Name ?? "Unassigned";
            }
            entries.Add(entry);
        }
        return entries;
    }

    private static int? ParseUser(string? v) => int.TryParse(v, out var id) ? id : null;

    // ── Excel export (register 1J) ─────────────────────────────────────────────

    public async Task<(byte[] Content, string FileName)> ExportToExcelAsync(CrmCaller caller, Guid? leadUuid, Guid? opportunityUuid, CancellationToken ct = default)
    {
        var (entries, subject) = await BuildAsync(caller, leadUuid, opportunityUuid, ct);
        var org = caller.OrganizationId;
        var lead = subject.Lead;
        var opp = subject.Opportunity;

        var accountUuid = opp?.AccountUuid ?? lead?.AccountUuid;
        var accounts = await _directory.AccountsAsync(org, [accountUuid], ct);
        var contacts = await _directory.ContactsAsync(org, [opp?.ContactPersonId ?? lead?.ContactPersonId], ct);
        var owner = await _directory.UserNameAsync(org, opp?.AssignedToUserId ?? lead?.AssignedToUserId, ct);
        var documents = entries.SelectMany(e => e.Documents).GroupBy(d => d.Uuid).Select(g => g.First()).OrderByDescending(d => d.SharedAt).ToList();
        var activityCount = entries.Count(e => e.EntryType == "ACTIVITY");

        using var wb = new XLWorkbook();

        var summary = wb.Worksheets.Add("Summary");
        var rows = new List<(string Label, object? Value)>();
        if (opp is not null)
        {
            rows.Add(("Opportunity", opp.OpportunityNumber));
            rows.Add(("Topic", opp.Topic));
            rows.Add(("Status", opp.Status));
            rows.Add(("Stage", opp.PipelineStage?.Name));
            rows.Add(("Estimated revenue", opp.EstimatedRevenue is { } er ? $"{er:N2} {opp.CurrencyCode}" : null));
            rows.Add(("Actual revenue", opp.ActualRevenue is { } ar ? $"{ar:N2} {opp.CurrencyCode}" : null));
            rows.Add(("Source lead", lead?.LeadNumber));
        }
        else if (lead is not null)
        {
            rows.Add(("Lead", lead.LeadNumber));
            rows.Add(("Topic", lead.Topic));
            rows.Add(("Status", lead.Status));
            rows.Add(("Stage", lead.PipelineStage?.Name));
            rows.Add(("Estimated revenue", lead.EstimatedRevenue is { } er ? $"{er:N2} {lead.CurrencyCode}" : null));
        }
        rows.Add(("Account", accountUuid is { } au && accounts.TryGetValue(au, out var acc) ? $"{acc.Name} ({acc.Code})" : lead?.CompanyName));
        rows.Add(("Contact", contacts.Values.FirstOrDefault()?.Name ?? lead?.FullName));
        rows.Add(("Source", lead?.LeadSource?.Name));
        rows.Add(("Campaign", (opp?.Campaign ?? lead?.Campaign)?.Name));
        rows.Add(("Owner", owner));
        rows.Add(("Created", (opp?.CreatedAt ?? lead?.CreatedAt)?.ToString("yyyy-MM-dd HH:mm") + " UTC"));
        rows.Add(("Activities", activityCount));
        rows.Add(("Documents", documents.Count));
        summary.Cell(1, 1).Value = "Field";
        summary.Cell(1, 2).Value = "Value";
        for (var i = 0; i < rows.Count; i++)
        {
            summary.Cell(i + 2, 1).Value = rows[i].Label;
            summary.Cell(i + 2, 2).Value = rows[i].Value switch { null => Blank.Value, int n => n, var v => v.ToString() };
        }
        Finish(summary);

        var timeline = wb.Worksheets.Add("Timeline");
        string[] headers = ["Date", "Type", "Subject", "Description", "Outcome", "Performed By", "Contact Person", "Duration (minutes)",
            "Pipeline Stage", "Follow-up Date", "Follow-up Notes", "From Lead"];
        for (var c = 0; c < headers.Length; c++) timeline.Cell(1, c + 1).Value = headers[c];
        var r = 2;
        foreach (var e in entries)
        {
            timeline.Cell(r, 1).Value = e.Date;
            timeline.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
            timeline.Cell(r, 2).Value = Txt(e.EntryType == "ACTIVITY" ? e.ActivityType : e.EntryType == "SCM_DOCUMENT" ? e.ScmKind : e.EntryType);
            timeline.Cell(r, 3).Value = Txt(e.EntryType switch
            {
                "ACTIVITY"      => e.Subject,
                "STATUS_CHANGE" => $"Status: {e.FromValue ?? "—"} → {e.ToValue}",
                "STAGE_CHANGE"  => $"Stage: {e.FromValue ?? "—"} → {e.ToValue}",
                "ASSIGNMENT"    => $"Assigned: {e.FromValue ?? "—"} → {e.ToValue}",
                "CREATED"       => "Created",
                "CONVERTED"     => $"Converted to {e.ToValue}",
                "SCM_DOCUMENT"  => $"{e.ScmNumber} ({e.ScmStatus})" + (e.GrandTotal is { } gt ? $" {gt:N2} {e.CurrencyCode}" : string.Empty),
                "DOCUMENT"      => e.DocumentName,
                _               => e.Subject
            });
            timeline.Cell(r, 4).Value = Txt(e.EntryType == "ACTIVITY" ? CrmHtml.PlainText(e.Description) : e.Reason);
            timeline.Cell(r, 5).Value = Txt(e.Outcome);
            timeline.Cell(r, 6).Value = Txt((e.PerformedBy ?? e.ChangedBy)?.Name);
            timeline.Cell(r, 7).Value = Txt(e.ContactPerson?.Name);
            if (e.DurationMinutes is { } dur) timeline.Cell(r, 8).Value = dur;
            timeline.Cell(r, 9).Value = Txt(e.PipelineStage);
            if (e.FollowUpDate is { } fu) { timeline.Cell(r, 10).Value = fu; timeline.Cell(r, 10).Style.DateFormat.Format = "yyyy-mm-dd"; }
            timeline.Cell(r, 11).Value = Txt(e.FollowUpNotes);
            timeline.Cell(r, 12).Value = e.FromLead ? "Yes" : string.Empty;
            r++;
        }
        Finish(timeline);
        timeline.Column(4).Width = Math.Min(timeline.Column(4).Width, 80);
        timeline.Column(4).Style.Alignment.WrapText = true;

        var docsSheet = wb.Worksheets.Add("Documents");
        string[] docHeaders = ["Date", "Name", "Type", "Direction", "Shared By", "Pipeline Stage", "Notes"];
        for (var c = 0; c < docHeaders.Length; c++) docsSheet.Cell(1, c + 1).Value = docHeaders[c];
        r = 2;
        foreach (var d in documents)
        {
            docsSheet.Cell(r, 1).Value = d.SharedAt;
            docsSheet.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
            docsSheet.Cell(r, 2).Value = d.DocumentName;
            docsSheet.Cell(r, 3).Value = d.DocumentType;
            docsSheet.Cell(r, 4).Value = d.Direction;
            docsSheet.Cell(r, 5).Value = Txt(d.SharedByName);
            docsSheet.Cell(r, 6).Value = Txt(d.PipelineStageName);
            docsSheet.Cell(r, 7).Value = Txt(d.Notes);
            r++;
        }
        Finish(docsSheet);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var number = opp?.OpportunityNumber ?? lead!.LeadNumber;
        return (ms.ToArray(), $"{number}-timeline.xlsx");
    }

    private static XLCellValue Txt(string? v) => string.IsNullOrEmpty(v) ? Blank.Value : v;

    private static void Finish(IXLWorksheet ws)
    {
        var header = ws.Row(1);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8EEF7");
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, 200);
        foreach (var col in ws.ColumnsUsed()) if (col.Width > 60) col.Width = 60;
    }
}

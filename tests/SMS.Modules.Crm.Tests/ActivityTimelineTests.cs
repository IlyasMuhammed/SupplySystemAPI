using ClosedXML.Excel;
using FluentAssertions;
using SMS.Modules.Crm.Models;
using SMS.Modules.Crm.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Crm.Tests;

/// <summary>Register 1G / 1H / 1J — activities, participants, documents, timeline and its export (TS-19..TS-22, BR-17..BR-20).</summary>
public sealed class ActivityTimelineTests
{
    private static CrmUpload Pdf(string name = "AC-Product-Catalog-2026.pdf") => new(name, "application/pdf", "%PDF-1.4 test"u8.ToArray());

    [Fact]
    public async Task An_activity_belongs_to_a_lead_or_an_opportunity_and_never_to_nothing()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        var onLead = await h.LogCallAsync(lead.Uuid);
        onLead.LeadUuid.Should().Be(lead.Uuid);
        onLead.PerformedByName.Should().Be("Salim Ahmed");
        onLead.PipelineStageName.Should().Be("New Lead", "the stage the lead was in when it happened");

        var (_, conv) = await h.ConvertedAsync();
        var onOpp = await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert { OpportunityUuid = conv.OpportunityUuid, ActivityType = "MEETING", Subject = "Kick-off" });
        onOpp.OpportunityUuid.Should().Be(conv.OpportunityUuid);

        await h.Activities.Invoking(a => a.CreateAsync(h.Rep(), new ActivityUpsert { ActivityType = "NOTE", Subject = "Orphan" }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*lead or an opportunity*");
        await h.Activities.Invoking(a => a.CreateAsync(h.Rep(), new ActivityUpsert { LeadUuid = lead.Uuid, ActivityType = "CHAT", Subject = "x" }))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task After_conversion_an_activity_logged_on_the_lead_goes_to_its_opportunity()
    {
        using var h = new CrmHarness();
        var (lead, conv) = await h.ConvertedAsync();
        var a = await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert { LeadUuid = lead.Uuid, ActivityType = "NOTE", Subject = "Late note" });
        a.OpportunityUuid.Should().Be(conv.OpportunityUuid);
        a.LeadUuid.Should().BeNull();
    }

    [Fact]
    public async Task BR19_notes_keep_rich_text_but_lose_scripts()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        var long_ = string.Concat(Enumerable.Repeat("<p>Customer confirmed <b>Haier 1.5-ton</b> inverter.</p>", 500));
        var a = await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert
        {
            LeadUuid = lead.Uuid, ActivityType = "MEETING", Subject = "Specs", Description = long_ + "<script>x()</script>"
        });
        a.Description.Should().StartWith("<p>Customer confirmed <b>Haier 1.5-ton</b> inverter.</p>").And.NotContain("script");
        a.Description!.Length.Should().BeGreaterThan(4000, "notes are nvarchar(max)");
    }

    [Fact]
    public async Task Completing_an_activity_stamps_it_and_a_completed_scheduled_call_counts_as_contact()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        var scheduled = await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert
        {
            LeadUuid = lead.Uuid, ActivityType = "PHONE_CALL", Subject = "Call back", ScheduledDate = DateTime.UtcNow.AddHours(2)
        });
        (await h.Leads.GetByIdAsync(h.Rep(), lead.Uuid)).Status.Should().Be("NEW");

        var done = await h.Activities.CompleteAsync(h.Rep(), scheduled.Uuid);
        done.IsCompleted.Should().BeTrue();
        done.CompletedDate.Should().NotBeNull();
        (await h.Leads.GetByIdAsync(h.Rep(), lead.Uuid)).Status.Should().Be("CONTACTED");
        await h.Activities.Invoking(a => a.CompleteAsync(h.Rep(), scheduled.Uuid)).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Participants_can_be_internal_users_customer_contacts_and_outsiders()
    {
        using var h = new CrmHarness();
        var (_, conv) = await h.ConvertedAsync();
        var contact = conv.ContactId;
        var a = await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert
        {
            OpportunityUuid = conv.OpportunityUuid, ActivityType = "MEETING", Subject = "Site meeting", DurationMinutes = 45,
            ContactPersonId = contact,
            Participants =
            [
                new() { ParticipantType = "INTERNAL_USER", UserId = 10, Role = "ORGANIZER" },
                new() { ParticipantType = "INTERNAL_USER", UserId = 11, Role = "ATTENDEE" },
                new() { ParticipantType = "CUSTOMER_CONTACT", ContactPersonId = contact },
                new() { ParticipantType = "EXTERNAL", ExternalName = "HVAC Consultant", ExternalEmail = "c@hvac.test", Role = "OPTIONAL" }
            ]
        });
        a.Participants.Select(p => p.Name).Should().Equal("Salim Ahmed", "Faisal Khan", "Ahmed Khan", "HVAC Consultant");
        a.ContactPersonName.Should().Be("Ahmed Khan");

        // A contact of another company is refused (BR-22).
        var otherAccount = h.Accounts.AddAccount(h.Org, "Other Co");
        var stranger = h.Accounts.AddContact(h.Org, otherAccount, "Ahmed Khan");
        await h.Activities.Invoking(x => x.CreateAsync(h.Rep(), new ActivityUpsert
        {
            OpportunityUuid = conv.OpportunityUuid, ActivityType = "MEETING", Subject = "Wrong", ContactPersonId = stranger
        })).Should().ThrowAsync<BadRequestException>().WithMessage("*another account*");
        await h.Activities.Invoking(x => x.CreateAsync(h.Rep(), new ActivityUpsert
        {
            OpportunityUuid = conv.OpportunityUuid, ActivityType = "MEETING", Subject = "No name",
            Participants = [new() { ParticipantType = "EXTERNAL" }]
        })).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task BR20_pending_follow_ups_are_the_uncompleted_activities_with_a_date_due()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        var call = await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert
        {
            LeadUuid = lead.Uuid, ActivityType = "PHONE_CALL", Subject = "Intro", FollowUpDate = DateTime.UtcNow.Date.AddDays(1),
            FollowUpType = "EMAIL_SENT", FollowUpNotes = "Send catalog"
        });
        await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert
        {
            LeadUuid = lead.Uuid, ActivityType = "TASK", Subject = "Prepare quote", DueDate = DateTime.UtcNow.Date.AddDays(-1)
        });
        await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert { LeadUuid = lead.Uuid, ActivityType = "NOTE", Subject = "Just a note" });
        await h.Activities.CreateAsync(h.Rep(11), new ActivityUpsert
        {
            LeadUuid = lead.Uuid, ActivityType = "TASK", Subject = "Someone else's", DueDate = DateTime.UtcNow.Date
        });

        var mine = await h.Activities.GetPendingFollowUpsAsync(h.Rep(), new PendingActivityFilter());
        mine.Select(p => (p.Activity.Subject, p.Reason, p.Overdue)).Should().Equal(("Prepare quote", "TASK", true), ("Intro", "FOLLOW_UP", false));

        await h.Activities.CompleteAsync(h.Rep(), call.Uuid);
        (await h.Activities.GetPendingFollowUpsAsync(h.Rep(), new PendingActivityFilter())).Should().ContainSingle();
        (await h.Activities.GetPendingFollowUpsAsync(h.Manager(), new PendingActivityFilter { Mine = false })).Should().HaveCount(2);
        await h.Activities.Invoking(a => a.CreateAsync(h.Rep(), new ActivityUpsert
        {
            LeadUuid = lead.Uuid, ActivityType = "NOTE", Subject = "x", FollowUpNotes = "no date"
        })).Should().ThrowAsync<BadRequestException>().WithMessage("*follow-up date*");
    }

    [Fact]
    public async Task A_rep_changes_only_their_own_activities()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        var a = await h.LogCallAsync(lead.Uuid);
        await h.Activities.Invoking(x => x.UpdateAsync(h.Rep(11), a.Uuid, new ActivityUpsert { Subject = "Hijack" }))
            .Should().ThrowAsync<ForbiddenException>();
        (await h.Activities.UpdateAsync(h.Manager(), a.Uuid, new ActivityUpsert { Subject = "Fixed" })).Subject.Should().Be("Fixed");
        await h.Activities.Invoking(x => x.CreateAsync(h.Rep(), new ActivityUpsert
        {
            LeadUuid = lead.Uuid, ActivityType = "NOTE", Subject = "For Faisal", PerformedByUserId = 11
        })).Should().ThrowAsync<ForbiddenException>();
    }

    // ── documents (1H) ──

    [Fact]
    public async Task TS22_a_document_attached_to_an_activity_is_stored_with_its_direction_and_stage()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        await h.LogCallAsync(lead.Uuid); // → CONTACTED, stage Initial Contact
        var shared = await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert { LeadUuid = lead.Uuid, ActivityType = "DOCUMENT_SHARED", Subject = "Catalog sent" });

        var doc = await h.Activities.AttachAsync(h.Rep(), shared.Uuid, Pdf(), new AttachDocumentRequest { DocumentType = "brochure", Direction = "SENT_TO_CUSTOMER", Notes = "Summer range" });
        doc.DocumentType.Should().Be("BROCHURE");
        doc.Direction.Should().Be("SENT_TO_CUSTOMER");
        doc.PipelineStageName.Should().Be("Initial Contact");
        doc.ContentType.Should().Be("application/pdf");
        doc.SharedByName.Should().Be("Salim Ahmed");

        var content = await h.Activities.GetDocumentContentAsync(h.Rep(), doc.Uuid);
        content.FileName.Should().Be("AC-Product-Catalog-2026.pdf");
        content.Content.Should().Equal("%PDF-1.4 test"u8.ToArray());

        var received = await h.Activities.AttachAsync(h.Rep(), shared.Uuid, Pdf("customer-po.pdf"), new AttachDocumentRequest { DocumentType = "CUSTOMER_PO", Direction = "RECEIVED_FROM_CUSTOMER" });
        var minutes = await h.Activities.AttachAsync(h.Rep(), shared.Uuid, Pdf("minutes.pdf"), new AttachDocumentRequest { Direction = "INTERNAL" });
        minutes.DocumentType.Should().Be("OTHER");
        (await h.Activities.GetByIdAsync(h.Rep(), shared.Uuid)).Documents.Select(d => d.Direction)
            .Should().BeEquivalentTo(["SENT_TO_CUSTOMER", "RECEIVED_FROM_CUSTOMER", "INTERNAL"]);

        await h.Activities.Invoking(a => a.AttachAsync(h.Rep(), shared.Uuid, new CrmUpload("evil.html", "text/html", "<script>"u8.ToArray()),
            new AttachDocumentRequest { Direction = "INTERNAL" })).Should().ThrowAsync<BadRequestException>();
        await h.Activities.Invoking(a => a.AttachAsync(h.Rep(), shared.Uuid, Pdf(), new AttachDocumentRequest())).Should().ThrowAsync<BadRequestException>();

        await h.Activities.Invoking(a => a.DeleteDocumentAsync(h.Rep(11), received.Uuid)).Should().ThrowAsync<ForbiddenException>();
        await h.Activities.DeleteDocumentAsync(h.Rep(), received.Uuid);
        await h.Activities.Invoking(a => a.GetDocumentContentAsync(h.Rep(), received.Uuid)).Should().ThrowAsync<NotFoundException>();
    }

    // ── timeline (C10) ──

    [Fact]
    public async Task TS19_the_lead_timeline_merges_activities_documents_and_status_changes_newest_first()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        await h.LogCallAsync(lead.Uuid, subject: "Initial contact with Ahmed Khan");
        var shared = await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert { LeadUuid = lead.Uuid, ActivityType = "DOCUMENT_SHARED", Subject = "Catalog" });
        await h.Activities.AttachAsync(h.Rep(), shared.Uuid, Pdf(), new AttachDocumentRequest { DocumentType = "BROCHURE", Direction = "SENT_TO_CUSTOMER" });
        await h.Leads.QualifyAsync(h.Rep(), lead.Uuid);

        var t = await h.Get<ITimelineService>().GetTimelineAsync(h.Rep(), lead.Uuid, null, 1, 50);
        t.Timeline.Select(e => e.Date).Should().BeInDescendingOrder();
        t.Timeline.Should().Contain(e => e.EntryType == "ACTIVITY" && e.ActivityType == "PHONE_CALL" && e.Subject == "Initial contact with Ahmed Khan");
        t.Timeline.Should().Contain(e => e.EntryType == "ACTIVITY" && e.ActivityType == "DOCUMENT_SHARED" && e.Documents.Count == 1);
        t.Timeline.Should().Contain(e => e.EntryType == "STATUS_CHANGE" && e.FromValue == "NEW" && e.ToValue == "CONTACTED");
        t.Timeline.Should().Contain(e => e.EntryType == "STATUS_CHANGE" && e.FromValue == "CONTACTED" && e.ToValue == "QUALIFIED"
            && e.ChangedBy!.Name == "Salim Ahmed");
        t.Timeline.Should().Contain(e => e.EntryType == "STAGE_CHANGE" && e.ToValue == "Qualification");
        t.Timeline.Should().Contain(e => e.EntryType == "CREATED");
        t.Timeline.Should().Contain(e => e.EntryType == "ASSIGNMENT" && e.ToValue == "Salim Ahmed");
        t.TotalCount.Should().Be(t.Timeline.Count);
    }

    [Fact]
    public async Task TS20_BR18_the_opportunity_timeline_shows_its_own_entries_then_the_lead_history_and_its_sales_documents()
    {
        using var h = new CrmHarness();
        var (lead, conv) = await h.ConvertedAsync();
        await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert { OpportunityUuid = conv.OpportunityUuid, ActivityType = "MEETING", Subject = "Proposal review" });
        await h.Opportunities.CreateInquiryAsync(h.Rep(), conv.OpportunityUuid);

        var t = await h.Get<ITimelineService>().GetTimelineAsync(h.Rep(), null, conv.OpportunityUuid, 1, 100);
        t.Timeline.Should().Contain(e => e.Subject == "Proposal review" && !e.FromLead);
        t.Timeline.Should().Contain(e => e.EntryType == "SCM_DOCUMENT" && e.ScmKind == "INQUIRY" && e.ScmNumber!.StartsWith("INQ-"));
        t.Timeline.Should().Contain(e => e.ActivityType == "PHONE_CALL" && e.FromLead && e.LeadNumber == lead.LeadNumber);
        t.Timeline.Should().Contain(e => e.EntryType == "CONVERTED" && e.FromLead && e.ToValue == conv.OpportunityNumber);
        t.Timeline.Should().Contain(e => e.EntryType == "CREATED" && !e.FromLead);
    }

    [Fact]
    public async Task The_timeline_pages()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        for (var i = 0; i < 12; i++)
            await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert { LeadUuid = lead.Uuid, ActivityType = "NOTE", Subject = $"Note {i}", ActivityDate = DateTime.UtcNow.AddMinutes(i) });

        var svc = h.Get<ITimelineService>();
        var all = await svc.GetTimelineAsync(h.Rep(), lead.Uuid, null, 1, 200);
        var page2 = await svc.GetTimelineAsync(h.Rep(), lead.Uuid, null, 2, 5);
        page2.Page.Should().Be(2);
        page2.PageSize.Should().Be(5);
        page2.TotalCount.Should().Be(all.TotalCount);
        page2.Timeline.Select(e => e.Date).Should().Equal(all.Timeline.Skip(5).Take(5).Select(e => e.Date));
    }

    [Fact]
    public async Task The_timeline_search_matches_text_case_insensitively_including_rich_text_notes_and_people()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        await h.LogCallAsync(lead.Uuid, subject: "Warehouse racking enquiry");
        await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert
            { LeadUuid = lead.Uuid, ActivityType = "NOTE", Subject = "Pricing", Description = "<p>Asked for a <strong>bulk discount</strong></p>" });
        var svc = h.Get<ITimelineService>();

        var bySubject = await svc.GetTimelineAsync(h.Rep(), lead.Uuid, null, 1, 50, "RACKING enq");
        // The call itself, plus the automatic NEW → CONTACTED change whose reason names the call (BR-08); nothing else.
        bySubject.Timeline.Should().ContainSingle(e => e.EntryType == "ACTIVITY").Which.Subject.Should().Be("Warehouse racking enquiry");
        bySubject.Timeline.Should().NotContain(e => e.Subject == "Pricing" || e.EntryType == "CREATED");
        bySubject.TotalCount.Should().Be(bySubject.Timeline.Count);

        // Text inside formatting tags matches; the tag names themselves do not.
        (await svc.GetTimelineAsync(h.Rep(), lead.Uuid, null, 1, 50, "BULK DISCOUNT")).Timeline.Should().ContainSingle(e => e.Subject == "Pricing");
        (await svc.GetTimelineAsync(h.Rep(), lead.Uuid, null, 1, 50, "strong")).Timeline.Should().BeEmpty();

        // People: the rep's name finds the entries they performed or changed.
        (await svc.GetTimelineAsync(h.Rep(), lead.Uuid, null, 1, 50, "Salim")).Timeline.Should().NotBeEmpty();
        (await svc.GetTimelineAsync(h.Rep(), lead.Uuid, null, 1, 50, "no such text")).TotalCount.Should().Be(0);

        var everything = await svc.GetTimelineAsync(h.Rep(), lead.Uuid, null, 1, 50, "  ");
        everything.TotalCount.Should().Be((await svc.GetTimelineAsync(h.Rep(), lead.Uuid, null, 1, 50)).TotalCount);
    }

    [Fact]
    public async Task TS21_the_excel_export_has_summary_timeline_and_documents_sheets()
    {
        using var h = new CrmHarness();
        var lead = await h.CreateLeadAsync();
        await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert
        {
            LeadUuid = lead.Uuid, ActivityType = "PHONE_CALL", Subject = "Intro call", Description = "<p>Interested in <b>1.5-ton</b> AC</p>",
            Outcome = "FOLLOW_UP_NEEDED", DurationMinutes = 15, FollowUpDate = new DateTime(2026, 10, 6), FollowUpNotes = "Send catalog"
        });
        var shared = await h.Activities.CreateAsync(h.Rep(), new ActivityUpsert { LeadUuid = lead.Uuid, ActivityType = "DOCUMENT_SHARED", Subject = "Catalog" });
        await h.Activities.AttachAsync(h.Rep(), shared.Uuid, Pdf(), new AttachDocumentRequest { DocumentType = "BROCHURE", Direction = "SENT_TO_CUSTOMER", Notes = "Summer" });

        var (content, name) = await h.Get<ITimelineExportService>().ExportToExcelAsync(h.Rep(), lead.Uuid, null);
        name.Should().Be($"{lead.LeadNumber}-timeline.xlsx");
        using var wb = new XLWorkbook(new MemoryStream(content));
        wb.Worksheets.Select(w => w.Name).Should().Equal("Summary", "Timeline", "Documents");

        var summary = wb.Worksheet("Summary");
        summary.Cell(2, 1).GetString().Should().Be("Lead");
        summary.Cell(2, 2).GetString().Should().Be(lead.LeadNumber);
        summary.RowsUsed().Any(r => r.Cell(1).GetString() == "Activities" && r.Cell(2).GetValue<int>() == 2).Should().BeTrue();
        summary.RowsUsed().Any(r => r.Cell(1).GetString() == "Documents" && r.Cell(2).GetValue<int>() == 1).Should().BeTrue();

        var timeline = wb.Worksheet("Timeline");
        timeline.Row(1).Cells(1, 11).Select(c => c.GetString()).Should().Equal("Date", "Type", "Subject", "Description", "Outcome",
            "Performed By", "Contact Person", "Duration (minutes)", "Pipeline Stage", "Follow-up Date", "Follow-up Notes");
        timeline.Row(1).Style.Font.Bold.Should().BeTrue();
        timeline.SheetView.SplitRow.Should().Be(1, "the header row is frozen");
        var call = timeline.RowsUsed().Single(r => r.Cell(3).GetString() == "Intro call");
        call.Cell(4).GetString().Should().Be("Interested in 1.5-ton AC");
        call.Cell(5).GetString().Should().Be("FOLLOW_UP_NEEDED");
        call.Cell(8).GetValue<int>().Should().Be(15);
        call.Cell(11).GetString().Should().Be("Send catalog");
        var dates = timeline.RowsUsed().Skip(1).Select(r => r.Cell(1).GetDateTime()).ToList();
        dates.Should().BeInDescendingOrder();

        var docs = wb.Worksheet("Documents");
        docs.Cell(2, 2).GetString().Should().Be("AC-Product-Catalog-2026.pdf");
        docs.Cell(2, 3).GetString().Should().Be("BROCHURE");
        docs.Cell(2, 4).GetString().Should().Be("SENT_TO_CUSTOMER");
        docs.Cell(2, 7).GetString().Should().Be("Summer");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Crm.Domain;

namespace SMS.Modules.Crm.Data.Maps;

// A41 M1–M11 (+ D-11 history, D-14 files, D-16 API keys). Table and index names as the spec's §-tables; money decimal(18,2);
// CHECK constraints list the CrmCodes vocabularies. In-CRM references are FKs with Restrict (no cascades) except the
// participants of an activity (spec: ON DELETE CASCADE).

internal static class CrmColumn
{
    public const string Money = "decimal(18,2)";
    public const string Date  = "date";

    /// <summary>"[Col] IN ('A','B')" for a CHECK constraint.</summary>
    public static string In(string column, IEnumerable<string> values) =>
        $"[{column}] IN ({string.Join(", ", values.Select(v => $"'{v}'"))})";
}

internal sealed class CrmLeadSourceMap : IEntityTypeConfiguration<CrmLeadSource>
{
    public void Configure(EntityTypeBuilder<CrmLeadSource> b)
    {
        b.ToTable("LeadSources");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UQ_LeadSources_OrgCode");
    }
}

internal sealed class CrmCampaignMap : IEntityTypeConfiguration<CrmCampaign>
{
    public void Configure(EntityTypeBuilder<CrmCampaign> b)
    {
        b.ToTable("Campaigns", t =>
        {
            t.HasCheckConstraint("CK_Campaigns_Type", CrmColumn.In(nameof(CrmCampaign.CampaignType), CampaignTypes.All));
            t.HasCheckConstraint("CK_Campaigns_Status", CrmColumn.In(nameof(CrmCampaign.Status), CampaignStatuses.All));
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.CampaignType).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.StartDate).HasColumnType(CrmColumn.Date);
        b.Property(x => x.EndDate).HasColumnType(CrmColumn.Date);
        b.Property(x => x.BudgetAmount).HasColumnType(CrmColumn.Money);
        b.Property(x => x.CurrencyCode).HasMaxLength(3);
        b.Property(x => x.ExternalReference).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UQ_Campaigns_OrgCode");
        b.HasIndex(x => new { x.OrganizationId, x.Status }).HasFilter("[IsDeleted] = 0").HasDatabaseName("IX_Campaigns_Status");
        b.HasIndex(x => new { x.OrganizationId, x.StartDate, x.EndDate }).HasDatabaseName("IX_Campaigns_Dates");
        b.HasOne(x => x.LeadSource).WithMany().HasForeignKey(x => x.LeadSourceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CrmSalesTeamMap : IEntityTypeConfiguration<CrmSalesTeam>
{
    public void Configure(EntityTypeBuilder<CrmSalesTeam> b)
    {
        b.ToTable("SalesTeams");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("UQ_SalesTeams_OrgCode");
        b.HasOne(x => x.DefaultCampaign).WithMany().HasForeignKey(x => x.DefaultCampaignId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Members).WithOne(x => x.SalesTeam).HasForeignKey(x => x.SalesTeamId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CrmSalesTeamMemberMap : IEntityTypeConfiguration<CrmSalesTeamMember>
{
    public void Configure(EntityTypeBuilder<CrmSalesTeamMember> b)
    {
        b.ToTable("SalesTeamMembers", t => t.HasCheckConstraint("CK_SalesTeamMembers_Role", CrmColumn.In(nameof(CrmSalesTeamMember.Role), TeamMemberRoles.All)));
        b.HasKey(x => x.Id);
        b.Property(x => x.Role).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.SalesTeamId, x.UserId }).IsUnique().HasDatabaseName("UQ_SalesTeamMembers_TeamUser");
        b.HasIndex(x => x.UserId).HasDatabaseName("IX_SalesTeamMembers_User");
    }
}

internal sealed class CrmPipelineStageMap : IEntityTypeConfiguration<CrmPipelineStage>
{
    public void Configure(EntityTypeBuilder<CrmPipelineStage> b)
    {
        b.ToTable("PipelineStages", t =>
        {
            t.HasCheckConstraint("CK_PipelineStages_Type", CrmColumn.In(nameof(CrmPipelineStage.StageType), StageTypes.All));
            t.HasCheckConstraint("CK_PipelineStages_Probability", "[WinProbability] IS NULL OR [WinProbability] BETWEEN 0 AND 100");
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.StageType).HasMaxLength(20).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        // SQL Server treats NULLs as equal in a unique index, so the organization's default pipeline (team NULL) is unique too.
        b.HasIndex(x => new { x.OrganizationId, x.SalesTeamId, x.Code }).IsUnique().HasDatabaseName("UQ_PipelineStages_OrgTeamCode");
        b.HasIndex(x => new { x.OrganizationId, x.SalesTeamId, x.StageType, x.Sequence }).HasDatabaseName("IX_PipelineStages_Sequence");
        b.HasOne(x => x.SalesTeam).WithMany().HasForeignKey(x => x.SalesTeamId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CrmLeadMap : IEntityTypeConfiguration<CrmLead>
{
    public void Configure(EntityTypeBuilder<CrmLead> b)
    {
        b.ToTable("Leads", t =>
        {
            t.HasCheckConstraint("CK_Leads_Status", CrmColumn.In(nameof(CrmLead.Status), LeadStatuses.All));
            t.HasCheckConstraint("CK_Leads_Rating", CrmColumn.In(nameof(CrmLead.Rating), LeadRatings.All));
            t.HasCheckConstraint("CK_Leads_Priority", CrmColumn.In(nameof(CrmLead.Priority), LeadPriorities.All));
            t.HasCheckConstraint("CK_Leads_Timeframe",
                $"[PurchaseTimeframe] IS NULL OR {CrmColumn.In(nameof(CrmLead.PurchaseTimeframe), PurchaseTimeframes.All)}");
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.LeadNumber).HasMaxLength(20).IsRequired();
        b.Property(x => x.Topic).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(4000);
        b.Property(x => x.Salutation).HasMaxLength(10);
        b.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
        b.Property(x => x.LastName).HasMaxLength(100).IsRequired();
        b.Property(x => x.FullName).HasMaxLength(201).IsRequired();
        b.Property(x => x.JobTitle).HasMaxLength(100);
        b.Property(x => x.Email).HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(30);
        b.Property(x => x.MobilePhone).HasMaxLength(30);
        b.Property(x => x.CompanyName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Website).HasMaxLength(500);
        b.Property(x => x.AddressLine1).HasMaxLength(200);
        b.Property(x => x.AddressLine2).HasMaxLength(200);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.StateProvince).HasMaxLength(100);
        b.Property(x => x.PostalCode).HasMaxLength(20);
        b.Property(x => x.Country).HasMaxLength(100);
        b.Property(x => x.SourceMedium).HasMaxLength(50);
        b.Property(x => x.SourceReference).HasMaxLength(200);
        b.Property(x => x.ExternalSourceSystem).HasMaxLength(30);
        b.Property(x => x.ExternalSourceId).HasMaxLength(200);
        b.Property(x => x.BudgetAmount).HasColumnType(CrmColumn.Money);
        b.Property(x => x.BudgetCurrencyCode).HasMaxLength(3);
        b.Property(x => x.PurchaseTimeframe).HasMaxLength(20);
        b.Property(x => x.Rating).HasMaxLength(10).IsRequired();
        b.Property(x => x.Priority).HasMaxLength(10).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.StatusReason).HasMaxLength(500);
        b.Property(x => x.EstimatedRevenue).HasColumnType(CrmColumn.Money);
        b.Property(x => x.EstimatedCloseDate).HasColumnType(CrmColumn.Date);
        b.Property(x => x.CurrencyCode).HasMaxLength(3);
        b.Property(x => x.CreatedByName).HasMaxLength(150);

        // §6.2 — the 12 indexes.
        b.HasIndex(x => new { x.OrganizationId, x.LeadNumber }).IsUnique().HasDatabaseName("UQ_Leads_OrgNumber");
        b.HasIndex(x => new { x.OrganizationId, x.Status }).HasFilter("[IsDeleted] = 0").HasDatabaseName("IX_Leads_Status");
        b.HasIndex(x => x.AssignedToUserId).HasFilter("[IsDeleted] = 0").HasDatabaseName("IX_Leads_AssignedTo");
        b.HasIndex(x => new { x.OrganizationId, x.SalesTeamId }).HasFilter("[IsDeleted] = 0").HasDatabaseName("IX_Leads_SalesTeam");
        b.HasIndex(x => x.CampaignId).HasDatabaseName("IX_Leads_Campaign");
        b.HasIndex(x => x.AccountUuid).HasFilter("[AccountUuid] IS NOT NULL").HasDatabaseName("IX_Leads_Account");
        b.HasIndex(x => x.ContactPersonId).HasFilter("[ContactPersonId] IS NOT NULL").HasDatabaseName("IX_Leads_ContactPerson");
        b.HasIndex(x => new { x.OrganizationId, x.Email }).HasFilter("[Email] IS NOT NULL").HasDatabaseName("IX_Leads_Email");
        b.HasIndex(x => new { x.OrganizationId, x.MobilePhone }).HasFilter("[MobilePhone] IS NOT NULL").HasDatabaseName("IX_Leads_Phone");
        b.HasIndex(x => new { x.OrganizationId, x.CompanyName }).HasDatabaseName("IX_Leads_CompanyName");
        b.HasIndex(x => new { x.OrganizationId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("IX_Leads_CreatedAt");
        b.HasIndex(x => new { x.OrganizationId, x.PipelineStageId }).HasFilter("[IsDeleted] = 0").HasDatabaseName("IX_Leads_PipelineStage");

        b.HasOne(x => x.LeadSource).WithMany().HasForeignKey(x => x.LeadSourceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Campaign).WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.SalesTeam).WithMany().HasForeignKey(x => x.SalesTeamId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PipelineStage).WithMany().HasForeignKey(x => x.PipelineStageId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CrmOpportunity>().WithMany().HasForeignKey(x => x.ConvertedOpportunityId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CrmOpportunityMap : IEntityTypeConfiguration<CrmOpportunity>
{
    public void Configure(EntityTypeBuilder<CrmOpportunity> b)
    {
        b.ToTable("Opportunities", t => t.HasCheckConstraint("CK_Opportunities_Status", CrmColumn.In(nameof(CrmOpportunity.Status), OpportunityStatuses.All)));
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OpportunityNumber).HasMaxLength(20).IsRequired();
        b.Property(x => x.Topic).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(4000);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.EstimatedRevenue).HasColumnType(CrmColumn.Money);
        b.Property(x => x.ActualRevenue).HasColumnType(CrmColumn.Money);
        b.Property(x => x.CurrencyCode).HasMaxLength(3);
        b.Property(x => x.EstimatedCloseDate).HasColumnType(CrmColumn.Date);
        b.Property(x => x.ActualCloseDate).HasColumnType(CrmColumn.Date);
        b.Property(x => x.WinLossReason).HasMaxLength(500);
        b.Property(x => x.CompetitorName).HasMaxLength(200);

        // §9.2 — the 8 indexes.
        b.HasIndex(x => new { x.OrganizationId, x.OpportunityNumber }).IsUnique().HasDatabaseName("UQ_Opportunities_OrgNumber");
        b.HasIndex(x => x.AccountUuid).HasDatabaseName("IX_Opportunities_Account");
        b.HasIndex(x => x.ContactPersonId).HasFilter("[ContactPersonId] IS NOT NULL").HasDatabaseName("IX_Opportunities_Contact");
        b.HasIndex(x => x.SourceLeadId).HasDatabaseName("IX_Opportunities_SourceLead");
        b.HasIndex(x => new { x.OrganizationId, x.Status }).HasFilter("[IsDeleted] = 0").HasDatabaseName("IX_Opportunities_Status");
        b.HasIndex(x => new { x.OrganizationId, x.PipelineStageId }).HasFilter("[IsDeleted] = 0").HasDatabaseName("IX_Opportunities_PipelineStage");
        b.HasIndex(x => x.AssignedToUserId).HasFilter("[IsDeleted] = 0").HasDatabaseName("IX_Opportunities_AssignedTo");
        b.HasIndex(x => new { x.OrganizationId, x.EstimatedCloseDate }).HasDatabaseName("IX_Opportunities_CloseDate");

        b.HasOne(x => x.SourceLead).WithMany().HasForeignKey(x => x.SourceLeadId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Campaign).WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PipelineStage).WithMany().HasForeignKey(x => x.PipelineStageId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.SalesTeam).WithMany().HasForeignKey(x => x.SalesTeamId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CrmActivityMap : IEntityTypeConfiguration<CrmActivity>
{
    public void Configure(EntityTypeBuilder<CrmActivity> b)
    {
        b.ToTable("Activities", t =>
        {
            t.HasCheckConstraint("CK_Activities_ParentRef", "[LeadId] IS NOT NULL OR [OpportunityId] IS NOT NULL");
            t.HasCheckConstraint("CK_Activities_Type", CrmColumn.In(nameof(CrmActivity.ActivityType), ActivityTypes.All));
            t.HasCheckConstraint("CK_Activities_Outcome", $"[Outcome] IS NULL OR {CrmColumn.In(nameof(CrmActivity.Outcome), ActivityOutcomes.All)}");
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.ActivityType).HasMaxLength(30).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description); // nvarchar(max) — rich text (BR-19)
        b.Property(x => x.Outcome).HasMaxLength(30);
        b.Property(x => x.OutcomeNotes).HasMaxLength(2000);
        b.Property(x => x.FollowUpDate).HasColumnType(CrmColumn.Date);
        b.Property(x => x.FollowUpType).HasMaxLength(30);
        b.Property(x => x.FollowUpNotes).HasMaxLength(500);

        // §10.2 — the 7 indexes.
        b.HasIndex(x => new { x.LeadId, x.ActivityDate }).IsDescending(false, true).HasFilter("[LeadId] IS NOT NULL").HasDatabaseName("IX_Activities_Lead");
        b.HasIndex(x => new { x.OpportunityId, x.ActivityDate }).IsDescending(false, true).HasFilter("[OpportunityId] IS NOT NULL").HasDatabaseName("IX_Activities_Opportunity");
        b.HasIndex(x => new { x.PerformedByUserId, x.ActivityDate }).IsDescending(false, true).HasDatabaseName("IX_Activities_PerformedBy");
        b.HasIndex(x => new { x.OrganizationId, x.ActivityType, x.ActivityDate }).IsDescending(false, false, true).HasDatabaseName("IX_Activities_Type");
        b.HasIndex(x => new { x.OrganizationId, x.FollowUpDate }).HasFilter("[FollowUpDate] IS NOT NULL AND [IsCompleted] = 0").HasDatabaseName("IX_Activities_FollowUp");
        b.HasIndex(x => new { x.OrganizationId, x.DueDate }).HasFilter("[DueDate] IS NOT NULL AND [IsCompleted] = 0").HasDatabaseName("IX_Activities_DueDate");
        b.HasIndex(x => new { x.OrganizationId, x.ScheduledDate }).HasFilter("[ScheduledDate] IS NOT NULL AND [IsCompleted] = 0").HasDatabaseName("IX_Activities_Scheduled");

        b.HasOne(x => x.Lead).WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Opportunity).WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PipelineStage).WithMany().HasForeignKey(x => x.PipelineStageId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Participants).WithOne(x => x.Activity).HasForeignKey(x => x.ActivityId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CrmActivityParticipantMap : IEntityTypeConfiguration<CrmActivityParticipant>
{
    public void Configure(EntityTypeBuilder<CrmActivityParticipant> b)
    {
        b.ToTable("ActivityParticipants", t =>
        {
            t.HasCheckConstraint("CK_ActivityParticipants_Type", CrmColumn.In(nameof(CrmActivityParticipant.ParticipantType), ParticipantTypes.All));
            t.HasCheckConstraint("CK_ActivityParticipants_Role", $"[Role] IS NULL OR {CrmColumn.In(nameof(CrmActivityParticipant.Role), ParticipantRoles.All)}");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.ParticipantType).HasMaxLength(20).IsRequired();
        b.Property(x => x.ExternalName).HasMaxLength(200);
        b.Property(x => x.ExternalEmail).HasMaxLength(200);
        b.Property(x => x.Role).HasMaxLength(30);
        b.HasIndex(x => x.ActivityId).HasDatabaseName("IX_ActivityParticipants_Activity");
    }
}

internal sealed class CrmActivityDocumentMap : IEntityTypeConfiguration<CrmActivityDocument>
{
    public void Configure(EntityTypeBuilder<CrmActivityDocument> b)
    {
        b.ToTable("ActivityDocuments", t =>
        {
            t.HasCheckConstraint("CK_ActivityDocuments_Type", CrmColumn.In(nameof(CrmActivityDocument.DocumentType), DocumentTypes.All));
            t.HasCheckConstraint("CK_ActivityDocuments_Direction", CrmColumn.In(nameof(CrmActivityDocument.Direction), DocumentDirections.All));
        });
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.DocumentType).HasMaxLength(30).IsRequired();
        b.Property(x => x.DocumentName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Direction).HasMaxLength(30).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasIndex(x => x.ActivityId).HasFilter("[ActivityId] IS NOT NULL").HasDatabaseName("IX_ActivityDocuments_Activity");
        b.HasIndex(x => x.LeadId).HasFilter("[LeadId] IS NOT NULL").HasDatabaseName("IX_ActivityDocuments_Lead");
        b.HasIndex(x => x.OpportunityId).HasFilter("[OpportunityId] IS NOT NULL").HasDatabaseName("IX_ActivityDocuments_Opportunity");
        b.HasOne(x => x.Activity).WithMany().HasForeignKey(x => x.ActivityId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CrmLead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CrmOpportunity>().WithMany().HasForeignKey(x => x.OpportunityId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.File).WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.PipelineStage).WithMany().HasForeignKey(x => x.PipelineStageId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CrmActivityFileMap : IEntityTypeConfiguration<CrmActivityFile>
{
    public void Configure(EntityTypeBuilder<CrmActivityFile> b)
    {
        b.ToTable("ActivityFiles");
        b.HasKey(x => x.Id);
        b.Property(x => x.FileName).HasMaxLength(255).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(150).IsRequired();
        b.Property(x => x.Content).IsRequired();
    }
}

internal sealed class CrmRecordHistoryMap : IEntityTypeConfiguration<CrmRecordHistory>
{
    public void Configure(EntityTypeBuilder<CrmRecordHistory> b)
    {
        b.ToTable("RecordHistory");
        b.HasKey(x => x.Id);
        b.Property(x => x.EntityType).HasMaxLength(20).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.Property(x => x.FromValue).HasMaxLength(100);
        b.Property(x => x.ToValue).HasMaxLength(100);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.UserName).HasMaxLength(150);
        b.HasIndex(x => new { x.OrganizationId, x.EntityType, x.EntityId, x.At }).HasDatabaseName("IX_RecordHistory_Entity");
    }
}

internal sealed class CrmLeadApiKeyMap : IEntityTypeConfiguration<CrmLeadApiKey>
{
    public void Configure(EntityTypeBuilder<CrmLeadApiKey> b)
    {
        b.ToTable("LeadApiKeys");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.KeyPrefix).HasMaxLength(16).IsRequired();
        b.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.KeyHash).IsUnique().HasDatabaseName("UQ_LeadApiKeys_Hash");
        b.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasDatabaseName("UQ_LeadApiKeys_OrgName");
    }
}

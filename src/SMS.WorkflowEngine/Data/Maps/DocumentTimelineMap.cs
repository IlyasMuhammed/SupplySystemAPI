using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.WorkflowEngine.Domain;

namespace SMS.WorkflowEngine.Data.Maps;

internal sealed class DocumentTimelineMap : IEntityTypeConfiguration<DocumentTimeline>
{
    public void Configure(EntityTypeBuilder<DocumentTimeline> b)
    {
        b.ToTable("document_timelines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.TraceId).IsRequired();
        // A29-P5-08 §13.7/§17.2 — unique per organization, not globally: a trace belongs to one
        // tenant, and the write path's "first insert for this trace" race relies on exactly this
        // conflict, so it has to be keyed the way every read is filtered (org first).
        b.HasIndex(x => new { x.OrganizationId, x.TraceId }).IsUnique();
        // Still indexed on its own, non-unique: lookups that carry no organization — a super admin,
        // or a background job that was handed none — would otherwise scan the table.
        b.HasIndex(x => x.TraceId);

        b.Property(x => x.Events).HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.ChainRootType).HasMaxLength(30);
        b.Property(x => x.ChainRootRef).HasMaxLength(100);

        b.Property(x => x.FirstEventAt).IsRequired();
        b.Property(x => x.LastEventAt).IsRequired();
        b.Property(x => x.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

        b.Property(x => x.RowVersion).IsRowVersion();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
    }
}

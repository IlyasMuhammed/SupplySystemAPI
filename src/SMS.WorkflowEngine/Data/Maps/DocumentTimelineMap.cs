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
        b.HasIndex(x => x.TraceId).IsUnique();

        b.Property(x => x.Events).HasColumnType("nvarchar(max)").IsRequired();
        b.Property(x => x.ChainRootType).HasMaxLength(30);
        b.Property(x => x.ChainRootRef).HasMaxLength(100);

        b.Property(x => x.FirstEventAt).IsRequired();
        b.Property(x => x.LastEventAt).IsRequired();
        b.Property(x => x.CreatedAt).HasDefaultValueSql("GETUTCDATE()");

        b.Property(x => x.RowVersion).IsRowVersion();
    }
}

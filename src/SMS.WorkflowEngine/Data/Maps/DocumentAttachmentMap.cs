using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.WorkflowEngine.Domain;

namespace SMS.WorkflowEngine.Data.Maps;

internal sealed class DocumentAttachmentMap : IEntityTypeConfiguration<DocumentAttachment>
{
    public void Configure(EntityTypeBuilder<DocumentAttachment> b)
    {
        b.ToTable("document_attachments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.InterfaceCode).HasMaxLength(30).IsRequired();
        b.Property(x => x.DocumentId).IsRequired();
        b.HasIndex(x => new { x.InterfaceCode, x.DocumentId });

        b.Property(x => x.FileName).HasMaxLength(255).IsRequired();
        b.Property(x => x.FileUrl).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.Property(x => x.IsDelete).HasDefaultValue(false);
        b.Property(x => x.UploadedDate).HasDefaultValueSql("GETUTCDATE()");
    }
}
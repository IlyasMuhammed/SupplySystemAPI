using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.WorkflowEngine.Domain;

namespace SMS.WorkflowEngine.Data.Maps;

internal sealed class DocumentAttachmentContentMap : IEntityTypeConfiguration<DocumentAttachmentContent>
{
    public void Configure(EntityTypeBuilder<DocumentAttachmentContent> b)
    {
        b.ToTable("document_attachment_contents");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.Content).HasColumnType("varbinary(max)").IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        b.Property(x => x.RequiredPermission).HasMaxLength(100);

        // One file per attachment. Removed with it, should the attachment row ever be hard-deleted.
        b.HasOne(x => x.DocumentAttachment)
         .WithOne()
         .HasForeignKey<DocumentAttachmentContent>(x => x.DocumentAttachmentId)
         .OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.DocumentAttachmentId).IsUnique();

        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P7-09 §9.1 — an invoice's PDF filed as an attachment on the invoice. The attachment store is
/// a mock here and its validation has its own tests in the workflow engine; what is checked is that
/// what this service hands it is exactly what that validation accepts, filed under the right document
/// with the right permission on it.
/// </summary>
public class SalesInvoiceDocumentArchiveTests
{
    private const int User = 42;
    private static readonly Guid InvoiceId = Guid.NewGuid();
    private static readonly byte[] Pdf = "%PDF-1.7\n% SINV-20260920-0001\n%%EOF"u8.ToArray();

    private sealed class ListLogger : ILogger<SalesInvoiceDocumentArchive>
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) =>
            Entries.Add((level, formatter(state, error), error));
    }

    private sealed class Rig
    {
        public Mock<ISalesInvoiceService> Invoices { get; } = new();
        public Mock<ISalesInvoiceDocumentService> Documents { get; } = new();
        public Mock<IAttachmentService> Attachments { get; } = new();
        public ListLogger Log { get; } = new();
        public GeneratedAttachmentRequest? Filed { get; private set; }
        public SalesInvoiceDocumentArchive Archive { get; }

        public Rig(string status = "ISSUED", decimal balance = 3780m)
        {
            Invoices.Setup(i => i.GetAsync(InvoiceId)).ReturnsAsync(new SalesInvoiceDetailModel
            {
                Uuid = InvoiceId, InvoiceNumber = "SINV-20260920-0001", Status = status, BalanceDue = balance, CurrencyCode = "PKR"
            });
            Documents.Setup(d => d.GeneratePdfAsync(InvoiceId)).ReturnsAsync(new SalesInvoicePdf("SINV-20260920-0001.pdf", Pdf));
            Attachments.Setup(a => a.StoreGeneratedAsync(It.IsAny<GeneratedAttachmentRequest>(), It.IsAny<int>()))
                       .Callback<GeneratedAttachmentRequest, int>((r, _) => Filed = r)
                       .ReturnsAsync(new StoredAttachment(Guid.Parse("11111111-1111-1111-1111-111111111111"), false));
            Archive = new SalesInvoiceDocumentArchive(Invoices.Object, Documents.Object, Attachments.Object, Log);
        }
    }

    // ── Filing on demand ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_pdf_is_filed_on_the_invoice_with_the_permission_that_opens_the_invoice()
    {
        var rig = new Rig();

        var stored = await rig.Archive.FilePdfAsync(InvoiceId, User);

        stored.Uuid.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var filed = rig.Filed!;
        filed.InterfaceCode.Should().Be("SALES_INVOICE", "what an invoice's attachment panel is opened with");
        filed.DocumentId.Should().Be(InvoiceId);
        filed.FileName.Should().Be("SINV-20260920-0001.pdf");
        filed.ContentType.Should().Be("application/pdf");
        filed.Content.Should().Equal(Pdf);
        filed.RequiredPermission.Should().Be(PermissionCodes.SALES_INVOICE_VIEW).And.Be("SALES_INVOICE_VIEW");
        rig.Attachments.Verify(a => a.StoreGeneratedAsync(It.IsAny<GeneratedAttachmentRequest>(), User), Times.Once);
    }

    [Fact]
    public async Task What_is_handed_over_is_what_the_attachment_store_accepts()
    {
        // The store refuses anything else, so a mismatch here would be a filing that silently never happens.
        var rig = new Rig();

        await rig.Archive.FilePdfAsync(InvoiceId, User);

        var filed = rig.Filed!;
        filed.InterfaceCode.Length.Should().BeInRange(1, 30);
        filed.FileName.Length.Should().BeInRange(1, 255);
        filed.FileName.Should().NotContainAny("/", "\\");
        filed.Content.AsSpan().StartsWith("%PDF-"u8).Should().BeTrue();
        filed.Content.Length.Should().BeInRange(1, 20 * 1024 * 1024);
        (filed.Notes ?? "").Length.Should().BeLessThanOrEqualTo(300);
        (filed.RequiredPermission ?? "").Length.Should().BeLessThanOrEqualTo(100);
    }

    [Theory]
    [InlineData("ISSUED", 3780.0, "Filed with the invoice issued; balance due 3,780.00 PKR.")]
    [InlineData("PARTIALLY_PAID", 2780.0, "Filed with the invoice partially paid; balance due 2,780.00 PKR.")]
    [InlineData("PAID", 0.0, "Filed with the invoice paid; balance due 0.00 PKR.")]
    [InlineData("OVERDUE", 3780.0, "Filed with the invoice overdue; balance due 3,780.00 PKR.")]
    public async Task Each_filing_says_how_the_invoice_stood_so_a_list_of_them_tells_them_apart(string status, double balance, string note)
    {
        var rig = new Rig(status, (decimal)balance);

        await rig.Archive.FilePdfAsync(InvoiceId, User);

        rig.Filed!.Notes.Should().Be(note);
    }

    [Fact]
    public async Task Already_filed_is_reported_as_such_not_as_a_second_copy()
    {
        var rig = new Rig();
        var existing = new StoredAttachment(Guid.NewGuid(), AlreadyStored: true);
        rig.Attachments.Setup(a => a.StoreGeneratedAsync(It.IsAny<GeneratedAttachmentRequest>(), It.IsAny<int>())).ReturnsAsync(existing);

        (await rig.Archive.FilePdfAsync(InvoiceId, User)).Should().Be(existing);
    }

    [Fact]
    public async Task A_draft_is_not_filed_and_the_store_is_never_asked()
    {
        var rig = new Rig("DRAFT");

        var act = async () => await rig.Archive.FilePdfAsync(InvoiceId, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*DRAFT*").WithMessage("*not what the customer is sent*");
        rig.Attachments.Verify(a => a.StoreGeneratedAsync(It.IsAny<GeneratedAttachmentRequest>(), It.IsAny<int>()), Times.Never);
        rig.Documents.Verify(d => d.GeneratePdfAsync(It.IsAny<Guid>()), Times.Never, "no PDF is rendered for nothing");
    }

    [Theory]
    [InlineData("ISSUED")]
    [InlineData("PARTIALLY_PAID")]
    [InlineData("PAID")]
    [InlineData("OVERDUE")]
    [InlineData("CANCELLED")]
    [InlineData("CREDIT_NOTE")]
    public async Task Every_other_status_can_be_filed(string status)
    {
        var rig = new Rig(status);

        var act = async () => await rig.Archive.FilePdfAsync(InvoiceId, User);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_unknown_invoice_is_not_found()
    {
        var rig = new Rig();
        rig.Invoices.Setup(i => i.GetAsync(It.IsAny<Guid>())).ReturnsAsync((SalesInvoiceDetailModel?)null);

        var act = async () => await rig.Archive.FilePdfAsync(Guid.NewGuid(), User);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_refusal_from_the_store_reaches_the_caller_of_the_explicit_action()
    {
        var rig = new Rig();
        rig.Attachments.Setup(a => a.StoreGeneratedAsync(It.IsAny<GeneratedAttachmentRequest>(), It.IsAny<int>()))
                       .ThrowsAsync(new BadRequestException("The document is not a PDF."));

        var act = async () => await rig.Archive.FilePdfAsync(InvoiceId, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*not a PDF*");
    }

    // ── Filing at issue: best effort, and never in the way ───────────────────

    [Fact]
    public async Task At_issue_the_pdf_is_filed_and_the_attachment_returned()
    {
        var rig = new Rig();

        var uuid = await rig.Archive.TryFileIssuedPdfAsync(InvoiceId, User);

        uuid.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        rig.Filed!.Notes.Should().Be("Filed when the invoice was issued.");
        rig.Log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task If_the_store_fails_at_issue_it_is_logged_and_reported_as_null_never_thrown()
    {
        var rig = new Rig();
        rig.Attachments.Setup(a => a.StoreGeneratedAsync(It.IsAny<GeneratedAttachmentRequest>(), It.IsAny<int>()))
                       .ThrowsAsync(new InvalidOperationException("the database is down"));

        var uuid = await rig.Archive.TryFileIssuedPdfAsync(InvoiceId, User);

        uuid.Should().BeNull();
        var entry = rig.Log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Message.Should().Contain(InvoiceId.ToString()).And.Contain("file it from the invoice");
        entry.Error.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task If_the_pdf_cannot_be_rendered_at_issue_that_too_is_logged_not_thrown()
    {
        var rig = new Rig();
        rig.Documents.Setup(d => d.GeneratePdfAsync(It.IsAny<Guid>())).ThrowsAsync(new InvalidOperationException("font missing"));

        (await rig.Archive.TryFileIssuedPdfAsync(InvoiceId, User)).Should().BeNull();
        rig.Log.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Error);
        rig.Attachments.Verify(a => a.StoreGeneratedAsync(It.IsAny<GeneratedAttachmentRequest>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task An_invoice_that_cannot_be_read_at_issue_is_also_no_reason_to_fail_the_issue()
    {
        var rig = new Rig();
        rig.Invoices.Setup(i => i.GetAsync(It.IsAny<Guid>())).ReturnsAsync((SalesInvoiceDetailModel?)null);

        (await rig.Archive.TryFileIssuedPdfAsync(Guid.NewGuid(), User)).Should().BeNull();
        rig.Log.Entries.Should().ContainSingle();
    }
}

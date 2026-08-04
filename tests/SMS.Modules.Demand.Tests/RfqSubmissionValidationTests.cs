using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

// Automated RFQ workflow — the public portal submission must "instantly validate" pricing and
// delivery lead time (supersedes the earlier FSD §5.2 "no validation by design" decision).

file static class RfqSubmissionBuild
{
    internal static string HashToken(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal static (RfqSubmissionService svc, DemandDbContext db, string rawToken, Guid line1Uuid, Guid line2Uuid)
        NewWithSeededLink(IBackgroundJobClient? jobsOverride = null)
    {
        var opts = new DbContextOptionsBuilder<DemandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new DemandDbContext(opts, new StaticTenantContext());

        var supplierId = Guid.NewGuid();
        var line1Uuid  = Guid.NewGuid();
        var line2Uuid  = Guid.NewGuid();

        var quotation = new Quotation
        {
            UUID            = Guid.NewGuid(),
            TraceId         = Guid.NewGuid(),
            QuotationNumber = "RFQ-2026-00001",
            Title           = "Test RFQ",
            SourceType      = "STANDALONE",
            Status          = "SENT",
            CreatedBy       = 1,
            CreatedDate     = DateTime.UtcNow,
            Lines =
            [
                new QuotationLine { UUID = line1Uuid, LineNo = 1, ItemDescription = "Laptop", UnitOfMeasure = "PC", Quantity = 2m },
                new QuotationLine { UUID = line2Uuid, LineNo = 2, ItemDescription = "Mouse",  UnitOfMeasure = "PC", Quantity = 5m }
            ],
            InvitedSuppliers = [new QuotationInvitedSupplier { SupplierId = supplierId, SupplierName = "Test Vendor", InvitedAt = DateTime.UtcNow }]
        };
        db.Quotations.Add(quotation);
        db.SaveChanges();

        var rawToken = Guid.NewGuid().ToString("N");
        db.RfqAccessLinks.Add(new RfqAccessLink
        {
            QuotationId = quotation.Id,
            SupplierId  = supplierId,
            ContactId   = 1,
            TokenHash   = HashToken(rawToken),
            Status      = "PENDING",
            GeneratedAt = DateTime.UtcNow,
            ExpiresAt   = DateTime.UtcNow.AddDays(7),
            CreatedBy   = 1
        });
        db.SaveChanges();

        var validation = new RfqLinkValidationService(db);
        var jobs       = jobsOverride ?? new Mock<IBackgroundJobClient>().Object;
        var svc        = new RfqSubmissionService(validation, db, jobs, NullLogger<RfqSubmissionService>.Instance);

        return (svc, db, rawToken, line1Uuid, line2Uuid);
    }
}

public class RfqSubmission_Validation_Tests
{
    [Fact]
    public async Task Valid_Price_And_Delivery_Days_Submits_Successfully()
    {
        var (svc, db, rawToken, line1, line2) = RfqSubmissionBuild.NewWithSeededLink();

        var result = await svc.SubmitAsync(rawToken, new RfqSubmitRequest
        {
            Lines =
            [
                new RfqSubmitLineRequest { LineUuid = line1, UnitPrice = "1200.50", DeliveryDays = "14", CanSupply = true },
                new RfqSubmitLineRequest { LineUuid = line2, UnitPrice = "9.99",    DeliveryDays = "3",  CanSupply = true }
            ]
        }, clientIp: "127.0.0.1");

        result.Status.Should().Be("SUBMITTED");
        result.ValidationErrors.Should().BeNullOrEmpty();

        var response = await db.VendorResponses.Include(r => r.Lines).FirstAsync();
        response.Lines.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_Failing_Notification_Enqueue_Does_Not_Fail_The_Submission()
    {
        // Regression test: the notification job used to be enqueued outside any try/catch, AFTER
        // the vendor response was already committed — a Hangfire hiccup there would surface as an
        // unhandled exception (the vendor sees "unexpected error") even though their response had
        // already saved successfully. The submission result must be unaffected by this failure.
        var failingJobs = new Mock<IBackgroundJobClient>();
        failingJobs
            .Setup(j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()))
            .Throws(new InvalidOperationException("Simulated Hangfire storage failure"));

        var (svc, db, rawToken, line1, line2) = RfqSubmissionBuild.NewWithSeededLink(failingJobs.Object);

        var result = await svc.SubmitAsync(rawToken, new RfqSubmitRequest
        {
            Lines =
            [
                new RfqSubmitLineRequest { LineUuid = line1, UnitPrice = "100", DeliveryDays = "5", CanSupply = true },
                new RfqSubmitLineRequest { LineUuid = line2, UnitPrice = "9.99", DeliveryDays = "3", CanSupply = true }
            ]
        }, clientIp: "127.0.0.1");

        result.Status.Should().Be("SUBMITTED");

        var response = await db.VendorResponses.Include(r => r.Lines).FirstAsync();
        response.Lines.Should().HaveCount(2);
        (await db.RfqAccessLinks.FirstAsync()).ConsumedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Blank_UnitPrice_Is_Rejected_With_Validation_Error()
    {
        var (svc, db, rawToken, line1, line2) = RfqSubmissionBuild.NewWithSeededLink();

        var result = await svc.SubmitAsync(rawToken, new RfqSubmitRequest
        {
            Lines =
            [
                new RfqSubmitLineRequest { LineUuid = line1, UnitPrice = "", DeliveryDays = "14", CanSupply = true },
                new RfqSubmitLineRequest { LineUuid = line2, UnitPrice = "9.99", DeliveryDays = "3", CanSupply = true }
            ]
        }, clientIp: "127.0.0.1");

        result.Status.Should().Be("VALIDATION_ERROR");
        result.ValidationErrors.Should().ContainMatch("*unit price*");

        // Nothing was persisted and the link is still usable — a validation failure must not
        // consume the one-time token.
        (await db.VendorResponses.AnyAsync()).Should().BeFalse();
        (await db.RfqAccessLinks.FirstAsync()).ConsumedAt.Should().BeNull();
    }

    [Fact]
    public async Task Negative_UnitPrice_Is_Rejected()
    {
        var (svc, _, rawToken, line1, line2) = RfqSubmissionBuild.NewWithSeededLink();

        var result = await svc.SubmitAsync(rawToken, new RfqSubmitRequest
        {
            Lines =
            [
                new RfqSubmitLineRequest { LineUuid = line1, UnitPrice = "-50", DeliveryDays = "14", CanSupply = true },
                new RfqSubmitLineRequest { LineUuid = line2, UnitPrice = "9.99", DeliveryDays = "3", CanSupply = true }
            ]
        }, clientIp: "127.0.0.1");

        result.Status.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task NonNumeric_DeliveryDays_Is_Rejected()
    {
        var (svc, _, rawToken, line1, line2) = RfqSubmissionBuild.NewWithSeededLink();

        var result = await svc.SubmitAsync(rawToken, new RfqSubmitRequest
        {
            Lines =
            [
                new RfqSubmitLineRequest { LineUuid = line1, UnitPrice = "100", DeliveryDays = "soon", CanSupply = true },
                new RfqSubmitLineRequest { LineUuid = line2, UnitPrice = "9.99", DeliveryDays = "3", CanSupply = true }
            ]
        }, clientIp: "127.0.0.1");

        result.Status.Should().Be("VALIDATION_ERROR");
        result.ValidationErrors.Should().ContainMatch("*delivery days*");
    }

    [Fact]
    public async Task Line_Marked_CanSupply_False_Skips_Validation_Even_With_Blank_Price()
    {
        var (svc, db, rawToken, line1, line2) = RfqSubmissionBuild.NewWithSeededLink();

        var result = await svc.SubmitAsync(rawToken, new RfqSubmitRequest
        {
            Lines =
            [
                new RfqSubmitLineRequest { LineUuid = line1, UnitPrice = "", DeliveryDays = "", CanSupply = false, Remarks = "Out of stock" },
                new RfqSubmitLineRequest { LineUuid = line2, UnitPrice = "9.99", DeliveryDays = "3", CanSupply = true }
            ]
        }, clientIp: "127.0.0.1");

        result.Status.Should().Be("SUBMITTED");

        var response = await db.VendorResponses.Include(r => r.Lines).FirstAsync();
        var declinedLine = response.Lines.Single(l => l.QuotationLineId == db.QuotationLines.First(x => x.UUID == line1).Id);
        declinedLine.NetUnitPrice.Should().Be(0m);
        declinedLine.LeadTimeDays.Should().BeNull();
    }
}

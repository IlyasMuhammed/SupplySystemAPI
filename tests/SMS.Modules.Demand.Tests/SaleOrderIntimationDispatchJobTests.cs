using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>A29-P4-07 §5.3 — "Failed emails retried 3x by Hangfire." This is the unit that's
/// actually retried, so it's tested against a real DemandDbContext to prove the row's Status/SentAt/
/// ErrorMessage genuinely reflect what happened, not just that SendEmailAsync was called.</summary>
public class SaleOrderIntimationDispatchJobTests
{
    private static (DemandDbContext Db, SaleOrderIntimationDispatchJob Job, Mock<INotificationService> Notifications) NewHarness()
    {
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = Guid.NewGuid() });
        var notifications = new Mock<INotificationService>();
        var job = new SaleOrderIntimationDispatchJob(db, notifications.Object, NullLogger<SaleOrderIntimationDispatchJob>.Instance);
        return (db, job, notifications);
    }

    private static async Task<Guid> SeedQueuedRow(DemandDbContext db)
    {
        var order = new SaleOrder
        {
            SoNumber = "SO-DISPATCH-TEST", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date,
            CurrencyId = Guid.NewGuid(), Status = "CONFIRMED", DeliveryMode = "SELF_PICKUP", CreatedBy = 1
        };
        db.SaleOrders.Add(order);
        await db.SaveChangesAsync();

        var intimation = new SaleOrderIntimation
        {
            SaleOrderId = order.Id, EventType = "SO_CONFIRMED", Recipients = "to@x.com",
            Subject = "Test subject", BodyHtml = "<p>Test body</p>", Status = "QUEUED"
        };
        db.SaleOrderIntimations.Add(intimation);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return intimation.UUID;
    }

    [Fact]
    public async Task Successful_send_marks_the_row_sent_and_stamps_sent_at()
    {
        var (db, job, notifications) = NewHarness();
        var uuid = await SeedQueuedRow(db);
        notifications.Setup(n => n.SendEmailAsync("to@x.com", "Test subject", "<p>Test body</p>")).Returns(Task.CompletedTask);

        await job.DispatchAsync(uuid);

        var row = await db.SaleOrderIntimations.AsNoTracking().SingleAsync(x => x.UUID == uuid);
        row.Status.Should().Be("SENT");
        row.SentAt.Should().NotBeNull();
        row.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Failed_send_marks_the_row_failed_and_rethrows_for_hangfire_to_retry()
    {
        var (db, job, notifications) = NewHarness();
        var uuid = await SeedQueuedRow(db);
        notifications.Setup(n => n.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("SMTP unreachable"));

        var act = () => job.DispatchAsync(uuid);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var row = await db.SaleOrderIntimations.AsNoTracking().SingleAsync(x => x.UUID == uuid);
        row.Status.Should().Be("FAILED");
        row.ErrorMessage.Should().Be("SMTP unreachable");
        row.SentAt.Should().BeNull();
    }

    [Fact]
    public async Task A_retry_that_succeeds_after_a_prior_failure_clears_the_error()
    {
        var (db, job, notifications) = NewHarness();
        var uuid = await SeedQueuedRow(db);
        notifications.SetupSequence(n => n.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("transient"))
            .Returns(Task.CompletedTask);

        try { await job.DispatchAsync(uuid); } catch (InvalidOperationException) { /* first attempt fails, as Hangfire would see it */ }
        await job.DispatchAsync(uuid); // second attempt (Hangfire's retry) succeeds

        var row = await db.SaleOrderIntimations.AsNoTracking().SingleAsync(x => x.UUID == uuid);
        row.Status.Should().Be("SENT");
        row.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Missing_row_is_a_no_op_that_does_not_throw()
    {
        var (_, job, notifications) = NewHarness();

        var act = () => job.DispatchAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
        notifications.Verify(n => n.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}

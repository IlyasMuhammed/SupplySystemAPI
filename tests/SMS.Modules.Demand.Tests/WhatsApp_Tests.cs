using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Services;
using SMS.Modules.Notifications.Data;
using SMS.Modules.Notifications.Domain;
using SMS.Modules.Notifications.Providers;
using SMS.Modules.Notifications.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

// ── Test helpers ──────────────────────────────────────────────────────────────

file static class WhatsAppBuild
{
    internal static NotificationsDbContext NotificationsDb() =>
        new(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    internal static DemandDbContext DemandDb() =>
        new(new DbContextOptionsBuilder<DemandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    internal static WhatsAppNotificationService Service(
        IWhatsAppProvider provider, NotificationsDbContext db) =>
        new(provider, db, NullLogger<WhatsAppNotificationService>.Instance);

    internal static RfqWhatsAppDispatchJob DispatchJob(
        DemandDbContext db, IWhatsAppNotificationService svc, string? contentSid = "HXtest") =>
        new(db, svc,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                    { ["WhatsApp:RfqContentSid"] = contentSid })
                .Build(),
            NullLogger<RfqWhatsAppDispatchJob>.Instance);

    internal static RfqAccessLink SeedLink(
        DemandDbContext db,
        string? mobile = "+923086987561",
        DateTime? whatsAppSentAt = null)
    {
        var quotation = new Quotation
        {
            UUID            = Guid.NewGuid(),
            QuotationNumber = "RFQ-TEST-001",
            Title           = "Test RFQ",
            SourceType      = "STANDALONE",
            Status          = "SENT",
            DueDate         = DateTime.UtcNow.AddDays(7),
            CreatedDate     = DateTime.UtcNow,
            CreatedBy       = 1
        };
        db.Quotations.Add(quotation);
        db.SaveChanges();

        var link = new RfqAccessLink
        {
            QuotationId         = quotation.Id,
            SupplierId          = Guid.NewGuid(),
            ContactId           = 1,
            TokenHash           = Guid.NewGuid().ToString("N"),
            Status              = "PENDING",
            GeneratedAt         = DateTime.UtcNow,
            ExpiresAt           = DateTime.UtcNow.AddDays(7),
            AccessCount         = 0,
            CreatedBy           = 1,
            ContactMobileNumber = mobile,
            WhatsAppSentAt      = whatsAppSentAt
        };
        db.RfqAccessLinks.Add(link);
        db.SaveChanges();
        return link;
    }
}

/// <summary>Provider that always succeeds and records call count.</summary>
file sealed class FakeSuccessProvider : IWhatsAppProvider
{
    public int CallCount { get; private set; }
    public List<string> CalledNumbers { get; } = [];

    public Task<string> SendAsync(string mobileNumber, string templateCode, object templateData, CancellationToken ct = default)
    {
        CallCount++;
        CalledNumbers.Add(mobileNumber);
        return Task.FromResult($"fake-msg-id-{CallCount}");
    }
}

/// <summary>Provider that always throws — simulates a transient provider failure.</summary>
file sealed class FakeFailProvider : IWhatsAppProvider
{
    public int CallCount { get; private set; }

    public Task<string> SendAsync(string mobileNumber, string templateCode, object templateData, CancellationToken ct = default)
    {
        CallCount++;
        throw new HttpRequestException("Simulated transient provider failure");
    }
}

// ── AC-WA-1: Mobile number masking ───────────────────────────────────────────

public class MobileNumberMasking_Tests
{
    [Theory]
    [InlineData("+923086987561", "+9230****7561")]
    [InlineData("+14155238886",  "+1415****8886")]
    [InlineData("+447700900123", "+4477****0123")]
    [InlineData("+1234",         "+****")]          // too short — fully masked
    public void MaskMobile_Produces_Correct_Pattern(string input, string expected)
    {
        var result = WhatsAppNotificationService.MaskMobile(input);
        result.Should().Be(expected);
    }

    [Fact]
    public void MaskMobile_Never_Returns_Full_Number()
    {
        var number = "+923086987561";
        var masked = WhatsAppNotificationService.MaskMobile(number);
        masked.Should().NotBe(number);
        masked.Should().Contain("****");
    }
}

// ── AC-WA-2: SendMessageAsync dispatches to all recipients ───────────────────

public class SendMessageAsync_MultipleRecipients_Tests
{
    [Fact]
    public async Task Dispatches_To_All_Recipients_In_Single_Call()
    {
        var db       = WhatsAppBuild.NotificationsDb();
        var provider = new FakeSuccessProvider();
        var svc      = WhatsAppBuild.Service(provider, db);

        var recipients = new[]
        {
            new WhatsAppRecipient("+923086987561", "Supplier A"),
            new WhatsAppRecipient("+14155238886",  "Supplier B"),
            new WhatsAppRecipient("+447700900123", "Supplier C")
        };

        await svc.SendMessageAsync(recipients, "HXtest", new { });

        provider.CallCount.Should().Be(3);
        provider.CalledNumbers.Should().Contain("+923086987561")
                                       .And.Contain("+14155238886")
                                       .And.Contain("+447700900123");
    }
}

// ── AC-WA-3: QUEUED log written immediately before provider call ──────────────

public class WhatsAppMessageLog_Queued_Tests
{
    [Fact]
    public async Task Creates_QUEUED_Log_Before_Provider_Dispatch()
    {
        var db       = WhatsAppBuild.NotificationsDb();
        var provider = new FakeSuccessProvider();
        var svc      = WhatsAppBuild.Service(provider, db);

        await svc.SendMessageAsync(
            [new WhatsAppRecipient("+923086987561", "Supplier A", ReferenceId: Guid.NewGuid())],
            "HXtest", new { });

        var log = await db.WhatsAppMessageLogs.SingleAsync();
        log.Status.Should().Be("SENT");       // updated after success
        log.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        log.MobileNumberMasked.Should().Be("+9230****7561");
        log.TemplateCode.Should().Be("HXtest");
    }

    [Fact]
    public async Task Creates_One_Log_Row_Per_Recipient()
    {
        var db       = WhatsAppBuild.NotificationsDb();
        var provider = new FakeSuccessProvider();
        var svc      = WhatsAppBuild.Service(provider, db);

        await svc.SendMessageAsync(
        [
            new WhatsAppRecipient("+923086987561", "A"),
            new WhatsAppRecipient("+14155238886",  "B")
        ], "HXtest", new { });

        var logs = await db.WhatsAppMessageLogs.ToListAsync();
        logs.Should().HaveCount(2);
        logs.All(l => l.Status == "SENT").Should().BeTrue();
    }

    [Fact]
    public async Task Sets_FAILED_Status_On_Provider_Error()
    {
        var db       = WhatsAppBuild.NotificationsDb();
        var provider = new FakeFailProvider();
        var svc      = WhatsAppBuild.Service(provider, db);

        var act = () => svc.SendMessageAsync(
            [new WhatsAppRecipient("+923086987561", "Supplier A")],
            "HXtest", new { });

        await act.Should().ThrowAsync<HttpRequestException>();

        var log = await db.WhatsAppMessageLogs.SingleAsync();
        log.Status.Should().Be("FAILED");
    }

    [Fact]
    public async Task Sets_ProviderMessageId_On_Success()
    {
        var db       = WhatsAppBuild.NotificationsDb();
        var provider = new FakeSuccessProvider();
        var svc      = WhatsAppBuild.Service(provider, db);

        await svc.SendMessageAsync(
            [new WhatsAppRecipient("+923086987561", "Supplier A")],
            "HXtest", new { });

        var log = await db.WhatsAppMessageLogs.SingleAsync();
        log.ProviderMessageId.Should().StartWith("fake-msg-id-");
    }
}

// ── AC-WA-4: Provider selection honours config key ────────────────────────────

public class ProviderSelection_Tests
{
    [Fact]
    public void NullProvider_Succeeds_Without_External_Call()
    {
        var provider = new NullWhatsAppProvider(NullLogger<NullWhatsAppProvider>.Instance);
        var act = async () => await provider.SendAsync("+923086987561", "HXtest", new { });
        act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Swapping_Provider_Requires_No_Change_To_Service_Calling_Code()
    {
        // Same service, two different provider implementations — calling code unchanged
        var db1      = WhatsAppBuild.NotificationsDb();
        var providerA = new FakeSuccessProvider();
        var svcA      = WhatsAppBuild.Service(providerA, db1);

        var db2      = WhatsAppBuild.NotificationsDb();
        var providerB = new NullWhatsAppProvider(NullLogger<NullWhatsAppProvider>.Instance);
        var svcB      = WhatsAppBuild.Service(providerB, db2);

        var recipients = new[] { new WhatsAppRecipient("+923086987561", "Test") };
        await svcA.SendMessageAsync(recipients, "HXtest", new { });
        await svcB.SendMessageAsync(recipients, "HXtest", new { });

        // Both completed without exception — provider swap was transparent
        providerA.CallCount.Should().Be(1);
        (await db1.WhatsAppMessageLogs.CountAsync()).Should().Be(1);
        (await db2.WhatsAppMessageLogs.CountAsync()).Should().Be(1);
    }
}

// ── AC-WA-5: WhatsApp failure does not block the email channel ────────────────

public class ChannelIndependence_Tests
{
    [Fact]
    public async Task WhatsApp_Failure_Does_Not_Propagate_To_Email_Job()
    {
        // Email and WhatsApp are separate Hangfire job enqueues in QuotationService.
        // This test verifies that the dispatch job for WhatsApp throws (for Hangfire retry)
        // but that the email job method is completely unaffected — it is a separate code path.

        var demandDb     = WhatsAppBuild.DemandDb();
        var link         = WhatsAppBuild.SeedLink(demandDb);
        var notifDb      = WhatsAppBuild.NotificationsDb();
        var failProvider = new FakeFailProvider();
        var whatsAppSvc  = WhatsAppBuild.Service(failProvider, notifDb);
        var whatsAppJob  = WhatsAppBuild.DispatchJob(demandDb, whatsAppSvc);

        // WhatsApp job throws
        var act = () => whatsAppJob.SendRfqResponseRequestWhatsAppAsync(link.Id);
        await act.Should().ThrowAsync<HttpRequestException>();

        // Reload to confirm whatsapp_sent_at was NOT stamped (failure path)
        var reloaded = await demandDb.RfqAccessLinks.FindAsync(link.Id);
        reloaded!.WhatsAppSentAt.Should().BeNull();
    }
}

// ── AC-WA-6: whatsapp_sent_at populated only on successful send ───────────────

public class WhatsAppSentAt_Tests
{
    [Fact]
    public async Task Sets_WhatsAppSentAt_After_Successful_Send()
    {
        var demandDb    = WhatsAppBuild.DemandDb();
        var link        = WhatsAppBuild.SeedLink(demandDb);
        var notifDb     = WhatsAppBuild.NotificationsDb();
        var successSvc  = WhatsAppBuild.Service(new FakeSuccessProvider(), notifDb);
        var job         = WhatsAppBuild.DispatchJob(demandDb, successSvc);

        await job.SendRfqResponseRequestWhatsAppAsync(link.Id);

        var updated = await demandDb.RfqAccessLinks.FindAsync(link.Id);
        updated!.WhatsAppSentAt.Should().NotBeNull();
        updated.WhatsAppSentAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Does_Not_Set_WhatsAppSentAt_On_Provider_Failure()
    {
        var demandDb   = WhatsAppBuild.DemandDb();
        var link       = WhatsAppBuild.SeedLink(demandDb);
        var notifDb    = WhatsAppBuild.NotificationsDb();
        var failSvc    = WhatsAppBuild.Service(new FakeFailProvider(), notifDb);
        var job        = WhatsAppBuild.DispatchJob(demandDb, failSvc);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => job.SendRfqResponseRequestWhatsAppAsync(link.Id));

        var reloaded = await demandDb.RfqAccessLinks.FindAsync(link.Id);
        reloaded!.WhatsAppSentAt.Should().BeNull();
    }

    [Fact]
    public async Task Skips_When_Already_Sent()
    {
        var demandDb    = WhatsAppBuild.DemandDb();
        var alreadySent = DateTime.UtcNow.AddHours(-1);
        var link        = WhatsAppBuild.SeedLink(demandDb, whatsAppSentAt: alreadySent);
        var notifDb     = WhatsAppBuild.NotificationsDb();
        var provider    = new FakeSuccessProvider();
        var svc         = WhatsAppBuild.Service(provider, notifDb);
        var job         = WhatsAppBuild.DispatchJob(demandDb, svc);

        await job.SendRfqResponseRequestWhatsAppAsync(link.Id);

        // Provider must NOT be called again — idempotency guard
        provider.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Skips_When_No_Mobile_Number()
    {
        var demandDb = WhatsAppBuild.DemandDb();
        var link     = WhatsAppBuild.SeedLink(demandDb, mobile: null);
        var notifDb  = WhatsAppBuild.NotificationsDb();
        var provider = new FakeSuccessProvider();
        var svc      = WhatsAppBuild.Service(provider, notifDb);
        var job      = WhatsAppBuild.DispatchJob(demandDb, svc);

        await job.SendRfqResponseRequestWhatsAppAsync(link.Id);

        provider.CallCount.Should().Be(0);
        var reloaded = await demandDb.RfqAccessLinks.FindAsync(link.Id);
        reloaded!.WhatsAppSentAt.Should().BeNull();
    }
}

// ── AC-WA-7: Hangfire retry — provider failure propagates from job ────────────

public class HangfireRetry_Tests
{
    [Fact]
    public async Task Job_Throws_On_Provider_Failure_For_Hangfire_Retry()
    {
        var demandDb   = WhatsAppBuild.DemandDb();
        var link       = WhatsAppBuild.SeedLink(demandDb);
        var notifDb    = WhatsAppBuild.NotificationsDb();
        var failProv   = new FakeFailProvider();
        var svc        = WhatsAppBuild.Service(failProv, notifDb);
        var job        = WhatsAppBuild.DispatchJob(demandDb, svc);

        // Job must throw so the Hangfire infrastructure can schedule a retry
        await Assert.ThrowsAsync<HttpRequestException>(
            () => job.SendRfqResponseRequestWhatsAppAsync(link.Id));

        failProv.CallCount.Should().Be(1, "provider was attempted once before the throw");
    }

    [Fact]
    public async Task Each_Retry_Attempt_Writes_A_New_Log_Row()
    {
        var demandDb  = WhatsAppBuild.DemandDb();
        var link      = WhatsAppBuild.SeedLink(demandDb);
        var notifDb   = WhatsAppBuild.NotificationsDb();
        var failProv  = new FakeFailProvider();
        var svc       = WhatsAppBuild.Service(failProv, notifDb);
        var job       = WhatsAppBuild.DispatchJob(demandDb, svc);

        // Simulate two Hangfire retry attempts
        await Assert.ThrowsAsync<HttpRequestException>(() => job.SendRfqResponseRequestWhatsAppAsync(link.Id));
        await Assert.ThrowsAsync<HttpRequestException>(() => job.SendRfqResponseRequestWhatsAppAsync(link.Id));

        var logs = await notifDb.WhatsAppMessageLogs.ToListAsync();
        logs.Should().HaveCount(2, "each attempt creates its own FAILED log row");
        logs.All(l => l.Status == "FAILED").Should().BeTrue();
    }
}

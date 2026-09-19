using System.Reflection;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Logistics.Controllers;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Couriers.Booking;
using SMS.Modules.Logistics.Couriers.Labels;
using SMS.Modules.Logistics.Couriers.Manual;
using SMS.Modules.Logistics.Couriers.Simulator;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Couriers;

// T-38 — labels: fetched, stored, served.
public class ConsignmentLabelTests
{
    private const int User = 42;
    private static readonly DateTime T0 = new(2026, 9, 17, 9, 0, 0, DateTimeKind.Utc);
    private static readonly IConfiguration NoConfig = new ConfigurationBuilder().Build();

    private sealed class Harness
    {
        public required LogisticsDbContext      Db       { get; init; }
        public required StaticTenantContext     Tenant   { get; init; }
        public required string                  DbName   { get; init; }
        public required ScriptedCourierProvider Carrier  { get; init; }
        public required CourierProviderRegistry Registry { get; init; }
        public required ConsignmentLabelService Labels   { get; init; }
        public required CarrierAccountRepository Accounts { get; init; }
    }

    private static Harness NewHarness(bool labels = true, LogisticsDbContext? db = null, StaticTenantContext? tenant = null)
    {
        string dbName = "";
        if (db is null) (db, tenant, dbName) = LogisticsTestDb.New();

        var carrier  = new ScriptedCourierProvider("SCRIPTED", labels: labels);
        var registry = new CourierProviderRegistry([new ManualCourierProvider(), new SimulatorCourierProvider(), carrier]);
        var resolver = new CarrierAccountResolver(db, registry, new CarrierCredentialVault(db, TestEncryption.New()));

        return new Harness
        {
            Db = db, Tenant = tenant!, DbName = dbName, Carrier = carrier, Registry = registry,
            Labels = new ConsignmentLabelService(db, resolver, NoConfig),
            Accounts = new CarrierAccountRepository(db, registry)
        };
    }

    /// <summary>A consignment already booked on the scripted API carrier, airway bill AWB-1.</summary>
    private static async Task<Guid> Booked(
        Harness h, string status = "BOOKED", string awb = "AWB-1", string? integrationMode = "API",
        string? providerKey = "SCRIPTED", bool? labelsEnabled = null, string consignmentNumber = "SHP-2026-00001")
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = "Scripted Express", Code = $"C{Guid.NewGuid():N}"[..6],
            IntegrationMode = integrationMode, ProviderKey = providerKey, IsActive = true
        };
        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();

        int? accountId = null;
        if (integrationMode == "API")
        {
            var accountUuid = await h.Accounts.CreateAsync(
                new CreateCarrierAccountRequest { CarrierUuid = carrier.UUID, AccountName = "Main", LabelsEnabled = labelsEnabled }, User);
            accountId = await h.Db.CarrierAccounts.Where(a => a.UUID == accountUuid).Select(a => a.Id).SingleAsync();
        }

        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = consignmentNumber, CarrierId = carrier.Id,
            CarrierName = carrier.Name, CarrierAccountId = accountId, Status = status, MasterAwb = awb,
            CreatedBy = User, CreatedDate = T0
        };
        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return consignment.UUID;
    }

    private static async Task<ConsignmentLabelFile> Get(Harness h, Guid uuid)
    {
        h.Db.ChangeTracker.Clear();
        var file = await h.Labels.GetLabelAsync(uuid, User);
        h.Db.ChangeTracker.Clear();
        return file!;
    }

    private static Task<Consignment> Reload(Harness h, Guid uuid) =>
        h.Db.Consignments.AsNoTracking().IgnoreQueryFilters().SingleAsync(c => c.UUID == uuid);

    private static CourierLabel Label(byte[] content, string type = "application/pdf") => new(content, type, "whatever.pdf");

    // ── Fetching and storing ──────────────────────────────────────────────────

    [Fact]
    public async Task The_first_print_fetches_the_label_stores_it_and_marks_the_consignment_label_ready()
    {
        var h = NewHarness();
        var uuid = await Booked(h);

        var file = await Get(h, uuid);

        file.ContentType.Should().Be("application/pdf");
        Encoding.ASCII.GetString(file.Content).Should().StartWith("%PDF-").And.Contain("AWB-1");
        h.Carrier.LabelCalls.Should().Equal("AWB-1");

        var stored = await h.Db.ConsignmentLabels.AsNoTracking().SingleAsync();
        stored.AwbNumber.Should().Be("AWB-1");
        stored.Source.Should().Be("FETCHED");
        stored.SizeBytes.Should().Be(file.Content.Length);
        (await Reload(h, uuid)).Status.Should().Be("LABEL_READY");
    }

    [Fact]
    public async Task A_reprint_comes_from_storage_without_asking_the_carrier_again()
    {
        var h = NewHarness();
        var uuid = await Booked(h);

        var first  = await Get(h, uuid);
        var second = await Get(h, uuid);

        second.Content.Should().Equal(first.Content);
        h.Carrier.LabelCalls.Should().HaveCount(1);
        (await h.Db.ConsignmentLabels.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_label_for_an_earlier_airway_bill_is_never_served_for_the_current_one()
    {
        // A parcel carrying the old label travels on a booking that no longer exists.
        var h = NewHarness();
        var uuid = await Booked(h, awb: "AWB-OLD");
        await Get(h, uuid);

        var consignment = await h.Db.Consignments.SingleAsync();
        consignment.MasterAwb = "AWB-NEW";
        await h.Db.SaveChangesAsync();

        var file = await Get(h, uuid);

        Encoding.ASCII.GetString(file.Content).Should().Contain("AWB-NEW").And.NotContain("AWB-OLD");
        h.Carrier.LabelCalls.Should().Equal("AWB-OLD", "AWB-NEW");
    }

    [Fact]
    public async Task The_same_label_stored_twice_is_kept_once()
    {
        var h = NewHarness();
        var uuid = await Booked(h);
        var consignment = await h.Db.Consignments.SingleAsync();
        var label = Label(ScriptedCourierProvider.Pdf("same"));

        await h.Labels.StoreAsync(consignment, label, ConsignmentLabelSource.Booking, "SCRIPTED", User, T0);
        await h.Labels.StoreAsync(consignment, label, ConsignmentLabelSource.Fetched, "SCRIPTED", User, T0.AddMinutes(1));

        (await h.Db.ConsignmentLabels.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Reprinting_after_pickup_does_not_move_the_consignment_backwards()
    {
        var h = NewHarness();
        var uuid = await Booked(h, status: "PICKED_UP");

        await Get(h, uuid);

        (await Reload(h, uuid)).Status.Should().Be("PICKED_UP");
    }

    [Fact]
    public async Task The_simulator_books_and_its_real_pdf_label_is_served()
    {
        // End to end through the booking job with the real simulator adapter.
        var h = NewHarness();
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = "Simcourier", Code = "SIM001", IntegrationMode = "API",
            ProviderKey = SimulatorCourierProvider.ProviderKey, IsActive = true
        };
        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();
        await h.Accounts.CreateAsync(new CreateCarrierAccountRequest { CarrierUuid = carrier.UUID, AccountName = "Sandbox" }, User);

        var delivery = new DeliveryOrder
        {
            UUID = Guid.NewGuid(), DeliveryNumber = "DLV-2026-00001", Direction = "OUTBOUND", SourceType = "MANUAL",
            ShipFromAddress = Address("Plot 1", "Karachi"), ShipToAddress = Address("Road 2", "Lahore"),
            CreatedBy = User, CreatedDate = T0
        };
        delivery.Packages.Add(new ShipmentPackage { UUID = Guid.NewGuid(), PackageBarcode = "PKG-001", GrossWeightKg = 4m, CreatedBy = User, CreatedDate = T0 });
        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = "SHP-2026-00077", CarrierId = carrier.Id, CarrierName = carrier.Name,
            CarrierServiceCode = "SIM-DELIVERED", CreatedBy = User, CreatedDate = T0
        };
        consignment.Deliveries.Add(new ConsignmentDelivery { UUID = Guid.NewGuid(), DeliveryOrder = delivery, Sequence = 1, CreatedBy = User, CreatedDate = T0 });
        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var booking = BookingService(h);
        await booking.RequestBookingAsync(consignment.UUID, new BookConsignmentRequest(), User);
        h.Db.ChangeTracker.Clear();
        (await booking.ExecuteAsync(consignment.UUID, h.Tenant.OrganizationId, T0)).Should().Be(BookingRunResult.Booked);

        var file = await Get(h, consignment.UUID);

        file.ContentType.Should().Be("application/pdf");
        file.Content.Length.Should().BeGreaterThan(1024);
        Encoding.ASCII.GetString(file.Content, 0, 5).Should().Be("%PDF-");
        file.FileName.Should().StartWith("SHP-2026-00077-SIM").And.EndWith(".pdf");

        (await BookingService(h).GetStatusAsync(consignment.UUID))!.HasStoredLabel.Should().BeTrue();
    }

    // ── Labels returned with the booking ──────────────────────────────────────

    [Fact]
    public async Task A_label_returned_with_the_booking_is_stored_and_printed_without_a_fetch()
    {
        var h = NewHarness();
        var uuid = await SeedForBooking(h);
        h.Carrier.ThenBooks("AWB-9", Label(ScriptedCourierProvider.Pdf("inline")));

        var booking = BookingService(h);
        await booking.RequestBookingAsync(uuid, new BookConsignmentRequest(), User);
        h.Db.ChangeTracker.Clear();
        (await booking.ExecuteAsync(uuid, h.Tenant.OrganizationId, T0)).Should().Be(BookingRunResult.Booked);

        (await Reload(h, uuid)).Status.Should().Be("LABEL_READY");
        (await h.Db.ConsignmentLabels.AsNoTracking().SingleAsync()).Source.Should().Be("BOOKING");

        var file = await Get(h, uuid);
        Encoding.ASCII.GetString(file.Content).Should().Contain("inline");
        h.Carrier.LabelCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_broken_label_returned_with_the_booking_does_not_undo_the_booking()
    {
        var h = NewHarness();
        var uuid = await SeedForBooking(h);
        h.Carrier.ThenBooks("AWB-9", Label(Encoding.UTF8.GetBytes("<html>Internal error</html>")));

        var booking = BookingService(h);
        await booking.RequestBookingAsync(uuid, new BookConsignmentRequest(), User);
        h.Db.ChangeTracker.Clear();

        (await booking.ExecuteAsync(uuid, h.Tenant.OrganizationId, T0)).Should().Be(BookingRunResult.Booked);

        var c = await Reload(h, uuid);
        c.Status.Should().Be("BOOKED");
        c.MasterAwb.Should().Be("AWB-9");
        (await h.Db.ConsignmentLabels.CountAsync()).Should().Be(0);

        await Get(h, uuid);
        h.Carrier.LabelCalls.Should().Equal(new[] { "AWB-9" }, "the label is fetched on first print instead");
    }

    // ── When there is no label to give ────────────────────────────────────────

    [Fact]
    public async Task There_is_no_label_before_booking()
    {
        var h = NewHarness();
        var uuid = await Booked(h, status: "DRAFT", awb: null!);

        await h.Labels.Invoking(l => l.GetLabelAsync(uuid, User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*not booked yet*");
    }

    [Fact]
    public async Task A_cancelled_consignments_label_is_never_printed_even_if_stored()
    {
        var h = NewHarness();
        var uuid = await Booked(h);
        await Get(h, uuid);

        var consignment = await h.Db.Consignments.SingleAsync();
        consignment.Status = "CANCELLED";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        await h.Labels.Invoking(l => l.GetLabelAsync(uuid, User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*cancelled*must not be printed*");
    }

    [Fact]
    public async Task A_manual_carrier_issues_its_own_labels()
    {
        var h = NewHarness();
        var uuid = await Booked(h, integrationMode: "MANUAL", providerKey: null);

        await h.Labels.Invoking(l => l.GetLabelAsync(uuid, User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*issues its own labels*");
    }

    [Fact]
    public async Task An_account_with_labels_switched_off_does_not_ask_the_carrier()
    {
        var h = NewHarness();
        var uuid = await Booked(h, labelsEnabled: false);

        await h.Labels.Invoking(l => l.GetLabelAsync(uuid, User))
            .Should().ThrowAsync<ConflictException>().WithMessage("*switched off for account 'Main'*");
        h.Carrier.LabelCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_organizations_consignment_has_no_label_here()
    {
        var h = NewHarness();
        var uuid = await Booked(h);

        var other = NewHarness(db: LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid()), tenant: new StaticTenantContext());

        (await other.Labels.GetLabelAsync(uuid, User)).Should().BeNull();
    }

    // ── When the carrier cannot supply one ────────────────────────────────────

    [Fact]
    public async Task A_carrier_that_does_not_answer_is_unavailable_and_nothing_changes()
    {
        var h = NewHarness();
        var uuid = await Booked(h);
        h.Carrier.ThenLabel((_, _) => Task.FromException<CourierLabelResult>(new HttpRequestException("reset")));

        await h.Labels.Invoking(l => l.GetLabelAsync(uuid, User))
            .Should().ThrowAsync<CarrierUnavailableException>().WithMessage("*did not return the label*Try again*");

        (await h.Db.ConsignmentLabels.CountAsync()).Should().Be(0);
        (await Reload(h, uuid)).Status.Should().Be("BOOKED");
    }

    [Fact]
    public async Task A_failed_answer_is_unavailable_but_a_refusal_or_unsupported_is_a_plain_no()
    {
        var h = NewHarness();
        var uuid = await Booked(h);
        h.Carrier.ThenLabel(CourierOutcome.Failed, message: "busy")
                 .ThenLabel(CourierOutcome.Refused, message: "label already voided")
                 .ThenLabel(CourierOutcome.Unsupported, message: "no labels on this service")
                 .ThenLabel(CourierOutcome.Succeeded);

        await h.Labels.Invoking(l => l.GetLabelAsync(uuid, User)).Should().ThrowAsync<CarrierUnavailableException>();
        await h.Labels.Invoking(l => l.GetLabelAsync(uuid, User)).Should().ThrowAsync<ConflictException>().WithMessage("*label already voided*");
        await h.Labels.Invoking(l => l.GetLabelAsync(uuid, User)).Should().ThrowAsync<ConflictException>().WithMessage("no labels on this service");
        await h.Labels.Invoking(l => l.GetLabelAsync(uuid, User)).Should().ThrowAsync<CarrierUnavailableException>().WithMessage("*without a label*");
    }

    [Fact]
    public async Task An_error_page_dressed_as_a_pdf_is_not_stored_or_served()
    {
        var h = NewHarness();
        var uuid = await Booked(h);
        h.Carrier.ThenLabel(CourierOutcome.Succeeded,
            Label(Encoding.UTF8.GetBytes("<!DOCTYPE html><script>alert(1)</script>")));

        await h.Labels.Invoking(l => l.GetLabelAsync(uuid, User))
            .Should().ThrowAsync<CarrierUnavailableException>().WithMessage("*content is not*error page*");
        (await h.Db.ConsignmentLabels.CountAsync()).Should().Be(0);
    }

    // ── What counts as a label ────────────────────────────────────────────────

    [Theory]
    [InlineData("application/pdf",                "%PDF-1.4 ...",        "application/pdf",   "pdf")]
    [InlineData("Application/PDF; charset=binary", "%PDF-1.4 ...",        "application/pdf",   "pdf")]
    [InlineData("image/png",                      "PNG\r\n\n...", "image/png",   "png")]
    [InlineData("image/gif",                      "GIF89a...",           "image/gif",         "gif")]
    [InlineData("application/x-zpl",              "  ^XA^FO50,50^FDHi^FS^XZ", "application/x-zpl", "zpl")]
    public void Real_label_formats_are_accepted_and_normalised(string declared, string content, string expectedType, string expectedExtension)
    {
        var bytes = content.Select(ch => (byte)ch).ToArray();

        var (type, extension) = ConsignmentLabelService.Validate(new CourierLabel(bytes, declared));

        type.Should().Be(expectedType);
        extension.Should().Be(expectedExtension);
    }

    [Fact]
    public void Jpeg_is_recognised_by_its_signature()
    {
        var (type, _) = ConsignmentLabelService.Validate(new CourierLabel([0xFF, 0xD8, 0xFF, 0xE0, 0x00], "image/jpeg"));
        type.Should().Be("image/jpeg");
    }

    [Theory]
    [InlineData("text/html",        "<html></html>", "not a label format")]
    [InlineData("image/svg+xml",    "<svg/>",        "not a label format")]
    [InlineData("application/json", "{}",            "not a label format")]
    [InlineData("application/pdf",  "<html>",        "content is not")]
    [InlineData("image/png",        "%PDF-",         "content is not")]
    [InlineData("application/pdf",  "",              "empty")]
    public void Anything_that_is_not_really_a_label_is_refused(string declared, string content, string expected)
    {
        var act = () => ConsignmentLabelService.Validate(new CourierLabel(Encoding.ASCII.GetBytes(content), declared));

        act.Should().Throw<CarrierUnavailableException>().WithMessage($"*{expected}*");
    }

    [Fact]
    public void An_oversized_label_is_refused()
    {
        var bytes = new byte[ConsignmentLabelService.MaxLabelBytes + 1];
        "%PDF-"u8.CopyTo(bytes);

        var act = () => ConsignmentLabelService.Validate(new CourierLabel(bytes, "application/pdf"));

        act.Should().Throw<CarrierUnavailableException>().WithMessage("*limit is 5 MB*");
    }

    [Fact]
    public void The_file_name_is_built_here_and_carries_nothing_that_could_escape_a_header_or_a_path()
    {
        ConsignmentLabelService.FileNameFor("SHP-2026-00001", "../..\\AWB\"1;\r\nX", "pdf")
            .Should().Be("SHP-2026-00001-AWB1X.pdf");
    }

    // ── The HTTP surface ──────────────────────────────────────────────────────

    private sealed class StubLabels(Func<ConsignmentLabelFile?> answer) : IConsignmentLabelService
    {
        public Task<ConsignmentLabelFile?> GetLabelAsync(Guid consignmentUuid, int userId, CancellationToken ct = default) =>
            Task.FromResult(answer());
    }

    private static ConsignmentsController Controller(IConsignmentLabelService labels) =>
        // Only the label service is exercised here; every other dependency is deliberately absent.
        new(null!, null!, labels, null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "42")], "test"))
                }
            }
        };

    [Fact]
    public async Task The_label_is_served_inline_uncached_and_unsniffable()
    {
        var controller = Controller(new StubLabels(() =>
            new ConsignmentLabelFile(ScriptedCourierProvider.Pdf(), "application/pdf", "SHP-2026-00001-AWB1.pdf")));

        var result = await controller.GetLabel(Guid.NewGuid(), CancellationToken.None);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("application/pdf");
        file.FileDownloadName.Should().BeNullOrEmpty("a download name would force an attachment instead of opening to print");

        var headers = controller.Response.Headers;
        headers.CacheControl.ToString().Should().Be("no-store");
        headers.XContentTypeOptions.ToString().Should().Be("nosniff");
        headers.ContentDisposition.ToString().Should().StartWith("inline").And.Contain("SHP-2026-00001-AWB1.pdf");
    }

    [Fact]
    public async Task A_carrier_that_cannot_supply_the_label_is_a_502_not_a_server_error()
    {
        var controller = Controller(new StubLabels(() => throw new CarrierUnavailableException("try later")));

        var result = await controller.GetLabel(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
    }

    [Fact]
    public async Task An_unknown_consignment_is_a_404()
    {
        var result = await Controller(new StubLabels(() => null)).GetLabel(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public void Printing_a_label_needs_the_booking_permission()
    {
        typeof(ConsignmentsController).GetMethod(nameof(ConsignmentsController.GetLabel))!
            .GetCustomAttribute<RequirePermissionAttribute>()!.Policy.Should().Be("Permission:SHIPMENT_BOOK");
    }

    // ── Against a real database ───────────────────────────────────────────────

    [SqlServerFact]
    public async Task Two_people_printing_the_same_label_at_once_store_it_once()
    {
        await using var server = await SqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        var tenant = new StaticTenantContext { OrganizationId = org };

        Guid uuid;
        await using (var setup = server.NewContext(org))
            uuid = await Booked(NewHarness(db: setup, tenant: tenant));

        var gate = new TaskCompletionSource();

        async Task<ConsignmentLabelFile> Print()
        {
            await using var db = server.NewContext(org);
            var h = NewHarness(db: db, tenant: tenant);
            // Both reach the carrier before either stores, so both try to insert.
            h.Carrier.ThenLabel(async (awb, _) =>
            {
                await gate.Task;
                return new CourierLabelResult(CourierOutcome.Succeeded, "SCRIPTED",
                    new CourierLabel(ScriptedCourierProvider.Pdf(awb), "application/pdf"));
            });
            return (await h.Labels.GetLabelAsync(uuid, User))!;
        }

        var prints = new[] { Print(), Print() };
        await Task.Delay(300);
        gate.SetResult();
        var files = await Task.WhenAll(prints);

        files[0].Content.Should().Equal(files[1].Content);

        await using var check = server.NewContext(org);
        (await check.ConsignmentLabels.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await check.Consignments.IgnoreQueryFilters().SingleAsync()).Status.Should().Be("LABEL_READY");
    }

    // ── Helpers for the booking path ──────────────────────────────────────────

    private static ConsignmentBookingService BookingService(Harness h)
    {
        var resolver = new CarrierAccountResolver(h.Db, h.Registry, new CarrierCredentialVault(h.Db, TestEncryption.New()));
        return new ConsignmentBookingService(
            h.Db, resolver, new CarrierCommandLedger(h.Db, h.Tenant, h.Registry), h.Registry,
            new NoScheduler(), h.Labels, NoConfig, NullLogger<ConsignmentBookingService>.Instance);
    }

    private sealed class NoScheduler : IConsignmentBookingScheduler
    {
        public void Enqueue(Guid consignmentUuid, Guid organizationId) { }
        public void ScheduleRetry(Guid consignmentUuid, Guid organizationId, TimeSpan delay) { }
    }

    private static async Task<Guid> SeedForBooking(Harness h)
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = "Scripted Express", Code = "SCR001", IntegrationMode = "API",
            ProviderKey = h.Carrier.Key, IsActive = true
        };
        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();
        await h.Accounts.CreateAsync(new CreateCarrierAccountRequest { CarrierUuid = carrier.UUID, AccountName = "Main" }, User);

        var delivery = new DeliveryOrder
        {
            UUID = Guid.NewGuid(), DeliveryNumber = "DLV-2026-00002", Direction = "OUTBOUND", SourceType = "MANUAL",
            ShipFromAddress = Address("Plot 1", "Karachi"), ShipToAddress = Address("Road 2", "Lahore"),
            CreatedBy = User, CreatedDate = T0
        };
        delivery.Packages.Add(new ShipmentPackage { UUID = Guid.NewGuid(), PackageBarcode = "PKG-001", GrossWeightKg = 4m, CreatedBy = User, CreatedDate = T0 });
        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = "SHP-2026-00088", CarrierId = carrier.Id, CarrierName = carrier.Name,
            CreatedBy = User, CreatedDate = T0
        };
        consignment.Deliveries.Add(new ConsignmentDelivery { UUID = Guid.NewGuid(), DeliveryOrder = delivery, Sequence = 1, CreatedBy = User, CreatedDate = T0 });
        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        return consignment.UUID;
    }

    private static Address Address(string line1, string city) => new()
    {
        UUID = Guid.NewGuid(), Line1 = line1, CityName = city, CountryName = "Pakistan", CountryIsoCode = "PK",
        CreatedBy = User, CreatedDate = T0
    };
}

using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SMS.Modules.Logistics.Controllers;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

/// <summary>
/// A29-P7-09 §8.3 — a delivery's gate pass kept on file as an attachment on the delivery. The
/// attachment store and the gate pass renderer are mocks here (each has its own tests); what is
/// checked is what is filed, under what, behind which permission — and that filing never gets in the
/// way of the collection it follows.
/// </summary>
public class GatePassArchiveTests
{
    private const int User = 42;
    private static readonly Guid DeliveryId = Guid.NewGuid();
    private static readonly byte[] Pdf = "%PDF-1.7\n% GatePass-DLV-2026-00001\n%%EOF"u8.ToArray();
    private static readonly Guid FiledId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class ListLogger : ILogger<GatePassArchive>
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) =>
            Entries.Add((level, formatter(state, error), error));
    }

    private sealed class Rig
    {
        public Mock<IDeliveryDocumentService> Documents { get; } = new();
        public Mock<IAttachmentService> Attachments { get; } = new();
        public ListLogger Log { get; } = new();
        public GeneratedAttachmentRequest? Filed { get; private set; }
        public GatePassArchive Archive { get; }

        public Rig()
        {
            Documents.Setup(d => d.GenerateGatePassAsync(DeliveryId)).ReturnsAsync((Pdf, "GatePass-DLV-2026-00001.pdf"));
            Attachments.Setup(a => a.StoreGeneratedAsync(It.IsAny<GeneratedAttachmentRequest>(), It.IsAny<int>()))
                       .Callback<GeneratedAttachmentRequest, int>((r, _) => Filed = r)
                       .ReturnsAsync(new StoredAttachment(FiledId, false));
            Archive = new GatePassArchive(Documents.Object, Attachments.Object, Log);
        }
    }

    // ── What is filed ────────────────────────────────────────────────────────

    [Fact]
    public async Task The_gate_pass_is_filed_on_the_delivery_behind_the_permission_that_lets_a_person_print_it()
    {
        var rig = new Rig();

        var stored = await rig.Archive.FileGatePassAsync(DeliveryId, User);

        stored.Uuid.Should().Be(FiledId);
        var filed = rig.Filed!;
        filed.InterfaceCode.Should().Be("DELIVERY", "what a delivery's attachment panel is opened with");
        filed.DocumentId.Should().Be(DeliveryId);
        filed.FileName.Should().Be("GatePass-DLV-2026-00001.pdf");
        filed.ContentType.Should().Be("application/pdf");
        filed.Content.Should().Equal(Pdf);
        filed.RequiredPermission.Should().Be(PermissionCodes.DELIVERY_VIEW).And.Be("DELIVERY_VIEW");
        filed.Notes.Should().Be("Gate pass, filed on request.");
        rig.Attachments.Verify(a => a.StoreGeneratedAsync(It.IsAny<GeneratedAttachmentRequest>(), User), Times.Once);
    }

    [Fact]
    public async Task What_is_handed_over_is_what_the_attachment_store_accepts()
    {
        var rig = new Rig();

        await rig.Archive.FileGatePassAsync(DeliveryId, User);

        var filed = rig.Filed!;
        filed.InterfaceCode.Length.Should().BeInRange(1, 30);
        filed.FileName.Length.Should().BeInRange(1, 255);
        filed.FileName.Should().NotContainAny("/", "\\");
        filed.Content.AsSpan().StartsWith("%PDF-"u8).Should().BeTrue();
        (filed.Notes ?? "").Length.Should().BeLessThanOrEqualTo(300);
        (filed.RequiredPermission ?? "").Length.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task Already_filed_is_reported_as_such()
    {
        var rig = new Rig();
        var existing = new StoredAttachment(Guid.NewGuid(), AlreadyStored: true);
        rig.Attachments.Setup(a => a.StoreGeneratedAsync(It.IsAny<GeneratedAttachmentRequest>(), It.IsAny<int>())).ReturnsAsync(existing);

        (await rig.Archive.FileGatePassAsync(DeliveryId, User)).Should().Be(existing);
    }

    [Fact]
    public async Task The_gate_passs_own_refusals_reach_the_caller_of_the_explicit_action_and_nothing_is_filed()
    {
        var rig = new Rig();
        rig.Documents.Setup(d => d.GenerateGatePassAsync(It.IsAny<Guid>()))
                     .ThrowsAsync(new ConflictException("Delivery DLV-2026-00001 is PICKING. A gate pass is issued once the goods are staged."));

        var act = async () => await rig.Archive.FileGatePassAsync(DeliveryId, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*staged*");
        rig.Attachments.Verify(a => a.StoreGeneratedAsync(It.IsAny<GeneratedAttachmentRequest>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task An_unknown_delivery_is_not_found_not_filed_against()
    {
        var rig = new Rig();
        rig.Documents.Setup(d => d.GenerateGatePassAsync(It.IsAny<Guid>())).ThrowsAsync(new NotFoundException("Delivery", Guid.Empty));

        var act = async () => await rig.Archive.FileGatePassAsync(Guid.NewGuid(), User);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ── After a collection: best effort ──────────────────────────────────────

    [Fact]
    public async Task After_a_collection_the_pass_is_filed_with_a_note_saying_why()
    {
        var rig = new Rig();

        var uuid = await rig.Archive.TryFileCollectionPassAsync(DeliveryId, User);

        uuid.Should().Be(FiledId);
        rig.Filed!.Notes.Should().Be("Collection gate pass, filed when the collection was recorded.");
        rig.Log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task If_the_store_fails_after_a_collection_it_is_logged_and_reported_as_null_never_thrown()
    {
        var rig = new Rig();
        rig.Attachments.Setup(a => a.StoreGeneratedAsync(It.IsAny<GeneratedAttachmentRequest>(), It.IsAny<int>()))
                       .ThrowsAsync(new InvalidOperationException("the database is down"));

        var uuid = await rig.Archive.TryFileCollectionPassAsync(DeliveryId, User);

        uuid.Should().BeNull();
        var entry = rig.Log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Message.Should().Contain(DeliveryId.ToString()).And.Contain("file it from the delivery");
        entry.Error.Should().BeOfType<InvalidOperationException>();
    }

    [Theory]
    [InlineData(typeof(ConflictException))]
    [InlineData(typeof(NotFoundException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Whatever_stops_the_pass_being_rendered_is_logged_not_thrown_after_a_collection(Type failure)
    {
        var rig = new Rig();
        var error = failure == typeof(NotFoundException)
            ? new NotFoundException("Delivery", DeliveryId)
            : (Exception)Activator.CreateInstance(failure, "cannot render")!;
        rig.Documents.Setup(d => d.GenerateGatePassAsync(It.IsAny<Guid>())).ThrowsAsync(error);

        (await rig.Archive.TryFileCollectionPassAsync(DeliveryId, User)).Should().BeNull();

        rig.Log.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Error);
        rig.Attachments.Verify(a => a.StoreGeneratedAsync(It.IsAny<GeneratedAttachmentRequest>(), It.IsAny<int>()), Times.Never);
    }

    // ── The endpoints ────────────────────────────────────────────────────────

    private sealed class Controllers
    {
        public Mock<IDeliveryService> Deliveries { get; } = new();
        public Mock<IGatePassArchive> Archive { get; } = new();
        public DeliveriesController Controller { get; }

        public Controllers()
        {
            Controller = new DeliveriesController(
                Deliveries.Object, Mock.Of<IPickListService>(), Mock.Of<IPackageService>(),
                Mock.Of<IDeliveryDocumentService>(), Archive.Object)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", User.ToString())], "test"))
                    }
                }
            };
        }
    }

    private static readonly RecordPickupRequest Collection = new()
    {
        PickupPersonName = "Ahmed Raza", PickupPersonIdType = "CNIC", PickupPersonIdNumber = "35202-1234567-1"
    };

    private static string MessageOf<T>(IActionResult result) => ((ApiResponse<T>)((OkObjectResult)result).Value!).Message;

    [Fact]
    public async Task Recording_a_collection_files_the_pass_afterwards_and_says_so()
    {
        var t = new Controllers();
        var calls = new List<string>();
        var picked = new PickupResultModel();
        t.Deliveries.Setup(s => s.RecordPickupAsync(DeliveryId, Collection, User)).Callback(() => calls.Add("collect")).ReturnsAsync(picked);
        t.Archive.Setup(a => a.TryFileCollectionPassAsync(DeliveryId, User)).Callback(() => calls.Add("file")).ReturnsAsync((Guid?)FiledId);

        var result = await t.Controller.RecordPickup(DeliveryId, Collection);

        calls.Should().Equal("collect", "file");
        ((ApiResponse<PickupResultModel>)((OkObjectResult)result).Value!).Result.Should().BeSameAs(picked);
        MessageOf<PickupResultModel>(result).Should().Contain("Delivery marked as delivered").And.Contain("gate pass is filed on the delivery");
    }

    [Fact]
    public async Task If_the_pass_cannot_be_filed_the_collection_still_stands_and_the_response_says_what_to_do()
    {
        var t = new Controllers();
        var picked = new PickupResultModel();
        t.Deliveries.Setup(s => s.RecordPickupAsync(DeliveryId, Collection, User)).ReturnsAsync(picked);
        t.Archive.Setup(a => a.TryFileCollectionPassAsync(DeliveryId, User)).ReturnsAsync((Guid?)null);

        var result = await t.Controller.RecordPickup(DeliveryId, Collection);

        ((ApiResponse<PickupResultModel>)((OkObjectResult)result).Value!).Result.Should().BeSameAs(picked, "the collection itself succeeded");
        MessageOf<PickupResultModel>(result).Should().Contain("could not be filed").And.Contain("file it from the delivery");
    }

    [Fact]
    public async Task A_delivery_that_is_not_there_or_a_collection_that_fails_files_nothing()
    {
        var t = new Controllers();
        t.Deliveries.Setup(s => s.RecordPickupAsync(It.IsAny<Guid>(), It.IsAny<RecordPickupRequest>(), It.IsAny<int>())).ReturnsAsync((PickupResultModel?)null);

        (await t.Controller.RecordPickup(DeliveryId, Collection)).Should().BeOfType<NotFoundObjectResult>();

        t.Deliveries.Setup(s => s.RecordPickupAsync(It.IsAny<Guid>(), It.IsAny<RecordPickupRequest>(), It.IsAny<int>())).ThrowsAsync(new ConflictException("not staged"));
        var act = async () => await t.Controller.RecordPickup(DeliveryId, Collection);
        await act.Should().ThrowAsync<ConflictException>();

        t.Archive.Verify(a => a.TryFileCollectionPassAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Attaching_the_gate_pass_on_demand_returns_the_attachment_and_names_a_repeat()
    {
        var t = new Controllers();
        var fresh = new StoredAttachment(Guid.NewGuid(), false);
        var repeat = new StoredAttachment(Guid.NewGuid(), true);
        t.Archive.SetupSequence(a => a.FileGatePassAsync(DeliveryId, User)).ReturnsAsync(fresh).ReturnsAsync(repeat);

        var first = await t.Controller.AttachGatePass(DeliveryId);
        var second = await t.Controller.AttachGatePass(DeliveryId);

        ((ApiResponse<StoredAttachment>)((OkObjectResult)first).Value!).Result.Should().Be(fresh);
        MessageOf<StoredAttachment>(first).Should().Be("Gate pass filed as an attachment.");
        MessageOf<StoredAttachment>(second).Should().Contain("already on file");
    }

    [Fact]
    public void The_attach_action_is_a_post_on_the_gate_pass_route_and_needs_the_permission_to_edit_a_delivery()
    {
        var method = typeof(DeliveriesController).GetMethod(nameof(DeliveriesController.AttachGatePass))!;

        method.GetCustomAttributes(typeof(HttpPostAttribute), false).Cast<HttpPostAttribute>().Single()
              .Template.Should().Be("{uuid:guid}/gate-pass/attach");
        method.GetCustomAttributes(typeof(RequirePermissionAttribute), false).Cast<RequirePermissionAttribute>().Single()
              .Policy.Should().Be("Permission:DELIVERY_EDIT", "filing changes what is on the delivery; reading the pass is DELIVERY_VIEW");
    }

    // ── Registration ─────────────────────────────────────────────────────────

    [Fact]
    public void The_archive_resolves_from_the_modules_own_registration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=none;Database=none;Trusted_Connection=True;" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new StaticTenantContext());
        services.AddLogisticsModule(configuration);

        // What the archive is handed by the rest of the application.
        services.AddSingleton(Mock.Of<IDeliveryDocumentService>());
        services.AddSingleton(Mock.Of<IAttachmentService>());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IGatePassArchive>().Should().BeOfType<GatePassArchive>();
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Visibility;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Visibility;

// T-61 — proof that goods reached somebody. Reaches F47: until this existed the only proof of
// delivery was a 500-character URL on the legacy shipment table, pointing somewhere nobody controls.
public class DeliveryProofTests
{
    private const int User = 42;

    private static readonly DateTime T0 = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>A one-pixel PNG. Real bytes, because the service checks them against the type.</summary>
    private static readonly byte[] Png =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01
    ];

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
    private static readonly byte[] Pdf  = "%PDF-1.4 a scanned delivery note"u8.ToArray();

    private sealed record Harness(LogisticsDbContext Db, DeliveryProofService Proofs, string DbName);

    private static Harness NewHarness()
    {
        var (db, _, dbName) = LogisticsTestDb.New();
        return new Harness(db, new DeliveryProofService(db), dbName);
    }

    private static async Task<Guid> NewCarrier(Harness h, string name = "Beta Road")
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = name, Code = $"C{Guid.NewGuid():N}"[..6], IsActive = true
        };
        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return carrier.UUID;
    }

    private static async Task<Guid> NewConsignment(
        Harness h, Guid? carrier = null, string status = "OUT_FOR_DELIVERY",
        DateTime? dispatchedAt = null, DateTime? arrivedAt = null)
    {
        var carrierRow = carrier is null
            ? null
            : await h.Db.Carriers.AsNoTracking().SingleAsync(c => c.UUID == carrier);

        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CarrierId = carrierRow?.Id, CarrierName = carrierRow?.Name,
            Status = status, MasterAwb = "AWB-1",
            ActualDispatchAt = dispatchedAt, ActualArrivalAt = arrivedAt,
            CreatedBy = User, CreatedDate = T0
        };

        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return consignment.UUID;
    }

    private static async Task<Guid> NewStop(Harness h, Guid consignmentUuid, int sequence)
    {
        var consignment = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == consignmentUuid);

        var stop = new ConsignmentStop
        {
            UUID = Guid.NewGuid(), ConsignmentId = consignment.Id, Sequence = sequence,
            StopType = LogisticsCode.Of(StopType.Drop), CreatedBy = User, CreatedDate = T0
        };

        h.Db.ConsignmentStops.Add(stop);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return stop.UUID;
    }

    private static async Task<int> NewDeliveredEvent(
        Harness h, Guid consignmentUuid, string? signedBy = "R. Ahmed",
        string? location = "Gate 3", DateTime? occurredAt = null, string milestone = "DELIVERED")
    {
        var consignment = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == consignmentUuid);

        var evt = new ConsignmentTrackingEvent
        {
            UUID = Guid.NewGuid(), ConsignmentId = consignment.Id,
            Milestone = milestone, SignedBy = signedBy, Location = location,
            CarrierStatus = "DL01",
            OccurredAt = occurredAt ?? T0, ReceivedAt = occurredAt ?? T0,
            Source = LogisticsCode.Of(TrackingEventSource.Webhook),
            EventKey = Guid.NewGuid().ToString("N").PadRight(64, '0')[..64],
            CreatedDate = T0
        };

        h.Db.ConsignmentTrackingEvents.Add(evt);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return evt.Id;
    }

    private static RecordProofRequest Record(
        string receivedBy = "R. Ahmed", string? relationship = null,
        DateTime? deliveredAt = null, Guid? stop = null) =>
        new()
        {
            ReceivedBy = receivedBy, Relationship = relationship,
            DeliveredAt = deliveredAt ?? T0, ConsignmentStopUuid = stop
        };

    // ── Recording by hand ─────────────────────────────────────────────────────

    [Fact]
    public async Task Recording_a_handover_names_who_took_the_goods_and_when()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var consignment = await NewConsignment(h, carrier, dispatchedAt: T0.AddHours(-4));

        var uuid = await h.Proofs.RecordAsync(
            consignment, Record(relationship: "security guard"), User);

        var proof = await h.Proofs.GetAsync(uuid!.Value);

        proof!.ReceivedBy.Should().Be("R. Ahmed");
        proof.Relationship.Should().Be("security guard");
        proof.DeliveredAt.Should().Be(T0);
        proof.Source.Should().Be("MANUAL");
        proof.CarrierName.Should().Be("Beta Road");
        proof.MasterAwb.Should().Be("AWB-1");
    }

    [Fact]
    public async Task Recording_a_handover_is_what_marks_a_manual_carrier_delivered()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, dispatchedAt: T0.AddHours(-4));

        await h.Proofs.RecordAsync(consignment, Record(), User);

        var stored = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == consignment);

        stored.Status.Should().Be("DELIVERED", "nothing polls a van driver");
        stored.ActualArrivalAt.Should().Be(T0);
    }

    [Fact]
    public async Task Recording_against_an_already_delivered_consignment_leaves_its_status_alone()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(
            h, status: "DELIVERED", dispatchedAt: T0.AddHours(-4), arrivedAt: T0.AddMinutes(-30));

        await h.Proofs.RecordAsync(consignment, Record(), User);

        var stored = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == consignment);

        stored.Status.Should().Be("DELIVERED");
        stored.ActualArrivalAt.Should().Be(T0.AddMinutes(-30),
            "the carrier's account of when it arrived is not overwritten by ours");
    }

    [Fact]
    public async Task A_proof_against_a_cancelled_consignment_is_refused()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, status: "CANCELLED");

        var record = async () => await h.Proofs.RecordAsync(consignment, Record(), User);

        await record.Should().ThrowAsync<ConflictException>()
            .WithMessage("*a movement that never happened*");
    }

    [Fact]
    public async Task A_proof_naming_nobody_is_refused_from_a_person()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);

        var record = async () => await h.Proofs.RecordAsync(consignment, Record(receivedBy: "  "), User);

        await record.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*proof of nothing*");
    }

    [Fact]
    public async Task A_delivery_cannot_have_happened_in_the_future()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);

        var record = async () => await h.Proofs.RecordAsync(
            consignment, Record(deliveredAt: DateTime.UtcNow.AddDays(1)), User);

        await record.Should().ThrowAsync<BadRequestException>().WithMessage("*in the future*");
    }

    [Fact]
    public async Task A_delivery_cannot_have_happened_before_the_goods_left()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, dispatchedAt: T0);

        var record = async () => await h.Proofs.RecordAsync(
            consignment, Record(deliveredAt: T0.AddHours(-2)), User);

        await record.Should().ThrowAsync<BadRequestException>().WithMessage("*before they left*");
    }

    [Fact]
    public async Task One_consignment_gets_one_proof()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await h.Proofs.RecordAsync(consignment, Record(), User);

        var second = async () => await h.Proofs.RecordAsync(consignment, Record(), User);

        await second.Should().ThrowAsync<ConflictException>()
            .WithMessage("*a second account of one handover*");
    }

    [Fact]
    public async Task Recording_against_a_consignment_that_is_not_there_returns_nothing()
    {
        var h = NewHarness();

        (await h.Proofs.RecordAsync(Guid.NewGuid(), Record(), User)).Should().BeNull();
    }

    // ── Multi-stop ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_multi_stop_run_gets_one_proof_per_drop()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var first  = await NewStop(h, consignment, 1);
        var second = await NewStop(h, consignment, 2);

        await h.Proofs.RecordAsync(consignment, Record("A. Khan",  stop: first), User);
        await h.Proofs.RecordAsync(consignment, Record("B. Iqbal", stop: second), User);

        var proofs = await h.Proofs.GetForConsignmentAsync(consignment);

        proofs.Should().HaveCount(2);
        proofs.Select(p => p.StopSequence).Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public async Task The_same_drop_cannot_be_proved_twice()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var stop = await NewStop(h, consignment, 1);

        await h.Proofs.RecordAsync(consignment, Record(stop: stop), User);

        var again = async () => await h.Proofs.RecordAsync(consignment, Record(stop: stop), User);

        await again.Should().ThrowAsync<ConflictException>().WithMessage("*Stop 1*");
    }

    [Fact]
    public async Task A_proof_cannot_be_filed_against_another_consignments_stop()
    {
        var h = NewHarness();
        var mine     = await NewConsignment(h);
        var somebodys = await NewConsignment(h);
        var theirStop = await NewStop(h, somebodys, 1);

        var record = async () => await h.Proofs.RecordAsync(mine, Record(stop: theirStop), User);

        await record.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*proves the wrong delivery*");
    }

    [Fact]
    public async Task Proving_a_drop_records_when_the_vehicle_got_there()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var stop = await NewStop(h, consignment, 1);

        await h.Proofs.RecordAsync(consignment, Record(deliveredAt: T0, stop: stop), User);

        var stored = await h.Db.ConsignmentStops.AsNoTracking().SingleAsync(s => s.UUID == stop);

        stored.ActualArrival.Should().Be(T0);
    }

    // ── From what the carrier reported ────────────────────────────────────────

    [Fact]
    public async Task A_delivered_scan_becomes_the_proof_without_anybody_typing_anything()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await NewDeliveredEvent(h, consignment);

        var recorded = await h.Proofs.SweepFromTrackingAsync(0);

        recorded.Should().Be(1);

        var proof = (await h.Proofs.GetForConsignmentAsync(consignment)).Single();

        proof.Source.Should().Be("CARRIER");
        proof.ReceivedBy.Should().Be("R. Ahmed");
        proof.Location.Should().Be("Gate 3");
        proof.DeliveredAt.Should().Be(T0);
        proof.CarrierStatus.Should().Be("DL01");
    }

    [Fact]
    public async Task A_scan_that_names_nobody_is_stored_anyway_and_says_so()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await NewDeliveredEvent(h, consignment, signedBy: null);

        await h.Proofs.SweepFromTrackingAsync(0);

        var proof = (await h.Proofs.GetForConsignmentAsync(consignment)).Single();

        proof.ReceivedBy.Should().BeNull(
            "discarding what the carrier said would lose the only record of the event, and inventing "
          + "a name to fill a required field would be worse");
        proof.IsDefensible.Should().BeFalse();
        proof.Warnings.Should().Contain(w => w.Contains("named nobody"));
    }

    [Fact]
    public async Task Ordinary_scans_produce_no_proof()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await NewDeliveredEvent(h, consignment, milestone: "OUT_FOR_DELIVERY");
        await NewDeliveredEvent(h, consignment, milestone: "IN_TRANSIT");

        (await h.Proofs.SweepFromTrackingAsync(0)).Should().Be(0);
    }

    [Fact]
    public async Task The_same_scan_swept_twice_produces_one_proof()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await NewDeliveredEvent(h, consignment);

        await h.Proofs.SweepFromTrackingAsync(0);

        (await h.Proofs.SweepFromTrackingAsync(0)).Should().Be(0);
        (await h.Proofs.GetForConsignmentAsync(consignment)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Two_delivered_scans_for_one_consignment_produce_one_proof()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await NewDeliveredEvent(h, consignment, occurredAt: T0);
        await NewDeliveredEvent(h, consignment, occurredAt: T0.AddMinutes(20));

        var recorded = await h.Proofs.SweepFromTrackingAsync(0);

        recorded.Should().Be(1, "the first is the handover and the rest are restatements of it");
        (await h.Proofs.GetForConsignmentAsync(consignment)).Single()
            .DeliveredAt.Should().Be(T0);
    }

    [Fact]
    public async Task A_proof_somebody_recorded_by_hand_is_not_overwritten_by_a_later_scan()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await h.Proofs.RecordAsync(consignment, Record("R. Ahmed in person"), User);
        await NewDeliveredEvent(h, consignment, signedBy: "ILLEGIBLE");

        (await h.Proofs.SweepFromTrackingAsync(0)).Should().Be(0);

        (await h.Proofs.GetForConsignmentAsync(consignment)).Single()
            .ReceivedBy.Should().Be("R. Ahmed in person",
                "the person who typed it was there and the scan was not");
    }

    // ── The artefacts ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_signature_is_stored_and_served_back_byte_for_byte()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var proof = await h.Proofs.RecordAsync(consignment, Record(), User);

        var fileUuid = await h.Proofs.AttachFileAsync(
            proof!.Value, "SIGNATURE", Png, "image/png", User);

        var served = await h.Proofs.GetFileAsync(fileUuid!.Value);

        served!.Content.Should().Equal(Png, "the bytes are the point — F47 was a link");
        served.ContentType.Should().Be("image/png");
    }

    [Theory]
    [InlineData("SIGNATURE")]
    [InlineData("PHOTO")]
    [InlineData("DOCUMENT")]
    public async Task Every_kind_of_artefact_can_actually_be_attached(string kind)
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var proof = await h.Proofs.RecordAsync(consignment, Record(), User);

        var uuid = await h.Proofs.AttachFileAsync(proof!.Value, kind, Pdf, "application/pdf", User);

        uuid.Should().NotBeNull();
        (await h.Proofs.GetAsync(proof.Value))!.Files.Single().Kind.Should().Be(kind);
    }

    [Fact]
    public async Task The_file_name_is_built_here_and_never_taken_from_the_upload()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var proof = await h.Proofs.RecordAsync(consignment, Record(), User);

        await h.Proofs.AttachFileAsync(proof!.Value, "PHOTO", Jpeg, "image/jpeg", User);

        var file = (await h.Proofs.GetAsync(proof.Value))!.Files.Single();

        file.FileName.Should().EndWith(".jpg");
        file.FileName.Should().NotContainAny("/", "\\", "\"", "..");
    }

    [Fact]
    public async Task An_upload_claiming_to_be_a_png_that_is_not_one_is_refused()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var proof = await h.Proofs.RecordAsync(consignment, Record(), User);

        var attach = async () => await h.Proofs.AttachFileAsync(
            proof!.Value, "SIGNATURE", "<html><script>alert(1)</script></html>"u8.ToArray(),
            "image/png", User);

        await attach.Should().ThrowAsync<BadRequestException>().WithMessage("*and the content is not*");
    }

    [Fact]
    public async Task A_type_a_browser_would_render_as_a_page_cannot_be_stored_at_all()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var proof = await h.Proofs.RecordAsync(consignment, Record(), User);

        var attach = async () => await h.Proofs.AttachFileAsync(
            proof!.Value, "DOCUMENT", "<html></html>"u8.ToArray(), "text/html", User);

        await attach.Should().ThrowAsync<BadRequestException>().WithMessage("*cannot be stored as proof*");
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var proof = await h.Proofs.RecordAsync(consignment, Record(), User);

        var attach = async () => await h.Proofs.AttachFileAsync(
            proof!.Value, "SIGNATURE", [], "image/png", User);

        await attach.Should().ThrowAsync<BadRequestException>().WithMessage("*empty*");
    }

    [Fact]
    public async Task A_file_past_the_limit_is_refused()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var proof = await h.Proofs.RecordAsync(consignment, Record(), User);

        var huge = new byte[DeliveryProofService.MaxFileBytes + 1];
        Png.CopyTo(huge, 0);

        var attach = async () => await h.Proofs.AttachFileAsync(
            proof!.Value, "PHOTO", huge, "image/png", User);

        await attach.Should().ThrowAsync<BadRequestException>().WithMessage("*limit is 10 MB*");
    }

    [Fact]
    public async Task The_same_bytes_uploaded_twice_are_stored_once()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var proof = await h.Proofs.RecordAsync(consignment, Record(), User);

        var first  = await h.Proofs.AttachFileAsync(proof!.Value, "SIGNATURE", Png, "image/png", User);
        var second = await h.Proofs.AttachFileAsync(proof.Value,  "SIGNATURE", Png, "image/png", User);

        second.Should().Be(first!.Value, "phones retry uploads on a bad signal");
        (await h.Proofs.GetAsync(proof.Value))!.Files.Should().HaveCount(1);
    }

    [Fact]
    public async Task An_artefact_removed_in_error_is_removed_softly()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var proof = await h.Proofs.RecordAsync(consignment, Record(), User);
        var file = await h.Proofs.AttachFileAsync(proof!.Value, "PHOTO", Jpeg, "image/jpeg", User);

        (await h.Proofs.RemoveFileAsync(file!.Value, User)).Should().BeTrue();

        (await h.Proofs.GetFileAsync(file.Value)).Should().BeNull();
        (await h.Proofs.GetAsync(proof.Value))!.Files.Should().BeEmpty();

        var stored = await h.Db.DeliveryProofFiles.AsNoTracking()
            .IgnoreQueryFilters().SingleAsync(f => f.UUID == file.Value);

        stored.IsDelete.Should().BeTrue();
        stored.Content.Should().Equal(Jpeg,
            "evidence deleted by mistake and gone for good is the one deletion nobody can undo");
    }

    [Fact]
    public async Task Attaching_to_a_proof_that_is_not_there_returns_nothing()
    {
        var h = NewHarness();

        (await h.Proofs.AttachFileAsync(Guid.NewGuid(), "PHOTO", Png, "image/png", User))
            .Should().BeNull();
    }

    // ── Is it defensible ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_proof_with_a_name_and_an_artefact_is_defensible()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var proof = await h.Proofs.RecordAsync(consignment, Record(), User);
        await h.Proofs.AttachFileAsync(proof!.Value, "SIGNATURE", Png, "image/png", User);

        var model = await h.Proofs.GetAsync(proof.Value);

        model!.IsDefensible.Should().BeTrue();
        model.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_name_with_nothing_on_file_says_what_is_missing()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var proof = await h.Proofs.RecordAsync(consignment, Record(), User);

        var model = await h.Proofs.GetAsync(proof!.Value);

        model!.IsDefensible.Should().BeFalse();
        model.Warnings.Should().Contain(w => w.Contains("no signature, photograph or document"));
    }

    [Fact]
    public async Task Filling_in_the_name_a_carrier_never_gave_is_what_a_patch_is_for()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await NewDeliveredEvent(h, consignment, signedBy: null);
        await h.Proofs.SweepFromTrackingAsync(0);

        var proof = (await h.Proofs.GetForConsignmentAsync(consignment)).Single();

        await h.Proofs.PatchAsync(proof.UUID, new PatchProofRequest
        {
            ReceivedBy = "R. Ahmed", Relationship = "reception", Notes = "Confirmed by telephone."
        }, User);

        var patched = await h.Proofs.GetAsync(proof.UUID);

        patched!.ReceivedBy.Should().Be("R. Ahmed");
        patched.Relationship.Should().Be("reception");
        patched.DeliveredAt.Should().Be(T0, "the carrier's account of when it happened is not amendable");
    }

    [Fact]
    public async Task A_name_already_recorded_cannot_be_blanked_out()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var proof = await h.Proofs.RecordAsync(consignment, Record(), User);

        var patch = async () => await h.Proofs.PatchAsync(
            proof!.Value, new PatchProofRequest { ReceivedBy = "   " }, User);

        await patch.Should().ThrowAsync<BadRequestException>().WithMessage("*cannot be blanked out*");
    }

    [Fact]
    public async Task A_proof_against_a_consignment_the_system_thinks_is_still_moving_is_flagged()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        await NewDeliveredEvent(h, consignment);
        await h.Proofs.SweepFromTrackingAsync(0);

        var proof = (await h.Proofs.GetForConsignmentAsync(consignment)).Single();

        proof.Warnings.Should().Contain(w => w.Contains("not delivered"),
            "the scan recorded the proof; only the status machine moves the consignment");
    }

    // ── Coverage ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Coverage_counts_what_is_delivered_against_what_can_be_demonstrated()
    {
        var h = NewHarness();

        // Defensible: a name and an artefact.
        var good = await NewConsignment(h, dispatchedAt: T0.AddHours(-2));
        var goodProof = await h.Proofs.RecordAsync(good, Record(), User);
        await h.Proofs.AttachFileAsync(goodProof!.Value, "SIGNATURE", Png, "image/png", User);

        // Weak: a name, nothing on file.
        var weak = await NewConsignment(h, dispatchedAt: T0.AddHours(-2));
        await h.Proofs.RecordAsync(weak, Record(), User);

        // Nothing at all.
        await NewConsignment(h, status: "DELIVERED", arrivedAt: T0.AddDays(-9));

        var coverage = await h.Proofs.GetCoverageAsync();

        coverage.Delivered.Should().Be(3);
        coverage.WithProof.Should().Be(2);
        coverage.Defensible.Should().Be(1);
        coverage.Weak.Should().Be(1);
        coverage.WithoutProof.Should().Be(1);
    }

    [Fact]
    public async Task Coverage_says_plainly_what_a_gap_means()
    {
        var h = NewHarness();
        await NewConsignment(h, status: "DELIVERED", arrivedAt: T0);

        var coverage = await h.Proofs.GetCoverageAsync();

        coverage.Warnings.Should().Contain(w => w.Contains("cannot be demonstrated if it is denied"));
        coverage.Gaps.Single().Gap.Should().Be("Delivered with no proof of any kind.");
    }

    [Fact]
    public async Task Gaps_come_oldest_first_because_that_is_what_gets_chased()
    {
        var h = NewHarness();
        await NewConsignment(h, status: "DELIVERED", arrivedAt: T0.AddDays(-1));
        await NewConsignment(h, status: "DELIVERED", arrivedAt: T0.AddDays(-30));
        await NewConsignment(h, status: "DELIVERED", arrivedAt: T0.AddDays(-7));

        var coverage = await h.Proofs.GetCoverageAsync();

        coverage.Gaps.Select(g => g.DeliveredAt)
            .Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task A_consignment_still_in_transit_is_not_counted_as_an_undocumented_delivery()
    {
        var h = NewHarness();
        await NewConsignment(h, status: "IN_TRANSIT");

        var coverage = await h.Proofs.GetCoverageAsync();

        coverage.Delivered.Should().Be(0);
        coverage.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task On_a_multi_stop_run_one_unevidenced_drop_makes_the_whole_run_weak()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h, dispatchedAt: T0.AddHours(-3));
        var first  = await NewStop(h, consignment, 1);
        var second = await NewStop(h, consignment, 2);

        var evidenced = await h.Proofs.RecordAsync(consignment, Record("A. Khan", stop: first), User);
        await h.Proofs.AttachFileAsync(evidenced!.Value, "SIGNATURE", Png, "image/png", User);

        await h.Proofs.RecordAsync(consignment, Record("B. Iqbal", stop: second), User);

        var stored = await h.Db.Consignments.SingleAsync(c => c.UUID == consignment);
        stored.Status = "DELIVERED";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var coverage = await h.Proofs.GetCoverageAsync();

        coverage.Defensible.Should().Be(0,
            "a run where two drops are evidenced and the third is not is disputed on the third");
        coverage.Weak.Should().Be(1);
    }

    // ── Tenancy ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_organization_never_sees_anothers_proofs_or_their_artefacts()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);
        var proof = await h.Proofs.RecordAsync(consignment, Record(), User);
        var file = await h.Proofs.AttachFileAsync(proof!.Value, "SIGNATURE", Png, "image/png", User);

        var stranger = new DeliveryProofService(LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid()));

        (await stranger.GetAsync(proof.Value)).Should().BeNull();
        (await stranger.GetFileAsync(file!.Value)).Should().BeNull();
        (await stranger.GetCoverageAsync()).Delivered.Should().Be(0);
    }
}

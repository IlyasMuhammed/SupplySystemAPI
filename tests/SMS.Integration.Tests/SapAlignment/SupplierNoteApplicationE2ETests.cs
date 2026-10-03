using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace SMS.Integration.Tests.SapAlignment;

/// <summary>
/// Supplier credit and debit notes (raised from supplier return orders) against invoice reversal and supplier
/// payments, end to end through the real host on LocalDB. The rules (purchase reviewer C', items B and E):
/// <list type="bullet">
/// <item>A note is applied only to an Approved invoice; naming any other invoice it is CARRIED_FORWARD, and applying
/// a carried-forward note to one is a 422.</item>
/// <item>A note never takes an invoice below what is paid or allocated (PaidAmount + live DRAFT/APPROVED payment
/// lines): on create it is carried forward instead, on apply it is a 422.</item>
/// <item>Applying one supplier's note to another supplier's invoice is a 400.</item>
/// <item>An invoice with a note applied cannot be reversed (409). Note application and supplier-payment
/// create/approve/post take the invoice's row lock, so a note racing a reversal or a payment has exactly one
/// winner and the books stay consistent.</item>
/// </list>
/// "The books agree" means, for one supplier: supplier-ledger balance = Σ (TotalAmount − PaidAmount) over its
/// Approved invoices − Σ carried-forward notes; and every invoice's TotalAmount = Subtotal + Tax − Σ notes applied
/// to it. No tax codes in this class, so every amount is net. Run alone:
/// <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~SupplierNoteApplicationE2ETests</c>.
/// </summary>
public sealed class SupplierNoteApplicationE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private const string Invoices = "/api/finance/invoices";
    private const decimal Price = 300m;

    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public SupplierNoteApplicationE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "SN");
    }

    private enum Kind { Credit, Debit }

    // ═══════════════════════════════════════════════════════════════════════════
    // 1. Happy path: PO → GRN → approved invoice → returns → notes → applied
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Returns_become_notes_that_reduce_the_approved_invoice_and_the_supplier_ledger()
    {
        var (wh, item) = await SetupAsync();
        var vendor = await _k.CreateVendorAsync("Happy Vendor");

        var po = await _k.CreatePurchaseOrderAsync(vendor, wh, (item, 10m, Price));
        await _k.SubmitApproveAndSendPoAsync(po);
        var grn = await _k.ReceiveAllAsync(po, wh);
        var inv = await AutoInvoiceAsync(grn);
        (await InvAsync(inv)).Total.Should().Be(3000m, "10 received at 300");
        await Approve(inv);

        var grnLine = (await _k.Ok(_k.Get($"/api/grns/{grn}"), "read GRN")).A("lines").Single();

        // Two of the ten go back; the supplier's credit note is applied to the GRN's invoice by itself.
        var sro1 = await ReadySroAsync(vendor, 2m, Price, grn, po, grnLine.G("uuid"), grnLine.G("poLineUuid"), item);
        var cn = (await _k.Ok(CreateNoteAsync(Kind.Credit, sro1, 600m, invoice: null), "credit note from the return")).GetGuid();
        var cnRow = await NoteAsync(Kind.Credit, cn);
        cnRow.AppliedTo.Should().Be(inv, $"the return's GRN invoice is approved, so the credit is deducted from it — {cnRow}");
        cnRow.Status.Should().Be("APPLIED_TO_INVOICE");
        (await InvAsync(inv)).Total.Should().Be(2400m);
        (await SroStatusAsync(sro1)).Should().Be("RESOLVED_CREDIT");

        // One more goes back; this time the supplier is debited.
        var sro2 = await ReadySroAsync(vendor, 1m, Price, grn, po, grnLine.G("uuid"), grnLine.G("poLineUuid"), item);
        var dn = (await _k.Ok(CreateNoteAsync(Kind.Debit, sro2, 300m, invoice: null), "debit note from the return")).GetGuid();
        var dnRow = await NoteAsync(Kind.Debit, dn);
        dnRow.AppliedTo.Should().Be(inv, dnRow.ToString());
        (await InvAsync(inv)).Total.Should().Be(2100m, "3,000 − 600 credit − 300 debit");
        (await SroStatusAsync(sro2)).Should().Be("RESOLVED_DEBIT");

        var detail = await GetInvoiceAsync(inv);
        detail.A("creditNotes").Select(c => c.G("uuid")).Should().Equal(cn);
        detail.A("debitNotes").Select(d => d.G("uuid")).Should().Equal(dn);
        (detail.D("subtotal") + detail.D("taxAmount") - detail.D("totalAmount")).Should().Be(900m, "the deductions");

        // Ledger: the approval's debit and one credit per note, each mirrored once on the master ledger.
        (await LedgerByRefAsync(inv)).Should().Equal([("INVOICE_APPROVED", 3000m, 0m)]);
        (await LedgerByRefAsync(cn)).Should().Equal([("CREDIT_NOTE_APPROVED", 0m, 600m)]);
        (await LedgerByRefAsync(dn)).Should().Equal([("DEBIT_NOTE_APPROVED", 0m, 300m)]);
        (await MasterByRefAsync(cn)).Select(m => (m.Debit, m.Credit)).Should().Equal([(0m, 600m)], "mirrored once on the master ledger");
        (await MasterByRefAsync(dn)).Select(m => (m.Debit, m.Credit)).Should().Equal([(0m, 300m)], "mirrored once on the master ledger");
        (await LedgerBalanceAsync(vendor)).Should().Be(2100m);
        await AssertBooksAgreeAsync(vendor, "after the two notes");

        var outstanding = await _k.Ok(_k.Get($"/api/suppliers/{vendor.Uuid}/outstanding-invoices"), "outstanding invoices");
        outstanding.Items().Single(i => i.G("invoiceUuid") == inv).D("outstandingAmount").Should().Be(2100m);

        // Notes are deducted from it: it cannot be reversed.
        var reverse = await Reverse(inv, "notes applied");
        reverse.Status.Should().Be(HttpStatusCode.Conflict, reverse.ToString());
        reverse.Message.Should().Contain(cnRow.Number).And.Contain(dnRow.Number);
        (await InvAsync(inv)).Match.Should().Be("Approved");
        (await LedgerByRefAsync(inv)).Should().NotContain(e => e.Type == "INVOICE_REVERSED");

        // Paid: never more than what is left after the notes.
        var over = await PaySupplierAsync(vendor, inv, 2100.01m);
        over.Status.Should().Be(HttpStatusCode.BadRequest, $"only 2,100 is owed — {over}");
        await PayAndPostAsync(vendor, inv, 2100m);
        var paid = await InvAsync(inv);
        (paid.Paid, paid.Payment).Should().Be((2100m, "FULLY_PAID"));
        (await LedgerBalanceAsync(vendor)).Should().Be(0m);
        await AssertBooksAgreeAsync(vendor, "after the payment");

        // A return with no GRN: the credit has no invoice, so it is carried forward and applied to the next one.
        var sro3 = await ReadySroAsync(vendor, 1m, Price);
        var cf = (await _k.Ok(CreateNoteAsync(Kind.Credit, sro3, 300m, invoice: null), "carried-forward credit")).GetGuid();
        var cfRow = await NoteAsync(Kind.Credit, cf);
        (cfRow.Status, cfRow.AppliedTo, cfRow.Carried).Should().Be(("CARRIED_FORWARD", (Guid?)null, (decimal?)300m), cfRow.ToString());
        (await LedgerBalanceAsync(vendor)).Should().Be(-300m, "the supplier owes us the credit until it is used");
        await AssertBooksAgreeAsync(vendor, "with a credit carried forward");

        var inv2 = await ApprovedDirectAsync(vendor, 1000m, "NEXT");
        await _k.Ok(ApplyAsync(Kind.Credit, cf, inv2), "apply the carried-forward credit");
        cfRow = await NoteAsync(Kind.Credit, cf);
        (cfRow.Status, cfRow.AppliedTo).Should().Be(("APPLIED", (Guid?)inv2));
        (await InvAsync(inv2)).Total.Should().Be(700m);
        (await LedgerByRefAsync(cf)).Should().ContainSingle("applying a carried-forward note posts nothing more — its credit was posted when it was raised");
        (await LedgerBalanceAsync(vendor)).Should().Be(700m);
        await AssertBooksAgreeAsync(vendor, "after applying the carried-forward credit");

        var again = await ApplyAsync(Kind.Credit, cf, inv2);
        ((int)again.Status).Should().Be(422, $"a note is applied once — {again}");
        (await Reverse(inv2, "note applied")).Status.Should().Be(HttpStatusCode.Conflict);
        (await InvAsync(inv2)).Total.Should().Be(700m);

        await AssertLedgerChainAsync(vendor);    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Rules: approved only, never below paid/allocated, same supplier (one at a time)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Notes_go_only_to_approved_invoices_of_the_same_supplier_and_never_below_what_is_paid_or_allocated()
    {
        var (wh, item) = await SetupAsync();
        var vendor = await _k.CreateVendorAsync("Rules Vendor");
        var other  = await _k.CreateVendorAsync("Other Vendor");

        // ── E: a Pending invoice is not a payable — a note naming it is carried forward ──
        var pending = await CreateInvoiceAsync(Direct(vendor, 1000m, "PEND"));
        var cnPending = (await _k.Ok(CreateNoteAsync(Kind.Credit, await ReadySroAsync(vendor, 1m, Price), 300m, pending), "credit naming a Pending invoice")).GetGuid();
        var row = await NoteAsync(Kind.Credit, cnPending);
        (row.Status, row.AppliedTo).Should().Be(("CARRIED_FORWARD", (Guid?)null), $"a Pending invoice is not a payable — {row}");
        (await InvAsync(pending)).Total.Should().Be(1000m);
        await AssertBooksAgreeAsync(vendor, "credit naming a Pending invoice");

        var toPending = await ApplyAsync(Kind.Credit, cnPending, pending);
        ((int)toPending.Status).Should().Be(422, $"applying to a Pending invoice — {toPending}");

        await Approve(pending);
        await AssertBooksAgreeAsync(vendor, "after approving the invoice the credit was carried past");
        await _k.Ok(ApplyAsync(Kind.Credit, cnPending, pending), "apply once it is approved");
        (await InvAsync(pending)).Total.Should().Be(700m);
        await AssertBooksAgreeAsync(vendor, "credit applied after approval");

        // ── E: the GRN's own (Matched, not approved) invoice — the return's note is carried forward ──
        var po = await _k.CreatePurchaseOrderAsync(vendor, wh, (item, 4m, Price));
        await _k.SubmitApproveAndSendPoAsync(po);
        var grn = await _k.ReceiveAllAsync(po, wh);
        var matched = await AutoInvoiceAsync(grn);
        (await InvAsync(matched)).Match.Should().Be("Matched");
        var grnLine = (await _k.Ok(_k.Get($"/api/grns/{grn}"), "read GRN")).A("lines").Single();
        var sroM = await ReadySroAsync(vendor, 1m, Price, grn, po, grnLine.G("uuid"), grnLine.G("poLineUuid"), item);
        var dnMatched = (await _k.Ok(CreateNoteAsync(Kind.Debit, sroM, 300m, invoice: null), "debit from a return on a Matched invoice")).GetGuid();
        row = await NoteAsync(Kind.Debit, dnMatched);
        (row.Status, row.AppliedTo).Should().Be(("CARRIED_FORWARD", (Guid?)null), $"the GRN's invoice is Matched, not approved — {row}");
        (await InvAsync(matched)).Total.Should().Be(1200m);
        await Approve(matched);
        await AssertBooksAgreeAsync(vendor, "Matched invoice approved after a carried-forward debit");

        // ── A Rejected invoice owes nothing ──
        var rejected = await CreateInvoiceAsync(Direct(vendor, 1000m, "REJ"));
        await _k.Ok(_k.Post($"{Invoices}/{rejected}/reject", new { Reason = "not ours" }), "reject");
        var dnRej = (await _k.Ok(CreateNoteAsync(Kind.Debit, await ReadySroAsync(vendor, 1m, 200m), 200m, rejected), "debit naming a Rejected invoice")).GetGuid();
        row = await NoteAsync(Kind.Debit, dnRej);
        (row.Status, row.AppliedTo).Should().Be(("CARRIED_FORWARD", (Guid?)null), $"a Rejected invoice owes nothing — {row}");
        (await InvAsync(rejected)).Total.Should().Be(1000m);
        var toRejected = await ApplyAsync(Kind.Debit, dnRej, rejected);
        ((int)toRejected.Status).Should().Be(422, toRejected.ToString());

        // ── Another supplier's invoice ──
        var otherInv = await ApprovedDirectAsync(other, 1000m, "OTH");
        var toOther = await ApplyAsync(Kind.Debit, dnRej, otherInv);
        toOther.Status.Should().Be(HttpStatusCode.BadRequest, $"{vendor.Name}'s debit note cannot reduce {other.Name}'s invoice — {toOther}");
        (await InvAsync(otherInv)).Total.Should().Be(1000m);
        (await NoteAsync(Kind.Debit, dnRej)).Status.Should().Be("CARRIED_FORWARD");
        var cnOther = await CreateNoteAsync(Kind.Credit, await ReadySroAsync(vendor, 1m, 100m), 100m, otherInv);
        if (cnOther.Status == HttpStatusCode.OK)
        {
            var r = await NoteAsync(Kind.Credit, cnOther.Result.GetGuid());
            r.AppliedTo.Should().NotBe(otherInv, $"a note raised for {vendor.Name} naming {other.Name}'s invoice is not deducted from it — {r}");
        }
        else cnOther.Status.Should().Be(HttpStatusCode.BadRequest, cnOther.ToString());
        (await InvAsync(otherInv)).Total.Should().Be(1000m);
        await AssertBooksAgreeAsync(other, "the other supplier");

        // ── A reversed invoice ──
        var reversed = await ApprovedDirectAsync(vendor, 1000m, "REV");
        await _k.Ok(Reverse(reversed, "rules"), "reverse");
        var toReversed = await ApplyAsync(Kind.Debit, dnRej, reversed);
        ((int)toReversed.Status).Should().Be(422, toReversed.ToString());
        var cnRev = (await _k.Ok(CreateNoteAsync(Kind.Credit, await ReadySroAsync(vendor, 1m, 100m), 100m, reversed), "credit naming a reversed invoice")).GetGuid();
        (await NoteAsync(Kind.Credit, cnRev)).Status.Should().Be("CARRIED_FORWARD");
        (await InvAsync(reversed)).Total.Should().Be(1000m);

        // ── Fully paid ──
        var full = await ApprovedDirectAsync(vendor, 1000m, "FULL");
        await PayAndPostAsync(vendor, full, 1000m);
        var toFull = await ApplyAsync(Kind.Debit, dnRej, full);
        ((int)toFull.Status).Should().Be(422, toFull.ToString());
        (await InvAsync(full)).Total.Should().Be(1000m);

        // ── Partly paid: 800 of 1,000 posted, so 200 is all a note may take ──
        var part = await ApprovedDirectAsync(vendor, 1000m, "PART");
        await PayAndPostAsync(vendor, part, 800m);
        var cnBig = (await _k.Ok(CreateNoteAsync(Kind.Credit, await ReadySroAsync(vendor, 1m, Price), 300m, part), "credit larger than what is left")).GetGuid();
        row = await NoteAsync(Kind.Credit, cnBig);
        row.Status.Should().Be("CARRIED_FORWARD", $"300 would take the invoice below the 800 already paid — {row}");
        var partNow = await InvAsync(part);
        partNow.Total.Should().Be(1000m);
        var bigApply = await ApplyAsync(Kind.Credit, cnBig, part);
        ((int)bigApply.Status).Should().Be(422, $"300 against 200 left — {bigApply}");
        await _k.Ok(ApplyAsync(Kind.Debit, dnRej, part), "a 200 debit fits exactly");
        partNow = await InvAsync(part);
        (partNow.Total, partNow.Paid).Should().Be((800m, 800m));
        partNow.Payment.Should().Be("FULLY_PAID", $"nothing is left to pay once the note took the rest — {partNow}");
        (await OutstandingAsync(vendor)).Should().NotContainKey(part);

        // ── A draft payment allocating everything: no room for a note until it is cancelled ──
        var alloc = await ApprovedDirectAsync(vendor, 1000m, "ALLOC");
        var draft = (await _k.Ok(PaySupplierAsync(vendor, alloc, 1000m), "draft payment of everything")).GetGuid();
        var dnAlloc = (await _k.Ok(CreateNoteAsync(Kind.Debit, await ReadySroAsync(vendor, 1m, Price), 300m, alloc), "debit naming a fully allocated invoice")).GetGuid();
        row = await NoteAsync(Kind.Debit, dnAlloc);
        row.Status.Should().Be("CARRIED_FORWARD", $"the draft payment allocates all 1,000 — {row}");
        (await InvAsync(alloc)).Total.Should().Be(1000m);
        var allocApply = await ApplyAsync(Kind.Debit, dnAlloc, alloc);
        ((int)allocApply.Status).Should().Be(422, allocApply.ToString());
        await _k.Ok(_k.Post($"/api/supplier-payments/{draft}/cancel"), "cancel the draft");
        await _k.Ok(ApplyAsync(Kind.Debit, dnAlloc, alloc), "room again once the draft is cancelled");
        (await InvAsync(alloc)).Total.Should().Be(700m);
        (await PaySupplierAsync(vendor, alloc, 700.01m)).Status.Should().Be(HttpStatusCode.BadRequest);
        await PayAndPostAsync(vendor, alloc, 700m);

        foreach (var inv in new[] { pending, matched, rejected, reversed, full, part, alloc })
            await AssertInvoiceConsistentAsync(inv, "rules");
        await AssertBooksAgreeAsync(vendor, "end of the rules");
        await AssertLedgerChainAsync(vendor);    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 2. Race: a note applied while the invoice is reversed
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_note_racing_a_reversal_of_the_same_invoice_has_exactly_one_winner()
    {
        await SetupAsync();
        var vendor = await _k.CreateVendorAsync("Reversal Race Vendor");
        var outcomes = new List<string>();

        for (var round = 1; round <= 12; round++)
        {
            var kind  = round % 2 == 0 ? Kind.Debit : Kind.Credit;
            var apply = (round - 1) / 2 % 2 == 1; // rounds 3,4,7,8,11,12 apply a carried-forward note
            var label = $"round {round} ({kind} {(apply ? "apply" : "create")})";

            var inv = await ApprovedDirectAsync(vendor, 1000m, $"RR{round}");
            var sro = await ReadySroAsync(vendor, 1m, 200m);
            Guid? note = null;
            if (apply)
            {
                note = (await _k.Ok(CreateNoteAsync(kind, sro, 200m, invoice: null), "carried-forward note")).GetGuid();
                (await NoteAsync(kind, note.Value)).Status.Should().Be("CARRIED_FORWARD");
            }

            var noteCall = apply ? ApplyAsync(kind, note!.Value, inv) : CreateNoteAsync(kind, sro, 200m, inv);
            var revCall  = Reverse(inv, $"race {round}");
            await Task.WhenAll(noteCall, revCall);
            var (n, r) = (await noteCall, await revCall);

            n.Status.Should().NotBe(HttpStatusCode.InternalServerError, $"{label}: note — {n}");
            r.Status.Should().NotBe(HttpStatusCode.InternalServerError, $"{label}: reversal — {r}");
            if (!apply && n.Status == HttpStatusCode.OK) note = n.Result.GetGuid();
            note.Should().NotBeNull($"{label}: the note is raised whoever wins (carried forward if the reversal won) — {n}");

            var noteRow   = await NoteAsync(kind, note!.Value);
            var invRow    = await InvAsync(inv);
            var applied   = await AppliedNotesAsync(inv);
            var reversed  = r.Status == HttpStatusCode.OK;
            var noteWon   = noteRow.AppliedTo == inv;
            outcomes.Add($"{round}:{kind}/{(apply ? "apply" : "create")}={(noteWon ? "note" : "")}{(reversed ? "reversal" : "")} [{(int)n.Status}/{(int)r.Status}]");

            (noteWon ^ reversed).Should().BeTrue($"{label}: exactly one wins — note {n} ({noteRow}) / reversal {r} / invoice {invRow}");

            if (reversed)
            {
                invRow.Match.Should().Be("Reversed");
                invRow.Total.Should().Be(1000m, $"{label}: nothing was deducted from the reversed invoice");
                applied.Should().Be(0m, $"{label}: no note stays applied to a reversed invoice");
                noteRow.Status.Should().Be("CARRIED_FORWARD", $"{label}: the note is still there to use — {noteRow}");
                if (apply) ((int)n.Status).Should().Be(422, $"{label}: the apply lost to the reversal — {n}");
                (await LedgerByRefAsync(inv)).Should().Equal([("INVOICE_APPROVED", 1000m, 0m), ("INVOICE_REVERSED", 0m, 1000m)], label);
            }
            else
            {
                r.Status.Should().Be(HttpStatusCode.Conflict, $"{label}: the reversal lost to the note — {r}");
                invRow.Match.Should().Be("Approved");
                invRow.Total.Should().Be(800m, label);
                (await LedgerByRefAsync(inv)).Should().Equal([("INVOICE_APPROVED", 1000m, 0m)], label);
            }

            (await LedgerByRefAsync(note.Value)).Should().ContainSingle($"{label}: one ledger credit for the note, posted when it was raised");
            (await NotesForSroAsync(sro)).Should().Be(1, $"{label}: the return was resolved once");
            await AssertInvoiceConsistentAsync(inv, label);
            await AssertBooksAgreeAsync(vendor, label);
        }

        Console.WriteLine($"PROBE note-vs-reversal outcomes: {string.Join(", ", outcomes)}");
        await AssertLedgerChainAsync(vendor);    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 3. Race: a note applied while a supplier payment is created / approved / posted
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_note_racing_a_supplier_payment_never_leaves_the_invoice_overpaid_or_over_allocated()
    {
        await SetupAsync();
        var vendor = await _k.CreateVendorAsync("Payment Race Vendor");
        var outcomes = new List<string>();
        string[] stages = ["create", "approve", "post"];

        for (var round = 1; round <= 12; round++)
        {
            // 1,000: the payment and the 300 note cannot both fit, exactly one wins. 700: both always fit, both win.
            var stage  = stages[(round - 1) % 3];
            var kind   = round % 2 == 0 ? Kind.Debit : Kind.Credit;
            var apply  = (round - 1) / 3 % 2 == 1;
            var amount = round <= 6 ? 1000m : 700m;
            var label  = $"round {round} ({kind} {(apply ? "apply" : "create")} vs payment {amount} {stage})";

            var inv = await ApprovedDirectAsync(vendor, 1000m, $"PR{round}");
            var sro = await ReadySroAsync(vendor, 1m, Price);
            Guid? note = null;
            if (apply)
                note = (await _k.Ok(CreateNoteAsync(kind, sro, 300m, invoice: null), "carried-forward note")).GetGuid();

            Guid? payment = null;
            if (stage != "create")
            {
                payment = (await _k.Ok(PaySupplierAsync(vendor, inv, amount), $"{label}: draft payment")).GetGuid();
                if (stage == "post") await _k.Ok(_k.Post($"/api/supplier-payments/{payment}/approve"), $"{label}: approve");
            }

            Task<Api> payCall = stage switch
            {
                "create"  => PaySupplierAsync(vendor, inv, amount),
                "approve" => _k.Post($"/api/supplier-payments/{payment}/approve"),
                _         => _k.Post($"/api/supplier-payments/{payment}/post")
            };
            var noteCall = apply ? ApplyAsync(kind, note!.Value, inv) : CreateNoteAsync(kind, sro, 300m, inv);
            await Task.WhenAll(payCall, noteCall);
            var (p, n) = (await payCall, await noteCall);

            p.Status.Should().NotBe(HttpStatusCode.InternalServerError, $"{label}: payment — {p}");
            n.Status.Should().NotBe(HttpStatusCode.InternalServerError, $"{label}: note — {n}");
            if (stage == "create" && p.Status == HttpStatusCode.OK) payment = p.Result.GetGuid();
            if (!apply && n.Status == HttpStatusCode.OK) note = n.Result.GetGuid();

            // Finish the payment the way a person would, where it still can be.
            if (payment is { } pid)
            {
                if (stage == "create") await _k.Post($"/api/supplier-payments/{pid}/approve");
                if (stage != "post")
                {
                    var post = await _k.Post($"/api/supplier-payments/{pid}/post");
                    post.Status.Should().NotBe(HttpStatusCode.InternalServerError, $"{label}: posting afterwards — {post}");
                }
            }

            var invRow   = await InvAsync(inv);
            var noteRow  = note is { } nid ? await NoteAsync(kind, nid) : null;
            var live     = await LiveAllocationsAsync(inv);
            var noteWon  = noteRow?.AppliedTo == inv;
            var payState = payment is { } ps ? await PaymentStatusAsync(ps) : "none";
            outcomes.Add($"{round}:{stage}/{kind}/{(apply ? "apply" : "create")}/{amount}: note={(noteWon ? "applied" : noteRow?.Status ?? "none")} pay={payState} total={invRow.Total} paid={invRow.Paid} [{(int)n.Status}/{(int)p.Status}]");

            invRow.Paid.Should().BeLessThanOrEqualTo(invRow.Total, $"{label}: never paid beyond the total — {invRow}, note {noteRow}, payment {payState}");
            invRow.Payment.Should().NotBe("OVERPAID", label);
            live.Should().BeLessThanOrEqualTo(invRow.Total, $"{label}: never allocated beyond the total — allocations {live}, {invRow}, note {noteRow}");
            (invRow.Total - invRow.Paid).Should().BeGreaterThanOrEqualTo(0m, $"{label}: outstanding is never negative");
            if (invRow.Paid == invRow.Total && invRow.Total > 0m) invRow.Payment.Should().Be("FULLY_PAID", $"{label}: {invRow}");
            if (noteRow is not null && !noteWon) noteRow.Status.Should().Be("CARRIED_FORWARD", $"{label}: a note that did not fit is still there to use");
            if (noteWon) invRow.Total.Should().Be(700m, label);
            if (amount == 1000m)
                (noteWon ^ invRow.Paid > 0m).Should().BeTrue($"{label}: a 1,000 payment and a 300 note cannot both fit — exactly one wins; note {n} ({noteRow}), payment {p} ({payState}), {invRow}");
            else
            {
                noteWon.Should().BeTrue($"{label}: 700 paid leaves room for the 300 note in either order — {n} ({noteRow}), {invRow}");
                invRow.Paid.Should().Be(700m, $"{label}: and the 700 payment fits in either order — {p} ({payState}), {invRow}");
            }

            await AssertInvoiceConsistentAsync(inv, label);
            await AssertBooksAgreeAsync(vendor, label);
        }

        Console.WriteLine($"PROBE note-vs-payment outcomes: {string.Join(" | ", outcomes)}");
        await AssertLedgerChainAsync(vendor);    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Two notes at once
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Two_notes_applied_to_one_invoice_at_once_both_count_and_one_return_is_resolved_once()
    {
        await SetupAsync();
        var vendor = await _k.CreateVendorAsync("Two Notes Vendor");

        for (var round = 1; round <= 3; round++)
        {
            // Two different returns, two notes against one invoice at the same moment: both deducted.
            var (k1, k2) = round switch { 1 => (Kind.Credit, Kind.Debit), 2 => (Kind.Credit, Kind.Credit), _ => (Kind.Debit, Kind.Debit) };
            var label = $"round {round} ({k1}+{k2} create)";
            var inv = await ApprovedDirectAsync(vendor, 1000m, $"TWO{round}");
            var s1 = await ReadySroAsync(vendor, 1m, 200m);
            var s2 = await ReadySroAsync(vendor, 1m, Price);
            var both = await Task.WhenAll(CreateNoteAsync(k1, s1, 200m, inv), CreateNoteAsync(k2, s2, 300m, inv));
            both.Should().OnlyContain(r => r.Status == HttpStatusCode.OK, $"{label}: {string.Join(" | ", both.Select(b => b.ToString()))}");
            (await InvAsync(inv)).Total.Should().Be(500m, $"{label}: neither deduction overwrote the other");
            var rows = new[] { await NoteAsync(k1, both[0].Result.GetGuid()), await NoteAsync(k2, both[1].Result.GetGuid()) };
            rows.Should().OnlyContain(x => x.AppliedTo == inv, label);
            rows.Select(x => x.Number).Should().OnlyHaveUniqueItems(label);
            await AssertInvoiceConsistentAsync(inv, label);
            await AssertBooksAgreeAsync(vendor, label);

            // Two carried-forward notes applied to one invoice at once: both deducted.
            label = $"round {round} ({k1}+{k2} apply)";
            var inv2 = await ApprovedDirectAsync(vendor, 1000m, $"TWOA{round}");
            var c1 = (await _k.Ok(CreateNoteAsync(k1, await ReadySroAsync(vendor, 1m, 200m), 200m, null), "carried 1")).GetGuid();
            var c2 = (await _k.Ok(CreateNoteAsync(k2, await ReadySroAsync(vendor, 1m, Price), 300m, null), "carried 2")).GetGuid();
            var applies = await Task.WhenAll(ApplyAsync(k1, c1, inv2), ApplyAsync(k2, c2, inv2));
            applies.Should().OnlyContain(r => r.Status == HttpStatusCode.OK, $"{label}: {string.Join(" | ", applies.Select(b => b.ToString()))}");
            (await InvAsync(inv2)).Total.Should().Be(500m, $"{label}: neither deduction overwrote the other");
            await AssertInvoiceConsistentAsync(inv2, label);

            // One carried-forward note applied to two invoices at once: exactly one gets it.
            label = $"round {round} (one {k1} to two invoices)";
            var a = await ApprovedDirectAsync(vendor, 1000m, $"ONEA{round}");
            var b = await ApprovedDirectAsync(vendor, 1000m, $"ONEB{round}");
            var c3 = (await _k.Ok(CreateNoteAsync(k1, await ReadySroAsync(vendor, 1m, 200m), 200m, null), "carried 3")).GetGuid();
            var split = await Task.WhenAll(ApplyAsync(k1, c3, a), ApplyAsync(k1, c3, b));
            split.Count(r => r.Status == HttpStatusCode.OK).Should().Be(1, $"{label}: {string.Join(" | ", split.Select(x => x.ToString()))}");
            ((await InvAsync(a)).Total + (await InvAsync(b)).Total).Should().Be(1800m, $"{label}: the 200 is deducted once");
            await AssertInvoiceConsistentAsync(a, label);
            await AssertInvoiceConsistentAsync(b, label);

            // One return resolved twice at once (a credit and a debit for the same goods): exactly one note.
            label = $"round {round} (one return, two notes)";
            var inv3 = await ApprovedDirectAsync(vendor, 1000m, $"ONE{round}");
            var s3 = await ReadySroAsync(vendor, 1m, 200m);
            var twice = await Task.WhenAll(CreateNoteAsync(Kind.Credit, s3, 200m, inv3), CreateNoteAsync(Kind.Debit, s3, 200m, inv3));
            twice.Should().NotContain(r => r.Status == HttpStatusCode.InternalServerError, string.Join(" | ", twice.Select(x => x.ToString())));
            (await NotesForSroAsync(s3)).Should().Be(1, $"{label}: one return, one financial resolution — {string.Join(" | ", twice.Select(x => x.ToString()))}");
            (await InvAsync(inv3)).Total.Should().Be(800m, $"{label}: the return's 200 is deducted once");
            await AssertInvoiceConsistentAsync(inv3, label);

            await AssertBooksAgreeAsync(vendor, $"round {round} end");
        }

        await AssertLedgerChainAsync(vendor);    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Legacy single-invoice payments (/api/finance/payments) and notes
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Legacy_single_invoice_payments_and_notes_never_overpay_and_leave_a_true_payment_status()
    {
        await SetupAsync();

        // 700 paid through the legacy flow, then a 300 note: exactly what was left, so the invoice is settled.
        var settledVendor = await _k.CreateVendorAsync("Legacy Settled Vendor");
        var a = await ApprovedDirectAsync(settledVendor, 1000m, "LGA");
        await _k.Ok(PayLegacyAsync(a, 700m), "legacy payment of 700");
        var cnA = (await _k.Ok(CreateNoteAsync(Kind.Credit, await ReadySroAsync(settledVendor, 1m, Price), 300m, a), "credit after a legacy payment")).GetGuid();
        (await NoteAsync(Kind.Credit, cnA)).AppliedTo.Should().Be(a, "300 is exactly what the legacy payment left");
        var invA = await InvAsync(a);
        invA.Total.Should().Be(700m);
        (await LegacyPaidAsync(a)).Should().Be(700m);

        // Every finding below is collected, so one run reports them all.
        using var scope = new FluentAssertions.Execution.AssertionScope();

        invA.Payment.Should().BeOneOf(["Paid", "FULLY_PAID"], $"700 is paid against a total of 700 — {invA}");
        var seen = await PayablesViewsAsync(settledVendor, a, (await GetInvoiceAsync(a)).S("invoiceNumber")!);
        seen.Where(v => v.Value).Select(v => v.Key).Should().BeEmpty($"nothing is owed on it — {invA}");

        var vendor = await _k.CreateVendorAsync("Legacy Vendor");

        // A 300 note, then a legacy payment of the original 1,000: 300 more than is owed.
        var b = await ApprovedDirectAsync(vendor, 1000m, "LGB");
        await _k.Ok(CreateNoteAsync(Kind.Debit, await ReadySroAsync(vendor, 1m, Price), 300m, b), "debit note");
        (await InvAsync(b)).Total.Should().Be(700m);
        var over = await PayLegacyAsync(b, 1000m);
        ((int)over.Status).Should().BeOneOf([400, 422], $"1,000 against 700 owed — {over}");
        (await LegacyPaidAsync(b)).Should().BeLessThanOrEqualTo((await InvAsync(b)).Total, "never paid beyond the total");

        // Fully paid through supplier payments: a legacy payment on top pays it twice.
        var c = await ApprovedDirectAsync(vendor, 1000m, "LGC");
        await PayAndPostAsync(vendor, c, 1000m);
        var twice = await PayLegacyAsync(c, 1000m);
        ((int)twice.Status).Should().BeOneOf([400, 422], $"the invoice is FULLY_PAID already — {twice}");

        // Legacy payment of everything racing a 300 note: exactly one fits.
        for (var round = 1; round <= 4; round++)
        {
            var kind = round % 2 == 0 ? Kind.Debit : Kind.Credit;
            var label = $"round {round} (legacy 1,000 vs {kind} 300)";
            var d = await ApprovedDirectAsync(vendor, 1000m, $"LGR{round}");
            var sro = await ReadySroAsync(vendor, 1m, Price);
            var pay = PayLegacyAsync(d, 1000m);
            var note = CreateNoteAsync(kind, sro, 300m, d);
            await Task.WhenAll(pay, note);
            var (p, n) = (await pay, await note);
            p.Status.Should().NotBe(HttpStatusCode.InternalServerError, $"{label}: {p}");
            n.Status.Should().NotBe(HttpStatusCode.InternalServerError, $"{label}: {n}");

            var inv = await InvAsync(d);
            var legacy = await LegacyPaidAsync(d);
            var applied = await AppliedNotesAsync(d);
            Console.WriteLine($"PROBE legacy race {label}: legacy={legacy} applied={applied} {inv} [{(int)p.Status}/{(int)n.Status}]");
            (legacy + applied).Should().BeLessThanOrEqualTo(inv.Subtotal + inv.Tax, $"{label}: paid plus deducted never exceeds the invoice — {inv}, pay {p}, note {n}");
            legacy.Should().BeLessThanOrEqualTo(inv.Total, $"{label}: {inv}");
            await AssertInvoiceConsistentAsync(d, label);
        }

        await AssertBooksAgreeAsync(vendor, "legacy");    }

    // ── setup helpers ────────────────────────────────────────────────────────────

    private static DateTime Today => DateTime.UtcNow.Date;

    private async Task<(Warehouse Wh, Product Item)> SetupAsync()
    {
        var pkr = await _k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        await _k.SetBaseCurrencyAsync(pkr);
        await _k.CreateApproverPlaceholdersAsync();
        var wh = await _k.CreateWarehouseAsync();
        var item = await _k.CreateProductAsync("Returnable Item", purchasePrice: Price, sellingPrice: 400m);
        return (wh, item);
    }

    /// <summary>The invoice GRN approval raised in the background (Hangfire).</summary>
    private async Task<Guid> AutoInvoiceAsync(Guid grn)
    {
        var rows = await SapKit.WaitForAsync(
            () => _f.QueryAsync("SELECT UUID FROM finance.invoices WHERE GrnUuid = @g AND IsDelete = 0", ("@g", grn)),
            r => r.Count > 0, $"the invoice auto-created from GRN {grn}");
        return (Guid)rows.Single()["UUID"]!;
    }

    private object Direct(Partner vendor, decimal subtotal, string tag) => new
    {
        SupplierInvoiceNo = $"{_k.Marker}-{tag}", SupplierId = vendor.Uuid,
        InvoiceDate = Today, ReceivedDate = Today, DueDate = Today.AddDays(30), Currency = "PKR", Subtotal = subtotal, TaxAmount = 0m
    };

    private async Task<Guid> CreateInvoiceAsync(object body) => (await _k.Ok(_k.Post(Invoices, body), "create supplier invoice")).GetGuid();

    private async Task<Guid> ApprovedDirectAsync(Partner vendor, decimal subtotal, string tag)
    {
        var inv = await CreateInvoiceAsync(Direct(vendor, subtotal, tag));
        await Approve(inv);
        return inv;
    }

    private Task<JsonElement> GetInvoiceAsync(Guid uuid) => _k.Ok(_k.Get($"{Invoices}/{uuid}"), "read supplier invoice");
    private async Task Approve(Guid uuid) => await _k.Ok(_k.Post($"{Invoices}/{uuid}/approve", new { Notes = "ok" }), "approve supplier invoice");
    private Task<Api> Reverse(Guid uuid, string reason) => _k.Post($"{Invoices}/{uuid}/reverse", new { Reason = reason });

    /// <summary>
    /// A supplier return order taken to SUPPLIER_RECEIVED (create → approve → dispatch → confirm receipt), the state
    /// a note is raised from. With a GRN line it returns received goods; without, it is a manual return with no
    /// warehouse, so no stock moves and no invoice is found from it.
    /// </summary>
    private async Task<Guid> ReadySroAsync(
        Partner vendor, decimal qty, decimal unitCost,
        Guid? grn = null, Guid? po = null, Guid? grnLine = null, Guid? poLine = null, Product? item = null)
    {
        var sro = (await _k.Ok(_k.Post("/api/sros", new
        {
            SupplierId = vendor.Uuid, SupplierName = vendor.Name, SroType = "POST_RECEIPT_DEFECT",
            OriginalGrnId = grn, OriginalPoId = po, WarehouseUuid = (Guid?)null,
            ReturnReason = "DAMAGED", Notes = "sn e2e",
            Lines = new[]
            {
                new
                {
                    GrnLineUuid = grnLine, PoLineUuid = poLine, ProductUuid = item?.ProductUuid,
                    ItemDescription = item?.Name ?? _k.Next("Returned goods"), UnitOfMeasure = "PCS",
                    QtyToReturn = qty, ReturnReason = "DAMAGED", Condition = "DAMAGED", UnitCost = (decimal?)unitCost
                }
            }
        }), "create SRO")).GetGuid();

        await _k.Ok(_k.Post($"/api/sros/{sro}/approve", new { Notes = "ok" }), "approve SRO");
        await _k.Ok(_k.Post($"/api/sros/{sro}/dispatch", new
        {
            RmaNumber = $"RMA-{Guid.NewGuid():N}"[..12], DispatchDate = DateTime.UtcNow, DispatchCarrier = "E2E Courier", DispatchTrackingRef = "TRK"
        }), "dispatch SRO");
        await _k.Ok(_k.Post($"/api/sros/{sro}/confirm-receipt", new { Notes = "received" }), "supplier received the return");
        (await SroStatusAsync(sro)).Should().Be("SUPPLIER_RECEIVED");
        return sro;
    }

    private async Task<string> SroStatusAsync(Guid sro) => (await _k.Ok(_k.Get($"/api/sros/{sro}"), "read SRO")).S("status")!;

    private Task<Api> CreateNoteAsync(Kind kind, Guid sro, decimal amount, Guid? invoice) => kind == Kind.Credit
        ? _k.Post("/api/credit-notes", new
        {
            SroId = sro, SupplierCreditNoteNo = $"SCN-{Guid.NewGuid():N}"[..16], CreditDate = Today, CreditAmount = amount,
            InvoiceUuid = invoice, Notes = "sn e2e"
        })
        : _k.Post("/api/debit-notes", new
        {
            SroId = sro, DebitReason = "DAMAGED_GOODS", DebitReasonDetail = "sn e2e", DebitAmount = amount, InvoiceUuid = invoice, Notes = "sn e2e"
        });

    private Task<Api> ApplyAsync(Kind kind, Guid note, Guid invoice) =>
        _k.Post($"/api/{(kind == Kind.Credit ? "credit" : "debit")}-notes/{note}/apply", new { InvoiceUuid = invoice });

    private static object Payment(Partner vendor, Guid invoice, decimal amount) => new
    {
        SupplierId = vendor.Uuid, SupplierName = vendor.Name, PaymentDate = Today, PaymentMethod = "CASH", TotalAmount = amount,
        Lines = new[] { new { InvoiceUuid = invoice, AllocatedAmount = amount } }
    };

    private Task<Api> PaySupplierAsync(Partner vendor, Guid invoice, decimal amount) =>
        _k.Post("/api/supplier-payments", Payment(vendor, invoice, amount));

    private Task<Api> PayLegacyAsync(Guid invoice, decimal amount) =>
        _k.Post("/api/finance/payments", new { InvoiceUuid = invoice, PaymentDate = Today, AmountPaid = amount, PaymentMethod = "CASH" });

    private async Task<decimal> LegacyPaidAsync(Guid invoice) =>
        (decimal)(await _f.QueryAsync(
            "SELECT ISNULL(SUM(AmountPaid), 0) AS N FROM finance.payments WHERE InvoiceUuid = @i AND IsDelete = 0 AND Status <> 'Reversed'",
            ("@i", invoice))).Single()["N"]!;

    /// <summary>Whether the invoice shows as owed in each place payables are listed.</summary>
    private async Task<Dictionary<string, bool>> PayablesViewsAsync(Partner vendor, Guid invoice, string number)
    {
        var id = invoice.ToString();
        var outstanding = await _k.Ok(_k.Get($"/api/suppliers/{vendor.Uuid}/outstanding-invoices"), "outstanding invoices");
        var aging       = await _k.Ok(_k.Get($"/api/suppliers/{vendor.Uuid}/aging"), "supplier aging");
        var crossAging  = await _k.Ok(_k.Get("/api/reports/supplier-aging"), "cross-supplier aging");
        var payables    = await _k.Ok(_k.Get("/api/reports/outstanding-payables?page=1&pageSize=100"), "outstanding payables");
        var reportAging = await _k.Ok(_k.Get("/api/reports/invoice-aging"), "Reports invoice aging");

        return new Dictionary<string, bool>
        {
            ["outstanding-invoices"]  = outstanding.Items().Any(i => i.G("invoiceUuid") == invoice),
            ["supplier aging"]        = aging.GetRawText().Contains(id, StringComparison.OrdinalIgnoreCase),
            ["cross-supplier aging"]  = crossAging.GetRawText().Contains(vendor.Uuid.ToString(), StringComparison.OrdinalIgnoreCase),
            ["outstanding payables"]  = payables.GetRawText().Contains(id, StringComparison.OrdinalIgnoreCase),
            ["Reports invoice aging"] = reportAging.A("items").Any(i => i.S("invoiceNumber") == number)
        };
    }

    private async Task PayAndPostAsync(Partner vendor, Guid invoice, decimal amount)
    {
        var p = (await _k.Ok(PaySupplierAsync(vendor, invoice, amount), $"pay {amount}")).GetGuid();
        await _k.Ok(_k.Post($"/api/supplier-payments/{p}/approve"), "approve payment");
        await _k.Ok(_k.Post($"/api/supplier-payments/{p}/post"), "post payment");
    }

    // ── read-side helpers (SQL) ──────────────────────────────────────────────────

    private sealed record InvRow(decimal Subtotal, decimal Tax, decimal Total, decimal Paid, string Match, string Payment)
    {
        public override string ToString() => $"invoice[subtotal {Subtotal}, tax {Tax}, total {Total}, paid {Paid}, {Match}, {Payment}]";
    }

    private sealed record NoteRow(string Number, string Status, Guid? AppliedTo, decimal Amount, decimal? Carried)
    {
        public override string ToString() => $"note[{Number} {Status} amount {Amount} carried {Carried?.ToString() ?? "-"} applied to {AppliedTo?.ToString() ?? "-"}]";
    }

    private async Task<InvRow> InvAsync(Guid uuid)
    {
        var r = (await _f.QueryAsync(
            "SELECT Subtotal, TaxAmount, TotalAmount, PaidAmount, MatchStatus, PaymentStatus FROM finance.invoices WHERE UUID = @u", ("@u", uuid))).Single();
        return new InvRow((decimal)r["Subtotal"]!, (decimal)(r["TaxAmount"] ?? 0m), (decimal)r["TotalAmount"]!, (decimal)r["PaidAmount"]!,
            (string)r["MatchStatus"]!, (string?)r["PaymentStatus"] ?? "");
    }

    private async Task<NoteRow> NoteAsync(Kind kind, Guid uuid)
    {
        var sql = kind == Kind.Credit
            ? "SELECT CreditNoteNumber AS Number, ApplicationStatus, AppliedToInvoiceUuid, CreditAmount AS Amount, CarriedForwardAmount FROM finance.credit_notes WHERE UUID = @u"
            : "SELECT DebitNoteNumber AS Number, ApplicationStatus, AppliedToInvoiceUuid, DebitAmount AS Amount, CarriedForwardAmount FROM finance.debit_notes WHERE UUID = @u";
        var r = (await _f.QueryAsync(sql, ("@u", uuid))).Single();
        return new NoteRow((string)r["Number"]!, (string?)r["ApplicationStatus"] ?? "", (Guid?)r["AppliedToInvoiceUuid"], (decimal)r["Amount"]!,
            (decimal?)r["CarriedForwardAmount"]);
    }

    /// <summary>What the notes applied to this invoice deducted from it.</summary>
    private async Task<decimal> AppliedNotesAsync(Guid invoice) =>
        (decimal)(await _f.QueryAsync(
            "SELECT (SELECT ISNULL(SUM(ISNULL(CarriedForwardAmount, CreditAmount)), 0) FROM finance.credit_notes WHERE AppliedToInvoiceUuid = @i AND IsDelete = 0) + " +
            "(SELECT ISNULL(SUM(ISNULL(CarriedForwardAmount, DebitAmount)), 0) FROM finance.debit_notes WHERE AppliedToInvoiceUuid = @i AND IsDelete = 0) AS N",
            ("@i", invoice))).Single()["N"]!;

    private async Task<int> NotesForSroAsync(Guid sro) =>
        (int)(await _f.QueryAsync(
            "SELECT (SELECT COUNT(*) FROM finance.credit_notes WHERE SroUuid = @s AND IsDelete = 0) + " +
            "(SELECT COUNT(*) FROM finance.debit_notes WHERE SroUuid = @s AND IsDelete = 0) AS N", ("@s", sro))).Single()["N"]!;

    /// <summary>Supplier-payment lines naming the invoice whose payment is not cancelled or bounced (posted ones included).</summary>
    private async Task<decimal> LiveAllocationsAsync(Guid invoice) =>
        (decimal)(await _f.QueryAsync(
            "SELECT ISNULL(SUM(l.AllocatedAmount), 0) AS N FROM finance.supplier_payment_lines l JOIN finance.supplier_payments p ON p.Id = l.SupplierPaymentId " +
            "WHERE l.InvoiceUuid = @i AND p.Status NOT IN ('CANCELLED', 'BOUNCED')", ("@i", invoice))).Single()["N"]!;

    private async Task<string> PaymentStatusAsync(Guid payment) =>
        (string)(await _f.QueryAsync("SELECT Status FROM finance.supplier_payments WHERE UUID = @u", ("@u", payment))).Single()["Status"]!;

    private async Task<Dictionary<Guid, decimal>> OutstandingAsync(Partner vendor) =>
        (await _k.Ok(_k.Get($"/api/suppliers/{vendor.Uuid}/outstanding-invoices"), "outstanding invoices"))
        .Items().ToDictionary(i => i.G("invoiceUuid"), i => i.D("outstandingAmount"));

    private async Task<List<(string Type, decimal Debit, decimal Credit)>> LedgerByRefAsync(Guid reference) =>
        (await _f.QueryAsync(
            "SELECT TransactionType, DebitAmount, CreditAmount FROM finance.supplier_ledger_entries WHERE ReferenceId = @r ORDER BY SequenceNo",
            ("@r", reference)))
        .Select(r => ((string)r["TransactionType"]!, (decimal)r["DebitAmount"]!, (decimal)r["CreditAmount"]!)).ToList();

    private async Task<List<(string Type, decimal Debit, decimal Credit)>> MasterByRefAsync(Guid reference) =>
        (await _f.QueryAsync(
            "SELECT TransactionType, DebitAmount, CreditAmount FROM finance.master_financial_ledger WHERE ReferenceId = @r ORDER BY SequenceNo",
            ("@r", reference)))
        .Select(r => ((string)r["TransactionType"]!, (decimal)r["DebitAmount"]!, (decimal)r["CreditAmount"]!)).ToList();

    private async Task<decimal> LedgerBalanceAsync(Partner vendor) =>
        (decimal)(await _f.QueryAsync(
            "SELECT ISNULL(SUM(DebitAmount - CreditAmount), 0) AS N FROM finance.supplier_ledger_entries WHERE SupplierId = @s", ("@s", vendor.Uuid)))
        .Single()["N"]!;

    /// <summary>An invoice's total is its gross less exactly the notes applied to it.</summary>
    private async Task AssertInvoiceConsistentAsync(Guid invoice, string context)
    {
        var inv = await InvAsync(invoice);
        var applied = await AppliedNotesAsync(invoice);
        inv.Total.Should().Be(inv.Subtotal + inv.Tax - applied,
            $"{context}: TotalAmount is the gross less the notes applied to it ({applied}) — {inv}");
        if (inv.Match == "Reversed") applied.Should().Be(0m, $"{context}: no note stays applied to a reversed invoice");
        if (applied > 0m) inv.Match.Should().Be("Approved", $"{context}: notes are applied only to approved invoices — {inv}");
    }

    /// <summary>
    /// Supplier ledger = what the supplier's approved invoices still owe − the notes carried forward (credits the
    /// supplier owes us until they are used). A note applied to an invoice that is not a payable, a deduction lost
    /// to a race, or a ledger entry posted twice all break it.
    /// </summary>
    private async Task AssertBooksAgreeAsync(Partner vendor, string context)
    {
        var ledger = await LedgerBalanceAsync(vendor);
        var owed = (decimal)(await _f.QueryAsync(
            "SELECT ISNULL(SUM(TotalAmount - PaidAmount), 0) AS N FROM finance.invoices WHERE SupplierId = @s AND IsDelete = 0 AND MatchStatus = 'Approved'",
            ("@s", vendor.Uuid))).Single()["N"]!;
        var carried = (decimal)(await _f.QueryAsync(
            "SELECT (SELECT ISNULL(SUM(ISNULL(CarriedForwardAmount, CreditAmount)), 0) FROM finance.credit_notes WHERE SupplierId = @s AND IsDelete = 0 AND ApplicationStatus = 'CARRIED_FORWARD') + " +
            "(SELECT ISNULL(SUM(ISNULL(CarriedForwardAmount, DebitAmount)), 0) FROM finance.debit_notes WHERE SupplierId = @s AND IsDelete = 0 AND ApplicationStatus = 'CARRIED_FORWARD') AS N",
            ("@s", vendor.Uuid))).Single()["N"]!;
        ledger.Should().Be(owed - carried, $"{context}: supplier ledger {ledger} = approved invoices' outstanding {owed} − carried-forward notes {carried}");
    }

    /// <summary>The supplier's ledger is one unbroken chain: SequenceNo 1..n, each BalanceAfter = previous + debit − credit.</summary>
    private async Task AssertLedgerChainAsync(Partner vendor)
    {
        var rows = await _f.QueryAsync(
            "SELECT SequenceNo, DebitAmount, CreditAmount, BalanceAfter, TransactionType FROM finance.supplier_ledger_entries WHERE SupplierId = @s ORDER BY SequenceNo",
            ("@s", vendor.Uuid));
        var balance = 0m;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            Convert.ToInt32(r["SequenceNo"]).Should().Be(i + 1, "the supplier ledger's sequence has no gaps or repeats");
            balance += (decimal)r["DebitAmount"]! - (decimal)r["CreditAmount"]!;
            ((decimal)r["BalanceAfter"]!).Should().Be(balance, $"entry {i + 1} ({r["TransactionType"]}) carries the running balance");
        }
    }
}

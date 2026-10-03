using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Sync;

/// <summary>
/// Plan S-11 added <c>TaxCode</c> to invoice and bill lines, omitted from the stored JSON when null. A payload
/// from before tax codes must keep <b>exactly</b> the canonical JSON — and so the fingerprint — it was stored
/// and pushed with; otherwise every synced invoice would look changed and be sent to QuickBooks again.
/// The JSON strings and digests below are what the gateway produced before the change.
/// </summary>
public class PayloadFingerprintTests
{
    private const string OldInvoiceJson =
        """{"externalId":"SI-1","docNumber":"INV-0001","customerExternalId":"C-1","txnDate":"2026-09-01T00:00:00","dueDate":"2026-10-01T00:00:00","currencyCode":"PKR","status":"Issued","lines":[{"lineNo":1,"itemExternalId":"I-1","description":"Widget","quantity":2,"unitPrice":100.5,"discountPercent":0,"taxPercent":17}],"headerDiscountAmount":0,"expectedTaxAmount":34.17,"expectedTotal":235.17,"customerMemo":null,"privateNote":"SO-1"}""";

    /// <summary>SHA-256 of <see cref="OldInvoiceJson"/>, computed outside .NET.</summary>
    private const string OldInvoiceFingerprint = "489dd6359728610c068bc12eaf1e0e4d02008ab37c9f6dcd65d327bbec71bc81";

    private const string OldBillJson =
        """{"externalId":"B-1","docNumber":"SUP-1","vendorExternalId":"V-1","txnDate":"2026-09-01T00:00:00","dueDate":null,"currencyCode":"PKR","lines":[{"lineNo":1,"itemExternalId":null,"category":"Freight","description":null,"quantity":null,"unitPrice":null,"amount":50,"taxPercent":null}],"expectedTaxAmount":0,"expectedTotal":50,"privateNote":null}""";

    private const string OldBillFingerprint = "7d43045751fa8a15f438823fd6fdad09d09ee85281109d12e07393b474d879d3";

    /// <summary>The invoice behind <see cref="OldInvoiceJson"/>, as today's SalesInvoicePayloadFactory builds it for an old line (no code).</summary>
    private static SalesInvoicePayload OldInvoice() => new()
    {
        ExternalId = "SI-1", DocNumber = "INV-0001", CustomerExternalId = "C-1",
        TxnDate = new DateTime(2026, 9, 1), DueDate = new DateTime(2026, 10, 1), CurrencyCode = "PKR",
        Status = SalesInvoicePayloadStatus.Issued,
        Lines = [new SalesInvoiceLinePayload
        {
            LineNo = 1, ItemExternalId = "I-1", Description = "Widget", Quantity = 2m, UnitPrice = 100.50m,
            DiscountPercent = 0m, TaxPercent = 17.00m, TaxCode = null
        }],
        HeaderDiscountAmount = 0m, ExpectedTaxAmount = 34.17m, ExpectedTotal = 235.17m, PrivateNote = "SO-1"
    };

    private static BillPayload OldBill() => new()
    {
        ExternalId = "B-1", DocNumber = "SUP-1", VendorExternalId = "V-1", TxnDate = new DateTime(2026, 9, 1), CurrencyCode = "PKR",
        Lines = [new BillLinePayload { LineNo = 1, Category = BillLineCategory.Freight, Amount = 50m, TaxPercent = null, TaxCode = null }],
        ExpectedTaxAmount = 0m, ExpectedTotal = 50m
    };

    [Fact]
    public void An_invoice_without_tax_codes_serializes_exactly_as_before_the_change()
    {
        var json = SyncPayloads.Serialize(OldInvoice());

        json.Should().Be(OldInvoiceJson);
        json.Should().NotContain("taxCode", "a null TaxCode is omitted, even though the canonical form writes every other null");
        SyncPayloads.Fingerprint(json).Should().Be(OldInvoiceFingerprint);
    }

    [Fact]
    public void A_bill_without_tax_codes_serializes_exactly_as_before_the_change()
    {
        var json = SyncPayloads.Serialize(OldBill());

        json.Should().Be(OldBillJson);
        SyncPayloads.Fingerprint(json).Should().Be(OldBillFingerprint);
    }

    [Fact]
    public void A_stored_old_payload_reads_back_and_re_serializes_to_the_same_fingerprint()
    {
        var invoice = (SalesInvoicePayload)SyncPayloads.Deserialize(SyncKind.SalesInvoice, OldInvoiceJson);
        invoice.Lines.Single().TaxCode.Should().BeNull();
        SyncPayloads.Fingerprint(SyncPayloads.Serialize(invoice)).Should().Be(OldInvoiceFingerprint);

        var bill = (BillPayload)SyncPayloads.Deserialize(SyncKind.Bill, OldBillJson);
        bill.Lines.Single().TaxCode.Should().BeNull();
        SyncPayloads.Fingerprint(SyncPayloads.Serialize(bill)).Should().Be(OldBillFingerprint);
    }

    [Fact]
    public void A_tax_code_is_written_after_the_rate_and_changes_the_fingerprint()
    {
        var invoice = OldInvoice();
        invoice.Lines[0].TaxCode = "GST17";
        var json = SyncPayloads.Serialize(invoice);

        json.Should().Contain("\"taxPercent\":17,\"taxCode\":\"GST17\"}");
        SyncPayloads.Fingerprint(json).Should().NotBe(OldInvoiceFingerprint);
        ((SalesInvoicePayload)SyncPayloads.Deserialize(SyncKind.SalesInvoice, json)).Lines[0].TaxCode.Should().Be("GST17");

        var bill = OldBill();
        bill.Lines[0].TaxCode    = "PST5";
        bill.Lines[0].TaxPercent = 5m;
        var billJson = SyncPayloads.Serialize(bill);
        billJson.Should().Contain("\"taxPercent\":5,\"taxCode\":\"PST5\"}");
        SyncPayloads.Fingerprint(billJson).Should().NotBe(OldBillFingerprint);
    }

    // ── Customers, vendors, items: untouched by S-10/S-11, locked here so a later field cannot re-send them all ──

    private const string CustomerJson =
        """{"externalId":"C-1","displayName":"Acme Traders","companyName":"Acme Traders","code":"C001","email":"ap@acme.example","phone":"042-111-222","fax":null,"website":null,"taxId":"TX-C-1","billingAddress":{"line1":"1 Mall Road","line2":null,"city":"Lahore","region":null,"postalCode":null,"country":"Pakistan"},"currencyCode":"PKR","paymentTermExternalId":null,"notes":null,"isActive":true}""";

    private const string VendorJson =
        """{"accountNumber":"S001","externalId":"V-1","displayName":"Karachi Supplies","companyName":"Karachi Supplies","code":"S001","email":"sales@ks.example","phone":null,"fax":null,"website":null,"taxId":"VT-V-1","billingAddress":null,"currencyCode":"PKR","paymentTermExternalId":null,"notes":null,"isActive":true}""";

    private const string ItemJson =
        """{"externalId":"I-1","name":"Widget","variantName":null,"sku":"W-1","description":"Widget description","kind":"Goods","salesPrice":100,"purchaseCost":60,"isSold":true,"isPurchased":true,"isActive":true}""";

    [Fact]
    public void Customer_vendor_and_item_payloads_serialize_exactly_as_before()
    {
        SyncPayloads.Serialize(TestPayloads.Customer()).Should().Be(CustomerJson);
        SyncPayloads.Serialize(TestPayloads.Vendor()).Should().Be(VendorJson);
        SyncPayloads.Serialize(TestPayloads.Item()).Should().Be(ItemJson);
    }

    [Fact]
    public void A_foreign_invoice_with_its_own_rate_differs_and_the_old_invoice_is_still_byte_for_byte_the_same()
    {
        SyncPayloads.Serialize(OldInvoice()).Should().Be(OldInvoiceJson, "ExchangeRate/ExchangeRateCurrencyCode are omitted when null");
        SyncPayloads.Serialize(OldBill()).Should().Be(OldBillJson);

        var foreign = OldInvoice();
        foreign.CurrencyCode = "USD";
        foreign.ExchangeRate = 281.5m;
        foreign.ExchangeRateCurrencyCode = "PKR";
        SyncPayloads.Fingerprint(SyncPayloads.Serialize(foreign)).Should().NotBe(OldInvoiceFingerprint);
    }

    [Fact]
    public async Task An_invoice_already_in_QuickBooks_sent_again_without_codes_is_not_sent_again()
    {
        await using var h = new SyncHarness();
        var connection = await h.ConnectAsync();
        await using (var db = h.DbAs(h.OrgId))
        {
            // Pushed before tax codes existed: its stored fingerprint is the old one.
            db.EntityMaps.Add(new EntityMap
            {
                ConnectionId = connection.Id, SourceSystem = QuickBooksSourceSystems.Scm, Kind = SyncKind.SalesInvoice,
                ExternalId = "SI-1", DisplayLabel = "INV-0001", PayloadJson = OldInvoiceJson,
                PayloadFingerprint = OldInvoiceFingerprint, LastPushedFingerprint = OldInvoiceFingerprint,
                RemoteId = "145", RemoteSyncToken = "0", State = SyncState.Synced
            });
            await db.SaveChangesAsync();
        }

        var result = await h.Send(OldInvoice());

        result.Outcome.Should().Be(GatewayOutcome.Accepted);
        result.State.Should().Be(SyncState.Synced, "the same data is already in QuickBooks");
        (await h.EntriesAsync(SyncKind.SalesInvoice, "SI-1")).Should().BeEmpty("nothing is queued");
        await h.DrainAsync();
        h.Provider.WriteCalls.Should().Be(0);
        (await h.MapAsync(SyncKind.SalesInvoice, "SI-1")).PayloadFingerprint.Should().Be(OldInvoiceFingerprint);
    }
}

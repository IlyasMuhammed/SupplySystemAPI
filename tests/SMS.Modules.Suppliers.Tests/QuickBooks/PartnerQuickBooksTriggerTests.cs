using System.Text.Json;
using AutoMapper;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Integration;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Repositories;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Suppliers.Tests.QuickBooks;

file sealed class OpenAccess : IUserSupplierAccessService
{
    public Task<bool> IsRestrictedAsync() => Task.FromResult(false);
    public Task<IReadOnlySet<Guid>> GetAllowedSupplierIdsAsync() => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
    public Task<bool> CanAccessSupplierAsync(Guid supplierUuid) => Task.FromResult(true);
}

file sealed class PlainEncryption : IEncryptionService
{
    public string Encrypt(string plaintext) => $"ENC:{plaintext}";
    public string Decrypt(string ciphertext) => ciphertext.StartsWith("ENC:") ? ciphertext[4..] : ciphertext;
}

/// <summary>The real partner services and repositories over one in-memory database, with a recording gateway.</summary>
file sealed class Rig
{
    public required SuppliersDbContext                        Db;
    public required Guid                                      Org;
    public required RecordingQuickBooksGateway                Gateway;
    public required QuickBooksEntitySyncBridge                Sync;
    public required FixedCurrencyCodes                        Currencies;
    public required ListLogger<PartnerQuickBooksPublisher>    PublisherLog;
    public required ListLogger<PartnerQuickBooksSource>       SourceLog;
    public required PartnerQuickBooksSource                   Source;
    public required PartnerQuickBooksPublisher                Publisher;
    public required SuppliersService                          Suppliers;
    public required BusinessPartnerService                    Partners;

    public static Rig New()
    {
        var (db, tenant, _) = SuppliersTestDb.New();
        var gateway      = new RecordingQuickBooksGateway();
        var sync         = new QuickBooksEntitySyncBridge(gateway);
        var currencies   = new FixedCurrencyCodes();
        var sourceLog    = new ListLogger<PartnerQuickBooksSource>();
        var publisherLog = new ListLogger<PartnerQuickBooksPublisher>();
        var source       = new PartnerQuickBooksSource(db, gateway, currencies, sourceLog);
        var publisher    = new PartnerQuickBooksPublisher(db, source, sync, publisherLog);

        var mapper = new MapperConfiguration(cfg => cfg.AddProfile<BusinessPartnerMappingProfile>()).CreateMapper();

        return new Rig
        {
            Db = db, Org = tenant.OrganizationId, Gateway = gateway, Sync = sync, Currencies = currencies,
            PublisherLog = publisherLog, SourceLog = sourceLog, Source = source, Publisher = publisher,
            Suppliers = new SuppliersService(
                new SuppliersRepository(db, new PlainEncryption(), new OpenAccess()),
                new DefaultSupplierEventPublisher(NullLogger<DefaultSupplierEventPublisher>.Instance),
                new PhoneNumberValidationService(), [], publisher),
            Partners = new BusinessPartnerService(
                new BusinessPartnerRepository(db, new OpenAccess(), mapper), new BusinessPartnerModelValidator(), [], publisher)
        };
    }

    public Task<Guid> NewSupplierAsync(string code = "SUP-1") =>
        Suppliers.CreateSupplierAsync(new CreateSupplierRequest
        {
            SupplierName = $"Supplier {code}", SupplierCode = code, Email = "ap@example.com", City = "Lahore"
        }, createdBy: 1);

    public void Forget()
    {
        Gateway.Calls.Clear();
        Gateway.Customers.Clear();
        Gateway.Vendors.Clear();
        Sync.Requests.Clear();
        Db.ChangeTracker.Clear();
    }
}

public class PartnerQuickBooksTriggerTests
{
    // ── BusinessPartnerService (/api/partners) ───────────────────────────────

    [Fact]
    public async Task Creating_a_partner_that_is_customer_and_vendor_sends_both_under_one_external_id()
    {
        var rig = Rig.New();

        var uuid = await rig.Partners.CreateAsync(new BusinessPartnerModel
        {
            CompanyName = "Both Ways Ltd", PartnerCode = "BW-1", IsCustomer = true, IsVendor = true
        }, createdBy: 1);

        rig.Gateway.Customers.Should().ContainSingle().Which.ExternalId.Should().Be(uuid.ToString());
        rig.Gateway.Vendors.Should().ContainSingle().Which.ExternalId.Should().Be(uuid.ToString());
        rig.Gateway.Customers[0].DisplayName.Should().Be("Both Ways Ltd");
        rig.Gateway.Vendors[0].AccountNumber.Should().Be("BW-1");
    }

    [Theory]
    [InlineData(true,  false, false, 1, 0)]
    [InlineData(false, true,  false, 0, 1)]
    [InlineData(false, false, true,  0, 1)]   // a carrier is a payee: a vendor
    public async Task Creating_a_partner_sends_only_the_roles_it_holds(bool customer, bool vendor, bool carrier, int customers, int vendors)
    {
        var rig = Rig.New();

        await rig.Partners.CreateAsync(new BusinessPartnerModel
        {
            CompanyName = "One Role", PartnerCode = "OR-1", IsCustomer = customer, IsVendor = vendor, IsCarrier = carrier,
            VehicleTypes = carrier ? "TRUCK" : null
        }, createdBy: 1);

        rig.Gateway.Customers.Should().HaveCount(customers);
        rig.Gateway.Vendors.Should().HaveCount(vendors);
    }

    [Fact]
    public async Task Updating_a_partner_sends_it_as_it_now_stands()
    {
        var rig = Rig.New();
        var uuid = await rig.Partners.CreateAsync(new BusinessPartnerModel { CompanyName = "Old Name", PartnerCode = "P-1", IsCustomer = true }, 1);
        rig.Forget();

        await rig.Partners.UpdateAsync(uuid, new BusinessPartnerModel { CompanyName = "New Name", PartnerCode = "P-1", IsCustomer = true, IsVendor = true }, 2);

        rig.Gateway.Customers.Should().ContainSingle().Which.DisplayName.Should().Be("New Name");
        rig.Gateway.Vendors.Should().ContainSingle("it became a vendor too").Which.DisplayName.Should().Be("New Name");
    }

    [Fact]
    public async Task Deleting_a_partner_sends_it_once_more_as_inactive()
    {
        var rig = Rig.New();
        var uuid = await rig.Partners.CreateAsync(new BusinessPartnerModel { CompanyName = "Short Lived", PartnerCode = "SL-1", IsCustomer = true }, 1);
        rig.Forget();

        await rig.Partners.DeleteAsync(uuid, 2);

        rig.Gateway.Customers.Should().ContainSingle().Which.IsActive.Should().BeFalse();
    }

    // ── SuppliersService (/api/suppliers) ────────────────────────────────────

    [Fact]
    public async Task Creating_a_supplier_the_legacy_way_sends_a_vendor()
    {
        var rig = Rig.New();

        var uuid = await rig.NewSupplierAsync();

        var vendor = rig.Gateway.Vendors.Should().ContainSingle().Subject;
        vendor.ExternalId.Should().Be(uuid.ToString());
        vendor.Email.Should().Be("ap@example.com");
        vendor.BillingAddress!.City.Should().Be("Lahore");
        vendor.IsActive.Should().BeTrue("a PENDING supplier is active until something bars it");
        rig.Gateway.Customers.Should().BeEmpty();
    }

    [Fact]
    public async Task Patching_a_supplier_sends_the_patched_values()
    {
        var rig = Rig.New();
        var uuid = await rig.NewSupplierAsync();
        rig.Forget();

        var ok = await rig.Suppliers.PatchSupplierAsync(uuid, new PatchSupplierRequest { Phone = "+92 42 000", Notes = "Net 30" }, 2);

        ok.Should().BeTrue();
        var vendor = rig.Gateway.Vendors.Should().ContainSingle().Subject;
        vendor.Phone.Should().Be("+92 42 000");
        vendor.Notes.Should().Be("Net 30");
    }

    [Fact]
    public async Task Patching_a_supplier_that_does_not_exist_sends_nothing()
    {
        var rig = Rig.New();

        var ok = await rig.Suppliers.PatchSupplierAsync(Guid.NewGuid(), new PatchSupplierRequest { Phone = "1" }, 2);

        ok.Should().BeFalse();
        rig.Gateway.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_step_of_the_status_workflow_is_sent_with_what_it_means_for_active()
    {
        var rig = Rig.New();
        var approved = await rig.NewSupplierAsync("A");
        var rejected = await rig.NewSupplierAsync("R");
        var blacklisted = await rig.NewSupplierAsync("B");
        var suspended = await rig.NewSupplierAsync("S");
        rig.Forget();

        await rig.Suppliers.ApproveSupplierAsync(approved, 9);
        await rig.Suppliers.ApproveSupplierAsync(blacklisted, 9);
        await rig.Suppliers.ApproveSupplierAsync(suspended, 9);
        await rig.Suppliers.RejectSupplierAsync(rejected, "Incomplete documents", 9);
        rig.Forget();

        await rig.Suppliers.BlacklistSupplierAsync(blacklisted, "Fraud", 9);
        await rig.Suppliers.SuspendSupplierAsync(suspended, "Late deliveries", DateTime.UtcNow.AddDays(30), 9);

        rig.Gateway.Vendors.Should().HaveCount(2);
        rig.Gateway.Vendors.Single(v => v.ExternalId == blacklisted.ToString()).IsActive.Should().BeFalse();
        rig.Gateway.Vendors.Single(v => v.ExternalId == suspended.ToString()).IsActive.Should().BeTrue("a suspension is temporary");
    }

    [Fact]
    public async Task Approving_and_rejecting_are_sent()
    {
        var rig = Rig.New();
        var approved = await rig.NewSupplierAsync("A");
        var rejected = await rig.NewSupplierAsync("R");
        rig.Forget();

        await rig.Suppliers.ApproveSupplierAsync(approved, 9);
        await rig.Suppliers.RejectSupplierAsync(rejected, "No", 9);

        rig.Gateway.Vendors.Single(v => v.ExternalId == approved.ToString()).IsActive.Should().BeTrue();
        rig.Gateway.Vendors.Single(v => v.ExternalId == rejected.ToString()).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_a_supplier_the_legacy_way_sends_it_as_inactive()
    {
        var rig = Rig.New();
        var uuid = await rig.NewSupplierAsync();
        rig.Forget();

        await rig.Suppliers.DeleteSupplierAsync(uuid, 3);

        rig.Gateway.Vendors.Should().ContainSingle().Which.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Contacts_bank_details_and_documents_are_not_on_the_payload_so_they_send_nothing()
    {
        var rig = Rig.New();
        var uuid = await rig.NewSupplierAsync();
        rig.Forget();

        await rig.Suppliers.AddContactAsync(uuid, new AddContactRequest { ContactName = "Ali" });
        await rig.Suppliers.UpsertBankDetailAsync(uuid, new UpsertBankDetailRequest { BankName = "HBL", BankAccountNo = "123" }, 1);
        await rig.Suppliers.AttachDocumentAsync(uuid, new AttachDocumentRequest { FileName = "ntn.pdf", FileUrl = "/x" }, 1);

        rig.Gateway.Calls.Should().BeEmpty();
    }

    // ── The gateway can never fail the partner's own operation ───────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_throwing_gateway_does_not_fail_the_save_and_is_logged_with_the_partner_id(bool faultedTask)
    {
        var rig = Rig.New();
        rig.Gateway.Throw = new HttpRequestException("gateway down");
        rig.Gateway.FaultTask = faultedTask;

        var uuid = await rig.Partners.CreateAsync(new BusinessPartnerModel { CompanyName = "Still Saved", PartnerCode = "SS-1", IsCustomer = true, IsVendor = true }, 1);

        (await rig.Db.BusinessPartners.AsNoTracking().AnyAsync(p => p.UUID == uuid)).Should().BeTrue();
        rig.Gateway.Calls.Should().HaveCount(2, "a failed customer push does not stop the vendor push");
        rig.PublisherLog.At(LogLevel.Warning).Should().HaveCount(2)
            .And.OnlyContain(w => w.Message.Contains(uuid.ToString()) && w.Exception is HttpRequestException);
    }

    [Fact]
    public async Task A_failure_before_the_gateway_is_reached_is_swallowed_and_logged_too()
    {
        var rig = Rig.New();
        rig.Currencies.Throw = new InvalidOperationException("Lookups is down");

        var uuid = await rig.NewSupplierAsync();

        uuid.Should().NotBeEmpty();
        rig.Gateway.Calls.Should().BeEmpty();
        rig.PublisherLog.At(LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain(uuid.ToString());
    }

    [Fact]
    public async Task An_invalid_answer_is_information_and_the_operation_succeeds()
    {
        var rig = Rig.New();
        rig.Gateway.Result = GatewayResult.Invalid([new GatewayError("DisplayName", "DUPLICATE", "Name taken.")]);

        var uuid = await rig.NewSupplierAsync();

        uuid.Should().NotBeEmpty();
        rig.PublisherLog.At(LogLevel.Information).Should().ContainSingle().Which.Message.Should().Contain("DUPLICATE");
        rig.PublisherLog.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        rig.SourceLog.Entries.Should().BeEmpty("the edit path no longer goes through the source's own send");
    }

    // ── A44 — the edit path is the entity sync gateway (D-25, D-76..D-79) ────

    [Fact]
    public async Task Every_role_is_one_entity_sync_request_with_the_payload_the_pull_back_would_send()
    {
        var rig = Rig.New();
        var uuid = await rig.Partners.CreateAsync(new BusinessPartnerModel
        {
            CompanyName = "Both Ways Ltd", PartnerCode = "BW-1", IsCustomer = true, IsVendor = true
        }, createdBy: 1);

        var requests = rig.Sync.Requests;
        requests.Select(r => r.EntityType).Should().Equal(SyncEntityType.Customer, SyncEntityType.Vendor);
        requests.Should().OnlyContain(r => r.EntityId == uuid && r.Action == SyncAction.Update && r.InitiatedBy == "system");
        requests[0].Document.Should().BeOfType<CustomerPayload>();
        requests[1].Document.Should().BeOfType<VendorPayload>();
        foreach (var r in requests)
        {
            r.IdempotencyKey.Should().MatchRegex($"^{r.EntityType}:Update:{uuid}:\\d+$");
            r.Snapshot!.DisplayName.Should().Be("Both Ways Ltd");
            r.Snapshot.IsActive.Should().BeTrue();
        }

        // Same JSON as the gateway's pull-back ("Push now" / dependency) builds → same fingerprint, no re-send.
        var published = requests.Select(r => JsonSerializer.Serialize(r.Document, r.Document.GetType())).ToList();
        rig.Forget();
        await rig.Source.PushAsync(SyncKind.Customer, [uuid.ToString()]);
        await rig.Source.PushAsync(SyncKind.Vendor, [uuid.ToString()]);
        published[0].Should().Be(JsonSerializer.Serialize(rig.Gateway.Customers.Single()));
        published[1].Should().Be(JsonSerializer.Serialize(rig.Gateway.Vendors.Single()));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("blacklist")]
    [InlineData("reject")]
    public async Task An_inactive_partner_is_a_Deactivate_request(string how)
    {
        var rig = Rig.New();
        var uuid = await rig.NewSupplierAsync();
        if (how == "blacklist") await rig.Suppliers.ApproveSupplierAsync(uuid, 9);
        rig.Forget();

        switch (how)
        {
            case "delete":    await rig.Suppliers.DeleteSupplierAsync(uuid, 3); break;
            case "blacklist": await rig.Suppliers.BlacklistSupplierAsync(uuid, "Fraud", 9); break;
            default:          await rig.Suppliers.RejectSupplierAsync(uuid, "No", 9); break;
        }

        var request = rig.Sync.Requests.Should().ContainSingle().Subject;
        (request.EntityType, request.Action, request.EntityId).Should().Be((SyncEntityType.Vendor, SyncAction.Deactivate, uuid));
        request.IdempotencyKey.Should().StartWith($"Vendor:Deactivate:{uuid}:");
        ((VendorPayload)request.Document).IsActive.Should().BeFalse();
        request.Snapshot!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task A_partner_with_no_customer_or_payee_role_sends_no_request()
    {
        var rig = Rig.New();
        rig.Db.BusinessPartners.Add(new SMS.Modules.Suppliers.Domain.BusinessPartner
        {
            UUID = Guid.NewGuid(), OrganizationId = rig.Org, SupplierCode = "NR-1", SupplierName = "No Role",
            IsVendor = false, IsCustomer = false, IsCarrier = false, IsServiceProvider = false,
            Status = "ACTIVE", IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        });
        await rig.Db.SaveChangesAsync();
        var uuid = rig.Db.BusinessPartners.Local.Single().UUID;
        rig.Forget();

        await rig.Publisher.PublishAsync(uuid);

        rig.Sync.Requests.Should().BeEmpty();
        rig.Currencies.Loads.Should().Be(0);
    }

    [Theory]
    [InlineData(EntitySyncOutcome.NoTargetConfigured, true,  LogLevel.Debug)]
    [InlineData(EntitySyncOutcome.ModuleNotLicensed,  false, LogLevel.Debug)]
    [InlineData(EntitySyncOutcome.Held,               true,  LogLevel.Debug)]
    [InlineData(EntitySyncOutcome.ValidationFailed,   false, LogLevel.Information)]
    [InlineData(EntitySyncOutcome.Queued,             false, LogLevel.Warning)]
    public async Task What_the_gateway_answers_is_logged_and_never_fails_the_save(EntitySyncOutcome outcome, bool success, LogLevel level)
    {
        var rig = Rig.New();
        rig.Sync.Answer = new EntitySyncResult { Success = success, Outcome = outcome, ErrorMessage = "why" };

        var uuid = await rig.NewSupplierAsync();

        uuid.Should().NotBeEmpty();
        rig.Gateway.Calls.Should().BeEmpty("the answer replaced the forward");
        rig.PublisherLog.Entries.Should().ContainSingle().Which.Level.Should().Be(level);
        rig.PublisherLog.Entries[0].Message.Should().Contain(uuid.ToString());
    }

    [Fact]
    public async Task Without_a_router_the_edit_goes_straight_to_the_QuickBooks_gateway_as_before_A44()
    {
        var (db, _, _) = SuppliersTestDb.New();
        var gateway = new RecordingQuickBooksGateway();
        var sourceLog = new ListLogger<PartnerQuickBooksSource>();
        var log = new ListLogger<PartnerQuickBooksPublisher>();
        var source = new PartnerQuickBooksSource(db, gateway, new FixedCurrencyCodes(), sourceLog);
        var publisher = new PartnerQuickBooksPublisher(db, source, new NullEntitySyncGateway(), log);
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile<BusinessPartnerMappingProfile>()).CreateMapper();
        var service = new BusinessPartnerService(new BusinessPartnerRepository(db, new OpenAccess(), mapper), new BusinessPartnerModelValidator(), [], publisher);

        var uuid = await service.CreateAsync(new BusinessPartnerModel { CompanyName = "Direct", PartnerCode = "D-1", IsCustomer = true, IsVendor = true }, 1);

        gateway.Calls.Should().Equal($"Customer:{uuid}", $"Vendor:{uuid}");
        var built = source.BuildRoles(await db.BusinessPartners.AsNoTracking().SingleAsync(p => p.UUID == uuid));
        JsonSerializer.Serialize(gateway.Customers.Single()).Should().Be(JsonSerializer.Serialize((CustomerPayload)built[0]));
        JsonSerializer.Serialize(gateway.Vendors.Single()).Should().Be(JsonSerializer.Serialize((VendorPayload)built[1]));

        // ...and a throwing gateway on that path is still contained and logged with the partner id.
        gateway.Throw = new HttpRequestException("down");
        await service.UpdateAsync(uuid, new BusinessPartnerModel { CompanyName = "Direct 2", PartnerCode = "D-1", IsCustomer = true }, 1);
        sourceLog.At(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain(uuid.ToString());
    }

    // ── Imports: one request per imported row, after the batch commit (D-78) ──

    [Fact]
    public async Task A_customer_import_pushes_one_request_per_imported_row()
    {
        var rig = Rig.New();
        var target = new SMS.Modules.Suppliers.Import.CustomerImportTarget(rig.Db, quickBooks: rig.Publisher);

        var outcomes = await target.ApplyAsync(rig.Org, 1,
        [
            new ImportApplyRow(2, new Dictionary<string, string?> { ["name"] = "Alpha" }, "CREATE", null),
            new ImportApplyRow(3, new Dictionary<string, string?> { ["name"] = "Beta", ["is_active"] = "false" }, "CREATE", null),
            new ImportApplyRow(4, new Dictionary<string, string?> { ["name"] = "Gamma" }, "CREATE", null)
        ]);

        rig.Sync.Requests.Select(r => r.EntityId).Should().Equal(outcomes.Select(o => o.EntityUuid));
        rig.Sync.Requests.Should().OnlyContain(r => r.EntityType == SyncEntityType.Customer);
        rig.Sync.Requests.Select(r => r.Action).Should().Equal(SyncAction.Update, SyncAction.Deactivate, SyncAction.Update);
        rig.Gateway.Customers.Select(c => c.DisplayName).Should().Equal("Alpha", "Beta", "Gamma");
    }

    [Fact]
    public async Task A_supplier_import_pushes_a_vendor_request_per_row_and_one_failing_row_does_not_stop_the_rest()
    {
        var rig = Rig.New();
        rig.Gateway.FailWhen = _ => rig.Gateway.Calls.Count == 1;   // the first push throws
        var target = new SMS.Modules.Suppliers.Import.SupplierImportTarget(rig.Db, quickBooks: rig.Publisher);

        var outcomes = await target.ApplyAsync(rig.Org, 1,
        [
            new ImportApplyRow(2, new Dictionary<string, string?> { ["code"] = "S-1", ["name"] = "One" }, "CREATE", null),
            new ImportApplyRow(3, new Dictionary<string, string?> { ["code"] = "S-2", ["name"] = "Two" }, "CREATE", null)
        ]);

        outcomes.Should().HaveCount(2).And.OnlyContain(o => o.Status == "IMPORTED");
        rig.Sync.Requests.Should().HaveCount(2).And.OnlyContain(r => r.EntityType == SyncEntityType.Vendor);
        rig.PublisherLog.At(LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain(outcomes[0].EntityUuid.ToString());
    }

    [Fact]
    public async Task Without_a_publisher_the_services_behave_exactly_as_before()
    {
        var (db, _, _) = SuppliersTestDb.New();
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile<BusinessPartnerMappingProfile>()).CreateMapper();
        var service = new BusinessPartnerService(new BusinessPartnerRepository(db, new OpenAccess(), mapper), new BusinessPartnerModelValidator(), []);

        var uuid = await service.CreateAsync(new BusinessPartnerModel { CompanyName = "Plain", PartnerCode = "PL-1", IsVendor = true }, 1);

        (await service.GetByIdAsync(uuid)).Should().NotBeNull();
    }
}

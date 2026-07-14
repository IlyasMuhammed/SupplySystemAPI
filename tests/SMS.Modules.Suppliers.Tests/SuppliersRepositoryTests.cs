using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Repositories;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

// ── Fake encryption (returns plaintext for testability) ───────────────────────

file sealed class FakeEncryption : IEncryptionService
{
    public string Encrypt(string plaintext) => $"ENC:{plaintext}";
    public string Decrypt(string ciphertext) => ciphertext.StartsWith("ENC:") ? ciphertext[4..] : ciphertext;
}

// ── Test builder ──────────────────────────────────────────────────────────────

file static class Build
{
    internal static (SuppliersRepository repo, SuppliersDbContext db) New(Action<SuppliersDbContext>? seed = null)
    {
        var opts = new DbContextOptionsBuilder<SuppliersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new SuppliersDbContext(opts);
        seed?.Invoke(db);
        db.SaveChanges();
        return (new SuppliersRepository(db, new FakeEncryption()), db);
    }

    internal static Supplier Supplier(SuppliersDbContext db,
        string name = "Test Supplier",
        string code = "TST001",
        string status = "PENDING",
        bool isDelete = false)
    {
        var s = new Supplier
        {
            UUID = Guid.NewGuid(),
            SupplierName = name,
            SupplierCode = code,
            Status = status,
            IsActive = true,
            IsDelete = isDelete,
            CreatedBy = 1,
            CreatedDate = DateTime.UtcNow
        };
        db.Suppliers.Add(s);
        return s;
    }
}

// ── CreateSupplierAsync ────────────────────────────────────────────────────────

public class CreateSupplierAsync_Tests
{
    [Fact]
    public async Task WithTwoTypes_CreatesTwoTypeMappingRows()
    {
        var (repo, db) = Build.New();
        var typeId1 = Guid.NewGuid();
        var typeId2 = Guid.NewGuid();

        var req = new CreateSupplierRequest
        {
            SupplierName = "Acme Corp",
            SupplierCode = "ACM001",
            SupplierTypeIds =
            [
                new SupplierTypeMappingInput { LookupValueId = typeId1, IsPrimary = true },
                new SupplierTypeMappingInput { LookupValueId = typeId2, IsPrimary = false }
            ],
            IndustryIds = []
        };

        var uuid = await repo.CreateSupplierAsync(req, createdBy: 1);

        var mappings = await db.SupplierTypeMappings.ToListAsync();
        mappings.Should().HaveCount(2);
        mappings.Select(m => m.LookupValueId).Should().Contain([typeId1, typeId2]);
        mappings.First(m => m.LookupValueId == typeId1).IsPrimary.Should().BeTrue();
        mappings.First(m => m.LookupValueId == typeId2).IsPrimary.Should().BeFalse();
    }

    [Fact]
    public async Task WithTwoIndustries_CreatesTwoIndustryMappingRows()
    {
        var (repo, db) = Build.New();
        var ind1 = Guid.NewGuid();
        var ind2 = Guid.NewGuid();

        var req = new CreateSupplierRequest
        {
            SupplierName = "Beta Ltd",
            SupplierCode = "BET001",
            SupplierTypeIds = [],
            IndustryIds =
            [
                new SupplierTypeMappingInput { LookupValueId = ind1, IsPrimary = true },
                new SupplierTypeMappingInput { LookupValueId = ind2, IsPrimary = false }
            ]
        };

        await repo.CreateSupplierAsync(req, createdBy: 1);

        var mappings = await db.SupplierIndustryMappings.ToListAsync();
        mappings.Should().HaveCount(2);
        mappings.Select(m => m.LookupValueId).Should().Contain([ind1, ind2]);
    }

    [Fact]
    public async Task WithoutOptionalPaymentTerms_Succeeds()
    {
        var (repo, _) = Build.New();

        var req = new CreateSupplierRequest
        {
            SupplierName = "Gamma Inc",
            SupplierCode = "GAM001",
            PreferredPaymentTerms = null,   // explicitly null — optional
            PreferredCurrency = null,        // explicitly null — optional
            SupplierTypeIds = [],
            IndustryIds = []
        };

        var uuid = await repo.CreateSupplierAsync(req, createdBy: 1);

        uuid.Should().NotBeEmpty();
    }

    [Fact]
    public async Task WithDuplicateSupplierCode_ThrowsConflictException()
    {
        var (repo, db) = Build.New(ctx => Build.Supplier(ctx, code: "DUP001"));

        var req = new CreateSupplierRequest
        {
            SupplierName = "Delta Corp",
            SupplierCode = "DUP001",      // same code as existing supplier
            SupplierTypeIds = [],
            IndustryIds = []
        };

        var act = () => repo.CreateSupplierAsync(req, createdBy: 1);

        await act.Should().ThrowAsync<ConflictException>()
            .WithMessage("*DUP001*");
    }

    [Fact]
    public async Task DuplicateCheck_IgnoresSoftDeletedSuppliers()
    {
        // A deleted supplier with the same code must NOT block creation.
        var (repo, _) = Build.New(ctx => Build.Supplier(ctx, code: "OLD001", isDelete: true));

        var req = new CreateSupplierRequest
        {
            SupplierName = "New Corp",
            SupplierCode = "OLD001",      // same code, but original is soft-deleted
            SupplierTypeIds = [],
            IndustryIds = []
        };

        var act = () => repo.CreateSupplierAsync(req, createdBy: 1);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NewSupplier_DefaultsStatusToPending()
    {
        var (repo, db) = Build.New();

        await repo.CreateSupplierAsync(new CreateSupplierRequest
        {
            SupplierName = "Status Test",
            SupplierCode = "STA001",
            SupplierTypeIds = [],
            IndustryIds = []
        }, createdBy: 1);

        var supplier = await db.Suppliers.SingleAsync();
        supplier.Status.Should().Be("PENDING");
        supplier.IsActive.Should().BeTrue();
    }
}

// ── GetSupplierByIdAsync ───────────────────────────────────────────────────────

public class GetSupplierByIdAsync_Tests
{
    [Fact]
    public async Task ReturnsFullDetail_WithTypesIndustriesAndContacts()
    {
        var typeId = Guid.NewGuid();
        var indId  = Guid.NewGuid();

        var (repo, db) = Build.New(ctx =>
        {
            var s = Build.Supplier(ctx, name: "MedPlus Pharma", code: "MED001");
            ctx.SaveChanges();
            ctx.SupplierTypeMappings.Add(new SupplierTypeMapping
            {
                SupplierId = s.Id, LookupValueId = typeId, IsPrimary = true,
                AssignedBy = 1, AssignedAt = DateTime.UtcNow
            });
            ctx.SupplierIndustryMappings.Add(new SupplierIndustryMapping
            {
                SupplierId = s.Id, LookupValueId = indId, IsPrimary = true,
                AssignedBy = 1, AssignedAt = DateTime.UtcNow
            });
            ctx.SupplierContacts.Add(new SupplierContact
            {
                SupplierId = s.Id, ContactName = "John Doe", IsPrimary = true, IsActive = true
            });
        });

        var supplier = await db.Suppliers.SingleAsync();
        var detail = await repo.GetSupplierByIdAsync(supplier.UUID);

        detail.Should().NotBeNull();
        detail!.SupplierName.Should().Be("MedPlus Pharma");
        detail.SupplierTypes.Should().HaveCount(1)
            .And.Contain(t => t.LookupValueId == typeId && t.IsPrimary);
        detail.Industries.Should().HaveCount(1)
            .And.Contain(i => i.LookupValueId == indId);
        detail.Contacts.Should().HaveCount(1)
            .And.Contain(c => c.ContactName == "John Doe");
    }

    [Fact]
    public async Task ReturnsNull_ForSoftDeletedSupplier()
    {
        var (repo, db) = Build.New(ctx => Build.Supplier(ctx, isDelete: true));

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        var result = await repo.GetSupplierByIdAsync(uuid);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExcludesInactiveContacts()
    {
        var (repo, db) = Build.New(ctx =>
        {
            var s = Build.Supplier(ctx);
            ctx.SaveChanges();
            ctx.SupplierContacts.Add(new SupplierContact
                { SupplierId = s.Id, ContactName = "Active", IsActive = true, IsPrimary = true });
            ctx.SupplierContacts.Add(new SupplierContact
                { SupplierId = s.Id, ContactName = "Inactive", IsActive = false, IsPrimary = false });
        });

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        var detail = await repo.GetSupplierByIdAsync(uuid);

        detail!.Contacts.Should().HaveCount(1)
            .And.Contain(c => c.ContactName == "Active");
    }
}

// ── GetSuppliersAsync ──────────────────────────────────────────────────────────

public class GetSuppliersAsync_Tests
{
    [Fact]
    public async Task FilterByStatus_ReturnsMatchingOnly()
    {
        var (repo, _) = Build.New(ctx =>
        {
            Build.Supplier(ctx, name: "Active Co",  code: "ACT001", status: "ACTIVE");
            Build.Supplier(ctx, name: "Pending Co", code: "PND001", status: "PENDING");
        });

        var result = await repo.GetSuppliersAsync(new SupplierListFilter { Status = "ACTIVE" });

        result.Data.Should().HaveCount(1);
        result.Data[0].SupplierName.Should().Be("Active Co");
    }

    [Fact]
    public async Task FilterBySupplierType_ReturnsMatchingOnly()
    {
        var mfgId  = Guid.NewGuid();
        var tradeId = Guid.NewGuid();

        var (repo, db) = Build.New(ctx =>
        {
            var mfg   = Build.Supplier(ctx, name: "Manufacturer", code: "MFG001");
            var trade = Build.Supplier(ctx, name: "Trader",       code: "TRD001");
            ctx.SaveChanges();
            ctx.SupplierTypeMappings.Add(new SupplierTypeMapping
                { SupplierId = mfg.Id,   LookupValueId = mfgId,   IsPrimary = true,  AssignedBy = 1, AssignedAt = DateTime.UtcNow });
            ctx.SupplierTypeMappings.Add(new SupplierTypeMapping
                { SupplierId = trade.Id, LookupValueId = tradeId, IsPrimary = true,  AssignedBy = 1, AssignedAt = DateTime.UtcNow });
        });

        var result = await repo.GetSuppliersAsync(new SupplierListFilter { SupplierType = mfgId });

        result.Data.Should().HaveCount(1);
        result.Data[0].SupplierName.Should().Be("Manufacturer");
    }

    [Fact]
    public async Task SearchByName_IsCaseInsensitive()
    {
        var (repo, _) = Build.New(ctx =>
        {
            Build.Supplier(ctx, name: "MedPlus Pharma", code: "MED001");
            Build.Supplier(ctx, name: "Acme Corp",      code: "ACM001");
        });

        var result = await repo.GetSuppliersAsync(new SupplierListFilter { Search = "medplus" });

        result.Data.Should().HaveCount(1);
        result.Data[0].SupplierName.Should().Be("MedPlus Pharma");
    }

    [Fact]
    public async Task SearchByCode_IsCaseInsensitive()
    {
        var (repo, _) = Build.New(ctx =>
        {
            Build.Supplier(ctx, name: "Alpha Co", code: "ALPHA1");
            Build.Supplier(ctx, name: "Beta Co",  code: "BETA01");
        });

        var result = await repo.GetSuppliersAsync(new SupplierListFilter { Search = "alpha" });

        result.Data.Should().HaveCount(1);
        result.Data[0].SupplierCode.Should().Be("ALPHA1");
    }

    [Fact]
    public async Task Pagination_ReturnsCorrectPage()
    {
        var (repo, _) = Build.New(ctx =>
        {
            for (int i = 1; i <= 25; i++)
                Build.Supplier(ctx, name: $"Supplier {i:D2}", code: $"S{i:D3}");
        });

        var page1 = await repo.GetSuppliersAsync(new SupplierListFilter { Page = 1, PageSize = 10 });
        var page2 = await repo.GetSuppliersAsync(new SupplierListFilter { Page = 2, PageSize = 10 });
        var page3 = await repo.GetSuppliersAsync(new SupplierListFilter { Page = 3, PageSize = 10 });

        page1.Data.Should().HaveCount(10);
        page2.Data.Should().HaveCount(10);
        page3.Data.Should().HaveCount(5);
        page1.TotalRecords.Should().Be(25);
        page1.TotalPages.Should().Be(3);
    }

    [Fact]
    public async Task ExcludesSoftDeletedSuppliers()
    {
        var (repo, _) = Build.New(ctx =>
        {
            Build.Supplier(ctx, name: "Visible",  code: "VIS001", isDelete: false);
            Build.Supplier(ctx, name: "Deleted",  code: "DEL001", isDelete: true);
        });

        var result = await repo.GetSuppliersAsync(new SupplierListFilter());

        result.Data.Should().HaveCount(1);
        result.Data[0].SupplierName.Should().Be("Visible");
    }

    [Fact]
    public async Task SupplierListItem_IncludesTypeAndIndustryIds()
    {
        var typeId = Guid.NewGuid();
        var indId  = Guid.NewGuid();

        var (repo, db) = Build.New(ctx =>
        {
            var s = Build.Supplier(ctx, code: "TYP001");
            ctx.SaveChanges();
            ctx.SupplierTypeMappings.Add(new SupplierTypeMapping
                { SupplierId = s.Id, LookupValueId = typeId, IsPrimary = true, AssignedBy = 1, AssignedAt = DateTime.UtcNow });
            ctx.SupplierIndustryMappings.Add(new SupplierIndustryMapping
                { SupplierId = s.Id, LookupValueId = indId,  IsPrimary = true, AssignedBy = 1, AssignedAt = DateTime.UtcNow });
        });

        var result = await repo.GetSuppliersAsync(new SupplierListFilter());

        var item = result.Data.Single();
        item.SupplierTypeIds.Should().Contain(typeId);
        item.IndustryIds.Should().Contain(indId);
    }
}

// ── PatchSupplierAsync ────────────────────────────────────────────────────────

public class PatchSupplierAsync_Tests
{
    [Fact]
    public async Task ReplacesTypeMappingsAtomically_OldDeleted_NewInserted()
    {
        var oldTypeId = Guid.NewGuid();
        var newTypeId = Guid.NewGuid();

        var (repo, db) = Build.New(ctx =>
        {
            var s = Build.Supplier(ctx, code: "PAT001");
            ctx.SaveChanges();
            ctx.SupplierTypeMappings.Add(new SupplierTypeMapping
                { SupplierId = s.Id, LookupValueId = oldTypeId, IsPrimary = true, AssignedBy = 1, AssignedAt = DateTime.UtcNow });
        });

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        var patched = await repo.PatchSupplierAsync(uuid, new PatchSupplierRequest
        {
            SupplierTypeIds =
            [
                new SupplierTypeMappingInput { LookupValueId = newTypeId, IsPrimary = true }
            ]
        }, modifiedBy: 2);

        patched.Should().BeTrue();

        var mappings = await db.SupplierTypeMappings.ToListAsync();
        mappings.Should().HaveCount(1);
        mappings[0].LookupValueId.Should().Be(newTypeId);
        mappings.Should().NotContain(m => m.LookupValueId == oldTypeId);
    }

    [Fact]
    public async Task UpdatesScalarFields_LeavesOthersUnchanged()
    {
        var (repo, db) = Build.New(ctx => Build.Supplier(ctx, name: "Old Name", code: "UPD001"));

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        await repo.PatchSupplierAsync(uuid, new PatchSupplierRequest
        {
            SupplierName = "New Name",
            City = "Lahore"
        }, modifiedBy: 1);

        var s = await db.Suppliers.SingleAsync();
        s.SupplierName.Should().Be("New Name");
        s.City.Should().Be("Lahore");
        s.SupplierCode.Should().Be("UPD001");  // unchanged
    }

    [Fact]
    public async Task ReturnsFalse_ForNonExistentSupplier()
    {
        var (repo, _) = Build.New();

        var result = await repo.PatchSupplierAsync(Guid.NewGuid(), new PatchSupplierRequest(), modifiedBy: 1);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task WhenTypeIdsNull_PreservesExistingMappings()
    {
        var typeId = Guid.NewGuid();

        var (repo, db) = Build.New(ctx =>
        {
            var s = Build.Supplier(ctx, code: "PRE001");
            ctx.SaveChanges();
            ctx.SupplierTypeMappings.Add(new SupplierTypeMapping
                { SupplierId = s.Id, LookupValueId = typeId, IsPrimary = true, AssignedBy = 1, AssignedAt = DateTime.UtcNow });
        });

        var uuid = (await db.Suppliers.SingleAsync()).UUID;

        // Patch with SupplierTypeIds = null (not supplied) — existing mappings must survive
        await repo.PatchSupplierAsync(uuid, new PatchSupplierRequest
        {
            SupplierName = "Updated Name",
            SupplierTypeIds = null
        }, modifiedBy: 1);

        var mappings = await db.SupplierTypeMappings.ToListAsync();
        mappings.Should().HaveCount(1);
        mappings[0].LookupValueId.Should().Be(typeId);
    }
}

// ── AddContactAsync ───────────────────────────────────────────────────────────

public class AddContactAsync_Tests
{
    [Fact]
    public async Task AddsContact_AndReturnsId()
    {
        var (repo, db) = Build.New(ctx => Build.Supplier(ctx, code: "CON001"));

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        var contactId = await repo.AddContactAsync(uuid, new AddContactRequest
        {
            ContactName = "Jane Smith",
            Title = "Procurement Lead",
            Email = "jane@example.com",
            Phone = "+1234567890",
            IsPrimary = true
        });

        contactId.Should().BeGreaterThan(0);

        var contact = await db.SupplierContacts.SingleAsync();
        contact.ContactName.Should().Be("Jane Smith");
        contact.IsPrimary.Should().BeTrue();
        contact.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ThrowsNotFoundException_ForMissingSupplier()
    {
        var (repo, _) = Build.New();

        var act = () => repo.AddContactAsync(Guid.NewGuid(), new AddContactRequest { ContactName = "X" });

        await act.Should().ThrowAsync<NotFoundException>();
    }
}

// ── Status state machine ──────────────────────────────────────────────────────

public class StatusStateMachine_Tests
{
    [Fact]
    public async Task Approve_FromPending_SetsActiveAndRecordsApprovedBy()
    {
        var (repo, db) = Build.New(ctx => Build.Supplier(ctx, code: "APV001", status: "PENDING"));

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        var (success, returnedUuid, id) = await repo.ApproveSupplierAsync(uuid, approvedBy: 42);

        success.Should().BeTrue();
        returnedUuid.Should().Be(uuid);
        id.Should().BeGreaterThan(0);

        var s = await db.Suppliers.SingleAsync();
        s.Status.Should().Be("ACTIVE");
        s.ApprovedBy.Should().Be(42);
        s.OnboardingDate.Should().NotBeNull();
    }

    [Fact]
    public async Task Reject_FromPending_SetsRejectedAndReason()
    {
        var (repo, db) = Build.New(ctx => Build.Supplier(ctx, code: "REJ001", status: "PENDING"));

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        var (success, _, _) = await repo.RejectSupplierAsync(uuid, "Credit risk", changedBy: 5);

        success.Should().BeTrue();

        var s = await db.Suppliers.SingleAsync();
        s.Status.Should().Be("REJECTED");
        s.RejectedReason.Should().Be("Credit risk");
        s.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Blacklist_FromActive_SetsBlacklistedStatus()
    {
        var (repo, db) = Build.New(ctx => Build.Supplier(ctx, code: "BLK001", status: "ACTIVE"));

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        await repo.BlacklistSupplierAsync(uuid, "Fraud detected", changedBy: 1);

        var s = await db.Suppliers.SingleAsync();
        s.Status.Should().Be("BLACKLISTED");
        s.BlacklistedReason.Should().Be("Fraud detected");
        s.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Suspend_FromActive_SetsSuspendedWithReviewDate()
    {
        var reviewDate = DateTime.UtcNow.AddDays(30);
        var (repo, db) = Build.New(ctx => Build.Supplier(ctx, code: "SUS001", status: "ACTIVE"));

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        await repo.SuspendSupplierAsync(uuid, "Payment overdue", reviewDate, changedBy: 3);

        var s = await db.Suppliers.SingleAsync();
        s.Status.Should().Be("SUSPENDED");
        s.SuspendedReason.Should().Be("Payment overdue");
        s.SuspendedReviewDate.Should().BeCloseTo(reviewDate, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Approve_FromRejected_ThrowsBadRequestException()
    {
        var (repo, db) = Build.New(ctx => Build.Supplier(ctx, code: "INV001", status: "REJECTED"));

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        var act = () => repo.ApproveSupplierAsync(uuid, approvedBy: 1);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*REJECTED*ACTIVE*");
    }

    [Fact]
    public async Task Suspend_FromPending_ThrowsBadRequestException()
    {
        var (repo, db) = Build.New(ctx => Build.Supplier(ctx, code: "INV002", status: "PENDING"));

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        var act = () => repo.SuspendSupplierAsync(uuid, "reason", null, changedBy: 1);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*PENDING*SUSPENDED*");
    }

    [Fact]
    public async Task Approve_FromSuspended_Succeeds()
    {
        var (repo, db) = Build.New(ctx => Build.Supplier(ctx, code: "SUS002", status: "SUSPENDED"));

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        var (success, _, _) = await repo.ApproveSupplierAsync(uuid, approvedBy: 1);

        success.Should().BeTrue();
        var s = await db.Suppliers.SingleAsync();
        s.Status.Should().Be("ACTIVE");
    }
}

// ── Bank details ──────────────────────────────────────────────────────────────

public class BankDetail_Tests
{
    [Fact]
    public async Task Upsert_Creates_AndEncryptsFields()
    {
        var (repo, db) = Build.New(ctx => Build.Supplier(ctx, code: "BNK001"));

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        await repo.UpsertBankDetailAsync(uuid, new UpsertBankDetailRequest
        {
            BankName      = "Meezan Bank",
            BankAccountNo = "12345678",
            BankIban      = "PK36MZNB",
            BankSwift     = "MZNBPKKA"
        }, userId: 1);

        var bd = await db.SupplierBankDetails.SingleAsync();
        bd.BankName.Should().Be("Meezan Bank");
        bd.BankAccountNo.Should().Be("ENC:12345678");  // FakeEncryption adds "ENC:" prefix
        bd.BankIban.Should().Be("ENC:PK36MZNB");
        bd.BankSwift.Should().Be("ENC:MZNBPKKA");
    }

    [Fact]
    public async Task Get_DecryptsFields()
    {
        var (repo, db) = Build.New(ctx => Build.Supplier(ctx, code: "BNK002"));

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        await repo.UpsertBankDetailAsync(uuid, new UpsertBankDetailRequest
        {
            BankName      = "HBL",
            BankAccountNo = "99887766",
            BankIban      = "PK36HBL1",
            BankSwift     = "HBLIPKKA"
        }, userId: 1);

        var model = await repo.GetBankDetailAsync(uuid);

        model.Should().NotBeNull();
        model!.BankName.Should().Be("HBL");
        model.BankAccountNo.Should().Be("99887766");   // decrypted by FakeEncryption
        model.BankIban.Should().Be("PK36HBL1");
        model.BankSwift.Should().Be("HBLIPKKA");
    }

    [Fact]
    public async Task Upsert_WhenRecordExists_Updates()
    {
        var (repo, db) = Build.New(ctx => Build.Supplier(ctx, code: "BNK003"));

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        await repo.UpsertBankDetailAsync(uuid, new UpsertBankDetailRequest { BankName = "First Bank" }, userId: 1);
        await repo.UpsertBankDetailAsync(uuid, new UpsertBankDetailRequest { BankName = "Updated Bank" }, userId: 2);

        var count = await db.SupplierBankDetails.CountAsync();
        count.Should().Be(1);  // no duplicate rows

        var bd = await db.SupplierBankDetails.SingleAsync();
        bd.BankName.Should().Be("Updated Bank");
        bd.UpdatedBy.Should().Be(2);
    }
}

// ── Documents ─────────────────────────────────────────────────────────────────

public class Document_Tests
{
    [Fact]
    public async Task AttachDocument_Returns_Id()
    {
        var (repo, db) = Build.New(ctx => Build.Supplier(ctx, code: "DOC001"));

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        var docId = await repo.AttachDocumentAsync(uuid, new AttachDocumentRequest
        {
            FileName     = "contract.pdf",
            FileUrl      = "https://storage.example.com/contract.pdf",
            DocumentType = "CONTRACT"
        }, userId: 1);

        docId.Should().BeGreaterThan(0);
        var doc = await db.SupplierDocuments.SingleAsync();
        doc.FileName.Should().Be("contract.pdf");
        doc.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetDocuments_ReturnsActiveOnly()
    {
        var (repo, db) = Build.New(ctx =>
        {
            var s = Build.Supplier(ctx, code: "DOC002");
            ctx.SaveChanges();
            ctx.SupplierDocuments.Add(new SupplierDocument
                { SupplierId = s.Id, FileName = "active.pdf",   FileUrl = "url1", UploadedAt = DateTime.UtcNow, UploadedBy = 1, IsActive = true  });
            ctx.SupplierDocuments.Add(new SupplierDocument
                { SupplierId = s.Id, FileName = "deleted.pdf",  FileUrl = "url2", UploadedAt = DateTime.UtcNow, UploadedBy = 1, IsActive = false });
        });

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        var docs = await repo.GetDocumentsAsync(uuid);

        docs.Should().HaveCount(1);
        docs[0].FileName.Should().Be("active.pdf");
    }

    [Fact]
    public async Task SoftDelete_SetsIsActiveFalse()
    {
        int docId = 0;
        var (repo, db) = Build.New(ctx =>
        {
            var s = Build.Supplier(ctx, code: "DOC003");
            ctx.SaveChanges();
            var doc = new SupplierDocument
                { SupplierId = s.Id, FileName = "todelete.pdf", FileUrl = "url", UploadedAt = DateTime.UtcNow, UploadedBy = 1, IsActive = true };
            ctx.SupplierDocuments.Add(doc);
            ctx.SaveChanges();
            docId = doc.Id;
        });

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        var result = await repo.SoftDeleteDocumentAsync(uuid, docId);

        result.Should().BeTrue();
        var doc = await db.SupplierDocuments.SingleAsync();
        doc.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task SoftDelete_ReturnsFalse_ForMissingDoc()
    {
        var (repo, db) = Build.New(ctx => Build.Supplier(ctx, code: "DOC004"));

        var uuid = (await db.Suppliers.SingleAsync()).UUID;
        var result = await repo.SoftDeleteDocumentAsync(uuid, docId: 9999);

        result.Should().BeFalse();
    }
}

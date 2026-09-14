using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Suppliers.Controllers;

[ApiController]
[Route("api/suppliers")]
//[Authorize]
[RequiresFeature("MODULE_SUPPLIERS")]
public class SuppliersController : ControllerBase
{
    private readonly ISuppliersService _service;
    private readonly IWebHostEnvironment _env;

    public SuppliersController(ISuppliersService service, IWebHostEnvironment env)
    {
        _service = service;
        _env = env;
    }

    // ── Full supplier CRUD ────────────────────────────────────────────────────

    [HttpPost]
//   [RequirePermission(PermissionCodes.SUPPLIER_CREATE)]
    public async Task<IActionResult> CreateSupplier([FromBody] CreateSupplierRequest req)
    {
        var userId = User.GetUserId();
        var uuid = await _service.CreateSupplierAsync(req, userId);
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpGet]
  //  [RequirePermission(PermissionCodes.SUPPLIER_VIEW)]
    public async Task<IActionResult> GetSuppliers(
        [FromQuery] string? status,
        [FromQuery] Guid? supplierType,
        [FromQuery] Guid? industryCategory,
        [FromQuery] string? country,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var filter = new SupplierListFilter
        {
            Status = status,
            SupplierType = supplierType,
            IndustryCategory = industryCategory,
            Country = country,
            Search = search,
            Page = page,
            PageSize = pageSize
        };
        var result = await _service.GetSuppliersAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<SupplierListItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
   // [RequirePermission(PermissionCodes.SUPPLIER_VIEW)]
    [RequiresSupplierAccess]
    public async Task<IActionResult> GetSupplierById(Guid uuid)
    {
        var detail = await _service.GetSupplierByIdAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SupplierDetailModel>.Ok(detail));
    }

    [HttpPatch("{uuid:guid}")]
   // [RequirePermission(PermissionCodes.SUPPLIER_EDIT)]
    [RequiresSupplierAccess]
    public async Task<IActionResult> PatchSupplier(Guid uuid, [FromBody] PatchSupplierRequest req)
    {
        var userId = User.GetUserId();
        var updated = await _service.PatchSupplierAsync(uuid, req, userId);
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPost("{uuid:guid}/contacts")]
   // [RequirePermission(PermissionCodes.SUPPLIER_EDIT)]
    [RequiresSupplierAccess]
    public async Task<IActionResult> AddContact(Guid uuid, [FromBody] AddContactRequest req)
    {
        var contactId = await _service.AddContactAsync(uuid, req);
        return Ok(ApiResponse<int>.Ok(contactId, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPatch("{uuid:guid}/contacts/{contactId:int}")]
    [RequirePermission(PermissionCodes.SUPPLIER_EDIT)]
    [RequiresSupplierAccess]
    public async Task<IActionResult> UpdateContact(Guid uuid, int contactId, [FromBody] PatchContactRequest req)
    {
        var updated = await _service.UpdateContactAsync(uuid, contactId, req);
        return updated
            ? Ok(ApiResponse.Ok("Contact updated."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpGet("{uuid:guid}/contacts/eligible")]
    [RequirePermission(PermissionCodes.RFQ_CREATE)]
    [RequiresSupplierAccess]
    public async Task<IActionResult> GetEligibleContacts(Guid uuid)
    {
        var result = await _service.GetEligibleContactsAsync(uuid);
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<EligibleContactsResponse>.Ok(result));
    }

    // ── Status state machine ──────────────────────────────────────────────────

    [HttpPost("{uuid:guid}/approve")]
   // [RequirePermission(PermissionCodes.SUPPLIER_MANAGE)]
    [RequiresSupplierAccess]
    public async Task<IActionResult> ApproveSupplier(Guid uuid)
    {
        var userId = User.GetUserId();
        var (success, _, _) = await _service.ApproveSupplierAsync(uuid, userId);
        return success
            ? Ok(ApiResponse.Ok("Supplier approved successfully."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPost("{uuid:guid}/reject")]
   //[RequirePermission(PermissionCodes.SUPPLIER_MANAGE)]
    [RequiresSupplierAccess]
    public async Task<IActionResult> RejectSupplier(Guid uuid, [FromBody] RejectSupplierRequest req)
    {
        var userId = User.GetUserId();
        var (success, _, _) = await _service.RejectSupplierAsync(uuid, req.Reason, userId);
        return success
            ? Ok(ApiResponse.Ok("Supplier rejected."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPost("{uuid:guid}/blacklist")]
   //[RequirePermission(PermissionCodes.SUPPLIER_MANAGE)]
    [RequiresSupplierAccess]
    public async Task<IActionResult> BlacklistSupplier(Guid uuid, [FromBody] BlacklistSupplierRequest req)
    {
        var userId = User.GetUserId();
        await _service.BlacklistSupplierAsync(uuid, req.Reason, userId);
        return Ok(ApiResponse.Ok("Supplier blacklisted."));
    }

    [HttpPost("{uuid:guid}/suspend")]
    //[RequirePermission(PermissionCodes.SUPPLIER_MANAGE)]
    [RequiresSupplierAccess]
    public async Task<IActionResult> SuspendSupplier(Guid uuid, [FromBody] SuspendSupplierRequest req)
    {
        var userId = User.GetUserId();
        await _service.SuspendSupplierAsync(uuid, req.Reason, req.ReviewDate, userId);
        return Ok(ApiResponse.Ok("Supplier suspended."));
    }

    [HttpDelete("{uuid:guid}")]
    //[RequirePermission(PermissionCodes.SUPPLIER_MANAGE)]
    [RequiresSupplierAccess]
    public async Task<IActionResult> DeleteSupplier(Guid uuid)
    {
        var deleted = await _service.DeleteSupplierAsync(uuid, User.GetUserId());
        return deleted
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    // ── Bank details ──────────────────────────────────────────────────────────

    [HttpPost("{uuid:guid}/bank-details")]
   // [RequirePermission(PermissionCodes.SUPPLIER_MANAGE)]
    [RequiresSupplierAccess]
    public async Task<IActionResult> UpsertBankDetail(Guid uuid, [FromBody] UpsertBankDetailRequest req)
    {
        var userId = User.GetUserId();
        await _service.UpsertBankDetailAsync(uuid, req, userId);
        return Ok(ApiResponse.Ok("Bank details saved."));
    }

    [HttpGet("{uuid:guid}/bank-details")]
    [RequiresSupplierAccess]
    public async Task<IActionResult> GetBankDetail(Guid uuid)
    {
        if (!User.HasPermission(PermissionCodes.SUPPLIER_MANAGE) &&
            !User.HasPermission(PermissionCodes.INVOICE_VIEW))
            return Forbid();

        var detail = await _service.GetBankDetailAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<BankDetailModel>.Ok(detail));
    }

    // ── Documents ─────────────────────────────────────────────────────────────

    [HttpPost("{uuid:guid}/documents")]
   // [RequirePermission(PermissionCodes.SUPPLIER_EDIT)]
    [RequiresSupplierAccess]
    public async Task<IActionResult> AttachDocument(Guid uuid, [FromBody] AttachDocumentRequest req)
    {
        var userId = User.GetUserId();
        var docId = await _service.AttachDocumentAsync(uuid, req, userId);
        return Ok(ApiResponse<int>.Ok(docId, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPost("{uuid:guid}/documents/upload")]
    [Consumes("multipart/form-data")]
   // [RequirePermission(PermissionCodes.SUPPLIER_EDIT)]
    [RequiresSupplierAccess]
    public async Task<IActionResult> UploadDocument(Guid uuid, IFormFile file, [FromForm] string? documentType)
    {
        if (file == null || file.Length == 0)
            return BadRequest(ApiResponse.Fail("No file was provided."));

        const long maxBytes = 20 * 1024 * 1024; // 20 MB
        if (file.Length > maxBytes)
            return BadRequest(ApiResponse.Fail("File size must not exceed 20 MB."));

        var uploadsDir = Path.Combine(_env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
                                      "uploads", "supplier-docs");
        Directory.CreateDirectory(uploadsDir);

        var ext      = Path.GetExtension(file.FileName);
        var safeName = $"{Guid.NewGuid()}{ext}";
        var filePath = Path.Combine(uploadsDir, safeName);

        await using (var stream = new FileStream(filePath, FileMode.Create))
            await file.CopyToAsync(stream);

        var req = new AttachDocumentRequest
        {
            FileName     = file.FileName,
            FileUrl      = $"/uploads/supplier-docs/{safeName}",
            DocumentType = documentType
        };

        var userId = User.GetUserId();
        var docId  = await _service.AttachDocumentAsync(uuid, req, userId);
        return Ok(ApiResponse<int>.Ok(docId, "Document uploaded successfully."));
    }

    [HttpGet("{uuid:guid}/documents")]
    //[RequirePermission(PermissionCodes.SUPPLIER_VIEW)]
    [RequiresSupplierAccess]
    public async Task<IActionResult> GetDocuments(Guid uuid)
    {
        var docs = await _service.GetDocumentsAsync(uuid);
        return Ok(ApiResponse<List<DocumentModel>>.Ok(docs));
    }

    [HttpDelete("{uuid:guid}/documents/{docId:int}")]
    //[RequirePermission(PermissionCodes.SUPPLIER_EDIT)]
    [RequiresSupplierAccess]
    public async Task<IActionResult> SoftDeleteDocument(Guid uuid, int docId)
    {
        var deleted = await _service.SoftDeleteDocumentAsync(uuid, docId);
        return deleted
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    // ── Legacy dropdown endpoints ─────────────────────────────────────────────

    [HttpGet("types")]
    public IActionResult GetSupplierTypes() => Ok(ApiResponse<object>.Ok(_service.GetSupplierTypes()));

    [HttpGet("categories")]
    public IActionResult GetCategories() => Ok(ApiResponse<object>.Ok(_service.GetCategories()));

    [HttpPost("types")]
    public IActionResult CreateSupplierType([FromBody] CreateSupplierTypeRequest req) =>
        Ok(ApiResponse<Guid>.Ok(_service.CreateSupplierType(req), StaticResponseMessage.recordCreatedSuccessfully));

 //   [HttpPost("categories")]
    //public IActionResult CreateCategory([FromBody] CreateCategoryRequest req) =>
    //    Ok(ApiResponse<Guid>.Ok(_service.CreateCategory(req), StaticResponseMessage.recordCreatedSuccessfully));

    [HttpPut("types/{id:guid}")]
    public IActionResult UpdateSupplierType(Guid id, [FromBody] CreateSupplierTypeRequest req) =>
        _service.UpdateSupplierType(id, req)
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpPut("categories/{id:guid}")]
    //public IActionResult UpdateCategory(Guid id, [FromBody] CreateCategoryRequest req) =>
    //    _service.UpdateCategory(id, req)
    //        ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
    //        : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpDelete("types/{id:guid}")]
    public IActionResult DeleteSupplierType(Guid id) =>
        _service.DeleteSupplierType(id)
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpDelete("categories/{id:guid}")]
    public IActionResult DeleteSupplierCategory(Guid id) =>
        _service.DeleteSupplierCategory(id)
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
}

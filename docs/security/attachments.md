# Attachments and uploads: hardening status

Status: **paused by the user, 2026-10-02.** It will become a separate, later project. What is described as
*done* below is in the source, tested and green. What is *open* is not started, or exists only as a
skipped test.

Owner while active: the attachments-hardening agent. Security audit: a618a3dc5c083d459 (child of
a145f8aee8ced4c6b), whose tests live in `tests/SMS.WorkflowEngine.Tests/AttachmentsSecurityAuditTests.cs`.

## 1. The problem

`api/attachments` (`src/SMS.WorkflowEngine/Controllers/AttachmentsController.cs`) is generic: it is keyed
by interface code + document id. Before this work it asked only for a sign-in:

- any signed-in user could upload to any kind of document, a view-only Auditor included;
- anyone could list any document's files;
- anyone could delete any file, including documents the system generated and filed (issued sales-invoice
  PDFs, gate passes);
- every query relied on the EF tenant filter, which a super admin bypasses;
- the interface code became a folder name as it arrived, so `../..` or an absolute path wrote files anywhere
  the app pool could write;
- any extension was kept and served back from the API origin by `UseStaticFiles` (`.html`, `.svg`, `.hxt`…),
  which is stored XSS.

## 2. Done

### 2.1 Per-document policy: `src/SMS.WorkflowEngine/Services/AttachmentAccessPolicy.cs`

Each list is "any one of". Upload and remove are refused for a kind of document the policy does not name,
which is a 400. The codes are exact and case-sensitive.

| Interface code | View (list) | Upload | Remove anybody's | Remove own only | Derived from |
|---|---|---|---|---|---|
| `PR`, `PR_LINE` | REQUISITION_VIEW_OWN, _VIEW_ALL, _CREATE, _APPROVE | REQUISITION_CREATE, _APPROVE | REQUISITION_APPROVE | REQUISITION_CREATE | pr-detail / pr-create guards. VIEW_ALL is left out of the change columns because the Auditor holds it. |
| `QUOTATION` | RFQ_VIEW, RFQ_CREATE, RFQ_MANAGE | RFQ_CREATE, RFQ_MANAGE | RFQ_MANAGE | RFQ_CREATE | QuotationsController (create RFQ_CREATE; send/award/cancel RFQ_MANAGE) |
| `RFQ_RESPONSE` | RFQ_VIEW, RFQ_CREATE, RFQ_MANAGE | RFQ_MANAGE | RFQ_MANAGE | none | recording a response is RFQ_MANAGE; vendors upload through the public portal, not here |
| `PO` | PO_VIEW, PO_CREATE, PO_EDIT, PO_APPROVE | PO_CREATE, PO_EDIT | PO_EDIT | PO_CREATE | po-detail / po-create / po-edit guards |
| `PO_TEMPLATE_LOGO` | PO_TEMPLATE_MANAGE | PO_TEMPLATE_MANAGE | PO_TEMPLATE_MANAGE | none | PoDocumentTemplateController |
| `GRN` | GOODS_RECEIVE, GRN_APPROVE, GRN_FINANCE_APPROVE, GRN_QC_CONFIRM | GOODS_RECEIVE, GRN_APPROVE | GRN_APPROVE | GOODS_RECEIVE | grn-detail / grn-create / grn-edit guards |
| `DELIVERY` | DELIVERY_VIEW, DELIVERY_EDIT | DELIVERY_EDIT | DELIVERY_EDIT | none | DeliveriesController |
| `INVOICE` | INVOICE_VIEW, INVOICE_PROCESS | INVOICE_PROCESS | INVOICE_PROCESS | none | InvoicesController (confirmed with C') |
| `SUPPLIER_PAYMENT` | PAYMENT_VIEW, PAYMENT_PROCESS | PAYMENT_PROCESS | PAYMENT_PROCESS | none | SupplierPaymentsController (confirmed with C') |
| `SALES_INVOICE` | SALES_INVOICE_VIEW, SALES_INVOICE_MANAGE | nobody | nobody | nobody | filed copies are the system's alone |
| `PRODUCT_IMAGE` | INVENTORY_VIEW, STOCK_MANAGE | STOCK_MANAGE | STOCK_MANAGE | none | product routes; pictures only |

`PRODUCT_IMAGE` and `PO_TEMPLATE_LOGO` take pictures only (`AttachmentAccessPolicy.TakesPicturesOnly`),
because the app shows them straight from their public URL in an `<img>`.

How the controller enforces it:

- **Upload:** the rule is checked before anything touches the disk. The folder name comes from the rule's
  own code.
- **List:** the View rule. Each row carries `CanRemove`, computed per caller by `AttachmentAccessPolicy.MayRemove`.
- **Content:** the View rule of the file's kind, plus the permission it was filed with.
- **Delete:** 404 when the file is missing or belongs to another organization; 409 for a filed document,
  whoever asks; then `MayRemove`, else 403.

`GET api/attachments/policy` returns the rules to the frontend.

Tests that prove it:

- `AttachmentAccessPolicyTests` pins the table. It also walks every seeded role (`AuthDataSeeder.RolePermissionSeed`
  by reflection) and checks two things against the page guards copied from `pages.routes.ts`: whoever can open
  a page still sees its files, and whoever edits the document can still add and remove.
- `AttachmentEndpointAccessTests` drives the real controller and service over an in-memory database.

### 2.2 Filed documents cannot be removed

A file with a `DocumentAttachmentContents` row (stored by `StoreGeneratedAsync`) is refused by:

- `AttachmentService.DeleteAsync`, with `ConflictException`, for every caller;
- the controller, with 409 and the message "This document was generated and filed by the system, so it
  cannot be removed…".

No route updates an attachment row in place. Every upload is a new row and a new GUID file, written with
`FileMode.CreateNew`.

### 2.3 Tenancy

`AttachmentService.OwnAttachments()` and `OwnContents()` add an explicit `OrganizationId == TenantContext.OrganizationId`.
Every read, delete, count and the `StoreGeneratedAsync` dedupe go through them. Another organization's file
is a 404 for everyone, super admins included, because super admins bypass the EF filter.

### 2.4 Upload hygiene

`src/SMS.Shared/Files/UploadRules.cs` is the shared allow-list. It is complete and tested in
`tests/SMS.WorkflowEngine.Tests/UploadRulesTests.cs`, which walks `FileExtensionContentTypeProvider.Mappings`.

- **Accepted:** pdf; png jpg jpeg gif webp bmp tif tiff heic; doc docx xls xlsx ppt pptx odt ods odp rtf;
  csv txt msg; zip 7z rar; dwg dxf.
- **`picturesOnly`:** png, jpg, jpeg, gif, webp, bmp.
- **Content type:** an active client content type (html, xhtml, svg, xml or any `+xml`, js, xsl, htc, hta,
  rfc822, flash) is refused whatever the name. The type that is recorded comes from the extension.
- **Names on disk and on screen:** the disk name is a GUID plus `DiskExtension()`. `DisplayName()` reduces a
  name to a safe leaf, cut to 255 characters with the extension kept.
- **Size:** 20 MB. The request limit is 20 MB + 64 KB, so the multipart envelope no longer cuts off a file
  of exactly 20 MB.
- **Notes:** more than 300 characters is a 400 before anything is written.

`AttachmentsController.Upload` uses it, and so does `AttachmentService.CreateAsync` (name cleaning, length
checks and the interface-code whitelist).

### 2.5 Frontend

`app-attachment-list` gates itself. Pages needed no edits.

- **Upload control:** shown only when the page is not `[readOnly]`, the rule has loaded, and
  `AuthService.hasPermission` holds one of the rule's upload codes.
- **Remove control:** shown only for `att.canRemove` from the server, never for `isGenerated`, never when
  read-only or before the rule loads.
- **Policy failure:** read-only (`AttachmentPolicyService`, `src/app/services/attachment-policy.service.ts`).
  The rules are fetched once per session; a failed fetch is not cached.
- **Hardening:**
  - the remove-confirmation escapes the file name;
  - a fetched file is opened in a tab only as pdf, an image or plain text, otherwise as a download, so a blob
    URL never renders a page in the app's origin.

### 2.6 Security audit ledger (a618a3dc5c083d459)

Tests are in `AttachmentsSecurityAuditTests` unless another file is named.

| # | Severity | Finding | Status | Test |
|---|---|---|---|---|
| 1 | Critical | Path traversal / arbitrary-directory write via `interfaceCode` | Fixed | `Audit_a_document_type_shaped_like_a_path_*` |
| 2 | High | Active content (.html/.svg/.js) served from the API origin | Fixed | `Audit_a_web_page_or_script_is_refused_*` |
| 3 | Medium-high | Uploaded files are public static files | **Open** (§3.1) | skipped: `Audit_an_uploaded_file_is_served_only_through_the_checked_content_route_*` |
| 4 | High | No permission per action or type; Auditor could upload and delete; GetContent ignored the type | Fixed | `Audit_an_auditor_*`, `Audit_without_the_document_types_permission_*`, `Audit_an_unknown_document_type_*` |
| 5 | High | Super admin crossed organizations (list, download, delete, shared logo id) | Fixed | `Audit_a_super_admin_*` |
| 6 | Medium-high | Filed copies deletable | Fixed | `Audit_a_filed_copy_cannot_be_deleted_*` |
| 7 | Medium | A bad code gave a 500 that leaked the server path | Fixed | `Audit_a_bad_document_type_is_a_400_*` |
| 8 | Low-medium | Over-long metadata gave a SQL truncation 500 plus an orphan file | Fixed | `Audit_metadata_too_long_*` |
| 9 | Low | Display name stored raw | Fixed | `Audit_an_uploaded_files_display_name_*` |
| 10 | High | The deny-list missed .hxt and the xml/+xml types; replaced by an allow-list | Fixed | `Audit_no_extension_the_static_file_middleware_serves_*` |
| 11 | Medium | A Requester could delete anyone's PR file | Fixed (DeleteOwn); draft lock and VIEW_OWN **open** (§3.4, §3.5) | `Audit_a_requester_cannot_remove_*` |
| 12 | High | The anonymous RFQ portal upload applies no rules | **Open** (§3.2) | skipped: `SMS.Modules.Demand.Tests/RfqPortalAttachmentSecurityAuditTests` |

A known trade-off: DELETE answers 409 for a filed copy before checking the caller's permission on that
kind. This tells a same-organization caller who knows the uuid that it is a filed copy. Judged negligible.

The frontend gating (§2.5) is covered by Karma specs but was not audited before the pause.

## 3. Open (how to resume)

1. **Uploaded files are public static files.** This is the auditor's finding 3. Downloading one needs no
   sign-in, organization or permission; the GUID is the only protection.
   - Plan: write uploads, except `PRODUCT_IMAGE` and `PO_TEMPLATE_LOGO`, to a non-public folder under the
     content root (App_Data), named by attachment uuid, and set `FileUrl = /api/attachments/{uuid}/content`.
   - Serve them through `GetContent`: the View rule, the organization filter, `nosniff`, `no-store`, and
     `Content-Disposition: attachment`.
   - No schema change is needed. Existing `/uploads/...` rows stay legacy.
   - The attachment panel already fetches `/api/` URLs with the token.
   - Skipped test: `AttachmentsSecurityAuditTests.Audit_an_uploaded_file_is_served_only_through_the_checked_content_route_not_as_a_public_static_file`.
2. **Other upload paths must adopt `SMS.Shared.Files.UploadRules`.** Each owner has the API; none had adopted
   it when the work was paused.
   - `SMS.Modules.Finance` `InvoicesController.UploadAttachment` (supplier-invoice reviewer C').
   - `SMS.Modules.Suppliers` `SuppliersController.UploadDocument` (sweep child a6b63f5b4023fba9e).
   - `SMS.Modules.Demand` `RfqPortalController.UploadAttachment`, the anonymous portal, and the SRO portal
     if it uploads (public-portals agent a9300144ac55b2706).
   - The change for each: call `UploadRules.Refusal(...)` before any disk write; store
     `{Guid}{UploadRules.DiskExtension(name)}` with `FileMode.CreateNew`; record `UploadRules.ContentTypeOf(name)`.
   - Failing test waiting for this: `tests/SMS.Modules.Demand.Tests/RfqPortalAttachmentSecurityAuditTests.cs`.
   - Two more holes in `SuppliersController`, reported by a6b63f5b4023fba9e, which made no change before the pause:
     - `AttachDocument` (JSON) stores a client-supplied `FileUrl` (any scheme, or a same-origin relative path)
       and a raw `FileName`;
     - `UploadDocument` does not length-check `DocumentType` (column max 100) before the file is written.
3. **RFQ portal rows are stamped with the SCM-DEMO organization.** An anonymous request has no `organizationId`
   claim, so `TenantContext` falls back to it. The rows are then invisible to the RFQ's real organization.
   The public-portals agent planned to fix this with an ambient `HangfireTenantScope` per request.
4. **Files are not locked once a document leaves draft.** Anyone with the change permission can still add
   or remove files on an approved PO, a posted GRN, and so on. This needs a status lookup per module. Pages
   already pass `[readOnly]` in some cases, for example a cancelled supplier payment.
5. **`REQUISITION_VIEW_OWN` lists the attachments of anybody's requisition.** It needs a PR-ownership lookup
   in Demand.
6. **Some pages upload directly and show their button to viewers.** product-detail and po-document-template
   call `AttachmentService.upload` themselves and show the button to anyone who can open the page. The
   server now refuses viewers with 403, but the button is still there. Hide it with
   `AttachmentPolicyService.ruleFor(...)` plus `hasPermission`.
7. **The document controllers themselves.** PO, PR, GRN, quotations PATCH and products were assigned to the
   endpoint-permission sweep (aa7938f0a09d8aead). The attachment table above matches the codes that sweep
   reported.

Note for whoever resumes: the API migrates the shared SMSGlobal database at startup. None of this needed
an EF model change (`dotnet ef migrations has-pending-model-changes` reports none for SMS.WorkflowEngine).
Keep it that way, or coordinate first.

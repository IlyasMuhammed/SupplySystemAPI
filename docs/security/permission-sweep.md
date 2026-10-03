# Endpoint permission sweep (paused 2026-10-03)

## The hole
The global filter in `src/SMS.API/Program.cs` only requires a sign-in. An action with no `[RequirePermission]`, on its class or on itself, is open to every signed-in user of the organization, whatever their role.

On 2026-10-02 the real host had **257 such actions** (appendix). A source scan found 261; the difference is `FilesController`, which runs in the separate SMS.FileStore host.

The sweep was paused by user decision with most of the work still open. This file is what a later project needs to resume it.

## The rule
The server must require exactly what the frontend already requires to reach the page or button that calls the action. The frontend checks three things:
- route guards: `permissionGuard(...)` in `pages.routes.ts`;
- the menu: `permRequired` in `app.menu.ts`;
- buttons: `hasPermission` / `hasAnyPermission`.

If an endpoint is called from several pages (pickers on other modules' forms count), it admits the union of what those pages admit. Anyone who can open a page today already holds the permission it needs, so nobody loses access they actually use.

Two calls don't count toward the union: the dashboard (`dashboard.ts:633`, `safe()`) and the topbar search (`app.topbar.ts:915`, `catchError`). Both swallow a 403 and show nothing.

## Done and kept
| What | Where | Tests |
|---|---|---|
| **Any-of permissions.** `[RequirePermission(A, B, ...)]` means ANY of the codes. Several attributes still mean ALL of them, and a single code is unchanged. The policy name is `Permission:A\|B`; `.AnyOf` lists the codes and `RequirePermissionAttribute.CodesOf(name)` parses a name. | `src/SMS.Shared/Authorization/RequirePermissionAttribute.cs`; `src/SMS.Modules.Auth/Authorization/{PermissionPolicyProvider,PermissionRequirement,PermissionAuthorizationHandler}.cs` | `tests/SMS.Modules.Auth.Tests/PermissionPolicyAnyOfTests.cs` (7; watched 3 fail on the old provider) |
| **Ratchet.** Every signed-in action without a permission must appear on a reviewed list with a reason. The list can only shrink. | `tests/SMS.Integration.Tests/Security/UngatedEndpointsRatchetTests.cs` | 3 tests, green. Watched it fail while the list was empty, and again on a stale line. |
| **Walk helpers.** `SeededRoles` reads the seeder's grants by reflection. `FrontendAccess` describes each `FrontendEntry` (route or button → actions it calls) and provides `SeededRoleLockOuts` and `FrontendParityGaps`. | `tests/SMS.Integration.Tests/Security/{SeededRoles,FrontendAccess}.cs` | used by the workflow tests below |
| **`GET api/notifications/whatsapp-logs` now requires PLATFORM_SUPER_ADMIN.** The log table has no organization column, so the endpoint read every organization's dispatches. No page calls it. | `src/SMS.Modules.Notifications/Controllers/NotificationsController.cs` | `Security/WorkflowAndNotificationPermissionTests.cs` (15 + 1 skipped) and `...HttpTests.cs` (6 + 1 skipped); watched them fail first |
| **Finance, 7 controllers / 44 actions, gated by C''s child.** SupplierPayments, legacy Payments, SupplierLedger, MasterLedger, MasterProductLedger, CreditNotes, DebitNotes. | Finance controllers | `tests/SMS.Modules.Finance.Tests/Authorization/FinanceControllerPermissionTests.cs` |
| **AuthController user and role administration, gated by the auth-hardening work.** | AuthController | the auth-hardening tests |

Sign-in-only is reviewed and deliberate (the reasons are in the ratchet list) for:
- the user's own account (`me`, profile, password, picture) and `tenant/current`;
- the user's own notifications;
- workflow approve, reject, delegate and recall, plus the inbox, approval detail, history and timeline. The service refuses anyone who is not the step's assignee, or the initiator for recall, and workflows can name any user.
- Lookups, TaxCodes and ExchangeRates reads (pickers; writes are gated);
- the QuickBooks API-key gateway, which uses its own scheme and `[RequireApiScope]`;
- Attachments, where per-document codes are enforced in `AttachmentAccessPolicy`.

## Still open (162 actions; each is an `OPEN:` line in the ratchet)
| Area | Open actions | Mapping |
|---|---|---|
| Workflow raw API: `WorkflowSubmit.Submit`, `WorkflowApprovalActions.Cancel` and `Reissue` | 3 | WORKFLOW_ADMIN. The service checks nobody, and no page calls these. **Blocked:** `Workflow/WorkflowLifecycleTests` calls them with permission-less tokens and needs Docker, so its `JwtHelper`/`CreateClientFor` must first add a `permission` claim. The tests are written but skipped (`Skip = "Open: ..."`). |
| Demand and Warehouse: PurchaseOrders 12, Requisitions 9, Quotations 3, Grns 14, Sros 10 | 48 | §A below |
| Inventory: Products 26, Warehouses 19, Attributes 10, StockAdjustments 4, InventoryItems 2, InventoryLedger 1 | 62 | §B below |
| Suppliers and Reports: Suppliers 22, Partners 5, 3 alias controllers, Reports 19 | 49 | §C below |

**How to resume.** Work one controller at a time:
1. Copy that controller's drafts from the scratch folders below into its test project. They need `Microsoft.AspNetCore.TestHost` 8.0.13, and their host policy provider must use `CodesOf`.
2. Watch them fail.
3. Add the attributes.
4. Remove the controller's `OPEN` lines from the ratchet.
5. Add a `<Module>FrontendAccessTests` walk with `SeededRoleLockOuts(...)` and `FrontendParityGaps(...)`, both expected to be empty. Add an "every action is gated" fact too: an ungated action admits everyone, so a walk alone can't fail first.

**Do the frontend first** wherever a write button is gated by status only (listed under each section). Otherwise the server gate turns a visible button into a 403 and the parity walk fails.

No seeder changes are needed. Every seeded role that really uses an action keeps it under the mappings below.

Drafts:
- `scratchpad/perm-sweep/demand-warehouse/drafts/`: PO, PR and Quotation tests plus `ControllerHost.cs`. 36 of their 84 cases fail on today's code.
- `scratchpad/perm-sweep/inventory/draft-tests/`
- `tests/SMS.Modules.Suppliers.Tests/Permissions/SuppliersControllerPermissionTests.cs` (in the repo; 3 skipped).

### A. Demand and Warehouse
Page sets:
- **PO pages** = PO_VIEW/CREATE/EDIT/APPROVE (routes 409/415)
- **PR pages** = REQUISITION_VIEW_OWN/VIEW_ALL/CREATE/APPROVE (389/395)
- **GRN pages** = GOODS_RECEIVE/GRN_APPROVE/GRN_FINANCE_APPROVE/GRN_QC_CONFIRM (433/439)
- **SRO** = GOODS_RECEIVE/WAREHOUSE_TRANSFER

Pickers on other forms:
- quotation-create (RFQ_CREATE/MANAGE): PO list, PR list and detail
- po-create (PO_CREATE): PRs
- sro-create: PO, GRN
- invoice-create (INVOICE_PROCESS): PO, GRN
- shipment-create (DELIVERY_TRACK): PO list
- delivery-create (DELIVERY_CREATE): PO, SRO
- grn-create (GOODS_RECEIVE): PO search and detail

**PurchaseOrders**
| Action | Codes | Note |
|---|---|---|
| GetPurchaseOrders | PO pages + RFQ_CREATE, RFQ_MANAGE, GOODS_RECEIVE, WAREHOUSE_TRANSFER, INVOICE_PROCESS, DELIVERY_TRACK, DELIVERY_CREATE | |
| GetPurchaseOrderById | PO pages + GOODS_RECEIVE, WAREHOUSE_TRANSFER, INVOICE_PROCESS, DELIVERY_CREATE | |
| SearchPurchaseOrders | GOODS_RECEIVE | |
| DownloadPdf, GetTimeline | PO pages | |
| Create | PO_CREATE | |
| Update, Split | PO_EDIT | |
| Submit, Send | PO_CREATE/PO_EDIT | tightened |
| Approve, Reject | PO pages | the workflow decides |

**Requisitions**
| Action | Codes | Note |
|---|---|---|
| reads | PR pages + RFQ_CREATE, RFQ_MANAGE, PO_CREATE | |
| Create | REQUISITION_CREATE | |
| Patch, Submit | REQUISITION_CREATE/REQUISITION_APPROVE | change the pr-edit guard at routes:393 from VIEW_ALL to APPROVE; the Auditor holds VIEW_ALL |
| Approve, Reject | PR pages | |
| Convert, ConvertSplit | PO_CREATE | today Requesters and the Auditor can convert |

**Quotations**
| Action | Codes |
|---|---|
| UpdateQuotation | RFQ_CREATE/RFQ_MANAGE |
| GetAccessLinks | RFQ_VIEW/RFQ_CREATE/RFQ_MANAGE |
| ResendLink | RFQ_MANAGE |

**Grns** (keep the class-level `[Authorize]`)
| Action | Codes | Note |
|---|---|---|
| reads | GRN pages + WAREHOUSE_TRANSFER, INVOICE_PROCESS | |
| Create | GOODS_RECEIVE | |
| Update, Delete, Submit, UpdateLine | GOODS_RECEIVE/GRN_APPROVE | |
| LinkVariant, InspectLine | + GRN_QC_CONFIRM | |
| MarkAllocationRun | ALLOCATION_RUN | |
| QcConfirm/QcReject/Approve/Reject | GRN pages | |

**Sros**
| Action | Codes |
|---|---|
| reads | SRO + DELIVERY_CREATE |
| 8 writes | SRO |

Frontend first (buttons gated by status only):
- po-detail Submit/Send: PO_CREATE/EDIT
- pr-detail Submit/Edit: REQUISITION_CREATE/APPROVE; Convert: PO_CREATE
- quotation-detail Convert-to-PO: PO_CREATE; Resend: RFQ_MANAGE. Send, Award, OpenBids and Cancel are already RFQ_MANAGE on the server, but the buttons are shown to RFQ_VIEW.
- grn-detail and grn-list: delete, submit and line edits
- Also fix `MultiTenancyIsolationFixture.GrantExtraPermissionsAsync`: its Requester posts POs, so grant it PO_CREATE/PO_VIEW. It uses the appsettings DB, so don't run it here.

Other findings:
- Quotation reads need only RFQ_VIEW, while their routes also admit RFQ_CREATE/MANAGE.
- An "unassigned" workflow step lets any user act on it.
- GrnService approve/reject fall back to a direct approval when there is no workflow.
- The frontend calls `grns/{id}/finance-approve` and `finance-reject`, which don't exist on the server.

### B. Inventory
Code sets:
- **INV** = INVENTORY_VIEW/STOCK_MANAGE (the products, categories, warehouses and ledger routes)
- **CATALOG** = INV + STOCK_ADJUST, ALLOCATION_VIEW, REQUISITION_CREATE, REQUISITION_VIEW_ALL, RFQ_CREATE, RFQ_MANAGE, PO_CREATE, PO_EDIT, GOODS_RECEIVE, WAREHOUSE_TRANSFER, GRN_APPROVE, GRN_FINANCE_APPROVE, GRN_QC_CONFIRM, MATERIAL_VIEW, MATERIAL_MANAGE, SALE_ORDER_CREATE, SALE_ORDER_EDIT, SUPPLIER_MANAGE, PRODUCT_LEDGER_VIEW, PROD_CREATE (every form with a product picker)
- **WH** = INV + STOCK_ADJUST, ALLOCATION_VIEW, REQUISITION_CREATE, REQUISITION_VIEW_ALL, PO_CREATE, PO_EDIT, GOODS_RECEIVE, WAREHOUSE_TRANSFER, GRN_*, DELIVERY_CREATE, DELIVERY_VIEW, PROD_CREATE (every form with a warehouse dropdown)

| Action | Codes |
|---|---|
| Products GetProducts / GetProduct / GetManufacturableProducts / Search / LookupVariantByBarcode | CATALOG |
| categories, sub-categories, GetProductStock / StockSummary | INV |
| GetVariantStock | INV + MATERIAL_* |
| the 15 product/category/variant/manufacturing-config writes | STOCK_MANAGE |
| Warehouses GetWarehouses | WH |
| Warehouses structure / stock | INV |
| Warehouses 16 writes | STOCK_MANAGE |
| Attributes reads | INV |
| Attributes writes | STOCK_MANAGE |
| StockAdjustments Get / Create | STOCK_ADJUST |
| StockAdjustments Approve / Reject | STOCK_MANAGE (four eyes) |
| ReorderAlerts | REORDER_MANAGE/INVENTORY_VIEW |
| MoveBin | STOCK_MANAGE/STOCK_LOCATION_UPDATE |
| InventoryLedger | INV |

**Why the Products/Warehouses gates were commented out.** No document says. Restoring them as written (INVENTORY_VIEW) would lock the Requester out of creating PRs, the Procurement Manager out of PO and RFQ forms, the Finance Officer out of R9, and sale-order users out of their forms. The any-of attribute fixes that.

Frontend first: the inventory pages have **no** permission check on any write button.
- product list delete
- product detail: Edit, Deactivate, image, variants (STOCK_MANAGE); Add Adjustment (STOCK_ADJUST)
- category and sub-category writes
- warehouse list and detail writes
- stock adjustment approve/reject
- reorder alerts "create adjustment"

Also gated by status only:
- MIR detail Edit
- GRN detail Link

### C. Suppliers and Reports
**Why Suppliers/Partners were left ungated.** No reason is recorded; `docs/scm-commercial/ADDENDUM-29-TASKS.md:351-353` only says Partners copied Suppliers. Most likely `GET api/suppliers` is the vendor picker on about 15 pages whose users hold no SUPPLIER_* code. `GetBankDetail` hand-codes an any-of check.

**Suppliers**
| Action | Codes |
|---|---|
| GetSuppliers | SUPPLIER_VIEW/CREATE/EDIT/MANAGE, PO_CREATE, PO_EDIT, REQUISITION_VIEW_OWN/VIEW_ALL/CREATE/APPROVE, RFQ_VIEW/CREATE/MANAGE, SUPPLY_VIEW, PAYMENT_PROCESS, PAYMENT_VIEW, INVOICE_VIEW, INVOICE_PROCESS, GOODS_RECEIVE, WAREHOUSE_TRANSFER, INVENTORY_VIEW, STOCK_MANAGE, DELIVERY_TRACK, USER_MANAGE (gate pr-detail Convert on PO_CREATE first, then REQUISITION_* can come out) |
| GetSupplierById | SUPPLIER_VIEW/EDIT/MANAGE, PAYMENT_PROCESS, DELIVERY_TRACK |
| Create | SUPPLIER_CREATE/MANAGE |
| Patch, contacts, documents | SUPPLIER_EDIT/MANAGE |
| Approve / Reject / Blacklist / Suspend / Delete / UpsertBankDetail | SUPPLIER_MANAGE |
| GetBankDetail | SUPPLIER_MANAGE/INVOICE_VIEW |
| GetDocuments | SUPPLIER_VIEW/EDIT/MANAGE |
| types/categories (no caller) | reads SUPPLIER_*; writes SUPPLIER_MANAGE |
| GetEligibleContacts | widen from RFQ_CREATE to RFQ_* + PO_VIEW/CREATE/EDIT/APPROVE (see lock-outs below) |

**Partners**
| Action | Codes |
|---|---|
| GetPartners | SUPPLIER_*, SALE_ORDER_VIEW/CREATE/EDIT, CUSTOMER_PAYMENT_RECORD, CUSTOMER_LEDGER_VIEW, SALES_INVOICE_VIEW, INVENTORY_VIEW, STOCK_MANAGE |
| GetPartnerById | SUPPLIER_VIEW/EDIT/MANAGE, SALE_ORDER_VIEW/CREATE/EDIT/CONFIRM, CUSTOMER_PAYMENT_RECORD, CUSTOMER_LEDGER_VIEW |
| Create | SUPPLIER_CREATE/MANAGE |
| Update | SUPPLIER_EDIT/MANAGE |
| Delete | SUPPLIER_MANAGE |
| the 3 alias controllers (no caller) | as GetPartners, or remove them |

**Reports**
| Action | Codes |
|---|---|
| the 13 page-backed reports | REPORT_VIEW/REPORT_EXPORT |
| PrFulfillment, PrToPoPipeline, StockLevelSummary (no caller) | REPORT_VIEW |
| UserActivity | AUDIT_LOG_VIEW/REPORT_VIEW |
| AuditTrail | AUDIT_LOG_VIEW/REPORT_VIEW + GOODS_RECEIVE/GRN_*/WAREHOUSE_TRANSFER, plus an in-code rule (below) |
| SupplierTimeline | SUPPLIER_VIEW/EDIT/MANAGE + `[RequiresSupplierAccess("supplierId")]` |

The extra AuditTrail codes come from the "Load Audit Trail" buttons on grn-detail and sro-detail. The in-code rule: without AUDIT_LOG_VIEW or REPORT_VIEW, a caller may read only module WAREHOUSE, entity GRN or SRO, by id.

Frontend first: supplier-detail shows every write to SUPPLIER_VIEW holders. The same goes for supplier-list delete and partner-list edit/delete.

Findings (open, worth fixing independently):
1. **Stored XSS.** `SuppliersController.UploadDocument` keeps any extension and serves the file from the API origin, and `AttachDocument` takes any URL scheme. Fix with `SMS.Shared.Files.UploadRules`.
2. **Stray route.** `[HttpPut("categories/{id:guid}")]` (around line 292) sits on `DeleteSupplierType`, so a PUT deletes a supplier type, with no permission check.
3. **Existing lock-outs on endpoints that are already gated:**
   - `GetEligibleContacts` (RFQ_CREATE) blocks a Purchase Officer from sending a PO;
   - `stock-movement` and `stock-ledger` (INVENTORY_VIEW) are reached from a route that admits REPORT_VIEW;
   - `reserved-stock` (STOCK_MANAGE) is reached from a route that admits REPORT_VIEW.
4. **Pages that open to more people than their data should reach:**
   - finance-reports shows payables to any REPORT_VIEW holder (the Finance gates match this, as parity);
   - the audit-trail and user-activity pages open on REPORT_VIEW alone.

## Open questions for the user
- **Credit and debit notes.** The SRO page lets warehouse staff create supplier credit and debit notes; the Finance gate admits INVOICE_PROCESS, GOODS_RECEIVE or WAREHOUSE_TRANSFER. Is that an acceptable segregation of duties?
- **Opening balances.** Only System Admin can import opening balances (SYSTEM_CONFIGURE); Org Admin lacks it.

## Appendix: the 257 ungated signed-in actions on 2026-10-02
| Controller | # | Actions |
|---|---|---|
| AttachmentsController | 5 | Delete, GetByDocument, GetContent, GetPolicy, Upload |
| AttributesController | 10 | CreateAttribute, DeleteAttribute, GetAttributes, GetCategoryAttributes, GetVariantAttributeValues, LinkCategoryAttribute, SetCategoryAttributes, SetVariantAttributeValues, UnlinkCategoryAttribute, UpdateAttribute |
| AuthController | 11 | DeactivateUser, DeleteProfilePicture, GetAllUsers, GetRolePermissions, GetUserPermissions, Me, SaveRolePermissions, SaveUserPermissions, UpdatePassword, UpdateProfile, UploadProfilePicture |
| CarriersAliasController | 1 | GetCarriers |
| CreditNotesController | 4 | ApplyCarriedForward, CreateCreditNote, GetCreditNoteById, GetCreditNotes |
| CustomersAliasController | 1 | GetCustomers |
| DebitNotesController | 5 | ApplyCarriedForward, CreateDebitNote, GetDebitNoteById, GetDebitNotes, UpdateStatus |
| ExchangeRatesController | 2 | GetList, Quote |
| GrnsController | 14 | ApproveGrn, CreateGrn, DeleteGrn, GetGrnById, GetGrns, InspectGrnLine, LinkGrnLineVariant, MarkAllocationRun, QcConfirmGrn, QcRejectGrn, RejectGrn, SubmitGrn, UpdateGrn, UpdateGrnLine |
| InventoryItemsController | 2 | GetReorderAlerts, MoveBin |
| InventoryLedgerController | 1 | GetLedger |
| LookupsController | 9 | GetAll, GetByType, GetCities, GetCitiesByCountry, GetCountries, GetCurrencies, GetDeliveryTerms, GetLookupTypes, GetPaymentTerms |
| MasterLedgerController | 6 | ExportExcel, ExportPdf, GetBalance, GetLedger, GetSummary, GetWriteOff |
| MasterProductLedgerController | 5 | ExportExcel, ExportPdf, GetLedger, GetProductJourney, GetSummary |
| NotificationsController | 7 | Delete, GetInbox, GetList, GetUnreadCount, GetWhatsAppLogs, MarkOneRead, MarkRead |
| PartnersController | 5 | CreatePartner, DeletePartner, GetPartnerById, GetPartners, UpdatePartner |
| PaymentsController | 4 | Create, GetById, GetList, Patch |
| ProductsController | 26 | CreateCategory, CreateProduct, CreateSubCategory, CreateVariant, DeactivateCategory, DeactivateSubCategory, DeleteCategory, DeleteProduct, DeleteSubCategory, DeleteVariant, GetCategories, GetManufacturableProducts, GetProduct, GetProducts, GetProductStock, GetProductStockSummary, GetSubCategories, GetSubCategoriesByCategory, GetVariantStock, LookupVariantByBarcode, PatchProduct, SearchProducts, SetManufacturingConfig, UpdateCategory, UpdateSubCategory, UpdateVariant |
| PurchaseOrdersController | 12 | ApprovePurchaseOrder, CreatePurchaseOrder, DownloadPdf, GetPurchaseOrderById, GetPurchaseOrders, GetTimeline, RejectPurchaseOrder, SearchPurchaseOrders, SendPurchaseOrder, SplitPurchaseOrder, SubmitPurchaseOrderForApproval, UpdatePurchaseOrder |
| QuickBooksDataController | 8 | GetStatus, PostStatus, PutBill, PutCustomer, PutItem, PutSalesInvoice, PutVendor, VoidSalesInvoice |
| QuotationsController | 3 | GetAccessLinks, ResendLink, UpdateQuotation |
| ReportsController | 19 | GetAuditTrail, GetBudgetUtilization, GetGrnVariance, GetInventoryValuation, GetInvoiceAging, GetKpis, GetPaymentSummary, GetPendingApprovals, GetPoSummary, GetPrFulfillmentStatus, GetPrToPoPipeline, GetReorderAlerts, GetShipmentTracker, GetSpendBySupplier, GetStockLevels, GetStockLevelSummary, GetSupplierPerformance, GetSupplierTimeline, GetUserActivity |
| RequisitionsController | 9 | ApproveRequisition, ConvertRequisition, ConvertRequisitionSplit, CreateRequisition, GetRequisitionById, GetRequisitions, PatchRequisition, RejectRequisition, SubmitRequisition |
| ServiceProvidersAliasController | 1 | GetServiceProviders |
| SrosController | 10 | ApproveSro, ConfirmReceipt, CreateSro, DispatchSro, EscalateSro, ExpectReplacement, GetSroById, GetSros, RejectSro, ResolveSro |
| StockAdjustmentsController | 4 | ApproveAdjustment, CreateAdjustment, GetAdjustments, RejectAdjustment |
| SupplierLedgerController | 2 | GetBalance, GetLedger |
| SupplierPaymentsController | 13 | Bounce, Cancel, Create, GetById, GetCrossSupplierAging, GetList, GetOutstandingInvoices, GetOutstandingPayables, GetPaymentMethodBreakdown, GetPaymentRegister, GetSupplierAging, GetSupplierLedgerReport, Post |
| SuppliersController | 22 | AddContact, ApproveSupplier, AttachDocument, BlacklistSupplier, CreateSupplier, CreateSupplierType, DeleteSupplier, DeleteSupplierCategory, DeleteSupplierType, GetBankDetail, GetCategories, GetDocuments, GetSupplierById, GetSuppliers, GetSupplierTypes, PatchSupplier, RejectSupplier, SoftDeleteDocument, SuspendSupplier, UpdateSupplierType, UploadDocument, UpsertBankDetail |
| TaxCodesController | 1 | GetList |
| TenantController | 1 | GetCurrent |
| TimelineController | 2 | GetByDocument, GetByTraceId |
| WarehousesController | 19 | CreateBin, CreateRack, CreateShelf, CreateStructuredBin, CreateWarehouse, CreateZone, DeactivateBin, DeactivateRack, DeactivateShelf, DeactivateZone, DeleteWarehouse, GetWarehouses, GetWarehouseStock, GetWarehouseStructure, UpdateBin, UpdateRack, UpdateShelf, UpdateWarehouse, UpdateZone |
| WorkflowApprovalActionsController | 5 | Cancel, Delegate, Recall, Reissue, Reject |
| WorkflowApprovalDetailController | 3 | GetApprovalDetail, GetAuditLog, GetSteps |
| WorkflowHistoryController | 1 | GetHistory |
| WorkflowInboxController | 2 | GetInbox, GetInboxCount |
| WorkflowSubmitController | 2 | Approve, Submit |

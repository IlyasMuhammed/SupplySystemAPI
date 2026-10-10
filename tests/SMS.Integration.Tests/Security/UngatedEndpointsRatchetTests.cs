using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Authorization;
using Xunit;
using Xunit.Abstractions;

namespace SMS.Integration.Tests.Security;

/// <summary>
/// Which signed-in endpoints check no permission at all. The global filter in Program.cs only demands a
/// sign-in, so an action without [RequirePermission] is open to every user of the organization whatever their
/// role. On 2026-10-02 that was 257 actions, among them the whole purchase order and requisition controllers, GRN
/// writes, products and warehouses. Every such action in the real host must be on the reviewed list below — either
/// with the reason a sign-in is enough, or marked OPEN with its mapping in docs/security/permission-sweep.md — and
/// the list can only shrink: an entry whose action gained a permission (or disappeared) must be removed.
/// Anonymous actions are reviewed separately, in <see cref="AnonymousEndpointsTests"/>.
/// </summary>
public sealed class UngatedEndpointsRatchetTests : IClassFixture<SapWebApplicationFactory>
{
    private sealed record Reviewed(string Controller, string Action, string Reason);

    // Open lines below, by the part of docs/security/permission-sweep.md that maps them (the sweep was paused on
    // 2026-10-02 before they were gated). Auth and Finance lines belong to their own hardening work.
    private const string GatingSweep            = "OPEN: workflow engine raw API — see docs/security/permission-sweep.md";
    private const string GatingDemandWarehouse  = "OPEN: Demand + Warehouse — see docs/security/permission-sweep.md";
    private const string GatingInventory        = "OPEN: Inventory — see docs/security/permission-sweep.md";
    private const string GatingSuppliersReports = "OPEN: Suppliers + Reports — see docs/security/permission-sweep.md";

    /// <summary>
    /// Controller → action ("*" = every action of the controller), each with why a sign-in alone is enough.
    /// </summary>
    private static readonly Reviewed[] Allowed =
    [
        // ── A signed-in user's own account (the token's user, never a body id) ───────
        new("AuthController", "Me",                   "the caller's own profile, permissions and organization"),
        new("AuthController", "UpdateProfile",        "the caller's own profile — the target is the token's user, never a body id (auth-hardening a3d085ddc4d6a0dcd)"),
        new("AuthController", "UpdatePassword",       "the caller's own password, which it must also supply — the target is the token's user"),
        new("AuthController", "UploadProfilePicture", "the caller's own picture"),
        new("AuthController", "DeleteProfilePicture", "the caller's own picture"),
        new("TenantController", "GetCurrent",         "the caller's own organization, its enabled features and the caller's own permissions — what the menu is built from"),
        new("ModulesController", "GetEnabled",        "any signed-in user (A37 API-CONTRACT §1.1 #2): the caller's own organization's usable module/feature codes — what the frontend ModuleService gates the UI with, the same facts GetCurrent already returns"),
        new("DashboardController", "GetSummary",      "the home dashboard: every section is left out unless the caller holds that area's own view permission (DashboardService checks each one), so it reveals nothing the caller's list pages would not"),

        // ── A user's own notifications (the repository filters every query by the token's user) ──
        new("NotificationsController", "GetList",        "the caller's own notifications"),
        new("NotificationsController", "GetInbox",       "the caller's own unread notifications"),
        new("NotificationsController", "GetUnreadCount", "the caller's own unread count (topbar bell)"),
        new("NotificationsController", "MarkRead",       "marks the caller's own notifications read"),
        new("NotificationsController", "MarkOneRead",    "marks one of the caller's own notifications read"),
        new("NotificationsController", "Delete",         "deletes one of the caller's own notifications"),

        // ── Approvals: who may act is decided by the workflow definition, not by a permission ──
        // The inbox, approval-detail and document-history routes are open to every signed-in user, because a
        // workflow can name any user or role as an approver. The service refuses anyone who is not an assigned
        // approver of the current step (or, for recall, the initiator), and every row is tenant-scoped.
        new("WorkflowInboxController", "GetInbox",                   "the caller's own pending approval steps"),
        new("WorkflowInboxController", "GetInboxCount",              "the caller's own pending count"),
        new("WorkflowSubmitController", "Approve",                   "refused unless the caller is an assigned approver of the current step (ForbiddenException)"),
        new("WorkflowApprovalActionsController", "Reject",           "refused unless the caller is an assigned approver of the current step (ForbiddenException)"),
        new("WorkflowApprovalActionsController", "Delegate",         "refused unless the caller is an assigned approver of the current step (ForbiddenException)"),
        new("WorkflowApprovalActionsController", "Recall",           "refused unless the caller initiated the approval (ForbiddenException)"),
        new("WorkflowApprovalDetailController", "GetApprovalDetail", "approval routing of one approval in the caller's organization, by its GUID — the approver's own screen"),
        new("WorkflowApprovalDetailController", "GetSteps",          "approval routing of one approval in the caller's organization, by its GUID"),
        new("WorkflowApprovalDetailController", "GetAuditLog",       "approval trail of one approval in the caller's organization, by its GUID"),
        new("WorkflowHistoryController", "GetHistory",               "approval trail of one document — the history panel shared by every module's detail page; no document content"),
        new("TimelineController", "GetByDocument",                   "lifecycle events of one document (type, number, who, when) — the timeline panel shared by every module's detail page"),
        new("TimelineController", "GetByTraceId",                    "lifecycle events of one trace chain by its GUID — as GetByDocument"),

        // ── Reference data every form's pickers read ─────────────────────────────────
        new("LookupsController", "GetAll",             "dropdown reference data (lookup values) read by every form; writes need SYSTEM_CONFIGURE"),
        new("LookupsController", "GetByType",          "one lookup type's values for a dropdown; writes need SYSTEM_CONFIGURE"),
        new("LookupsController", "GetLookupTypes",     "the lookup type catalog for dropdowns; writes need SYSTEM_CONFIGURE"),
        new("LookupsController", "GetCountries",       "the shared country catalog (address and supplier forms); writes need LOCATION_MANAGE / SYSTEM_CONFIGURE"),
        new("LookupsController", "GetCities",          "the shared city catalog; writes need LOCATION_MANAGE / SYSTEM_CONFIGURE"),
        new("LookupsController", "GetCitiesByCountry", "the shared city catalog; writes need LOCATION_MANAGE / SYSTEM_CONFIGURE"),
        new("LookupsController", "GetCurrencies",      "the currency catalog every priced document picks from; writes need SYSTEM_CONFIGURE"),
        new("LookupsController", "GetPaymentTerms",    "payment terms for supplier, PO and sale order forms; writes need SYSTEM_CONFIGURE"),
        new("LookupsController", "GetDeliveryTerms",   "delivery terms for PO and sale order forms; writes need SYSTEM_CONFIGURE"),
        new("TaxCodesController", "GetList",           "SAP alignment: feeds the tax-code picker on sale orders and supplier invoices; writes need FINANCE_SETUP_MANAGE"),
        new("ExchangeRatesController", "GetList",      "SAP alignment: the rate shown on supplier invoices; writes need FINANCE_SETUP_MANAGE"),
        new("ExchangeRatesController", "Quote",        "SAP alignment: the rate a document of a given date would use; writes need FINANCE_SETUP_MANAGE"),

        // ── Machine-to-machine: not a user at all ───────────────────────────────────
        new("QuickBooksDataController", "*",           "the QuickBooks gateway: API-key scheme only (no user token), and every action needs its own [RequireApiScope]"),

        // ── OPEN: not gated yet. The 2026-10-02 permission sweep was paused before these landed; the mapping each
        //    should get is in docs/security/permission-sweep.md. Each line comes off as its action gains a
        //    [RequirePermission]; The_reviewed_list_only_shrinks fails until it does. ─────────────────────
        new("WorkflowSubmitController", "Submit", GatingSweep),
        new("WorkflowApprovalActionsController", "Cancel", GatingSweep),
        new("WorkflowApprovalActionsController", "Reissue", GatingSweep),
        new("GrnsController", "ApproveGrn", GatingDemandWarehouse),
        new("GrnsController", "CreateGrn", GatingDemandWarehouse),
        new("GrnsController", "DeleteGrn", GatingDemandWarehouse),
        new("GrnsController", "GetGrnById", GatingDemandWarehouse),
        new("GrnsController", "GetGrns", GatingDemandWarehouse),
        new("GrnsController", "InspectGrnLine", GatingDemandWarehouse),
        new("GrnsController", "LinkGrnLineVariant", GatingDemandWarehouse),
        new("GrnsController", "MarkAllocationRun", GatingDemandWarehouse),
        new("GrnsController", "QcConfirmGrn", GatingDemandWarehouse),
        new("GrnsController", "QcRejectGrn", GatingDemandWarehouse),
        new("GrnsController", "RejectGrn", GatingDemandWarehouse),
        new("GrnsController", "SubmitGrn", GatingDemandWarehouse),
        new("GrnsController", "UpdateGrn", GatingDemandWarehouse),
        new("GrnsController", "UpdateGrnLine", GatingDemandWarehouse),
        new("PurchaseOrdersController", "ApprovePurchaseOrder", GatingDemandWarehouse),
        new("PurchaseOrdersController", "CreatePurchaseOrder", GatingDemandWarehouse),
        new("PurchaseOrdersController", "DownloadPdf", GatingDemandWarehouse),
        new("PurchaseOrdersController", "GetPurchaseOrderById", GatingDemandWarehouse),
        new("PurchaseOrdersController", "GetPurchaseOrders", GatingDemandWarehouse),
        new("PurchaseOrdersController", "GetTimeline", GatingDemandWarehouse),
        new("PurchaseOrdersController", "RejectPurchaseOrder", GatingDemandWarehouse),
        new("PurchaseOrdersController", "SearchPurchaseOrders", GatingDemandWarehouse),
        new("PurchaseOrdersController", "SendPurchaseOrder", GatingDemandWarehouse),
        new("PurchaseOrdersController", "SplitPurchaseOrder", GatingDemandWarehouse),
        new("PurchaseOrdersController", "SubmitPurchaseOrderForApproval", GatingDemandWarehouse),
        new("PurchaseOrdersController", "UpdatePurchaseOrder", GatingDemandWarehouse),
        new("QuotationsController", "GetAccessLinks", GatingDemandWarehouse),
        new("QuotationsController", "ResendLink", GatingDemandWarehouse),
        new("QuotationsController", "UpdateQuotation", GatingDemandWarehouse),
        new("RequisitionsController", "ApproveRequisition", GatingDemandWarehouse),
        new("RequisitionsController", "ConvertRequisition", GatingDemandWarehouse),
        new("RequisitionsController", "ConvertRequisitionSplit", GatingDemandWarehouse),
        new("RequisitionsController", "CreateRequisition", GatingDemandWarehouse),
        new("RequisitionsController", "GetRequisitionById", GatingDemandWarehouse),
        new("RequisitionsController", "GetRequisitions", GatingDemandWarehouse),
        new("RequisitionsController", "PatchRequisition", GatingDemandWarehouse),
        new("RequisitionsController", "RejectRequisition", GatingDemandWarehouse),
        new("RequisitionsController", "SubmitRequisition", GatingDemandWarehouse),
        new("SrosController", "ApproveSro", GatingDemandWarehouse),
        new("SrosController", "ConfirmReceipt", GatingDemandWarehouse),
        new("SrosController", "CreateSro", GatingDemandWarehouse),
        new("SrosController", "DispatchSro", GatingDemandWarehouse),
        new("SrosController", "EscalateSro", GatingDemandWarehouse),
        new("SrosController", "ExpectReplacement", GatingDemandWarehouse),
        new("SrosController", "GetSroById", GatingDemandWarehouse),
        new("SrosController", "GetSros", GatingDemandWarehouse),
        new("SrosController", "RejectSro", GatingDemandWarehouse),
        new("SrosController", "ResolveSro", GatingDemandWarehouse),
        new("AttributesController", "CreateAttribute", GatingInventory),
        new("AttributesController", "DeleteAttribute", GatingInventory),
        new("AttributesController", "GetAttributes", GatingInventory),
        new("AttributesController", "GetCategoryAttributes", GatingInventory),
        new("AttributesController", "GetVariantAttributeValues", GatingInventory),
        new("AttributesController", "LinkCategoryAttribute", GatingInventory),
        new("AttributesController", "SetCategoryAttributes", GatingInventory),
        new("AttributesController", "SetVariantAttributeValues", GatingInventory),
        new("AttributesController", "UnlinkCategoryAttribute", GatingInventory),
        new("AttributesController", "UpdateAttribute", GatingInventory),
        new("InventoryItemsController", "GetReorderAlerts", GatingInventory),
        new("InventoryItemsController", "MoveBin", GatingInventory),
        new("InventoryLedgerController", "GetLedger", GatingInventory),
        new("ProductsController", "CreateCategory", GatingInventory),
        new("ProductsController", "CreateProduct", GatingInventory),
        new("ProductsController", "CreateSubCategory", GatingInventory),
        new("ProductsController", "CreateVariant", GatingInventory),
        new("ProductsController", "DeactivateCategory", GatingInventory),
        new("ProductsController", "DeactivateSubCategory", GatingInventory),
        new("ProductsController", "DeleteCategory", GatingInventory),
        new("ProductsController", "DeleteProduct", GatingInventory),
        new("ProductsController", "DeleteSubCategory", GatingInventory),
        new("ProductsController", "DeleteVariant", GatingInventory),
        new("ProductsController", "GetCategories", GatingInventory),
        new("ProductsController", "GetManufacturableProducts", GatingInventory),
        new("ProductsController", "GetProduct", GatingInventory),
        new("ProductsController", "GetProducts", GatingInventory),
        new("ProductsController", "GetProductStock", GatingInventory),
        new("ProductsController", "GetProductStockSummary", GatingInventory),
        new("ProductsController", "GetSubCategories", GatingInventory),
        new("ProductsController", "GetSubCategoriesByCategory", GatingInventory),
        new("ProductsController", "GetVariantStock", GatingInventory),
        new("ProductsController", "LookupVariantByBarcode", GatingInventory),
        new("ProductsController", "PatchProduct", GatingInventory),
        new("ProductsController", "SearchProducts", GatingInventory),
        new("ProductsController", "SetManufacturingConfig", GatingInventory),
        new("ProductsController", "UpdateCategory", GatingInventory),
        new("ProductsController", "UpdateSubCategory", GatingInventory),
        new("ProductsController", "UpdateVariant", GatingInventory),
        new("StockAdjustmentsController", "ApproveAdjustment", GatingInventory),
        new("StockAdjustmentsController", "CreateAdjustment", GatingInventory),
        new("StockAdjustmentsController", "GetAdjustments", GatingInventory),
        new("StockAdjustmentsController", "RejectAdjustment", GatingInventory),
        new("WarehousesController", "CreateBin", GatingInventory),
        new("WarehousesController", "CreateRack", GatingInventory),
        new("WarehousesController", "CreateShelf", GatingInventory),
        new("WarehousesController", "CreateStructuredBin", GatingInventory),
        new("WarehousesController", "CreateWarehouse", GatingInventory),
        new("WarehousesController", "CreateZone", GatingInventory),
        new("WarehousesController", "DeactivateBin", GatingInventory),
        new("WarehousesController", "DeactivateRack", GatingInventory),
        new("WarehousesController", "DeactivateShelf", GatingInventory),
        new("WarehousesController", "DeactivateZone", GatingInventory),
        new("WarehousesController", "DeleteWarehouse", GatingInventory),
        new("WarehousesController", "GetWarehouses", GatingInventory),
        new("WarehousesController", "GetWarehouseStock", GatingInventory),
        new("WarehousesController", "GetWarehouseStructure", GatingInventory),
        new("WarehousesController", "UpdateBin", GatingInventory),
        new("WarehousesController", "UpdateRack", GatingInventory),
        new("WarehousesController", "UpdateShelf", GatingInventory),
        new("WarehousesController", "UpdateWarehouse", GatingInventory),
        new("WarehousesController", "UpdateZone", GatingInventory),
        new("CarriersAliasController", "GetCarriers", GatingSuppliersReports),
        new("PartnersController", "CreatePartner", GatingSuppliersReports),
        new("PartnersController", "DeletePartner", GatingSuppliersReports),
        new("PartnersController", "GetPartnerById", GatingSuppliersReports),
        new("PartnersController", "GetPartners", GatingSuppliersReports),
        new("PartnersController", "UpdatePartner", GatingSuppliersReports),
        new("ReportsController", "GetAuditTrail", GatingSuppliersReports),
        new("ReportsController", "GetBudgetUtilization", GatingSuppliersReports),
        new("ReportsController", "GetGrnVariance", GatingSuppliersReports),
        new("ReportsController", "GetInventoryValuation", GatingSuppliersReports),
        new("ReportsController", "GetInvoiceAging", GatingSuppliersReports),
        new("ReportsController", "GetKpis", GatingSuppliersReports),
        new("ReportsController", "GetPaymentSummary", GatingSuppliersReports),
        new("ReportsController", "GetPendingApprovals", GatingSuppliersReports),
        new("ReportsController", "GetPoSummary", GatingSuppliersReports),
        new("ReportsController", "GetPrFulfillmentStatus", GatingSuppliersReports),
        new("ReportsController", "GetPrToPoPipeline", GatingSuppliersReports),
        new("ReportsController", "GetReorderAlerts", GatingSuppliersReports),
        new("ReportsController", "GetShipmentTracker", GatingSuppliersReports),
        new("ReportsController", "GetSpendBySupplier", GatingSuppliersReports),
        new("ReportsController", "GetStockLevels", GatingSuppliersReports),
        new("ReportsController", "GetStockLevelSummary", GatingSuppliersReports),
        new("ReportsController", "GetSupplierPerformance", GatingSuppliersReports),
        new("ReportsController", "GetSupplierTimeline", GatingSuppliersReports),
        new("ReportsController", "GetUserActivity", GatingSuppliersReports),
        new("ServiceProvidersAliasController", "GetServiceProviders", GatingSuppliersReports),
        new("SuppliersController", "AddContact", GatingSuppliersReports),
        new("SuppliersController", "ApproveSupplier", GatingSuppliersReports),
        new("SuppliersController", "AttachDocument", GatingSuppliersReports),
        new("SuppliersController", "BlacklistSupplier", GatingSuppliersReports),
        new("SuppliersController", "CreateSupplier", GatingSuppliersReports),
        new("SuppliersController", "CreateSupplierType", GatingSuppliersReports),
        new("SuppliersController", "DeleteSupplier", GatingSuppliersReports),
        new("SuppliersController", "DeleteSupplierCategory", GatingSuppliersReports),
        new("SuppliersController", "DeleteSupplierType", GatingSuppliersReports),
        new("SuppliersController", "GetBankDetail", GatingSuppliersReports),
        new("SuppliersController", "GetCategories", GatingSuppliersReports),
        new("SuppliersController", "GetDocuments", GatingSuppliersReports),
        new("SuppliersController", "GetSupplierById", GatingSuppliersReports),
        new("SuppliersController", "GetSuppliers", GatingSuppliersReports),
        new("SuppliersController", "GetSupplierTypes", GatingSuppliersReports),
        new("SuppliersController", "PatchSupplier", GatingSuppliersReports),
        new("SuppliersController", "RejectSupplier", GatingSuppliersReports),
        new("SuppliersController", "SoftDeleteDocument", GatingSuppliersReports),
        new("SuppliersController", "SuspendSupplier", GatingSuppliersReports),
        new("SuppliersController", "UpdateSupplierType", GatingSuppliersReports),
        new("SuppliersController", "UploadDocument", GatingSuppliersReports),
        new("SuppliersController", "UpsertBankDetail", GatingSuppliersReports),
        // Generic over every kind of document, so one [RequirePermission] cannot fit: each action applies the kind's own
        // View/Upload/Delete codes in code (AttachmentAccessPolicy; tests/SMS.WorkflowEngine.Tests/AttachmentAccessPolicyTests.cs
        // and AttachmentEndpointAccessTests.cs), and refuses a kind the policy does not name.
        new("AttachmentsController", "Delete",        "per-document Delete/DeleteOwn codes of the file's kind, enforced by AttachmentAccessPolicy.MayRemove; a filed document is 409"),
        new("AttachmentsController", "GetByDocument", "per-document View codes enforced by AttachmentAccessPolicy; an unknown kind is 400"),
        new("AttachmentsController", "GetContent",    "per-document View codes of the file's kind, plus the permission it was filed with (AttachmentAccessPolicy)"),
        new("AttachmentsController", "GetPolicy",     "the attachment rules themselves — the same for every caller, no data; every attachment panel reads them"),
        new("AttachmentsController", "Upload",        "per-document Upload codes enforced by AttachmentAccessPolicy before anything is written; an unknown kind is 400"),
    ];

    private readonly SapWebApplicationFactory _f;
    private readonly ITestOutputHelper _out;

    public UngatedEndpointsRatchetTests(SapWebApplicationFactory factory, ITestOutputHelper output)
    {
        _f   = factory;
        _out = output;
    }

    private List<(string Controller, string Action)> UngatedActions()
    {
        var actions = _f.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>().ToList();
        actions.Should().NotBeEmpty();

        return actions
            .Where(a => !a.EndpointMetadata.OfType<IAllowAnonymous>().Any()
                     && !a.EndpointMetadata.OfType<RequirePermissionAttribute>().Any())
            .Select(a => (Controller: a.ControllerTypeInfo.Name, Action: a.ActionName))
            .Distinct()
            .OrderBy(a => a.Controller).ThenBy(a => a.Action)
            .ToList();
    }

    private static bool IsAllowed((string Controller, string Action) a) =>
        Allowed.Any(r => r.Controller == a.Controller && (r.Action == "*" || r.Action == a.Action));

    [Fact]
    public void Every_signed_in_action_without_a_permission_is_a_reviewed_one()
    {
        var ungated = UngatedActions();

        var unreviewed = ungated.Where(a => !IsAllowed(a)).Select(a => $"{a.Controller}.{a.Action}").ToList();
        foreach (var a in unreviewed) _out.WriteLine(a);

        unreviewed.Should().BeEmpty(
            "every signed-in user of the organization can call an action that checks no permission; gate it with the " +
            "[RequirePermission] the frontend checks before it offers the page or button, or add it here with the reason it is safe");
    }

    [Fact]
    public void The_reviewed_list_only_shrinks()
    {
        var ungated = UngatedActions();

        var stale = Allowed
            .Where(r => r.Action == "*"
                ? !ungated.Any(a => a.Controller == r.Controller)
                : !ungated.Contains((r.Controller, r.Action)))
            .Select(r => $"{r.Controller}.{r.Action}")
            .ToList();

        stale.Should().BeEmpty("an action that now checks a permission (or no longer exists) must come off the reviewed list, so it can never quietly lose its gate again");

        Allowed.Where(r => string.IsNullOrWhiteSpace(r.Reason)).Should().BeEmpty("every entry says why a sign-in is enough");
        Allowed.GroupBy(r => (r.Controller, r.Action)).Where(g => g.Count() > 1).Should().BeEmpty();
    }

    [Fact]
    public void A_whole_controller_entry_covers_only_controllers_that_check_nothing()
    {
        // "*" is for controllers with no gate at all; once one action is gated, the others are listed by name.
        var gatedSomewhere = _f.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>()
            .Where(a => a.EndpointMetadata.OfType<RequirePermissionAttribute>().Any())
            .Select(a => a.ControllerTypeInfo.Name)
            .ToHashSet();

        Allowed.Where(r => r.Action == "*" && gatedSomewhere.Contains(r.Controller))
            .Select(r => r.Controller)
            .Should().BeEmpty();
    }
}

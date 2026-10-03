using System.Security.Claims;
using FluentAssertions;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Services;
using SMS.WorkflowEngine.Tests.Helpers;
using Xunit;

namespace SMS.WorkflowEngine.Tests;

/// <summary>
/// Who may list, add and remove the files attached to each kind of document. Until this existed,
/// <c>api/attachments</c> asked only for a sign-in: a view-only Auditor could attach a file to any
/// document, and anybody could remove anything — the documents the system itself files included.
/// </summary>
public class AttachmentAccessPolicyTests
{
    /// <summary>
    /// Every interface code something files attachments under: the frontend's attachment panels and its
    /// direct uploads, and the modules that file generated documents or a vendor's portal uploads.
    /// </summary>
    private static readonly string[] CodesInUse =
    [
        "PR", "PR_LINE",               // pr-create, pr-detail
        "QUOTATION",                   // quotation-create, quotation-detail
        "RFQ_RESPONSE",                // quotation-detail (read-only); RfqPortalController files a vendor's uploads
        "PO",                          // po-create, po-detail
        "PO_TEMPLATE_LOGO",            // po-document-template
        "GRN",                         // grn-create, grn-detail
        "DELIVERY",                    // delivery-detail; GatePassArchive files gate passes
        "INVOICE",                     // invoice-create, invoice-detail
        "SUPPLIER_PAYMENT",            // supplier-payment-create, supplier-payment-detail
        "SALES_INVOICE",               // sales-invoice-detail (read-only); SalesInvoiceDocumentArchive files issued PDFs
        "PRODUCT_IMAGE",               // product-create, product-detail
        // A32 — sales pre-order pipeline (docs/sales-preorder/API-CONTRACT.md §3).
        "SALE_INQUIRY",                // inquiry detail
        "SALE_QUOTATION",              // quotation detail
        "CUSTOMER_PO",                 // sale order header — the customer's PO document (documentId = the order)
        "SALE_ORDER",                  // sale order attachments tab
    ];

    private static string[] Split(string codes) => codes.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static ClaimsPrincipal Holding(params string[] permissions) =>
        new(new ClaimsIdentity(permissions.Select(p => new Claim("permission", p)), "Test"));

    // ── The map ──────────────────────────────────────────────────────────────

    [Fact]
    public void There_is_one_rule_for_every_kind_of_document_files_are_attached_to_and_none_for_anything_else()
    {
        AttachmentAccessPolicy.Rules.Select(r => r.InterfaceCode).Should().OnlyHaveUniqueItems();
        AttachmentAccessPolicy.Rules.Select(r => r.InterfaceCode).Should().BeEquivalentTo(CodesInUse);
    }

    /// <summary>View · add · remove anybody's · remove only one's own.</summary>
    [Theory]
    [InlineData("PR",               "REQUISITION_VIEW_OWN REQUISITION_VIEW_ALL REQUISITION_CREATE REQUISITION_APPROVE", "REQUISITION_CREATE REQUISITION_APPROVE", "REQUISITION_APPROVE", "REQUISITION_CREATE")]
    [InlineData("PR_LINE",          "REQUISITION_VIEW_OWN REQUISITION_VIEW_ALL REQUISITION_CREATE REQUISITION_APPROVE", "REQUISITION_CREATE REQUISITION_APPROVE", "REQUISITION_APPROVE", "REQUISITION_CREATE")]
    [InlineData("QUOTATION",        "RFQ_VIEW RFQ_CREATE RFQ_MANAGE",                                                   "RFQ_CREATE RFQ_MANAGE",                  "RFQ_MANAGE",          "RFQ_CREATE")]
    [InlineData("RFQ_RESPONSE",     "RFQ_VIEW RFQ_CREATE RFQ_MANAGE",                                                   "RFQ_MANAGE",                             "RFQ_MANAGE",          "")]
    [InlineData("PO",               "PO_VIEW PO_CREATE PO_EDIT PO_APPROVE",                                             "PO_CREATE PO_EDIT",                      "PO_EDIT",             "PO_CREATE")]
    [InlineData("PO_TEMPLATE_LOGO", "PO_TEMPLATE_MANAGE",                                                               "PO_TEMPLATE_MANAGE",                     "PO_TEMPLATE_MANAGE",  "")]
    [InlineData("GRN",              "GOODS_RECEIVE GRN_APPROVE GRN_FINANCE_APPROVE GRN_QC_CONFIRM",                     "GOODS_RECEIVE GRN_APPROVE",              "GRN_APPROVE",         "GOODS_RECEIVE")]
    [InlineData("DELIVERY",         "DELIVERY_VIEW DELIVERY_EDIT",                                                      "DELIVERY_EDIT",                          "DELIVERY_EDIT",       "")]
    [InlineData("INVOICE",          "INVOICE_VIEW INVOICE_PROCESS",                                                     "INVOICE_PROCESS",                        "INVOICE_PROCESS",     "")]
    [InlineData("SUPPLIER_PAYMENT", "PAYMENT_VIEW PAYMENT_PROCESS",                                                     "PAYMENT_PROCESS",                        "PAYMENT_PROCESS",     "")]
    [InlineData("SALES_INVOICE",    "SALES_INVOICE_VIEW SALES_INVOICE_MANAGE",                                          "",                                       "",                    "")]
    [InlineData("PRODUCT_IMAGE",    "INVENTORY_VIEW STOCK_MANAGE",                                                      "STOCK_MANAGE",                           "STOCK_MANAGE",        "")]
    [InlineData("SALE_INQUIRY",     "SALE_INQUIRY_VIEW SALE_INQUIRY_CREATE SALE_INQUIRY_EDIT",                          "SALE_INQUIRY_CREATE SALE_INQUIRY_EDIT",  "SALE_INQUIRY_EDIT",   "SALE_INQUIRY_CREATE")]
    [InlineData("SALE_QUOTATION",   "SALE_QUOTATION_VIEW SALE_QUOTATION_CREATE SALE_QUOTATION_EDIT SALE_QUOTATION_SEND", "SALE_QUOTATION_CREATE SALE_QUOTATION_EDIT", "SALE_QUOTATION_EDIT", "SALE_QUOTATION_CREATE")]
    [InlineData("CUSTOMER_PO",      "SALE_ORDER_VIEW SALE_ORDER_CREATE SALE_ORDER_EDIT SALE_ORDER_CONFIRM",             "SALE_ORDER_CREATE SALE_ORDER_EDIT",      "SALE_ORDER_EDIT",     "SALE_ORDER_CREATE")]
    [InlineData("SALE_ORDER",       "SALE_ORDER_VIEW SALE_ORDER_CREATE SALE_ORDER_EDIT SALE_ORDER_CONFIRM",             "SALE_ORDER_CREATE SALE_ORDER_EDIT",      "SALE_ORDER_EDIT",     "SALE_ORDER_CREATE")]
    public void Each_rule_asks_for_what_that_documents_own_pages_and_controllers_ask_for(
        string code, string view, string add, string removeAny, string removeOwn)
    {
        var rule = AttachmentAccessPolicy.Find(code);

        rule.Should().NotBeNull();
        rule!.View.Should().BeEquivalentTo(Split(view));
        rule.Upload.Should().BeEquivalentTo(Split(add));
        rule.Delete.Should().BeEquivalentTo(Split(removeAny));
        rule.DeleteOwn.Should().BeEquivalentTo(Split(removeOwn));
    }

    [Fact]
    public void Whoever_may_add_a_file_may_remove_at_least_their_own_and_nobody_else_may_remove_anything()
    {
        foreach (var rule in AttachmentAccessPolicy.Rules)
            rule.Delete.Concat(rule.DeleteOwn).Should().BeEquivalentTo(rule.Upload, rule.InterfaceCode);
    }

    [Fact]
    public void Every_permission_a_rule_names_is_a_real_permission_code()
    {
        var named = AttachmentAccessPolicy.Rules.SelectMany(r => r.View.Concat(r.Upload).Concat(r.Delete).Concat(r.DeleteOwn)).Distinct();

        named.Should().BeSubsetOf(PermissionCodes.All, "a typo here would quietly lock everybody out");
    }

    [Fact]
    public void Whoever_may_add_or_remove_a_file_may_also_see_the_list_they_are_changing()
    {
        foreach (var rule in AttachmentAccessPolicy.Rules)
        {
            rule.Upload.Should().BeSubsetOf(rule.View, rule.InterfaceCode);
            rule.Delete.Should().BeSubsetOf(rule.View, rule.InterfaceCode);
            rule.DeleteOwn.Should().BeSubsetOf(rule.View, rule.InterfaceCode);
        }
    }

    private static ClaimsPrincipal User(int id, params string[] permissions) =>
        new(new ClaimsIdentity(permissions.Select(p => new Claim("permission", p)).Append(new Claim("sub", id.ToString())), "Test"));

    [Fact]
    public void A_requester_may_remove_their_own_file_from_a_requisition_and_nobody_elses()
    {
        var pr = AttachmentAccessPolicy.Find("PR")!;
        var requester = User(4, SeededRoles.Permissions(EnumRole.Requester));

        AttachmentAccessPolicy.MayRemove(requester, pr, uploadedBy: 4).Should().BeTrue();
        AttachmentAccessPolicy.MayRemove(requester, pr, uploadedBy: 1).Should().BeFalse("an approver's evidence is not theirs to strip");
    }

    [Fact]
    public void An_approver_may_remove_anybodys_file()
    {
        var pr = AttachmentAccessPolicy.Find("PR")!;
        var approver = User(2, SeededRoles.Permissions(EnumRole.ProcurementManager));

        AttachmentAccessPolicy.MayRemove(approver, pr, uploadedBy: 4).Should().BeTrue();
    }

    [Fact]
    public void A_file_with_no_known_uploader_is_nobodys_own()
    {
        var quotation = AttachmentAccessPolicy.Find("QUOTATION")!;

        // A vendor's portal upload is recorded as user 0, and a token with no "sub" reads as user 0 too.
        AttachmentAccessPolicy.MayRemove(User(0, "RFQ_CREATE"), quotation, uploadedBy: 0).Should().BeFalse();
        AttachmentAccessPolicy.MayRemove(Holding("RFQ_CREATE"), quotation, uploadedBy: 0).Should().BeFalse();
        AttachmentAccessPolicy.MayRemove(User(9, "RFQ_MANAGE"), quotation, uploadedBy: 0).Should().BeTrue();
    }

    [Fact]
    public void Owning_the_file_is_not_enough_without_the_permission()
    {
        var po = AttachmentAccessPolicy.Find("PO")!;

        AttachmentAccessPolicy.MayRemove(User(4, "PO_VIEW"), po, uploadedBy: 4).Should().BeFalse();
    }

    [Theory]
    [InlineData("PRODUCT_IMAGE", true)]
    [InlineData("PO_TEMPLATE_LOGO", true)]
    [InlineData("PO", false)]
    [InlineData("DELIVERY", false)]
    public void Only_the_kinds_shown_straight_in_an_img_take_pictures_only(string code, bool picturesOnly)
    {
        AttachmentAccessPolicy.TakesPicturesOnly(code).Should().Be(picturesOnly);
    }

    [Fact]
    public void Every_rule_lets_somebody_see_the_files()
    {
        AttachmentAccessPolicy.Rules.Should().OnlyContain(r => r.View.Count > 0);
    }

    [Fact]
    public void Nobody_adds_to_or_removes_from_a_sales_invoices_filed_copies_through_the_endpoint()
    {
        var rule = AttachmentAccessPolicy.Find("SALES_INVOICE")!;
        var everything = Holding(PermissionCodes.All.ToArray());

        AttachmentAccessPolicy.Allows(everything, rule, AttachmentAction.Upload).Should().BeFalse(
            "a file uploaded there would sit among the system's filed copies looking like one of them");
        AttachmentAccessPolicy.Allows(everything, rule, AttachmentAction.Delete).Should().BeFalse();
        AttachmentAccessPolicy.Allows(everything, rule, AttachmentAction.DeleteOwn).Should().BeFalse();
        AttachmentAccessPolicy.Allows(everything, rule, AttachmentAction.View).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("po")]                     // the codes are exact: "po" would be a second, separate list
    [InlineData("PO ")]
    [InlineData("SUPPLIER_QUOTATION")]
    [InlineData("MIR_GENERAL")]            // a workflow code, but nothing attaches files to it
    [InlineData("..\\..\\wwwroot")]
    public void A_code_that_is_not_in_the_map_has_no_rule(string? code)
    {
        AttachmentAccessPolicy.Find(code).Should().BeNull();
    }

    [Fact]
    public void Any_one_of_the_listed_permissions_is_enough_and_none_of_them_is_not()
    {
        var po = AttachmentAccessPolicy.Find("PO")!;

        AttachmentAccessPolicy.Allows(Holding("PO_APPROVE"), po, AttachmentAction.View).Should().BeTrue();
        AttachmentAccessPolicy.Allows(Holding("PO_EDIT"), po, AttachmentAction.Upload).Should().BeTrue();
        AttachmentAccessPolicy.Allows(Holding("PO_VIEW"), po, AttachmentAction.Upload).Should().BeFalse();
        AttachmentAccessPolicy.Allows(Holding("PO_VIEW"), po, AttachmentAction.Delete).Should().BeFalse();
        AttachmentAccessPolicy.Allows(Holding(), po, AttachmentAction.View).Should().BeFalse();
        AttachmentAccessPolicy.Allows(Holding("INVOICE_VIEW", "INVOICE_PROCESS"), po, AttachmentAction.View).Should().BeFalse();
    }

    // ── The seeded roles ─────────────────────────────────────────────────────

    /// <summary>
    /// The guards of every page that shows a document type's attachment panel (or uploads under its
    /// code), copied from SupplyChainFrontend/src/app/pages/pages.routes.ts. Whoever can open one of these
    /// pages today must still see the files on it.
    /// </summary>
    private static readonly Dictionary<string, string[]> OpensThePage = new()
    {
        ["PR"]               = ["REQUISITION_VIEW_OWN", "REQUISITION_VIEW_ALL", "REQUISITION_CREATE", "REQUISITION_APPROVE"], // pr-detail; pr-create is REQUISITION_CREATE
        ["PR_LINE"]          = ["REQUISITION_VIEW_OWN", "REQUISITION_VIEW_ALL", "REQUISITION_CREATE", "REQUISITION_APPROVE"], // pr-detail
        ["QUOTATION"]        = ["RFQ_VIEW", "RFQ_CREATE", "RFQ_MANAGE"],                                                    // quotation-detail; -create is RFQ_CREATE/RFQ_MANAGE
        ["RFQ_RESPONSE"]     = ["RFQ_VIEW", "RFQ_CREATE", "RFQ_MANAGE"],                                                    // quotation-detail
        ["PO"]               = ["PO_VIEW", "PO_CREATE", "PO_EDIT", "PO_APPROVE"],                                           // po-detail; po-create is PO_CREATE
        ["PO_TEMPLATE_LOGO"] = ["PO_TEMPLATE_MANAGE"],                                                                      // po-document-template
        ["GRN"]              = ["GOODS_RECEIVE", "GRN_APPROVE", "GRN_FINANCE_APPROVE", "GRN_QC_CONFIRM"],                   // grn-detail; grn-create is GOODS_RECEIVE
        ["DELIVERY"]         = ["DELIVERY_VIEW"],                                                                           // delivery-detail
        ["INVOICE"]          = ["INVOICE_VIEW", "INVOICE_PROCESS"],                                                         // invoice-detail (VIEW), invoice-create (PROCESS)
        ["SUPPLIER_PAYMENT"] = ["PAYMENT_VIEW", "PAYMENT_PROCESS"],                                                         // supplier-payment-detail; -create is PAYMENT_PROCESS
        ["SALES_INVOICE"]    = ["SALES_INVOICE_VIEW", "SALES_INVOICE_MANAGE"],                                              // sales-invoice-detail
        ["PRODUCT_IMAGE"]    = ["INVENTORY_VIEW", "STOCK_MANAGE"],                                                          // product-detail; product-create is STOCK_MANAGE
        ["SALE_INQUIRY"]     = ["SALE_INQUIRY_VIEW", "SALE_INQUIRY_CREATE", "SALE_INQUIRY_EDIT"],                           // inquiry detail; -new is SALE_INQUIRY_CREATE
        ["SALE_QUOTATION"]   = ["SALE_QUOTATION_VIEW", "SALE_QUOTATION_CREATE", "SALE_QUOTATION_EDIT", "SALE_QUOTATION_SEND"], // quotation detail
        ["CUSTOMER_PO"]      = ["SALE_ORDER_VIEW", "SALE_ORDER_CREATE", "SALE_ORDER_EDIT", "SALE_ORDER_CONFIRM"],           // sales/orders/:uuid guard
        ["SALE_ORDER"]       = ["SALE_ORDER_VIEW", "SALE_ORDER_CREATE", "SALE_ORDER_EDIT", "SALE_ORDER_CONFIRM"],           // sales/orders/:uuid guard
    };

    /// <summary>
    /// What it takes to change the document itself: the guard of its create/edit page, or the permission its
    /// controller puts on its writes — except that a permission which only ever lets somebody READ (the
    /// Auditor's REQUISITION_VIEW_ALL, admitted by pr-edit's guard) does not make them an editor.
    /// </summary>
    private static readonly Dictionary<string, string[]> ChangesTheDocument = new()
    {
        ["PR"]               = ["REQUISITION_CREATE", "REQUISITION_APPROVE"],
        ["PR_LINE"]          = ["REQUISITION_CREATE", "REQUISITION_APPROVE"],
        ["QUOTATION"]        = ["RFQ_CREATE", "RFQ_MANAGE"],          // QuotationsController: create RFQ_CREATE, send/award/cancel RFQ_MANAGE
        ["RFQ_RESPONSE"]     = ["RFQ_MANAGE"],                        // QuotationsController: recording a response is RFQ_MANAGE
        ["PO"]               = ["PO_CREATE", "PO_EDIT"],
        ["PO_TEMPLATE_LOGO"] = ["PO_TEMPLATE_MANAGE"],                // PoDocumentTemplateController
        ["GRN"]              = ["GOODS_RECEIVE", "GRN_APPROVE"],      // grn-create, grn-edit
        ["DELIVERY"]         = ["DELIVERY_EDIT"],                     // DeliveriesController: PATCH, gate-pass/attach
        ["INVOICE"]          = ["INVOICE_PROCESS"],                   // InvoicesController: every write
        ["SUPPLIER_PAYMENT"] = ["PAYMENT_PROCESS"],                   // supplier-payment-create
        ["SALES_INVOICE"]    = [],                                    // filed copies are the system's alone
        ["PRODUCT_IMAGE"]    = ["STOCK_MANAGE"],                      // product-create
        ["SALE_INQUIRY"]     = ["SALE_INQUIRY_CREATE", "SALE_INQUIRY_EDIT"],
        ["SALE_QUOTATION"]   = ["SALE_QUOTATION_CREATE", "SALE_QUOTATION_EDIT"],
        ["CUSTOMER_PO"]      = ["SALE_ORDER_CREATE", "SALE_ORDER_EDIT"], // SaleOrdersController: create, edit, customer-po
        ["SALE_ORDER"]       = ["SALE_ORDER_CREATE", "SALE_ORDER_EDIT"],
    };

    [Fact]
    public void Every_seeded_role_that_can_open_a_page_still_sees_its_files_and_every_editor_can_still_add_and_remove_them()
    {
        var failures = new List<string>();

        foreach (var (role, permissions) in SeededRoles.All)
        {
            var user = Holding(permissions);

            foreach (var code in CodesInUse)
            {
                var rule = AttachmentAccessPolicy.Find(code);
                if (rule is null) { failures.Add($"{code}: no rule"); continue; }

                var opens   = OpensThePage[code].Any(permissions.Contains);
                var changes = ChangesTheDocument[code].Any(permissions.Contains);

                if (opens != AttachmentAccessPolicy.Allows(user, rule, AttachmentAction.View))
                    failures.Add($"{role} / {code}: opens the page = {opens}, may list = {!opens}");
                if (changes != AttachmentAccessPolicy.Allows(user, rule, AttachmentAction.Upload))
                    failures.Add($"{role} / {code}: changes the document = {changes}, may upload = {!changes}");
                var mayRemoveSome = AttachmentAccessPolicy.Allows(user, rule, AttachmentAction.Delete)
                                 || AttachmentAccessPolicy.Allows(user, rule, AttachmentAction.DeleteOwn);
                if (changes != mayRemoveSome)
                    failures.Add($"{role} / {code}: changes the document = {changes}, may remove (at least their own) = {mayRemoveSome}");
            }
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public void The_auditor_sees_every_document_it_can_open_and_can_add_or_remove_nothing_anywhere()
    {
        var auditor = Holding(SeededRoles.Permissions(EnumRole.Auditor));

        var visible = AttachmentAccessPolicy.Rules
            .Where(r => AttachmentAccessPolicy.Allows(auditor, r, AttachmentAction.View)).Select(r => r.InterfaceCode);
        visible.Should().BeEquivalentTo(
            "PR", "PR_LINE", "QUOTATION", "RFQ_RESPONSE", "PO", "INVOICE", "SUPPLIER_PAYMENT", "SALES_INVOICE", "PRODUCT_IMAGE");

        AttachmentAccessPolicy.Rules.Should().OnlyContain(r =>
            !AttachmentAccessPolicy.Allows(auditor, r, AttachmentAction.Upload) &&
            !AttachmentAccessPolicy.Allows(auditor, r, AttachmentAction.Delete) &&
            !AttachmentAccessPolicy.Allows(auditor, r, AttachmentAction.DeleteOwn),
            "the Auditor is \"view only — no data modification\"");
    }

    [Fact]
    public void A_requester_works_with_requisitions_and_sees_nothing_else()
    {
        var requester = Holding(SeededRoles.Permissions(EnumRole.Requester));

        AttachmentAccessPolicy.Rules.Where(r => AttachmentAccessPolicy.Allows(requester, r, AttachmentAction.View))
            .Select(r => r.InterfaceCode).Should().BeEquivalentTo("PR", "PR_LINE");
        AttachmentAccessPolicy.Rules.Where(r => AttachmentAccessPolicy.Allows(requester, r, AttachmentAction.Upload))
            .Select(r => r.InterfaceCode).Should().BeEquivalentTo("PR", "PR_LINE");
    }

    [Theory]
    [InlineData(EnumRole.SystemAdmin)]
    [InlineData(EnumRole.OrgAdmin)]
    public void An_administrator_may_do_everything_except_touch_a_sales_invoices_filed_copies(EnumRole role)
    {
        var admin = Holding(SeededRoles.Permissions(role));

        AttachmentAccessPolicy.Rules.Should().OnlyContain(r => AttachmentAccessPolicy.Allows(admin, r, AttachmentAction.View));
        AttachmentAccessPolicy.Rules.Where(r => AttachmentAccessPolicy.Allows(admin, r, AttachmentAction.Upload))
            .Select(r => r.InterfaceCode).Should().BeEquivalentTo(CodesInUse.Except(["SALES_INVOICE"]));
    }
}

using System.Security.Claims;
using SMS.Shared.Authorization;

namespace SMS.WorkflowEngine.Services;

/// <summary>What a caller wants to do with a document's attachments.</summary>
public enum AttachmentAction
{
    View,
    Upload,
    /// <summary>Remove any uploaded file — the reviewer's or manager's say over the document.</summary>
    Delete,
    /// <summary>Remove a file the caller uploaded themselves, and no one else's.</summary>
    DeleteOwn
}

/// <summary>
/// Who may list, add and remove the attachments of one kind of document. Each list is "any one of":
/// holding a single code in it is enough. An empty list means nobody may, through <c>api/attachments</c>.
/// <see cref="Delete"/> is removing anybody's file; <see cref="DeleteOwn"/> only one the caller uploaded —
/// so a Requester can take back their own mistake without being able to strip an approver's evidence.
/// </summary>
public sealed record AttachmentAccessRule(
    string InterfaceCode, IReadOnlyList<string> View, IReadOnlyList<string> Upload,
    IReadOnlyList<string> Delete, IReadOnlyList<string> DeleteOwn)
{
    public IReadOnlyList<string> For(AttachmentAction action) => action switch
    {
        AttachmentAction.View      => View,
        AttachmentAction.Upload    => Upload,
        AttachmentAction.Delete    => Delete,
        AttachmentAction.DeleteOwn => DeleteOwn,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };
}

/// <summary>
/// The one place that says which permission lets a caller see, add and remove the files attached to each
/// kind of document — read by <c>AttachmentsController</c> on every request, and handed to the frontend
/// (<c>GET api/attachments/policy</c>) so the attachment panel offers only what the server will allow.
/// <para>
/// <b>Why it is needed.</b> The endpoint is generic — keyed by interface code + document id — and asked only
/// for a sign-in, so a view-only Auditor could attach a file to any document, and anybody could remove
/// anything. It cannot ask the owning module (WorkflowEngine is referenced by them, not the other way
/// round), so the rules are written down here instead.
/// </para>
/// <para>
/// <b>Where each rule comes from.</b> "View" is the set of permissions that open a page showing the panel
/// (the frontend route guards, which match the controllers' GETs where those check anything at all);
/// "Upload"/"Delete" is what it takes to change the document itself. A permission that only ever lets
/// somebody read — the Auditor's <c>REQUISITION_VIEW_ALL</c>, admitted by the PR edit page's guard — does
/// not make them an editor. Whoever may change a list may also see it.
/// </para>
/// <para>
/// <b>A whitelist.</b> A code not in here has no rule and is refused outright. The code used to be taken
/// as it came, and even became a folder name on disk.
/// </para>
/// </summary>
public static class AttachmentAccessPolicy
{
    /// <param name="removeAny">Who may remove anybody's file.</param>
    /// <param name="removeOwn">Who may remove only what they uploaded — the adders who are not reviewers.</param>
    private static AttachmentAccessRule Rule(string code, string[] view, string[] add, string[] removeAny, string[]? removeOwn = null) =>
        new(code, view, add, removeAny, removeOwn ?? []);

    private static readonly string[] RequisitionViewers =
    [
        PermissionCodes.REQUISITION_VIEW_OWN, PermissionCodes.REQUISITION_VIEW_ALL,
        PermissionCodes.REQUISITION_CREATE,   PermissionCodes.REQUISITION_APPROVE
    ];
    // Whoever raises a requisition, and whoever approves one (Procurement Manager holds APPROVE, not CREATE).
    private static readonly string[] RequisitionEditors = [PermissionCodes.REQUISITION_CREATE, PermissionCodes.REQUISITION_APPROVE];

    private static readonly string[] RfqViewers = [PermissionCodes.RFQ_VIEW, PermissionCodes.RFQ_CREATE, PermissionCodes.RFQ_MANAGE];

    private static readonly string[] SaleOrderViewers =
    [
        PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.SALE_ORDER_CREATE, PermissionCodes.SALE_ORDER_EDIT, PermissionCodes.SALE_ORDER_CONFIRM
    ];
    private static readonly string[] SaleOrderEditors = [PermissionCodes.SALE_ORDER_CREATE, PermissionCodes.SALE_ORDER_EDIT];

    public static IReadOnlyList<AttachmentAccessRule> Rules { get; } =
    [
        // A requester takes back their own file; only an approver removes somebody else's.
        Rule("PR",      RequisitionViewers, RequisitionEditors, [PermissionCodes.REQUISITION_APPROVE], [PermissionCodes.REQUISITION_CREATE]),
        Rule("PR_LINE", RequisitionViewers, RequisitionEditors, [PermissionCodes.REQUISITION_APPROVE], [PermissionCodes.REQUISITION_CREATE]),

        // QuotationsController: creating is RFQ_CREATE; sending, awarding and cancelling are RFQ_MANAGE.
        Rule("QUOTATION", RfqViewers, [PermissionCodes.RFQ_CREATE, PermissionCodes.RFQ_MANAGE],
            [PermissionCodes.RFQ_MANAGE], [PermissionCodes.RFQ_CREATE]),
        // A vendor's own uploads arrive through the public RFQ portal, not here. Recording a response on the
        // vendor's behalf is RFQ_MANAGE, and so is attaching what they sent with it.
        Rule("RFQ_RESPONSE", RfqViewers, [PermissionCodes.RFQ_MANAGE], [PermissionCodes.RFQ_MANAGE]),

        // Raising a PO is PO_CREATE, changing one PO_EDIT.
        Rule("PO",
            [PermissionCodes.PO_VIEW, PermissionCodes.PO_CREATE, PermissionCodes.PO_EDIT, PermissionCodes.PO_APPROVE],
            [PermissionCodes.PO_CREATE, PermissionCodes.PO_EDIT],
            [PermissionCodes.PO_EDIT], [PermissionCodes.PO_CREATE]),
        // The organization's PO letterhead logo — PoDocumentTemplateController is PO_TEMPLATE_MANAGE throughout.
        Rule("PO_TEMPLATE_LOGO", [PermissionCodes.PO_TEMPLATE_MANAGE], [PermissionCodes.PO_TEMPLATE_MANAGE], [PermissionCodes.PO_TEMPLATE_MANAGE]),

        // Receiving is GOODS_RECEIVE; the Inventory Manager's GRN_APPROVE is the say over a GRN.
        Rule("GRN",
            [PermissionCodes.GOODS_RECEIVE, PermissionCodes.GRN_APPROVE, PermissionCodes.GRN_FINANCE_APPROVE, PermissionCodes.GRN_QC_CONFIRM],
            [PermissionCodes.GOODS_RECEIVE, PermissionCodes.GRN_APPROVE],
            [PermissionCodes.GRN_APPROVE], [PermissionCodes.GOODS_RECEIVE]),

        // DeliveriesController: reading is DELIVERY_VIEW, every change (and filing a gate pass) DELIVERY_EDIT.
        Rule("DELIVERY", [PermissionCodes.DELIVERY_VIEW, PermissionCodes.DELIVERY_EDIT], [PermissionCodes.DELIVERY_EDIT], [PermissionCodes.DELIVERY_EDIT]),

        // InvoicesController: reading is INVOICE_VIEW, entering and every change INVOICE_PROCESS.
        Rule("INVOICE", [PermissionCodes.INVOICE_VIEW, PermissionCodes.INVOICE_PROCESS], [PermissionCodes.INVOICE_PROCESS], [PermissionCodes.INVOICE_PROCESS]),
        // SupplierPaymentsController: reading is PAYMENT_VIEW; recording, posting, cancelling PAYMENT_PROCESS.
        Rule("SUPPLIER_PAYMENT", [PermissionCodes.PAYMENT_VIEW, PermissionCodes.PAYMENT_PROCESS], [PermissionCodes.PAYMENT_PROCESS], [PermissionCodes.PAYMENT_PROCESS]),

        // The invoice's filed copies (SalesInvoiceDocumentArchive) are the system's alone: a file uploaded
        // among them would look like one of them, so nobody adds or removes anything here.
        Rule("SALES_INVOICE", [PermissionCodes.SALES_INVOICE_VIEW, PermissionCodes.SALES_INVOICE_MANAGE], [], []),

        // Products are browsed with INVENTORY_VIEW and maintained with STOCK_MANAGE.
        Rule("PRODUCT_IMAGE", [PermissionCodes.INVENTORY_VIEW, PermissionCodes.STOCK_MANAGE], [PermissionCodes.STOCK_MANAGE], [PermissionCodes.STOCK_MANAGE]),

        // A32 — the sales pre-order pipeline (docs/sales-preorder/API-CONTRACT.md §3). Whoever records an inquiry or
        // raises a quotation takes back their own file; whoever edits it removes anybody's.
        Rule("SALE_INQUIRY",
            [PermissionCodes.SALE_INQUIRY_VIEW, PermissionCodes.SALE_INQUIRY_CREATE, PermissionCodes.SALE_INQUIRY_EDIT],
            [PermissionCodes.SALE_INQUIRY_CREATE, PermissionCodes.SALE_INQUIRY_EDIT],
            [PermissionCodes.SALE_INQUIRY_EDIT], [PermissionCodes.SALE_INQUIRY_CREATE]),
        Rule("SALE_QUOTATION",
            [PermissionCodes.SALE_QUOTATION_VIEW, PermissionCodes.SALE_QUOTATION_CREATE, PermissionCodes.SALE_QUOTATION_EDIT, PermissionCodes.SALE_QUOTATION_SEND],
            [PermissionCodes.SALE_QUOTATION_CREATE, PermissionCodes.SALE_QUOTATION_EDIT],
            [PermissionCodes.SALE_QUOTATION_EDIT], [PermissionCodes.SALE_QUOTATION_CREATE]),
        // A sale order's files — the customer's PO document, and anything else — keyed by the order's uuid. Viewed with
        // whatever opens the order page (its route guard), changed with SALE_ORDER_CREATE / SALE_ORDER_EDIT.
        Rule("CUSTOMER_PO", SaleOrderViewers, SaleOrderEditors, [PermissionCodes.SALE_ORDER_EDIT], [PermissionCodes.SALE_ORDER_CREATE]),
        Rule("SALE_ORDER",  SaleOrderViewers, SaleOrderEditors, [PermissionCodes.SALE_ORDER_EDIT], [PermissionCodes.SALE_ORDER_CREATE]),
    ];

    /// <summary>
    /// Kinds of attachment the app shows straight from their public URL in an <c>&lt;img&gt;</c> — a product's
    /// picture, the PO letterhead's logo — so nothing but a picture may be uploaded there.
    /// </summary>
    public static bool TakesPicturesOnly(string interfaceCode) => interfaceCode is "PRODUCT_IMAGE" or "PO_TEMPLATE_LOGO";

    /// <summary>
    /// Whether this caller may remove this uploaded file: anybody's with <see cref="AttachmentAccessRule.Delete"/>,
    /// or one they uploaded themselves with <see cref="AttachmentAccessRule.DeleteOwn"/>. Says nothing about a
    /// filed document, which nobody may remove.
    /// </summary>
    public static bool MayRemove(ClaimsPrincipal user, AttachmentAccessRule rule, int uploadedBy)
    {
        if (Allows(user, rule, AttachmentAction.Delete)) return true;

        var caller = user.GetUserId();
        return caller > 0 && caller == uploadedBy && Allows(user, rule, AttachmentAction.DeleteOwn);
    }

    // Exact, case-sensitive: "po" is not "PO", and would otherwise become a second, separate list.
    private static readonly Dictionary<string, AttachmentAccessRule> ByCode =
        Rules.ToDictionary(r => r.InterfaceCode, StringComparer.Ordinal);

    /// <summary>The rule for this kind of document, or <c>null</c> when nothing may attach files to it.</summary>
    public static AttachmentAccessRule? Find(string? interfaceCode) =>
        interfaceCode is not null && ByCode.TryGetValue(interfaceCode, out var rule) ? rule : null;

    public static bool Allows(ClaimsPrincipal user, AttachmentAccessRule rule, AttachmentAction action) =>
        rule.For(action).Any(user.HasPermission);
}

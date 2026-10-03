using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using SMS.Modules.Suppliers.Controllers;
using SMS.Shared.Authorization;
using Xunit;
using static SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Suppliers.Tests.Permissions;

/// <summary>
/// OPEN — the 2026-10-02 permission sweep was paused before this controller was gated (docs/security/permission-sweep.md).
/// /api/suppliers answers every signed-in user of the organization: its [RequirePermission] lines are commented out
/// (P1-05's notes record the state, not a reason), so an Auditor can approve or blacklist a supplier and a Purchase
/// Officer can rewrite its bank account. <see cref="Expected"/> is the mapping the sweep worked out for when it resumes:
/// each action admits the codes of the pages and buttons that call it — pages.routes.ts' <c>permissionGuard(...)</c>
/// (ANY of its codes), app.menu.ts and the components' <c>hasPermission(...)</c> — so nobody who can open a page today
/// is refused the call it makes. The supplier pages offer their writes to every viewer, so gating those needs the
/// buttons to check the same codes (supplier-detail, supplier-list's delete, quotation-detail's contact edit).
/// The facts that fail on today's code are skipped; the ones that hold are live.
/// </summary>
public class SuppliersControllerPermissionTests
{
    private const string Open = "Open: see docs/security/permission-sweep.md";

    // ── Who reaches each kind of call (pages.routes.ts / app.menu.ts / the components' hasPermission) ──

    /// <summary>suppliers/supplier-list and the "All Suppliers" menu entry.</summary>
    internal static readonly string[] SupplierScreens = [SUPPLIER_VIEW, SUPPLIER_CREATE, SUPPLIER_EDIT, SUPPLIER_MANAGE];

    /// <summary>suppliers/supplier-detail/:uuid — its read-only tabs (documents, timeline).</summary>
    internal static readonly string[] SupplierDetailPage = [SUPPLIER_VIEW, SUPPLIER_EDIT, SUPPLIER_MANAGE];

    /// <summary>suppliers/supplier-create and the "New Supplier" menu entry.</summary>
    internal static readonly string[] SupplierCreators = [SUPPLIER_CREATE, SUPPLIER_MANAGE];

    /// <summary>supplier-detail's Edit, Add Contact and document buttons (canEditSupplier).</summary>
    internal static readonly string[] SupplierEditors = [SUPPLIER_EDIT, SUPPLIER_MANAGE];

    /// <summary>supplier-detail's status actions and bank edit, supplier-list's delete (canManageSupplier).</summary>
    internal static readonly string[] SupplierManagers = [SUPPLIER_MANAGE];

    /// <summary>
    /// GET /api/suppliers is the vendor picker of half the app, so it admits every page that offers one: the supplier
    /// screens and rate cards; PO create/edit; the PR's Convert to PO dialog (pr-detail, shown on status alone to any
    /// requisition user); the RFQ's Send and Record Response dialogs; Purchase Required's supplier filter; supplier
    /// payment create; the master payables ledger; invoice create; SRO create; product create/edit; the carrier list's
    /// supplier link; and the Users page's supplier mapping. The dashboard tiles and the topbar search call it too but
    /// swallow a 403 (safe()/catchError), so they widen nothing.
    /// </summary>
    internal static readonly string[] VendorPicker =
    [
        .. SupplierScreens,
        PO_CREATE, PO_EDIT,
        REQUISITION_VIEW_OWN, REQUISITION_VIEW_ALL, REQUISITION_CREATE, REQUISITION_APPROVE,
        RFQ_VIEW, RFQ_CREATE, RFQ_MANAGE,
        SUPPLY_VIEW,
        PAYMENT_PROCESS, PAYMENT_VIEW, INVOICE_VIEW, INVOICE_PROCESS,
        GOODS_RECEIVE, WAREHOUSE_TRANSFER,
        INVENTORY_VIEW, STOCK_MANAGE,
        DELIVERY_TRACK,
        USER_MANAGE,
    ];

    /// <summary>One supplier's record: supplier-detail, the payment page locked to a supplier (PAYMENT_PROCESS) and the
    /// carrier list's linked-supplier lookup (DELIVERY_TRACK).</summary>
    internal static readonly string[] SupplierReaders = [.. SupplierDetailPage, PAYMENT_PROCESS, DELIVERY_TRACK];

    /// <summary>Who a supplier can be sent to: the RFQ's Send dialog (quotation-detail, any RFQ code) and the PO's
    /// Send to Supplier (po-detail, any PO code) — both offered on status alone. It was RFQ_CREATE only, which refused
    /// the Purchase Officer sending an approved PO and the RFQ_MANAGE holder who is the one allowed to send an RFQ.</summary>
    internal static readonly string[] ContactPickers = [RFQ_VIEW, RFQ_CREATE, RFQ_MANAGE, PO_VIEW, PO_CREATE, PO_EDIT, PO_APPROVE];

    /// <summary>Bank details: supplier managers, and finance's invoice readers who pay against them (the check that
    /// used to live in the action's body, unchanged).</summary>
    internal static readonly string[] BankReaders = [SUPPLIER_MANAGE, INVOICE_VIEW];

    public static readonly (string Action, string[] Codes)[] Expected =
    [
        (nameof(SuppliersController.CreateSupplier),        SupplierCreators),
        (nameof(SuppliersController.GetSuppliers),          VendorPicker),
        (nameof(SuppliersController.GetSupplierById),       SupplierReaders),
        (nameof(SuppliersController.PatchSupplier),         SupplierEditors),
        (nameof(SuppliersController.AddContact),            SupplierEditors),
        (nameof(SuppliersController.UpdateContact),         SupplierEditors),
        (nameof(SuppliersController.GetEligibleContacts),   ContactPickers),
        (nameof(SuppliersController.ApproveSupplier),       SupplierManagers),
        (nameof(SuppliersController.RejectSupplier),        SupplierManagers),
        (nameof(SuppliersController.BlacklistSupplier),     SupplierManagers),
        (nameof(SuppliersController.SuspendSupplier),       SupplierManagers),
        (nameof(SuppliersController.DeleteSupplier),        SupplierManagers),
        (nameof(SuppliersController.UpsertBankDetail),      SupplierManagers),
        (nameof(SuppliersController.GetBankDetail),         BankReaders),
        (nameof(SuppliersController.AttachDocument),        SupplierEditors),
        (nameof(SuppliersController.UploadDocument),        SupplierEditors),
        (nameof(SuppliersController.GetDocuments),          SupplierDetailPage),
        (nameof(SuppliersController.SoftDeleteDocument),    SupplierEditors),
        // The legacy dropdowns: nothing in the frontend calls them (the supplier forms read lookup values).
        // Reading follows the supplier screens; changing this organization's types is supplier management.
        (nameof(SuppliersController.GetSupplierTypes),      SupplierScreens),
        (nameof(SuppliersController.GetCategories),         SupplierScreens),
        (nameof(SuppliersController.CreateSupplierType),    SupplierManagers),
        (nameof(SuppliersController.UpdateSupplierType),    SupplierManagers),
        (nameof(SuppliersController.DeleteSupplierType),    SupplierManagers),
        (nameof(SuppliersController.DeleteSupplierCategory), SupplierManagers),
    ];

    public static IEnumerable<object[]> ExpectedRows() => Expected.Select(e => new object[] { e.Action, e.Codes });

    private static IEnumerable<MethodInfo> Actions =>
        typeof(SuppliersController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetCustomAttributes<HttpMethodAttribute>().Any());

    [Fact]
    public void The_controller_has_the_actions_this_test_knows_about()
    {
        // Guards the guard: a reflection query that silently matches nothing passes every assertion below.
        Actions.Should().HaveCount(24);
        Expected.Select(e => e.Action).Should().OnlyHaveUniqueItems().And.BeEquivalentTo(Actions.Select(a => a.Name));
    }

    [Fact(Skip = Open)]
    public void Every_action_has_exactly_one_permission_and_none_is_anonymous()
    {
        Actions.Where(a => a.GetCustomAttributes<RequirePermissionAttribute>().Count() != 1
                        || a.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Select(a => a.Name)
            .Should().BeEmpty("a supplier must not be readable or changeable by every signed-in user of the organization");

        typeof(SuppliersController).GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
        // Per action, not per controller: a class-level attribute would be ANDed with each action's.
        typeof(SuppliersController).GetCustomAttributes<RequirePermissionAttribute>().Should().BeEmpty();
    }

    [Theory(Skip = Open)]
    [MemberData(nameof(ExpectedRows))]
    public void Each_action_admits_exactly_the_codes_of_the_pages_and_buttons_that_call_it(string action, string[] codes)
    {
        var attribute = typeof(SuppliersController).GetMethod(action)!.GetCustomAttribute<RequirePermissionAttribute>();

        attribute.Should().NotBeNull($"{action} must name a permission");
        attribute!.AnyOf.Should().BeEquivalentTo(codes, action);
        RequirePermissionAttribute.CodesOf(attribute.Policy!).Should().BeEquivalentTo(codes, "the policy name must carry the same codes");
    }

    [Fact]
    public void Every_action_on_one_supplier_still_checks_the_users_supplier_access()
    {
        var unchecked_ = Actions
            .Where(a => a.GetCustomAttributes<HttpMethodAttribute>().Any(h => h.Template?.StartsWith("{uuid:guid}") == true))
            .Where(a => a.GetCustomAttribute<RequiresSupplierAccessAttribute>() is null)
            .Select(a => a.Name);

        unchecked_.Should().BeEmpty("REQ-2.x: a supplier the user is not mapped to is a 403 by id, whatever their permissions");
    }

    [Fact(Skip = Open)]
    public void Deleting_a_supplier_type_answers_only_DELETE_types_id()
    {
        // The commented-out UpdateCategory left its [HttpPut("categories/{id:guid}")] behind on the next method, so a
        // PUT to a category's address deletes the supplier TYPE with that id (and checks no permission).
        var verbs = typeof(SuppliersController).GetMethod(nameof(SuppliersController.DeleteSupplierType))!
            .GetCustomAttributes<HttpMethodAttribute>().ToList();

        verbs.Should().ContainSingle();
        verbs[0].Should().BeOfType<Microsoft.AspNetCore.Mvc.HttpDeleteAttribute>();
        verbs[0].Template.Should().Be("types/{id:guid}");
    }
}

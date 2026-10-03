using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SMS.Modules.Finance.Controllers;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Authorization;
using Xunit;
using static SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Finance.Tests.Authorization;

/// <summary>
/// The supplier-side Finance controllers that used to answer any signed-in user of the organization — supplier
/// ledger, supplier payments (and the payables reports that live on that controller), the legacy payments, the
/// master payables ledger, the master product ledger, credit notes and debit notes — now name a permission on
/// every action. Each action admits exactly the users the frontend lets reach the page or button that calls it
/// (pages.routes.ts' <c>permissionGuard(...)</c>, which takes ANY of its codes, and the components'
/// <c>hasPermission(...)</c>), so nobody who can open a page today is locked out of the call it makes.
/// </summary>
public class FinanceControllerPermissionTests
{
    private static readonly Type[] Controllers =
    [
        typeof(SupplierLedgerController), typeof(SupplierPaymentsController), typeof(PaymentsController),
        typeof(MasterLedgerController), typeof(MasterProductLedgerController),
        typeof(CreditNotesController), typeof(DebitNotesController),
    ];

    private static IEnumerable<MethodInfo> ActionsOf(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                  .Where(m => !m.IsSpecialName && m.GetCustomAttributes<HttpMethodAttribute>().Any());

    private static MethodInfo Action(Type controller, string action) =>
        controller.GetMethod(action, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;

    // ── What each kind of endpoint admits ────────────────────────────────────

    /// <summary>One supplier's payables (ledger, balance, outstanding invoices, aging): supplier-detail and
    /// partner-detail show them to every SUPPLIER_VIEW/EDIT/MANAGE holder; the payment-create page (PAYMENT_PROCESS)
    /// loads the outstanding invoices; and finance's readers see the same rows on the master payables ledger.</summary>
    private static readonly string[] SupplierPayables =
        [SUPPLIER_VIEW, SUPPLIER_EDIT, SUPPLIER_MANAGE, INVOICE_VIEW, PAYMENT_VIEW, PAYMENT_PROCESS];

    /// <summary>Cross-supplier payables reports: finance's readers, and the finance reports page's guard
    /// (REPORT_VIEW or REPORT_EXPORT), which already shows invoice aging and payment summaries.</summary>
    private static readonly string[] PayablesReports =
        [PAYMENT_VIEW, PAYMENT_PROCESS, INVOICE_VIEW, REPORT_VIEW, REPORT_EXPORT];

    /// <summary>finance/payments, finance/payments/:uuid and both payments-legacy routes.</summary>
    private static readonly string[] PaymentPages = [PAYMENT_VIEW, PAYMENT_PROCESS];

    /// <summary>finance/master-ledger (and its menu entry).</summary>
    private static readonly string[] MasterLedgerPage = [PAYMENT_VIEW, INVOICE_VIEW];

    /// <summary>inventory/master-product-ledger (and its menu entry).</summary>
    private static readonly string[] MasterProductLedgerPage = [INVENTORY_VIEW, STOCK_MANAGE];

    /// <summary>Raising a credit or debit note: finance (it reduces what a supplier invoice asks for), and the SRO
    /// detail page (warehouse/sro/:uuid: GOODS_RECEIVE or WAREHOUSE_TRANSFER), which resolves a supplier return by
    /// raising one and offers the button on status alone.</summary>
    private static readonly string[] NoteRaisers = [INVOICE_PROCESS, GOODS_RECEIVE, WAREHOUSE_TRANSFER];

    public static readonly (Type Controller, string Action, string[] Codes)[] Expected =
    [
        (typeof(SupplierLedgerController), nameof(SupplierLedgerController.GetLedger),  SupplierPayables),
        (typeof(SupplierLedgerController), nameof(SupplierLedgerController.GetBalance), SupplierPayables),

        (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.Create),  [PAYMENT_PROCESS]),
        (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.GetList), PaymentPages),
        (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.GetById), PaymentPages),
        (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.Approve), [PAYMENT_APPROVE]),
        (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.Cancel),  [PAYMENT_PROCESS]),
        (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.Post),    [PAYMENT_PROCESS]),
        (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.Bounce),  [PAYMENT_PROCESS]),
        (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.GetOutstandingInvoices),  SupplierPayables),
        (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.GetSupplierAging),        SupplierPayables),
        // Literally GetLedger's call, so the same gate: the same rows must not be readable by more people here.
        (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.GetSupplierLedgerReport), SupplierPayables),
        (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.GetCrossSupplierAging),     PayablesReports),
        (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.GetPaymentRegister),        PayablesReports),
        (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.GetOutstandingPayables),    PayablesReports),
        (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.GetPaymentMethodBreakdown), PayablesReports),

        (typeof(PaymentsController), nameof(PaymentsController.Create),  [PAYMENT_PROCESS]),
        (typeof(PaymentsController), nameof(PaymentsController.GetList), PaymentPages),
        (typeof(PaymentsController), nameof(PaymentsController.GetById), PaymentPages),
        (typeof(PaymentsController), nameof(PaymentsController.Patch),   [PAYMENT_PROCESS]),

        (typeof(MasterLedgerController), nameof(MasterLedgerController.GetLedger),   MasterLedgerPage),
        (typeof(MasterLedgerController), nameof(MasterLedgerController.GetSummary),  MasterLedgerPage),
        (typeof(MasterLedgerController), nameof(MasterLedgerController.GetBalance),  MasterLedgerPage),
        (typeof(MasterLedgerController), nameof(MasterLedgerController.ExportPdf),   MasterLedgerPage),
        (typeof(MasterLedgerController), nameof(MasterLedgerController.ExportExcel), MasterLedgerPage),
        (typeof(MasterLedgerController), nameof(MasterLedgerController.GetWriteOff), MasterLedgerPage),
        // These three were already gated and stay as they were.
        (typeof(MasterLedgerController), nameof(MasterLedgerController.ImportOpeningBalance), [SYSTEM_CONFIGURE]),
        (typeof(MasterLedgerController), nameof(MasterLedgerController.CreateWriteOff),       [PAYMENT_PROCESS]),
        (typeof(MasterLedgerController), nameof(MasterLedgerController.ApproveWriteOff),      [PAYMENT_APPROVE]),
        (typeof(MasterLedgerController), nameof(MasterLedgerController.RejectWriteOff),       [PAYMENT_APPROVE]),

        (typeof(MasterProductLedgerController), nameof(MasterProductLedgerController.GetLedger),         MasterProductLedgerPage),
        (typeof(MasterProductLedgerController), nameof(MasterProductLedgerController.GetSummary),        MasterProductLedgerPage),
        (typeof(MasterProductLedgerController), nameof(MasterProductLedgerController.GetProductJourney), MasterProductLedgerPage),
        (typeof(MasterProductLedgerController), nameof(MasterProductLedgerController.ExportPdf),         MasterProductLedgerPage),
        (typeof(MasterProductLedgerController), nameof(MasterProductLedgerController.ExportExcel),       MasterProductLedgerPage),

        (typeof(CreditNotesController), nameof(CreditNotesController.CreateCreditNote),     NoteRaisers),
        (typeof(CreditNotesController), nameof(CreditNotesController.GetCreditNotes),       [INVOICE_VIEW]),
        (typeof(CreditNotesController), nameof(CreditNotesController.GetCreditNoteById),    [INVOICE_VIEW]),
        (typeof(CreditNotesController), nameof(CreditNotesController.ApplyCarriedForward),  [INVOICE_PROCESS]),

        (typeof(DebitNotesController), nameof(DebitNotesController.CreateDebitNote),     NoteRaisers),
        (typeof(DebitNotesController), nameof(DebitNotesController.GetDebitNotes),       [INVOICE_VIEW]),
        (typeof(DebitNotesController), nameof(DebitNotesController.GetDebitNoteById),    [INVOICE_VIEW]),
        (typeof(DebitNotesController), nameof(DebitNotesController.UpdateStatus),        [INVOICE_PROCESS]),
        (typeof(DebitNotesController), nameof(DebitNotesController.ApplyCarriedForward), [INVOICE_PROCESS]),
    ];

    public static IEnumerable<object[]> ExpectedRows() =>
        Expected.Select(e => new object[] { e.Controller, e.Action, e.Codes });

    // ── Reflection ───────────────────────────────────────────────────────────

    [Fact]
    public void The_controllers_and_their_actions_are_actually_found()
    {
        // Guards the guard: a reflection query that silently matches nothing passes every assertion below.
        Controllers.SelectMany(ActionsOf).Should().HaveCount(44);
    }

    [Fact]
    public void Every_action_has_exactly_one_permission_and_none_is_anonymous()
    {
        var wrong = Controllers.SelectMany(c => ActionsOf(c).Select(a => (Controller: c, Action: a)))
            .Where(x => x.Action.GetCustomAttributes<RequirePermissionAttribute>().Count() != 1
                     || x.Action.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .ToList();

        wrong.Should().BeEmpty("supplier payables, payments and notes must not be readable or changeable by every signed-in user");

        foreach (var controller in Controllers)
        {
            controller.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull(controller.Name);
            // Per action, not per controller: a class-level attribute would be ANDed with each action's.
            controller.GetCustomAttributes<RequirePermissionAttribute>().Should().BeEmpty(controller.Name);
        }
    }

    [Fact]
    public void The_expectations_cover_every_action_and_nothing_else()
    {
        var actual   = Controllers.SelectMany(c => ActionsOf(c).Select(a => $"{c.Name}.{a.Name}"));
        var expected = Expected.Select(e => $"{e.Controller.Name}.{e.Action}");

        expected.Should().OnlyHaveUniqueItems();
        expected.Should().BeEquivalentTo(actual);
    }

    [Theory]
    [MemberData(nameof(ExpectedRows))]
    public void Each_action_admits_exactly_the_codes_of_the_pages_that_call_it(Type controller, string action, string[] codes)
    {
        var attribute = Action(controller, action).GetCustomAttribute<RequirePermissionAttribute>();

        attribute.Should().NotBeNull($"{controller.Name}.{action} must name a permission");
        attribute!.AnyOf.Should().BeEquivalentTo(codes, $"{controller.Name}.{action}");
        RequirePermissionAttribute.CodesOf(attribute.Policy!).Should().BeEquivalentTo(codes, "the policy name must carry the same codes");
    }

    /// <summary>
    /// The frontend side of each call, as read from pages.routes.ts (a route's guard), app.menu.ts and the
    /// components' hasPermission checks (a button's). Every code that lets someone reach the call must be accepted.
    /// </summary>
    public static IEnumerable<object[]> FrontendCallers() => new (string Page, string[] Reach, Type Controller, string Action)[]
    {
        ("suppliers/supplier-detail/:uuid (Ledger tab)",               [SUPPLIER_VIEW, SUPPLIER_EDIT, SUPPLIER_MANAGE], typeof(SupplierLedgerController), nameof(SupplierLedgerController.GetLedger)),
        ("suppliers/supplier-detail/:uuid (Ledger tab)",               [SUPPLIER_VIEW, SUPPLIER_EDIT, SUPPLIER_MANAGE], typeof(SupplierLedgerController), nameof(SupplierLedgerController.GetBalance)),
        ("suppliers/supplier-detail/:uuid (Outstanding Invoices tab)", [SUPPLIER_VIEW, SUPPLIER_EDIT, SUPPLIER_MANAGE], typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.GetOutstandingInvoices)),
        ("suppliers/partner-detail/:uuid (Ledger tab)",                [SUPPLIER_VIEW, SUPPLIER_EDIT, SUPPLIER_MANAGE], typeof(SupplierLedgerController), nameof(SupplierLedgerController.GetLedger)),
        ("suppliers/partner-detail/:uuid (Ledger tab)",                [SUPPLIER_VIEW, SUPPLIER_EDIT, SUPPLIER_MANAGE], typeof(SupplierLedgerController), nameof(SupplierLedgerController.GetBalance)),
        ("finance/payments/create",                                    [PAYMENT_PROCESS],                               typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.GetOutstandingInvoices)),
        ("finance/payments/create",                                    [PAYMENT_PROCESS],                               typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.Create)),
        ("finance/payments",                                           [PAYMENT_VIEW, PAYMENT_PROCESS],                 typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.GetList)),
        ("finance/payments/:uuid",                                     [PAYMENT_VIEW, PAYMENT_PROCESS],                 typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.GetById)),
        ("finance/payments/:uuid Approve (hasPermission)",             [PAYMENT_APPROVE],                               typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.Approve)),
        ("finance/payments/:uuid Post (hasPermission)",                [PAYMENT_PROCESS],                               typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.Post)),
        ("finance/payments/:uuid Bounce (hasPermission)",              [PAYMENT_PROCESS],                               typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.Bounce)),
        ("finance/payments/:uuid Cancel (hasPermission)",              [PAYMENT_PROCESS],                               typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.Cancel)),
        ("finance/payments-legacy",                                    [PAYMENT_VIEW, PAYMENT_PROCESS],                 typeof(PaymentsController), nameof(PaymentsController.GetList)),
        ("finance/payments-legacy/:uuid",                              [PAYMENT_VIEW, PAYMENT_PROCESS],                 typeof(PaymentsController), nameof(PaymentsController.GetById)),
        ("finance/master-ledger",                                      [PAYMENT_VIEW, INVOICE_VIEW],                    typeof(MasterLedgerController), nameof(MasterLedgerController.GetLedger)),
        ("finance/master-ledger",                                      [PAYMENT_VIEW, INVOICE_VIEW],                    typeof(MasterLedgerController), nameof(MasterLedgerController.GetSummary)),
        ("finance/master-ledger",                                      [PAYMENT_VIEW, INVOICE_VIEW],                    typeof(MasterLedgerController), nameof(MasterLedgerController.ExportPdf)),
        ("finance/master-ledger",                                      [PAYMENT_VIEW, INVOICE_VIEW],                    typeof(MasterLedgerController), nameof(MasterLedgerController.ExportExcel)),
        ("inventory/master-product-ledger",                            [INVENTORY_VIEW, STOCK_MANAGE],                  typeof(MasterProductLedgerController), nameof(MasterProductLedgerController.GetLedger)),
        ("inventory/master-product-ledger",                            [INVENTORY_VIEW, STOCK_MANAGE],                  typeof(MasterProductLedgerController), nameof(MasterProductLedgerController.GetSummary)),
        ("inventory/master-product-ledger",                            [INVENTORY_VIEW, STOCK_MANAGE],                  typeof(MasterProductLedgerController), nameof(MasterProductLedgerController.GetProductJourney)),
        ("inventory/master-product-ledger",                            [INVENTORY_VIEW, STOCK_MANAGE],                  typeof(MasterProductLedgerController), nameof(MasterProductLedgerController.ExportPdf)),
        ("inventory/master-product-ledger",                            [INVENTORY_VIEW, STOCK_MANAGE],                  typeof(MasterProductLedgerController), nameof(MasterProductLedgerController.ExportExcel)),
        ("warehouse/sro/:uuid Create Credit Note",                     [GOODS_RECEIVE, WAREHOUSE_TRANSFER],             typeof(CreditNotesController), nameof(CreditNotesController.CreateCreditNote)),
        ("warehouse/sro/:uuid Create Debit Note",                      [GOODS_RECEIVE, WAREHOUSE_TRANSFER],             typeof(DebitNotesController), nameof(DebitNotesController.CreateDebitNote)),
    }.Select(x => new object[] { x.Page, x.Reach, x.Controller, x.Action });

    [Theory]
    [MemberData(nameof(FrontendCallers))]
    public void Nobody_who_can_reach_a_page_or_button_is_refused_the_call_it_makes(string page, string[] reach, Type controller, string action)
    {
        var accepted = Action(controller, action).GetCustomAttribute<RequirePermissionAttribute>()?.AnyOf ?? [];

        accepted.Should().Contain(reach, $"{page} calls {controller.Name}.{action}");
    }

    [Fact]
    public void The_supplier_scoped_endpoints_still_check_the_supplier()
    {
        foreach (var (controller, action) in new[]
        {
            (typeof(SupplierLedgerController), nameof(SupplierLedgerController.GetLedger)),
            (typeof(SupplierLedgerController), nameof(SupplierLedgerController.GetBalance)),
            (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.GetOutstandingInvoices)),
            (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.GetSupplierAging)),
            (typeof(SupplierPaymentsController), nameof(SupplierPaymentsController.GetSupplierLedgerReport)),
        })
            Action(controller, action).GetCustomAttribute<RequiresSupplierAccessAttribute>()!.RouteParamName.Should().Be("supplierId", $"{controller.Name}.{action}");
    }
}

/// <summary>
/// The same gates through a real ASP.NET Core pipeline (as ReverseEndpointHttpTests): the seven controllers, a
/// header-driven sign-in, and a policy provider that reads "Permission:A|B" with <see cref="RequirePermissionAttribute.CodesOf"/>
/// and accepts ANY of the codes — as the real PermissionPolicyProvider does. Each seeded role (AuthDataSeeder) gets
/// its own permission set; the services are mocks.
/// </summary>
public class FinanceControllerPermissionHttpTests : IClassFixture<FinanceControllerPermissionHttpTests.Host>
{
    private const int Caller = 42;
    private readonly Host _host;

    public FinanceControllerPermissionHttpTests(Host host) => _host = host;

    // ── The seeded roles (src/SMS.Modules.Auth/Data/AuthDataSeeder.cs RolePermissionSeed) ──

    private static readonly Dictionary<string, string[]> Roles = new()
    {
        ["SystemAdmin"] = All.Except([SALE_ORDER_CONFIG_WRITE]).ToArray(),
        ["OrgAdmin"]    = All.Except([SYSTEM_CONFIGURE, PLATFORM_SUPER_ADMIN, SALE_ORDER_CONFIG_WRITE]).ToArray(),
        ["ProcurementManager"] =
        [
            SUPPLIER_VIEW, SUPPLIER_CREATE, SUPPLIER_EDIT, SUPPLIER_MANAGE, RFQ_VIEW, RFQ_CREATE, RFQ_MANAGE,
            CONTRACT_VIEW, CONTRACT_MANAGE, PO_VIEW, PO_CREATE, PO_EDIT, PO_APPROVE, PO_CANCEL, BUDGET_VIEW, BUDGET_MANAGE,
            REQUISITION_VIEW_ALL, REQUISITION_APPROVE, DELIVERY_TRACK, DELIVERY_VIEW, DELIVERY_CREATE, DELIVERY_EDIT,
            SHIPMENT_BOOK, CARRIER_MANAGE, SHIPMENT_RATE_VIEW, BOM_VIEW, PROD_VIEW, SUPPLY_VIEW, SUPPLY_CREATE, SUPPLY_CANCEL,
            REPORT_VIEW, REPORT_EXPORT, WORKFLOW_VIEW,
        ],
        ["PurchaseOfficer"] =
        [
            SUPPLIER_VIEW, REQUISITION_CREATE, REQUISITION_VIEW_OWN, RFQ_VIEW, PO_VIEW, PO_CREATE, PO_EDIT, INVENTORY_VIEW,
            DELIVERY_TRACK, DELIVERY_VIEW, REPORT_VIEW,
        ],
        ["InventoryManager"] =
        [
            INVENTORY_VIEW, STOCK_MANAGE, STOCK_ADJUST, REORDER_MANAGE, WAREHOUSE_TRANSFER, GOODS_RECEIVE, PUTAWAY, GRN_APPROVE,
            ALLOCATION_VIEW, ALLOCATION_RUN, ALLOCATION_ADMIN, BOM_VIEW, BOM_CREATE, BOM_EDIT, BOM_SUBMIT, BOM_APPROVE,
            BOM_ACTIVATE, BOM_OBSOLETE, BOM_ADMIN, PROD_VIEW, PROD_CREATE, PROD_PLAN, PROD_START, PROD_REPORT, PROD_CANCEL,
            PROD_MANAGER, MI_CREATE, MI_CONFIRM, MI_REVERSE, SUPPLY_VIEW, SUPPLY_CREATE, SUPPLY_CANCEL, QI_CREATE, QI_APPROVE,
            FGR_CREATE, FGR_CONFIRM, PROD_LEDGER_VIEW, REPORT_VIEW,
        ],
        ["WarehouseOperator"] =
        [
            GOODS_RECEIVE, PUTAWAY, PICKING, DISPATCH, STOCK_LOCATION_UPDATE, INVENTORY_VIEW, GRN_QC_CONFIRM, DELIVERY_VIEW,
            DELIVERY_EDIT, SHIPMENT_BOOK, ALLOCATION_VIEW, BOM_VIEW, PROD_VIEW, PROD_START, PROD_REPORT, MI_CREATE, MI_CONFIRM,
            QI_CREATE, FGR_CREATE, FGR_CONFIRM,
        ],
        ["FinanceOfficer"] =
        [
            INVOICE_VIEW, INVOICE_PROCESS, PAYMENT_VIEW, PAYMENT_PROCESS, SALES_INVOICE_VIEW, SALES_INVOICE_MANAGE,
            CUSTOMER_PAYMENT_VIEW, CUSTOMER_PAYMENT_RECORD, CUSTOMER_LEDGER_VIEW, PRODUCT_LEDGER_VIEW, RECONCILIATION,
            BUDGET_VIEW, BUDGET_MONITOR, REPORT_VIEW, REPORT_EXPORT, GRN_FINANCE_APPROVE, INTEGRATION_VIEW, INTEGRATION_SYNC,
        ],
        ["Requester"] = [REQUISITION_CREATE, REQUISITION_VIEW_OWN],
        ["Auditor"] =
        [
            SUPPLIER_VIEW, RFQ_VIEW, CONTRACT_VIEW, PO_VIEW, BUDGET_VIEW, REQUISITION_VIEW_ALL, INVENTORY_VIEW, INVOICE_VIEW,
            PAYMENT_VIEW, SALES_INVOICE_VIEW, CUSTOMER_PAYMENT_VIEW, CUSTOMER_LEDGER_VIEW, PRODUCT_LEDGER_VIEW, ALLOCATION_VIEW,
            BOM_VIEW, PROD_VIEW, SUPPLY_VIEW, PROD_LEDGER_VIEW, AUDIT_LOG_VIEW, REPORT_VIEW, REPORT_EXPORT, WORKFLOW_VIEW,
            INTEGRATION_VIEW,
        ],
        ["FinanceManager"] =
        [
            INVOICE_VIEW, INVOICE_PROCESS, PAYMENT_VIEW, PAYMENT_PROCESS, PAYMENT_APPROVE, SALES_INVOICE_VIEW,
            SALES_INVOICE_MANAGE, CUSTOMER_PAYMENT_VIEW, CUSTOMER_PAYMENT_RECORD, CUSTOMER_LEDGER_VIEW, PRODUCT_LEDGER_VIEW,
            RECONCILIATION, BUDGET_VIEW, BUDGET_MANAGE, REPORT_VIEW, REPORT_EXPORT, GRN_FINANCE_APPROVE, INTEGRATION_VIEW,
            INTEGRATION_MANAGE, INTEGRATION_SYNC, FINANCE_SETUP_MANAGE,
        ],
        ["SupplyDeptAdmin"] = [SALE_ORDER_CONFIG_READ, SALE_ORDER_CONFIG_WRITE],
    };

    // ── Representative calls, and which seeded roles each one admits (worked out by hand from the seeder) ──

    private static readonly Guid Id = Guid.NewGuid();

    private sealed record Probe(string Name, string Method, string Url, string? Body, string[] Admitted);

    private static readonly string[] SysOrg = ["SystemAdmin", "OrgAdmin"];

    private static readonly Probe[] Probes =
    [
        new("list supplier payments",   "GET",   "/api/supplier-payments", null,                 [.. SysOrg, "FinanceOfficer", "Auditor", "FinanceManager"]),
        new("read a supplier payment",  "GET",   $"/api/supplier-payments/{Id}", null,           [.. SysOrg, "FinanceOfficer", "Auditor", "FinanceManager"]),
        new("record a supplier payment","POST",  "/api/supplier-payments", "{}",                 [.. SysOrg, "FinanceOfficer", "FinanceManager"]),
        new("approve a payment",        "POST",  $"/api/supplier-payments/{Id}/approve", "{}",   [.. SysOrg, "FinanceManager"]),
        new("post a payment",           "POST",  $"/api/supplier-payments/{Id}/post", "{}",      [.. SysOrg, "FinanceOfficer", "FinanceManager"]),
        new("bounce a payment",         "POST",  $"/api/supplier-payments/{Id}/bounce", "{}",    [.. SysOrg, "FinanceOfficer", "FinanceManager"]),
        new("cancel a payment",         "POST",  $"/api/supplier-payments/{Id}/cancel", "{}",    [.. SysOrg, "FinanceOfficer", "FinanceManager"]),
        new("a supplier's ledger",      "GET",   $"/api/suppliers/{Id}/ledger", null,            [.. SysOrg, "ProcurementManager", "PurchaseOfficer", "FinanceOfficer", "Auditor", "FinanceManager"]),
        new("a supplier's balance",     "GET",   $"/api/suppliers/{Id}/balance", null,           [.. SysOrg, "ProcurementManager", "PurchaseOfficer", "FinanceOfficer", "Auditor", "FinanceManager"]),
        new("outstanding invoices",     "GET",   $"/api/suppliers/{Id}/outstanding-invoices", null, [.. SysOrg, "ProcurementManager", "PurchaseOfficer", "FinanceOfficer", "Auditor", "FinanceManager"]),
        new("cross-supplier aging",     "GET",   "/api/reports/supplier-aging", null,            [.. SysOrg, "ProcurementManager", "PurchaseOfficer", "InventoryManager", "FinanceOfficer", "Auditor", "FinanceManager"]),
        new("legacy payments list",     "GET",   "/api/finance/payments", null,                  [.. SysOrg, "FinanceOfficer", "Auditor", "FinanceManager"]),
        new("legacy payment patch",     "PATCH", $"/api/finance/payments/{Id}", """{"notes":"x"}""", [.. SysOrg, "FinanceOfficer", "FinanceManager"]),
        new("master payables ledger",   "GET",   "/api/finance/master-ledger", null,             [.. SysOrg, "FinanceOfficer", "Auditor", "FinanceManager"]),
        new("raise a write-off",        "POST",  "/api/finance/master-ledger/write-off", "{}",   [.. SysOrg, "FinanceOfficer", "FinanceManager"]),
        new("import opening balances",  "POST",  "/api/finance/master-ledger/opening-balance", "{}", ["SystemAdmin"]),
        new("master product ledger",    "GET",   "/api/inventory/master-product-ledger", null,   [.. SysOrg, "PurchaseOfficer", "InventoryManager", "WarehouseOperator", "Auditor"]),
        new("raise a credit note",      "POST",  "/api/credit-notes", """{"supplierCreditNoteNo":"CN-1","creditAmount":10}""",
                                                                                                  [.. SysOrg, "InventoryManager", "WarehouseOperator", "FinanceOfficer", "FinanceManager"]),
        new("raise a debit note",       "POST",  "/api/debit-notes", """{"debitReason":"SHORT_SUPPLY","debitAmount":10}""",
                                                                                                  [.. SysOrg, "InventoryManager", "WarehouseOperator", "FinanceOfficer", "FinanceManager"]),
        new("apply a credit note",      "POST",  $"/api/credit-notes/{Id}/apply", $$"""{"invoiceUuid":"{{Id}}"}""", [.. SysOrg, "FinanceOfficer", "FinanceManager"]),
        new("list debit notes",         "GET",   "/api/debit-notes", null,                       [.. SysOrg, "FinanceOfficer", "Auditor", "FinanceManager"]),
    ];

    public static IEnumerable<object[]> RoleByProbe() =>
        from probe in Probes from role in Roles.Keys select new object[] { probe.Name, role };

    private Task<HttpResponseMessage> SendAsync(string method, string url, string? json, IEnumerable<string>? permissions, bool signedIn = true)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (signedIn) request.Headers.Add("X-User", Caller.ToString());
        var held = permissions?.ToArray() ?? [];
        if (held.Length > 0) request.Headers.Add("X-Permissions", string.Join(',', held));
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return _host.Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> AsRole(string role, string method, string url, string? json = null) =>
        SendAsync(method, url, json, Roles[role]);

    [Theory]
    [MemberData(nameof(RoleByProbe))]
    public async Task Each_seeded_role_is_let_in_or_refused_as_its_permissions_say(string probeName, string role)
    {
        var probe = Probes.Single(p => p.Name == probeName);

        var response = await AsRole(role, probe.Method, probe.Url, probe.Body);

        if (probe.Admitted.Contains(role))
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{role} may {probe.Name} ({probe.Method} {probe.Url})");
        else
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} may not {probe.Name} ({probe.Method} {probe.Url})");
    }

    [Fact]
    public async Task An_auditor_reads_payments_and_ledgers_but_cannot_record_post_approve_or_raise_anything()
    {
        var payment = Guid.NewGuid();

        (await AsRole("Auditor", "GET", "/api/supplier-payments")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AsRole("Auditor", "GET", $"/api/supplier-payments/{payment}")).StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        (await AsRole("Auditor", "GET", "/api/finance/master-ledger")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AsRole("Auditor", "GET", $"/api/suppliers/{payment}/ledger")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await AsRole("Auditor", "POST", $"/api/supplier-payments/{payment}/post", "{}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AsRole("Auditor", "POST", $"/api/supplier-payments/{payment}/approve", "{}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AsRole("Auditor", "POST", $"/api/supplier-payments/{payment}/bounce", "{}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AsRole("Auditor", "POST", $"/api/supplier-payments/{payment}/cancel", "{}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AsRole("Auditor", "PATCH", $"/api/finance/payments/{payment}", """{"status":"Reversed"}""")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AsRole("Auditor", "POST", $"/api/credit-notes/{payment}/apply", "{}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AsRole("Auditor", "PATCH", $"/api/debit-notes/{payment}/status", """{"newStatus":"CLOSED"}""")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _host.SupplierPayments.Verify(s => s.PostAsync(payment, It.IsAny<int>()), Times.Never);
        _host.SupplierPayments.Verify(s => s.ApproveAsync(payment, It.IsAny<int>()), Times.Never);
        _host.SupplierPayments.Verify(s => s.BounceAsync(payment, It.IsAny<int>()), Times.Never);
        _host.SupplierPayments.Verify(s => s.CancelAsync(payment, It.IsAny<int>()), Times.Never);
        _host.LegacyPayments.Verify(s => s.PatchAsync(payment, It.IsAny<PatchPaymentRequest>(), It.IsAny<int>()), Times.Never);
        _host.CreditNotes.Verify(s => s.ApplyCarriedForwardAsync(payment, It.IsAny<ApplyCreditNoteRequest>(), It.IsAny<int>()), Times.Never);
        _host.DebitNotes.Verify(s => s.UpdateStatusAsync(payment, It.IsAny<UpdateDebitNoteStatusRequest>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task A_finance_officer_posts_and_bounces_a_payment_but_cannot_approve_one()
    {
        var payment = Guid.NewGuid();

        (await AsRole("FinanceOfficer", "POST", $"/api/supplier-payments/{payment}/post", "{}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AsRole("FinanceOfficer", "POST", $"/api/supplier-payments/{payment}/bounce", "{}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AsRole("FinanceOfficer", "POST", $"/api/supplier-payments/{payment}/approve", "{}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _host.SupplierPayments.Verify(s => s.PostAsync(payment, Caller), Times.Once);
        _host.SupplierPayments.Verify(s => s.BounceAsync(payment, Caller), Times.Once);
        _host.SupplierPayments.Verify(s => s.ApproveAsync(payment, It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task A_finance_manager_approves_a_payment()
    {
        var payment = Guid.NewGuid();

        (await AsRole("FinanceManager", "POST", $"/api/supplier-payments/{payment}/approve", "{}")).StatusCode.Should().Be(HttpStatusCode.OK);

        _host.SupplierPayments.Verify(s => s.ApproveAsync(payment, Caller), Times.Once);
    }

    [Fact]
    public async Task A_warehouse_operator_resolves_a_return_with_a_note_but_reads_no_payables()
    {
        var sro = Guid.NewGuid();

        (await AsRole("WarehouseOperator", "POST", "/api/debit-notes", $$"""{"sroId":"{{sro}}","debitReason":"SHORT_SUPPLY","debitAmount":10}"""))
            .StatusCode.Should().Be(HttpStatusCode.OK, "the SRO detail page offers Create Debit Note to GOODS_RECEIVE holders");
        _host.DebitNotes.Verify(s => s.CreateAsync(It.Is<CreateDebitNoteRequest>(r => r.SroId == sro), Caller), Times.Once);

        (await AsRole("WarehouseOperator", "GET", "/api/debit-notes")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AsRole("WarehouseOperator", "POST", $"/api/debit-notes/{sro}/apply", "{}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AsRole("WarehouseOperator", "GET", $"/api/suppliers/{sro}/ledger")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AsRole("WarehouseOperator", "GET", "/api/supplier-payments")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AsRole("WarehouseOperator", "GET", "/api/reports/outstanding-payables")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _host.DebitNotes.Verify(s => s.ApplyCarriedForwardAsync(sro, It.IsAny<ApplyDebitNoteRequest>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task A_purchase_officer_sees_a_suppliers_ledger_from_the_supplier_page_but_no_payments()
    {
        var supplier = Guid.NewGuid();

        (await AsRole("PurchaseOfficer", "GET", $"/api/suppliers/{supplier}/ledger")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AsRole("PurchaseOfficer", "GET", $"/api/suppliers/{supplier}/balance")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AsRole("PurchaseOfficer", "GET", $"/api/suppliers/{supplier}/outstanding-invoices")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await AsRole("PurchaseOfficer", "GET", "/api/supplier-payments")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AsRole("PurchaseOfficer", "GET", "/api/finance/payments")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AsRole("PurchaseOfficer", "POST", "/api/supplier-payments", "{}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AsRole("PurchaseOfficer", "GET", "/api/finance/master-ledger")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_signed_in_user_with_no_finance_permission_is_refused_and_an_anonymous_one_is_challenged()
    {
        (await SendAsync("GET", "/api/supplier-payments", null, [REQUISITION_CREATE])).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync("GET", "/api/credit-notes", null, null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync("GET", "/api/supplier-payments", null, All, signedIn: false)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Test host plumbing (as ReverseEndpointHttpTests) ─────────────────────

    public sealed class Host : IAsyncLifetime
    {
        public Mock<ISupplierLedgerService>          Ledger           { get; } = new();
        public Mock<ISupplierPaymentService>         SupplierPayments { get; } = new();
        public Mock<IPaymentService>                 LegacyPayments   { get; } = new();
        public Mock<IMasterLedgerQueryService>       MasterLedger     { get; } = new();
        public Mock<IOpeningBalanceService>          OpeningBalance   { get; } = new();
        public Mock<IDebtWriteOffService>            WriteOffs        { get; } = new();
        public Mock<IMasterProductLedgerQueryService> ProductLedger   { get; } = new();
        public Mock<ICreditNoteService>              CreditNotes      { get; } = new();
        public Mock<IDebitNoteService>               DebitNotes       { get; } = new();

        private IHost _host = null!;
        public HttpClient Client { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            // Every allowed probe must come back 200, so a 404 can never pass for "let in": the state changes succeed.
            SupplierPayments.Setup(s => s.ApproveAsync(It.IsAny<Guid>(), It.IsAny<int>())).ReturnsAsync(true);
            SupplierPayments.Setup(s => s.PostAsync(It.IsAny<Guid>(), It.IsAny<int>())).ReturnsAsync(true);
            SupplierPayments.Setup(s => s.BounceAsync(It.IsAny<Guid>(), It.IsAny<int>())).ReturnsAsync(true);
            SupplierPayments.Setup(s => s.CancelAsync(It.IsAny<Guid>(), It.IsAny<int>())).ReturnsAsync(true);
            SupplierPayments.Setup(s => s.GetByUuidAsync(It.IsAny<Guid>())).ReturnsAsync(new SupplierPaymentDetailModel());
            LegacyPayments.Setup(s => s.PatchAsync(It.IsAny<Guid>(), It.IsAny<PatchPaymentRequest>(), It.IsAny<int>())).ReturnsAsync(true);

            _host = await new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddControllers(options =>
                    {
                        options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
                        options.Filters.Add(new AuthorizeFilter(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()));
                    }).ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyTheseControllers()));

                    services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("Test", _ => { });
                    services.AddAuthorization();
                    services.AddSingleton<IAuthorizationPolicyProvider, AnyOfPermissionPolicyProvider>();

                    services.AddSingleton(Ledger.Object);
                    services.AddSingleton(SupplierPayments.Object);
                    services.AddSingleton(LegacyPayments.Object);
                    services.AddSingleton(MasterLedger.Object);
                    services.AddSingleton(OpeningBalance.Object);
                    services.AddSingleton(WriteOffs.Object);
                    services.AddSingleton(Mock.Of<IPoDocumentTemplateService>());
                    services.AddSingleton(ProductLedger.Object);
                    services.AddSingleton(CreditNotes.Object);
                    services.AddSingleton(DebitNotes.Object);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e => e.MapControllers());
                });
            }).StartAsync();

            Client = _host.GetTestClient();
        }

        public async Task DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private sealed class OnlyTheseControllers : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            foreach (var controller in new[]
            {
                typeof(SupplierLedgerController), typeof(SupplierPaymentsController), typeof(PaymentsController),
                typeof(MasterLedgerController), typeof(MasterProductLedgerController),
                typeof(CreditNotesController), typeof(DebitNotesController),
            })
                feature.Controllers.Add(controller.GetTypeInfo());
        }
    }

    private sealed class HeaderAuthentication : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public HeaderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-User", out var user))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new("sub", user.ToString()) };
            if (Request.Headers.TryGetValue("X-Permissions", out var permissions))
                claims.AddRange(permissions.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => new Claim("permission", p)));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }

    /// <summary>Reads a "Permission:A|B" name the way the real PermissionPolicyProvider does: holding ANY code is enough.</summary>
    private sealed class AnyOfPermissionPolicyProvider : DefaultAuthorizationPolicyProvider
    {
        public AnyOfPermissionPolicyProvider(IOptions<AuthorizationOptions> options) : base(options) { }

        public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
            RequirePermissionAttribute.CodesOf(policyName) is { Count: > 0 } codes
                ? Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .RequireAssertion(context => codes.Any(context.User.HasPermission))
                    .Build())
                : base.GetPolicyAsync(policyName);
    }
}

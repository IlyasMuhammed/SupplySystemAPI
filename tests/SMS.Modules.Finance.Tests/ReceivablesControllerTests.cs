using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using SMS.Modules.Finance.Controllers;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P7-08 — the receivables endpoints: that each one is gated by the right permission, lives at the
/// route the spec names, and hands the service exactly what the request said. Same reflection approach
/// as Logistics' <c>ControllerAuthorizationTests</c>, scoped to the three new controllers: the
/// supplier-side Finance controllers predate the permission convention and are not this task's to change.
/// </summary>
public class ReceivablesControllerTests
{
    private const int Caller = 42;

    private static readonly Type[] Controllers =
        [typeof(SalesInvoicesController), typeof(CustomerPaymentsController), typeof(CustomerLedgerController)];

    private static IEnumerable<MethodInfo> ActionsOf(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                  .Where(m => !m.IsSpecialName && m.GetCustomAttributes<HttpMethodAttribute>().Any());

    private static string? PolicyOf(Type controller, string action) =>
        controller.GetMethod(action)!.GetCustomAttribute<RequirePermissionAttribute>()?.Policy;

    // ── Every action is gated ────────────────────────────────────────────────

    [Fact]
    public void The_controllers_and_their_actions_are_actually_found()
    {
        // Guards the guard: a reflection query that silently matches nothing passes every assertion below.
        Controllers.SelectMany(ActionsOf).Should().HaveCount(13);
    }

    [Fact]
    public void Every_controller_is_behind_the_finance_feature_flag()
    {
        foreach (var controller in Controllers)
            controller.GetCustomAttribute<RequiresFeatureAttribute>()!.FeatureCode.Should().Be("MODULE_FINANCE", controller.Name);
    }

    [Fact]
    public void Every_controller_is_an_api_controller()
    {
        foreach (var controller in Controllers)
            controller.GetCustomAttribute<ApiControllerAttribute>().Should().NotBeNull(controller.Name);
    }

    [Fact]
    public void Every_action_requires_a_permission_and_none_is_anonymous()
    {
        var unguarded = Controllers.SelectMany(c => ActionsOf(c).Select(a => (Controller: c, Action: a)))
            .Where(x => x.Action.GetCustomAttribute<RequirePermissionAttribute>() is null
                     || x.Action.GetCustomAttribute<AllowAnonymousAttribute>() is not null
                     || x.Controller.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
            .ToList();

        unguarded.Should().BeEmpty("financial data must not be reachable by any authenticated user of the organization");
    }

    [Theory]
    // Sales invoices: reading is one permission, everything that changes one is another.
    [InlineData(typeof(SalesInvoicesController), nameof(SalesInvoicesController.GetList),     "SALES_INVOICE_VIEW")]
    [InlineData(typeof(SalesInvoicesController), nameof(SalesInvoicesController.GetById),     "SALES_INVOICE_VIEW")]
    [InlineData(typeof(SalesInvoicesController), nameof(SalesInvoicesController.DownloadPdf), "SALES_INVOICE_VIEW")]
    [InlineData(typeof(SalesInvoicesController), nameof(SalesInvoicesController.Create),      "SALES_INVOICE_MANAGE")]
    [InlineData(typeof(SalesInvoicesController), nameof(SalesInvoicesController.Update),      "SALES_INVOICE_MANAGE")]
    [InlineData(typeof(SalesInvoicesController), nameof(SalesInvoicesController.Delete),      "SALES_INVOICE_MANAGE")]
    [InlineData(typeof(SalesInvoicesController), nameof(SalesInvoicesController.Issue),       "SALES_INVOICE_MANAGE")]
    [InlineData(typeof(SalesInvoicesController), nameof(SalesInvoicesController.AttachPdf),   "SALES_INVOICE_MANAGE")]
    // Customer payments: recording and applying money is what settles an invoice.
    [InlineData(typeof(CustomerPaymentsController), nameof(CustomerPaymentsController.GetList),  "CUSTOMER_PAYMENT_VIEW")]
    [InlineData(typeof(CustomerPaymentsController), nameof(CustomerPaymentsController.GetById),  "CUSTOMER_PAYMENT_VIEW")]
    [InlineData(typeof(CustomerPaymentsController), nameof(CustomerPaymentsController.Record),   "CUSTOMER_PAYMENT_RECORD")]
    [InlineData(typeof(CustomerPaymentsController), nameof(CustomerPaymentsController.Allocate), "CUSTOMER_PAYMENT_RECORD")]
    // The ledger is its own permission: a statement of account is more than either document.
    [InlineData(typeof(CustomerLedgerController), nameof(CustomerLedgerController.GetLedger), "CUSTOMER_LEDGER_VIEW")]
    public void Each_action_needs_the_permission_that_matches_what_it_does(Type controller, string action, string code) =>
        PolicyOf(controller, action).Should().Be($"Permission:{code}");

    [Fact]
    public void The_receivables_endpoints_do_not_borrow_the_supplier_side_permissions()
    {
        var used = Controllers.SelectMany(ActionsOf)
            .Select(a => a.GetCustomAttribute<RequirePermissionAttribute>()!.Policy).Distinct().ToList();

        used.Should().NotContain(["Permission:INVOICE_VIEW", "Permission:INVOICE_PROCESS", "Permission:PAYMENT_VIEW", "Permission:PAYMENT_PROCESS", "Permission:PAYMENT_APPROVE"]);
    }

    // ── Routes ───────────────────────────────────────────────────────────────

    private static IEnumerable<(string Verb, string Route, string Action)> RoutesOf(Type controller)
    {
        var prefix = controller.GetCustomAttribute<RouteAttribute>()!.Template!;

        foreach (var action in ActionsOf(controller))
            foreach (var http in action.GetCustomAttributes<HttpMethodAttribute>())
            {
                var template = http.Template;
                var route = template is null ? prefix
                          : template.StartsWith('/') ? template.TrimStart('/')
                          : $"{prefix}/{template}";
                yield return (http.HttpMethods.Single(), route, $"{controller.Name}.{action.Name}");
            }
    }

    [Fact]
    public void The_routes_are_the_ones_the_spec_and_the_task_name()
    {
        Controllers.SelectMany(RoutesOf).Select(r => $"{r.Verb} {r.Route}").Should().BeEquivalentTo(
        [
            "POST api/sales-invoices",
            "GET api/sales-invoices",
            "GET api/sales-invoices/{uuid:guid}",
            "PUT api/sales-invoices/{uuid:guid}",
            "DELETE api/sales-invoices/{uuid:guid}",
            "POST api/sales-invoices/{uuid:guid}/issue",
            "POST api/sales-invoices/{uuid:guid}/attach-pdf",
            "GET api/sales-invoices/{uuid:guid}/pdf",
            "POST api/customer-payments",
            "GET api/customer-payments",
            "GET api/customer-payments/{uuid:guid}",
            "POST api/customer-payments/{uuid:guid}/allocate",
            "GET api/partners/{partnerId:guid}/ledger",
        ]);
    }

    [Fact]
    public void No_two_finance_endpoints_answer_the_same_verb_and_route()
    {
        // ASP.NET Core resolves two actions on one route by failing the request with an ambiguity error,
        // and only when that route is hit — so nothing else would notice.
        var everything = typeof(SalesInvoicesController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract && t.GetCustomAttribute<RouteAttribute>() is not null)
            .SelectMany(RoutesOf)
            .Select(r => (Key: $"{r.Verb} {NormalizeParameterNames(r.Route)}", r.Action))
            .ToList();

        everything.GroupBy(x => x.Key).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Action))}")
            .Should().BeEmpty();
    }

    private static string NormalizeParameterNames(string route) =>
        System.Text.RegularExpressions.Regex.Replace(route, @"\{[^}:]+(:[^}]+)?\}", m => m.Groups[1].Success ? "{x" + m.Groups[1].Value + "}" : "{x}");

    // ── What each action hands the service ───────────────────────────────────

    private static T AsUser<T>(T controller, int userId = Caller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId.ToString())], "test"))
            }
        };
        return controller;
    }

    private static T Body<T>(IActionResult result) where T : class =>
        ((ApiResponse<T>)((OkObjectResult)result).Value!).Result!;

    private static string Message(IActionResult result) =>
        result switch
        {
            OkObjectResult { Value: ApiResponse r } => r.Message,
            OkObjectResult ok => (string)ok.Value!.GetType().GetProperty("Message")!.GetValue(ok.Value)!,
            _ => throw new InvalidOperationException($"unexpected {result.GetType().Name}")
        };

    private sealed class Invoices
    {
        public Mock<ISalesInvoiceService> Service { get; } = new();
        public Mock<ISalesInvoiceDocumentService> Documents { get; } = new();
        public Mock<ISalesInvoiceDocumentArchive> Archive { get; } = new();
        public SalesInvoicesController Controller { get; }
        public Invoices() => Controller = AsUser(new SalesInvoicesController(Service.Object, Documents.Object, Archive.Object));
    }

    [Fact]
    public async Task Creating_an_invoice_passes_the_delivery_and_the_caller_and_says_so()
    {
        var t = new Invoices();
        var delivery = Guid.NewGuid();
        var created = new SalesInvoiceCreated(Guid.NewGuid(), "SINV-20260920-0001", 4000m, "PKR", AlreadyExisted: false);
        t.Service.Setup(s => s.CreateFromFulfillmentAsync(delivery, Caller)).ReturnsAsync(created);

        var result = await t.Controller.Create(new CreateSalesInvoiceRequest { DeliveryUuid = delivery });

        Body<SalesInvoiceCreated>(result).Should().Be(created);
        ((ApiResponse<SalesInvoiceCreated>)((OkObjectResult)result).Value!).Message.Should().Be("Record created successfully.");
    }

    [Fact]
    public async Task Asking_for_a_delivery_that_already_has_an_invoice_returns_that_invoice_and_names_it()
    {
        var t = new Invoices();
        var existing = new SalesInvoiceCreated(Guid.NewGuid(), "SINV-20260920-0007", 4000m, "PKR", AlreadyExisted: true);
        t.Service.Setup(s => s.CreateFromFulfillmentAsync(It.IsAny<Guid>(), It.IsAny<int>())).ReturnsAsync(existing);

        var result = await t.Controller.Create(new CreateSalesInvoiceRequest { DeliveryUuid = Guid.NewGuid() });

        Body<SalesInvoiceCreated>(result).AlreadyExisted.Should().BeTrue();
        ((ApiResponse<SalesInvoiceCreated>)((OkObjectResult)result).Value!).Message.Should().Contain("SINV-20260920-0007").And.Contain("already exists");
    }

    [Fact]
    public async Task Getting_an_invoice_that_is_not_there_is_a_404_and_one_that_is_comes_back_whole()
    {
        var t = new Invoices();
        var known = Guid.NewGuid();
        var detail = new SalesInvoiceDetailModel { Uuid = known, InvoiceNumber = "SINV-1" };
        t.Service.Setup(s => s.GetAsync(known)).ReturnsAsync(detail);
        t.Service.Setup(s => s.GetAsync(It.Is<Guid>(g => g != known))).ReturnsAsync((SalesInvoiceDetailModel?)null);

        (await t.Controller.GetById(Guid.NewGuid())).Should().BeOfType<NotFoundObjectResult>();
        Body<SalesInvoiceDetailModel>(await t.Controller.GetById(known)).Should().BeSameAs(detail);
    }

    [Fact]
    public async Task The_list_passes_the_filter_through_untouched()
    {
        var t = new Invoices();
        var filter = new SalesInvoiceFilter { Status = "OVERDUE", Page = 3, PageSize = 50, Search = "acme" };
        var page = new PaginatedResponse<SalesInvoiceListItemModel> { TotalRecords = 7 };
        t.Service.Setup(s => s.ListAsync(filter)).ReturnsAsync(page);

        Body<PaginatedResponse<SalesInvoiceListItemModel>>(await t.Controller.GetList(filter)).Should().BeSameAs(page);
    }

    [Fact]
    public async Task Update_delete_and_issue_act_as_the_caller()
    {
        var t = new Invoices();
        var id = Guid.NewGuid();
        var update = new UpdateSalesInvoiceRequest { DueDate = new DateTime(2026, 10, 30), Notes = "Net 45" };
        var issued = new SalesInvoiceIssued(id, "SINV-1", "ISSUED", 4000m, 4000m);
        t.Service.Setup(s => s.IssueAsync(id, Caller)).ReturnsAsync(issued);

        Message(await t.Controller.Update(id, update)).Should().Be("Record updated successfully.");
        Message(await t.Controller.Delete(id)).Should().Be("Record deleted successfully.");
        Body<SalesInvoiceIssued>(await t.Controller.Issue(id)).Should().Be(issued);

        t.Service.Verify(s => s.UpdateAsync(id, update, Caller), Times.Once);
        t.Service.Verify(s => s.DeleteAsync(id, Caller), Times.Once);
    }

    [Fact]
    public async Task Issuing_files_the_pdf_after_the_invoice_is_issued_and_says_so()
    {
        var t = new Invoices();
        var id = Guid.NewGuid();
        var issued = new SalesInvoiceIssued(id, "SINV-1", "ISSUED", 4000m, 4000m);
        var calls = new List<string>();
        t.Service.Setup(s => s.IssueAsync(id, Caller)).Callback(() => calls.Add("issue")).ReturnsAsync(issued);
        t.Archive.Setup(a => a.TryFileIssuedPdfAsync(id, Caller)).Callback(() => calls.Add("file")).ReturnsAsync((Guid?)Guid.NewGuid());

        var result = await t.Controller.Issue(id);

        calls.Should().Equal("issue", "file");
        Body<SalesInvoiceIssued>(result).Should().Be(issued);
        ((ApiResponse<SalesInvoiceIssued>)((OkObjectResult)result).Value!).Message.Should().Be("Sales invoice issued and its PDF filed.");
    }

    [Fact]
    public async Task If_the_pdf_could_not_be_filed_the_invoice_is_still_issued_and_the_response_says_what_to_do()
    {
        var t = new Invoices();
        var id = Guid.NewGuid();
        var issued = new SalesInvoiceIssued(id, "SINV-1", "ISSUED", 4000m, 4000m);
        t.Service.Setup(s => s.IssueAsync(id, Caller)).ReturnsAsync(issued);
        t.Archive.Setup(a => a.TryFileIssuedPdfAsync(id, Caller)).ReturnsAsync((Guid?)null);

        var result = await t.Controller.Issue(id);

        Body<SalesInvoiceIssued>(result).Should().Be(issued, "the issue itself succeeded");
        ((ApiResponse<SalesInvoiceIssued>)((OkObjectResult)result).Value!).Message.Should().Contain("could not be filed").And.Contain("file it from the invoice");
    }

    [Fact]
    public async Task An_invoice_that_fails_to_issue_is_never_filed()
    {
        var t = new Invoices();
        t.Service.Setup(s => s.IssueAsync(It.IsAny<Guid>(), It.IsAny<int>())).ThrowsAsync(new InvalidOperationException("already issued"));

        var act = async () => await t.Controller.Issue(Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>();
        t.Archive.Verify(a => a.TryFileIssuedPdfAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Attaching_the_pdf_on_demand_returns_the_attachment_and_names_a_repeat()
    {
        var t = new Invoices();
        var id = Guid.NewGuid();
        var fresh = new StoredAttachment(Guid.NewGuid(), AlreadyStored: false);
        var repeat = new StoredAttachment(Guid.NewGuid(), AlreadyStored: true);
        t.Archive.SetupSequence(a => a.FilePdfAsync(id, Caller)).ReturnsAsync(fresh).ReturnsAsync(repeat);

        var first = await t.Controller.AttachPdf(id);
        var second = await t.Controller.AttachPdf(id);

        Body<StoredAttachment>(first).Should().Be(fresh);
        ((ApiResponse<StoredAttachment>)((OkObjectResult)first).Value!).Message.Should().Be("Invoice PDF filed as an attachment.");
        Body<StoredAttachment>(second).Should().Be(repeat);
        ((ApiResponse<StoredAttachment>)((OkObjectResult)second).Value!).Message.Should().Contain("already on file");
    }

    [Fact]
    public async Task The_pdf_comes_back_as_a_file_named_after_the_invoice()
    {
        var t = new Invoices();
        var id = Guid.NewGuid();
        var bytes = "%PDF-1.7"u8.ToArray();
        t.Documents.Setup(d => d.GeneratePdfAsync(id)).ReturnsAsync(new SalesInvoicePdf("SINV-20260920-0001.pdf", bytes));

        var file = (await t.Controller.DownloadPdf(id)).Should().BeOfType<FileContentResult>().Subject;

        file.ContentType.Should().Be("application/pdf");
        file.FileDownloadName.Should().Be("SINV-20260920-0001.pdf");
        file.FileContents.Should().Equal(bytes);
    }

    private sealed class Payments
    {
        public Mock<ICustomerPaymentService> Service { get; } = new();
        public CustomerPaymentsController Controller { get; }
        public Payments() => Controller = AsUser(new CustomerPaymentsController(Service.Object));
    }

    [Fact]
    public async Task Recording_a_payment_hands_over_every_field_and_the_caller()
    {
        var t = new Payments();
        var partner = Guid.NewGuid();
        var invoice = Guid.NewGuid();
        var recorded = new CustomerPaymentRecorded(Guid.NewGuid(), "CPAY-20260920-0001", 1000m, 1000m, 0m, "PKR", [], 3000m);
        CustomerPaymentDetails? seen = null;
        t.Service.Setup(s => s.RecordPaymentAsync(partner, 1000m, "CHEQUE", It.IsAny<CustomerPaymentDetails>(), Caller))
                 .Callback<Guid, decimal, string, CustomerPaymentDetails, int>((_, _, _, d, _) => seen = d)
                 .ReturnsAsync(recorded);

        var result = await t.Controller.Record(new RecordCustomerPaymentRequest
        {
            PartnerId = partner, Amount = 1000m, Method = "CHEQUE", CurrencyCode = "PKR",
            PaymentDate = new DateTime(2026, 9, 18), ChequeNumber = "004521", BankReference = "BR-1", Notes = "counter",
            Allocations = [new ManualPaymentAllocation(invoice, 1000m)]
        });

        Body<CustomerPaymentRecorded>(result).Should().Be(recorded);
        seen!.CurrencyCode.Should().Be("PKR");
        seen.PaymentDate.Should().Be(new DateTime(2026, 9, 18));
        (seen.ChequeNumber, seen.BankReference, seen.Notes).Should().Be(("004521", "BR-1", "counter"));
        seen.Allocations.Should().ContainSingle().Which.Should().Be(new ManualPaymentAllocation(invoice, 1000m));
    }

    [Fact]
    public async Task A_payment_with_no_allocations_key_is_applied_fifo_not_left_on_account()
    {
        var t = new Payments();
        CustomerPaymentDetails? seen = null;
        t.Service.Setup(s => s.RecordPaymentAsync(It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CustomerPaymentDetails>(), It.IsAny<int>()))
                 .Callback<Guid, decimal, string, CustomerPaymentDetails, int>((_, _, _, d, _) => seen = d)
                 .ReturnsAsync(new CustomerPaymentRecorded(Guid.NewGuid(), "CPAY-1", 1m, 0m, 1m, "PKR", [], 0m));

        await t.Controller.Record(new RecordCustomerPaymentRequest { PartnerId = Guid.NewGuid(), Amount = 1m, Method = "CASH", CurrencyCode = "PKR" });
        seen!.Allocations.Should().BeNull("null is FIFO");

        await t.Controller.Record(new RecordCustomerPaymentRequest { PartnerId = Guid.NewGuid(), Amount = 1m, Method = "CASH", CurrencyCode = "PKR", Allocations = [] });
        seen!.Allocations.Should().NotBeNull().And.BeEmpty("an empty list is the caller deliberately putting it on account");
    }

    [Fact]
    public async Task Allocating_with_no_body_is_fifo_and_with_a_body_is_exactly_what_it_says()
    {
        var t = new Payments();
        var id = Guid.NewGuid();
        var invoice = Guid.NewGuid();
        var applied = new CustomerPaymentAllocated(id, "CPAY-1", 1000m, 1000m, 0m, []);
        t.Service.Setup(s => s.AllocateAsync(id, It.IsAny<IReadOnlyList<ManualPaymentAllocation>?>(), Caller)).ReturnsAsync(applied);

        Body<CustomerPaymentAllocated>(await t.Controller.Allocate(id, null)).Should().Be(applied);
        t.Service.Verify(s => s.AllocateAsync(id, null, Caller), Times.Once);

        await t.Controller.Allocate(id, new AllocateCustomerPaymentRequest());
        t.Service.Verify(s => s.AllocateAsync(id, null, Caller), Times.Exactly(2));

        await t.Controller.Allocate(id, new AllocateCustomerPaymentRequest { Allocations = [new ManualPaymentAllocation(invoice, 400m)] });
        t.Service.Verify(s => s.AllocateAsync(id,
            It.Is<IReadOnlyList<ManualPaymentAllocation>?>(a => a != null && a.Count == 1 && a[0] == new ManualPaymentAllocation(invoice, 400m)), Caller), Times.Once);
    }

    [Fact]
    public async Task Getting_a_payment_that_is_not_there_is_a_404()
    {
        var t = new Payments();
        t.Service.Setup(s => s.GetAsync(It.IsAny<Guid>())).ReturnsAsync((CustomerPaymentDetailModel?)null);

        (await t.Controller.GetById(Guid.NewGuid())).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task The_payment_list_passes_the_filter_through_untouched()
    {
        var t = new Payments();
        var filter = new CustomerPaymentFilter { Unallocated = true, Page = 2 };
        var page = new PaginatedResponse<CustomerPaymentListItemModel> { TotalRecords = 3 };
        t.Service.Setup(s => s.ListAsync(filter)).ReturnsAsync(page);

        Body<PaginatedResponse<CustomerPaymentListItemModel>>(await t.Controller.GetList(filter)).Should().BeSameAs(page);
    }

    [Fact]
    public async Task The_ledger_endpoint_asks_for_the_customer_in_the_route_with_the_filter_from_the_query()
    {
        var query = new Mock<ICustomerLedgerQueryService>();
        var partner = Guid.NewGuid();
        var filter = new CustomerLedgerFilter { DateFrom = new DateTime(2026, 9, 1), Page = 2, PageSize = 10 };
        var page = new PaginatedResponse<CustomerLedgerEntryModel> { TotalRecords = 12 };
        query.Setup(s => s.GetLedgerAsync(partner, filter)).ReturnsAsync(page);
        var controller = AsUser(new CustomerLedgerController(query.Object));

        Body<PaginatedResponse<CustomerLedgerEntryModel>>(await controller.GetLedger(partner, filter)).Should().BeSameAs(page);
    }

    // ── The wire shape the frontend will send ────────────────────────────────

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void A_payment_request_binds_from_the_json_the_frontend_would_send()
    {
        var invoice = Guid.NewGuid();
        var partner = Guid.NewGuid();
        var json = $$"""
        {
          "partnerId": "{{partner}}", "amount": 2500.50, "method": "BANK_TRANSFER", "currencyCode": "PKR",
          "paymentDate": "2026-09-18", "bankReference": "TRX-9",
          "allocations": [ { "invoiceUuid": "{{invoice}}", "amount": 1000 } ]
        }
        """;

        var req = JsonSerializer.Deserialize<RecordCustomerPaymentRequest>(json, Web)!;

        (req.PartnerId, req.Amount, req.Method, req.CurrencyCode, req.BankReference).Should().Be((partner, 2500.50m, "BANK_TRANSFER", "PKR", "TRX-9"));
        req.PaymentDate.Should().Be(new DateTime(2026, 9, 18));
        req.Allocations.Should().ContainSingle().Which.Should().Be(new ManualPaymentAllocation(invoice, 1000m));
    }

    [Fact]
    public void An_omitted_allocations_key_and_an_empty_one_are_told_apart()
    {
        var omitted = JsonSerializer.Deserialize<RecordCustomerPaymentRequest>("""{"amount":1}""", Web)!;
        var empty = JsonSerializer.Deserialize<RecordCustomerPaymentRequest>("""{"amount":1,"allocations":[]}""", Web)!;

        omitted.Allocations.Should().BeNull();
        empty.Allocations.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void An_allocate_and_an_invoice_update_request_bind_from_json()
    {
        var invoice = Guid.NewGuid();

        var allocate = JsonSerializer.Deserialize<AllocateCustomerPaymentRequest>($$"""{"allocations":[{"invoiceUuid":"{{invoice}}","amount":400}]}""", Web)!;
        allocate.Allocations.Should().ContainSingle().Which.InvoiceUuid.Should().Be(invoice);

        var update = JsonSerializer.Deserialize<UpdateSalesInvoiceRequest>("""{"dueDate":"2026-10-30","notes":"Net 45"}""", Web)!;
        (update.DueDate, update.Notes).Should().Be((new DateTime(2026, 10, 30), "Net 45"));

        var create = JsonSerializer.Deserialize<CreateSalesInvoiceRequest>($$"""{"deliveryUuid":"{{invoice}}"}""", Web)!;
        create.DeliveryUuid.Should().Be(invoice);
    }
}

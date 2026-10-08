using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using SMS.Modules.Finance.Controllers;
using SMS.Modules.Finance.Models;
using SMS.Shared.Authorization;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// Cross-cutting security audit of the SAP-alignment change (docs/finance/SAP-ALIGNMENT-PLAN.md, "API contract"):
/// the permission and module gates every new or changed endpoint carries, read off the controllers the way the
/// MVC pipeline reads them, and the request bodies — none of which may let a client set a server-owned field
/// (organization, who/when, status, the reversal/cancellation stamps, the exchange-rate snapshot).
/// </summary>
public class SapAlignmentEndpointSecurityAuditTests
{
    private static string? PermissionOf(MethodInfo action) =>
        action.GetCustomAttributes<RequirePermissionAttribute>().SingleOrDefault()?.Policy?["Permission:".Length..];

    private static IEnumerable<string> FeaturesOf(MethodInfo action) =>
        action.GetCustomAttributes<RequiresFeatureAttribute>().Select(a => a.FeatureCode)
            .Concat(action.DeclaringType!.GetCustomAttributes<RequiresFeatureAttribute>().Select(a => a.FeatureCode));

    private static MethodInfo Action(Type controller, string name) =>
        controller.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        ?? throw new InvalidOperationException($"{controller.Name}.{name} not found");

    // ── The permission matrix of the plan's API contract ─────────────────────

    public static IEnumerable<object?[]> Contract() =>
    [
        // Reads: any signed-in user (the global AuthorizeFilter), no permission, no module.
        [typeof(TaxCodesController),      nameof(TaxCodesController.GetList),               null,                                     false],
        [typeof(ExchangeRatesController), nameof(ExchangeRatesController.GetList),          null,                                     false],
        [typeof(ExchangeRatesController), nameof(ExchangeRatesController.Quote),            null,                                     false],
        // Writes: FINANCE_SETUP_MANAGE and MODULE_FINANCE.
        [typeof(TaxCodesController),      nameof(TaxCodesController.Create),                PermissionCodes.FINANCE_SETUP_MANAGE,     true],
        [typeof(TaxCodesController),      nameof(TaxCodesController.Update),                PermissionCodes.FINANCE_SETUP_MANAGE,     true],
        [typeof(TaxCodesController),      nameof(TaxCodesController.CreateFromRatesInUse),  PermissionCodes.FINANCE_SETUP_MANAGE,     true],
        // S-7 actions.
        [typeof(SalesInvoicesController), nameof(SalesInvoicesController.Cancel),           PermissionCodes.SALES_INVOICE_MANAGE,     true],
        [typeof(InvoicesController),      nameof(InvoicesController.Reverse),               PermissionCodes.INVOICE_PROCESS,          true],
    ];

    [Theory]
    [MemberData(nameof(Contract))]
    public void SecurityAudit_each_new_endpoint_carries_exactly_the_gate_the_contract_names(
        Type controller, string actionName, string? permission, bool needsFinanceModule)
    {
        var action = Action(controller, actionName);

        PermissionOf(action).Should().Be(permission, $"{controller.Name}.{actionName}'s permission");
        FeaturesOf(action).Contains("MODULE_FINANCE").Should().Be(needsFinanceModule, $"{controller.Name}.{actionName}'s module gate");

        action.GetCustomAttributes<AllowAnonymousAttribute>().Should().BeEmpty("no new endpoint is anonymous");
        controller.GetCustomAttributes<AllowAnonymousAttribute>().Should().BeEmpty();
    }

    [Fact]
    public void SecurityAudit_every_uuid_route_of_the_new_endpoints_is_guid_constrained()
    {
        // A {uuid:guid} constraint means a malformed id never reaches a service (404 from routing), and no
        // string id can be smuggled into a lookup.
        var routed = new[]
        {
            Action(typeof(TaxCodesController), nameof(TaxCodesController.Update)),
            Action(typeof(SalesInvoicesController), nameof(SalesInvoicesController.Cancel)),
            Action(typeof(InvoicesController), nameof(InvoicesController.Reverse)),
        };

        foreach (var action in routed)
            action.GetCustomAttributes<HttpMethodAttribute>().Single().Template.Should().StartWith("{uuid:guid}", action.Name);
    }

    // ── Mass assignment ──────────────────────────────────────────────────────

    private static readonly string[] ServerOwned =
    [
        "Id", "OrganizationId", "CreatedBy", "CreatedDate", "ModifiedBy", "ModifiedDate", "IsDelete", "IsDeleted",
        "Status", "Source",
        "ApprovedBy", "ApprovedAt",
        "ReversedAt", "ReversedBy", "ReversalReason",
        "CancelledAt", "CancelledBy", "CancellationReason",
        "ExchangeRate", "BaseCurrencyCode", "BaseTotalAmount", "BaseGrandTotal",
        "TaxPercent", "TaxCode", "TotalAmount", "PaidAmount", "AmountPaid", "BalanceDue",
    ];

    public static IEnumerable<object[]> RequestBodies() =>
    [
        [typeof(SaveTaxCodeRequest)],
        [typeof(SaveExchangeRateRequest)],
        [typeof(CancelSalesInvoiceRequest)],
        [typeof(ReverseInvoiceRequest)],
        [typeof(CreateInvoiceRequest)],
        [typeof(PatchInvoiceRequest)],
    ];

    [Theory]
    [MemberData(nameof(RequestBodies))]
    public void SecurityAudit_no_request_body_of_the_change_can_set_a_server_owned_field(Type body)
    {
        var settable = body.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Select(p => p.Name)
            .ToList();

        settable.Intersect(ServerOwned, StringComparer.OrdinalIgnoreCase).Should().BeEmpty(
            $"{body.Name} is bound straight from the client's JSON; a server-owned field on it is a mass-assignment hole");
    }

    [Fact]
    public void SecurityAudit_no_uuid_field_of_the_setup_bodies_lets_a_client_pick_or_retarget_a_row()
    {
        // Which row a write touches comes from the route, never from the body.
        foreach (var body in new[] { typeof(SaveTaxCodeRequest), typeof(SaveExchangeRateRequest), typeof(CancelSalesInvoiceRequest), typeof(ReverseInvoiceRequest) })
            body.GetProperties().Where(p => p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?))
                .Select(p => p.Name).Should().BeEmpty(body.Name);
    }
}

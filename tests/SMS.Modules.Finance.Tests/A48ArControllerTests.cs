using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using SMS.Modules.Finance.ArAp.Receivables;
using SMS.Shared.Authorization;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>A48 AR — the AR endpoints carry the contract's permissions (§2–§4) and writes the MODULE_FINANCE feature (§0).</summary>
public class A48ArControllerTests
{
    public static TheoryData<Type, string, string, string> Routes => new()
    {
        { typeof(CreditControlController), "GET",  "{customerUuid:guid}/exposure",  "AR_VIEW|CREDIT_CHECK|CREDIT_MANAGE" },
        { typeof(CreditControlController), "POST", "{customerUuid:guid}/check",     "CREDIT_CHECK" },
        { typeof(CreditControlController), "POST", "{customerUuid:guid}/hold",      "CREDIT_MANAGE" },
        { typeof(CreditControlController), "POST", "{customerUuid:guid}/release",   "CREDIT_MANAGE" },
        { typeof(CreditControlController), "POST", "{customerUuid:guid}/temporary-limit", "CREDIT_MANAGE" },
        { typeof(CreditControlController), "PUT",  "{customerUuid:guid}/risk-tier", "CREDIT_MANAGE" },
        { typeof(CreditControlController), "GET",  "{customerUuid:guid}/history",   "AR_VIEW|CREDIT_MANAGE" },
        { typeof(CreditControlController), "GET",  "exposures",                     "AR_VIEW|CREDIT_MANAGE" },
        { typeof(DunningController), "GET",  "procedures",                           "AR_VIEW|DUNNING_EXECUTE|DUNNING_ADMIN|FINANCE_AR_AP_ADMIN" },
        { typeof(DunningController), "POST", "procedures",                           "FINANCE_AR_AP_ADMIN" },
        { typeof(DunningController), "PUT",  "procedures/{uuid:guid}",               "FINANCE_AR_AP_ADMIN" },
        { typeof(DunningController), "POST", "runs/preview",                         "DUNNING_EXECUTE" },
        { typeof(DunningController), "POST", "runs/{uuid:guid}/execute",             "DUNNING_EXECUTE" },
        { typeof(DunningController), "POST", "runs/{uuid:guid}/cancel",              "DUNNING_EXECUTE" },
        { typeof(DunningController), "POST", "runs/{uuid:guid}/items/{itemUuid:guid}/skip", "DUNNING_EXECUTE" },
        { typeof(DunningController), "GET",  "runs",                                 "AR_VIEW|DUNNING_EXECUTE|DUNNING_ADMIN" },
        { typeof(DunningController), "GET",  "customers/{customerUuid:guid}/history", "AR_VIEW|DUNNING_EXECUTE|DUNNING_ADMIN" },
        { typeof(DunningController), "POST", "customers/{customerUuid:guid}/reset",  "DUNNING_ADMIN" },
        { typeof(ArCreditMemoController), "POST", "credit-memo",                     "AR_CREDIT_MEMO" },
        { typeof(ArCreditMemoController), "POST", "credit-memo/{creditNoteUuid:guid}/apply", "AR_CREDIT_MEMO" },
        { typeof(ArCreditMemoController), "GET",  "credit-memo/{creditNoteUuid:guid}/applications", "AR_VIEW|AR_CREDIT_MEMO" },
        { typeof(ArCreditMemoController), "GET",  "customers/{customerUuid:guid}/open-credits", "AR_VIEW|AR_CREDIT_MEMO" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public void Each_route_has_the_contract_permission_and_writes_the_feature_gate(Type controller, string verb, string template, string codes)
    {
        var action = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(m => m.GetCustomAttributes<HttpMethodAttribute>().Any(a => a.HttpMethods.Contains(verb) && a.Template == template));

        action.GetCustomAttribute<RequirePermissionAttribute>()!.AnyOf.Should().BeEquivalentTo(codes.Split('|'));

        var write = verb != "GET" && template != "{customerUuid:guid}/check";
        (action.GetCustomAttribute<RequiresFeatureAttribute>() is not null).Should().Be(write);
    }

    [Fact]
    public void The_routes_live_under_api_finance()
    {
        typeof(CreditControlController).GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/finance/credit");
        typeof(DunningController).GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/finance/dunning");
        typeof(ArCreditMemoController).GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/finance/ar");
    }
}

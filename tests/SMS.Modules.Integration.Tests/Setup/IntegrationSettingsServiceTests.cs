using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Connections;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Integration.Tests.Setup;

public class IntegrationSettingsServiceTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static async Task<T> As<T>(ConnectionsHarness h, Guid org, Func<IIntegrationSettingsService, Task<T>> act)
    {
        await using var scope = h.Scope(org);
        return await act(scope.ServiceProvider.GetRequiredService<IIntegrationSettingsService>());
    }

    private static async Task<IntegrationConnection> ConnectedWithReferenceAsync(ConnectionsHarness h, Guid? org = null)
    {
        var c = await h.SeedConnectionAsync(org ?? OrgA, ConnectionStatus.NeedsSetup);
        await SetupTestKit.SeedReferenceAsync(h, c);
        return c;
    }

    private static UpdateIntegrationSettingsRequest Valid() => new()
    {
        AutoPushCustomers        = true,
        AutoPushVendors          = false,
        AutoPushItems            = true,
        AutoPushSalesInvoices    = false,
        AutoPushBills            = true,
        ItemTypeDefault          = "Service",
        PartnerScope             = "AllActive",
        DefaultIncomeAccountId   = "1",
        DefaultExpenseAccountId  = "4",
        FreightExpenseAccountId  = "5",
        DiscountAccountId        = "2",
        DefaultPurchaseTaxCodeId = "10",
        DocumentStartDate        = new DateTime(2026, 7, 1, 15, 30, 0, DateTimeKind.Utc)
    };

    // ── Settings ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_a_connection_the_defaults_are_returned_and_nothing_is_created()
    {
        await using var h = ConnectionsHarness.Create();

        var settings = await As(h, OrgA, s => s.GetSettingsAsync());

        settings.Mode.Should().Be("DryRun");
        settings.PartnerScope.Should().Be("OnlyWhenReferenced");
        await using var db = h.OpenAs(OrgA);
        (await db.Settings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_valid_update_is_saved_and_audited_with_before_and_after()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await ConnectedWithReferenceAsync(h);

        var model = await As(h, OrgA, s => s.UpdateSettingsAsync(Valid(), 7));

        model.ItemTypeDefault.Should().Be("Service");
        model.PartnerScope.Should().Be("AllActive");
        model.AutoPushVendors.Should().BeFalse();
        model.DefaultExpenseAccountId.Should().Be("4");
        model.DocumentStartDate.Should().Be(new DateTime(2026, 7, 1), "a date, not an instant");
        model.Mode.Should().Be("DryRun", "mode is not changed through settings");

        await using var db = h.OpenAs(OrgA);
        var stored = await db.Settings.SingleAsync();
        (stored.ModifiedBy, stored.ConnectionId).Should().Be((7, c.Id));

        var audit = await db.SettingsAudit.SingleAsync();
        audit.Area.Should().Be("Settings");
        audit.UserId.Should().Be(7);
        JsonDocument.Parse(audit.BeforeJson!).RootElement.GetProperty("itemTypeDefault").GetString().Should().Be("NonInventory");
        JsonDocument.Parse(audit.AfterJson!).RootElement.GetProperty("itemTypeDefault").GetString().Should().Be("Service");
    }

    [Fact]
    public async Task Enum_values_are_case_insensitive()
    {
        await using var h = ConnectionsHarness.Create();
        await ConnectedWithReferenceAsync(h);
        var request = Valid();
        request.ItemTypeDefault = "noninventory";
        request.PartnerScope    = "onlywhenreferenced";

        var model = await As(h, OrgA, s => s.UpdateSettingsAsync(request, 7));

        (model.ItemTypeDefault, model.PartnerScope).Should().Be(("NonInventory", "OnlyWhenReferenced"));
    }

    [Theory]
    [InlineData("Inventory", "AllActive", "Item type 'Inventory'")]
    [InlineData("1", "AllActive", "Item type '1'")]
    [InlineData("", "AllActive", "Item type ''")]
    [InlineData("Service", "Everything", "Partner scope 'Everything'")]
    [InlineData("Service", "-1", "Partner scope '-1'")]
    public async Task Unknown_enum_values_are_refused(string itemType, string scope, string message)
    {
        await using var h = ConnectionsHarness.Create();
        await ConnectedWithReferenceAsync(h);
        var request = Valid();
        request.ItemTypeDefault = itemType;
        request.PartnerScope    = scope;

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.UpdateSettingsAsync(request, 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage($"{message}*Use one of*");
    }

    public static IEnumerable<object[]> BadAccounts() =>
    [
        // field, account id, expected message fragment
        ["income",   "99", "default income account '99' is not in QuickBooks' chart of accounts"],
        ["income",   "3",  "default income account 'Office Expenses' is a Expense account; it must be Income or Other Income"],
        ["income",   "7",  "default income account 'Old Sales' is inactive"],
        ["expense",  "1",  "default expense account 'Sales' is a Income account"],
        ["expense",  "6",  "default expense account 'Bank' is a Bank account"],
        ["freight",  "2",  "freight expense account 'Other Income' is a Other Income account"],
        ["discount", "4",  "discount account 'Cost of Sales' is a Cost of Goods Sold account"],
        ["discount", "6",  "discount account 'Bank'"]
    ];

    [Theory]
    [MemberData(nameof(BadAccounts))]
    public async Task Accounts_must_exist_be_active_and_be_the_right_type(string field, string id, string message)
    {
        await using var h = ConnectionsHarness.Create();
        await ConnectedWithReferenceAsync(h);
        var request = Valid();
        switch (field)
        {
            case "income":   request.DefaultIncomeAccountId  = id; break;
            case "expense":  request.DefaultExpenseAccountId = id; break;
            case "freight":  request.FreightExpenseAccountId = id; break;
            case "discount": request.DiscountAccountId       = id; break;
        }

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.UpdateSettingsAsync(request, 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage($"*{message}*");

        await using var db = h.OpenAs(OrgA);
        (await db.Settings.CountAsync()).Should().Be(0, "a refused update changes nothing");
        (await db.SettingsAudit.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("4")]   // "Cost of Goods Sold"
    [InlineData("8")]   // "CostofGoodsSold" — the SDK enum name
    [InlineData("5")]   // "Other Expense"
    [InlineData("3")]   // "Expense"
    public async Task Every_expense_type_spelling_is_accepted_for_expense_and_freight(string id)
    {
        await using var h = ConnectionsHarness.Create();
        await ConnectedWithReferenceAsync(h);
        var request = Valid();
        request.DefaultExpenseAccountId = id;
        request.FreightExpenseAccountId = id;

        (await As(h, OrgA, s => s.UpdateSettingsAsync(request, 7))).DefaultExpenseAccountId.Should().Be(id);
    }

    [Theory]
    [InlineData("99", "is not one of QuickBooks' tax codes")]
    [InlineData("12", "is inactive")]
    public async Task The_default_purchase_tax_code_must_exist_and_be_active(string id, string message)
    {
        await using var h = ConnectionsHarness.Create();
        await ConnectedWithReferenceAsync(h);
        var request = Valid();
        request.DefaultPurchaseTaxCodeId = id;

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.UpdateSettingsAsync(request, 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage($"*{message}*");
    }

    [Fact]
    public async Task Account_ids_cannot_be_checked_before_reference_data_is_loaded()
    {
        await using var h = ConnectionsHarness.Create();
        await h.SeedConnectionAsync(OrgA, ConnectionStatus.NeedsSetup);

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.UpdateSettingsAsync(Valid(), 7)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*Refresh the reference data first*");
    }

    [Fact]
    public async Task Clearing_the_accounts_needs_no_reference_data()
    {
        await using var h = ConnectionsHarness.Create();
        await h.SeedConnectionAsync(OrgA, ConnectionStatus.NeedsSetup);
        var request = new UpdateIntegrationSettingsRequest { DefaultIncomeAccountId = "  ", DiscountAccountId = "" };

        var model = await As(h, OrgA, s => s.UpdateSettingsAsync(request, 7));

        model.DefaultIncomeAccountId.Should().BeNull();
        model.DiscountAccountId.Should().BeNull();
    }

    [Fact]
    public async Task Settings_need_a_connection()
    {
        await using var h = ConnectionsHarness.Create();

        await FluentActions.Awaiting(() => As(h, OrgA, s => s.UpdateSettingsAsync(Valid(), 7)))
            .Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Settings_are_tenant_scoped()
    {
        await using var h = ConnectionsHarness.Create();
        await ConnectedWithReferenceAsync(h, OrgA);
        await As(h, OrgA, s => s.UpdateSettingsAsync(Valid(), 7));
        await ConnectedWithReferenceAsync(h, OrgB);

        (await As(h, OrgB, s => s.GetSettingsAsync())).ItemTypeDefault.Should().Be("NonInventory");
        (await As(h, OrgA, s => s.GetSettingsAsync())).ItemTypeDefault.Should().Be("Service");
    }
}

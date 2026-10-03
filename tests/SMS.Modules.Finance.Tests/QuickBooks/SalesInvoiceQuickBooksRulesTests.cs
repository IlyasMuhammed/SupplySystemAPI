using FluentAssertions;
using SMS.Modules.Finance.Integration;
using Xunit;

namespace SMS.Modules.Finance.Tests.QuickBooks;

/// <summary>Plan D-7/D-8 — which sales invoice changes QuickBooks hears about, as one table.</summary>
public class SalesInvoiceQuickBooksRulesTests
{
    [Theory]
    // A draft never goes, edited or not.
    [InlineData(null,             "DRAFT",          false, "None")]
    [InlineData("DRAFT",          "DRAFT",          true,  "None")]
    // Leaving DRAFT sends it.
    [InlineData("DRAFT",          "ISSUED",         false, "Upsert")]
    [InlineData(null,             "ISSUED",         false, "Upsert")]
    [InlineData("DRAFT",          "CREDIT_NOTE",    false, "Upsert")]   // the gateway refuses it, visibly
    // Payments and the calendar move the status only — nothing to send (D-8).
    [InlineData("ISSUED",         "PARTIALLY_PAID", false, "None")]
    [InlineData("PARTIALLY_PAID", "PAID",           false, "None")]
    [InlineData("ISSUED",         "PAID",           false, "None")]
    [InlineData("ISSUED",         "OVERDUE",        false, "None")]
    [InlineData("OVERDUE",        "PAID",           false, "None")]
    [InlineData("PAID",           "PARTIALLY_PAID", false, "None")]   // a bounced cheque
    [InlineData("ISSUED",         "ISSUED",         false, "None")]
    // An edit to an issued invoice's content is sent again, whatever the payment status.
    [InlineData("ISSUED",         "ISSUED",         true,  "Upsert")]
    [InlineData("PARTIALLY_PAID", "PARTIALLY_PAID", true,  "Upsert")]
    [InlineData("PAID",           "PAID",           true,  "Upsert")]
    [InlineData("OVERDUE",        "OVERDUE",        true,  "Upsert")]
    // Cancelling an invoice that went voids it; cancelling a draft never sent sends nothing.
    [InlineData("ISSUED",         "CANCELLED",      false, "Void")]
    [InlineData("OVERDUE",        "CANCELLED",      false, "Void")]
    [InlineData("PARTIALLY_PAID", "CANCELLED",      true,  "Void")]
    [InlineData("DRAFT",          "CANCELLED",      false, "None")]
    [InlineData(null,             "CANCELLED",      false, "None")]
    [InlineData("CANCELLED",      "CANCELLED",      true,  "None")]
    public void On_change(string? previous, string current, bool contentChanged, string expected)
    {
        SalesInvoiceQuickBooksRules.OnChange(previous, current, contentChanged).Should().Be(Enum.Parse<QuickBooksPushAction>(expected));
    }

    [Theory]
    [InlineData("DRAFT",          "None")]
    [InlineData("ISSUED",         "Upsert")]
    [InlineData("PARTIALLY_PAID", "Upsert")]
    [InlineData("PAID",           "Upsert")]
    [InlineData("OVERDUE",        "Upsert")]
    [InlineData("CREDIT_NOTE",    "Upsert")]
    [InlineData("CANCELLED",      "Void")]
    public void For_the_current_state(string status, string expected)
    {
        SalesInvoiceQuickBooksRules.ForCurrentState(status).Should().Be(Enum.Parse<QuickBooksPushAction>(expected));
    }
}

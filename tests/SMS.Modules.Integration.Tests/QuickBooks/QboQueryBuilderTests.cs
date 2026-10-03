using FluentAssertions;
using SMS.Modules.Integration.Providers.QuickBooks;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.QuickBooks;

public class QboQueryBuilderTests
{
    private readonly QboQueryBuilder _builder = new();

    // ── Literal escaping ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Acme", "'Acme'")]
    [InlineData("O'Brien", @"'O\'Brien'")]
    [InlineData("King's Groceries", @"'King\'s Groceries'")]
    [InlineData("''", @"'\'\''")]
    [InlineData(@"C:\Temp", @"'C:\\Temp'")]
    [InlineData(@"ends with \", @"'ends with \\'")]
    [InlineData(@"\'", @"'\\\''")]
    [InlineData("", "''")]
    public void Literal_escapes_quotes_and_backslashes(string value, string expected)
    {
        QboQueryBuilder.Literal(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("Café Zürich")]
    [InlineData("株式会社 テスト")]
    [InlineData("Ωmega — “quoted” ½")]
    [InlineData("emoji 🙂 name")]
    public void Literal_keeps_unicode_as_is(string value)
    {
        QboQueryBuilder.Literal(value).Should().Be("'" + value + "'");
    }

    [Theory]
    [InlineData("A\nB", "'AB'")]
    [InlineData("A\r\nB", "'AB'")]
    [InlineData("A\tB", "'AB'")]
    [InlineData("A\0B", "'AB'")]
    [InlineData("A\u001bB", "'AB'")]
    [InlineData("A\u007fB", "'AB'")]
    [InlineData("A\u0085B", "'AB'")]
    public void Literal_strips_control_characters(string value, string expected)
    {
        QboQueryBuilder.Literal(value).Should().Be(expected);
    }

    [Fact]
    public void Literal_rejects_null()
    {
        var act = () => QboQueryBuilder.Literal(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    /// Whatever the input, the literal is one quoted token: every quote inside it is escaped, so the
    /// query parser can never see the string end early.
    /// </summary>
    [Theory]
    [InlineData("x' OR Name LIKE '%")]
    [InlineData("x' or 1=1 --")]
    [InlineData("'; select * from Vendor where Name = 'y")]
    [InlineData(@"x\' OR Active IN (true,false) and DisplayName = '")]
    [InlineData(@"\\' OR '1'='1")]
    [InlineData("x'\n OR DisplayName = 'y")]
    public void Injection_attempts_stay_inside_one_string_literal(string hostile)
    {
        var literal = QboQueryBuilder.Literal(hostile);

        literal.Should().StartWith("'").And.EndWith("'");
        UnescapedQuoteCount(literal).Should().Be(2, "only the delimiting quotes may be unescaped");
        Unescape(literal).Should().Be(new string(hostile.Where(c => !char.IsControl(c)).ToArray()),
            "the parser must read back exactly the (control-free) value");
    }

    [Fact]
    public void A_hostile_name_produces_a_query_with_a_single_condition_on_the_name()
    {
        var query = _builder.FindByName(SyncKind.Customer, "x' OR Name LIKE '%");

        query.Should().Be(@"select * from Customer where DisplayName = 'x\' OR Name LIKE \'%' and Active IN (true, false)");
    }

    // ── Queries ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Customer_is_found_by_DisplayName_including_inactive()
    {
        _builder.FindByName(SyncKind.Customer, "O'Brien")
            .Should().Be(@"select * from Customer where DisplayName = 'O\'Brien' and Active IN (true, false)");
    }

    [Fact]
    public void Vendor_is_found_by_DisplayName_including_inactive()
    {
        _builder.FindByName(SyncKind.Vendor, "Acme")
            .Should().Be("select * from Vendor where DisplayName = 'Acme' and Active IN (true, false)");
    }

    [Fact]
    public void Item_is_found_by_Name_including_inactive()
    {
        _builder.FindByName(SyncKind.Item, "Widget")
            .Should().Be("select * from Item where Name = 'Widget' and Active IN (true, false)");
    }

    [Fact]
    public void Item_is_found_by_Sku_including_inactive()
    {
        _builder.FindItemBySku("WID-'1")
            .Should().Be(@"select * from Item where Sku = 'WID-\'1' and Active IN (true, false)");
    }

    [Theory]
    [InlineData(SyncKind.SalesInvoice)]
    [InlineData(SyncKind.Bill)]
    public void Documents_are_not_found_by_name(SyncKind kind)
    {
        var act = () => _builder.FindByName(kind, "x");
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Invoice_is_found_by_DocNumber()
    {
        _builder.FindInvoiceByDocNumber("INV-1'0")
            .Should().Be(@"select * from Invoice where DocNumber = 'INV-1\'0'");
    }

    [Fact]
    public void Bill_is_found_by_DocNumber()
    {
        _builder.FindBillByDocNumber("SUP-77")
            .Should().Be("select * from Bill where DocNumber = 'SUP-77'");
    }

    [Theory]
    [InlineData(SyncKind.Customer, "Customer")]
    [InlineData(SyncKind.Vendor, "Vendor")]
    [InlineData(SyncKind.Item, "Item")]
    public void List_page_includes_inactive_and_pages(SyncKind kind, string entity)
    {
        _builder.ListPage(kind, 1001, 1000)
            .Should().Be($"select * from {entity} where Active IN (true, false) STARTPOSITION 1001 MAXRESULTS 1000");
    }

    [Theory]
    [InlineData(5000, 1000)]
    [InlineData(1001, 1000)]
    [InlineData(1000, 1000)]
    [InlineData(250, 250)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void List_page_caps_MaxResults_at_1000(int requested, int expected)
    {
        _builder.ListPage(SyncKind.Customer, 1, requested).Should().EndWith($"MAXRESULTS {expected}");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-10, 1)]
    [InlineData(1, 1)]
    [InlineData(2001, 2001)]
    public void List_page_start_position_is_one_based(int requested, int expected)
    {
        _builder.ListPage(SyncKind.Vendor, requested, 10).Should().Contain($"STARTPOSITION {expected} ");
    }

    [Theory]
    [InlineData(SyncKind.SalesInvoice)]
    [InlineData(SyncKind.Bill)]
    public void Documents_cannot_be_listed(SyncKind kind)
    {
        var act = () => _builder.ListPage(kind, 1, 10);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("Account")]
    [InlineData("TaxCode")]
    [InlineData("TaxRate")]
    [InlineData("Term")]
    [InlineData("CompanyCurrency")]
    public void Reference_pages(string entity)
    {
        _builder.SelectPage(entity, 1, 1000).Should().Be($"select * from {entity} STARTPOSITION 1 MAXRESULTS 1000");
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Account where 1=1")]
    [InlineData("account")]
    [InlineData("")]
    public void Reference_pages_only_accept_known_entities(string entity)
    {
        var act = () => _builder.SelectPage(entity, 1, 10);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Helpers: a tiny model of the QBO string parser ─────────────────────────────────────

    private static int UnescapedQuoteCount(string literal)
    {
        var count = 0;
        for (var i = 0; i < literal.Length; i++)
        {
            if (literal[i] == '\\') { i++; continue; }
            if (literal[i] == '\'') count++;
        }
        return count;
    }

    private static string Unescape(string literal)
    {
        var inner = literal[1..^1];
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length) { result.Append(inner[++i]); continue; }
            result.Append(inner[i]);
        }
        return result.ToString();
    }
}

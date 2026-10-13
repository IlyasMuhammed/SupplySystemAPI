using FluentAssertions;
using SMS.Modules.Import.Domain;
using SMS.Modules.Import.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Import.Tests;

/// <summary>A40 C6/C7 — value parsing, every generic validation code, the four auto-mapping tiers and threshold.</summary>
public class RulesTests
{
    private static readonly IReadOnlyList<ImportFieldDefinition> Fields =
        new ImportTargetRegistry([FakeTarget.Products()]).Get("PRODUCT").Fields;

    private static GenericRowResult Check(params (string Field, string? Value)[] values) =>
        ImportGenericValidator.Validate(Fields, values.ToDictionary(v => v.Field, v => v.Value));

    // ── Values ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("yes", true)] [InlineData("Y", true)] [InlineData("1", true)] [InlineData("TRUE", true)]
    [InlineData("no", false)] [InlineData("n", false)] [InlineData("0", false)] [InlineData("False", false)]
    public void Booleans(string text, bool expected)
    {
        ImportValueParser.TryParseBoolean(text, out var b).Should().BeTrue();
        b.Should().Be(expected);
    }

    [Theory]
    [InlineData("2026-03-15")] [InlineData("03-15-2026")] [InlineData("03/15/2026")] [InlineData("15-Mar-2026")]
    [InlineData("46096")] [InlineData("2026-03-15T00:00:00")]
    public void Dates(string text)
    {
        ImportValueParser.TryParseDate(text, out var d).Should().BeTrue(text);
        d.Should().Be(new DateOnly(2026, 3, 15));
    }

    [Theory]
    [InlineData("15/03/2026")] [InlineData("hello")] [InlineData("0")] [InlineData("9999999")]
    public void Not_dates(string text) => ImportValueParser.TryParseDate(text, out _).Should().BeFalse();

    [Theory]
    [InlineData("1,234.56", "1234.56")] [InlineData("1.234,56", "1234.56")] [InlineData("1234,56", "1234.56")]
    [InlineData("1,234", "1234")] [InlineData("1 234 567.5", "1234567.5")] [InlineData("-12", "-12")]
    [InlineData("1.234.567", "1234567")] [InlineData("12.50", "12.5")]
    public void Decimals(string text, string expected)
    {
        ImportValueParser.TryParseDecimal(text, out var d).Should().BeTrue();
        ImportValueParser.Format(d).Should().Be(expected);
    }

    // ── Generic validation codes ─────────────────────────────────────────────

    [Fact]
    public void Trimming_empty_to_null_defaults_and_normalized_values()
    {
        var r = Check(("code", "  P-1 "), ("name", "Widget"), ("price", " 1,234.5 "), ("active", null), ("opened", "15-Mar-2026"), ("email", ""));
        r.Errors.Should().BeEmpty();
        r.Values["code"].Should().Be("P-1");
        r.Values["price"].Should().Be("1234.5");
        r.Values["active"].Should().BeNull("an optional field's default is the target's create default — blank stays blank so an UPDATE keeps the stored value");
        r.Values["opened"].Should().Be("2026-03-15");
        r.Values["email"].Should().BeNull();
    }

    [Fact]
    public void A_required_field_with_a_default_fills_a_blank_or_unmapped_cell()
    {
        IReadOnlyList<ImportFieldDefinition> fields =
        [
            new("code", "Code", null, ImportFieldTypes.Text, true, true, 1, null),
            new("kind", "Kind", null, ImportFieldTypes.Text, true, false, 2, null, DefaultValue: "STD"),
            new("active", "Active", null, ImportFieldTypes.Boolean, false, false, 3, null, DefaultValue: "true")
        ];
        var blank = ImportGenericValidator.Validate(fields, new Dictionary<string, string?> { ["code"] = "A", ["kind"] = " ", ["active"] = "" });
        blank.Errors.Should().BeEmpty();
        (blank.Values["kind"], blank.Values["active"]).Should().Be(("STD", null));
        var unmapped = ImportGenericValidator.Validate(fields, new Dictionary<string, string?> { ["code"] = "A" });
        unmapped.Values["kind"].Should().Be("STD");
        unmapped.Values.ContainsKey("active").Should().BeFalse();
    }

    [Theory]
    [InlineData("code", "  ", ImportErrorCodes.Required)]
    [InlineData("qty", "1.5", ImportErrorCodes.InvalidType)]
    [InlineData("price", "abc", ImportErrorCodes.InvalidType)]
    [InlineData("active", "maybe", ImportErrorCodes.InvalidType)]
    [InlineData("code", "123456789012345678901", ImportErrorCodes.MaxLength)]
    [InlineData("price", "100000.01", ImportErrorCodes.OutOfRange)]
    [InlineData("qty", "-1", ImportErrorCodes.OutOfRange)]
    [InlineData("price", "1.234", ImportErrorCodes.InvalidPrecision)]
    [InlineData("ref", "ab-12", ImportErrorCodes.PatternMismatch)]
    [InlineData("email", "not-an-email", ImportErrorCodes.InvalidEmail)]
    [InlineData("phone", "12ab", ImportErrorCodes.InvalidPhone)]
    [InlineData("phone", "12345", ImportErrorCodes.InvalidPhone)]
    [InlineData("opened", "31/31/2026", ImportErrorCodes.InvalidDate)]
    public void Each_generic_code(string field, string value, string code)
    {
        var values = new List<(string, string?)> { ("code", "P-1"), ("name", "N") };
        values.RemoveAll(v => v.Item1 == field);
        values.Add((field, value));
        var r = Check(values.ToArray());
        r.Errors.Should().ContainSingle().Which.Should().Match<ImportFieldError>(e => e.Code == code && e.Field == field);
    }

    [Fact]
    public void Precision_ignores_trailing_zeros_and_messages_name_the_field()
    {
        Check(("code", "P"), ("name", "N"), ("price", "1.500")).Errors.Should().BeEmpty();
        Check(("code", "P"), ("name", null)).Errors.Single().Message.Should().Be("Product Name is required.");
        Check(("code", "P"), ("name", "N"), ("price", "-1")).Errors.Single().Message.Should().Be("Selling Price must be between 0 and 100000.");
    }

    [Fact]
    public void Currency_fields_are_three_letter_codes()
    {
        var f = new ImportFieldDefinition("currency_code", "Currency", null, ImportFieldTypes.Currency, false, false, 1, "USD");
        var ok = ImportGenericValidator.Validate([f], new Dictionary<string, string?> { ["currency_code"] = " pkr " });
        ok.Errors.Should().BeEmpty();
        ok.Values["currency_code"].Should().Be("PKR");
        ImportGenericValidator.Validate([f], new Dictionary<string, string?> { ["currency_code"] = "12.5" })
            .Errors.Single().Should().Match<ImportFieldError>(e => e.Code == ImportErrorCodes.InvalidType && e.Message == "Currency must be a 3-letter currency code.");
    }

    [Fact]
    public void Duplicate_in_file_on_match_fields_and_external_id()
    {
        var rows = new List<(int, IReadOnlyDictionary<string, string?>)>
        {
            (2, new Dictionary<string, string?> { ["code"] = "A", ["external_id"] = "X1" }),
            (3, new Dictionary<string, string?> { ["code"] = "a", ["external_id"] = "X2" }),
            (4, new Dictionary<string, string?> { ["code"] = "B", ["external_id"] = "x1" }),
            (5, new Dictionary<string, string?> { ["code"] = null }),
            (6, new Dictionary<string, string?> { ["code"] = null }),
        };
        var dups = ImportGenericValidator.FindInFileDuplicates(rows, ["code"], Fields);
        dups.Keys.Should().BeEquivalentTo([3, 4]);
        dups[3].Single().Should().Match<ImportFieldError>(e => e.Code == ImportErrorCodes.DuplicateInFile && e.Message == "Duplicate of row 2 (Product Code).");
        dups[4].Single().Field.Should().Be("external_id");
    }

    // ── Auto-mapping ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("code", "code", 1.00)]
    [InlineData("PRODUCT NAME", "name", 1.00)]
    [InlineData("product_name", "name", 0.95)]
    [InlineData("Selling-Price", "price", 0.95)]
    [InlineData("MATNR", "external_id", null)] // MATNR → sku; this profile has no sku field
    [InlineData("sku", "code", 0.90)]          // the field's own alias
    [InlineData("legacy_id", "external_id", 0.90)]
    [InlineData("Ext ID", "external_id", 0.90)]
    [InlineData("Source_ID", "external_id", 0.90)]
    [InlineData("Quantty", "qty", 0.91)]
    [InlineData("Contact Emial", "email", 0.91)]
    [InlineData("Colour", "qty", null)]
    [InlineData("Prodct Code", "code", 0.91)]
    public void Tiers(string header, string field, double? expected)
    {
        var f = Fields.Single(x => x.FieldName == field);
        var score = ImportAutoMapper.Score(header, f);
        if (expected is null) score.Should().BeNull();
        else if (expected == 0.91) score.Should().BeInRange(0.70m, 0.89m, "fuzzy scores are capped at 0.89");
        else score.Should().Be((decimal)expected);
    }

    [Fact]
    public void Sap_style_names_map_through_the_built_in_aliases()
    {
        var sku = new ImportFieldDefinition("sku", "SKU", null, ImportFieldTypes.Text, true, true, 1, null);
        var name = new ImportFieldDefinition("name", "Name", null, ImportFieldTypes.Text, true, false, 2, null);
        var map = ImportAutoMapper.Map(["MATNR", "MAKTX", "ext_id", "Unrelated"], [ImportTargetRegistry.ExternalIdField, sku, name]);
        map.Select(m => m.FieldName).Should().Equal("sku", "name", "external_id", null);
        map.Take(3).Should().OnlyContain(m => m.Confidence == 0.90m);
        map[3].Confidence.Should().BeNull();
    }

    [Fact]
    public void Each_field_and_column_is_used_once_best_score_first_and_below_threshold_stays_unmapped()
    {
        var map = ImportAutoMapper.Map(["Product Cod", "Product Code", "zz", "Is Activ"], Fields);
        map[1].Should().Match<AutoMatch>(m => m.FieldName == "code" && m.Confidence == 1.00m);
        map[0].FieldName.Should().NotBe("code", "the exact header won the field");
        map[2].FieldName.Should().BeNull();
        map[3].Should().Match<AutoMatch>(m => m.FieldName == "active" && m.Confidence >= 0.70m && m.Confidence <= 0.89m);
        ImportAutoMapper.Levenshtein("kitten", "sitting", 5).Should().Be(3);
    }
}

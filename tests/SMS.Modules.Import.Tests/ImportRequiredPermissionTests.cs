using FluentAssertions;
using SMS.Modules.Import.Domain;
using SMS.Modules.Import.Models;
using SMS.Modules.Import.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Import.Tests;

/// <summary>A49 D-POST-5: a profile's required codes, on top of the import rights. The gate runs at session start, validate and execute.</summary>
public class ImportRequiredPermissionTests
{
    private static readonly ImportProfileDefinition Def = new("X", "Employees", null, 1, false, ["a"], [], RequiredPermissions: ["HRM_EMPLOYEE_EDIT", "HRM_BANK_EDIT"]);

    private static ImportCaller Caller(params string[] codes) => new(1, "U", new HashSet<string>(codes));

    [Fact]
    public async Task Import_rights_alone_are_refused_and_the_message_names_what_is_missing()
    {
        var refused = await new ImportOpeningGate().Invoking(g => g.EnsureAllowedAsync(Guid.NewGuid(), Def, Caller("IMPORT_UPLOAD", "IMPORT_EXECUTE", "HRM_EMPLOYEE_EDIT"), default))
            .Should().ThrowAsync<ForbiddenException>();
        refused.Which.Message.Should().Contain("HRM_BANK_EDIT").And.NotContain("HRM_EMPLOYEE_EDIT");
    }

    private static readonly ImportProfileDefinition Secret = Def with { SensitiveFields = ["cnic"] };

    private sealed class Reversing : IEncryptionService
    {
        public string Encrypt(string plaintext) => new(plaintext.Reverse().ToArray());
        public string Decrypt(string ciphertext) => new(ciphertext.Reverse().ToArray());
    }

    [Fact]
    public void A_sensitive_profile_is_encrypted_at_rest_and_another_is_untouched()
    {
        var p = new ImportRowProtector(new Reversing());
        var stored = p.Protect(Secret, "[\"12345\"]");
        stored.Should().StartWith("enc:").And.NotContain("12345");
        p.Open(stored).Should().Be("[\"12345\"]");

        p.Protect(Def, "[\"12345\"]").Should().Be("[\"12345\"]");
        p.Open("[\"12345\"]").Should().Be("[\"12345\"]");
    }

    [Fact]
    public void Without_encryption_a_sensitive_profile_fails_closed() =>
        new ImportRowProtector().Invoking(p => p.Protect(Secret, "x")).Should().Throw<InvalidOperationException>();

    [Fact]
    public void Masking_covers_sensitive_and_unmapped_values_and_errors_lose_the_value()
    {
        ImportRowProtector.MaskValue(Secret, "cnic", "12345").Should().Be(ImportRowProtector.Mask);
        ImportRowProtector.MaskValue(Secret, null, "12345").Should().Be(ImportRowProtector.Mask, "an unmapped column could hold anything");
        ImportRowProtector.MaskValue(Secret, "name", "Sara").Should().Be("Sara");
        ImportRowProtector.MaskValue(Def, "cnic", "12345").Should().Be("12345", "other profiles are unchanged");

        ImportRowProtector.Sanitize(Secret, [new("cnic", "DUPLICATE_IN_FILE", "Duplicate of row 2.", "12345"), new("name", "X", "m", "Sara")])
            .Select(e => e.Value).Should().Equal(null, "Sara");
        ImportRowProtector.Sanitize(Def, [new("cnic", "X", "m", "12345")]).Single().Value.Should().Be("12345");
    }

    [Theory]
    [InlineData("05/01/2024", false)]
    [InlineData("25/01/2024", false)]
    [InlineData("01-05-2024", false)]
    [InlineData("2024-01-05", true)]
    [InlineData("05-Jan-2024", true)]
    [InlineData("45000", true)]
    public void Unambiguous_dates_refuse_what_reads_two_ways(string text, bool ok) =>
        ImportValueParser.TryParseUnambiguousDate(text, out _).Should().Be(ok);

    [Fact]
    public void A_field_without_the_flag_parses_as_before()
    {
        ImportField(false).Values["d"].Should().Be("2024-05-01", "05/01/2024 stays month-first for other profiles");
        ImportField(true).Errors.Single().Code.Should().Be(ImportErrorCodes.InvalidDate);

        static GenericRowResult ImportField(bool strict) => ImportGenericValidator.Validate(
            [new ImportFieldDefinition("d", "D", null, ImportFieldTypes.Date, false, false, 1, null, UnambiguousDateOnly: strict)],
            new Dictionary<string, string?> { ["d"] = "05/01/2024" });
    }

    [Fact]
    public async Task All_codes_pass_and_a_profile_with_none_is_unaffected()
    {
        await new ImportOpeningGate().Invoking(g => g.EnsureAllowedAsync(Guid.NewGuid(), Def, Caller("HRM_EMPLOYEE_EDIT", "HRM_BANK_EDIT"), default)).Should().NotThrowAsync();
        await new ImportOpeningGate().Invoking(g => g.EnsureAllowedAsync(Guid.NewGuid(), Def with { RequiredPermissions = null }, Caller(), default)).Should().NotThrowAsync();
    }
}

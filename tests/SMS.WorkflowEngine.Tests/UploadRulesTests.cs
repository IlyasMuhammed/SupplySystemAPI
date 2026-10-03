using FluentAssertions;
using Microsoft.AspNetCore.StaticFiles;
using SMS.Shared.Files;
using Xunit;

namespace SMS.WorkflowEngine.Tests;

/// <summary>
/// SMS.Shared's <see cref="UploadRules"/> — what every upload endpoint accepts before writing a file that the
/// API then serves back from its own origin. Checked against the static file middleware's real extension map
/// (<see cref="FileExtensionContentTypeProvider"/>), so a type the middleware would serve as a page or a script
/// cannot slip in, however many extensions it learns.
/// </summary>
public class UploadRulesTests
{
    private const long Small = 1024;

    /// <summary>What a browser runs, or renders as a document of the origin that served it.</summary>
    private static bool ScriptCapable(string contentType)
    {
        var type = contentType.ToLowerInvariant();
        return type.Contains("html") || type is "text/xml" or "application/xml" || type.EndsWith("+xml")
            || type.Contains("javascript") || type.Contains("ecmascript") || type.Contains("xsl")
            || type.Contains("x-component") || type.Contains("hta") || type.Contains("rfc822") || type.Contains("flash");
    }

    public static IEnumerable<object[]> ScriptCapableExtensions() =>
        new FileExtensionContentTypeProvider().Mappings
            .Where(m => ScriptCapable(m.Value))
            .Select(m => new object[] { m.Key, m.Value });

    [Theory]
    [MemberData(nameof(ScriptCapableExtensions))]
    public void No_extension_the_static_file_middleware_serves_as_a_page_or_script_is_accepted(string extension, string servedAs)
    {
        UploadRules.Refusal("statement" + extension, Small, "application/octet-stream").Should().NotBeNull(servedAs);
        UploadRules.Refusal("statement" + extension.ToUpperInvariant(), Small, "application/octet-stream").Should().NotBeNull(servedAs);
        UploadRules.DiskExtension("statement" + extension).Should().BeEmpty();
    }

    [Theory]
    [InlineData("quote.pdf",          "application/pdf")]
    [InlineData("photo.JPG",          "image/jpeg")]
    [InlineData("photo.jpeg",         "image/jpeg")]
    [InlineData("scan.png",           "image/png")]
    [InlineData("scan.tiff",          "image/tiff")]
    [InlineData("contract.docx",      "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("prices.xlsx",        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("prices.csv",         "text/csv")]
    [InlineData("note.txt",           "text/plain")]
    [InlineData("supplier-mail.msg",  "application/vnd.ms-outlook")]
    [InlineData("drawings.zip",       "application/zip")]
    [InlineData("part.dwg",           "image/vnd.dwg")]
    public void An_ordinary_business_file_is_accepted_and_recorded_as_its_extension_says(string fileName, string recordedAs)
    {
        UploadRules.Refusal(fileName, Small, "application/octet-stream").Should().BeNull();
        UploadRules.ContentTypeOf(fileName).Should().Be(recordedAs);
        UploadRules.DiskExtension(fileName).Should().Be(Path.GetExtension(fileName).ToLowerInvariant());
    }

    [Fact]
    public void Every_accepted_extension_is_served_as_something_harmless_or_not_served_at_all()
    {
        var served = new FileExtensionContentTypeProvider().Mappings;
        string[] accepted =
        [
            ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".heic", ".doc", ".docx", ".xls",
            ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".csv", ".txt", ".msg", ".zip", ".7z", ".rar", ".dwg", ".dxf"
        ];

        foreach (var extension in accepted)
        {
            UploadRules.Refusal("f" + extension, Small, null).Should().BeNull(extension);
            if (served.TryGetValue(extension, out var type))
                ScriptCapable(type).Should().BeFalse($"{extension} is served as {type}");
            ScriptCapable(UploadRules.ContentTypeOf("f" + extension)).Should().BeFalse(extension);
        }
    }

    [Theory]
    [InlineData("statement.html",   "application/pdf")]
    [InlineData("invoice.pdf.html", "application/pdf")]
    [InlineData("statement.html ",  "application/pdf")]   // Windows drops the trailing space on disk
    [InlineData("helper.js",        "application/pdf")]
    [InlineData("logo.svg",         "image/png")]
    [InlineData("page.hxt",         "application/octet-stream")]
    [InlineData("schema.xsd",       "application/octet-stream")]
    [InlineData("README",           "text/plain")]        // no extension at all
    [InlineData("program.exe",      "application/octet-stream")]
    public void Anything_not_on_the_list_is_refused_whatever_the_client_says_it_is(string fileName, string contentType)
    {
        UploadRules.Refusal(fileName, Small, contentType).Should().NotBeNull();
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("TEXT/HTML; charset=utf-8")]
    [InlineData("image/svg+xml")]
    [InlineData("application/xhtml+xml")]
    [InlineData("application/atom+xml")]
    [InlineData("text/xml")]
    [InlineData("application/javascript")]
    public void A_file_the_client_announces_as_a_page_or_script_is_refused_even_with_a_harmless_name(string contentType)
    {
        UploadRules.Refusal("quote.pdf", Small, contentType).Should().NotBeNull();
    }

    [Fact]
    public void Size_is_checked_both_ways()
    {
        UploadRules.Refusal("a.pdf", 0, "application/pdf").Should().NotBeNull();
        UploadRules.Refusal("a.pdf", UploadRules.MaxFileBytes, "application/pdf").Should().BeNull();
        UploadRules.Refusal("a.pdf", UploadRules.MaxFileBytes + 1, "application/pdf").Should().Contain("20 MB");
    }

    [Theory]
    [InlineData("logo.png",  true)]
    [InlineData("logo.JPEG", true)]
    [InlineData("logo.webp", true)]
    [InlineData("logo.pdf",  false)]
    [InlineData("logo.tiff", false)]   // not drawn by every browser
    [InlineData("logo.svg",  false)]
    public void Where_only_a_picture_will_do_only_a_picture_is_accepted(string fileName, bool accepted)
    {
        (UploadRules.Refusal(fileName, Small, null, picturesOnly: true) is null).Should().Be(accepted);
    }

    [Theory]
    [InlineData("..\\..\\windows\\evil.pdf", "evil.pdf")]
    [InlineData("../../etc/passwd.pdf",      "passwd.pdf")]
    [InlineData("C:\\temp\\SINV-1.pdf",      "SINV-1.pdf")]
    [InlineData("SINV\r\n-1.pdf",            "SINV-1.pdf")]
    [InlineData("quote\t<img src=x>.pdf",    "quoteimg src=x.pdf")]
    [InlineData("quote<b>bold</b>.pdf",      "b.pdf")]          // "/" is a separator: only what follows the last one is the name
    [InlineData("a:b|c?d*e\"f.pdf",          "abcdef.pdf")]
    public void A_display_name_is_the_leaf_with_nothing_a_file_name_cannot_hold(string given, string shown)
    {
        UploadRules.DisplayName(given).Should().Be(shown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..\\..\\")]
    [InlineData("...")]
    public void A_name_with_nothing_usable_left_has_no_display_name_and_is_refused(string? given)
    {
        UploadRules.DisplayName(given).Should().BeNull();
        UploadRules.Refusal(given, Small, "application/pdf").Should().NotBeNull();
    }

    [Fact]
    public void A_long_name_is_cut_to_the_column_from_the_middle_so_it_keeps_its_extension()
    {
        var shown = UploadRules.DisplayName(new string('q', 400) + ".pdf")!;

        shown.Length.Should().Be(UploadRules.MaxFileNameLength);
        shown.Should().EndWith(".pdf");
        UploadRules.Refusal(new string('q', 400) + ".pdf", Small, "application/pdf").Should().BeNull();
        UploadRules.DisplayName("abcdef.pdf", maxLength: 8).Should().Be("abcd.pdf");
    }

    [Fact]
    public void The_disk_extension_is_never_anything_the_list_does_not_name()
    {
        UploadRules.DiskExtension("x.PDF").Should().Be(".pdf");
        UploadRules.DiskExtension("x.html").Should().BeEmpty();
        UploadRules.DiskExtension("x.pdf:evil.html").Should().BeEmpty();
        UploadRules.DiskExtension("x").Should().BeEmpty();
        UploadRules.ContentTypeOf("x.html").Should().Be("application/octet-stream");
    }
}

using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Accounting.Tests;

/// <summary>A42 D-5/D-6 template seeding and the template API — T-TPL-01..05, CSV/JSON formats.</summary>
public class TemplateTests
{
    [Fact]
    public async Task Seeder_creates_the_16_headers_and_the_three_authored_charts_idempotently()
    {
        using var h = new AccountingHarness();
        await h.SeedTemplatesAsync();

        var templates = await h.Db.CoaTemplates.IgnoreQueryFilters().ToListAsync();
        templates.Should().HaveCount(16).And.OnlyContain(t => t.IsGlobal && t.OrganizationId == null && t.IsActive && t.Version == "1.0");
        templates.Where(t => t.AccountCount > 0).Select(t => (t.TemplateCode, t.AccountCount)).Should().BeEquivalentTo(
            [("GLOBAL_GENERAL", 104), ("US_GENERAL", 110), ("PK_GENERAL", 110)]);
        templates.Single(t => t.TemplateCode == "US_GENERAL").Should().Match<CoaTemplate>(t =>
            t.CountryCode == "USA" && t.CurrencyCode == "USD" && t.BusinessType == "General" && t.TemplateName == "United States — General Business");
        (await h.Db.CoaTemplateItems.CountAsync()).Should().Be(324);
        var itemIds = await h.Db.CoaTemplateItems.Select(i => i.Id).OrderBy(i => i).ToListAsync();

        h.NewScope();
        await h.SeedTemplatesAsync();
        (await h.Db.CoaTemplates.IgnoreQueryFilters().CountAsync()).Should().Be(16);
        (await h.Db.CoaTemplateItems.Select(i => i.Id).OrderBy(i => i).ToListAsync()).Should().Equal(itemIds, "same version: items untouched");
    }

    [Fact]
    public async Task A_new_version_replaces_the_items_and_an_invalid_file_is_skipped()
    {
        using var h = new AccountingHarness();
        await h.SeedTemplatesAsync();
        var us = await h.Db.CoaTemplates.IgnoreQueryFilters().SingleAsync(t => t.TemplateCode == "US_GENERAL");
        us.Version = "0.9";
        us.TemplateName = "stale";
        await h.Db.SaveChangesAsync();
        var before = await h.Db.CoaTemplateItems.Where(i => i.TemplateId == us.Id).Select(i => i.Id).ToListAsync();

        h.NewScope();
        await h.SeedTemplatesAsync();
        us = await h.Db.CoaTemplates.IgnoreQueryFilters().SingleAsync(t => t.TemplateCode == "US_GENERAL");
        us.Version.Should().Be("1.0");
        us.TemplateName.Should().Be("United States — General Business");
        var after = await h.Db.CoaTemplateItems.Where(i => i.TemplateId == us.Id).Select(i => i.Id).ToListAsync();
        after.Should().HaveCount(110).And.NotIntersectWith(before);

        // An invalid items file: logged, the template keeps what it had.
        h.NewScope();
        var broken = new TemplateSeeder(h.Db, null, name => name == "US_GENERAL.json"
            ? """{ "version": "2.0", "accounts": [ { "code": "1", "name": "Assets", "root_type": "Asset", "level": 1, "is_group": true } ] }"""
            : TemplateSeeder.ReadEmbedded(name));
        await broken.SeedAsync();
        (await h.Db.CoaTemplates.IgnoreQueryFilters().SingleAsync(t => t.TemplateCode == "US_GENERAL")).Should().Match<CoaTemplate>(t =>
            t.Version == "1.0" && t.AccountCount == 110);
    }

    [Fact]
    public async Task T_TPL_01_list_filters_and_search()
    {
        using var h = new AccountingHarness();
        await h.SeedTemplatesAsync();

        (await h.Templates().ListAsync(h.Viewer(), "USA", null, null)).Select(t => t.TemplateCode)
            .Should().Equal("US_GENERAL", "US_RETAIL", "US_MANUFACTURING", "US_SERVICES");
        (await h.Templates().ListAsync(h.Viewer(), "GLOBAL", null, null)).Select(t => t.TemplateCode)
            .Should().Equal("GLOBAL_GENERAL", "GLOBAL_SERVICES", "GLOBAL_NONPROFIT");
        (await h.Templates().ListAsync(h.Viewer(), null, "Retail", null)).Select(t => t.TemplateCode).Should().BeEquivalentTo("US_RETAIL", "GB_RETAIL");
        (await h.Templates().ListAsync(h.Viewer(), null, null, "Pakistan")).Select(t => t.TemplateCode).Should().Equal("PK_GENERAL");
        var all = await h.Templates().ListAsync(h.Viewer(), null, null, null);
        all.Should().HaveCount(16).And.OnlyContain(t => t.IsSystem);

        var filters = await h.Templates().FiltersAsync(h.Viewer());
        filters.Countries.Should().ContainEquivalentOf(new { Code = "PAK", Name = "Pakistan" });
        filters.Countries.Should().HaveCount(9);
        filters.BusinessTypes.Should().BeEquivalentTo("General", "Services", "NonProfit", "Retail", "Manufacturing");

        await ((Func<Task>)(() => h.Templates().ListAsync(h.Nobody(), null, null, null))).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task T_TPL_02_csv_download_has_the_five_roots_and_resolvable_parent_codes()
    {
        using var h = new AccountingHarness();
        await h.SeedTemplatesAsync();
        var file = await h.Templates().DownloadAsync(h.Viewer(), await h.TemplateUuidAsync("US_GENERAL"), "csv");

        file.FileName.Should().Be("US_GENERAL.csv");
        file.ContentType.Should().Be("text/csv");
        var text = Encoding.UTF8.GetString(file.Content).TrimStart('﻿');
        text.Should().StartWith(ChartTestHelpers.CsvHeader + "\r\n1,Assets,,1,Asset,true,,");
        var parsed = CoaFile.ParseCsv(text);
        parsed.Errors.Should().BeEmpty();
        parsed.Rows.Should().HaveCount(110);
        parsed.Rows.Where(r => r.Level == 1).Select(r => r.RootType).Should().Equal(RootTypes.All);
        CoaValidator.Validate(parsed.Rows).Should().BeEmpty();
        var codes = parsed.Rows.Select(r => r.Code).ToHashSet();
        parsed.Rows.Where(r => r.ParentCode != null).Should().OnlyContain(r => codes.Contains(r.ParentCode!));
    }

    [Fact]
    public async Task T_TPL_03_json_download_is_a_nested_tree()
    {
        using var h = new AccountingHarness();
        await h.SeedTemplatesAsync();
        var file = await h.Templates().DownloadAsync(h.Viewer(), await h.TemplateUuidAsync("US_GENERAL"), "json");

        file.FileName.Should().Be("US_GENERAL.json");
        var text = Encoding.UTF8.GetString(file.Content);
        text.Should().Contain("\"template_code\": \"US_GENERAL\"").And.Contain("\"children\": [");
        var parsed = CoaFile.ParseJson(text, out var header);
        header!.CountryCode.Should().Be("USA");
        parsed.Rows.Should().HaveCount(110);
        parsed.Rows.Where(r => r.Level == 1).Should().HaveCount(5).And.OnlyContain(r => r.ParentCode == null);
        parsed.Rows.Single(r => r.Code == "1112").Should().Match<CoaRow>(r => r.ParentCode == "111" && r.DefaultAccountKey == DefaultKeys.Cash);
        CoaValidator.Validate(parsed.Rows).Should().BeEmpty();
    }

    [Fact]
    public async Task T_TPL_04_a_system_template_is_read_only()
    {
        using var h = new AccountingHarness();
        await h.SeedTemplatesAsync();
        var us = await h.TemplateUuidAsync("US_GENERAL");
        var act = () => h.Templates().DeleteAsync(h.Admin(), us);
        (await act.Should().ThrowAsync<ForbiddenException>()).Which.Message.Should().Be("System templates are read-only.");

        // An organization's own template can be removed; another organization's cannot be seen.
        h.Db.CoaTemplates.Add(new CoaTemplate { TemplateCode = "MINE", TemplateName = "Mine", OrganizationId = h.Org });
        await h.Db.SaveChangesAsync();
        var mine = (await h.Templates().ListAsync(h.Viewer(), null, null, "Mine")).Single();
        mine.IsSystem.Should().BeFalse();
        await ((Func<Task>)(() => h.Templates().DeleteAsync(h.Admin(Guid.NewGuid()), mine.Uuid))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => h.Templates().DeleteAsync(h.Viewer(), mine.Uuid))).Should().ThrowAsync<ForbiddenException>();
        await h.Templates().DeleteAsync(h.Admin(), mine.Uuid);
        (await h.Templates().ListAsync(h.Viewer(), null, null, "Mine")).Should().BeEmpty();
    }

    [Fact]
    public async Task T_TPL_05_preview_is_flat_depth_first_with_levels_and_root_types()
    {
        using var h = new AccountingHarness();
        await h.SeedTemplatesAsync();
        var uuid = await h.TemplateUuidAsync("US_GENERAL");
        var preview = await h.Templates().PreviewAsync(h.Viewer(), uuid);

        preview.Should().HaveCount(110);
        preview.Should().OnlyContain(i => i.Level >= 1 && i.Level <= 4 && RootTypes.IsValid(i.RootType) && i.IsGroup == (i.Level < 4));
        var seen = new HashSet<string>();
        foreach (var i in preview)
        {
            if (i.ParentCode is not null) seen.Should().Contain(i.ParentCode, "a parent comes before its children");
            seen.Add(i.AccountCode);
        }
        preview.Count(i => i.IsDefaultAccount).Should().Be(16);

        var detail = await h.Templates().GetAsync(h.Viewer(), uuid);
        detail.Accounts.Select(a => a.Code).Should().Equal("1", "2", "3", "4", "5");
        detail.Accounts[0].Children.Should().NotBeEmpty();
        await ((Func<Task>)(() => h.Templates().GetAsync(h.Viewer(), Guid.NewGuid()))).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public void Blank_templates_and_csv_escaping()
    {
        using var h = new AccountingHarness();
        var csv = h.Templates().Blank("csv");
        csv.FileName.Should().Be("coa-template.csv");
        Encoding.UTF8.GetString(csv.Content).TrimStart('﻿').Trim().Should().Be(ChartTestHelpers.CsvHeader);
        var json = h.Templates().Blank("json");
        json.FileName.Should().Be("coa-template.json");
        Encoding.UTF8.GetString(json.Content).Should().Contain("\"accounts\"");
        ((Action)(() => h.Templates().Blank("xlsx"))).Should().Throw<BadRequestException>();

        CoaFile.CsvField("Cash, petty").Should().Be("\"Cash, petty\"");
        CoaFile.CsvField("The \"main\" account").Should().Be("\"The \"\"main\"\" account\"");
        CoaFile.CsvField("two\nlines").Should().Be("\"two\nlines\"");
        CoaFile.CsvField("plain").Should().Be("plain");

        var rows = new List<CoaRow> { new() { Code = "1", Name = "Assets, all", Level = 1, RootType = "Asset", IsGroup = true, Description = "say \"hi\"\nthere" } };
        var back = CoaFile.ParseCsv(Encoding.UTF8.GetString(CoaFile.WriteCsv(rows)));
        back.Errors.Should().BeEmpty();
        back.Rows.Single().Should().Match<CoaRow>(r => r.Name == "Assets, all" && r.Description == "say \"hi\"\nthere" && r.Line == 2);
    }
}

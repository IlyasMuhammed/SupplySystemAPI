using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Accounting.Tests.ChartTestHelpers;

namespace SMS.Modules.Accounting.Tests;

/// <summary>A42 §5.5 R1–R13 — T-UPL-01..10 and "all errors at once".</summary>
public class UploadTests
{
    private static async Task<CoaUploadException> RefusedAsync(AccountingHarness h, string fileName, byte[] content)
    {
        var act = () => h.Chart().UploadAsync(h.Admin(), fileName, content);
        return (await act.Should().ThrowAsync<CoaUploadException>()).Which;
    }

    [Fact]
    public async Task T_UPL_01_a_valid_csv_creates_the_chart()
    {
        using var h = new AccountingHarness();
        var lines = ValidCsvLines(7); // 15 groups + 35 leaves = 50
        lines.Count.Should().Be(51);

        var result = await h.Chart().UploadAsync(h.Admin(), "legacy.csv", Bytes(lines));

        result.AccountsCreated.Should().Be(53, "50 from the file + 39 / 390 / 3900 (D-9)");
        result.DefaultsAssigned.Should().Be(1, "only DEFAULT_OPENING_BALANCE → 3900");
        result.Status.Should().Be(SetupStatuses.AccountsCreated);
        (await h.AccountAsync("3900")).Should().Match<ChartOfAccount>(a => a.IsSystemAccount && a.MaterializedPath == "/3/39/390/3900" && a.AccountType == AccountTypes.Equity);
        (await h.AccountAsync("11102")).MaterializedPath.Should().Be("/1/11/111/11102");
        (await h.Db.ChartOfAccounts.Where(a => a.Level == 1).ToListAsync()).Should().HaveCount(5).And.OnlyContain(a => a.IsSystemAccount);
        var status = await h.Db.CoaSetupStatuses.SingleAsync();
        status.TemplateId.Should().BeNull();
        h.Events.Of<CoaUploadedEvent>().Should().ContainSingle().Which.Should().Match<CoaUploadedEvent>(e =>
            e.FileName == "legacy.csv" && e.Format == "csv" && e.AccountCount == 53);
    }

    [Fact]
    public async Task T_UPL_02_missing_root()
    {
        using var h = new AccountingHarness();
        var lines = ValidCsvLines(1).Where(l => !l.Contains(",Equity,")).ToList();
        var ex = await RefusedAsync(h, "c.csv", Bytes(lines));
        ex.Errors.Should().ContainSingle(e => e.Rule == "R2").Which.Message.Should().Be(
            "Missing root type: Equity. All five root types (Asset, Liability, Equity, Revenue, Expense) are required.");
    }

    [Fact]
    public async Task T_UPL_03_duplicate_code_names_its_line()
    {
        using var h = new AccountingHarness();
        var lines = ValidCsvLines(2);
        lines.Add("11101,Again,111,4,Asset,false,Cash,");
        var ex = await RefusedAsync(h, "c.csv", Bytes(lines));
        ex.Errors.Should().ContainSingle().Which.Should().Match<SMS.Modules.Accounting.Models.UploadError>(e =>
            e.Rule == "R3" && e.Line == lines.Count && e.Code == "11101" && e.Message == $"Duplicate account code: 11101 at line {lines.Count}.");
    }

    [Fact]
    public async Task T_UPL_04_level_5()
    {
        using var h = new AccountingHarness();
        var lines = ValidCsvLines(1);
        lines.Add("111011,Too deep,11101,5,Asset,false,,");
        var ex = await RefusedAsync(h, "c.csv", Bytes(lines));
        ex.Errors.Should().Contain(e => e.Rule == "R4" && e.Message == "Invalid level 5 for account 111011. SMS supports levels 1 through 4.");
    }

    [Fact]
    public async Task T_UPL_05_orphan_parent()
    {
        using var h = new AccountingHarness();
        var lines = ValidCsvLines(1);
        lines.Add("1199,Orphan,119,4,Asset,false,Cash,");
        var ex = await RefusedAsync(h, "c.csv", Bytes(lines));
        ex.Errors.Should().ContainSingle().Which.Message.Should().Be("Account 1199 references parent 119 which is not found or not at level 3.");
    }

    [Fact]
    public async Task T_UPL_06_more_than_2000_accounts()
    {
        using var h = new AccountingHarness();
        var lines = ValidCsvLines(398); // 15 + 5 × 398 = 2005
        var ex = await RefusedAsync(h, "c.csv", Bytes(lines));
        ex.Errors.Should().ContainSingle().Which.Message.Should().Be("Upload contains 2005 accounts. Maximum allowed is 2000.");
    }

    [Fact]
    public async Task T_UPL_07_a_json_tree_with_default_markers()
    {
        using var h = new AccountingHarness();
        await h.SeedTemplatesAsync();
        var json = await h.Templates().DownloadAsync(h.Viewer(), await h.TemplateUuidAsync("PK_GENERAL"), "json");

        var result = await h.Chart().UploadAsync(h.Admin(), "pk.JSON", json.Content);

        result.AccountsCreated.Should().Be(110);
        result.DefaultsAssigned.Should().Be(16);
        var accounts = await h.Db.ChartOfAccounts.ToListAsync();
        var byId = accounts.ToDictionary(a => a.Id);
        accounts.Where(a => a.Level > 1).Should().OnlyContain(a =>
            byId[a.ParentAccountId!.Value].Level == a.Level - 1 && a.MaterializedPath == byId[a.ParentAccountId!.Value].MaterializedPath + "/" + a.AccountCode);
        (await h.Db.AccountingDefaults.CountAsync(d => d.AccountId != null)).Should().Be(16);
    }

    [Fact]
    public async Task T_UPL_08_malformed_json_and_other_r1_cases()
    {
        using var h = new AccountingHarness();
        (await RefusedAsync(h, "c.json", Encoding.UTF8.GetBytes("{ \"accounts\": [ { \"code\": "))).Errors
            .Should().ContainSingle().Which.Should().Match<SMS.Modules.Accounting.Models.UploadError>(e => e.Rule == "R1"
                && e.Message == "Invalid file format. Upload a CSV or JSON file conforming to the SMS CoA schema.");
        (await RefusedAsync(h, "c.xlsx", Bytes(ValidCsvLines(1)))).Errors.Should().ContainSingle(e => e.Rule == "R1");
        (await RefusedAsync(h, "c.csv", Bytes(["code,name", "1,Assets"]))).Errors.Should().ContainSingle(e => e.Rule == "R1" && e.Line == 1);
        (await RefusedAsync(h, "c.json", Encoding.UTF8.GetBytes("[1,2]"))).Errors.Should().ContainSingle(e => e.Rule == "R1");
        var tooBig = new byte[GlLimits.MaxUploadBytes + 1];
        await ((Func<Task>)(() => h.Chart().UploadAsync(h.Admin(), "c.csv", tooBig))).Should().ThrowAsync<BadRequestException>()
            .WithMessage("The file must be 5 MB or smaller.");
    }

    [Fact]
    public async Task T_UPL_09_level_4_marked_as_group()
    {
        using var h = new AccountingHarness();
        var lines = ValidCsvLines(1).Select(l => l.StartsWith("11101,") ? l.Replace(",false,", ",true,") : l).ToList();
        var ex = await RefusedAsync(h, "c.csv", Bytes(lines));
        ex.Errors.Should().Contain(e => e.Rule == "R7" && e.Message == "Account 11101 is Level 4 and must not be marked as a group.");
    }

    [Fact]
    public async Task T_UPL_10_after_journal_entries_are_posted()
    {
        using var h = new AccountingHarness();
        await h.ApplyAsync();
        h.Db.JournalEntries.Add(new JournalEntry { OrganizationId = h.Org, EntryNumber = "JE-2026-00009", EntryType = "GENERAL", EntryDate = new DateOnly(2026, 3, 1) });
        await h.Db.SaveChangesAsync();

        var ex = await RefusedAsync(h, "c.csv", Bytes(ValidCsvLines(1)));
        ex.Errors.Should().ContainSingle().Which.Should().Match<SMS.Modules.Accounting.Models.UploadError>(e => e.Rule == "R13"
            && e.Message == "Chart of Accounts cannot be replaced after journal entries have been posted. Add or modify individual accounts instead.");
        await ((Func<Task>)(() => h.Chart().ApplyTemplateAsync(h.Admin(), h.TemplateUuidAsync("US_GENERAL").GetAwaiter().GetResult())))
            .Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Every_problem_is_reported_at_once_with_its_line()
    {
        using var h = new AccountingHarness();
        var lines = new List<string>
        {
            CsvHeader,
            "1,Assets,,1,Asset,true,,",
            "2,Liabilities,,1,Liability,true,,",
            "3,Equity,,1,Equity,true,,",
            "4,Revenue,,1,Revenue,true,,",
            "6,Expenses,,1,Expense,true,,",                     // line 6: R6
            "11,Current,1,2,Asset,false,,",                     // line 7: R8
            "111,Cash,11,3,Asset,true,,",
            "1111,Petty \"Cash\",111,4,Asset,false,Cash,",       // line 9: R11? no — quote inside a field is data
            "11A-1,Bad code,111,4,Asset,false,Cash,",           // line 10: R11
            "1112,Bank,111,4,Liability,false,Bank,",            // line 11: R9 (root ancestor) + D-23 type
            "2111,Wrong digit,111,4,Asset,false,Cash,",         // line 12: R9 (code must start with 1)
            $"1113,{new string('x', 201)},111,4,Asset,false,Cash,", // line 13: R12
            "1114,Receivable,111,4,Asset,false,Payable,",       // line 14: D-23
            "1115,Cash,111,abc,Asset,false,Cash,",              // line 15: R4
        };

        var ex = await RefusedAsync(h, "c.csv", Bytes(lines));

        ex.Message.Should().Be($"The file has {ex.Errors.Count} problems. Nothing was imported.");
        ex.Errors.Select(e => (e.Line, e.Rule)).Should().Contain([
            (6, "R6"), (7, "R8"), (10, "R11"), (11, "R9"), (12, "R9"), (13, "R12"), (14, "R9"), (15, "R4")]);
        ex.Errors.Should().NotContain(e => e.Line == 9);
        ex.Errors.Single(e => e.Line == 14).Message.Should().Be("Account type Payable is not valid for Asset accounts.");
        ex.Errors.Single(e => e.Line == 12 && e.Rule == "R9").Message.Should().Be("Account 2111 must start with 1, the code of its root type (Asset).");
        ex.Errors.Where(e => e.Line == 11).Select(e => e.Message).Should().Contain("Account 1112 has root_type 'Liability' but its root ancestor is 'Asset'.");
        ex.Errors.Select(e => e.Line ?? int.MaxValue).Should().BeInAscendingOrder();
        (await h.Db.ChartOfAccounts.CountAsync()).Should().Be(0, "nothing was imported");
    }

    [Fact]
    public async Task Json_problems_carry_a_path_and_the_code()
    {
        using var h = new AccountingHarness();
        var json = """
            { "accounts": [
              { "code": "1", "name": "Assets", "root_type": "Asset", "level": 1, "is_group": true, "children": [
                { "code": "11", "name": "Current", "root_type": "Asset", "level": 2, "is_group": true, "children": [
                  { "code": "111", "name": "Cash", "root_type": "Asset", "level": 3, "is_group": true, "children": [
                    { "code": "1111", "name": "Petty", "root_type": "Asset", "level": 4, "is_group": true }
                  ] } ] } ] } ] }
            """;
        var ex = await RefusedAsync(h, "c.json", Encoding.UTF8.GetBytes(json));
        ex.Errors.Should().Contain(e => e.Rule == "R7" && e.Code == "1111" && e.Path == "accounts[0].children[0].children[0].children[0]" && e.Line == null);
        ex.Errors.Count(e => e.Rule == "R2").Should().Be(4);
    }

    [Fact]
    public async Task Replacing_is_refused_once_a_fiscal_year_exists_and_permission_is_needed()
    {
        using var h = new AccountingHarness();
        await ((Func<Task>)(() => h.Chart().UploadAsync(h.Viewer(), "c.csv", Bytes(ValidCsvLines(1))))).Should().ThrowAsync<ForbiddenException>();
        await h.Chart().UploadAsync(h.Admin(), "c.csv", Bytes(ValidCsvLines(1)));
        await h.AddFiscalYearAsync();

        var act = () => h.Chart().UploadAsync(h.Admin(), "c.csv", Bytes(ValidCsvLines(2)));
        (await act.Should().ThrowAsync<ConflictException>()).Which.Message.Should().Be(
            "The chart can only be replaced before the fiscal year is set and before opening balances are posted.");
    }
}

using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Accounting.Tests.ChartTestHelpers;

namespace SMS.Modules.Accounting.Tests;

/// <summary>A42 apply template (BR-TPL-004/005, D-9) and account CRUD / queries — T-COA-01..11.</summary>
public class ChartOfAccountsTests
{
    [Fact]
    public async Task T_COA_01_apply_us_general()
    {
        using var h = new AccountingHarness();
        await h.SeedTemplatesAsync();
        var uuid = await h.TemplateUuidAsync("US_GENERAL");

        var result = await h.Chart().ApplyTemplateAsync(h.Admin(), uuid);

        result.Should().Match<ApplyResultModel>(r => r.AccountsCreated == 110 && r.DefaultsAssigned == 16 && r.Status == SetupStatuses.AccountsCreated);
        var template = await h.Db.CoaTemplates.IgnoreQueryFilters().SingleAsync(t => t.Uuid == uuid);
        var accounts = await h.Db.ChartOfAccounts.ToListAsync();
        var byId = accounts.ToDictionary(a => a.Id);
        accounts.Should().HaveCount(110).And.OnlyContain(a => a.SourceTemplateId == template.Id && a.IsActive && a.OrganizationId == h.Org);
        foreach (var a in accounts)
        {
            if (a.Level == 1)
            {
                a.ParentAccountId.Should().BeNull();
                a.MaterializedPath.Should().Be("/" + a.AccountCode);
                a.IsSystemAccount.Should().BeTrue();
                continue;
            }
            var parent = byId[a.ParentAccountId!.Value];
            parent.Level.Should().Be((byte)(a.Level - 1));
            parent.RootType.Should().Be(a.RootType);
            a.MaterializedPath.Should().Be(parent.MaterializedPath + "/" + a.AccountCode);
            a.IsGroup.Should().Be(a.Level < 4);
            a.IsSystemAccount.Should().Be(a.AccountCode == "3900");
        }
        (await h.AccountAsync("1112")).MaterializedPath.Should().Be("/1/11/111/1112");
        var defaults = await h.Db.AccountingDefaults.Include(d => d.Account).ToListAsync();
        defaults.Single(d => d.DefaultKey == DefaultKeys.Cash).Account!.AccountCode.Should().Be("1112");
        defaults.Single(d => d.DefaultKey == DefaultKeys.OpeningBalance).Account!.AccountCode.Should().Be("3900");
        var status = await h.Db.CoaSetupStatuses.SingleAsync();
        status.Should().Match<CoaSetupStatus>(s => s.TemplateId == template.Id && s.TemplateAppliedAt != null && s.AccountsCreatedCount == 110);
        h.Events.Of<CoaTemplateAppliedEvent>().Should().ContainSingle().Which.AccountCount.Should().Be(110);
        (await h.Setup.GetAsync(h.Viewer())).TemplateName.Should().Be("United States — General Business");
    }

    [Fact]
    public async Task Applying_again_replaces_the_chart_and_is_refused_after_the_fiscal_year()
    {
        using var h = new AccountingHarness();
        await h.ApplyAsync("US_GENERAL");
        h.Db.OpeningBalanceEntries.Add(new OpeningBalanceEntry { OrganizationId = h.Org, AccountId = (await h.AccountAsync("1112")).Id, DebitAmount = 5 });
        await h.Db.SaveChangesAsync();

        var result = await h.Chart().ApplyTemplateAsync(h.Admin(), await h.TemplateUuidAsync("GLOBAL_GENERAL"));

        result.AccountsCreated.Should().Be(104);
        (await h.Db.ChartOfAccounts.CountAsync()).Should().Be(104, "BR-TPL-005: the old chart is gone");
        (await h.Db.OpeningBalanceEntries.CountAsync()).Should().Be(0, "draft opening balances of the old chart go with it");

        // No items yet: Apply is refused (D-5).
        await ((Func<Task>)(() => h.Chart().ApplyTemplateAsync(h.Admin(), h.TemplateUuidAsync("GB_RETAIL").GetAwaiter().GetResult())))
            .Should().ThrowAsync<ConflictException>().WithMessage("Accounts for this template are coming*");

        await h.AddFiscalYearAsync();
        var act = () => h.Chart().ApplyTemplateAsync(h.Admin(), h.TemplateUuidAsync("US_GENERAL").GetAwaiter().GetResult());
        (await act.Should().ThrowAsync<ConflictException>()).Which.Message.Should().Be(ChartOfAccountsService.ReplaceRefusal);
        await ((Func<Task>)(() => h.Chart().ApplyTemplateAsync(h.Viewer(), Guid.NewGuid()))).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task T_COA_02_a_level_4_account_under_a_level_3_parent()
    {
        using var h = new AccountingHarness();
        await h.ApplyAsync();
        var parent = await h.AccountAsync("111");
        (await h.Chart().SuggestCodeAsync(h.Viewer(), parent.Uuid)).Code.Should().Be("1116", "US_GENERAL has 1111..1115 under 111");

        var created = await h.Chart().CreateAsync(h.Admin(), new AccountCreateRequest
        {
            ParentUuid = parent.Uuid, AccountCode = "1119", AccountName = "Cash in Transit", AccountType = AccountTypes.Cash, Description = "Deposits on the way",
        });

        created.Should().Match<AccountDetailModel>(a => a.Level == 4 && !a.IsGroup && a.RootType == RootTypes.Asset && a.MaterializedPath == "/1/11/111/1119"
            && a.ParentCode == "111" && a.Parent!.Uuid == parent.Uuid && a.CanDelete && a.DeleteBlockedReason == null);
        h.Events.Of<ChartOfAccountCreatedEvent>().Should().ContainSingle(e => e.AccountCode == "1119" && e.Level == 4);
        (await h.Db.CoaSetupStatuses.SingleAsync()).AccountsCreatedCount.Should().Be(111);

        await ((Func<Task>)(() => h.Chart().CreateAsync(h.Admin(), new AccountCreateRequest { ParentUuid = parent.Uuid, AccountCode = "1119", AccountName = "Dup" })))
            .Should().ThrowAsync<ConflictException>().WithMessage("Account code 1119 already exists.");
        await ((Func<Task>)(() => h.Chart().CreateAsync(h.Viewer(), new AccountCreateRequest { ParentUuid = parent.Uuid, AccountCode = "1118", AccountName = "X" })))
            .Should().ThrowAsync<ForbiddenException>();
        await ((Func<Task>)(() => h.Chart().CreateAsync(h.Admin(), new AccountCreateRequest { ParentUuid = parent.Uuid, AccountCode = "1118", AccountName = "X", AccountType = AccountTypes.Payable })))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Account type Payable is not valid for Asset accounts.");
        await ((Func<Task>)(() => h.Chart().CreateAsync(h.Admin(), new AccountCreateRequest { ParentUuid = parent.Uuid, AccountCode = "11-8", AccountName = "X" })))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Account code '11-8' contains invalid characters. Use digits only.");
    }

    [Fact]
    public async Task T_COA_03_and_04_parent_level_and_root_rules()
    {
        using var h = new AccountingHarness();
        await h.ApplyAsync();
        // The level follows the parent (one above, BR-COA-009): a level-1 parent gives a level-2 group.
        var group = await h.Chart().CreateAsync(h.Admin(), new AccountCreateRequest { ParentUuid = (await h.AccountAsync("1")).Uuid, AccountCode = "19", AccountName = "Other" });
        group.Should().Match<AccountDetailModel>(a => a.Level == 2 && a.IsGroup);
        await ((Func<Task>)(() => h.Chart().CreateAsync(h.Admin(), new AccountCreateRequest { ParentUuid = group.Uuid, AccountCode = "191", AccountName = "G", AccountType = AccountTypes.Cash })))
            .Should().ThrowAsync<BadRequestException>().WithMessage("A group account cannot have an account type.");
        // Nothing goes under a leaf.
        var leaf = (await h.AccountAsync("1112")).Uuid;
        await ((Func<Task>)(() => h.Chart().CreateAsync(h.Admin(), new AccountCreateRequest { ParentUuid = leaf, AccountCode = "11121", AccountName = "X" })))
            .Should().ThrowAsync<BadRequestException>();
        // T-COA-04: the root type comes from the parent; a code of another root is refused (D-8).
        var cash = (await h.AccountAsync("111")).Uuid;
        await ((Func<Task>)(() => h.Chart().CreateAsync(h.Admin(), new AccountCreateRequest { ParentUuid = cash, AccountCode = "2119", AccountName = "X" })))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Account code must start with 1, the code of Asset accounts.");
    }

    [Fact]
    public async Task T_COA_05_to_08_delete_rules()
    {
        using var h = new AccountingHarness();
        await h.ApplyAsync();
        var free = await h.Chart().CreateAsync(h.Admin(), new AccountCreateRequest { ParentUuid = (await h.AccountAsync("111")).Uuid, AccountCode = "1119", AccountName = "Spare" });

        // T-COA-05
        await h.Chart().DeleteAsync(h.Admin(), free.Uuid);
        (await h.Chart().GetAsync(h.Viewer(), free.Uuid)).Should().Match<AccountDetailModel>(a => !a.IsActive && !a.CanDelete);
        h.Events.Of<ChartOfAccountDeletedEvent>().Should().ContainSingle(e => e.AccountCode == "1119");
        // The code is free again (filtered unique index).
        await h.Chart().CreateAsync(h.Admin(), new AccountCreateRequest { ParentUuid = (await h.AccountAsync("111")).Uuid, AccountCode = "1119", AccountName = "Spare 2" });

        // T-COA-06
        var used = await h.AccountAsync("1111");
        used.HasTransactions = true;
        await h.Db.SaveChangesAsync();
        await Refused(h, used.Uuid, "Cannot delete an account with posted transactions.");
        // T-COA-07
        await Refused(h, (await h.AccountAsync("111")).Uuid, "Cannot delete an account that has child accounts.");
        // T-COA-08
        await Refused(h, (await h.AccountAsync("3900")).Uuid, "Cannot delete a system account.");
        await Refused(h, (await h.AccountAsync("1")).Uuid, "Cannot delete a system account.");
        // D-24 / D-30 — an assigned default.
        await Refused(h, (await h.AccountAsync("1112")).Uuid, "This account is assigned as Cash Account. Assign another account first.");
        // A mapping or an opening balance.
        var mapped = await h.AccountAsync("1114");
        h.Db.ExternalSystemMappings.Add(new ExternalSystemMapping { OrganizationId = h.Org, AccountId = mapped.Id, SystemType = ExternalSystemTypes.Xero, SystemName = "Xero UK", ExternalAccountCode = "090" });
        var opening = await h.AccountAsync("1119");
        h.Db.OpeningBalanceEntries.Add(new OpeningBalanceEntry { OrganizationId = h.Org, AccountId = opening.Id, DebitAmount = 10 });
        await h.Db.SaveChangesAsync();
        await Refused(h, mapped.Uuid, "This account is mapped to Xero UK. Remove the mapping first.");
        await Refused(h, opening.Uuid, "This account has an opening balance. Remove it first.");

        (await h.Chart().GetAsync(h.Viewer(), used.Uuid)).DeleteBlockedReason.Should().Be("Cannot delete an account with posted transactions.");
    }

    private static async Task Refused(AccountingHarness h, Guid uuid, string message) =>
        await ((Func<Task>)(() => h.Chart().DeleteAsync(h.Admin(), uuid))).Should().ThrowAsync<ConflictException>().WithMessage(message);

    [Fact]
    public async Task Update_rules_and_events()
    {
        using var h = new AccountingHarness();
        await h.ApplyAsync();
        var acct = await h.AccountAsync("1114");

        var updated = await h.Chart().UpdateAsync(h.Admin(), acct.Uuid, new AccountUpdateRequest
        {
            AccountName = "Checking — Payroll (old bank)", AccountType = AccountTypes.Bank, Description = acct.Description, ExternalCode = "QB-35",
        });
        updated.Should().Match<AccountDetailModel>(a => a.AccountName == "Checking — Payroll (old bank)" && a.ExternalCode == "QB-35" && a.AccountCode == "1114");
        h.Events.Of<ChartOfAccountUpdatedEvent>().Single().ChangedFields.Should().Equal("accountName", "externalCode");

        var obe = await h.AccountAsync("3900");
        await ((Func<Task>)(() => h.Chart().UpdateAsync(h.Admin(), obe.Uuid, new AccountUpdateRequest { AccountName = "Renamed", AccountType = AccountTypes.Equity })))
            .Should().ThrowAsync<ConflictException>().WithMessage("Opening Balance Equity (3900) cannot be renamed.");
        await ((Func<Task>)(() => h.Chart().UpdateAsync(h.Admin(), acct.Uuid, new AccountUpdateRequest { AccountName = "X", AccountType = AccountTypes.Revenue })))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Account type Revenue is not valid for Asset accounts.");

        // Roots can be renamed before completion, not after.
        var root = await h.AccountAsync("1");
        await h.Chart().UpdateAsync(h.Admin(), root.Uuid, new AccountUpdateRequest { AccountName = "Assets (all)" });
        var status = await h.Db.CoaSetupStatuses.SingleAsync();
        status.Status = SetupStatuses.Complete;
        await h.Db.SaveChangesAsync();
        await ((Func<Task>)(() => h.Chart().UpdateAsync(h.Admin(), root.Uuid, new AccountUpdateRequest { AccountName = "Assets" })))
            .Should().ThrowAsync<ConflictException>().WithMessage("Root accounts are locked once the setup is complete.");
        // After completion, an assigned required default still cannot be deleted (D-30).
        await Refused(h, (await h.AccountAsync("1112")).Uuid, "This account is assigned as Cash Account. Assign another account first.");
    }

    [Fact]
    public async Task T_COA_09_export_round_trips()
    {
        using var h = new AccountingHarness();
        await h.ApplyAsync();
        var csv = await h.Chart().ExportAsync(h.Viewer(), "csv");
        csv.FileName.Should().Be("chart-of-accounts.csv");
        var parsed = CoaFile.ParseCsv(Encoding.UTF8.GetString(csv.Content));
        parsed.Rows.Should().HaveCount(110);
        CoaValidator.Validate(parsed.Rows).Should().BeEmpty();
        parsed.Rows.Single(r => r.Code == "1112").ParentCode.Should().Be("111");

        var json = await h.Chart().ExportAsync(h.Viewer(), "json");
        json.FileName.Should().Be("chart-of-accounts.json");
        var tree = CoaFile.ParseJson(Encoding.UTF8.GetString(json.Content), out _);
        tree.Rows.Should().HaveCount(110);
        tree.Rows.Single(r => r.Code == "1112").DefaultAccountKey.Should().Be(DefaultKeys.Cash);

        // Re-uploading the JSON export rebuilds the same chart with its defaults.
        var result = await h.Chart().UploadAsync(h.Admin(), "chart-of-accounts.json", json.Content);
        result.Should().Match<ApplyResultModel>(r => r.AccountsCreated == 110 && r.DefaultsAssigned == 16);
    }

    [Fact]
    public async Task T_COA_10_subtree_by_materialized_path_and_ancestors()
    {
        using var h = new AccountingHarness();
        await h.ApplyAsync();
        var current = await h.AccountAsync("11");
        var subtree = await h.Chart().GetSubtreeAsync(h.Viewer(), current.Uuid);
        subtree.Should().NotBeEmpty().And.OnlyContain(a => a.MaterializedPath.StartsWith("/1/11/"));
        subtree.Select(a => a.AccountCode).Should().Contain(["111", "1111", "1112"]).And.NotContain(["11", "12", "1"]);
        subtree.Count.Should().Be(await h.Db.ChartOfAccounts.CountAsync(a => a.MaterializedPath.StartsWith("/1/11/")));

        var ancestors = await h.Chart().GetAncestorsAsync(h.Viewer(), (await h.AccountAsync("1112")).Uuid);
        ancestors.Select(a => a.AccountCode).Should().Equal("1", "11", "111");
    }

    [Fact]
    public async Task T_COA_11_one_organizations_chart_is_invisible_to_another()
    {
        using var h = new AccountingHarness();
        await h.ApplyAsync();
        var mine = await h.AccountAsync("1112");
        var other = Guid.NewGuid();

        (await h.Chart().GetTreeAsync(h.Viewer(other))).Roots.Should().BeEmpty();
        (await h.Chart().GetFlatAsync(h.Viewer(other), new AccountFlatQuery())).Should().BeEmpty();
        await ((Func<Task>)(() => h.Chart().GetAsync(h.Viewer(other), mine.Uuid))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => h.Chart().DeleteAsync(h.Admin(other), mine.Uuid))).Should().ThrowAsync<NotFoundException>();
        var myGroup = (await h.AccountAsync("111")).Uuid;
        await ((Func<Task>)(() => h.Chart().CreateAsync(h.Admin(other), new AccountCreateRequest { ParentUuid = myGroup, AccountCode = "1119", AccountName = "X" })))
            .Should().ThrowAsync<NotFoundException>();

        // The other organization applies its own chart: same codes, no clash, still separate.
        await h.Chart().ApplyTemplateAsync(h.Admin(other), await h.TemplateUuidAsync("PK_GENERAL"));
        (await h.Chart().GetTreeAsync(h.Viewer(other))).Summary.TotalAccounts.Should().Be(110);
        (await h.Db.ChartOfAccounts.IgnoreQueryFilters().CountAsync(a => a.OrganizationId == h.Org)).Should().Be(110);
    }

    [Fact]
    public async Task Start_blank_creates_only_the_system_accounts_and_replaces_like_apply()
    {
        using var h = new AccountingHarness();
        await h.ApplyAsync(); // an existing chart is replaced

        var result = await h.Chart().StartBlankAsync(h.Admin());

        result.Should().Match<ApplyResultModel>(r => r.AccountsCreated == 8 && r.DefaultsAssigned == 1 && r.Status == SetupStatuses.AccountsCreated);
        var accounts = (await h.Db.ChartOfAccounts.ToListAsync()).OrderBy(a => a.MaterializedPath, StringComparer.Ordinal).ToList();
        accounts.Select(a => a.MaterializedPath).Should().Equal("/1", "/2", "/3", "/3/39", "/3/39/390", "/3/39/390/3900", "/4", "/5");
        accounts.Where(a => a.Level == 1).Select(a => a.AccountName).Should().Equal("Assets", "Liabilities", "Equity", "Revenue", "Expenses");
        accounts.Single(a => a.AccountCode == "3900").Should().Match<ChartOfAccount>(a => a.IsSystemAccount && a.AccountType == AccountTypes.Equity && !a.IsGroup);
        accounts.Where(a => a.Level == 1).Should().OnlyContain(a => a.IsSystemAccount && a.SourceTemplateId == null);
        var status = await h.Db.CoaSetupStatuses.SingleAsync();
        status.Should().Match<CoaSetupStatus>(s => s.TemplateId == null && s.TemplateAppliedAt == null && s.AccountsCreatedCount == 8);
        (await h.Db.AccountingDefaults.Include(d => d.Account).SingleAsync(d => d.AccountId != null)).Account!.AccountCode.Should().Be("3900");
        (await h.Setup.HistoryAsync(h.Viewer())).Should().Contain(x => x.Summary == "Blank chart of accounts started: 8 system accounts created.");
        h.Events.Of<CoaUploadedEvent>().Should().ContainSingle(e => e.Format == "BLANK");

        // The user builds levels 2–4 in the tree from here.
        var assets = accounts.Single(a => a.AccountCode == "1");
        var l2 = await h.Chart().CreateAsync(h.Admin(), new AccountCreateRequest { ParentUuid = assets.Uuid, AccountCode = "11", AccountName = "Current Assets" });
        var l3 = await h.Chart().CreateAsync(h.Admin(), new AccountCreateRequest { ParentUuid = l2.Uuid, AccountCode = "111", AccountName = "Cash" });
        (await h.Chart().CreateAsync(h.Admin(), new AccountCreateRequest { ParentUuid = l3.Uuid, AccountCode = "1111", AccountName = "Petty Cash", AccountType = AccountTypes.Cash }))
            .MaterializedPath.Should().Be("/1/11/111/1111");

        // Same replace rules: permission needed; refused once a fiscal year exists.
        await ((Func<Task>)(() => h.Chart().StartBlankAsync(h.Viewer()))).Should().ThrowAsync<ForbiddenException>();
        await h.AddFiscalYearAsync();
        (await ((Func<Task>)(() => h.Chart().StartBlankAsync(h.Admin()))).Should().ThrowAsync<ConflictException>())
            .Which.Message.Should().Be(ChartOfAccountsService.ReplaceRefusal);
    }

    [Fact]
    public async Task Tree_and_flat_carry_the_delete_refusal_of_every_account()
    {
        using var h = new AccountingHarness();
        await h.ApplyAsync();
        (await h.AccountAsync("1111")).HasTransactions = true;
        var mapped = await h.AccountAsync("1114");
        h.Db.ExternalSystemMappings.Add(new ExternalSystemMapping { OrganizationId = h.Org, AccountId = mapped.Id, SystemType = ExternalSystemTypes.Xero, SystemName = "Xero UK", ExternalAccountCode = "090" });
        h.Db.OpeningBalanceEntries.Add(new OpeningBalanceEntry { OrganizationId = h.Org, AccountId = (await h.AccountAsync("1115")).Id, DebitAmount = 3 });
        await h.Db.SaveChangesAsync();

        var tree = await h.Chart().GetTreeAsync(h.Viewer());
        var flat = (await h.Chart().GetFlatAsync(h.Viewer(), new AccountFlatQuery())).ToDictionary(a => a.AccountCode);
        var nodes = new Dictionary<string, AccountTreeNodeModel>();
        void Walk(IEnumerable<AccountTreeNodeModel> list) { foreach (var n in list) { nodes[n.AccountCode] = n; Walk(n.Children); } }
        Walk(tree.Roots);

        var expected = new Dictionary<string, string>
        {
            ["1"] = "Cannot delete a system account.",
            ["3900"] = "Cannot delete a system account.",
            ["111"] = "Cannot delete an account that has child accounts.",
            ["1111"] = "Cannot delete an account with posted transactions.",
            ["1112"] = "This account is assigned as Cash Account. Assign another account first.",
            ["1114"] = "This account is mapped to Xero UK. Remove the mapping first.",
            ["1115"] = "This account has an opening balance. Remove it first.",
        };
        foreach (var (code, reason) in expected)
        {
            nodes[code].DeleteBlockedReason.Should().Be(reason, code);
            nodes[code].CanDelete.Should().BeFalse(code);
            flat[code].DeleteBlockedReason.Should().Be(reason, code);
            flat[code].CanDelete.Should().BeFalse(code);
        }
        var free = nodes.Values.Where(n => n.CanDelete).ToList();
        free.Should().NotBeEmpty().And.OnlyContain(n => n.DeleteBlockedReason == null && !n.IsSystemAccount && n.Level == 4);
        // Same answer as the detail endpoint.
        foreach (var n in nodes.Values)
            (await h.Chart().GetAsync(h.Viewer(), n.Uuid)).DeleteBlockedReason.Should().Be(n.DeleteBlockedReason, n.AccountCode);
    }

    [Fact]
    public async Task Tree_flat_and_summary()
    {
        using var h = new AccountingHarness();
        await h.ApplyAsync();
        var tree = await h.Chart().GetTreeAsync(h.Viewer());
        tree.Roots.Select(r => r.AccountCode).Should().Equal("1", "2", "3", "4", "5");
        tree.Summary.TotalAccounts.Should().Be(110);
        tree.Summary.PostableAccounts.Should().Be(await h.Db.ChartOfAccounts.CountAsync(a => !a.IsGroup));
        tree.Summary.LastModifiedAt.Should().NotBeNull();
        tree.Roots[0].Children.Should().OnlyContain(c => c.ParentCode == "1" && c.Level == 2);

        var leaves = await h.Chart().GetFlatAsync(h.Viewer(), new AccountFlatQuery { IsGroup = false, RootType = RootTypes.Asset, AccountType = AccountTypes.Bank });
        leaves.Should().NotBeEmpty().And.OnlyContain(a => a.AccountType == AccountTypes.Bank && a.Level == 4 && a.ParentUuid != null);
        (await h.Chart().GetFlatAsync(h.Viewer(), new AccountFlatQuery { Search = "Petty" })).Select(a => a.AccountCode).Should().Equal("1111");
        (await h.Chart().GetFlatAsync(h.Viewer(), new AccountFlatQuery { Level = 1 })).Should().HaveCount(5);
    }
}

using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Accounting.Tests;

/// <summary>A42 §12 / D-21 — T-EXT-01..05, CSV import/export, credentials guard, isolation.</summary>
public class ExternalMappingTests
{
    private static CreateExternalMappingRequest Qbo(ChartOfAccount account, string code = "1000") => new()
    {
        SystemType = ExternalSystemTypes.QuickBooksOnline, AccountUuid = account.Uuid, ExternalAccountCode = code, ExternalAccountName = "Petty Cash",
        SyncDirection = "outbound",
    };

    private static MemoryStream Csv(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task T_EXT_01_a_quickbooks_online_mapping_is_created()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);

        var mapping = await h.Mappings().CreateAsync(h.Admin(), Qbo(chart["1111"]));

        mapping.Should().Match<ExternalMappingModel>(m => m.SystemType == ExternalSystemTypes.QuickBooksOnline && m.SystemName == "QuickBooks Online"
            && m.Account.AccountCode == "1111" && m.ExternalAccountCode == "1000" && m.SyncDirection == SyncDirections.Outbound && m.IsActive);
        h.Events.Of<ExternalMappingCreatedEvent>().Should().ContainSingle().Which.Should().Match<ExternalMappingCreatedEvent>(e =>
            e.MappingId == mapping.Uuid && e.AccountCode == "1111" && e.ExternalCode == "1000");
        (await h.Setup.HistoryAsync(h.Viewer())).Should().Contain(x => x.Summary == "Account 1111 mapped to 1000 in QuickBooks Online.");
        (await h.Mappings().ListAsync(h.Viewer(), "quickbooks_online")).Should().ContainSingle();
        (await h.Mappings().ListAsync(h.Viewer(), ExternalSystemTypes.Xero)).Should().BeEmpty();
    }

    [Fact]
    public async Task T_EXT_02_the_same_account_maps_once_per_system()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        await h.Mappings().CreateAsync(h.Admin(), Qbo(chart["1111"]));

        await ((Func<Task>)(() => h.Mappings().CreateAsync(h.Admin(), Qbo(chart["1111"], "1001")))).Should().ThrowAsync<ConflictException>()
            .WithMessage("This account is already mapped for QuickBooks Online.");

        var xero = Qbo(chart["1111"], "090");
        xero.SystemType = ExternalSystemTypes.Xero;
        (await h.Mappings().CreateAsync(h.Admin(), xero)).SystemName.Should().Be("Xero");
    }

    [Fact]
    public async Task T_EXT_03_twenty_mappings_import_from_csv_and_a_second_import_updates_them()
    {
        using var h = new AccountingHarness();
        var leaves = Enumerable.Range(1, 20).Select(i => ($"1{200 + i}", RootTypes.Asset, AccountTypes.OtherCurrentAsset)).ToArray();
        await h.SeedChartAsync(null, leaves);
        var csv = new StringBuilder("sms_account_code,external_code,external_name,direction\r\n");
        for (var i = 1; i <= 20; i++) csv.Append($"1{200 + i},EXT-{i},\"Account, number {i}\",Bidirectional\r\n");

        var first = await h.Mappings().ImportAsync(h.Admin(), Csv(csv.ToString()), ExternalSystemTypes.Xero, null);

        first.Created.Should().Be(20);
        first.Updated.Should().Be(0);
        first.Errors.Should().BeEmpty();
        (await h.Db.ExternalSystemMappings.CountAsync()).Should().Be(20);
        (await h.Db.ExternalSystemMappings.SingleAsync(m => m.ExternalAccountCode == "EXT-3")).ExternalAccountName.Should().Be("Account, number 3");
        h.Events.Of<ExternalMappingCreatedEvent>().Should().HaveCount(20);

        var second = await h.Mappings().ImportAsync(h.Admin(), Csv("sms_account_code,external_code\n1201,NEW-1\n"), "xero", "Xero (HQ)");
        second.Created.Should().Be(0);
        second.Updated.Should().Be(1);
        h.NewScope();
        (await h.Db.ExternalSystemMappings.SingleAsync(m => m.ExternalAccountCode == "NEW-1"))
            .Should().Match<ExternalSystemMapping>(m => m.SyncDirection == SyncDirections.None && m.SystemName == "Xero (HQ)");
    }

    [Fact]
    public async Task Import_reports_line_errors_and_applies_the_good_lines()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        chart["1113"].IsActive = false;
        await h.Db.SaveChangesAsync();
        const string text = "sms_account_code,external_code,external_name,direction\n" +
                            "1111,1000,Petty,Outbound\n" +   // line 2 ok
                            "111,1100,Group,None\n" +        // line 3 group
                            "9999,X,,\n" +                   // line 4 unknown
                            "1112,,Bank,None\n" +            // line 5 no external code
                            "2111,2000,AP,Sideways\n" +      // line 6 bad direction
                            "1113,1200,AR,None\n" +          // line 7 inactive
                            "\n" +                           // blank line ignored
                            "1111,1001,Again,None\n";        // line 9 duplicate in file

        var result = await h.Mappings().ImportAsync(h.Admin(), Csv(text), ExternalSystemTypes.Tally, null);

        result.Created.Should().Be(1);
        result.Errors.Select(e => e.Line).Should().Equal(3, 4, 5, 6, 7, 9);
        result.Errors[0].Message.Should().Be("Only Level 4 accounts can be mapped.");
        result.Errors[1].Message.Should().Be("Account 9999 does not exist.");
        result.Errors[4].Message.Should().Be("Mapped account must be active.");

        await ((Func<Task>)(() => h.Mappings().ImportAsync(h.Admin(), Csv("code,name\n1,2\n"), ExternalSystemTypes.Tally, null)))
            .Should().ThrowAsync<BadRequestException>();
        await ((Func<Task>)(() => h.Mappings().ImportAsync(h.Admin(), Csv(text), "NETSUITE", null))).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task T_EXT_04_05_only_active_leaf_accounts_can_be_mapped()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        chart["1112"].IsActive = false;
        await h.Db.SaveChangesAsync();

        await ((Func<Task>)(() => h.Mappings().CreateAsync(h.Admin(), Qbo(chart["111"])))).Should().ThrowAsync<BadRequestException>()
            .WithMessage("Only Level 4 accounts can be mapped.");
        await ((Func<Task>)(() => h.Mappings().CreateAsync(h.Admin(), Qbo(chart["1112"])))).Should().ThrowAsync<BadRequestException>()
            .WithMessage("Mapped account must be active.");
    }

    [Theory]
    [InlineData("{\"apiKey\":\"abc\"}")]
    [InlineData("{\"auth\":{\"client_secret\":\"abc\"}}")]
    [InlineData("{\"accessToken\":\"abc\"}")]
    [InlineData("[{\"Password\":\"x\"}]")]
    [InlineData("{\"private-key\":\"x\"}")]
    public async Task Connection_settings_never_carry_credentials(string json)
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        var request = Qbo(chart["1111"]);
        request.ConnectionConfigJson = json;

        await ((Func<Task>)(() => h.Mappings().CreateAsync(h.Admin(), request))).Should().ThrowAsync<BadRequestException>()
            .WithMessage("Connection settings must not contain credentials.");
    }

    [Fact]
    public async Task Connection_settings_must_be_json_and_may_hold_references()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        var request = Qbo(chart["1111"]);
        request.ConnectionConfigJson = "{not json";
        await ((Func<Task>)(() => h.Mappings().CreateAsync(h.Admin(), request))).Should().ThrowAsync<BadRequestException>()
            .WithMessage("Connection settings must be valid JSON.");

        request.ConnectionConfigJson = "{\"realmId\":\"123\",\"vaultRef\":\"qbo/acme\"}";
        (await h.Mappings().CreateAsync(h.Admin(), request)).ConnectionConfigJson.Should().Contain("realmId");
    }

    [Fact]
    public async Task Update_delete_and_export()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        var m = await h.Mappings().CreateAsync(h.Admin(), Qbo(chart["1111"]));
        await h.Mappings().CreateAsync(h.Admin(), Qbo(chart["2111"], "=2000"));

        var updated = await h.Mappings().UpdateAsync(h.Admin(), m.Uuid, new UpdateExternalMappingRequest
            { ExternalAccountCode = "1005", ExternalAccountName = "Cash on hand", SyncDirection = "Inbound", IsActive = false });
        updated.Should().Match<ExternalMappingModel>(x => x.ExternalAccountCode == "1005" && x.SyncDirection == SyncDirections.Inbound && !x.IsActive);
        await ((Func<Task>)(() => h.Mappings().UpdateAsync(h.Admin(), m.Uuid, new UpdateExternalMappingRequest { ExternalAccountCode = "" })))
            .Should().ThrowAsync<BadRequestException>();

        var csv = Encoding.UTF8.GetString(await h.Mappings().ExportAsync(h.Viewer(), null)).TrimStart('﻿');
        csv.Should().StartWith("sms_account_code,external_code,external_name,direction,system_type,system_name,is_active");
        csv.Should().Contain("1111,1005,Cash on hand,Inbound,QUICKBOOKS_ONLINE,QuickBooks Online,false");
        csv.Should().Contain("2111,'=2000,", "formula-like values are neutralised in the export");

        // The export imports back (round trip).
        h.NewScope();
        var back = await h.Mappings().ImportAsync(h.Admin(), Csv(csv), ExternalSystemTypes.QuickBooksOnline, null);
        back.Updated.Should().Be(2);
        back.Errors.Should().BeEmpty();
        csv.Should().Contain("\r\n", "CRLF on every host (D-58)");
        (await h.Mappings().ListAsync(h.Viewer(), ExternalSystemTypes.QuickBooksOnline)).Single(x => x.Account.AccountCode == "2111")
            .ExternalAccountCode.Should().Be("=2000", "QA-42: the export's formula guard is removed on import, so the round trip is lossless");

        await h.Mappings().DeleteAsync(h.Admin(), m.Uuid);
        (await h.Mappings().ListAsync(h.Viewer(), null)).Should().ContainSingle();
        await ((Func<Task>)(() => h.Mappings().DeleteAsync(h.Admin(), m.Uuid))).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Mappings_are_per_organization_and_need_setup_manage()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        var m = await h.Mappings().CreateAsync(h.Admin(), Qbo(chart["1111"]));
        var other = Guid.NewGuid();
        var theirChart = await h.SeedChartAsync(other, ("1111", RootTypes.Asset, AccountTypes.Cash));

        (await h.Mappings().ListAsync(h.Viewer(other), null)).Should().BeEmpty();
        await ((Func<Task>)(() => h.Mappings().UpdateAsync(h.Admin(other), m.Uuid, new UpdateExternalMappingRequest { ExternalAccountCode = "x" })))
            .Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => h.Mappings().DeleteAsync(h.Admin(other), m.Uuid))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => h.Mappings().CreateAsync(h.Admin(), Qbo(theirChart["1111"], "z")))).Should().ThrowAsync<NotFoundException>();
        (await h.Mappings().CreateAsync(h.Admin(other), Qbo(theirChart["1111"]))).Account.AccountCode.Should().Be("1111", "the same system + code is free in another organization");

        await ((Func<Task>)(() => h.Mappings().CreateAsync(h.Viewer(), Qbo(chart["2111"])))).Should().ThrowAsync<ForbiddenException>();
        await ((Func<Task>)(() => h.Mappings().ListAsync(h.Nobody(), null))).Should().ThrowAsync<ForbiddenException>();
    }
}

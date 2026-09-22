using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Lookups.Data;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Repositories;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Lookups.Tests;

/// <summary>
/// A29-P7-09 — the company's own bank details, kept on the organization's one document template so a
/// sales invoice can say where to pay. Free text, because banks do not agree on which fields exist.
/// </summary>
public class PoDocumentTemplateBankDetailsTests
{
    private const int User = 7;

    private static (LookupsDbContext Db, PoDocumentTemplateService Service, Guid Org, string DbName) New(Guid? org = null, string? dbName = null)
    {
        org    ??= Guid.NewGuid();
        dbName ??= Guid.NewGuid().ToString();
        var db = new LookupsDbContext(
            new DbContextOptionsBuilder<LookupsDbContext>().UseInMemoryDatabase(dbName).Options,
            new StaticTenantContext { OrganizationId = org.Value });
        return (db, new PoDocumentTemplateService(new PoDocumentTemplateRepository(db)), org.Value, dbName);
    }

    private static UpsertPoDocumentTemplateRequest Request(string? bank) => new()
    {
        CompanyName = "Sunrise Electricals", CompanyAddress = "Plot 12, Karachi", FooterText = "Thank you", BankDetails = bank
    };

    [Fact]
    public async Task Bank_details_are_saved_and_come_back_with_the_template()
    {
        var (_, service, _, _) = New();
        const string details = "Habib Bank Ltd, Korangi Branch\nAccount title: Sunrise Electricals\nIBAN: PK36HABB0000123456702568";

        await service.UpsertAsync(Request(details), User);

        var template = await service.GetActiveAsync();
        template!.BankDetails.Should().Be(details, "the line breaks are part of it");
        template.CompanyName.Should().Be("Sunrise Electricals");
    }

    [Fact]
    public async Task Surrounding_whitespace_is_trimmed_and_blank_means_none()
    {
        var (_, service, _, _) = New();

        await service.UpsertAsync(Request("  HBL 0123456789  \n"), User);
        (await service.GetActiveAsync())!.BankDetails.Should().Be("HBL 0123456789");

        foreach (var blank in new[] { "", "   ", "\r\n\t" })
        {
            await service.UpsertAsync(Request(blank), User);
            (await service.GetActiveAsync())!.BankDetails.Should().BeNull($"'{blank.Replace("\r", "\\r").Replace("\n", "\\n")}' clears it");
        }
    }

    [Fact]
    public async Task Saving_again_replaces_the_details_and_leaves_a_single_template()
    {
        var (db, service, _, _) = New();

        await service.UpsertAsync(Request("Old bank"), User);
        await service.UpsertAsync(Request("New bank"), User);

        (await service.GetActiveAsync())!.BankDetails.Should().Be("New bank");
        (await db.PoDocumentTemplates.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_limit_is_a_thousand_characters_and_beyond_it_nothing_is_saved()
    {
        var (db, service, _, _) = New();

        await service.UpsertAsync(Request(new string('b', 1000)), User);
        (await service.GetActiveAsync())!.BankDetails!.Length.Should().Be(1000);

        var act = async () => await service.UpsertAsync(Request(new string('b', 1001)), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*1000 characters*");
        (await service.GetActiveAsync())!.BankDetails!.Length.Should().Be(1000, "the refused save changed nothing");
        (await db.PoDocumentTemplates.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_organization_that_has_never_set_a_template_has_no_bank_details_rather_than_someone_elses()
    {
        var a = New();
        await a.Service.UpsertAsync(Request("Org A's account"), User);
        var b = New(Guid.NewGuid(), a.DbName);

        (await b.Service.GetActiveAsync())!.BankDetails.Should().BeNull();
        (await a.Service.GetActiveAsync())!.BankDetails.Should().Be("Org A's account");
    }

    [Fact]
    public async Task Each_organization_keeps_its_own_details()
    {
        var a = New();
        var b = New(Guid.NewGuid(), a.DbName);

        await a.Service.UpsertAsync(Request("Org A's account"), User);
        await b.Service.UpsertAsync(Request("Org B's account"), User);

        (await a.Service.GetActiveAsync())!.BankDetails.Should().Be("Org A's account");
        (await b.Service.GetActiveAsync())!.BankDetails.Should().Be("Org B's account");
    }

    [Fact]
    public async Task With_no_template_at_all_the_defaults_carry_no_bank_details()
    {
        var (_, service, _, _) = New();

        var template = await service.GetActiveAsync();

        template!.BankDetails.Should().BeNull();
        template.FooterText.Should().NotBeNullOrEmpty("the other defaults are unchanged");
    }

    [Fact]
    public void The_request_and_the_model_both_carry_the_field()
    {
        typeof(UpsertPoDocumentTemplateRequest).GetProperty(nameof(UpsertPoDocumentTemplateRequest.BankDetails)).Should().NotBeNull();
        typeof(PoDocumentTemplateModel).GetProperty(nameof(PoDocumentTemplateModel.BankDetails)).Should().NotBeNull();
    }
}

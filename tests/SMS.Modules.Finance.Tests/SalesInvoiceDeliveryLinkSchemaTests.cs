using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P7-04 §9.5 — the link from an invoice to the delivery it bills, and the index that makes "one
/// live invoice per delivery" a database fact rather than only a service habit. Whether the filtered
/// index really refuses a second invoice is proved against a scratch LocalDB (the in-memory provider
/// does not enforce unique indexes); everything checkable from the model and migration is here.
/// </summary>
public class SalesInvoiceDeliveryLinkSchemaTests
{
    private static FinanceDbContext SqlServerContext() =>
        new(new DbContextOptionsBuilder<FinanceDbContext>()
                .UseSqlServer("Server=none;Database=none;Trusted_Connection=True;").Options,
            new StaticTenantContext());

    private static IEntityType Invoice()
    {
        using var db = SqlServerContext();
        return db.Model.FindEntityType(typeof(SalesInvoice))!;
    }

    private static Migration LinkMigration()
    {
        using var db = SqlServerContext();
        var assembly = db.GetService<IMigrationsAssembly>();
        var entry = assembly.Migrations.Single(m => m.Key.EndsWith("_AddSalesInvoiceDeliveryLink", StringComparison.Ordinal));
        return assembly.CreateMigration(entry.Value, "Microsoft.EntityFrameworkCore.SqlServer");
    }

    [Fact]
    public void The_delivery_is_optional_on_an_invoice_because_not_every_invoice_comes_from_one()
    {
        var invoice = Invoice();

        invoice.FindProperty(nameof(SalesInvoice.DeliveryUuid))!.IsNullable.Should().BeTrue();
        invoice.FindProperty(nameof(SalesInvoice.DeliveryNumber))!.IsNullable.Should().BeTrue();
        invoice.FindProperty(nameof(SalesInvoice.DeliveryNumber))!.GetMaxLength().Should().Be(25);
    }

    [Fact]
    public void A_delivery_has_at_most_one_live_invoice_per_organization()
    {
        var index = Invoice().GetIndexes().Single(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(["OrganizationId", "DeliveryUuid"]));

        index.IsUnique.Should().BeTrue();

        var filter = index.GetFilter();
        filter.Should().Contain("[DeliveryUuid] IS NOT NULL", "invoices with no delivery must not collide on NULL");
        filter.Should().Contain("[IsDelete] = 0", "a deleted invoice frees its delivery");
        filter.Should().Contain("[Status] <> 'CANCELLED'", "a cancelled invoice frees its delivery");
    }

    [Fact]
    public void The_filter_names_the_same_status_the_service_treats_as_cancelled()
    {
        // The literal in the index and the constant in the code must never drift apart.
        SalesInvoiceStatuses.Cancelled.Should().Be("CANCELLED");
    }

    [Fact]
    public void The_migration_adds_the_two_nullable_columns_and_the_filtered_unique_index()
    {
        var up = LinkMigration().UpOperations;

        var columns = up.OfType<AddColumnOperation>().ToList();
        columns.Should().HaveCount(2);
        columns.Should().OnlyContain(c => c.Table == "sales_invoices" && c.Schema == "finance" && c.IsNullable);
        columns.Single(c => c.Name == "DeliveryNumber").MaxLength.Should().Be(25);
        columns.Single(c => c.Name == "DeliveryUuid").ClrType.Should().Be(typeof(Guid));

        var index = up.OfType<CreateIndexOperation>().Should().ContainSingle().Subject;
        index.Name.Should().Be("IX_sales_invoices_OrganizationId_DeliveryUuid");
        index.IsUnique.Should().BeTrue();
        index.Columns.Should().Equal("OrganizationId", "DeliveryUuid");
        index.Filter.Should().Be("[DeliveryUuid] IS NOT NULL AND [IsDelete] = 0 AND [Status] <> 'CANCELLED'");

        up.Should().HaveCount(3, "nothing else rides along in this migration");
    }

    [Fact]
    public void Existing_invoices_survive_the_migration_because_the_new_columns_default_to_null()
    {
        LinkMigration().UpOperations.OfType<AddColumnOperation>()
            .Should().OnlyContain(c => c.IsNullable && c.DefaultValue == null && c.DefaultValueSql == null);
    }

    [Fact]
    public void Rolling_it_back_drops_the_index_before_the_column_it_covers_and_nothing_more()
    {
        var down = LinkMigration().DownOperations;

        down.Should().HaveCount(3);
        down[0].Should().BeOfType<DropIndexOperation>()
             .Which.Name.Should().Be("IX_sales_invoices_OrganizationId_DeliveryUuid");
        down.Skip(1).Should().AllBeOfType<DropColumnOperation>();
        down.OfType<DropColumnOperation>().Select(o => o.Name).Should().BeEquivalentTo("DeliveryUuid", "DeliveryNumber");
    }

    [Fact]
    public void It_comes_after_the_tables_it_alters()
    {
        using var db = SqlServerContext();
        var names = db.GetService<IMigrationsAssembly>().Migrations.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        var link = names.FindIndex(n => n.EndsWith("_AddSalesInvoiceDeliveryLink", StringComparison.Ordinal));
        link.Should().BeGreaterThan(names.FindIndex(n => n.EndsWith("_CreateSalesInvoiceTables", StringComparison.Ordinal)));
        link.Should().BeGreaterThan(names.FindIndex(n => n.EndsWith("_CreateCustomerLedger", StringComparison.Ordinal)));
    }
}

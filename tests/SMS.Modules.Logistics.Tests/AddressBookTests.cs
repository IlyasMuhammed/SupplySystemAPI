using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Logistics.Controllers;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

/// <summary>
/// A29-P9-07 §7.7 — the addresses a customer can be shipped to. A sale order names its shipping address by id,
/// and until this existed no address could be made except by typing one onto a delivery.
/// </summary>
public class AddressBookTests
{
    private static readonly Guid Acme   = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Globex = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private sealed class Book
    {
        internal readonly Guid Org = Guid.NewGuid();
        internal readonly string DbName;
        internal DateTime Now = new(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);

        internal Book() => DbName = Guid.NewGuid().ToString();

        internal AddressBookService For(Guid? org = null) =>
            new(LogisticsTestDb.OpenAs(DbName, org ?? Org), new AddressNormalizer(new FakeCityLookup()), new Clock(this));

        private sealed class Clock(Book book) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => new(book.Now);
        }
    }

    private static AddressRequest Request(
        Guid? customer = null, string line1 = "Plot 12, Korangi Industrial Area", string? line2 = null, string city = "Karachi",
        string? postal = "74900", string country = "Pakistan", string? iso = "PK", string? phone = null, string? type = null) =>
        new()
        {
            ConsigneeUuid = customer ?? Acme, Line1 = line1, Line2 = line2, CityName = city, PostalCode = postal,
            CountryName = country, CountryIsoCode = iso, ContactPhone = phone, AddressType = type
        };

    // ── Saving ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_address_is_saved_for_the_customer_as_a_customer_address_and_can_be_read_back()
    {
        var book = new Book();

        var saved = await book.For().CreateAsync(Request(line1: "  Plot 12  ", city: " Karachi "), createdBy: 7);

        (saved.Line1, saved.CityName, saved.CountryName, saved.AddressType, saved.ValidationStatus)
            .Should().Be(("Plot 12", "Karachi", "Pakistan", "CUSTOMER", "VALID"));
        var read = await book.For().GetAsync(saved.UUID);
        read.Should().NotBeNull();
        read!.Line1.Should().Be("Plot 12");

        await using var db = LogisticsTestDb.OpenAs(book.DbName, book.Org);
        var stored = await db.Addresses.SingleAsync();
        (stored.ConsigneeUuid, stored.CreatedBy, stored.CreatedDate, stored.IsActive, stored.IsDelete).Should().Be(((Guid?)Acme, 7, book.Now, true, false));
    }

    [Fact]
    public async Task An_address_needs_a_customer()
    {
        var book = new Book();

        foreach (var customer in new Guid?[] { null, Guid.Empty })
        {
            var request = Request();
            request.ConsigneeUuid = customer;

            var act = () => book.For().CreateAsync(request, 1);
            (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("Name the customer");
        }

        await using var db = LogisticsTestDb.OpenAs(book.DbName, book.Org);
        (await db.Addresses.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("", "Karachi", "Pakistan", "Address line 1")]
    [InlineData("Plot 12", "", "Pakistan", "City")]
    [InlineData("Plot 12", "Karachi", "", "Country")]
    public async Task A_structurally_meaningless_address_is_refused_by_field_name_and_nothing_is_saved(string line1, string city, string country, string field)
    {
        var book = new Book();

        var act = () => book.For().CreateAsync(Request(line1: line1, city: city, country: country), 1);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Be($"{field} is required.");
        await using var db = LogisticsTestDb.OpenAs(book.DbName, book.Org);
        (await db.Addresses.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_phone_that_cannot_be_read_saves_the_address_as_unvalidated_with_the_reason_instead_of_refusing_it()
    {
        var saved = await new Book().For().CreateAsync(Request(phone: "call reception"), 1);

        saved.ValidationStatus.Should().Be("UNVALIDATED");
        saved.ValidationNotes.Should().NotBeNullOrWhiteSpace();
        saved.ContactPhone.Should().Be("call reception");
    }

    [Fact]
    public async Task An_address_type_may_be_named_when_it_is_one_the_book_knows_and_is_refused_when_it_is_not()
    {
        var book = new Book();

        (await book.For().CreateAsync(Request(type: "project_site"), 1)).AddressType.Should().Be("PROJECT_SITE");

        var act = () => book.For().CreateAsync(Request(type: "MOON_BASE"), 1);
        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("'MOON_BASE'").And.Contain("CUSTOMER");
    }

    // ── Reading one ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_address_that_is_not_there_or_is_deleted_or_is_another_organizations_is_not_found()
    {
        var book  = new Book();
        var mine  = await book.For().CreateAsync(Request(), 1);
        var gone  = await book.For().CreateAsync(Request(line1: "Deleted Road"), 1);
        await using (var db = LogisticsTestDb.OpenAs(book.DbName, book.Org))
        {
            (await db.Addresses.SingleAsync(a => a.UUID == gone.UUID)).IsDelete = true;
            await db.SaveChangesAsync();
        }

        (await book.For().GetAsync(mine.UUID)).Should().NotBeNull();
        (await book.For().GetAsync(Guid.NewGuid())).Should().BeNull();
        (await book.For().GetAsync(gone.UUID)).Should().BeNull();
        (await book.For(Guid.NewGuid()).GetAsync(mine.UUID)).Should().BeNull("it belongs to another organization");
    }

    // ── The customer's book ──────────────────────────────────────────────────

    [Fact]
    public async Task A_customers_book_lists_only_their_addresses_newest_first()
    {
        var book = new Book();
        await book.For().CreateAsync(Request(line1: "First Street"), 1);
        book.Now = book.Now.AddHours(1);
        await book.For().CreateAsync(Request(line1: "Second Street"), 1);
        book.Now = book.Now.AddHours(1);
        await book.For().CreateAsync(Request(customer: Globex, line1: "Globex Street"), 1);
        book.Now = book.Now.AddHours(1);
        await book.For().CreateAsync(Request(line1: "Third Street"), 1);

        var listed = await book.For().ListForConsigneeAsync(Acme);

        listed.Select(a => a.Line1).Should().Equal("Third Street", "Second Street", "First Street");
        (await book.For().ListForConsigneeAsync(Globex)).Select(a => a.Line1).Should().Equal("Globex Street");
        (await book.For().ListForConsigneeAsync(Guid.NewGuid())).Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_place_saved_twice_is_listed_once_at_its_newest_whatever_the_case_or_spacing()
    {
        var book = new Book();
        var older = await book.For().CreateAsync(Request(line1: "Plot 12, Korangi", city: "Karachi"), 1);
        book.Now = book.Now.AddDays(1);
        var newer = await book.For().CreateAsync(Request(line1: "  PLOT 12, KORANGI ", city: "karachi"), 1);
        book.Now = book.Now.AddDays(1);
        await book.For().CreateAsync(Request(line1: "Plot 12, Korangi", city: "Karachi", postal: "75000"), 1);   // a different postcode is a different place

        var listed = await book.For().ListForConsigneeAsync(Acme);

        listed.Should().HaveCount(2);
        listed.Select(a => a.UUID).Should().Contain(newer.UUID).And.NotContain(older.UUID);
    }

    [Fact]
    public async Task A_deleted_or_deactivated_address_is_not_in_the_book()
    {
        var book = new Book();
        var kept    = await book.For().CreateAsync(Request(line1: "Kept Road"), 1);
        var deleted = await book.For().CreateAsync(Request(line1: "Deleted Road"), 1);
        var retired = await book.For().CreateAsync(Request(line1: "Retired Road"), 1);
        await using (var db = LogisticsTestDb.OpenAs(book.DbName, book.Org))
        {
            (await db.Addresses.SingleAsync(a => a.UUID == deleted.UUID)).IsDelete = true;
            (await db.Addresses.SingleAsync(a => a.UUID == retired.UUID)).IsActive = false;
            await db.SaveChangesAsync();
        }

        (await book.For().ListForConsigneeAsync(Acme)).Select(a => a.UUID).Should().Equal(kept.UUID);
    }

    [Fact]
    public async Task Another_organizations_addresses_for_the_same_customer_id_are_never_listed()
    {
        var book = new Book();
        var other = Guid.NewGuid();
        await book.For().CreateAsync(Request(line1: "Mine"), 1);
        await book.For(other).CreateAsync(Request(line1: "Theirs"), 1);

        (await book.For().ListForConsigneeAsync(Acme)).Select(a => a.Line1).Should().Equal("Mine");
        (await book.For(other).ListForConsigneeAsync(Acme)).Select(a => a.Line1).Should().Equal("Theirs");
    }

    [Fact]
    public async Task One_list_never_carries_more_than_fifty_places()
    {
        var book = new Book();
        foreach (var i in Enumerable.Range(1, 60))
        {
            book.Now = book.Now.AddMinutes(1);
            await book.For().CreateAsync(Request(line1: $"Street {i:00}"), 1);
        }

        var listed = await book.For().ListForConsigneeAsync(Acme);

        listed.Should().HaveCount(50);
        listed.First().Line1.Should().Be("Street 60");
        listed.Last().Line1.Should().Be("Street 11");
    }

    // ── The endpoints ────────────────────────────────────────────────────────

    private static readonly Type Controller = typeof(AddressesController);

    private static string[] Policies(string action) =>
        [.. Controller.GetMethod(action)!.GetCustomAttributes<RequirePermissionAttribute>().Select(a => a.Policy!)];

    [Fact]
    public void The_endpoints_are_where_the_order_form_looks_for_them()
    {
        Controller.GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/addresses");
        Controller.GetCustomAttribute<RequiresFeatureAttribute>()!.FeatureCode.Should().Be("MODULE_LOGISTICS");
        Controller.GetMethod(nameof(AddressesController.Create))!.GetCustomAttribute<HttpPostAttribute>().Should().NotBeNull();
        Controller.GetMethod(nameof(AddressesController.GetById))!.GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("{uuid:guid}");
        Controller.GetMethod(nameof(AddressesController.List))!.GetCustomAttribute<HttpGetAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void Reading_an_address_needs_the_sale_order_view_permission_and_saving_one_the_create_permission()
    {
        Policies(nameof(AddressesController.GetById)).Should().Equal("Permission:SALE_ORDER_VIEW");
        Policies(nameof(AddressesController.List)).Should().Equal("Permission:SALE_ORDER_VIEW");
        Policies(nameof(AddressesController.Create)).Should().Equal("Permission:SALE_ORDER_CREATE");
    }

    [Fact]
    public async Task Listing_without_a_customer_is_a_bad_request_and_asks_nothing_of_the_service()
    {
        var svc = new Mock<IAddressBookService>();

        var result = await new AddressesController(svc.Object).List(Guid.Empty);

        result.Should().BeOfType<BadRequestObjectResult>();
        svc.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_address_is_a_404_and_a_known_one_is_wrapped_in_the_usual_envelope()
    {
        var known = new AddressModel { UUID = Guid.NewGuid(), Line1 = "Plot 12" };
        var svc = new Mock<IAddressBookService>();
        svc.Setup(s => s.GetAsync(It.IsAny<Guid>())).ReturnsAsync((AddressModel?)null);
        svc.Setup(s => s.GetAsync(known.UUID)).ReturnsAsync(known);
        var controller = new AddressesController(svc.Object);

        (await controller.GetById(Guid.NewGuid())).Should().BeOfType<NotFoundObjectResult>();
        var ok = (OkObjectResult)await controller.GetById(known.UUID);
        ((ApiResponse<AddressModel>)ok.Value!).Result.Should().BeSameAs(known);
    }
}

using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SMS.Modules.Tenancy.Controllers;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Repositories;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

/// <summary>
/// A35 P2-02..04 / P2-10 (T-C3-01..04) / P1-14 settings part — tenant.organization_currency_settings: the D-7 read
/// fallback, GetBaseCurrencyIdAsync(org, domain), the settings PUT with BR-C3-02 (active org currencies) and BR-C3-03
/// (409 when a domain base is used by locked documents, via every ICurrencyUsageChecker), provisioning and the
/// Organization.BaseCurrency mirror.
/// </summary>
public class OrganizationCurrencySettingsTests
{
    private static readonly Guid Pkr = Guid.NewGuid();
    private static readonly Guid Usd = Guid.NewGuid();
    private static readonly Guid Eur = Guid.NewGuid();
    private static readonly Guid Aed = Guid.NewGuid();
    private static readonly Guid Inactive = Guid.NewGuid();

    private readonly TenancyDbContext _db =
        new(new DbContextOptionsBuilder<TenancyDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private readonly FakeOrgCurrencies _orgCurrencies = new();
    private readonly FakeUsage _usage = new();

    private static readonly Dictionary<Guid, string> Codes = new()
    {
        [Pkr] = "PKR", [Usd] = "USD", [Eur] = "EUR", [Aed] = "AED", [Inactive] = "JPY"
    };

    private async Task<Guid> SeedOrg(Guid? baseCurrency, bool configureCurrencies = true)
    {
        var org = new Organization
        {
            Id = Guid.NewGuid(), OrgCode = "O" + Guid.NewGuid().ToString("N")[..8], OrgName = "Acme", Plan = "BASIC",
            BaseCurrency = baseCurrency, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        _db.Organizations.Add(org);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        if (configureCurrencies)
        {
            foreach (var id in new[] { Pkr, Usd, Eur, Aed }) _orgCurrencies.Add(org.Id, id, Codes[id], active: true);
            _orgCurrencies.Add(org.Id, Inactive, "JPY", active: false);
        }
        return org.Id;
    }

    private OrganizationCurrencyService Resolver(bool withLookup = true) =>
        new(_db, withLookup ? _orgCurrencies : null);

    private OrganizationCurrencySettingsService Settings(bool withLookup = true)
    {
        var codes = new Mock<ICurrencyCodeLookup>();
        codes.Setup(c => c.GetCodeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => Codes.TryGetValue(id, out var c) ? c : null);
        return new OrganizationCurrencySettingsService(_db, new ICurrencyUsageChecker[] { _usage },
            withLookup ? _orgCurrencies : null, codes.Object);
    }

    private static UpdateOrgCurrencySettingsRequest All(Guid id) =>
        new() { SaleBaseCurrencyId = id, PurchaseBaseCurrencyId = id, ServiceBaseCurrencyId = id };

    private async Task<OrganizationCurrencySettings?> Row(Guid org)
    {
        _db.ChangeTracker.Clear();
        return await _db.OrganizationCurrencySettings.SingleOrDefaultAsync(s => s.OrganizationId == org);
    }

    // ── D-7 read fallback + resolver (T-C4-02's org half) ───────────────────

    [Fact]
    public async Task A_missing_row_reads_as_everything_equal_to_the_organizations_base_currency()
    {
        var org = await SeedOrg(Usd);

        var s = await ((IOrganizationCurrencyService)Resolver()).GetSettingsAsync(org);

        s.IsStored.Should().BeFalse();
        new[] { s.SaleBaseCurrencyId, s.PurchaseBaseCurrencyId, s.ServiceBaseCurrencyId, s.RateCurrencyId }.Should().OnlyContain(id => id == Usd);
        (await Resolver().GetBaseCurrencyIdAsync(org)).Should().Be(Usd);
        (await ((IOrganizationCurrencyService)Resolver()).GetBaseCurrencyIdAsync(org, TransactionDomain.Purchase)).Should().Be(Usd);
    }

    [Fact]
    public async Task Without_a_row_or_a_base_currency_the_new_reads_use_PKR_but_the_legacy_read_stays_null()
    {
        var org = await SeedOrg(null);

        (await ((IOrganizationCurrencyService)Resolver()).GetBaseCurrencyIdAsync(org, TransactionDomain.Sale)).Should().Be(Pkr);
        (await ((IOrganizationCurrencyService)Resolver()).GetSettingsAsync(org)).RateCurrencyId.Should().Be(Pkr);
        (await Resolver().GetBaseCurrencyIdAsync(org)).Should().BeNull(
            "QuickBooks preflight and SO pricing read null as 'no base currency configured'");
    }

    [Fact]
    public async Task Clearing_the_profile_base_currency_is_refused_once_settings_are_stored()
    {
        var org = await SeedOrg(Pkr);
        await Settings().UpdateAsync(org, All(Pkr), 1);

        var act = () => ProfileService().UpdateOrganizationAsync(org, new UpdateOrganizationRequest { OrgName = "Acme", ClearBaseCurrency = true }, 2);

        await act.Should().ThrowAsync<BadRequestException>();
        (await _db.Organizations.SingleAsync(o => o.Id == org)).BaseCurrency.Should().Be(Pkr);
    }

    [Fact]
    public async Task When_nothing_resolves_the_old_overload_still_answers_null_and_the_snapshot_holds_empty_ids()
    {
        var org = await SeedOrg(null, configureCurrencies: false);

        (await Resolver(withLookup: false).GetBaseCurrencyIdAsync(org)).Should().BeNull();
        (await ((IOrganizationCurrencyService)Resolver(withLookup: false)).GetSettingsAsync(org)).SaleBaseCurrencyId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task A_stored_row_answers_per_domain_and_the_old_overload_is_the_sale_base()
    {
        var org = await SeedOrg(Pkr);
        await Settings().UpdateAsync(org, new UpdateOrgCurrencySettingsRequest
        {
            SaleBaseCurrencyId = Eur, PurchaseBaseCurrencyId = Usd, ServiceBaseCurrencyId = Aed
        }, userId: 3);

        IOrganizationCurrencyService r = Resolver();
        (await r.GetBaseCurrencyIdAsync(org)).Should().Be(Eur);
        (await r.GetBaseCurrencyIdAsync(org, TransactionDomain.Sale)).Should().Be(Eur);
        (await r.GetBaseCurrencyIdAsync(org, TransactionDomain.Purchase)).Should().Be(Usd);
        (await r.GetBaseCurrencyIdAsync(org, TransactionDomain.Service)).Should().Be(Aed);
        var snap = await r.GetSettingsAsync(org);
        snap.IsStored.Should().BeTrue();
        snap.RateCurrencyId.Should().Be(Pkr, "the rate currency was not part of the edit");
    }

    [Fact]
    public async Task Another_organizations_row_is_never_read()
    {
        var mine = await SeedOrg(Pkr);
        var other = await SeedOrg(Pkr);
        await Settings().UpdateAsync(other, All(Usd), 1);

        (await Resolver().GetBaseCurrencyIdAsync(mine)).Should().Be(Pkr);
        (await Row(mine)).Should().BeNull("an update of another organization never writes this one's row");
    }

    // ── T-C3-01..04 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task T_C3_01_all_three_bases_the_same_currency_is_saved_and_mirrored_into_the_organization()
    {
        var org = await SeedOrg(null);

        var model = await Settings().UpdateAsync(org, All(Pkr), userId: 7);

        model.IsStored.Should().BeTrue();
        model.SaleBaseCurrencyCode.Should().Be("PKR");
        var row = (await Row(org))!;
        new[] { row.SaleBaseCurrencyId, row.PurchaseBaseCurrencyId, row.ServiceBaseCurrencyId, row.RateCurrencyId }.Should().OnlyContain(id => id == Pkr);
        row.ModifiedBy.Should().Be(7);
        (await _db.Organizations.SingleAsync(o => o.Id == org)).BaseCurrency.Should().Be(Pkr);
    }

    [Fact]
    public async Task T_C3_02_purchase_base_USD_is_saved_and_only_purchase_changes()
    {
        var org = await SeedOrg(Pkr);

        await Settings().UpdateAsync(org, new UpdateOrgCurrencySettingsRequest
        {
            SaleBaseCurrencyId = Pkr, PurchaseBaseCurrencyId = Usd, ServiceBaseCurrencyId = Pkr
        }, 1);

        IOrganizationCurrencyService r = Resolver();
        (await r.GetBaseCurrencyIdAsync(org, TransactionDomain.Purchase)).Should().Be(Usd);
        (await r.GetBaseCurrencyIdAsync(org, TransactionDomain.Sale)).Should().Be(Pkr);
        (await _db.Organizations.SingleAsync(o => o.Id == org)).BaseCurrency.Should().Be(Pkr);
    }

    [Fact]
    public async Task T_C3_03_changing_the_sale_base_with_a_confirmed_sale_order_is_409_and_nothing_changes()
    {
        var org = await SeedOrg(Pkr);
        await Settings().UpdateAsync(org, All(Pkr), 1);
        _usage.Domain[(org, TransactionDomain.Sale)] = "1 confirmed sale order";

        var act = () => Settings().UpdateAsync(org, new UpdateOrgCurrencySettingsRequest
        {
            SaleBaseCurrencyId = Eur, PurchaseBaseCurrencyId = Pkr, ServiceBaseCurrencyId = Pkr, ExchangeGainAccountCode = "7110"
        }, 1);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("Cannot change the sale base currency — 1 confirmed sale order.");
        var row = (await Row(org))!;
        row.SaleBaseCurrencyId.Should().Be(Pkr);
        row.ExchangeGainAccountCode.Should().BeNull();
        (await _db.Organizations.SingleAsync(o => o.Id == org)).BaseCurrency.Should().Be(Pkr);
    }

    [Fact]
    public async Task T_C3_04_changing_the_sale_base_with_no_transactions_is_saved()
    {
        var org = await SeedOrg(Pkr);
        await Settings().UpdateAsync(org, All(Pkr), 1);

        await Settings().UpdateAsync(org, new UpdateOrgCurrencySettingsRequest
        {
            SaleBaseCurrencyId = Eur, PurchaseBaseCurrencyId = Pkr, ServiceBaseCurrencyId = Pkr
        }, 1);

        (await Row(org))!.SaleBaseCurrencyId.Should().Be(Eur);
        (await _db.Organizations.SingleAsync(o => o.Id == org)).BaseCurrency.Should().Be(Eur);
    }

    [Fact]
    public async Task Usage_in_one_domain_does_not_block_another_and_an_unchanged_used_base_does_not_block_the_save()
    {
        var org = await SeedOrg(Pkr);
        _usage.Domain[(org, TransactionDomain.Sale)] = "3 issued sales invoices";

        await Settings().UpdateAsync(org, new UpdateOrgCurrencySettingsRequest
        {
            SaleBaseCurrencyId = Pkr, PurchaseBaseCurrencyId = Usd, ServiceBaseCurrencyId = Pkr, ExchangeLossAccountCode = " 7120 "
        }, 1);

        var row = (await Row(org))!;
        row.PurchaseBaseCurrencyId.Should().Be(Usd);
        row.ExchangeLossAccountCode.Should().Be("7120");
    }

    [Fact]
    public async Task The_purchase_base_is_locked_by_purchase_usage()
    {
        var org = await SeedOrg(Pkr);
        _usage.Domain[(org, TransactionDomain.Purchase)] = "2 approved purchase orders";

        var act = () => Settings().UpdateAsync(org, new UpdateOrgCurrencySettingsRequest
        {
            SaleBaseCurrencyId = Pkr, PurchaseBaseCurrencyId = Usd, ServiceBaseCurrencyId = Pkr
        }, 1);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("Cannot change the purchase base currency — 2 approved purchase orders.");
        (await Row(org)).Should().BeNull("a refused first save creates no row");
    }

    // ── BR-C3-02 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_base_must_be_an_active_currency_of_this_organization()
    {
        var org = await SeedOrg(Pkr);
        var other = await SeedOrg(Pkr, configureCurrencies: false);
        var foreign = Guid.NewGuid();
        _orgCurrencies.Add(other, foreign, "MXN", active: true); Codes[foreign] = "MXN";

        foreach (var (bad, code) in new[] { (Inactive, "JPY"), (foreign, "MXN"), (Guid.NewGuid(), (string?)null) })
        {
            var act = () => Settings().UpdateAsync(org, new UpdateOrgCurrencySettingsRequest
            {
                SaleBaseCurrencyId = Pkr, PurchaseBaseCurrencyId = bad, ServiceBaseCurrencyId = Pkr
            }, 1);
            var ex = await act.Should().ThrowAsync<BadRequestException>();
            ex.WithMessage("* is not an active currency of this organization.");
            if (code is not null) ex.WithMessage($"{code} *");
        }

        (await Row(org)).Should().BeNull();
    }

    [Fact]
    public async Task An_empty_id_is_refused()
    {
        var org = await SeedOrg(Pkr);

        var act = () => Settings().UpdateAsync(org, All(Guid.Empty), 1);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Account_codes_are_trimmed_blank_is_null_and_more_than_20_characters_is_refused()
    {
        var org = await SeedOrg(Pkr);
        var req = All(Pkr);
        req.ExchangeGainAccountCode = "  ";
        req.UnrealizedGainAccountCode = "7130";
        await Settings().UpdateAsync(org, req, 1);
        var row = (await Row(org))!;
        row.ExchangeGainAccountCode.Should().BeNull();
        row.UnrealizedGainAccountCode.Should().Be("7130");

        req.UnrealizedLossAccountCode = new string('9', 21);
        var act = () => Settings().UpdateAsync(org, req, 1);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("Account codes can be at most 20 characters");
    }

    // ── Rate currency (D-2) ─────────────────────────────────────────────────

    [Fact]
    public async Task The_rate_currency_is_read_only_any_change_is_409_even_without_rates()
    {
        // REV-07 / D-2 — Finance's SYSTEM 1.0 row was seeded for this currency; the settings API must not drift from it.
        var org = await SeedOrg(Pkr);
        await Settings().UpdateAsync(org, All(Pkr), 1);

        var req = All(Pkr);
        req.RateCurrencyId = Usd;
        var act = () => Settings().UpdateAsync(org, req, 1);
        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("The rate currency can't be changed.");
        (await Row(org))!.RateCurrencyId.Should().Be(Pkr);

        req.RateCurrencyId = Pkr;   // sending back the stored value is not a change
        await Settings().UpdateAsync(org, req, 1);
        req.RateCurrencyId = null;  // nor is leaving it out
        await Settings().UpdateAsync(org, req, 1);
    }

    // ── REV-06: an organization without a row ───────────────────────────────

    [Fact]
    public async Task Setting_the_profile_base_of_an_org_without_a_row_moves_only_the_sale_base()
    {
        var org = await SeedOrg(null);   // no row; every domain reads as PKR
        _usage.Domain[(org, TransactionDomain.Purchase)] = "2 approved purchase orders";

        await ProfileService().UpdateOrganizationAsync(org, new UpdateOrganizationRequest { OrgName = "Acme", BaseCurrency = Usd }, 2);

        var row = (await Row(org))!;
        row.SaleBaseCurrencyId.Should().Be(Usd);
        row.PurchaseBaseCurrencyId.Should().Be(Pkr, "approved POs were locked at PKR — the purchase base must not follow");
        row.ServiceBaseCurrencyId.Should().Be(Pkr);
        row.RateCurrencyId.Should().Be(Pkr, "the rate currency CUR seeded (base ?? PKR at seeding)");
        IOrganizationCurrencyService r = Resolver();
        (await r.GetBaseCurrencyIdAsync(org, TransactionDomain.Purchase)).Should().Be(Pkr);
        (await r.GetBaseCurrencyIdAsync(org)).Should().Be(Usd);
    }

    [Fact]
    public async Task Setting_the_profile_base_of_an_org_without_a_row_is_409_when_sales_are_locked()
    {
        var org = await SeedOrg(null);
        _usage.Domain[(org, TransactionDomain.Sale)] = "1 confirmed sale order";

        var act = () => ProfileService().UpdateOrganizationAsync(org, new UpdateOrganizationRequest { OrgName = "Acme", BaseCurrency = Usd }, 2);

        await act.Should().ThrowAsync<ConflictException>();
        (await Row(org)).Should().BeNull();
    }

    [Fact]
    public async Task Clearing_the_profile_base_without_a_row_is_409_when_any_domain_is_in_use_and_allowed_otherwise()
    {
        var used = await SeedOrg(Usd, configureCurrencies: true);
        _db.OrganizationCurrencySettings.RemoveRange(_db.OrganizationCurrencySettings);
        await _db.SaveChangesAsync();
        _usage.Domain[(used, TransactionDomain.Service)] = "1 service document";

        var act = () => ProfileService().UpdateOrganizationAsync(used, new UpdateOrganizationRequest { OrgName = "Acme", ClearBaseCurrency = true }, 2);
        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*service base*");
        (await _db.Organizations.SingleAsync(o => o.Id == used)).BaseCurrency.Should().Be(Usd);

        var free = await SeedOrg(Usd);
        await ProfileService().UpdateOrganizationAsync(free, new UpdateOrganizationRequest { OrgName = "Acme", ClearBaseCurrency = true }, 2);
        _db.ChangeTracker.Clear();
        (await _db.Organizations.SingleAsync(o => o.Id == free)).BaseCurrency.Should().BeNull();
    }

    // ── GET model ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_get_model_carries_codes_and_a_lock_per_domain()
    {
        var org = await SeedOrg(Pkr);
        _usage.Domain[(org, TransactionDomain.Sale)] = "12 confirmed sale orders";
        _usage.Rate[org] = "34 exchange rates";

        var m = await Settings().GetAsync(org);

        m.IsStored.Should().BeFalse();
        m.SaleBaseCurrencyCode.Should().Be("PKR");
        m.RateCurrencyCode.Should().Be("PKR");
        m.Locks.Sale.Should().BeEquivalentTo(new OrgCurrencySettingsLock { Locked = true, Reason = "12 confirmed sale orders" });
        m.Locks.Purchase.Locked.Should().BeFalse();
        m.Locks.Service.Locked.Should().BeFalse();
        m.Locks.RateCurrency.Should().BeEquivalentTo(new OrgCurrencySettingsLock { Locked = true, Reason = "34 exchange rates" });
    }

    [Fact]
    public async Task An_unknown_organization_is_404()
    {
        var act = () => Settings().UpdateAsync(Guid.NewGuid(), All(Pkr), 1);
        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ── Provisioning (P1-14) ────────────────────────────────────────────────

    [Fact]
    public async Task Provisioning_creates_the_row_from_the_base_currency_once()
    {
        var org = await SeedOrg(Usd);
        var handler = new OrganizationCurrencySettingsProvisioningHandler(_db, _orgCurrencies);

        await handler.OnOrganizationProvisionedAsync(org);
        await handler.OnOrganizationProvisionedAsync(org);

        _db.ChangeTracker.Clear();
        var rows = await _db.OrganizationCurrencySettings.Where(s => s.OrganizationId == org).ToListAsync();
        rows.Should().ContainSingle();
        new[] { rows[0].SaleBaseCurrencyId, rows[0].PurchaseBaseCurrencyId, rows[0].ServiceBaseCurrencyId, rows[0].RateCurrencyId }
            .Should().OnlyContain(id => id == Usd);
    }

    [Fact]
    public async Task Provisioning_an_organization_without_a_base_currency_creates_no_row()
    {
        // D-7: a stored row's sale base must equal Organization.BaseCurrency. With no base the missing row already reads
        // as PKR for the new reads, and the legacy read keeps answering null.
        var org = await SeedOrg(null);
        var handler = new OrganizationCurrencySettingsProvisioningHandler(_db, _orgCurrencies);

        await handler.OnOrganizationProvisionedAsync(org);

        (await Row(org)).Should().BeNull();
        (await _db.Organizations.SingleAsync(o => o.Id == org)).BaseCurrency.Should().BeNull();
    }

    [Fact]
    public async Task The_startup_backfill_fills_every_organization_with_a_base_and_no_row()
    {
        var a = await SeedOrg(Usd);
        var b = await SeedOrg(null);
        await Settings().UpdateAsync(b, All(Eur), 1);
        var c = await SeedOrg(null);

        await new OrganizationCurrencySettingsProvisioningHandler(_db, _orgCurrencies).BackfillAsync();

        (await Row(a))!.SaleBaseCurrencyId.Should().Be(Usd);
        (await Row(b))!.SaleBaseCurrencyId.Should().Be(Eur, "an existing row is left alone");
        (await Row(c)).Should().BeNull("no base currency → no row");
    }

    [Fact]
    public async Task Saving_settings_for_an_organization_without_a_base_sets_its_base_to_the_sale_base()
    {
        var org = await SeedOrg(null);

        await Settings().UpdateAsync(org, new UpdateOrgCurrencySettingsRequest
        {
            SaleBaseCurrencyId = Aed, PurchaseBaseCurrencyId = Usd, ServiceBaseCurrencyId = Aed
        }, 1);

        (await _db.Organizations.SingleAsync(o => o.Id == org)).BaseCurrency.Should().Be(Aed);
        (await Resolver().GetBaseCurrencyIdAsync(org)).Should().Be(Aed);
        (await Row(org))!.RateCurrencyId.Should().Be(Pkr, "the first save keeps the fallback rate currency");
    }

    // ── Organization profile edit keeps the mirror (D-7) ────────────────────

    private TenancyService ProfileService()
    {
        var codes = new Mock<ICurrencyCodeLookup>();
        codes.Setup(c => c.GetCodeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, CancellationToken _) => Codes.TryGetValue(id, out var c) ? c : null);
        var repo = new TenancyRepository(_db, Mock.Of<IOrgUserProvisioningService>(), Mock.Of<IWorkflowSeedingService>());
        return new TenancyService(repo, Mock.Of<IOrgUserProvisioningService>(), Mock.Of<ITenantSnapshotProvider>(), codes.Object,
            currencySettings: Settings());
    }

    [Fact]
    public async Task A_profile_base_currency_change_moves_the_stored_sale_base_too()
    {
        var org = await SeedOrg(Pkr);
        await Settings().UpdateAsync(org, new UpdateOrgCurrencySettingsRequest
        {
            SaleBaseCurrencyId = Pkr, PurchaseBaseCurrencyId = Usd, ServiceBaseCurrencyId = Pkr
        }, 1);

        await ProfileService().UpdateOrganizationAsync(org, new UpdateOrganizationRequest { OrgName = "Acme", BaseCurrency = Eur }, 2);

        var row = (await Row(org))!;
        row.SaleBaseCurrencyId.Should().Be(Eur);
        row.PurchaseBaseCurrencyId.Should().Be(Usd);
        (await _db.Organizations.SingleAsync(o => o.Id == org)).BaseCurrency.Should().Be(Eur);
    }

    [Fact]
    public async Task A_profile_base_currency_change_is_409_when_the_sale_base_is_in_use()
    {
        var org = await SeedOrg(Pkr);
        await Settings().UpdateAsync(org, All(Pkr), 1);
        _usage.Domain[(org, TransactionDomain.Sale)] = "1 confirmed sale order";

        var act = () => ProfileService().UpdateOrganizationAsync(org, new UpdateOrganizationRequest { OrgName = "Renamed", BaseCurrency = Eur }, 2);

        await act.Should().ThrowAsync<ConflictException>();
        var o = await _db.Organizations.SingleAsync(x => x.Id == org);
        o.BaseCurrency.Should().Be(Pkr);
        o.OrgName.Should().Be("Acme");
    }

    // ── Lookups reference check ─────────────────────────────────────────────

    [Fact]
    public async Task A_currency_in_any_settings_row_is_referenced_for_lookups()
    {
        var org = await SeedOrg(Pkr);
        await Settings().UpdateAsync(org, new UpdateOrgCurrencySettingsRequest
        {
            SaleBaseCurrencyId = Pkr, PurchaseBaseCurrencyId = Usd, ServiceBaseCurrencyId = Aed
        }, 1);

        var checker = new TenancyCurrencyReferenceChecker(_db);
        checker.IsValueReferenced(Usd).Should().BeTrue();
        checker.IsValueReferenced(Aed).Should().BeTrue();
        checker.IsValueReferenced(Eur).Should().BeFalse();
    }

    // ── Controller + DI ─────────────────────────────────────────────────────

    [Fact]
    public void The_controller_is_routed_and_every_action_gated_per_the_contract()
    {
        var type = typeof(OrganizationCurrencySettingsController);
        type.GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/organization/currency-settings");

        var get = type.GetMethod(nameof(OrganizationCurrencySettingsController.Get))!;
        get.GetCustomAttribute<HttpGetAttribute>().Should().NotBeNull();
        get.GetCustomAttribute<RequirePermissionAttribute>()!.AnyOf
            .Should().BeEquivalentTo(PermissionCodes.CURRENCY_VIEW, PermissionCodes.ORG_CURRENCY_SETTINGS_MANAGE);

        var put = type.GetMethod(nameof(OrganizationCurrencySettingsController.Update))!;
        put.GetCustomAttribute<HttpPutAttribute>().Should().NotBeNull();
        put.GetCustomAttribute<RequirePermissionAttribute>()!.AnyOf
            .Should().BeEquivalentTo(PermissionCodes.ORG_CURRENCY_SETTINGS_MANAGE);
    }

    [Fact]
    public async Task The_controller_always_acts_on_the_callers_own_organization()
    {
        var mine = await SeedOrg(Pkr);
        var other = await SeedOrg(Pkr);
        var tenant = new StaticTenantContext { OrganizationId = mine, IsSuperAdmin = true };
        var controller = new OrganizationCurrencySettingsController(Settings(), tenant);
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };

        await controller.Update(All(Usd));

        (await Row(mine))!.SaleBaseCurrencyId.Should().Be(Usd);
        (await Row(other)).Should().BeNull();
    }

    [Fact]
    public void The_module_registers_the_settings_service_and_the_provisioning_handler()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=none;Database=none;Trusted_Connection=True;" })
            .Build();
        var services = new ServiceCollection();
        services.AddTenancyModule(configuration);

        services.Should().Contain(d => d.ServiceType == typeof(IOrganizationCurrencySettingsService));
        services.Should().Contain(d => d.ServiceType == typeof(IOrganizationProvisionedHandler)
                                    && d.ImplementationType == typeof(OrganizationCurrencySettingsProvisioningHandler));
    }

    // ── Fakes ───────────────────────────────────────────────────────────────

    internal sealed class FakeOrgCurrencies : IOrgCurrencyLookup
    {
        private readonly List<(Guid Org, OrgCurrencyInfo Info)> _rows = new();

        public void Add(Guid org, Guid id, string code, bool active) =>
            _rows.Add((org, new OrgCurrencyInfo(id, code, code, code, 2, 0.01m, "before", active, _rows.Count)));

        public Task<OrgCurrencyInfo?> GetAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) =>
            Task.FromResult(_rows.Where(r => r.Org == organizationId && r.Info.CurrencyId == currencyId).Select(r => r.Info).FirstOrDefault());

        public Task<OrgCurrencyInfo?> GetByCodeAsync(Guid organizationId, string code, CancellationToken ct = default) =>
            Task.FromResult(_rows.Where(r => r.Org == organizationId && r.Info.Code == code).Select(r => r.Info).FirstOrDefault());

        public Task<IReadOnlyList<OrgCurrencyInfo>> ListAsync(Guid organizationId, bool activeOnly, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OrgCurrencyInfo>>(_rows.Where(r => r.Org == organizationId && (!activeOnly || r.Info.IsActive)).Select(r => r.Info).ToList());

        public Task<int> GetDecimalPlacesAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) => Task.FromResult(2);
    }

    internal sealed class FakeUsage : ICurrencyUsageChecker
    {
        public Dictionary<(Guid, TransactionDomain), string> Domain { get; } = new();
        public Dictionary<Guid, string> Rate { get; } = new();

        public Task<string?> DescribeCurrencyUsageAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> DescribeDomainBaseUsageAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default) =>
            Task.FromResult(Domain.TryGetValue((organizationId, domain), out var d) ? d : null);

        public Task<string?> DescribeRateCurrencyUsageAsync(Guid organizationId, CancellationToken ct = default) =>
            Task.FromResult(Rate.TryGetValue(organizationId, out var d) ? d : null);
    }
}

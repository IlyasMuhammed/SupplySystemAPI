using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>A29-P2-04 — CRUD over inventory.PricingRules.</summary>
public class PricingRuleServiceTests
{
    private const int User = 7;
    private static readonly Guid Currency = Guid.NewGuid();

    private sealed record Harness(InventoryDbContext Db, PricingRuleService Service, Guid VariantUuid, int VariantId);

    private static async Task<Harness> NewHarness(Guid? baseCurrency = null)
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        var product = new Product { Uuid = Guid.NewGuid(), Sku = "CABLE-4MM", Name = "4mm cable", Status = "ACTIVE", IsActive = true, CreatedBy = 1 };
        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), Sku = "CABLE-4MM-V1", VariantName = "Default",
            PurchasePrice = 10m, IsDefault = true, IsActive = true, CreatedDate = DateTime.UtcNow
        };
        product.Variants.Add(variant);
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var currency = new Mock<IOrganizationCurrencyService>();
        currency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>())).ReturnsAsync(baseCurrency);

        var service = new PricingRuleService(db, tenant, currency.Object);
        return new Harness(db, service, variant.Uuid, variant.Id);
    }

    private static CreatePricingRuleRequest ValidCreateRequest(Guid variantUuid, Guid? partnerUuid = null) => new()
    {
        VariantUuid = variantUuid, PartnerUuid = partnerUuid, PriceType = PricingRuleType.Selling,
        UnitPrice = 50m, CurrencyId = Currency, EffectiveFrom = DateTime.UtcNow.Date
    };

    [Fact]
    public async Task Creates_a_rule_and_reads_it_back()
    {
        var h = await NewHarness();
        var uuid = await h.Service.CreateAsync(ValidCreateRequest(h.VariantUuid), User);

        var model = await h.Service.GetByIdAsync(uuid);

        model.Should().NotBeNull();
        model!.VariantUuid.Should().Be(h.VariantUuid);
        model.UnitPrice.Should().Be(50m);
        model.PriceType.Should().Be(PricingRuleType.Selling);
        model.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Create_defaults_currency_to_the_orgs_base_currency_when_not_supplied()
    {
        var h = await NewHarness(baseCurrency: Currency);
        var req = ValidCreateRequest(h.VariantUuid);
        req.CurrencyId = null;

        var uuid = await h.Service.CreateAsync(req, User);

        (await h.Service.GetByIdAsync(uuid))!.CurrencyId.Should().Be(Currency);
    }

    [Fact]
    public async Task Create_fails_when_no_currency_is_supplied_and_the_org_has_no_base_currency()
    {
        var h = await NewHarness(baseCurrency: null);
        var req = ValidCreateRequest(h.VariantUuid);
        req.CurrencyId = null;

        var act = () => h.Service.CreateAsync(req, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Create_fails_for_an_unknown_variant()
    {
        var h = await NewHarness();
        var req = ValidCreateRequest(Guid.NewGuid());

        var act = () => h.Service.CreateAsync(req, User);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Theory]
    [InlineData("BOGUS")]
    [InlineData("")]
    public async Task Create_fails_for_an_invalid_price_type(string priceType)
    {
        var h = await NewHarness();
        var req = ValidCreateRequest(h.VariantUuid);
        req.PriceType = priceType;

        var act = () => h.Service.CreateAsync(req, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Create_fails_for_a_contract_rule_with_no_partner()
    {
        var h = await NewHarness();
        var req = ValidCreateRequest(h.VariantUuid);
        req.PriceType = PricingRuleType.Contract;
        req.PartnerUuid = null;

        var act = () => h.Service.CreateAsync(req, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Create_succeeds_for_a_contract_rule_with_a_partner()
    {
        var h = await NewHarness();
        var partner = Guid.NewGuid();
        var req = ValidCreateRequest(h.VariantUuid, partner);
        req.PriceType = PricingRuleType.Contract;

        var uuid = await h.Service.CreateAsync(req, User);

        (await h.Service.GetByIdAsync(uuid))!.PartnerUuid.Should().Be(partner);
    }

    [Fact]
    public async Task Create_fails_when_min_qty_exceeds_max_qty()
    {
        var h = await NewHarness();
        var req = ValidCreateRequest(h.VariantUuid);
        req.MinQty = 20;
        req.MaxQty = 10;

        var act = () => h.Service.CreateAsync(req, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Create_fails_when_effective_from_is_after_effective_to()
    {
        var h = await NewHarness();
        var req = ValidCreateRequest(h.VariantUuid);
        req.EffectiveFrom = DateTime.UtcNow.Date;
        req.EffectiveTo   = DateTime.UtcNow.Date.AddDays(-1);

        var act = () => h.Service.CreateAsync(req, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Update_changes_the_fields_and_returns_true()
    {
        var h = await NewHarness();
        var uuid = await h.Service.CreateAsync(ValidCreateRequest(h.VariantUuid), User);

        var updated = await h.Service.UpdateAsync(uuid, new UpdatePricingRuleRequest
        {
            PriceType = PricingRuleType.Promotional, UnitPrice = 30m, CurrencyId = Currency,
            EffectiveFrom = DateTime.UtcNow.Date, IsActive = true
        });

        updated.Should().BeTrue();
        var model = await h.Service.GetByIdAsync(uuid);
        model!.UnitPrice.Should().Be(30m);
        model.PriceType.Should().Be(PricingRuleType.Promotional);
    }

    [Fact]
    public async Task Update_returns_false_for_an_unknown_rule()
    {
        var h = await NewHarness();

        var updated = await h.Service.UpdateAsync(Guid.NewGuid(), new UpdatePricingRuleRequest
        {
            PriceType = PricingRuleType.Selling, UnitPrice = 1m, CurrencyId = Currency, EffectiveFrom = DateTime.UtcNow
        });

        updated.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_soft_deletes_by_clearing_is_active_rather_than_removing_the_row()
    {
        var h = await NewHarness();
        var uuid = await h.Service.CreateAsync(ValidCreateRequest(h.VariantUuid), User);

        var deleted = await h.Service.DeleteAsync(uuid);

        deleted.Should().BeTrue();
        var model = await h.Service.GetByIdAsync(uuid);
        model.Should().NotBeNull("a soft delete keeps the row");
        model!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_returns_false_for_an_unknown_rule()
    {
        var h = await NewHarness();

        (await h.Service.DeleteAsync(Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task List_filters_by_variant_partner_type_and_active()
    {
        var h = await NewHarness();
        var partner = Guid.NewGuid();

        await h.Service.CreateAsync(ValidCreateRequest(h.VariantUuid), User);
        var contractUuid = await h.Service.CreateAsync(
            new CreatePricingRuleRequest
            {
                VariantUuid = h.VariantUuid, PartnerUuid = partner, PriceType = PricingRuleType.Contract,
                UnitPrice = 40m, CurrencyId = Currency, EffectiveFrom = DateTime.UtcNow.Date
            }, User);
        await h.Service.DeleteAsync(contractUuid); // now inactive

        var byPartner = await h.Service.GetListAsync(new PricingRuleListFilter { PartnerUuid = partner });
        byPartner.Data.Should().ContainSingle(x => x.Uuid == contractUuid);

        var byType = await h.Service.GetListAsync(new PricingRuleListFilter { PriceType = PricingRuleType.Selling });
        byType.Data.Should().OnlyContain(x => x.PriceType == PricingRuleType.Selling);

        var activeOnly = await h.Service.GetListAsync(new PricingRuleListFilter { IsActive = true });
        activeOnly.Data.Should().NotContain(x => x.Uuid == contractUuid);

        var inactiveOnly = await h.Service.GetListAsync(new PricingRuleListFilter { IsActive = false });
        inactiveOnly.Data.Should().ContainSingle(x => x.Uuid == contractUuid);
    }

    [Fact]
    public async Task List_is_scoped_to_the_variant_when_requested()
    {
        var h = await NewHarness();
        await h.Service.CreateAsync(ValidCreateRequest(h.VariantUuid), User);

        var result = await h.Service.GetListAsync(new PricingRuleListFilter { VariantUuid = h.VariantUuid });

        result.Data.Should().OnlyContain(x => x.VariantUuid == h.VariantUuid);
        result.TotalRecords.Should().Be(1);
    }
}

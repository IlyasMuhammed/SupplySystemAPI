using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.ProcurementCycle.Infrastructure;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Reports.Models;
using SMS.Modules.Warehouse.Models;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using Xunit;

namespace SMS.Integration.Tests.Manufacturing;

// A30-P4-21 — the one HTTP-level test the task register's own PARTIAL note (2026-09-26) flagged as
// missing: every seam of SO→FR→PO→BOM→PMR→SR→MI→QI→FGR→Allocation→Fulfilled was proven at the
// unit/module level with the *other* module's pieces mocked at the interface boundary, but nothing
// exercised the real DI container assembling Demand's SaleOrderFulfillmentListener and Material's
// ProductionReadinessListener together against one running app. This does exactly that, over real
// HTTP against the real Program.cs pipeline, reusing ProcurementCycleWebApplicationFactory (already
// migrates Material/Inventory/Demand/Warehouse schemas from empty — see its own extensive comment on
// why that needed the EnsureCreated-and-stamp-history workaround).
//
// Chain: Steel Rod (Purchase) → Steel Bolt (Manufacture, BOM1) → Bolt Kit (Manufacture, BOM2) →
// Packaged Bolt Kit (Manufacture, BOM3 — also takes Packaging Box, Purchase) — a genuine 3-level
// manufacture chain (Raw→Semi→FG→Assembly per the task register's own wording), triggered by
// confirming a sale order for the top (Assembly) item with zero stock anywhere.
//
// The ledger's debit=credit+scrap identity is proven as a separate, deliberately decoupled
// standalone production order at the very end (see STEP 9) rather than folded into the sale-order
// chain itself: SaleOrderFulfillmentListener only moves a sale order line's Status off OPEN once its
// DeficitQty reaches exactly zero (confirmed by reading the listener directly), so introducing a QI
// rejection anywhere in the chain that feeds the sold quantity would leave the line permanently
// short and never prove the "RESERVED" transition this test's real job is to prove. The unit-level
// ledger math itself was already proven directly in Material's own QualityAndFgrTests; what is new
// here is exercising the same identity through the real HTTP controllers instead of an in-process
// service call.
public sealed class ManufacturingCycleTests : IClassFixture<ProcurementCycleWebApplicationFactory>
{
    private readonly ProcurementCycleWebApplicationFactory _factory;
    private readonly HttpClient _admin;

    public ManufacturingCycleTests(ProcurementCycleWebApplicationFactory factory)
    {
        _factory = factory;
        _admin   = factory.CreateAdminClient();
    }

    [Fact]
    public async Task Full_manufacturing_cycle_from_sale_order_through_a_three_level_chain_to_reservation()
    {
        // ═══════════════════════════════════════════════════════════════════════
        // STEP 0 — SETUP: warehouse, customer, a second real user (BOM approval's
        // four-eyes rule and QI's "not the order's own creator" rule are identity
        // checks, not permission checks — the SystemAdmin override that lets one
        // login submit *and* approve every workflow-engine document does not apply
        // to either of these, both being inline code, not workflow-engine steps).
        // ═══════════════════════════════════════════════════════════════════════

        // ROLE-type workflow steps (PO's PROCUREMENT_MANAGER/FINANCE_MANAGER tiers) resolve their
        // *candidate* approver list eagerly at submit time and throw if zero active users hold that
        // role — the admin's SystemAdmin override only changes who is *allowed* to approve an
        // already-resolved step, not whether a resolvable candidate must exist in the first place.
        // InventoryManager's own placeholder is unnecessary — invMgr below is a real user holding it.
        await Task.WhenAll(
            CreatePlaceholderRoleUserAsync("procmgr@p421.test", (int)EnumRole.ProcurementManager),
            CreatePlaceholderRoleUserAsync("finmgr@p421.test", (int)EnumRole.FinanceOfficer),
            // GRN_QC's "QC Inspector Verification" step maps to WarehouseOperator, not a dedicated
            // QC role (see ApproverResolutionService's role-code table).
            CreatePlaceholderRoleUserAsync("qcinspector@p421.test", (int)EnumRole.WarehouseOperator));

        var warehouse  = await CreateWarehouseAsync("MFG-WH", "Manufacturing Warehouse");
        var customerId = await CreateCustomerAsync("Manufacturing Test Customer");
        var currencyId = await CreateCurrencyAsync("US Dollar", "USD", "$");
        var invMgr     = await CreateSecondApproverClientAsync();

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 1 — PRODUCTS: Steel Rod, Steel Bolt, Bolt Kit, Packaging Box, Packaged Bolt Kit
        // ═══════════════════════════════════════════════════════════════════════

        var steelRod  = await CreateManufacturingProductAsync("P421 Steel Rod", ProductType.RawMaterial, SupplyMethod.Purchase, purchasePrice: 5m);
        var steelBolt = await CreateManufacturingProductAsync("P421 Steel Bolt", ProductType.SemiFinished, SupplyMethod.Manufacture, defaultProductionWarehouseId: warehouse.Id);
        var boltKit   = await CreateManufacturingProductAsync("P421 Bolt Kit", ProductType.SemiFinished, SupplyMethod.Manufacture, defaultProductionWarehouseId: warehouse.Id);
        var packaging = await CreateManufacturingProductAsync("P421 Packaging Box", ProductType.RawMaterial, SupplyMethod.Purchase, purchasePrice: 2m);
        var packaged  = await CreateManufacturingProductAsync("P421 Packaged Bolt Kit", ProductType.FinishedGood, SupplyMethod.Manufacture,
            defaultProductionWarehouseId: warehouse.Id, sellingPrice: 500m, availableForRetail: true);

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 2 — SUPPLIER + BOMS: 3 recipes, each create→submit(admin)→approve+activate(invMgr)
        // ═══════════════════════════════════════════════════════════════════════

        var supplierId = await CreateSupplierAsync("P421 Raw Materials Ltd", "P421-SUP");
        await SetDefaultSupplierAsync(steelRod.VariantUuid, supplierId, unitCost: 5m, currencyId);
        await SetDefaultSupplierAsync(packaging.VariantUuid, supplierId, unitCost: 2m, currencyId);

        var bom1 = await CreateAndActivateBomAsync(invMgr, steelBolt.ProductUuid, 1m, (steelRod.VariantUuid, 1m, true));
        var bom2 = await CreateAndActivateBomAsync(invMgr, boltKit.ProductUuid, 1m, (steelBolt.VariantUuid, 4m, true));
        var bom3 = await CreateAndActivateBomAsync(invMgr, packaged.ProductUuid, 1m, (boltKit.VariantUuid, 1m, true), (packaging.VariantUuid, 1m, false));
        bom1.Should().NotBeEmpty(); bom2.Should().NotBeEmpty(); bom3.Should().NotBeEmpty();

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 3 — SALE ORDER: 1x Packaged Bolt Kit, confirmed with zero stock anywhere
        // ═══════════════════════════════════════════════════════════════════════

        var soUuid = await PostAsync<Guid>("/api/sale-orders", new CreateSaleOrderRequest
        {
            PartnerId    = customerId,
            CurrencyId   = currencyId,
            DeliveryMode = "SELF_PICKUP",
            Lines        = [new CreateSaleOrderLineRequest { VariantUuid = packaged.VariantUuid, Quantity = 1 }]
        });

        var soBeforeConfirm = await GetAsync<SaleOrderModel>($"/api/sale-orders/{soUuid}");
        soBeforeConfirm.Lines.Single().Quantity.Should().Be(1);

        var confirmResp = await _admin.PostAsync($"/api/sale-orders/{soUuid}/confirm", null);
        confirmResp.StatusCode.Should().Be(HttpStatusCode.OK, $"sale order confirm failed: {await confirmResp.Content.ReadAsStringAsync()}");

        var soAfterConfirm = await GetAsync<SaleOrderModel>($"/api/sale-orders/{soUuid}");
        soAfterConfirm.Lines.Single().DeficitQty.Should().Be(1, "no stock exists anywhere yet");

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 4 — WAIT for the async AutoPoCreationJob (Hangfire) to raise and
        // fully plan the top-level production order — routed to
        // ISaleOrderManufacturingService, never a purchase order, since Packaged
        // Bolt Kit's SupplyMethod is MANUFACTURE.
        // ═══════════════════════════════════════════════════════════════════════

        var topProdUuid = await WaitForSingleProductionOrderAsync(packaged.ProductUuid, "top-level Packaged Bolt Kit order (raised by AutoPoCreationJob → SaleOrderManufacturingService)");

        // The row itself is visible (its own CreateAsync's own SaveChanges) the instant it exists,
        // but the *same* Hangfire job execution then goes on to plan it — several more awaited steps
        // within that one still-running job. CreateChildForSupplyAsync recursively awaits the
        // child's own PlanCoreAsync (which is what raises the grandchild and the grandchild's own
        // supply requirement) before the parent's own shortage loop moves on to its next material,
        // so waiting for the *parent's* two supply requirements (Bolt Kit's manufacture shortage,
        // Packaging Box's purchase shortage) to both exist is enough to guarantee the entire chain
        // underneath — child, grandchild, grandchild's own SR — has already been fully planned too.
        List<SupplyRequirementModel> topSupplyReqs;
        try
        {
            // Count == 2 alone isn't enough: EnsureForShortageAsync's own SaveChangesAsync commits
            // the SupplyRequirement row (making it visible to a concurrent GET) *before* it calls
            // ActAsync, which is what sets SupplySourceUuid — so also wait for both rows to have
            // actually been acted on (a real PO or child order raised against them), not just exist.
            topSupplyReqs = await WaitForAsync(
                () => GetAsync<List<SupplyRequirementModel>>($"/api/production-orders/{topProdUuid}/supply-requirements"),
                list => list.Count == 2 && list.All(sr => sr.SupplySourceUuid.HasValue),
                "the top-level order's own two supply requirements (Bolt Kit, Packaging Box), each fully acted on — and, transitively, the whole chain underneath them");
        }
        catch (TimeoutException)
        {
            var diagOrder = await GetAsync<ProductionOrderDetailModel>($"/api/production-orders/{topProdUuid}");
            var diagMaterials = await GetAsync<List<ProductionMaterialModel>>($"/api/production-orders/{topProdUuid}/materials");
            var diagSrs = await GetAsync<List<SupplyRequirementModel>>($"/api/production-orders/{topProdUuid}/supply-requirements");
            throw new Exception(
                $"DIAG order.Status={diagOrder.Status} readiness={diagOrder.MaterialReadiness} children={diagOrder.ChildOrders.Count} | " +
                $"materials=[{string.Join("; ", diagMaterials.Select(m => $"{m.MaterialProductName} req={m.RequiredQuantity} shortage={m.ShortageQuantity} status={m.Status} covered={m.IsCovered}"))}] | " +
                $"srs=[{string.Join("; ", diagSrs.Select(s => $"{s.ProductName} method={s.SupplyMethod} status={s.Status}"))}]");
        }

        var topDetail = await GetAsync<ProductionOrderDetailModel>($"/api/production-orders/{topProdUuid}");
        topDetail.SourceType.Should().Be(ProductionSourceType.SalesOrder);
        topDetail.PlannedQuantity.Should().Be(1);

        var boltKitChildUuid = topDetail.ChildOrders.Single().UUID;
        var boltKitDetail = await GetAsync<ProductionOrderDetailModel>($"/api/production-orders/{boltKitChildUuid}");
        boltKitDetail.ProductUuid.Should().Be(boltKit.ProductUuid);
        boltKitDetail.PlannedQuantity.Should().Be(1);

        var steelBoltGrandchildUuid = boltKitDetail.ChildOrders.Single().UUID;
        var steelBoltDetail = await GetAsync<ProductionOrderDetailModel>($"/api/production-orders/{steelBoltGrandchildUuid}");
        steelBoltDetail.ProductUuid.Should().Be(steelBolt.ProductUuid);
        steelBoltDetail.PlannedQuantity.Should().Be(4, "1 Bolt Kit needs 4 Steel Bolts per BOM2");

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 5 — RAW MATERIAL PURCHASES: Steel Rod (4, for the chain) and
        // Packaging Box (1), each auto-raised DRAFT via the Supply Requirement
        // engine, submitted/approved/sent/received exactly like any other PO.
        // ═══════════════════════════════════════════════════════════════════════

        var steelBoltSupplyReqs = await GetAsync<List<SupplyRequirementModel>>($"/api/production-orders/{steelBoltGrandchildUuid}/supply-requirements");
        var steelRodSr = steelBoltSupplyReqs.Single(sr => sr.SupplyMethod == SupplyMethod.Purchase);
        steelRodSr.SupplySourceType.Should().Be(SupplySourceType.PurchaseOrder);
        var steelRodPoUuid = steelRodSr.SupplySourceUuid!.Value;

        var packagingSr = topSupplyReqs.Single(sr => sr.SupplyMethod == SupplyMethod.Purchase);
        var packagingPoUuid = packagingSr.SupplySourceUuid!.Value;

        await SubmitAndFullyApprovePoAsync(steelRodPoUuid);
        await ReceiveFullyAsync(steelRodPoUuid, warehouse.Uuid, 4);

        await SubmitAndFullyApprovePoAsync(packagingPoUuid);
        await ReceiveFullyAsync(packagingPoUuid, warehouse.Uuid, 1);

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 6 — STEEL BOLT (grandchild): now READY (Steel Rod reserved by the
        // GRN's own allocation run) — issue, start, report, complete, QI, FGR.
        // ═══════════════════════════════════════════════════════════════════════

        (await GetAsync<ProductionOrderDetailModel>($"/api/production-orders/{steelBoltGrandchildUuid}")).Status.Should().Be(ProductionOrderStatus.Ready);
        await RunFloorCycleToFgrAsync(invMgr, steelBoltGrandchildUuid, produced: 4, accepted: 4, rejected: 0);

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 7 — BOLT KIT (child): the grandchild's FGR just re-ran the
        // allocation engine for Steel Bolt, reserving it for this order's own PMR.
        // ═══════════════════════════════════════════════════════════════════════

        (await GetAsync<ProductionOrderDetailModel>($"/api/production-orders/{boltKitChildUuid}")).Status.Should().Be(ProductionOrderStatus.Ready);
        await RunFloorCycleToFgrAsync(invMgr, boltKitChildUuid, produced: 1, accepted: 1, rejected: 0);

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 8 — PACKAGED BOLT KIT (top): both its materials (Bolt Kit from the
        // chain, Packaging Box from its own GRN) are now covered.
        // ═══════════════════════════════════════════════════════════════════════

        (await GetAsync<ProductionOrderDetailModel>($"/api/production-orders/{topProdUuid}")).Status.Should().Be(ProductionOrderStatus.Ready);
        await RunFloorCycleToFgrAsync(invMgr, topProdUuid, produced: 1, accepted: 1, rejected: 0);

        // The top order's own FGR just re-ran allocation for Packaged Bolt Kit —
        // SaleOrderFulfillmentListener (told after every run, same as Material's own
        // ProductionReadinessListener) re-parents that hold onto the exact
        // SALES_ORDER-sourced reservation the delivery pipeline already reads. This
        // is the one thing no unit test could prove: both modules' own
        // IAllocationRunListener wired together by the real container.
        var soFinal = await GetAsync<SaleOrderModel>($"/api/sale-orders/{soUuid}");
        var finalLine = soFinal.Lines.Single();
        finalLine.DeficitQty.Should().Be(0, "the one unit sold was fully produced and received");
        finalLine.Status.Should().Be("RESERVED");

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 9 — CHAINED MANUFACTURING REPORT (A30-P5-06) over the real 3-level chain
        // ═══════════════════════════════════════════════════════════════════════

        var chained = await GetAsync<ChainedManufacturingReport>($"/api/reports/manufacturing/chained/{topProdUuid}");
        chained.TotalOrdersInChain.Should().Be(3, "top + Bolt Kit child + Steel Bolt grandchild");
        chained.MaxDepth.Should().Be(2);
        chained.Root.ProductionOrderUuid.Should().Be(topProdUuid);
        chained.Root.Children.Single().ProductionOrderUuid.Should().Be(boltKitChildUuid);
        chained.Root.Children.Single().Children.Single().ProductionOrderUuid.Should().Be(steelBoltGrandchildUuid);

        // ═══════════════════════════════════════════════════════════════════════
        // STEP 10 — LEDGER VERIFICATION: debit = credit + scrap, on a standalone
        // order deliberately decoupled from the sale-order chain above (see this
        // class's own top-of-file note on why it cannot live inside that chain).
        // Manually raised (not sale-order-triggered), so it gets its own 2-unit
        // Steel Rod shortage and its own auto-PO, independent of STEP 5's.
        // ═══════════════════════════════════════════════════════════════════════

        var standaloneUuid = await PostAsync<Guid>("/api/production-orders", new CreateProductionOrderRequest
        {
            ProductUuid     = steelBolt.ProductUuid,
            PlannedQuantity = 2,
            WarehouseUuid   = warehouse.Uuid,
            RequiredDate    = DateTime.UtcNow.Date.AddDays(7),
            Plan            = true
        });

        var standaloneSupplyReqs = await GetAsync<List<SupplyRequirementModel>>($"/api/production-orders/{standaloneUuid}/supply-requirements");
        var standaloneSteelRodSr = standaloneSupplyReqs.Single(sr => sr.SupplyMethod == SupplyMethod.Purchase);
        var standaloneSteelRodPoUuid = standaloneSteelRodSr.SupplySourceUuid!.Value;
        await SubmitAndFullyApprovePoAsync(standaloneSteelRodPoUuid);
        await ReceiveFullyAsync(standaloneSteelRodPoUuid, warehouse.Uuid, 2);

        (await GetAsync<ProductionOrderDetailModel>($"/api/production-orders/{standaloneUuid}")).Status.Should().Be(ProductionOrderStatus.Ready);

        await RunFloorCycleToFgrAsync(invMgr, standaloneUuid, produced: 2, accepted: 1, rejected: 1);

        var ledger = await GetAsync<ProductionLedgerModel>($"/api/production-orders/{standaloneUuid}/ledger");
        ledger.Summary.FinishedGoodsQuantity.Should().Be(1);
        ledger.Summary.ScrapQuantity.Should().Be(1);
        ledger.Summary.YieldPercent.Should().Be(50m, "1 finished / (1 finished + 1 scrap)");
        ledger.Entries.Should().Contain(e => e.EntryType == "DEBIT", "the 2 Steel Rods issued to the floor");
        ledger.Entries.Should().Contain(e => e.EntryType == "CREDIT" && e.Quantity == 1m, "the FGR of the 1 accepted unit");
    }

    // ── Setup helpers ────────────────────────────────────────────────────────────

    private async Task<WarehouseModel> CreateWarehouseAsync(string code, string name)
    {
        await PostAsync<int>("/api/warehouses", new CreateWarehouseRequest { Code = code, Name = name });
        var all = await GetAsync<List<WarehouseModel>>("/api/warehouses");
        return all.Single(w => w.Code == code);
    }

    private async Task<Guid> CreateSupplierAsync(string name, string code) =>
        await PostAsync<Guid>("/api/suppliers", new SMS.Modules.Suppliers.Models.CreateSupplierRequest { SupplierName = name, SupplierCode = code });

    // A fresh org has no base currency configured, and both rate cards and sale orders refuse to
    // fall back silently — supply one explicitly everywhere instead of also configuring org settings.
    private async Task<Guid> CreateCurrencyAsync(string name, string code, string symbol) =>
        await PostAsync<Guid>("/api/lookups/currencies", new SMS.Modules.Lookups.Models.CreateCurrencyRequest { Name = name, Code = code, Symbol = symbol });

    private async Task<Guid> CreateCustomerAsync(string name) =>
        await PostAsync<Guid>("/api/partners", new SMS.Modules.Suppliers.Models.BusinessPartnerModel
        {
            PartnerCode = "P421-CUST", CompanyName = name, PartnerType = "CUSTOMER", IsCustomer = true, IsActive = true
        });

    private async Task CreatePlaceholderRoleUserAsync(string email, int roleId)
    {
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            FirstName = "Placeholder",
            LastName  = "Approver",
            Email     = email,
            RoleID    = roleId,
            SupplierType = "INTERNAL"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            $"placeholder user create failed for role {roleId}: {await resp.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// A genuinely different login from the admin's — BOM approval's four-eyes rule
    /// (§9.3) and QI's "not the order's own creator" rule (§18.4) are both identity
    /// checks in inline code, not permission checks, so the SystemAdmin override
    /// that lets one login submit-and-approve every workflow-engine document (PO,
    /// GRN, MIR) never applies to either. Created via the real HTTP endpoint (so
    /// account creation itself is validated normally); only the password is set
    /// directly (the email it would otherwise arrive by is swallowed by the
    /// factory's own NoOpEmailService).
    /// </summary>
    private async Task<HttpClient> CreateSecondApproverClientAsync()
    {
        const string email = "invmgr@p421.test";
        const string password = "InvMgr@12345!";

        var createResp = await _admin.PostAsJsonAsync("/api/users", new
        {
            FirstName = "Second", LastName = "Approver", Email = email,
            RoleID = (int)EnumRole.InventoryManager, SupplierType = "INTERNAL"
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created, $"second user create failed: {await createResp.Content.ReadAsStringAsync()}");

        using (var scope = _factory.Services.CreateScope())
        {
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<UserAccount>>();
            var hash = hasher.HashPassword(null!, password);
            await _factory.ExecuteNonQueryAsync(
                "UPDATE auth.UserAccounts SET Password = @hash WHERE Email = @email",
                cmd => { cmd.Parameters.AddWithValue("@hash", hash); cmd.Parameters.AddWithValue("@email", email); });
        }

        using var anon = _factory.CreateClient();
        var loginResp = await anon.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = password });
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK, $"second user login failed: {await loginResp.Content.ReadAsStringAsync()}");
        var body = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("result").GetProperty("accessToken").GetString()!;

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(int ProductId, Guid ProductUuid, Guid VariantUuid)> CreateManufacturingProductAsync(
        string name, string productType, string supplyMethod, decimal purchasePrice = 0m,
        decimal? sellingPrice = null, bool availableForRetail = false, int? defaultProductionWarehouseId = null)
    {
        var resp = await _admin.PostAsJsonAsync("/api/products", new CreateProductRequest
        {
            Name = name, UomCode = "PCS", ProductType = productType, SupplyMethod = supplyMethod,
            DefaultProductionWarehouseId = defaultProductionWarehouseId,
            Variants =
            [
                new CreateProductVariantRequest
                {
                    VariantName = "Default", PurchasePrice = purchasePrice, SellingPrice = sellingPrice,
                    IsDefault = true, IsAvailableForProduction = true, IsAvailableForRetail = availableForRetail
                }
            ]
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, $"product '{name}' create failed: {await resp.Content.ReadAsStringAsync()}");
        var productId = (await _factory.ReadResultAsync<JsonElement>(resp)).GetProperty("id").GetInt32();

        var detail = await GetAsync<ProductDetailModel>($"/api/products/{productId}");
        return (productId, detail.Uuid, detail.Variants.Single().Uuid);
    }

    /// <summary>The only place a variant's DefaultSupplierId is ever actually set (VariantSupplierService.SetPreferredAsync) — a rate card marked preferred.</summary>
    private async Task SetDefaultSupplierAsync(Guid variantUuid, Guid supplierUuid, decimal unitCost, Guid currencyId)
    {
        var rateCardUuid = await PostAsync<Guid>("/api/rate-cards", new CreateVariantSupplierRequest
        {
            VariantUuid = variantUuid, SupplierUuid = supplierUuid, VendorUnitCost = unitCost,
            EffectiveFrom = DateTime.UtcNow.Date, CurrencyId = currencyId
        });
        var preferredResp = await _admin.PatchAsync($"/api/rate-cards/{rateCardUuid}/preferred", null);
        preferredResp.StatusCode.Should().Be(HttpStatusCode.OK, $"set-preferred failed: {await preferredResp.Content.ReadAsStringAsync()}");
    }

    private async Task<Guid> CreateAndActivateBomAsync(HttpClient approver, Guid outputProductUuid, decimal baseQuantity, params (Guid Variant, decimal Qty, bool Critical)[] lines)
    {
        var uuid = await PostAsync<Guid>("/api/boms", new CreateBomRequest
        {
            ProductUuid  = outputProductUuid,
            BaseQuantity = baseQuantity,
            Lines        = lines.Select(l => new BomLineRequest { MaterialVariantUuid = l.Variant, Quantity = l.Qty, IsCritical = l.Critical }).ToList()
        });

        var submitResp = await _admin.PostAsync($"/api/boms/{uuid}/submit", null);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, $"BOM submit failed: {await submitResp.Content.ReadAsStringAsync()}");

        var approveResp = await approver.PostAsync($"/api/boms/{uuid}/approve", null);
        approveResp.StatusCode.Should().Be(HttpStatusCode.OK, $"BOM approve failed: {await approveResp.Content.ReadAsStringAsync()}");

        var activateResp = await approver.PostAsync($"/api/boms/{uuid}/activate", null);
        activateResp.StatusCode.Should().Be(HttpStatusCode.OK, $"BOM activate failed: {await activateResp.Content.ReadAsStringAsync()}");

        return uuid;
    }

    // ── Production order helpers ─────────────────────────────────────────────────

    /// <summary>
    /// The AutoPoCreationJob that raises the top-level order runs on a real Hangfire background
    /// worker (Program.cs calls AddHangfireServer(), confirmed by reading it — QueuePollInterval is
    /// zero, so this is normally near-instant, but it is still genuinely asynchronous relative to
    /// the confirm HTTP call already having returned).
    /// </summary>
    private async Task<Guid> WaitForSingleProductionOrderAsync(Guid productUuid, string description) =>
        (await WaitForAsync(
            () => GetAsync<PaginatedResponse<ProductionOrderListItemModel>>($"/api/production-orders?productUuid={productUuid}"),
            page => page.Data.Count == 1,
            description)).Data[0].UUID;

    /// <summary>Polls an async fetch until its result satisfies a condition, or times out. Every
    /// wait in this test is for the *same* underlying reason — Hangfire's own worker (a real one,
    /// not a mock — see Program.cs's AddHangfireServer()) processing a job asynchronously relative
    /// to the HTTP call that enqueued it having already returned.</summary>
    private static async Task<T> WaitForAsync<T>(Func<Task<T>> fetch, Func<T, bool> isReady, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var result = await fetch();
            if (isReady(result)) return result;
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Timed out waiting for: {description}");
            await Task.Delay(300);
        }
    }

    /// <summary>Issues every material a READY order is holding, then walks it start→report→complete→QI→FGR in one shot each.</summary>
    private async Task RunFloorCycleToFgrAsync(HttpClient inspector, Guid productionOrderUuid, decimal produced, decimal accepted, decimal rejected)
    {
        var materials = await GetAsync<List<ProductionMaterialModel>>($"/api/production-orders/{productionOrderUuid}/materials");
        var issueLines = materials.Where(m => m.Outstanding > 0).Select(m => new CreateProductionIssueLineRequest
        {
            RequirementUuid = m.UUID, Quantity = m.Outstanding
        }).ToList();

        var issueResp = await _admin.PostAsJsonAsync($"/api/production-orders/{productionOrderUuid}/issues", new CreateProductionIssueRequest
        {
            IssueType = "STANDARD", Lines = issueLines, Confirm = true
        });
        issueResp.StatusCode.Should().Be(HttpStatusCode.OK, $"material issue failed for {productionOrderUuid}: {await issueResp.Content.ReadAsStringAsync()}");

        var startResp = await _admin.PostAsync($"/api/production-orders/{productionOrderUuid}/start", null);
        startResp.StatusCode.Should().Be(HttpStatusCode.OK, $"start failed: {await startResp.Content.ReadAsStringAsync()}");

        var reportResp = await _admin.PostAsJsonAsync($"/api/production-orders/{productionOrderUuid}/report-output", new ReportOutputRequest { Quantity = produced });
        reportResp.StatusCode.Should().Be(HttpStatusCode.OK, $"report-output failed: {await reportResp.Content.ReadAsStringAsync()}");

        var completeResp = await _admin.PostAsync($"/api/production-orders/{productionOrderUuid}/complete", null);
        completeResp.StatusCode.Should().Be(HttpStatusCode.OK, $"complete failed: {await completeResp.Content.ReadAsStringAsync()}");

        var qiLines = new List<CreateQualityInspectionLineRequest>();
        if (accepted > 0) qiLines.Add(new CreateQualityInspectionLineRequest { CheckName = "Visual", Result = "PASS", QuantityChecked = accepted });
        if (rejected > 0) qiLines.Add(new CreateQualityInspectionLineRequest { CheckName = "Visual", Result = "FAIL", QuantityChecked = rejected });

        var qiResp = await inspector.PostAsJsonAsync($"/api/production-orders/{productionOrderUuid}/quality-inspections", new CreateQualityInspectionRequest { Lines = qiLines });
        qiResp.StatusCode.Should().Be(HttpStatusCode.OK, $"QI failed: {await qiResp.Content.ReadAsStringAsync()}");

        if (accepted > 0)
        {
            var fgrResp = await _admin.PostAsJsonAsync($"/api/production-orders/{productionOrderUuid}/fgr", new CreateFinishedGoodsReceiptRequest { Quantity = accepted, Confirm = true });
            fgrResp.StatusCode.Should().Be(HttpStatusCode.OK, $"FGR failed: {await fgrResp.Content.ReadAsStringAsync()}");
        }
    }

    // ── Procurement helpers (same shape as ProcurementToIssueCycleTests) ─────────

    private async Task SubmitAndFullyApprovePoAsync(Guid poUuid)
    {
        var submitResp = await _admin.PostAsync($"/api/purchase-orders/{poUuid}/submit", null);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, $"PO submit failed: {await submitResp.Content.ReadAsStringAsync()}");

        for (var i = 0; i < 6; i++)
        {
            var approveResp = await _admin.PostAsync($"/api/purchase-orders/{poUuid}/approve", null);
            approveResp.StatusCode.Should().Be(HttpStatusCode.OK, $"PO approve failed: {await approveResp.Content.ReadAsStringAsync()}");

            var po = await GetAsync<PoDetailModel>($"/api/purchase-orders/{poUuid}");
            if (po.Status == "APPROVED") break;
        }
        (await GetAsync<PoDetailModel>($"/api/purchase-orders/{poUuid}")).Status.Should().Be("APPROVED");

        var sendResp = await _admin.PostAsJsonAsync($"/api/purchase-orders/{poUuid}/send", new { SupplierContactMobile = (string?)null });
        sendResp.StatusCode.Should().Be(HttpStatusCode.OK, $"PO send failed: {await sendResp.Content.ReadAsStringAsync()}");
    }

    private async Task ReceiveFullyAsync(Guid poUuid, Guid warehouseUuid, decimal quantity)
    {
        var po = await GetAsync<PoDetailModel>($"/api/purchase-orders/{poUuid}");
        var line = po.Lines.Single();

        var grnUuid = await PostAsync<Guid>("/api/grns", new CreateGrnRequest
        {
            PoUuid = poUuid, WarehouseUuid = warehouseUuid, ReceivedAt = DateTime.UtcNow,
            Lines = [new GrnLineReceiveInput { PoLineUuid = line.UUID, QtyReceived = quantity, QtyAccepted = quantity, QtyRejected = 0 }]
        });

        var submitResp = await _admin.PostAsync($"/api/grns/{grnUuid}/submit", null);
        submitResp.StatusCode.Should().Be(HttpStatusCode.OK, $"GRN submit failed: {await submitResp.Content.ReadAsStringAsync()}");

        var approveResp = await _admin.PostAsJsonAsync($"/api/grns/{grnUuid}/approve", new { Remarks = (string?)null });
        approveResp.StatusCode.Should().Be(HttpStatusCode.OK, $"GRN approve failed: {await approveResp.Content.ReadAsStringAsync()}");
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────────

    private async Task<T> PostAsync<T>(string url, object body)
    {
        var resp = await _admin.PostAsJsonAsync(url, body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, $"POST {url} failed: {await resp.Content.ReadAsStringAsync()}");
        return await _factory.ReadResultAsync<T>(resp);
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var resp = await _admin.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} failed: {await resp.Content.ReadAsStringAsync()}");
        return await _factory.ReadResultAsync<T>(resp);
    }
}

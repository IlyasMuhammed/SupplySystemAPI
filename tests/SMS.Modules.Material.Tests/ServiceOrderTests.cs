using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Repositories;
using SMS.Modules.Material.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Models;
using Xunit;

namespace SMS.Modules.Material.Tests;

/// <summary>
/// A36-P2-14 / P3-11 / P4-07 — service orders at service level (TS-06..TS-16, TS-24..TS-30, TS-32, TS-33): the real
/// allocation engine, stock reservations, inventory ledger, supply requirement engine and both readiness listeners; the
/// purchase order side, partners and the sale order listener are mocks.
/// </summary>
public class ServiceOrderTests
{
    private const int Tech = 7;
    private static readonly string Today = DateTime.UtcNow.ToString("yyyy-MM-dd");

    private sealed class Harness
    {
        public MaterialDbContext  Material  { get; }
        public InventoryDbContext Inventory { get; }
        public BomRepository      Boms      { get; }
        public IServiceOrderService       Orders  { get; }
        public IServiceOrderDemandService Demand  { get; }
        public IAllocationEngine          Engine  { get; }
        public Mock<IPurchaseOrderService> PurchaseOrders { get; } = new();
        public Mock<ISaleOrderServiceFulfillmentListener> SoListener { get; } = new();
        public List<Job> Jobs { get; } = [];
        public Guid Main { get; }
        public Guid Vendor   { get; } = Guid.NewGuid();
        public Guid Customer { get; } = Guid.NewGuid();
        private readonly Dictionary<string, int> _seq = [];

        public Harness()
        {
            var tenant = new StaticTenantContext();
            Material  = new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
            Inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

            var numbers = new Mock<IDocumentNumberGenerator>();
            numbers.Setup(n => n.NextAsync(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync((string p, DateTime? _, Guid? _, CancellationToken _) => $"{p}-2026-{_seq[p] = _seq.GetValueOrDefault(p) + 1:D5}");

            var main = new Warehouse { Uuid = Guid.NewGuid(), Code = "MAIN", Name = "Main", IsActive = true, CreatedBy = 1 };
            Inventory.Warehouses.Add(main);
            Inventory.SaveChanges();
            Main = main.Uuid;

            var partners = new Mock<IPartnerRoleLookup>();
            partners.Setup(p => p.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((PartnerRoleInfo?)null);
            partners.Setup(p => p.GetAsync(Vendor, It.IsAny<CancellationToken>())).ReturnsAsync(new PartnerRoleInfo(Vendor, "CoolFix Ltd", false, true, true));
            partners.Setup(p => p.GetAsync(Customer, It.IsAny<CancellationToken>())).ReturnsAsync(new PartnerRoleInfo(Customer, "Acme Retail", true, false, true));
            var names = new Mock<ISupplierNameLookupService>();
            names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
                 .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.ToDictionary(i => i, i => i == Customer ? "Acme Retail" : "CoolFix Ltd"));
            var rates = new Mock<IVariantSupplierResolver>();
            rates.Setup(r => r.GetActiveRateAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateOnly>())).ReturnsAsync((ActiveRateInfo?)null);
            PurchaseOrders.Setup(p => p.AddOrIncreaseProductionLineAsync(It.IsAny<CreatePoRequest>(), It.IsAny<int>()))
                .ReturnsAsync((CreatePoRequest r, int _) => new PoConsolidationResult(Guid.NewGuid(), "PO-2026-00042", Guid.NewGuid(), r.Lines!.Single().Quantity, true));
            var jobs = new Mock<IBackgroundJobClient>();
            jobs.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Callback<Job, IState>((job, _) => Jobs.Add(job)).Returns("job");

            var services = new ServiceCollection();
            services.AddSingleton(Material);
            services.AddSingleton(Inventory);
            services.AddSingleton(numbers.Object);
            services.AddSingleton(PurchaseOrders.Object);
            services.AddSingleton(names.Object);
            services.AddSingleton(partners.Object);
            services.AddSingleton(rates.Object);
            services.AddSingleton(jobs.Object);
            services.AddSingleton(SoListener.Object);
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<InventoryLedgerService>>(NullLogger<InventoryLedgerService>.Instance);
            services.AddSingleton<IInventoryLedgerService, InventoryLedgerService>();
            services.AddSingleton<IStockReservationService, StockReservationService>();
            services.AddSingleton<IAllocationRunListener, ProductionReadinessListener>();
            services.AddSingleton<IAllocationReceiptListener, ProductionReadinessListener>();
            services.AddSingleton<IAllocationRunListener, ServiceReadinessListener>();
            services.AddSingleton<IAllocationReceiptListener, ServiceReadinessListener>();
            services.AddSingleton<IAllocationEngine, AllocationEngine>();
            services.AddSingleton<ISupplyRequirementEngine, SupplyRequirementEngine>();
            services.AddSingleton<ServiceOrderService>();
            services.AddSingleton<IServiceOrderService>(sp => sp.GetRequiredService<ServiceOrderService>());
            services.AddSingleton<IServiceOrderDemandService>(sp => sp.GetRequiredService<ServiceOrderService>());
            var provider = services.BuildServiceProvider();

            Boms   = new BomRepository(Material, Inventory, numbers.Object, partners: partners.Object, supplierNames: names.Object);
            Orders = provider.GetRequiredService<IServiceOrderService>();
            Demand = provider.GetRequiredService<IServiceOrderDemandService>();
            Engine = provider.GetRequiredService<IAllocationEngine>();
        }

        public (Guid Product, Guid Variant) Product(string name, string type = ProductType.Consumable, string uom = "PCS",
            bool hasServiceBom = false, string? invoicing = null, decimal? hours = null, Guid? defaultSupplier = null)
        {
            var product = new Product
            {
                Uuid = Guid.NewGuid(), Sku = $"SKU-{Guid.NewGuid():N}"[..12], Name = name, UomCode = uom, ProductType = type,
                SupplyMethod = ProductTypeRules.DefaultSupplyMethod(type), IsStockable = type != ProductType.Service,
                HasServiceBom = hasServiceBom, ServiceInvoicingPolicy = invoicing, EstimatedDurationHours = hours, IsActive = true, CreatedBy = 1
            };
            product.Variants.Add(new ProductVariant
            {
                Uuid = Guid.NewGuid(), Sku = $"{product.Sku}-1", VariantName = "Default", IsDefault = true, IsActive = true, PurchasePrice = 5m,
                SellingPrice = 50m, IsAvailableForProduction = true, IsAvailableForServices = true, DefaultSupplierId = defaultSupplier, CreatedBy = 1
            });
            Inventory.Products.Add(product);
            Inventory.SaveChanges();
            return (product.Uuid, product.Variants.Single().Uuid);
        }

        public void Stock(Guid variantUuid, decimal qty)
        {
            var variantId = Inventory.ProductVariants.First(v => v.Uuid == variantUuid).Id;
            var warehouseId = Inventory.Warehouses.First(w => w.Uuid == Main).Id;
            Inventory.InventoryItems.Add(new InventoryItem { Uuid = Guid.NewGuid(), VariantId = variantId, WarehouseId = warehouseId, QtyOnHand = qty, UnitCost = 3m });
            Inventory.SaveChanges();
        }

        public (decimal OnHand, decimal Reserved) Item(Guid variantUuid)
        {
            var variantId = Inventory.ProductVariants.First(v => v.Uuid == variantUuid).Id;
            var rows = Inventory.InventoryItems.AsNoTracking().Where(i => i.VariantId == variantId).ToList();
            return (rows.Sum(r => r.QtyOnHand), rows.Sum(r => r.QtyReserved));
        }

        public async Task ActiveBomAsync(Guid service, params BomLineRequest[] lines)
        {
            var uuid = await Boms.CreateAsync(new CreateBomRequest { ProductUuid = service, BaseQuantity = 1m, Lines = [.. lines] }, Tech);
            await Boms.SubmitAsync(uuid, Tech);
            await Boms.ApproveAsync(uuid, Tech + 1);
            await Boms.ActivateAsync(uuid, Tech + 1);
        }

        public Task<Guid> CreateAsync(Guid service, decimal qty = 1m, int? assignee = Tech) => Orders.CreateAsync(new CreateServiceOrderRequest
        {
            ServiceProductUuid = service, CustomerUuid = Customer, Quantity = qty, WarehouseUuid = Main,
            AssignedUserId = assignee, ScheduledDate = Today, ScheduledTime = "09:30", Priority = 1, Notes = "ground floor"
        }, Tech);

        /// <summary>Oil change: 1 filter (critical), 4 L oil + 10% scrap (critical), 1 washer (non-critical).</summary>
        public async Task<(Guid Service, Guid Filter, Guid Oil, Guid Washer)> OilChangeAsync(string? invoicing = null)
        {
            var (service, _) = Product("Oil Change", ProductType.Service, "HR", hasServiceBom: true, invoicing: invoicing, hours: 1.5m);
            var (_, filter)  = Product("Oil Filter");
            var (_, oil)     = Product("Engine Oil", uom: "L", defaultSupplier: Vendor);
            var (_, washer)  = Product("Drain Plug Washer");
            await ActiveBomAsync(service,
                new BomLineRequest { MaterialVariantUuid = filter, Quantity = 1m, IsCritical = true },
                new BomLineRequest { MaterialVariantUuid = oil, Quantity = 4m, ScrapPercentage = 10m, IsCritical = true },
                new BomLineRequest { MaterialVariantUuid = washer, Quantity = 1m, IsCritical = false });
            return (service, filter, oil, washer);
        }

        public List<TimelineEvent> Events() => Jobs.Where(j => j.Method.Name == "AppendAsync").Select(j => (TimelineEvent)j.Args[1]).ToList();
    }

    private static ServiceMaterialModel M(ServiceOrderDetailModel d, Guid variant) => d.Materials.Single(m => m.VariantUuid == variant && m.Status != SmrStatus.Cancelled);

    // ── Create (SVC-01, D-5) ──────────────────────────────────────────────────

    [Fact]
    public async Task Create_makes_a_draft_with_product_defaults_and_the_contract_shapes()
    {
        var h = new Harness();
        var (service, _) = h.Product("Oil Change", ProductType.Service, "HR", invoicing: ServiceInvoicingPolicy.CostPlus, hours: 1.5m);

        var d = (await h.Orders.GetAsync(await h.CreateAsync(service, qty: 2m)))!;
        d.ServiceNumber.Should().Be("SVC-2026-00001");
        d.Status.Should().Be(ServiceOrderStatus.Draft);
        d.MaterialReadiness.Should().Be(ServiceReadinessCode.NotChecked);
        d.InvoicingPolicy.Should().Be(ServiceInvoicingPolicy.CostPlus);
        d.BillingModel.Should().Be(ServiceBillingModel.Inclusive);
        d.EstimatedHours.Should().Be(3m, "1.5 h × 2");
        d.ScheduledDate.Should().Be(Today);
        d.ScheduledTime.Should().Be("09:30");
        d.CustomerName.Should().Be("Acme Retail");
        d.SourceType.Should().Be(ServiceOrderSource.Manual);
        d.AllowedActions.Should().Equal("EDIT", "PLAN", "CANCEL");
        h.Events().Single().EventType.Should().Be(ServiceTimelineEventTypes.Created);
        h.Events().Single().InterfaceCode.Should().Be("SERVICE_ORDER");
        h.Events().Single().DocumentId.Should().Be(d.Uuid);
    }

    [Fact]
    public async Task Create_refuses_a_non_service_product_a_non_customer_and_a_time_without_a_date()
    {
        var h = new Harness();
        var (part, _) = h.Product("Oil Filter");
        var (service, _) = h.Product("Oil Change", ProductType.Service, "HR");

        await FluentActions.Awaiting(() => h.CreateAsync(part)).Should().ThrowAsync<BadRequestException>().WithMessage("*not a service product*");
        await FluentActions.Awaiting(() => h.Orders.CreateAsync(new CreateServiceOrderRequest
            { ServiceProductUuid = service, CustomerUuid = h.Vendor, Quantity = 1, WarehouseUuid = h.Main }, Tech))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*customer*");
        await FluentActions.Awaiting(() => h.Orders.CreateAsync(new CreateServiceOrderRequest
            { ServiceProductUuid = service, CustomerUuid = h.Customer, Quantity = 1, WarehouseUuid = h.Main, ScheduledTime = "10:00" }, Tech))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*needs a scheduled date*");
    }

    // ── Plan (TS-07, TS-08, TS-09, TS-21, TS-29, SVC-03) ──────────────────────

    [Fact]
    public async Task TS07_TS29_plan_with_stock_explodes_at_quantity_reserves_everything_and_is_ready()
    {
        var h = new Harness();
        var (service, filter, oil, washer) = await h.OilChangeAsync();
        h.Stock(filter, 10); h.Stock(oil, 50); h.Stock(washer, 10);

        var d = await h.Orders.PlanAsync(await h.CreateAsync(service, qty: 3m), Tech);

        d.Status.Should().Be(ServiceOrderStatus.Ready);
        d.MaterialReadiness.Should().Be(ServiceReadinessCode.Ready);
        d.BomNumber.Should().Be("BOM-2026-00001");
        d.BomVersion.Should().Be(1);
        M(d, filter).RequiredQuantity.Should().Be(3m);
        M(d, oil).NetQuantity.Should().Be(12m, "4 L × 3");
        M(d, oil).ScrapAllowance.Should().Be(1.2m);
        M(d, oil).RequiredQuantity.Should().Be(13.2m);
        M(d, oil).ReservedQuantity.Should().Be(13.2m);
        M(d, oil).Status.Should().Be(SmrStatus.FullyReserved);
        M(d, oil).AvailableQuantity.Should().Be(50m - 13.2m);
        M(d, washer).IsCritical.Should().BeFalse();
        h.Item(oil).Reserved.Should().Be(13.2m);
        d.AllowedActions.Should().Equal("EDIT", "START", "CANCEL");
        h.Events().Select(e => e.EventType).Should().Contain(ServiceTimelineEventTypes.Planned);
    }

    [Fact]
    public async Task TS08_partial_shortage_is_material_pending_and_raises_a_supply_requirement()
    {
        var h = new Harness();
        var (service, filter, oil, washer) = await h.OilChangeAsync();
        h.Stock(filter, 10); h.Stock(oil, 2); h.Stock(washer, 10);

        var d = await h.Orders.PlanAsync(await h.CreateAsync(service), Tech);

        d.Status.Should().Be(ServiceOrderStatus.MaterialPending);
        d.MaterialReadiness.Should().Be(ServiceReadinessCode.Partial);
        M(d, oil).ReservedQuantity.Should().Be(2m);
        M(d, oil).ShortageQuantity.Should().Be(2.4m);
        M(d, oil).Status.Should().Be(SmrStatus.PartiallyReserved);
        M(d, oil).SupplyRequirementStatus.Should().Be(SupplyRequirementStatus.Ordered);
        var sr = h.Material.SupplyRequirements.Single();
        sr.DemandSourceType.Should().Be(SupplyDemandSourceType.ServiceOrder);
        sr.DemandReference.Should().Be(d.ServiceNumber);
        sr.QuantityRequired.Should().Be(2.4m);
        sr.TraceId.Should().Be(d.TraceId);
        d.AllowedActions.Should().Equal("EDIT", "CANCEL");
    }

    [Fact]
    public async Task TS09_plan_without_a_BOM_is_not_applicable_and_ready_and_so_is_an_enabled_service_with_no_active_BOM()
    {
        var h = new Harness();
        var (plain, _)   = h.Product("House Cleaning", ProductType.Service, "HR");
        var (enabled, _) = h.Product("AC Service", ProductType.Service, "HR", hasServiceBom: true);

        foreach (var service in new[] { plain, enabled })
        {
            var d = await h.Orders.PlanAsync(await h.CreateAsync(service), Tech);
            d.Status.Should().Be(ServiceOrderStatus.Ready);
            d.MaterialReadiness.Should().Be(ServiceReadinessCode.NotApplicable);
            d.Materials.Should().BeEmpty();
            d.BomId.Should().BeNull();
        }
    }

    [Fact]
    public async Task SVC03_planning_needs_an_assignee_and_only_a_draft_plans()
    {
        var h = new Harness();
        var (service, _) = h.Product("House Cleaning", ProductType.Service, "HR");
        var uuid = await h.CreateAsync(service, assignee: null);

        await FluentActions.Awaiting(() => h.Orders.PlanAsync(uuid, Tech)).Should().ThrowAsync<BadRequestException>().WithMessage("Assign a technician*");

        var ready = await h.CreateAsync(service);
        await h.Orders.PlanAsync(ready, Tech);
        await FluentActions.Awaiting(() => h.Orders.PlanAsync(ready, Tech)).Should().ThrowAsync<BadRequestException>().WithMessage("*only a draft*");
    }

    [Fact]
    public async Task TS21_subcontract_line_is_bought_from_its_vendor_and_labour_is_tracking_only()
    {
        var h = new Harness();
        var (service, _) = h.Product("AC Installation", ProductType.Service, "HR", hasServiceBom: true);
        var (_, bracket) = h.Product("Bracket");
        var (_, gas)     = h.Product("Gas Refill (outsourced)", ProductType.Service, "EA");
        var (_, labor)   = h.Product("Technician Hour", ProductType.Service, "HR");
        await h.ActiveBomAsync(service,
            new BomLineRequest { MaterialVariantUuid = bracket, Quantity = 2m, IsCritical = true },
            new BomLineRequest { MaterialVariantUuid = gas, Quantity = 1m, SourceType = BomLineSourceType.Subcontract, SubcontractSupplierUuid = h.Vendor, IsCritical = true },
            new BomLineRequest { MaterialVariantUuid = labor, Quantity = 3m, SourceType = BomLineSourceType.InternalLabor, Uom = "HR" });
        h.Stock(bracket, 5);

        var d = await h.Orders.PlanAsync(await h.CreateAsync(service), Tech);

        d.Status.Should().Be(ServiceOrderStatus.Ready, "the subcontract is ordered and labour never blocks");
        M(d, gas).SourceType.Should().Be(BomLineSourceType.Subcontract);
        M(d, gas).SupplyRequirementStatus.Should().Be(SupplyRequirementStatus.Ordered);
        M(d, gas).ShortageQuantity.Should().Be(0m);
        M(d, labor).IsCritical.Should().BeFalse();
        M(d, labor).ShortageQuantity.Should().Be(0m);
        M(d, labor).RequiredQuantity.Should().Be(3m);
        h.PurchaseOrders.Verify(p => p.AddOrIncreaseProductionLineAsync(
            It.Is<CreatePoRequest>(r => r.SupplierId == h.Vendor && r.Lines!.Single().VariantUuid == gas && r.Title!.StartsWith("Service supply")), Tech), Times.Once);
        h.Material.SupplyRequirements.Single().SupplyMethod.Should().Be(SupplyMethod.Purchase);
    }

    // ── Start, ad-hoc, waiting (TS-10..13, TS-32, TS-33) ──────────────────────

    [Fact]
    public async Task TS10_start_issues_everything_held_in_one_issue()
    {
        var h = new Harness();
        var (service, filter, oil, washer) = await h.OilChangeAsync();
        h.Stock(filter, 10); h.Stock(oil, 50); h.Stock(washer, 10);
        var uuid = await h.CreateAsync(service);
        await h.Orders.PlanAsync(uuid, Tech);

        var d = await h.Orders.StartAsync(uuid, Tech);

        d.Status.Should().Be(ServiceOrderStatus.InProgress);
        d.ActualStartDate.Should().NotBeNull();
        M(d, oil).IssuedQuantity.Should().Be(4.4m);
        M(d, oil).Status.Should().Be(SmrStatus.Issued);
        h.Item(oil).Should().Be((45.6m, 0m));
        var issue = h.Material.ServiceMaterialIssues.Include(i => i.Lines).Single();
        issue.IssueType.Should().Be(ServiceIssueType.Issue);
        issue.Lines.Should().HaveCount(3);
        h.Inventory.InventoryLedgerEntries.Count(e => e.TransactionType == InventoryTransactionType.ServiceIssue).Should().Be(3);
        d.AllowedActions.Should().Equal("EDIT", "ADD_MATERIAL", "COMPLETE", "CANCEL");
        h.Events().Select(e => e.EventType).Should().Contain([ServiceTimelineEventTypes.Started, ServiceTimelineEventTypes.MaterialIssued]);

        await FluentActions.Awaiting(() => h.Orders.StartAsync(uuid, Tech)).Should().ThrowAsync<BadRequestException>().WithMessage("*only a ready*");
    }

    [Fact]
    public async Task TS11_TS12_TS13_TS33_adhoc_material_issues_at_once_or_waits_then_resumes_and_issues_again()
    {
        var h = new Harness();
        var (service, _) = h.Product("Plumbing", ProductType.Service, "HR");
        var (_, pipe)    = h.Product("Copper Pipe", uom: "M", defaultSupplier: h.Vendor);
        var (_, tape)    = h.Product("PTFE Tape");
        h.Stock(tape, 5);
        var uuid = await h.CreateAsync(service);
        await h.Orders.PlanAsync(uuid, Tech);
        await h.Orders.StartAsync(uuid, Tech);

        // TS-11 — in stock: held and issued at once.
        var d = await h.Orders.AddMaterialAsync(uuid, new AddAdhocMaterialRequest { VariantUuid = tape, Quantity = 2, Notes = "leak" }, Tech);
        d.Status.Should().Be(ServiceOrderStatus.InProgress);
        M(d, tape).IsAdhoc.Should().BeTrue();
        M(d, tape).IssuedQuantity.Should().Be(2m);
        M(d, tape).CanRemove.Should().BeFalse("it was issued");

        // TS-12 — none in stock: supply raised, the job waits.
        d = await h.Orders.AddMaterialAsync(uuid, new AddAdhocMaterialRequest { VariantUuid = pipe, Quantity = 3 }, Tech);
        d.Status.Should().Be(ServiceOrderStatus.Waiting);
        M(d, pipe).ShortageQuantity.Should().Be(3m);
        M(d, pipe).SupplyRequirementStatus.Should().Be(SupplyRequirementStatus.Ordered);
        M(d, pipe).CanRemove.Should().BeTrue();
        d.AllowedActions.Should().Equal("EDIT", "ADD_MATERIAL", "CANCEL");
        h.Events().Select(e => e.EventType).Should().Contain(ServiceTimelineEventTypes.Waiting);

        // TS-13 — the goods arrive and allocation runs: the listener resumes the job (ST-07).
        h.Stock(pipe, 10);
        await h.Engine.AllocateAsync(pipe, null, Tech);
        d = (await h.Orders.GetAsync(uuid))!;
        d.Status.Should().Be(ServiceOrderStatus.InProgress);
        M(d, pipe).ReservedQuantity.Should().Be(3m);

        // An add issues only the new line…
        var issueCount = h.Material.ServiceMaterialIssues.Count();
        d = await h.Orders.AddMaterialAsync(uuid, new AddAdhocMaterialRequest { VariantUuid = tape, Quantity = 1 }, Tech);
        M(d, pipe).IssuedQuantity.Should().Be(0m, "an add issues only the new line");
        h.Material.ServiceMaterialIssues.Count().Should().Be(issueCount + 1);

        // TS-33 (A36-P5-10 QA regression) — …so "Reserve all available" must work IN_PROGRESS: it issues what is now held,
        // otherwise the resumed job's late material could never be issued.
        d = await h.Orders.AllocateAsync(uuid, Tech);
        d.Status.Should().Be(ServiceOrderStatus.InProgress);
        M(d, pipe).IssuedQuantity.Should().Be(3m);
        M(d, pipe).Status.Should().Be(SmrStatus.Issued);
        h.Material.ServiceMaterialIssues.Count().Should().Be(issueCount + 2, "a second issue document for the held pipe");
    }

    [Fact]
    public async Task Allocate_while_waiting_reserves_and_issues_what_arrived()
    {
        var h = new Harness();
        var (service, _) = h.Product("Plumbing", ProductType.Service, "HR");
        var (_, pipe)    = h.Product("Copper Pipe", uom: "M");
        var uuid = await h.CreateAsync(service);
        await h.Orders.PlanAsync(uuid, Tech);
        await h.Orders.StartAsync(uuid, Tech);
        (await h.Orders.AddMaterialAsync(uuid, new AddAdhocMaterialRequest { VariantUuid = pipe, Quantity = 3 }, Tech)).Status.Should().Be(ServiceOrderStatus.Waiting);
        h.Material.SupplyRequirements.Single().Status.Should().Be(SupplyRequirementStatus.Open, "no supplier known: left for a person");

        h.Stock(pipe, 3);
        var d = await h.Orders.AllocateAsync(uuid, Tech);

        d.Status.Should().Be(ServiceOrderStatus.InProgress);
        M(d, pipe).IssuedQuantity.Should().Be(3m);
        M(d, pipe).Status.Should().Be(SmrStatus.Issued);
        h.Material.SupplyRequirements.Single().Status.Should().Be(SupplyRequirementStatus.Cancelled, "the unacted paperwork is dropped once covered");
    }

    [Fact]
    public async Task Adhoc_rules_only_while_running_never_a_service_and_positive()
    {
        var h = new Harness();
        var (service, _) = h.Product("Plumbing", ProductType.Service, "HR");
        var (_, other)   = h.Product("Other Service", ProductType.Service, "HR");
        var (_, tape)    = h.Product("PTFE Tape");
        var uuid = await h.CreateAsync(service);
        await h.Orders.PlanAsync(uuid, Tech);

        await FluentActions.Awaiting(() => h.Orders.AddMaterialAsync(uuid, new AddAdhocMaterialRequest { VariantUuid = tape, Quantity = 1 }, Tech))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*during service execution*");
        await h.Orders.StartAsync(uuid, Tech);
        await FluentActions.Awaiting(() => h.Orders.AddMaterialAsync(uuid, new AddAdhocMaterialRequest { VariantUuid = other, Quantity = 1 }, Tech))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*is a service*");
        await FluentActions.Awaiting(() => h.Orders.AddMaterialAsync(uuid, new AddAdhocMaterialRequest { VariantUuid = tape, Quantity = 0 }, Tech))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task TS32_an_unissued_adhoc_material_is_hard_deleted_and_its_hold_and_supply_released()
    {
        var h = new Harness();
        var (service, filter, oil, washer) = await h.OilChangeAsync();
        h.Stock(filter, 10); h.Stock(oil, 50); h.Stock(washer, 10);
        var (_, pipe) = h.Product("Copper Pipe", uom: "M", defaultSupplier: h.Vendor);
        var uuid = await h.CreateAsync(service);
        await h.Orders.PlanAsync(uuid, Tech);
        await h.Orders.StartAsync(uuid, Tech);
        var d = await h.Orders.AddMaterialAsync(uuid, new AddAdhocMaterialRequest { VariantUuid = pipe, Quantity = 3 }, Tech);
        d.Status.Should().Be(ServiceOrderStatus.Waiting);

        d = await h.Orders.RemoveMaterialAsync(uuid, M(d, pipe).Uuid, Tech);

        d.Status.Should().Be(ServiceOrderStatus.InProgress);
        d.Materials.Should().NotContain(m => m.VariantUuid == pipe);
        h.Material.ServiceMaterialRequirements.Count(m => m.MaterialVariantUuid == pipe).Should().Be(0, "hard delete");
        h.Material.SupplyRequirements.Single().Status.Should().Be(SupplyRequirementStatus.Cancelled);
        (await h.Engine.GetDemandsAsync(demandType: AllocationDemandType.ServiceOrder, variantUuid: pipe)).Should().BeEmpty();

        await FluentActions.Awaiting(() => h.Orders.RemoveMaterialAsync(uuid, M(d, oil).Uuid, Tech))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Only ad-hoc*");
        var (_, tape) = h.Product("PTFE Tape");
        h.Stock(tape, 5);
        d = await h.Orders.AddMaterialAsync(uuid, new AddAdhocMaterialRequest { VariantUuid = tape, Quantity = 1 }, Tech);
        await FluentActions.Awaiting(() => h.Orders.RemoveMaterialAsync(uuid, M(d, tape).Uuid, Tech))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*already issued*");
    }

    // ── Completion & ledger (TS-14, TS-15, TS-24..28, SVC-COMP-01..06) ────────

    private static async Task<(Harness H, Guid Uuid, Guid Filter, Guid Oil, Guid Washer)> StartedOilChangeAsync(string? invoicing = null)
    {
        var h = new Harness();
        var (service, filter, oil, washer) = await h.OilChangeAsync(invoicing);
        h.Stock(filter, 10); h.Stock(oil, 50); h.Stock(washer, 10);
        var uuid = await h.CreateAsync(service);
        await h.Orders.PlanAsync(uuid, Tech);
        await h.Orders.StartAsync(uuid, Tech);
        return (h, uuid, filter, oil, washer);
    }

    private static CompleteServiceOrderRequest Consume(ServiceOrderDetailModel d, params (Guid Variant, decimal Qty)[] lines) => new()
    {
        ConsumedMaterials = lines.Select(l => new ConsumedMaterialRequest { SmrUuid = M(d, l.Variant).Uuid, ConsumedQuantity = l.Qty }).ToList(),
        ActualHours = 1.5m, CompletionNotes = "Done.", CustomerSignature = true
    };

    [Fact]
    public async Task TS14_TS24_complete_with_everything_consumed_writes_one_debit_per_material_and_no_returns()
    {
        var (h, uuid, filter, oil, washer) = await StartedOilChangeAsync();
        var started = (await h.Orders.GetAsync(uuid))!;

        var d = await h.Orders.CompleteAsync(uuid, Consume(started, (filter, 1), (oil, 4.4m), (washer, 1)), Tech);

        d.Status.Should().Be(ServiceOrderStatus.Completed);
        d.ActualHours.Should().Be(1.5m);
        d.CustomerSignature.Should().BeTrue();
        d.CompletionNotes.Should().Be("Done.");
        d.Materials.Should().OnlyContain(m => m.Status == SmrStatus.Consumed);
        d.Ledger.Should().HaveCount(3).And.OnlyContain(e => e.EntryType == "DEBIT" && e.Quantity > 0 && e.MovementType == "SERVICE_ISSUE");
        h.Material.ServiceMaterialIssues.Count(i => i.IssueType == ServiceIssueType.Return).Should().Be(0);
        h.Material.ServiceLedgerEntries.Should().OnlyContain(e => e.TraceId == d.TraceId && e.ServiceNumber == d.ServiceNumber);
        d.AllowedActions.Should().Equal("EDIT", "CLOSE");
        h.Events().Select(e => e.EventType).Should().Contain(ServiceTimelineEventTypes.Completed);
    }

    [Fact]
    public async Task TS15_TS25_TS26_partial_consumption_returns_the_rest_and_the_ledger_nets_to_consumption()
    {
        var (h, uuid, filter, oil, washer) = await StartedOilChangeAsync();
        var started = (await h.Orders.GetAsync(uuid))!;

        var d = await h.Orders.CompleteAsync(uuid, Consume(started, (filter, 1), (oil, 3.5m), (washer, 0)), Tech);

        M(d, oil).ConsumedQuantity.Should().Be(3.5m);
        M(d, oil).ReturnedQuantity.Should().Be(0.9m);
        M(d, oil).Status.Should().Be(SmrStatus.Consumed);
        M(d, washer).Status.Should().Be(SmrStatus.Returned);
        h.Item(oil).OnHand.Should().Be(50m - 3.5m, "the unused 0.9 L went back");
        h.Inventory.InventoryLedgerEntries.Count(e => e.TransactionType == InventoryTransactionType.ServiceReturn).Should().Be(2);

        var ledger = await h.Orders.GetLedgerAsync(uuid);
        ledger.Entries.Should().HaveCount(5);
        ledger.Entries.Where(e => e.MovementType == "SERVICE_RETURN").Should().OnlyContain(e => e.Quantity < 0 && e.SourceDocumentType == "SERVICE_RETURN");
        ledger.Entries.Select(e => e.TransactionDate).Should().BeInAscendingOrder();
        ledger.NetByProduct.Single(n => n.VariantUuid == oil).NetQuantity.Should().Be(3.5m);
        ledger.NetByProduct.Single(n => n.VariantUuid == washer).NetQuantity.Should().Be(0m);
    }

    [Fact]
    public async Task SVC_COMP_rules_are_enforced_with_the_FSD_messages()
    {
        var (h, uuid, filter, oil, washer) = await StartedOilChangeAsync(ServiceInvoicingPolicy.TimeAndMaterial);
        var d = (await h.Orders.GetAsync(uuid))!;

        await FluentActions.Awaiting(() => h.Orders.CompleteAsync(uuid, Consume(d, (filter, 2), (oil, 1), (washer, 1)), Tech))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Consumed quantity cannot exceed issued quantity");
        await FluentActions.Awaiting(() => h.Orders.CompleteAsync(uuid, Consume(d, (filter, -1), (oil, 1), (washer, 1)), Tech))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Consumed quantity cannot be negative");
        await FluentActions.Awaiting(() => h.Orders.CompleteAsync(uuid, Consume(d, (filter, 1), (oil, 1)), Tech))
            .Should().ThrowAsync<BadRequestException>().WithMessage("All issued materials must have consumed quantity confirmed");
        var noHours = Consume(d, (filter, 1), (oil, 1), (washer, 1));
        noHours.ActualHours = null;
        await FluentActions.Awaiting(() => h.Orders.CompleteAsync(uuid, noHours, Tech))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Actual hours required for Time & Material billing");

        await h.Orders.CompleteAsync(uuid, Consume(d, (filter, 1), (oil, 1), (washer, 1)), Tech);
        await FluentActions.Awaiting(() => h.Orders.CompleteAsync(uuid, Consume(d, (filter, 1), (oil, 1), (washer, 1)), Tech))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Service can only be completed from In Progress status");
    }

    [Fact]
    public async Task TS28_labour_only_service_completes_with_an_empty_ledger_and_its_hours()
    {
        var h = new Harness();
        var (service, _) = h.Product("House Cleaning", ProductType.Service, "HR", invoicing: ServiceInvoicingPolicy.TimeAndMaterial);
        var uuid = await h.CreateAsync(service);
        await h.Orders.PlanAsync(uuid, Tech);
        await h.Orders.StartAsync(uuid, Tech);

        var d = await h.Orders.CompleteAsync(uuid, new CompleteServiceOrderRequest { ActualHours = 4m, CustomerSignature = true }, Tech);

        d.Status.Should().Be(ServiceOrderStatus.Completed);
        d.ActualHours.Should().Be(4m);
        d.Ledger.Should().BeEmpty();
        (await h.Orders.GetLedgerAsync(uuid)).NetByProduct.Should().BeEmpty();
    }

    [Fact]
    public async Task Ledger_entries_are_immutable_and_the_service_has_no_way_to_change_them()
    {
        var (h, uuid, filter, oil, washer) = await StartedOilChangeAsync();
        var d = (await h.Orders.GetAsync(uuid))!;
        await h.Orders.CompleteAsync(uuid, Consume(d, (filter, 1), (oil, 4.4m), (washer, 1)), Tech);

        var entry = h.Material.ServiceLedgerEntries.First();
        entry.Quantity = 99m;
        FluentActions.Invoking(() => h.Material.SaveChanges()).Should().Throw<InvalidOperationException>().WithMessage("*immutable*");
        h.Material.Entry(entry).State = EntityState.Unchanged;
        h.Material.ServiceLedgerEntries.Remove(entry);
        await FluentActions.Awaiting(() => h.Material.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>();
        h.Material.Entry(entry).State = EntityState.Unchanged;

        typeof(IServiceOrderService).GetMethods().Where(m => m.Name.Contains("Ledger")).Select(m => m.Name).Should().Equal("GetLedgerAsync");
    }

    // ── Close / cancel (D-13, TS-16, TS-30, D-16) ─────────────────────────────

    [Fact]
    public async Task Close_moves_only_a_completed_order()
    {
        var (h, uuid, filter, oil, washer) = await StartedOilChangeAsync();
        await FluentActions.Awaiting(() => h.Orders.CloseAsync(uuid, Tech)).Should().ThrowAsync<BadRequestException>().WithMessage("*only a completed*");
        var d = (await h.Orders.GetAsync(uuid))!;
        await h.Orders.CompleteAsync(uuid, Consume(d, (filter, 1), (oil, 4.4m), (washer, 1)), Tech);

        d = await h.Orders.CloseAsync(uuid, Tech);
        d.Status.Should().Be(ServiceOrderStatus.Closed);
        d.AllowedActions.Should().Equal("EDIT");
    }

    [Fact]
    public async Task TS16_cancel_in_material_pending_releases_holds_and_cancels_supply()
    {
        var h = new Harness();
        var (service, filter, oil, washer) = await h.OilChangeAsync();
        h.Stock(filter, 10); h.Stock(oil, 2); h.Stock(washer, 10);
        var uuid = await h.CreateAsync(service);
        (await h.Orders.PlanAsync(uuid, Tech)).Status.Should().Be(ServiceOrderStatus.MaterialPending);

        await FluentActions.Awaiting(() => h.Orders.CancelAsync(uuid, " ", Tech)).Should().ThrowAsync<BadRequestException>().WithMessage("A reason is required*");
        var d = await h.Orders.CancelAsync(uuid, "Customer postponed", Tech);

        d.Status.Should().Be(ServiceOrderStatus.Cancelled);
        d.Materials.Should().OnlyContain(m => m.Status == SmrStatus.Cancelled);
        d.Notes.Should().EndWith("Cancelled: Customer postponed");
        h.Item(oil).Reserved.Should().Be(0m);
        h.Item(filter).Reserved.Should().Be(0m);
        h.Material.SupplyRequirements.Single().Status.Should().Be(SupplyRequirementStatus.Cancelled);
        d.AllowedActions.Should().BeEmpty();
        await FluentActions.Awaiting(() => h.Orders.CancelAsync(uuid, "again", Tech)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task TS30_cancel_after_issue_returns_the_issued_stock_automatically()
    {
        var (h, uuid, filter, oil, _) = await StartedOilChangeAsync();
        h.Item(oil).OnHand.Should().Be(45.6m);

        var d = await h.Orders.CancelAsync(uuid, "Wrong vehicle", Tech);

        d.Status.Should().Be(ServiceOrderStatus.Cancelled);
        h.Item(oil).Should().Be((50m, 0m));
        h.Item(filter).OnHand.Should().Be(10m);
        d.Materials.Single(m => m.VariantUuid == oil).Status.Should().Be(SmrStatus.Returned);
        h.Material.ServiceMaterialIssues.Single(i => i.IssueType == ServiceIssueType.Return).Should().NotBeNull();
        d.Ledger.Should().BeEmpty("the ledger is written at completion only");
    }

    // ── Update / concurrency (SVC-12, SVC-13) ─────────────────────────────────

    [Fact]
    public async Task Update_takes_everything_in_draft_clears_nulls_and_only_notes_later()
    {
        var h = new Harness();
        var (service, _) = h.Product("House Cleaning", ProductType.Service, "HR");
        var uuid = await h.CreateAsync(service);
        var d = (await h.Orders.GetAsync(uuid))!;

        d = await h.Orders.UpdateAsync(uuid, new UpdateServiceOrderRequest
        {
            CustomerUuid = h.Customer, Quantity = 2, WarehouseUuid = h.Main, AssignedUserId = null, AssignedRoleId = 3,
            ScheduledDate = null, ScheduledTime = null, Priority = 3, Notes = "new", RowVersion = d.RowVersion
        }, Tech);
        d.Quantity.Should().Be(2m);
        d.AssignedUserId.Should().BeNull();
        d.AssignedRoleId.Should().Be(3);
        d.ScheduledDate.Should().BeNull();
        d.Priority.Should().Be(3);

        await h.Orders.PlanAsync(uuid, Tech);
        await h.Orders.StartAsync(uuid, Tech);
        d = (await h.Orders.GetAsync(uuid))!;
        d = await h.Orders.UpdateAsync(uuid, new UpdateServiceOrderRequest
        {
            CustomerUuid = h.Customer, Quantity = 9, WarehouseUuid = h.Main, Priority = 0, Notes = "only this", RowVersion = d.RowVersion
        }, Tech);
        d.Quantity.Should().Be(2m);
        d.Priority.Should().Be(3);
        d.Notes.Should().Be("only this");
    }

    [Fact]
    public async Task SVC13_a_stale_row_version_is_a_conflict()
    {
        var h = new Harness();
        var (service, _) = h.Product("House Cleaning", ProductType.Service, "HR");
        var uuid = await h.CreateAsync(service);

        await FluentActions.Awaiting(() => h.Orders.UpdateAsync(uuid, new UpdateServiceOrderRequest
            { CustomerUuid = h.Customer, Quantity = 1, WarehouseUuid = h.Main, Priority = 1, RowVersion = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]) }, Tech))
            .Should().ThrowAsync<ConflictException>();
    }

    // ── Sale orders (D-10, TS-06) ──────────────────────────────────────────────

    [Fact]
    public async Task TS06_ensure_for_a_sale_order_line_is_idempotent_and_listed_in_creation_order()
    {
        var h = new Harness();
        var (service, variant) = h.Product("Installation", ProductType.Service, "HR", hours: 2m);
        var (_, part) = h.Product("Oil Filter");
        var so = Guid.NewGuid(); var line1 = Guid.NewGuid(); var line2 = Guid.NewGuid(); var trace = Guid.NewGuid();

        var a = await h.Demand.EnsureForSaleOrderLineAsync(so, line1, "SO-2026-00001", h.Customer, variant, 2m, DateTime.UtcNow.Date, null, Tech, trace);
        var again = await h.Demand.EnsureForSaleOrderLineAsync(so, line1, "SO-2026-00001", h.Customer, variant, 2m, DateTime.UtcNow.Date, null, Tech, trace);
        var b = await h.Demand.EnsureForSaleOrderLineAsync(so, line2, "SO-2026-00001", h.Customer, variant, 1m, null, null, Tech, trace);

        again.ServiceOrderUuid.Should().Be(a.ServiceOrderUuid);
        var detail = (await h.Orders.GetAsync(a.ServiceOrderUuid))!;
        detail.Status.Should().Be(ServiceOrderStatus.Draft);
        detail.SourceType.Should().Be(ServiceOrderSource.SalesOrder);
        detail.SourceUuid.Should().Be(so);
        detail.SourceLineUuid.Should().Be(line1);
        detail.SourceReference.Should().Be("SO-2026-00001");
        detail.TraceId.Should().Be(trace);
        detail.WarehouseUuid.Should().Be(h.Main, "no warehouse given: the organization's default");
        detail.EstimatedHours.Should().Be(4m);

        var refs = await h.Demand.GetForSaleOrderAsync(new StaticTenantContext().OrganizationId, so);
        refs.Select(r => r.ServiceOrderUuid).Should().Equal(a.ServiceOrderUuid, b.ServiceOrderUuid);
        refs[0].SoLineUuid.Should().Be(line1);

        await FluentActions.Awaiting(() => h.Demand.EnsureForSaleOrderLineAsync(so, Guid.NewGuid(), "SO-2026-00001", h.Customer, part, 1m, null, null, Tech))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*not a service product*");
    }

    [Fact]
    public async Task Sale_order_cancel_cancels_unstarted_orders_keeps_running_ones_and_does_not_call_back()
    {
        var h = new Harness();
        var (_, variant) = h.Product("Installation", ProductType.Service, "HR");
        var so = Guid.NewGuid();
        var notStarted = await h.Demand.EnsureForSaleOrderLineAsync(so, Guid.NewGuid(), "SO-1", h.Customer, variant, 1m, null, null, Tech);
        var running    = await h.Demand.EnsureForSaleOrderLineAsync(so, Guid.NewGuid(), "SO-1", h.Customer, variant, 1m, null, null, Tech);
        var r = (await h.Orders.GetAsync(running.ServiceOrderUuid))!;
        await h.Orders.UpdateAsync(running.ServiceOrderUuid, new UpdateServiceOrderRequest
            { CustomerUuid = h.Customer, Quantity = 1, WarehouseUuid = h.Main, AssignedUserId = Tech, Priority = 1, RowVersion = r.RowVersion }, Tech);
        await h.Orders.PlanAsync(running.ServiceOrderUuid, Tech);
        await h.Orders.StartAsync(running.ServiceOrderUuid, Tech);

        var result = await h.Demand.CancelForSaleOrderAsync(so, "Customer withdrew", Tech);

        result.Cancelled.Select(c => c.ServiceOrderUuid).Should().Equal(notStarted.ServiceOrderUuid);
        result.Cancelled.Single().Status.Should().Be(ServiceOrderStatus.Cancelled);
        result.KeptRunning.Single().Status.Should().Be(ServiceOrderStatus.InProgress);
        h.SoListener.Verify(l => l.OnServiceOrderChangedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        // A later re-ensure of the cancelled line returns it rather than raising a second order.
        var cancelledLine = (await h.Orders.GetAsync(notStarted.ServiceOrderUuid))!.SourceLineUuid!.Value;
        (await h.Demand.EnsureForSaleOrderLineAsync(so, cancelledLine, "SO-1", h.Customer, variant, 1m, null, null, Tech))
            .ServiceOrderUuid.Should().Be(notStarted.ServiceOrderUuid);
    }

    [Fact]
    public async Task The_sale_order_is_told_on_complete_close_and_cancel_and_a_listener_failure_is_swallowed()
    {
        var h = new Harness();
        var (_, variant) = h.Product("Installation", ProductType.Service, "HR");
        var so = Guid.NewGuid(); var line = Guid.NewGuid();
        var o = await h.Demand.EnsureForSaleOrderLineAsync(so, line, "SO-1", h.Customer, variant, 1m, null, null, Tech);
        var d = (await h.Orders.GetAsync(o.ServiceOrderUuid))!;
        await h.Orders.UpdateAsync(o.ServiceOrderUuid, new UpdateServiceOrderRequest
            { CustomerUuid = h.Customer, Quantity = 1, WarehouseUuid = h.Main, AssignedUserId = Tech, Priority = 1, RowVersion = d.RowVersion }, Tech);
        await h.Orders.PlanAsync(o.ServiceOrderUuid, Tech);
        await h.Orders.StartAsync(o.ServiceOrderUuid, Tech);
        h.SoListener.Setup(l => l.OnServiceOrderChangedAsync(It.IsAny<Guid>(), so, line, Tech, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Demand is down"));

        (await h.Orders.CompleteAsync(o.ServiceOrderUuid, new CompleteServiceOrderRequest(), Tech)).Status.Should().Be(ServiceOrderStatus.Completed);
        await h.Orders.CloseAsync(o.ServiceOrderUuid, Tech);

        h.SoListener.Verify(l => l.OnServiceOrderChangedAsync(new StaticTenantContext().OrganizationId, so, line, Tech, It.IsAny<CancellationToken>()), Times.Exactly(2));

        var other = await h.Demand.EnsureForSaleOrderLineAsync(so, Guid.NewGuid(), "SO-1", h.Customer, variant, 1m, null, null, Tech);
        await h.Orders.CancelAsync(other.ServiceOrderUuid, "not needed", Tech);
        h.SoListener.Verify(l => l.OnServiceOrderChangedAsync(It.IsAny<Guid>(), so, It.IsAny<Guid>(), Tech, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // ── Reads ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_filters_by_comma_statuses_and_dates_and_sorts_by_date_then_priority_and_dashboard_buckets()
    {
        var h = new Harness();
        var (service, _) = h.Product("House Cleaning", ProductType.Service, "HR");
        var today = await h.CreateAsync(service);
        var later = await h.Orders.CreateAsync(new CreateServiceOrderRequest
            { ServiceProductUuid = service, CustomerUuid = h.Customer, Quantity = 1, WarehouseUuid = h.Main, ScheduledDate = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd"), Priority = 3 }, Tech);
        var urgentToday = await h.Orders.CreateAsync(new CreateServiceOrderRequest
            { ServiceProductUuid = service, CustomerUuid = h.Customer, Quantity = 1, WarehouseUuid = h.Main, ScheduledDate = Today, Priority = 3, AssignedUserId = Tech }, Tech);
        await h.Orders.PlanAsync(urgentToday, Tech);

        var all = await h.Orders.GetListAsync(new ServiceOrderListFilter());
        all.Data.Select(o => o.Uuid).Should().Equal(urgentToday, today, later);
        (await h.Orders.GetListAsync(new ServiceOrderListFilter { Status = "draft, READY" })).TotalRecords.Should().Be(3);
        (await h.Orders.GetListAsync(new ServiceOrderListFilter { Status = "READY" })).Data.Single().Uuid.Should().Be(urgentToday);
        (await h.Orders.GetListAsync(new ServiceOrderListFilter { FromDate = DateTime.UtcNow.Date, ToDate = DateTime.UtcNow.Date })).TotalRecords.Should().Be(2);
        (await h.Orders.GetListAsync(new ServiceOrderListFilter { Search = "cleaning" })).TotalRecords.Should().Be(3);
        all.Data[0].AssignedUserId.Should().Be(Tech);

        var dash = await h.Orders.GetDashboardAsync(Tech);
        dash.Today.Select(o => o.Uuid).Should().BeEquivalentTo([today, urgentToday]);
        dash.Mine.Select(o => o.Uuid).Should().BeEquivalentTo([today, urgentToday]);
        dash.WaitingForMaterials.Should().BeEmpty();
        dash.CompletionRate.CompletedThisWeek.Should().Be(0);
    }

    [Fact]
    public void Allowed_actions_follow_the_status()
    {
        ServiceOrderService.AllowedActions(ServiceOrderStatus.Draft).Should().Equal("EDIT", "PLAN", "CANCEL");
        ServiceOrderService.AllowedActions(ServiceOrderStatus.MaterialPending).Should().Equal("EDIT", "CANCEL");
        ServiceOrderService.AllowedActions(ServiceOrderStatus.Ready).Should().Equal("EDIT", "START", "CANCEL");
        ServiceOrderService.AllowedActions(ServiceOrderStatus.InProgress).Should().Equal("EDIT", "ADD_MATERIAL", "COMPLETE", "CANCEL");
        ServiceOrderService.AllowedActions(ServiceOrderStatus.Waiting).Should().Equal("EDIT", "ADD_MATERIAL", "CANCEL");
        ServiceOrderService.AllowedActions(ServiceOrderStatus.Completed).Should().Equal("EDIT", "CLOSE");
        ServiceOrderService.AllowedActions(ServiceOrderStatus.Cancelled).Should().BeEmpty();
    }

    [Fact]
    public void Readiness_rules_cover_not_applicable_subcontract_and_running_transitions()
    {
        var order = new ServiceOrder { Status = ServiceOrderStatus.Planned };
        ServiceReadiness.ApplyOrder(order, new Dictionary<Guid, string>());
        order.Status.Should().Be(ServiceOrderStatus.Ready);
        order.MaterialReadiness.Should().Be(ServiceReadinessCode.NotApplicable);

        var sub = new ServiceMaterialRequirement { SourceType = BomLineSourceType.Subcontract, RequiredQuantity = 1, IsCritical = true };
        order.Materials.Add(sub);
        ServiceReadiness.ApplyOrder(order, new Dictionary<Guid, string> { [sub.UUID] = SupplyRequirementStatus.Open });
        order.Status.Should().Be(ServiceOrderStatus.MaterialPending);
        ServiceReadiness.ApplyOrder(order, new Dictionary<Guid, string> { [sub.UUID] = SupplyRequirementStatus.Ordered });
        order.Status.Should().Be(ServiceOrderStatus.Ready);

        order.Status = ServiceOrderStatus.InProgress;
        order.Materials.Add(new ServiceMaterialRequirement { RequiredQuantity = 2, IsCritical = true, IsAdhoc = true });
        ServiceReadiness.ApplyOrder(order, new Dictionary<Guid, string> { [sub.UUID] = SupplyRequirementStatus.Ordered });
        order.Status.Should().Be(ServiceOrderStatus.Waiting);
        order.Materials.Last().ReservedQuantity = 2;
        ServiceReadiness.ApplyOrder(order, new Dictionary<Guid, string> { [sub.UUID] = SupplyRequirementStatus.Ordered });
        order.Status.Should().Be(ServiceOrderStatus.InProgress);
        order.MaterialReadiness.Should().Be(ServiceReadinessCode.Ready);
    }
}

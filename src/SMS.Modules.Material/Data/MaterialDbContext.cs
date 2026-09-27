using Microsoft.EntityFrameworkCore;
using SMS.Modules.Material.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Material.Data;

internal sealed class MaterialDbContext : DbContext, ITenantScopedDbContext
{
    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public MaterialDbContext(DbContextOptions<MaterialDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

    internal DbSet<Project>                    Projects                    => Set<Project>();
    internal DbSet<MaterialIssueRequest>       MaterialIssueRequests       => Set<MaterialIssueRequest>();
    internal DbSet<MaterialIssueRequestDetail> MaterialIssueRequestDetails => Set<MaterialIssueRequestDetail>();
    internal DbSet<MirLineApproval>            MirLineApprovals            => Set<MirLineApproval>();
    internal DbSet<StockReservation>           StockReservations           => Set<StockReservation>();
    internal DbSet<MaterialIssueVoucher>       MaterialIssueVouchers       => Set<MaterialIssueVoucher>();
    internal DbSet<MaterialIssueVoucherLine>   MaterialIssueVoucherLines   => Set<MaterialIssueVoucherLine>();
    internal DbSet<MivLineBatchSerial>         MivLineBatchSerials         => Set<MivLineBatchSerial>();
    internal DbSet<ProjectCostLedger>          ProjectCostLedger           => Set<ProjectCostLedger>();
    internal DbSet<DepartmentCostLedger>       DepartmentCostLedger        => Set<DepartmentCostLedger>();
    internal DbSet<MaterialReturn>             MaterialReturns             => Set<MaterialReturn>();
    internal DbSet<MaterialReturnDetail>       MaterialReturnDetails       => Set<MaterialReturnDetail>();
    internal DbSet<Wastage>                    Wastages                    => Set<Wastage>();
    internal DbSet<MaterialConsumption>        MaterialConsumptions        => Set<MaterialConsumption>();
    // A30 §7 — bills of materials.
    internal DbSet<BillOfMaterial>             BillsOfMaterials            => Set<BillOfMaterial>();
    internal DbSet<BillOfMaterialLine>         BillOfMaterialLines         => Set<BillOfMaterialLine>();
    // A30 §11–§13, §16 — production orders, what they need, what is short, and what moved to the floor.
    internal DbSet<ProductionOrder>               ProductionOrders               => Set<ProductionOrder>();
    internal DbSet<ProductionMaterialRequirement> ProductionMaterialRequirements => Set<ProductionMaterialRequirement>();
    internal DbSet<SupplyRequirement>             SupplyRequirements             => Set<SupplyRequirement>();
    // A31 C9 — "already handled manually" markers on the Purchase Required dashboard.
    internal DbSet<PurchaseRequiredAcknowledgement> PurchaseRequiredAcknowledgements => Set<PurchaseRequiredAcknowledgement>();
    internal DbSet<ProductionMaterialIssue>       ProductionMaterialIssues       => Set<ProductionMaterialIssue>();
    internal DbSet<ProductionMaterialIssueLine>   ProductionMaterialIssueLines   => Set<ProductionMaterialIssueLine>();
    // A30 §18-19 — quality inspection and finished goods receipt.
    internal DbSet<QualityInspection>             QualityInspections             => Set<QualityInspection>();
    internal DbSet<QualityInspectionLine>         QualityInspectionLines         => Set<QualityInspectionLine>();
    internal DbSet<FinishedGoodsReceipt>          FinishedGoodsReceipts          => Set<FinishedGoodsReceipt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("material");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MaterialDbContext).Assembly);
        modelBuilder.ApplyTenantQueryFilters(this);
        
        // F35 — each of those filters puts WHERE OrganizationId = @org on every query against
        // every one of these tables, and none of them had an index leading with it.
        modelBuilder.ApplyTenantIndexes();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        this.StampTenantScopedEntities(_tenantContext);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        this.StampTenantScopedEntities(_tenantContext);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}

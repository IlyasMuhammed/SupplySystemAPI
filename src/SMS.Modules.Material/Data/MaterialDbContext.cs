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
    // A36 — service orders, what they need, what moved, and the immutable ledger of what they consumed.
    internal DbSet<ServiceOrder>                  ServiceOrders                  => Set<ServiceOrder>();
    internal DbSet<ServiceMaterialRequirement>    ServiceMaterialRequirements    => Set<ServiceMaterialRequirement>();
    internal DbSet<ServiceMaterialIssue>          ServiceMaterialIssues          => Set<ServiceMaterialIssue>();
    internal DbSet<ServiceMaterialIssueLine>      ServiceMaterialIssueLines      => Set<ServiceMaterialIssueLine>();
    internal DbSet<ServiceLedgerEntry>            ServiceLedgerEntries           => Set<ServiceLedgerEntry>();

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
        this.StampModifiedAt(); // A37 D-16 (BOM header + lines)
        GuardServiceLedger();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        this.StampTenantScopedEntities(_tenantContext);
        this.StampModifiedAt(); // A37 D-16 (BOM header + lines)
        GuardServiceLedger();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>A36 D-9 / SVC-LED-02 — service ledger entries are written once; a correction is an offsetting entry.</summary>
    private void GuardServiceLedger()
    {
        if (ChangeTracker.Entries<ServiceLedgerEntry>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Service ledger entries are immutable; post an offsetting entry instead.");
    }
}

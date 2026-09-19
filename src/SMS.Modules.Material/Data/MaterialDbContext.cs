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

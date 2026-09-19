using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Data;

internal sealed class DemandDbContext : DbContext, ITenantScopedDbContext
{
    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public DemandDbContext(DbContextOptions<DemandDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

    internal DbSet<PurchaseRequisition>       PurchaseRequisitions      => Set<PurchaseRequisition>();
    internal DbSet<PrLine>                    PrLines                   => Set<PrLine>();
    internal DbSet<Quotation>                 Quotations                => Set<Quotation>();
    internal DbSet<QuotationLine>             QuotationLines            => Set<QuotationLine>();
    internal DbSet<VendorResponse>            VendorResponses           => Set<VendorResponse>();
    internal DbSet<VendorResponseLine>        VendorResponseLines       => Set<VendorResponseLine>();
    internal DbSet<QuotationInvitedSupplier>  QuotationInvitedSuppliers => Set<QuotationInvitedSupplier>();
    internal DbSet<PoLine>                    PoLines                   => Set<PoLine>();
    internal DbSet<PurchaseOrder>             PurchaseOrders            => Set<PurchaseOrder>();
    internal DbSet<PurchaseOrderLine>         PurchaseOrderLines        => Set<PurchaseOrderLine>();
    internal DbSet<PurchaseOrderPrLink>       PurchaseOrderPrLinks      => Set<PurchaseOrderPrLink>();
    internal DbSet<RfqAccessLink>             RfqAccessLinks            => Set<RfqAccessLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("demand");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DemandDbContext).Assembly);
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

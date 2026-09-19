using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Data;

internal sealed class LogisticsDbContext : DbContext, ITenantScopedDbContext
{
    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public LogisticsDbContext(DbContextOptions<LogisticsDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

    // Legacy — retired by T-16. SMS.Modules.Reports still queries Shipments directly.
    internal DbSet<Carrier>  Carriers  => Set<Carrier>();
    internal DbSet<Shipment> Shipments => Set<Shipment>();

    internal DbSet<Address> Addresses => Set<Address>();

    /// <summary>Which account a booking goes out on.</summary>
    internal DbSet<CarrierAccount> CarrierAccounts => Set<CarrierAccount>();

    /// <summary>
    /// The named products a carrier sells, and the terms that price them — the dim divisor above
    /// all. What <c>CarrierServiceCode</c> has meant all along without anything to say so.
    /// </summary>
    internal DbSet<CarrierService> CarrierServices => Set<CarrierService>();

    /// <summary>
    /// Negotiated tariffs (T-46) — what a carrier charges when it will not quote, and what its own
    /// quote gets checked against when it will. One card per carrier, service and period.
    /// </summary>
    /// <summary>Standing decisions about how goods ship (T-49), lowest priority first.</summary>
    internal DbSet<ShippingRule> ShippingRules => Set<ShippingRule>();

    /// <summary>
    /// What is owed to carriers for movements already made and not yet billed (T-52). One row per
    /// consignment, enforced — two would double the liability.
    /// </summary>
    internal DbSet<FreightAccrual> FreightAccruals => Set<FreightAccrual>();

    /// <summary>
    /// Bills from carriers (T-53) — the third leg of the three-way match. Not Finance's
    /// <c>Invoice</c>, which is supplier-bound and purchase-order shaped.
    /// </summary>
    internal DbSet<CarrierInvoice>     CarrierInvoices     => Set<CarrierInvoice>();
    internal DbSet<CarrierInvoiceLine> CarrierInvoiceLines => Set<CarrierInvoiceLine>();

    /// <summary>
    /// Cash a carrier collects on delivery, and whether it came back (T-57). The opposite of an
    /// accrual: this is money the carrier holds on our behalf.
    /// </summary>
    internal DbSet<CodCollection> CodCollections => Set<CodCollection>();
    internal DbSet<CodRemittance> CodRemittances => Set<CodRemittance>();

    internal DbSet<RateCard>      RateCards      => Set<RateCard>();
    internal DbSet<RateCardLane>  RateCardLanes  => Set<RateCardLane>();
    internal DbSet<RateCardBreak> RateCardBreaks => Set<RateCardBreak>();

    /// <summary>
    /// Encrypted secrets, kept apart from the accounts so that listing accounts never touches
    /// ciphertext. Only <c>CarrierCredentialVault</c> should read this.
    /// </summary>
    internal DbSet<CarrierCredential> CarrierCredentials => Set<CarrierCredential>();

    /// <summary>Written before every booking or cancellation call. See <c>CarrierCommandLedger</c>.</summary>
    internal DbSet<CarrierCommand> CarrierCommands => Set<CarrierCommand>();

    /// <summary>Label bytes. Only <c>ConsignmentLabelService</c> should read this.</summary>
    internal DbSet<ConsignmentLabel> ConsignmentLabels => Set<ConsignmentLabel>();

    internal DbSet<ConsignmentTrackingEvent> ConsignmentTrackingEvents => Set<ConsignmentTrackingEvent>();

    /// <summary>Verified carrier webhook deliveries. See <c>CarrierWebhookService</c>.</summary>
    internal DbSet<CarrierWebhookDelivery> CarrierWebhookDeliveries => Set<CarrierWebhookDelivery>();

    internal DbSet<DocumentNumberSequence> DocumentNumberSequences => Set<DocumentNumberSequence>();

    // Layer A — the commitment to move goods.
    internal DbSet<DeliveryOrder>     DeliveryOrders     => Set<DeliveryOrder>();
    internal DbSet<DeliveryOrderLine> DeliveryOrderLines => Set<DeliveryOrderLine>();

    // Warehouse execution — the instruction sheet that turns a released delivery into picked goods.
    internal DbSet<PickList>     PickLists     => Set<PickList>();
    internal DbSet<PickListLine> PickListLines => Set<PickListLine>();

    // Layer B — how it is physically packed.
    internal DbSet<ShipmentPackage> ShipmentPackages => Set<ShipmentPackage>();
    internal DbSet<PackageContent>  PackageContents  => Set<PackageContent>();

    // Layer C — the booked movement.
    internal DbSet<Consignment>         Consignments         => Set<Consignment>();
    internal DbSet<ConsignmentDelivery> ConsignmentDeliveries => Set<ConsignmentDelivery>();
    internal DbSet<ConsignmentStop>     ConsignmentStops     => Set<ConsignmentStop>();

    /// <summary>Itemised freight charges (T-47) — the base carriage, then each surcharge.</summary>
    internal DbSet<ConsignmentCharge>   ConsignmentCharges   => Set<ConsignmentCharge>();

    /// <summary>What has gone wrong with a movement (T-60), named and owned until it is settled.</summary>
    internal DbSet<DeliveryException> DeliveryExceptions => Set<DeliveryException>();

    /// <summary>Proof that goods reached somebody (T-61) — and the artefacts themselves, not a link.</summary>
    internal DbSet<DeliveryProof>     DeliveryProofs     => Set<DeliveryProof>();
    internal DbSet<DeliveryProofFile> DeliveryProofFiles => Set<DeliveryProofFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("logistics");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LogisticsDbContext).Assembly);
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

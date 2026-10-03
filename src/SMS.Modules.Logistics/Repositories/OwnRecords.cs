using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Repositories;

/// <summary>
/// A33 R-14 — the caller organization's own deliveries, pick lists, packages and consignments. The EF tenant filter
/// is off for a super admin, so every by-uuid read or write in the delivery operations goes through these: another
/// organization's record is "not found" (404), super admin included — the A32 <c>OwnOrders()</c> rule.
/// <para>
/// <b>Who calls.</b> Every caller of the delivery operations runs in a request (the controllers, and the document,
/// gate-pass, sale-order-delivery and route services they use). A Hangfire job whose originating organization was
/// captured (<c>HangfireTenantScope</c>) resolves that organization here, so it is scoped the same way; a job with
/// none would resolve the default organization and find nothing — refusing rather than reaching across tenants.
/// Paths that legitimately work across the request's tenant take the organization explicitly instead (the A33
/// creator and canceller, the carrier webhook and tracking paths, which do not use these repositories).
/// </para>
/// </summary>
internal static class OwnRecords
{
    public static IQueryable<DeliveryOrder> OwnDeliveries(this LogisticsDbContext db)
    {
        var org = db.TenantContext.OrganizationId;
        return db.DeliveryOrders.Where(d => d.OrganizationId == org && !d.IsDelete);
    }

    public static IQueryable<PickList> OwnPickLists(this LogisticsDbContext db)
    {
        var org = db.TenantContext.OrganizationId;
        return db.PickLists.Where(p => p.OrganizationId == org && !p.IsDelete);
    }

    public static IQueryable<ShipmentPackage> OwnPackages(this LogisticsDbContext db)
    {
        var org = db.TenantContext.OrganizationId;
        return db.ShipmentPackages.Where(p => p.OrganizationId == org && !p.IsDelete);
    }

    public static IQueryable<Consignment> OwnConsignments(this LogisticsDbContext db)
    {
        var org = db.TenantContext.OrganizationId;
        return db.Consignments.Where(c => c.OrganizationId == org && !c.IsDelete);
    }

    public static IQueryable<Carrier> OwnCarriers(this LogisticsDbContext db)
    {
        var org = db.TenantContext.OrganizationId;
        return db.Carriers.Where(c => c.OrganizationId == org && !c.IsDelete);
    }
}

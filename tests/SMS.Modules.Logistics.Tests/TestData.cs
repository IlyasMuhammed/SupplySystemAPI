using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Tests;

// Builders for the module's existing entities. Every required field is populated so a test that
// only cares about one property doesn't fail on an unrelated NOT NULL.
internal static class TestData
{
    internal static Carrier Carrier(
        string code = "TCS",
        string name = "TCS Express",
        Guid? organizationId = null)
    {
        var e = new Carrier
        {
            UUID                = Guid.NewGuid(),
            Name                = name,
            Code                = code,
            ServiceType         = "Courier",
            TrackingUrlTemplate = "https://example.test/track/{tracking}",
            ContactName         = "Ops Desk",
            ContactPhone        = "+923001234567",
            ContactEmail        = "ops@example.test",
            Status              = "Active",
            IsActive            = true,
            CreatedBy           = 1,
            CreatedDate         = DateTime.UtcNow
        };

        // Left at Guid.Empty unless a test is specifically exercising the stamping rule.
        if (organizationId.HasValue) e.OrganizationId = organizationId.Value;
        return e;
    }

    internal static Shipment Shipment(
        string shipmentNumber = "SHP-2026-00001",
        string poNumber       = "PO-2026-00001",
        Guid? organizationId  = null)
    {
        var now = DateTime.UtcNow;
        var e = new Shipment
        {
            UUID               = Guid.NewGuid(),
            ShipmentNumber     = shipmentNumber,
            PoUuid             = Guid.NewGuid(),
            PoNumber           = poNumber,
            ShipmentType       = "Courier",
            DispatchDate       = now,
            EstimatedArrival   = now.AddDays(3),
            DestinationAddress = "Plot 12, Korangi Industrial Area, Karachi",
            Status             = "Preparing",
            IsActive           = true,
            CreatedBy          = 1,
            CreatedDate        = now
        };

        if (organizationId.HasValue) e.OrganizationId = organizationId.Value;
        return e;
    }
}

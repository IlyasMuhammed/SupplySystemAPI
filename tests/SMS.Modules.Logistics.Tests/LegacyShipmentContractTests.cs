using System.Reflection;
using FluentAssertions;
using SMS.Modules.Logistics.Models;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

/// <summary>
/// T-16 (TC-16.5 / TC-16.6) — pins the shape of the deprecated <c>api/logistics/shipments</c>
/// responses.
/// <para>
/// These four Angular screens — carrier list/create and shipment list/create/detail — are the
/// only logistics UI that exists until the cockpit ships in T-19, and they deserialize into
/// hand-written TypeScript interfaces in <c>logistics.service.ts</c>. Those interfaces do not
/// tolerate a renamed or removed field, and nothing else in the build would notice.
/// </para>
/// <para>
/// So the contract is asserted here rather than assumed. When the endpoints are rewritten to read
/// through to the new model, this test is what proves the rewrite kept the shape identical.
/// </para>
/// </summary>
public class LegacyShipmentContractTests
{
    private static IEnumerable<string> PropertiesOf<T>() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                 .Select(p => p.Name);

    [Fact]
    public void The_shipment_list_row_keeps_exactly_the_fields_the_screen_reads()
    {
        // Mirrors ShipmentListItemModel in SupplyChainFrontend/src/app/services/logistics.service.ts.
        PropertiesOf<ShipmentListItemModel>().Should().BeEquivalentTo(
        [
            nameof(ShipmentListItemModel.UUID),
            nameof(ShipmentListItemModel.ShipmentNumber),
            nameof(ShipmentListItemModel.PoNumber),
            nameof(ShipmentListItemModel.CarrierName),
            nameof(ShipmentListItemModel.ShipmentType),
            nameof(ShipmentListItemModel.DispatchDate),
            nameof(ShipmentListItemModel.EstimatedArrival),
            nameof(ShipmentListItemModel.ActualArrival),
            nameof(ShipmentListItemModel.Status),
            nameof(ShipmentListItemModel.TrackingNumber)
        ]);
    }

    [Fact]
    public void The_shipment_detail_keeps_exactly_the_fields_the_screen_reads()
    {
        PropertiesOf<ShipmentDetailModel>().Should().BeEquivalentTo(
        [
            nameof(ShipmentDetailModel.UUID),
            nameof(ShipmentDetailModel.ShipmentNumber),
            nameof(ShipmentDetailModel.PoUuid),
            nameof(ShipmentDetailModel.PoNumber),
            nameof(ShipmentDetailModel.CarrierUuid),
            nameof(ShipmentDetailModel.CarrierName),
            nameof(ShipmentDetailModel.ShipmentType),
            nameof(ShipmentDetailModel.DispatchDate),
            nameof(ShipmentDetailModel.EstimatedArrival),
            nameof(ShipmentDetailModel.ActualArrival),
            nameof(ShipmentDetailModel.TrackingNumber),
            nameof(ShipmentDetailModel.TrackingUrl),
            nameof(ShipmentDetailModel.OriginWarehouseUuid),
            nameof(ShipmentDetailModel.DestinationAddress),
            nameof(ShipmentDetailModel.WeightKg),
            nameof(ShipmentDetailModel.VolumeCbm),
            nameof(ShipmentDetailModel.FreightCost),
            nameof(ShipmentDetailModel.Status),
            // ProofOfDeliveryUrl was removed with F47. The screen's POD upload went with it — a
            // path into wwwroot is a proof only until the next redeploy, and T-61 stores the
            // artefact itself against the consignment instead.
            nameof(ShipmentDetailModel.Notes),
            nameof(ShipmentDetailModel.CreatedDate)
        ]);
    }

    [Fact]
    public void The_carrier_models_keep_exactly_the_fields_the_screens_read()
    {
        PropertiesOf<CarrierListItemModel>().Should().BeEquivalentTo(
        [
            nameof(CarrierListItemModel.UUID),
            nameof(CarrierListItemModel.Name),
            nameof(CarrierListItemModel.Code),
            nameof(CarrierListItemModel.ServiceType),
            nameof(CarrierListItemModel.Status),
            // RatePerKg was removed with F39: it was multiplied by nothing, and rate cards (T-46)
            // are what price carriage now.
            nameof(CarrierListItemModel.IsActive)
        ]);

        // Note what is absent: the carrier gained ProviderKey, IntegrationMode, ScacCode and
        // DefaultCurrency in T-15, and none of them leaked into the response the existing screens
        // read. New columns must not change an old contract.
        PropertiesOf<CarrierDetailModel>().Should().NotContain("ProviderKey")
            .And.NotContain("IntegrationMode");
    }

    [Fact]
    public void The_shipment_filter_keeps_the_query_parameters_the_screen_sends()
    {
        PropertiesOf<ShipmentFilter>().Should().BeEquivalentTo(
        [
            nameof(ShipmentFilter.Status),
            nameof(ShipmentFilter.Search),
            nameof(ShipmentFilter.CarrierUuid),
            nameof(ShipmentFilter.Page),
            nameof(ShipmentFilter.PageSize)
        ]);
    }
}

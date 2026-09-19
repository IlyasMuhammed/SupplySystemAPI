using SMS.Modules.Logistics.Couriers;

namespace SMS.Modules.Logistics.Tests.Couriers;

/// <summary>
/// Runs the contract suite against <see cref="StubCourierProvider"/>.
/// <para>
/// This is what proves the base suite itself works — an abstract description of a contract that
/// has never been executed is a wish, not a test. T-32's simulator derives from the same base.
/// </para>
/// </summary>
public class StubCourierProviderContractTests : CourierProviderContractTests
{
    protected override ICourierProvider CreateProvider() => new StubCourierProvider();

    protected override CourierBookingRequest? RefusableRequest() =>
        BookableRequest() with
        {
            ShipTo = BookableRequest().ShipTo with
            {
                PostalCode = StubCourierProvider.UnserviceablePostcode
            }
        };
}

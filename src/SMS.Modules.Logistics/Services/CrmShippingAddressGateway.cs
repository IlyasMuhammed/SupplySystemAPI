using SMS.Modules.Logistics.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// Lead conversion's way into the customer's address book (<see cref="ICrmShippingAddressGateway"/>): the lead's address
/// becomes the first saved delivery address of the customer it created. Goes through the address book's own service, so
/// the address is normalized and validated exactly as one typed on the sale order would be.
/// </summary>
internal sealed class CrmShippingAddressGateway : ICrmShippingAddressGateway
{
    private readonly IAddressBookService _book;
    private readonly ITenantContext      _tenant;

    public CrmShippingAddressGateway(IAddressBookService book, ITenantContext tenant)
    {
        _book   = book;
        _tenant = tenant;
    }

    public async Task AddForCustomerAsync(Guid organizationId, Guid accountUuid, CrmShippingAddress address, int userId, CancellationToken ct = default)
    {
        if (organizationId != _tenant.OrganizationId)
            throw new BadRequestException("An address can only be saved in the caller's own organization.");

        // Never doubles an address the book already has for this customer (a re-run, or one typed in meanwhile).
        var existing = await _book.ListForConsigneeAsync(accountUuid);
        if (existing.Any(a => Same(a.Line1, address.Line1) && Same(a.PostalCode, address.PostalCode) && Same(a.CityName, address.City)))
            return;

        await _book.CreateAsync(new AddressRequest
        {
            Line1         = address.Line1,
            Line2         = address.Line2,
            CityName      = address.City,
            State         = address.State,
            PostalCode    = address.PostalCode,
            CountryName   = address.Country,
            ContactName   = address.ContactName,
            ContactPhone  = address.ContactPhone,
            ContactEmail  = address.ContactEmail,
            AddressType   = "CUSTOMER",
            ConsigneeUuid = accountUuid
        }, userId);
    }

    private static bool Same(string? a, string? b) =>
        string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
}

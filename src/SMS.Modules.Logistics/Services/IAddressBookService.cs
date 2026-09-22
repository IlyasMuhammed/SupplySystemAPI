using SMS.Modules.Logistics.Models;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// The addresses a customer can be shipped to, kept where a sale order can point at them (A29 §7.7: an order's
/// <c>shipping_address_id</c> is a <c>logistics.addresses</c> row, never a flat string). Until this existed an
/// address could only be made by typing one onto a delivery, so no order could name one.
/// <para>
/// An address is still a snapshot, as the entity says: this saves one and lists them, it does not edit one, and
/// two saves of the same street are two rows. The list folds repeats into the newest so a customer's book reads
/// as places, not as history.
/// </para>
/// </summary>
public interface IAddressBookService
{
    /// <summary>
    /// Saves an address for <see cref="AddressRequest.ConsigneeUuid"/>, the customer it belongs to, normalizing it
    /// like a delivery's: only a structurally meaningless one (no line 1, city or country) is refused, and anything
    /// else that cannot be confirmed saves as UNVALIDATED with the reason.
    /// </summary>
    /// <exception cref="SMS.Shared.Exceptions.BadRequestException">No customer named, or the address is meaningless.</exception>
    Task<AddressModel> CreateAsync(AddressRequest request, int createdBy);

    /// <summary>One address, or null when there is none by that id in this organization.</summary>
    Task<AddressModel?> GetAsync(Guid uuid);

    /// <summary>The customer's addresses, newest first, each place once.</summary>
    Task<IReadOnlyList<AddressModel>> ListForConsigneeAsync(Guid consigneeUuid);
}

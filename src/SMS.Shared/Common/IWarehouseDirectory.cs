namespace SMS.Shared.Common;

/// <summary>
/// A warehouse as another module needs to see it: where it is, and who answers the phone there.
/// </summary>
/// <param name="Address">Free text, as the warehouse master holds it — there is no postal code or country code here.</param>
public sealed record WarehouseContact(
    Guid    Uuid,
    string  Code,
    string  Name,
    string? Address,
    string? City,
    string? Country,
    string? ContactName,
    string? ContactPhone,
    bool    IsActive);

/// <summary>
/// Reads a warehouse's address and contact from the module that owns warehouses.
/// <para>
/// A cross-module interface for the same reason <see cref="IStockReservationService"/> is: Logistics
/// keeps a warehouse as a bare UUID with no foreign key, and cannot see Inventory's entities. This is
/// the one thing it needs from them — where a delivery leaves from, so a carrier can be told where to
/// collect it.
/// </para>
/// </summary>
public interface IWarehouseDirectory
{
    /// <summary>The warehouse, or null when no warehouse in this organization has that UUID.</summary>
    Task<WarehouseContact?> FindAsync(Guid warehouseUuid, CancellationToken ct = default);
}

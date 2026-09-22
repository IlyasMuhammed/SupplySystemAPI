using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Services;

internal sealed class AddressBookService : IAddressBookService
{
    /// <summary>How many of a customer's newest addresses are looked at, and the most one list returns.</summary>
    private const int Scan = 200;
    private const int MaxListed = 50;

    private readonly LogisticsDbContext _db;
    private readonly IAddressNormalizer _normalizer;
    private readonly TimeProvider       _clock;

    public AddressBookService(LogisticsDbContext db, IAddressNormalizer normalizer, TimeProvider? clock = null)
    {
        _db         = db;
        _normalizer = normalizer;
        _clock      = clock ?? TimeProvider.System;
    }

    public async Task<AddressModel> CreateAsync(AddressRequest request, int createdBy)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ConsigneeUuid is not { } customer || customer == Guid.Empty)
            throw new BadRequestException("Name the customer this address belongs to.");

        var type = string.IsNullOrWhiteSpace(request.AddressType)
            ? LogisticsCode.Of(AddressType.Customer)
            : LogisticsCode.TryParse<AddressType>(request.AddressType.Trim().ToUpperInvariant(), out var parsed)
                ? LogisticsCode.Of(parsed)
                : throw new BadRequestException(
                    $"'{request.AddressType}' is not an address type. Valid: {string.Join(", ", LogisticsCode.Codes<AddressType>())}.");

        var address = new Address
        {
            UUID           = Guid.NewGuid(),
            Line1          = request.Line1,
            Line2          = request.Line2,
            CityId         = request.CityId,
            CityName       = request.CityName,
            State          = request.State,
            PostalCode     = request.PostalCode,
            CountryName    = request.CountryName,
            CountryIsoCode = request.CountryIsoCode,
            ContactName    = request.ContactName,
            ContactPhone   = request.ContactPhone,
            ContactEmail   = request.ContactEmail,
            Latitude       = request.Latitude,
            Longitude      = request.Longitude,
            AddressType    = type,
            ConsigneeUuid  = customer,
            CreatedBy      = createdBy,
            CreatedDate    = _clock.GetUtcNow().UtcDateTime
        };

        await _normalizer.NormalizeAsync(address);

        _db.Addresses.Add(address);
        await _db.SaveChangesAsync();

        return ToModel(address);
    }

    public async Task<AddressModel?> GetAsync(Guid uuid)
    {
        var address = await _db.Addresses.AsNoTracking().FirstOrDefaultAsync(a => a.UUID == uuid && !a.IsDelete);
        return address is null ? null : ToModel(address);
    }

    public async Task<IReadOnlyList<AddressModel>> ListForConsigneeAsync(Guid consigneeUuid)
    {
        var newest = await _db.Addresses.AsNoTracking()
            .Where(a => a.ConsigneeUuid == consigneeUuid && a.IsActive && !a.IsDelete)
            .OrderByDescending(a => a.CreatedDate).ThenByDescending(a => a.Id)
            .Take(Scan)
            .ToListAsync();

        // A place once, at its newest: the same street saved for two deliveries is one line in the book.
        return [.. newest
            .GroupBy(a => (Same(a.Line1), Same(a.Line2), Same(a.CityName), Same(a.PostalCode), Same(a.CountryName)))
            .Select(g => g.First())
            .Take(MaxListed)
            .Select(ToModel)];
    }

    private static string Same(string? text) => (text ?? string.Empty).Trim().ToUpperInvariant();

    private static AddressModel ToModel(Address a) => new()
    {
        UUID             = a.UUID,
        Line1            = a.Line1,
        Line2            = a.Line2,
        CityId           = a.CityId,
        CityName         = a.CityName,
        State            = a.State,
        PostalCode       = a.PostalCode,
        CountryName      = a.CountryName,
        CountryIsoCode   = a.CountryIsoCode,
        ContactName      = a.ContactName,
        ContactPhone     = a.ContactPhone,
        ContactPhoneE164 = a.ContactPhoneE164,
        ContactEmail     = a.ContactEmail,
        AddressType      = a.AddressType,
        ValidationStatus = a.ValidationStatus,
        ValidationNotes  = a.ValidationNotes
    };
}

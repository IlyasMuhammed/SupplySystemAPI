using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Inventory.Services.LeadTimes;

/// <summary>
/// A34 PB-03/04 (D-10) — the caller's own organization's lead-time defaults (API-CONTRACT §4.4). Always the caller's
/// organization, filtered explicitly: the EF tenant filter is off for a super admin.
/// </summary>
public interface ILeadTimeDefaultsService
{
    /// <summary>The row, or the system defaults with <c>isSaved: false</c>. Never 404.</summary>
    Task<LeadTimeDefaultsModel> GetAsync(CancellationToken ct = default);

    /// <summary>Upsert. 400 per field outside 0–365 (BR-C3-01) or missing. A concurrent first save is retried as an update.</summary>
    Task<LeadTimeDefaultsModel> UpdateAsync(UpdateLeadTimeDefaultsRequest request, int userId, CancellationToken ct = default);
}

internal sealed class LeadTimeDefaultsService : ILeadTimeDefaultsService
{
    /// <summary>A lost first-save race or a concurrent update is retried; more than this many collisions in a row is a 409.</summary>
    private const int MaxAttempts = 10;

    private readonly InventoryDbContext _db;

    public LeadTimeDefaultsService(InventoryDbContext db) => _db = db;

    public async Task<LeadTimeDefaultsModel> GetAsync(CancellationToken ct = default)
    {
        var org = _db.TenantContext.OrganizationId;
        var row = org == Guid.Empty
            ? null
            : await _db.LeadTimeDefaults.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(d => d.OrganizationId == org, ct);

        return row is null ? SystemDefaultsModel() : ToModel(row);
    }

    public async Task<LeadTimeDefaultsModel> UpdateAsync(UpdateLeadTimeDefaultsRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var values = Validate(request);

        var org = _db.TenantContext.OrganizationId;
        if (org == Guid.Empty)
            throw new BadRequestException("Lead-time defaults belong to an organization: sign in to one first.");

        for (var attempt = 1; ; attempt++)
        {
            // Tracked, own organization explicitly (a super admin has no tenant filter).
            var row = await _db.LeadTimeDefaults.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.OrganizationId == org, ct);
            var now = DateTime.UtcNow;
            if (row is null)
            {
                row = new LeadTimeDefaults { OrganizationId = org, CreatedBy = userId, CreatedDate = now };
                _db.LeadTimeDefaults.Add(row);
            }

            row.PickPackDays            = values.PickPackDays;
            row.ShippingLeadTimeDays    = values.ShippingLeadTimeDays;
            row.SalesBufferDays         = values.SalesBufferDays;
            row.ManufacturingBufferDays = values.ManufacturingBufferDays;
            row.QualityInspectionDays   = values.QualityInspectionDays;
            row.InternalTransferDays    = values.InternalTransferDays;
            row.ModifiedBy              = userId;
            row.ModifiedDate            = now;

            try
            {
                await _db.SaveChangesAsync(ct);
                return ToModel(row);
            }
            catch (DbUpdateException ex) when (IsLostRace(ex))
            {
                // Another request saved this organization's first row (unique OrganizationId) or changed it (row
                // version) between our read and our write. Forget what we tracked and do it again against what is
                // there now: a PUT carries every value, so applying it on top is exactly "the later save wins".
                foreach (var entry in _db.ChangeTracker.Entries<LeadTimeDefaults>().ToList())
                    entry.State = EntityState.Detached;

                if (attempt >= MaxAttempts)
                    throw new ConflictException("The lead-time defaults were being changed by someone else at the same time. Try again.");
            }
        }
    }

    private static bool IsLostRace(DbUpdateException ex) =>
        ex is DbUpdateConcurrencyException
        || ex.InnerException is SqlException { Number: 2601 or 2627 };

    /// <summary>BR-C3-01: every value present and within 0–365 (API-CONTRACT §4.4 messages).</summary>
    private static LeadTimeDefaultValues Validate(UpdateLeadTimeDefaultsRequest r) => new(
        Check(r.PickPackDays,            LeadTimeComponentCode.PickPack),
        Check(r.ShippingLeadTimeDays,    LeadTimeComponentCode.Shipping),
        Check(r.SalesBufferDays,         LeadTimeComponentCode.SalesBuffer),
        Check(r.ManufacturingBufferDays, LeadTimeComponentCode.MfgBuffer),
        Check(r.QualityInspectionDays,   LeadTimeComponentCode.Qc),
        Check(r.InternalTransferDays,    LeadTimeComponentCode.Transfer),
        IsSaved: true);

    private static int Check(int? value, string code)
    {
        var name = LeadTimeResolver.Name(code);
        if (value is not int days)
            throw new BadRequestException($"{name} days is required.");
        if (days < 0 || days > LeadTimeResolver.MaxDefaultDays)
            throw new BadRequestException($"{name} days must be between 0 and {LeadTimeResolver.MaxDefaultDays}.");
        return days;
    }

    private static LeadTimeDefaultsModel SystemDefaultsModel()
    {
        var d = LeadTimeDefaultValues.System;
        return new LeadTimeDefaultsModel
        {
            PickPackDays            = d.PickPackDays,
            ShippingLeadTimeDays    = d.ShippingLeadTimeDays,
            SalesBufferDays         = d.SalesBufferDays,
            ManufacturingBufferDays = d.ManufacturingBufferDays,
            QualityInspectionDays   = d.QualityInspectionDays,
            InternalTransferDays    = d.InternalTransferDays,
            IsSaved                 = false
        };
    }

    private static LeadTimeDefaultsModel ToModel(LeadTimeDefaults row) => new()
    {
        PickPackDays            = row.PickPackDays,
        ShippingLeadTimeDays    = row.ShippingLeadTimeDays,
        SalesBufferDays         = row.SalesBufferDays,
        ManufacturingBufferDays = row.ManufacturingBufferDays,
        QualityInspectionDays   = row.QualityInspectionDays,
        InternalTransferDays    = row.InternalTransferDays,
        IsSaved                 = true,
        ModifiedBy              = row.ModifiedBy,
        ModifiedDate            = row.ModifiedDate
    };
}

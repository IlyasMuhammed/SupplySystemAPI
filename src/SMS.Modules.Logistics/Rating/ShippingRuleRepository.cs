using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Rating;

internal interface IShippingRuleRepository
{
    Task<Guid> CreateAsync(CreateShippingRuleRequest req, int userId);
    Task<IReadOnlyList<ShippingRuleModel>> GetAllAsync();
    Task<ShippingRuleModel?> GetByUuidAsync(Guid uuid);
    Task<bool> PatchAsync(Guid uuid, PatchShippingRuleRequest req, int userId);
    Task<bool> DeleteAsync(Guid uuid, int userId);

    /// <summary>Active rules in priority order, with their carriers. The order they are tried in.</summary>
    Task<IReadOnlyList<ShippingRule>> GetActiveInOrderAsync(CancellationToken ct = default);
}

internal sealed class ShippingRuleRepository : IShippingRuleRepository
{
    internal const string Cheapest = "CHEAPEST";
    internal const string Fastest  = "FASTEST";

    private static readonly string[] Strategies = [Cheapest, Fastest];

    private static readonly string[] Clearable =
    [
        "MIN_WEIGHT", "MAX_WEIGHT", "ORIGIN_COUNTRY", "ORIGIN_POSTCODE",
        "DESTINATION_COUNTRY", "DESTINATION_POSTCODE", "MIN_VALUE", "MAX_VALUE",
        "HAZARDOUS", "COD", "CARRIER", "SERVICE"
    ];

    private readonly LogisticsDbContext _db;
    public ShippingRuleRepository(LogisticsDbContext db) => _db = db;

    // ── Create ────────────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateShippingRuleRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var name     = Require(req.Name, "A shipping rule needs a name — somebody has to be able to say which rule fired.");
        var strategy = Strategy(req.Strategy);

        Carrier? carrier = null;

        if (req.CarrierUuid is { } carrierUuid)
            carrier = await _db.Carriers.FirstOrDefaultAsync(c => c.UUID == carrierUuid && !c.IsDelete)
                ?? throw new NotFoundException("Carrier", carrierUuid);

        var rule = new ShippingRule
        {
            UUID                      = Guid.NewGuid(),
            OrganizationId            = carrier?.OrganizationId ?? Guid.Empty,
            Name                      = name,
            Description               = Trim(req.Description),
            Priority                  = req.Priority,
            MinChargeableWeightKg     = req.MinChargeableWeightKg,
            MaxChargeableWeightKg     = req.MaxChargeableWeightKg,
            OriginCountryIso          = Upper(req.OriginCountryIso),
            OriginPostcodePrefix      = Upper(req.OriginPostcodePrefix),
            DestinationCountryIso     = Upper(req.DestinationCountryIso),
            DestinationPostcodePrefix = Upper(req.DestinationPostcodePrefix),
            MinDeclaredValue          = req.MinDeclaredValue,
            MaxDeclaredValue          = req.MaxDeclaredValue,
            AppliesToHazardous        = req.AppliesToHazardous,
            AppliesToCod              = req.AppliesToCod,
            CarrierId                 = carrier?.Id,
            ServiceCode               = Trim(req.ServiceCode),
            Strategy                  = strategy,
            IsActive                  = true,
            CreatedBy                 = userId,
            CreatedDate               = DateTime.UtcNow
        };

        Validate(rule);
        await RefusePriorityClashAsync(rule.Priority, excludingId: null);

        _db.ShippingRules.Add(rule);
        await _db.SaveChangesAsync();

        return rule.UUID;
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ShippingRuleModel>> GetAllAsync()
    {
        var rules = await _db.ShippingRules.AsNoTracking()
            .Include(r => r.Carrier)
            .Where(r => !r.IsDelete)
            .OrderBy(r => r.Priority)
            .ToListAsync();

        return [.. rules.Select(ToModel)];
    }

    public async Task<ShippingRuleModel?> GetByUuidAsync(Guid uuid)
    {
        var rule = await _db.ShippingRules.AsNoTracking()
            .Include(r => r.Carrier)
            .FirstOrDefaultAsync(r => r.UUID == uuid && !r.IsDelete);

        return rule is null ? null : ToModel(rule);
    }

    public async Task<IReadOnlyList<ShippingRule>> GetActiveInOrderAsync(CancellationToken ct = default) =>
        await _db.ShippingRules.AsNoTracking()
            .Include(r => r.Carrier)
            .Where(r => !r.IsDelete && r.IsActive)
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Id)
            .ToListAsync(ct);

    private static ShippingRuleModel ToModel(ShippingRule r) => new()
    {
        UUID                      = r.UUID,
        Name                      = r.Name,
        Description               = r.Description,
        Priority                  = r.Priority,
        IsActive                  = r.IsActive,
        MinChargeableWeightKg     = r.MinChargeableWeightKg,
        MaxChargeableWeightKg     = r.MaxChargeableWeightKg,
        OriginCountryIso          = r.OriginCountryIso,
        OriginPostcodePrefix      = r.OriginPostcodePrefix,
        DestinationCountryIso     = r.DestinationCountryIso,
        DestinationPostcodePrefix = r.DestinationPostcodePrefix,
        MinDeclaredValue          = r.MinDeclaredValue,
        MaxDeclaredValue          = r.MaxDeclaredValue,
        AppliesToHazardous        = r.AppliesToHazardous,
        AppliesToCod              = r.AppliesToCod,
        CarrierUuid               = r.Carrier?.UUID,
        CarrierName               = r.Carrier?.Name,
        ServiceCode               = r.ServiceCode,
        Strategy                  = r.Strategy ?? Cheapest,
        Summary                   = Summarize(r),
        CreatedDate               = r.CreatedDate
    };

    /// <summary>
    /// What the rule says, in a sentence. A rule list that has to be decoded column by column is a
    /// rule list nobody audits.
    /// </summary>
    private static string Summarize(ShippingRule r)
    {
        var conditions = new List<string>();

        if (r.MinChargeableWeightKg is { } min && r.MaxChargeableWeightKg is { } max)
            conditions.Add($"{min:0.###}–{max:0.###} kg");
        else if (r.MinChargeableWeightKg is { } from) conditions.Add($"over {from:0.###} kg");
        else if (r.MaxChargeableWeightKg is { } to)   conditions.Add($"up to {to:0.###} kg");

        if (r.OriginCountryIso is not null || r.OriginPostcodePrefix is not null)
            conditions.Add($"from {Place(r.OriginCountryIso, r.OriginPostcodePrefix)}");

        if (r.DestinationCountryIso is not null || r.DestinationPostcodePrefix is not null)
            conditions.Add($"to {Place(r.DestinationCountryIso, r.DestinationPostcodePrefix)}");

        if (r.MinDeclaredValue is { } minValue) conditions.Add($"worth {minValue:N0} or more");
        if (r.MaxDeclaredValue is { } maxValue) conditions.Add($"worth up to {maxValue:N0}");

        if (r.AppliesToHazardous is { } hazardous) conditions.Add(hazardous ? "hazardous" : "non-hazardous");
        if (r.AppliesToCod       is { } cod)       conditions.Add(cod ? "with COD" : "without COD");

        var what = conditions.Count == 0 ? "Anything" : char.ToUpperInvariant(conditions[0][0]) + conditions[0][1..];
        if (conditions.Count > 1) what += ", " + string.Join(", ", conditions.Skip(1));

        var action = (r.Carrier?.Name, r.ServiceCode) switch
        {
            (null, null)              => $"the {(r.Strategy ?? Cheapest).ToLowerInvariant()} carrier",
            (null, var service)       => $"the {(r.Strategy ?? Cheapest).ToLowerInvariant()} carrier on {service}",
            (var carrier, null)       => $"{carrier}, {(r.Strategy ?? Cheapest).ToLowerInvariant()} service",
            var (carrier, service)    => $"{carrier} {service}"
        };

        return $"{what} → {action}.";
    }

    private static string Place(string? country, string? prefix) =>
        (country, prefix) switch
        {
            (null, var p) => $"postcode {p}",
            (var c, null) => c!,
            var (c, p)    => $"{c} {p}"
        };

    // ── Update ────────────────────────────────────────────────────────────────

    public async Task<bool> PatchAsync(Guid uuid, PatchShippingRuleRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var rule = await _db.ShippingRules.FirstOrDefaultAsync(r => r.UUID == uuid && !r.IsDelete);
        if (rule is null) return false;

        if (req.Name is not null) rule.Name = Require(req.Name, "A shipping rule name cannot be blank.");
        if (req.Description is not null) rule.Description = Trim(req.Description);

        if (req.MinChargeableWeightKg is { } minWeight) rule.MinChargeableWeightKg = minWeight;
        if (req.MaxChargeableWeightKg is { } maxWeight) rule.MaxChargeableWeightKg = maxWeight;
        if (req.MinDeclaredValue      is { } minValue)  rule.MinDeclaredValue      = minValue;
        if (req.MaxDeclaredValue      is { } maxValue)  rule.MaxDeclaredValue      = maxValue;

        if (req.OriginCountryIso          is not null) rule.OriginCountryIso          = Upper(req.OriginCountryIso);
        if (req.OriginPostcodePrefix      is not null) rule.OriginPostcodePrefix      = Upper(req.OriginPostcodePrefix);
        if (req.DestinationCountryIso     is not null) rule.DestinationCountryIso     = Upper(req.DestinationCountryIso);
        if (req.DestinationPostcodePrefix is not null) rule.DestinationPostcodePrefix = Upper(req.DestinationPostcodePrefix);

        if (req.AppliesToHazardous is { } hazardous) rule.AppliesToHazardous = hazardous;
        if (req.AppliesToCod       is { } cod)       rule.AppliesToCod       = cod;

        if (req.ServiceCode is not null) rule.ServiceCode = Trim(req.ServiceCode);
        if (req.Strategy    is not null) rule.Strategy    = Strategy(req.Strategy);
        if (req.IsActive    is { } active) rule.IsActive  = active;

        if (req.CarrierUuid is { } carrierUuid)
        {
            var carrier = await _db.Carriers.FirstOrDefaultAsync(c => c.UUID == carrierUuid && !c.IsDelete)
                ?? throw new NotFoundException("Carrier", carrierUuid);

            rule.CarrierId = carrier.Id;
        }

        ApplyClears(rule, req.ClearConditions);

        if (req.Priority is { } priority)
        {
            await RefusePriorityClashAsync(priority, rule.Id);
            rule.Priority = priority;
        }

        Validate(rule);

        rule.ModifiedBy   = userId;
        rule.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(Guid uuid, int userId)
    {
        var rule = await _db.ShippingRules.FirstOrDefaultAsync(r => r.UUID == uuid && !r.IsDelete);
        if (rule is null) return false;

        // Soft: a consignment routed by this rule months ago is only explainable while the rule
        // that routed it still exists.
        rule.IsDelete     = true;
        rule.IsActive     = false;
        rule.ModifiedBy   = userId;
        rule.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    // ── Rules about rules ─────────────────────────────────────────────────────

    private static void Validate(ShippingRule rule)
    {
        if (rule.MinChargeableWeightKg is < 0 || rule.MaxChargeableWeightKg is < 0)
            throw new BadRequestException("A weight condition cannot be negative.");

        if (rule.MinChargeableWeightKg is { } min && rule.MaxChargeableWeightKg is { } max && min > max)
            throw new BadRequestException(
                $"This rule matches weights from {min:0.###} kg to {max:0.###} kg, which is nothing at all.");

        if (rule.MinDeclaredValue is < 0 || rule.MaxDeclaredValue is < 0)
            throw new BadRequestException("A declared-value condition cannot be negative.");

        if (rule.MinDeclaredValue is { } minValue && rule.MaxDeclaredValue is { } maxValue && minValue > maxValue)
            throw new BadRequestException(
                $"This rule matches values from {minValue:N2} to {maxValue:N2}, which is nothing at all.");

        // A rule that names neither a carrier nor a service is still useful — it says "shop
        // everything, cheapest wins" — so the only thing worth refusing is a service with no
        // carrier, which cannot be resolved against anything.
        if (rule.CarrierId is null && !string.IsNullOrWhiteSpace(rule.ServiceCode))
            throw new BadRequestException(
                $"'{rule.ServiceCode}' names a service without a carrier. Service codes belong to a "
              + "carrier, so the same code can mean different things at two of them.");
    }

    private async Task RefusePriorityClashAsync(int priority, int? excludingId)
    {
        var clash = await _db.ShippingRules
            .FirstOrDefaultAsync(r => !r.IsDelete && r.Priority == priority
                                   && (excludingId == null || r.Id != excludingId));

        if (clash is not null)
            throw new ConflictException(
                $"'{clash.Name}' is already priority {priority}. Two rules at one priority would make "
              + "the one that fires depend on row order, and the symptom is a parcel on the wrong carrier.");
    }

    private static string Strategy(string? value)
    {
        var strategy = Upper(value);

        if (strategy is null) return Cheapest;

        if (!Strategies.Contains(strategy))
            throw new BadRequestException(
                $"'{value}' is not a strategy. Valid values: {string.Join(", ", Strategies)}.");

        return strategy;
    }

    private static void ApplyClears(ShippingRule rule, List<string>? clears)
    {
        if (clears is null || clears.Count == 0) return;

        foreach (var raw in clears)
        {
            switch (raw?.Trim().ToUpperInvariant())
            {
                case "MIN_WEIGHT":           rule.MinChargeableWeightKg     = null; break;
                case "MAX_WEIGHT":           rule.MaxChargeableWeightKg     = null; break;
                case "ORIGIN_COUNTRY":       rule.OriginCountryIso          = null; break;
                case "ORIGIN_POSTCODE":      rule.OriginPostcodePrefix      = null; break;
                case "DESTINATION_COUNTRY":  rule.DestinationCountryIso     = null; break;
                case "DESTINATION_POSTCODE": rule.DestinationPostcodePrefix = null; break;
                case "MIN_VALUE":            rule.MinDeclaredValue          = null; break;
                case "MAX_VALUE":            rule.MaxDeclaredValue          = null; break;
                case "HAZARDOUS":            rule.AppliesToHazardous        = null; break;
                case "COD":                  rule.AppliesToCod              = null; break;
                // Clearing the carrier clears the service with it: a service code with no carrier
                // resolves against nothing, and leaving one behind would fail validation anyway.
                case "CARRIER":              rule.CarrierId   = null;
                                             rule.ServiceCode = null; break;
                case "SERVICE":              rule.ServiceCode = null; break;
                default:
                    throw new BadRequestException(
                        $"'{raw}' is not a clearable condition. Valid values: {string.Join(", ", Clearable)}.");
            }
        }
    }

    private static string Require(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new BadRequestException(message) : value.Trim();

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Upper(string? value) => Trim(value)?.ToUpperInvariant();
}

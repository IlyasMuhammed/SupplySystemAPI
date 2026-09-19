using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Rating;

/// <summary>
/// Standing decisions about how goods ship, applied automatically — and audited.
/// <para>
/// <b>Rules narrow; shopping prices.</b> A rule says which carriers and which service are eligible;
/// T-48 then compares what is left. So a rule never needs rewriting when a tariff changes, and the
/// figure it produces is the same figure a person comparing by hand would have seen.
/// </para>
/// </summary>
public interface IShippingRuleService
{
    Task<Guid> CreateAsync(CreateShippingRuleRequest req, int userId);
    Task<IReadOnlyList<ShippingRuleModel>> GetAllAsync();
    Task<ShippingRuleModel?> GetByUuidAsync(Guid uuid);
    Task<bool> PatchAsync(Guid uuid, PatchShippingRuleRequest req, int userId);
    Task<bool> DeleteAsync(Guid uuid, int userId);

    /// <summary>
    /// Which rule fires for this consignment, why the earlier ones did not, and what the winner
    /// comes to. Changes nothing.
    /// </summary>
    Task<ShippingRuleDecisionModel?> EvaluateAsync(
        Guid consignmentUuid, DateTime? shipDate = null, CancellationToken ct = default);

    /// <summary>Evaluates, then accepts what the rule chose — carrier, service and price.</summary>
    Task<ConsignmentRateModel?> ApplyAsync(
        Guid consignmentUuid, DateTime? shipDate, int userId, CancellationToken ct = default);
}

internal sealed class ShippingRuleService : IShippingRuleService
{
    private readonly LogisticsDbContext        _db;
    private readonly IShippingRuleRepository   _rules;
    private readonly IChargeableWeightService  _weights;
    private readonly IRateShoppingService      _shopping;
    private readonly ICarrierServiceRepository _services;

    public ShippingRuleService(
        LogisticsDbContext db, IShippingRuleRepository rules, IChargeableWeightService weights,
        IRateShoppingService shopping, ICarrierServiceRepository services)
    {
        _db       = db;
        _rules    = rules;
        _weights  = weights;
        _shopping = shopping;
        _services = services;
    }

    public Task<Guid> CreateAsync(CreateShippingRuleRequest req, int userId) => _rules.CreateAsync(req, userId);
    public Task<IReadOnlyList<ShippingRuleModel>> GetAllAsync() => _rules.GetAllAsync();
    public Task<ShippingRuleModel?> GetByUuidAsync(Guid uuid) => _rules.GetByUuidAsync(uuid);
    public Task<bool> PatchAsync(Guid uuid, PatchShippingRuleRequest req, int userId) =>
        _rules.PatchAsync(uuid, req, userId);
    public Task<bool> DeleteAsync(Guid uuid, int userId) => _rules.DeleteAsync(uuid, userId);

    // ── Evaluating ────────────────────────────────────────────────────────────

    public async Task<ShippingRuleDecisionModel?> EvaluateAsync(
        Guid consignmentUuid, DateTime? shipDate = null, CancellationToken ct = default)
    {
        var weight = await _weights.GetAsync(consignmentUuid, ct);
        if (weight is null) return null;

        var consignment = await _db.Consignments.AsNoTracking()
            .Include(c => c.ShipFromAddress)
            .Include(c => c.ShipToAddress)
            .Include(c => c.Deliveries).ThenInclude(cd => cd.DeliveryOrder).ThenInclude(d => d.Lines)
            .Include(c => c.Deliveries).ThenInclude(cd => cd.DeliveryOrder).ThenInclude(d => d.Packages)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.UUID == consignmentUuid && !c.IsDelete, ct);

        if (consignment is null) return null;

        var facts = FactsOf(consignment, weight.TotalChargeableKg);

        var decision = new ShippingRuleDecisionModel
        {
            ConsignmentUuid       = consignment.UUID,
            ConsignmentNumber     = consignment.ConsignmentNumber,
            ChargeableWeightKg    = facts.ChargeableWeightKg,
            DeclaredValue         = facts.DeclaredValue,
            IsHazardous           = facts.IsHazardous,
            HasCod                = facts.HasCod,
            OriginCountryIso      = facts.OriginCountryIso,
            OriginPostcode        = facts.OriginPostcode,
            DestinationCountryIso = facts.DestinationCountryIso,
            DestinationPostcode   = facts.DestinationPostcode,
            Warnings              = [.. weight.Warnings]
        };

        var rules = await _rules.GetActiveInOrderAsync(ct);

        if (rules.Count == 0)
        {
            decision.Warnings.Add(
                "No shipping rules are configured, so nothing can be routed automatically. "
              + "Rate-shop the consignment and choose by hand.");
            return decision;
        }

        ShippingRule? matched = null;

        foreach (var rule in rules)
        {
            var verdict = ShippingRuleMatcher.Evaluate(rule, facts);

            decision.Considered.Add(new ShippingRuleVerdictModel
            {
                RuleUuid = rule.UUID, Name = rule.Name, Priority = rule.Priority,
                Matched = verdict.Matched, Reason = verdict.Explanation
            });

            if (!verdict.Matched) continue;

            matched = rule;
            decision.MatchedRule = decision.Considered[^1];

            // First match wins, and the rest are not even looked at — which is what priority is
            // for. Listing them as "not considered" would be noise; the list stops where it stopped.
            break;
        }

        if (matched is null)
        {
            decision.Warnings.Add(
                $"None of the {rules.Count} active rule(s) matches this consignment. Add a catch-all "
              + "rule with no conditions at the lowest priority, or choose a carrier by hand.");
            return decision;
        }

        decision.Selection = Describe(matched);

        await PriceAsync(decision, matched, facts, shipDate, ct);

        return decision;
    }

    /// <summary>
    /// What the rule's selection costs, through rate shopping — so the price a rule produces is the
    /// same price a person comparing by hand would have got.
    /// </summary>
    private async Task PriceAsync(
        ShippingRuleDecisionModel decision, ShippingRule rule, ShipmentFacts facts,
        DateTime? shipDate, CancellationToken ct)
    {
        var shop = await _shopping.ShopAsync(decision.ConsignmentUuid, new RateShopRequest
        {
            CarrierUuids = rule.Carrier is null ? null : [rule.Carrier.UUID],
            ServiceCode  = rule.ServiceCode,
            Strategy     = rule.Strategy,
            ShipDate     = shipDate
        }, ct);

        if (shop is null) return;

        decision.Excluded = shop.Excluded;

        foreach (var warning in shop.Warnings)
            if (!decision.Warnings.Contains(warning)) decision.Warnings.Add(warning);

        var options = shop.Options;

        // Hazardous goods on a service that declares it will not take them. The column has existed
        // since T-43 and nothing read it; this is the first thing that can, and it must be a
        // refusal rather than a warning — a rule is applied without anybody looking.
        if (facts.IsHazardous)
        {
            var (allowed, refused) = await SplitByHazardAsync(options, ct);

            decision.Excluded.AddRange(refused);
            options = allowed;

            if (refused.Count > 0)
                decision.Warnings.Add(
                    $"{refused.Count} option(s) were set aside because the service does not carry "
                  + "hazardous goods.");
        }

        decision.Options     = options;
        decision.Recommended = options.FirstOrDefault(o => o.Rank is not null);

        if (decision.Recommended is null)
            decision.Warnings.Add(
                $"'{rule.Name}' matched, but nothing it allows could be priced. "
              + string.Join(" ", decision.Excluded.Select(e => e.Reason)));
    }

    /// <summary>
    /// Splits shopped options into those the carrier's service will carry hazardous goods on, and
    /// those it will not. A service nobody has configured is left alone — silence is not a refusal.
    /// </summary>
    private async Task<(List<RateShopOptionModel> Allowed, List<RateShopExclusionModel> Refused)>
        SplitByHazardAsync(List<RateShopOptionModel> options, CancellationToken ct)
    {
        var allowed = new List<RateShopOptionModel>();
        var refused = new List<RateShopExclusionModel>();

        foreach (var option in options)
        {
            var carrier = await _db.Carriers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.UUID == option.CarrierUuid && !c.IsDelete, ct);

            var service = carrier is null
                ? null
                : await _services.ResolveAsync(carrier.Id, option.ServiceCode, ct);

            if (service is not null && !service.SupportsHazardous)
            {
                refused.Add(new RateShopExclusionModel
                {
                    CarrierUuid = option.CarrierUuid,
                    CarrierName = option.CarrierName,
                    ServiceCode = option.ServiceCode,
                    TotalAmount = option.TotalAmount,
                    Currency    = option.Currency,
                    Reason      = $"{option.CarrierName} {option.ServiceCode} does not carry hazardous goods."
                });

                continue;
            }

            allowed.Add(option);
        }

        // Re-ranked, because removing the winner would otherwise leave the list starting at rank 2.
        for (var i = 0; i < allowed.Count; i++)
            if (allowed[i].Rank is not null) allowed[i].Rank = i + 1;

        return (allowed, refused);
    }

    private static string Describe(ShippingRule rule) =>
        (rule.Carrier?.Name, rule.ServiceCode) switch
        {
            (null, _)              => $"Any carrier, {(rule.Strategy ?? "CHEAPEST").ToLowerInvariant()} first.",
            (var carrier, null)    => $"{carrier}, {(rule.Strategy ?? "CHEAPEST").ToLowerInvariant()} of its services.",
            var (carrier, service) => $"{carrier} on {service}."
        };

    // ── Applying ──────────────────────────────────────────────────────────────

    public async Task<ConsignmentRateModel?> ApplyAsync(
        Guid consignmentUuid, DateTime? shipDate, int userId, CancellationToken ct = default)
    {
        var decision = await EvaluateAsync(consignmentUuid, shipDate, ct);
        if (decision is null) return null;

        if (decision.MatchedRule is null)
            throw new ConflictException(
                "No shipping rule matches this consignment, so there is nothing to apply. "
              + string.Join(" ", decision.Warnings));

        if (decision.Recommended is null)
            throw new ConflictException(
                $"'{decision.MatchedRule.Name}' matched, but nothing it allows could be priced. "
              + string.Join(" ", decision.Excluded.Select(e => e.Reason)));

        var chosen = decision.Recommended;

        return await _shopping.AcceptAsync(consignmentUuid, new AcceptRateRequest
        {
            CarrierUuid        = chosen.CarrierUuid,
            CarrierAccountUuid = chosen.CarrierAccountUuid,
            ServiceCode        = chosen.ServiceCode,
            Source             = chosen.Source,
            ShipDate           = shipDate
        }, userId, ct);
    }

    // ── The facts a rule is judged against ────────────────────────────────────

    private static ShipmentFacts FactsOf(Consignment consignment, decimal chargeableKg)
    {
        var deliveries = consignment.Deliveries
            .Select(cd => cd.DeliveryOrder)
            .Where(d => d is not null && !d.IsDelete)
            .ToList();

        var packages = deliveries
            .SelectMany(d => d.Packages)
            .Where(p => !p.IsVoided && !p.IsDelete && p.ParentPackageId == null)
            .DistinctBy(p => p.Id)
            .ToList();

        var declared = packages.Any(p => p.DeclaredValue is not null)
            ? packages.Sum(p => p.DeclaredValue ?? 0m)
            : (decimal?)null;

        return new ShipmentFacts(
            ChargeableWeightKg:    chargeableKg,
            DeclaredValue:         declared,
            // One hazardous line makes the whole consignment hazardous. It travels on one vehicle.
            IsHazardous:           deliveries.SelectMany(d => d.Lines).Any(l => l.IsHazardous),
            HasCod:                consignment.CodAmount is > 0,
            OriginCountryIso:      consignment.ShipFromAddress?.CountryIsoCode,
            OriginPostcode:        consignment.ShipFromAddress?.PostalCode,
            DestinationCountryIso: consignment.ShipToAddress?.CountryIsoCode,
            DestinationPostcode:   consignment.ShipToAddress?.PostalCode);
    }
}

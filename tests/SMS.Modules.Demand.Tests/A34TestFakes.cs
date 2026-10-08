using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Demand.Tests;

// A34 (DEM): stand-ins for Logistics' route lookup, Tenancy, Inventory's lead-time calculator and Material's readiness
// and production service, shared by the A34 Demand test classes. Each treats another organization's rows as absent.

internal sealed class A34Routes : IFulfillmentRouteLookup, IVariantFulfillmentRoutes
{
    public readonly Dictionary<Guid, (Guid Org, FulfillmentRouteSummary Route)> Routes = [];
    public readonly Dictionary<Guid, FulfillmentRouteDefaults> Defaults = [];
    public readonly Dictionary<(Guid Org, Guid Variant), Guid> VariantRoutes = [];

    public FulfillmentRouteSummary Add(Guid org, string code, string category, params string[] steps)
    {
        var route = new FulfillmentRouteSummary(Guid.NewGuid(), code, code.Replace('_', ' '), true, false, false,
            steps.Contains(FulfillmentStepCode.Pack), steps.Contains(FulfillmentStepCode.Ship), steps) { Category = category };
        Routes[route.Uuid] = (org, route);
        return route;
    }

    public Task<IReadOnlyDictionary<Guid, FulfillmentRouteSummary>> GetAsync(
        Guid organizationId, IReadOnlyCollection<Guid> routeUuids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, FulfillmentRouteSummary>>(Routes
            .Where(r => r.Value.Org == organizationId && routeUuids.Contains(r.Key))
            .ToDictionary(r => r.Key, r => r.Value.Route));

    public Task<FulfillmentRouteDefaults> GetOrgDefaultsAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult(Defaults.GetValueOrDefault(organizationId) ?? new FulfillmentRouteDefaults(null, null));

    public Task<IReadOnlyList<FulfillmentRouteSummary>> ListActiveAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FulfillmentRouteSummary>>(
            Routes.Values.Where(r => r.Org == organizationId && r.Route.IsActive).Select(r => r.Route).ToList());

    public Task<IReadOnlyDictionary<Guid, Guid>> GetRouteUuidsAsync(
        Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(VariantRoutes
            .Where(r => r.Key.Org == organizationId && variantUuids.Contains(r.Key.Variant))
            .ToDictionary(r => r.Key.Variant, r => r.Value));
}

internal sealed class A34Tenants : ITenantSnapshotProvider
{
    public readonly Dictionary<Guid, HashSet<string>> Features = [];

    public void Enable(Guid org, params string[] features)
    {
        if (!Features.TryGetValue(org, out var set)) Features[org] = set = [];
        foreach (var f in features) set.Add(f);
    }

    public void Disable(Guid org, string feature) => Features.GetValueOrDefault(org)?.Remove(feature);

    public Task<TenantSnapshot?> GetSnapshotAsync(Guid organizationId) =>
        Task.FromResult<TenantSnapshot?>(new TenantSnapshot(true, Features.GetValueOrDefault(organizationId) ?? []));

    public void Invalidate(Guid organizationId) { }
}

internal sealed class FakeReadiness : IManufacturingReadiness
{
    public readonly Dictionary<(Guid Org, Guid Variant), ManufacturingReadinessInfo> Info = [];
    public readonly List<IReadOnlyCollection<Guid>> Calls = [];

    public void Ready(Guid org, Guid variant, string name = "Widget — Red", bool manufactured = true, bool bom = true, bool warehouse = true) =>
        Info[(org, variant)] = new ManufacturingReadinessInfo(variant, name, manufactured, bom, warehouse);

    public Task<IReadOnlyDictionary<Guid, ManufacturingReadinessInfo>> CheckAsync(
        Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default)
    {
        Calls.Add(variantUuids);
        return Task.FromResult<IReadOnlyDictionary<Guid, ManufacturingReadinessInfo>>(Info
            .Where(i => i.Key.Org == organizationId && variantUuids.Contains(i.Key.Variant))
            .ToDictionary(i => i.Key.Variant, i => i.Value));
    }
}

/// <summary>Total = <see cref="Days"/> (per variant when set); components as given, else one PICK_PACK of the whole total.</summary>
internal sealed class FakeLeadTimes : ILeadTimeCalculator
{
    public readonly List<(Guid Org, LeadTimeRequest Request)> Calls = [];
    public readonly Dictionary<Guid, IReadOnlyList<LeadTimeComponentResult>> Components = [];
    public int Days { get; set; } = 5;
    public Exception? Throw { get; set; }

    public Task<LeadTimeResult> CalculateAsync(Guid organizationId, LeadTimeRequest request, CancellationToken ct = default)
    {
        Calls.Add((organizationId, request));
        if (Throw is not null) throw Throw;
        var components = Components.GetValueOrDefault(request.VariantUuid)
                         ?? [new LeadTimeComponentResult(LeadTimeComponentCode.PickPack, "Pick & pack", Days, LeadTimeSource.OrgDefault)];
        var total = components.Sum(c => c.Days);
        var today = DateTime.UtcNow.Date;
        return Task.FromResult(new LeadTimeResult(total, today.AddDays(total),
            request.RequestedDate?.Date.AddDays(-total), request.RequestedDate is null ? null : today.AddDays(total) <= request.RequestedDate.Value.Date,
            request.RouteUuid, null, FulfillmentRouteCategory.Stock, components, DateTime.UtcNow));
    }
}

/// <summary>Material's make-to-order service: one PO per (order, line), DRAFT on create, PLANNED once planned.</summary>
internal sealed class FakeProduction : ISaleOrderProductionService
{
    public sealed class Po
    {
        public required Guid Org; public required Guid SaleOrder; public Guid? Line;
        public Guid Uuid = Guid.NewGuid(); public required string Number; public string Status = "DRAFT";
        public decimal Planned; public decimal Accepted; public Guid? Route; public bool Issued;
        public Guid? DeliveryUuid; public string? DeliveryNumber;
        public DateTime? RequiredDate; public DateTime? PlannedStartDate;
    }

    public readonly List<Po> Pos = [];
    public readonly List<(Guid Org, SaleOrderProductionRequest Request, int User)> CreateCalls = [];
    public readonly List<(Guid Org, IReadOnlyCollection<Guid> Uuids)> PlanCalls = [];
    public readonly List<(Guid Org, Guid SaleOrder, string Reason)> CancelCalls = [];
    public Exception? ThrowOnCreate { get; set; }
    public Exception? ThrowOnPlan { get; set; }
    public Func<Task>? OnCreate { get; set; }
    public Action? OnCancel { get; set; }
    private int _n;

    public Po Add(Guid org, Guid so, Guid? line, string status, decimal planned = 10m, Guid? route = null, bool issued = false)
    {
        var po = new Po { Org = org, SaleOrder = so, Line = line, Number = $"PROD-2026-{++_n:00000}", Status = status, Planned = planned, Route = route, Issued = issued };
        Pos.Add(po);
        return po;
    }

    private static SaleOrderProductionRef Ref(Po p, bool created) =>
        new(p.Uuid, p.Number, p.Line, p.Status, p.Planned, p.Accepted, p.Route, p.DeliveryUuid, p.DeliveryNumber, created);

    public async Task<IReadOnlyList<SaleOrderProductionRef>> CreateDraftsAsync(
        Guid organizationId, SaleOrderProductionRequest request, int userId, CancellationToken ct = default)
    {
        lock (_sync) CreateCalls.Add((organizationId, request, userId));
        if (OnCreate is not null) await OnCreate();
        if (ThrowOnCreate is not null) throw ThrowOnCreate;
        var result = new List<SaleOrderProductionRef>();
        lock (_sync)
        {
            foreach (var line in request.Lines)
            {
                var existing = Pos.FirstOrDefault(p => p.Org == organizationId && p.SaleOrder == request.SaleOrderUuid
                                                    && p.Line == line.SoLineUuid && p.Status != "CANCELLED" && p.Route is not null);
                if (existing is not null) { result.Add(Ref(existing, false)); continue; }
                var po = Add(organizationId, request.SaleOrderUuid, line.SoLineUuid, "DRAFT", line.Quantity, line.FulfillmentRouteUuid);
                po.RequiredDate = line.RequiredDate;
                po.PlannedStartDate = line.PlannedStartDate;
                result.Add(Ref(po, true));
            }
        }
        return result;
    }

    public async Task PlanDraftsAsync(Guid organizationId, IReadOnlyCollection<Guid> productionOrderUuids, int userId, CancellationToken ct = default)
    {
        lock (_sync) PlanCalls.Add((organizationId, productionOrderUuids));
        if (OnPlan is not null) await OnPlan();
        if (ThrowOnPlan is not null) throw ThrowOnPlan;
        lock (_sync)
            foreach (var po in Pos.Where(p => p.Org == organizationId && productionOrderUuids.Contains(p.Uuid) && p.Status == "DRAFT"))
                po.Status = "PLANNED";
    }

    private readonly object _sync = new();
    public Func<Task>? OnPlan { get; set; }

    private static readonly string[] Cancellable = ["DRAFT", "PLANNED", "MATERIAL_PENDING", "READY"];

    public Task<SaleOrderProductionCancellation> CancelForSaleOrderAsync(
        Guid organizationId, Guid saleOrderUuid, string reason, int userId, CancellationToken ct = default)
    {
        OnCancel?.Invoke();
        var cancelled = new List<SaleOrderProductionRef>();
        var running = new List<SaleOrderProductionRef>();
        lock (_sync)
        {
            CancelCalls.Add((organizationId, saleOrderUuid, reason));
            foreach (var po in Pos.Where(p => p.Org == organizationId && p.SaleOrder == saleOrderUuid && p.Status != "CANCELLED"))
            {
                if (Cancellable.Contains(po.Status) && !po.Issued) { po.Status = "CANCELLED"; cancelled.Add(Ref(po, false)); }
                else running.Add(Ref(po, false));
            }
        }
        return Task.FromResult(new SaleOrderProductionCancellation(cancelled, running));
    }

    public Task<IReadOnlyList<SaleOrderProductionRef>> GetForSaleOrderAsync(Guid organizationId, Guid saleOrderUuid, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<SaleOrderProductionRef>>(
                Pos.Where(p => p.Org == organizationId && p.SaleOrder == saleOrderUuid).Select(p => Ref(p, false)).ToList());
    }
}

/// <summary>D-12 / D-19 one BOM level's days per variant (default 1, the D-12 system default).</summary>
internal sealed class FakeLevelDays : SMS.Modules.Demand.Services.IManufacturingLevelDays
{
    public readonly Dictionary<Guid, int> Days = [];

    public Task<int> GetAsync(Guid organizationId, Guid variantUuid, CancellationToken ct = default) =>
        Task.FromResult(Days.GetValueOrDefault(variantUuid, 1));
}

internal static class A34
{
    public const string Inventory     = "MODULE_INVENTORY";
    public const string Logistics     = "MODULE_LOGISTICS";
    public const string Manufacturing = "MODULE_MANUFACTURING";
    public const string Demand        = "MODULE_DEMAND";

    public static BadRequestException BadRequest(string message) => new(message);
}

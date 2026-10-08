using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using SMS.Modules.Inventory.Services.LeadTimes;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Inventory.Tests.LeadTimeKit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// A34-PC-04 (T-C4-08, D-27, REV-03): the calculator's cache — one BOM traversal per key, the organization and the
/// resolved route in the key, and the request's dates computed on every call.
/// </summary>
public class LeadTimeCalculatorCacheTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private sealed class World
    {
        public string Name { get; } = Guid.NewGuid().ToString();
        public FakeFulfillmentRouteLookup Routes { get; } = new();
        public FakeBomStructureReader Boms { get; } = new();
        public RecordingMemoryCache Cache { get; } = new();

        // A fresh context per call (as per request in the host), one cache shared by all (a singleton in the host).
        public LeadTimeCalculator Calculator(Guid org, bool superAdmin = false)
        {
            var db = Db(Name, org, superAdmin);
            return new LeadTimeCalculator(db, new LeadTimeInputsLoader(db), Cache, Routes, Boms);
        }
    }

    private static async Task<(World W, Guid Fg)> ManufacturedAsync()
    {
        var w = new World();
        var mfg = w.Routes.Add(OrgA, "MFG_PICK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        var raw = await VariantAsync(Db(w.Name, OrgA), name: "Raw", configure: x => x.LeadTimeDays = 4);
        var fg = await VariantAsync(Db(w.Name, OrgA), SupplyMethod.Manufacture, route: mfg.Uuid, name: "FG",
            configure: x => x.ManufacturingLeadTimeDays = 5);
        w.Boms.Add(OrgA, fg.Uuid, 1m, (raw.Uuid, 1m, 0m));
        return (w, fg.Uuid);
    }

    [Fact]
    public async Task T_C4_08_the_same_calculation_within_5_minutes_does_not_traverse_the_BOM_again()
    {
        var (w, fg) = await ManufacturedAsync();

        var first = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(fg, 3));
        var calls = w.Boms.Calls;
        var second = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(fg, 3));

        calls.Should().BeGreaterThan(0);
        w.Boms.Calls.Should().Be(calls, "a cached result: no BOM re-traversal");
        second.Should().BeEquivalentTo(first);
        w.Cache.Entries.Should().ContainSingle().Which.Key.Should().Match<LeadTimeCacheKey>(k =>
            k.OrganizationId == OrgA && k.VariantUuid == fg && k.Quantity == 3m && k.UtcDate == DateTime.UtcNow.Date && k.RouteUuid != null);
        w.Cache.Entries.Single().Expiry.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Another_quantity_or_route_is_another_key()
    {
        var (w, fg) = await ManufacturedAsync();
        var stock = w.Routes.Add(OrgA, "PICK_AND_SHIP");

        await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(fg, 3));
        var calls = w.Boms.Calls;
        await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(fg, 4));
        w.Boms.Calls.Should().BeGreaterThan(calls, "the exact quantity is in the key");

        await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(fg, 3, stock.Uuid));
        w.Cache.Entries.Select(e => e.Key.RouteUuid).Distinct().Should().HaveCount(2, "the resolved route is in the key");
    }

    [Fact]
    public async Task REV_03_a_cached_result_still_computes_the_requested_date_fields_per_call()
    {
        var (w, fg) = await ManufacturedAsync();
        var today = DateTime.UtcNow.Date;

        var plain = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(fg, 1));
        var calls = w.Boms.Calls;
        var soon = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(fg, 1, RequestedDate: today.AddDays(2)));
        var later = await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(fg, 1, RequestedDate: today.AddDays(60)));

        w.Boms.Calls.Should().Be(calls, "all three share one cache entry");
        plain.LatestStartDate.Should().BeNull();
        soon.MeetsRequestedDate.Should().BeFalse();
        soon.LatestStartDate.Should().Be(today.AddDays(2 - plain.TotalLeadTimeDays));
        later.MeetsRequestedDate.Should().BeTrue();
        later.LatestStartDate.Should().Be(today.AddDays(60 - plain.TotalLeadTimeDays));
        later.EarliestDeliveryDate.Should().Be(today.AddDays(plain.TotalLeadTimeDays));
    }

    [Fact]
    public async Task A_cached_entry_of_one_org_never_answers_another_org_super_admin_included()
    {
        var (w, fg) = await ManufacturedAsync();
        await w.Calculator(OrgA).CalculateAsync(OrgA, new LeadTimeRequest(fg, 1));

        await FluentActions.Awaiting(() => w.Calculator(OrgB, superAdmin: true).CalculateAsync(OrgB, new LeadTimeRequest(fg, 1)))
            .Should().ThrowAsync<NotFoundException>();
        w.Cache.Entries.Should().OnlyContain(e => e.Key.OrganizationId == OrgA);
    }
}

/// <summary>A real <see cref="MemoryCache"/> that records the lead-time keys it stores and their expiry.</summary>
internal sealed class RecordingMemoryCache : IMemoryCache
{
    private readonly MemoryCache _inner = new(new MemoryCacheOptions());
    private readonly List<ICacheEntry> _created = [];

    public IReadOnlyList<(LeadTimeCacheKey Key, TimeSpan? Expiry)> Entries => _created
        .Where(e => e.Key is LeadTimeCacheKey)
        .Select(e => ((LeadTimeCacheKey)e.Key, e.AbsoluteExpirationRelativeToNow))
        .ToList();

    public ICacheEntry CreateEntry(object key)
    {
        var entry = _inner.CreateEntry(key);
        _created.Add(entry);
        return entry;
    }

    public void Remove(object key) => _inner.Remove(key);
    public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);
    public void Dispose() => _inner.Dispose();
}

# Addendum 46 C11 — Resolver query plan verification (A46-0290..0302)

Measured 2026-10-17 on SQL Server LocalDB (scratch database, own name `SMS_A46_Plan_*`, dropped after the run; never the shared/Azure DB). Demand migrations applied with `MigrateAsync`; 26,800 `demand.SupplierPricelists` rows seeded (1,000 products x 3 suppliers x 4 qty tiers x 2 date ranges = 24,000 for org A, + 2,400 org-B copies + 400 variant-specific rows; both `PricingDateBasis` values mixed, every 13th row inactive). Statistics `FULLSCAN`. Raw plans/IO: `docs/pricelist/query-plans/`.

Tests: `tests/SMS.Integration.Tests/Pricelist/ResolverPlanKit.cs`, `ResolverPlanTests.cs` (CI-sized class `ResolverPlanTests`, 2.6k rows; full class `ResolverPerfTests`, `[Trait("Category","Perf")]`, 26.8k rows). All skip when `(localdb)\MSSQLLocalDB` is unreachable. Run: `dotnet test tests/SMS.Integration.Tests --artifacts-path <scratch> --filter "FullyQualifiedName~Pricelist.ResolverP"`; plans are written to `$env:A46_PERF_OUT` (default `%TEMP%\a46-perf`).

## 1. What the resolver really sends (finding)

The real EF query (captured by a `DbCommandInterceptor`, replayed with the same parameters under `SET STATISTICS IO/TIME ON` + `SET STATISTICS XML ON`):

```sql
SELECT [s].[Id], [s].[CreatedBy], [s].[CreatedDate], [s].[CurrencyId], [s].[IsActive], [s].[LeadTimeDays], [s].[MinQuantity], [s].[ModifiedBy], [s].[ModifiedDate], [s].[Notes], [s].[OrganizationId], [s].[PricingDateBasis], [s].[Priority], [s].[ProductUuid], [s].[RowVersion], [s].[SupplierUuid], [s].[UUID], [s].[UnitPrice], [s].[ValidFrom], [s].[ValidTo], [s].[VariantUuid], [s].[VendorProductCode], [s].[VendorProductName]
FROM [demand].[SupplierPricelists] AS [s]
WHERE [s].[OrganizationId] = @__Org_0 AND [s].[IsActive] = CAST(1 AS bit) AND [s].[SupplierUuid] = @__request_SupplierUuid_1
  AND [s].[MinQuantity] <= @__request_Quantity_2 AND [s].[ProductUuid] = @__p_3
  AND ([s].[VariantUuid] = @__request_VariantUuid_4 OR [s].[VariantUuid] IS NULL)
```

* **There is no `OR` on `PricingDateBasis` in the real SQL.** Date-basis and validity are evaluated in memory after materialising the candidates (<= ~20 rows per product/supplier), so Option A/B (split query / filtered indexes) address a problem the real query does not have. The only `OR` is the variant OR (`VariantUuid = @v OR IS NULL`).
* The query selects the whole entity (23 columns incl. `Notes`, `RowVersion`, audit columns). `IX_SupplierPricelists_Lookup` cannot cover that, so every candidate row costs a key lookup.

## 2. Measured numbers (warm cache)

| Query | Plan | Key lookups | Logical reads | Server elapsed |
|---|---|---|---|---|
| **Real EF query (today)** | Index Seek `IX_SupplierPricelists_Lookup` (+ `IX_SupplierPricelists_Variant` for the OR branch) -> Nested Loops -> Clustered Index Seek (PK) | **1 operator (6-8 row lookups)** | **21-27** (6-8 rows) | 0 ms |
| Spec query (BR-37 text, OR on PricingDateBasis, mapped to this schema) | Index Seek `IX_SupplierPricelists_Lookup`, Sort | 0 | **3-4** | 0 ms |
| Real query shape, **projected** to the 11 columns it uses, **no** index change | Seek + lookup (lacks `UUID`) | 1 | 27 | 0 ms |
| Projected + upgraded `IX_SupplierPricelists_Lookup` (adds `UUID` to INCLUDE) | Index Seek only | **0** | **4** (8 rows) | 0 ms |
| Same, variant filtered in memory | Index Seek only | 0 | 4 | 0 ms |
| Same, split on variant (`UNION ALL`) | Two seeks | 0 | 8 | 0 ms |
| Real entity query after the index upgrade (no projection) | unchanged | 1 | 22-28 | 0 ms |

Every plan above is a **seek; no scan anywhere** (no Table/Clustered/Index Scan operator). Full table size: 26,800 rows.

End-to-end resolver call (EF translate + execute + materialise + in-memory date filter, fresh `DbContext` per call, warm, n=300, random products/suppliers): **p50 0.84 ms, p95 1.2 ms, max 4.3 ms** (other runs: p50 0.83-1.3 ms). Server-side elapsed for the statement is 0 ms (`STATISTICS TIME`), so the SQL part is well under 1 ms; the ~0.8 ms is client-side EF overhead, which the projection patch slightly reduces (fewer columns to materialise).

Concurrency (TS-58 / A46-0300..0302): 50 simultaneous resolutions on separate contexts/connections, varied products/suppliers/quantities/delivery dates: **34-418 ms total** across runs (limit 5 s), **0 deadlocks**, `sys.dm_exec_requests` sampled every 5 ms during the run -> **0 blocked requests** in every run. Read-only, no locks held beyond S-lock on pages (BR-19 confirmed).

## 3. Verdict vs targets

| Target (BR-37 / TS-57..59) | Real EF query today | With patch below |
|---|---|---|
| Index Seek on `IX_SupplierPricelists_Lookup`, not scan | PASS | PASS |
| 0 key lookups | **FAIL** (full-entity SELECT) | PASS |
| Logical reads <= 5 | **FAIL** (21-27) | PASS (4) |
| < 1 ms elapsed, warm | PASS server-side (0 ms); end-to-end p50 ~0.84 ms (borderline, p95 1.2 ms; spec allows up to 5 ms under load) | PASS |
| 50 concurrent: < 5 s, no deadlock/blocking | PASS | PASS |
| OR on PricingDateBasis -> scan? | N/A: not in the real SQL; spec SQL seeks (3-4 reads) | - |

No scan, so **neither Option A nor Option B is warranted** (the spec's own rule: "only add filtered indexes if the plan verification shows a scan"). A filtered index was tried too (`WHERE IsActive = 1`): the optimizer ignored it without a hint because the existing index is equivalent, and it adds write cost for nothing. The real defect is the missing `UUID` in the INCLUDE list plus the full-entity projection. This is **Option C**: project + widen the existing index.

## 4. Patch proposal for BE-1 (not applied by PERF)

**Resolver** (`PurchasePriceResolver.ResolveAsync`, candidates query) - project to what is consumed instead of materialising `SupplierPricelist`:

```csharp
var candidates = (await query
    .Select(r => new Candidate(r.Id, r.UUID, r.VariantUuid, r.MinQuantity, r.Priority, r.PricingDateBasis,
                               r.UnitPrice, r.CurrencyId, r.LeadTimeDays, r.ValidFrom, r.ValidTo))
    .ToListAsync(ct))
    .OrderBy(r => r.Priority).ThenBy(r => r.VariantUuid == null ? 1 : 0).ThenByDescending(r => r.MinQuantity).ThenBy(r => r.Id)
    .ToList();
// private sealed record Candidate(int Id, Guid UUID, Guid? VariantUuid, decimal MinQuantity, int Priority,
//     PricingDateBasis PricingDateBasis, decimal UnitPrice, Guid CurrencyId, int? LeadTimeDays, DateTime ValidFrom, DateTime ValidTo);
```

The currency-aware (C10) change keeps working unchanged: it needs only `UnitPrice`, `CurrencyId`, `PricingDateBasis`, validity and `LeadTimeDays` - all in the projection. If BE-1 adds more row columns to the result, add them to the projection **and** to the index INCLUDE.

**Index** - guarded, idempotent, one atomic rebuild of the existing index (`DROP_EXISTING`); append to the `A46_PricingFoundation` migration `Up` as its own `migrationBuilder.Sql` batch (no `Down` needed beyond leaving the wider index in place):

```sql
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SupplierPricelists_Lookup' AND object_id = OBJECT_ID('demand.SupplierPricelists'))
    CREATE NONCLUSTERED INDEX [IX_SupplierPricelists_Lookup] ON [demand].[SupplierPricelists]
        ([OrganizationId], [SupplierUuid], [ProductUuid], [IsActive], [ValidFrom], [ValidTo])
        INCLUDE ([UUID], [VariantUuid], [UnitPrice], [MinQuantity], [Priority], [PricingDateBasis], [CurrencyId], [LeadTimeDays]);
ELSE IF NOT EXISTS (SELECT 1 FROM sys.index_columns ic
                    JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE i.name = 'IX_SupplierPricelists_Lookup' AND i.object_id = OBJECT_ID('demand.SupplierPricelists')
                      AND ic.is_included_column = 1 AND c.name = 'UUID')
    CREATE NONCLUSTERED INDEX [IX_SupplierPricelists_Lookup] ON [demand].[SupplierPricelists]
        ([OrganizationId], [SupplierUuid], [ProductUuid], [IsActive], [ValidFrom], [ValidTo])
        INCLUDE ([UUID], [VariantUuid], [UnitPrice], [MinQuantity], [Priority], [PricingDateBasis], [CurrencyId], [LeadTimeDays])
        WITH (DROP_EXISTING = ON);
```

Tested: runs twice on the scratch DB (second run is a no-op), `ProposedIndexSql.Covering` in `ResolverPlanTests.cs`. Also update the index definition in the original A45 migration's `CREATE INDEX` only if BE-1 prefers fresh databases to get it directly; the guarded statement above already covers both fresh and existing databases (fresh: A45 creates the narrow one, A46 widens it). On a populated shared DB the rebuild of a ~100k-row index is sub-second; it is `ONLINE`-eligible only on Enterprise, so run at normal start-up (same as the other guarded migrations).

**Acceptance tests** (already written, in `ResolverPerfTests`): `G_ProposedProjectedResolverQuery_MeetsBr37` builds the projected EF query and asserts seek, 0 lookups, <= 5 reads after the index SQL; once BE-1 patches the resolver, the real-resolver capture in `ResolverPlanTests.Resolver_ReturnsAPrice_AndEmitsOneSeekableQuery` can be tightened to `KeyLookups == 0`.

## 5. Caveats

* Tests set `PendingModelChangesWarning` to ignore: other A46 agents edit the Demand model before their migration exists; only `SupplierPricelists` matters here. Remove the `ConfigureWarnings` line once `A46_PricingFoundation` is in.
* LocalDB is not Azure SQL; plan shape (seek vs scan, lookups) transfers, absolute timings do not. Re-run `Category=Perf` against a staging copy before relying on the 1 ms figure in production.
* The Variant OR uses `IX_SupplierPricelists_Variant` for the `VariantUuid = @v` branch (index union); with the widened index this costs 0 extra lookups (measured 4 reads). Splitting it (Option A style) was slower (8 reads) and is not recommended.
* Parameter sniffing: quantity is the only skewed parameter (`MinQuantity <= @q` is a residual predicate on the seek result, <= 20 rows), so no plan instability was observed across 300 randomised calls.

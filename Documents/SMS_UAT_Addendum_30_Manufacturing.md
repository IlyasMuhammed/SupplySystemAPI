# UAT Plan — Addendum 30 (Manufacturing, Production & Allocation Engine)

A30-P5-10. FSD §40. Written 2026-09-26, against the build completed through A30-P5-09 (69/79 tasks
excluding this one and the open A30-P4-21 HTTP E2E gap — see `docs/manufacturing/ADDENDUM-30-MANUFACTURING.md`).

## Prerequisites

- An organization with `MODULE_MANUFACTURING` enabled (Organization Features).
- A tester with a role holding: `BOM_*`, `PROD_*`, `SUPPLY_*`, `MI_*`, `QI_*`, `FGR_*`, `PROD_LEDGER_VIEW`,
  `ALLOCATION_*`, `SALE_ORDER_*`, `INVENTORY_VIEW`, `REPORT_VIEW`. Inventory Manager already holds the
  manufacturing set per `AuthDataSeeder`; use that role, or a second account with a narrower set for
  UAT-05's escalation checks (see its own notes).
- A second tester account (or a configured supervisor for the first) for §18.4's "inspector ≠ creator"
  rule in UAT-02 and the escalation notifications in UAT-05.
- One active warehouse.

## Test data — 5 products, 3 multi-level BOMs

One chain, three levels deep, so a single data set exercises chained manufacturing (UAT-03) without
needing a second unrelated product tree.

| # | Product | Type | Supply Method | Role in the chain |
|---|---|---|---|---|
| 1 | Steel Rod | Raw Material | Purchase | Bottom of the chain — bought, never made |
| 2 | Steel Bolt | Component | Manufacture | Made from Steel Rod (BOM 1) |
| 3 | Bolt Kit | Finished Good | Manufacture | Made from Steel Bolt (BOM 2) |
| 4 | Packaging Box | Raw Material | Purchase | Bought, used only in BOM 3 |
| 5 | Packaged Bolt Kit | Finished Good | Manufacture | Made from Bolt Kit + Packaging Box (BOM 3) |

| BOM | Output | Base Qty | Line 1 | Line 2 |
|---|---|---|---|---|
| 1 | Steel Bolt | 1 | Steel Rod × 1 (critical) | — |
| 2 | Bolt Kit | 1 | Steel Bolt × 4 (critical) | — |
| 3 | Packaged Bolt Kit | 1 | Bolt Kit × 1 (critical) | Packaging Box × 1 (non-critical) |

Set `IsAvailableForProduction` on Steel Rod, Steel Bolt, Bolt Kit and Packaging Box's default variants
before building the BOMs (V-B02 refuses a line otherwise — see [[variant-channel-availability]] /
D2 in the reality-check doc). Give Steel Rod and Packaging Box a default supplier and a purchase price
so an auto-raised purchase order (UAT-02's shortage path) has somewhere to go.

**This data has been seeded into SMSGlobal**, organization SCM-DEMO (`84d0a96d-52d4-4375-9260-357b46fb9d9f`),
2026-09-26, with the user's explicit go-ahead. All three BOMs are ACTIVE. UUIDs, for reference:

| Product | UUID | Default Variant UUID |
|---|---|---|
| UAT Steel Rod (Purchase) | `21b4451a-08bd-4b4a-b3d4-2cca896aa76e` | `1f4cc81c-7272-4405-818c-105dd2ec90c8` |
| UAT Steel Bolt (Manufacture) | `3b44ce04-f6b8-4e51-be1d-7989df1bed43` | `74b03b33-e4f0-4830-996c-f8e95fc0ca15` |
| UAT Bolt Kit (Manufacture) | `34b3a289-b35a-4aab-beb1-3dd0afd0b1bb` | `649b978a-06d7-4e2b-a5b1-4e18f3efd2d4` |
| UAT Packaging Box (Purchase) | `5a64496e-8b90-4582-9765-08b950dcf571` | `2f63bafb-86e8-4dea-a9d7-6270a6f22586` |
| UAT Packaged Bolt Kit (Manufacture) | `8dd4ae8a-c373-4b4c-a572-0ad422a68faf` | `c29c0c05-fef3-45a8-aeb9-a04555200881` |

| BOM | UUID |
|---|---|
| 1 — Steel Bolt ← Steel Rod ×1 | `aaaf85de-7717-44bf-89d0-7c23d6b611cd` |
| 2 — Bolt Kit ← Steel Bolt ×4 | `51df5ed9-44e1-4b9f-9604-a6930fd65866` |
| 3 — Packaged Bolt Kit ← Bolt Kit ×1 + Packaging Box ×1 | `dd30e459-74ba-43b0-9205-2b5aed53aa19` |

Not seeded: a default supplier on Steel Rod/Packaging Box (assign a real supplier that already exists
in SCM-DEMO before running UAT-02's purchase-shortage step — fabricating one was avoided) and any
starting stock (every UAT script below deliberately starts from zero on-hand quantity).

## UAT-01 — BOM lifecycle: draft → submit → approve → activate → version

**Covers**: §7–9, BOM approval four-eyes rule, versioning.

1. As the tester, create BOM 1 (Steel Bolt ← Steel Rod × 1) as a draft. **Expect**: saved as DRAFT,
   version 1.
2. Submit it. **Expect**: status SUBMITTED; the submitter cannot approve their own submission if they
   try (400, "cannot approve it").
3. As the second tester, approve it. **Expect**: status APPROVED.
4. Activate it. **Expect**: status ACTIVE; `activatedAt`/`activatedBy` set.
5. Repeat steps 1–4 for BOM 2 (Bolt Kit ← Steel Bolt × 4) and BOM 3 (Packaged Bolt Kit ← Bolt Kit × 1 +
   Packaging Box × 1, the second line non-critical).
6. Create a new version of BOM 1 (`/new-version`). **Expect**: a DRAFT version 2 with the same lines,
   same `TraceId` as version 1; BOM 1 version 1 stays ACTIVE until version 2 is itself activated.
7. Try to delete BOM 1 version 1 while it is ACTIVE. **Expect**: refused — only DRAFT/REJECTED can be
   edited or deleted.

## UAT-02 — A single production order end to end, including a purchased shortage

**Covers**: §11–13, §16–19, allocation engine, notifications PROD_CREATED/PROD_READY/
PROD_SHORTAGE/QI_REQUIRED/QI_COMPLETED/FGR_CONFIRMED/PROD_COMPLETED, SR_CREATED.

1. With zero Steel Rod on hand, create a production order for 10 Bolt Kits (needs 40 Steel Bolts,
   which need 40 Steel Rods) and plan it. **Expect**: status MATERIAL_PENDING; a `SR-` supply
   requirement raised for the Steel Rod shortage with `supplyMethod = PURCHASE`; a PROD_CREATED and a
   PROD_SHORTAGE notification land for the order's creator; an SR_CREATED notification escalates to
   their supervisor.
2. Open the auto-raised purchase order for Steel Rod (linked from the supply requirement) and receive
   it in full via GRN. **Expect**: the next allocation run reserves the Steel Rod for the order's own
   material requirement; the order becomes READY; a PROD_READY notification lands; an
   ALLOCATION_COMPLETED notification lands for the Steel Rod line specifically.
3. Issue the Steel Rod to the floor (`STANDARD` material issue, confirm it). **Expect**: stock leaves,
   the requirement's `IssuedQuantity` updates, an `MI_CONFIRMED` timeline event appears on the order.
4. Start the order, report 10 units of output, complete it. **Expect**: status QUALITY_INSPECTION; a
   QI_REQUIRED notification escalates to the creator's supervisor (§18.4 — the creator cannot inspect
   their own order).
5. As the supervisor, record a quality inspection: 9 accepted, 1 rejected. **Expect**: QI accepted, a
   QI_COMPLETED notification lands for the order's creator, the order's own `RejectedQuantity` = 1.
6. Receive a finished goods receipt for 9 units, confirmed. **Expect**: stock credited for 9 Bolt
   Kits; the order moves to COMPLETED (all of the QI's accepted quantity has arrived); an
   FGR_CONFIRMED and a PROD_COMPLETED notification land.
7. Open `GET /api/production-orders/{uuid}/ledger` (or the order's own "Ledger" tab). **Expect**: one
   DEBIT row for the Steel Rod issue, one synthesised DEBIT row for the 1-unit QI rejection (scrap),
   one CREDIT row for the 9-unit FGR, yield 90% (9 ÷ (9+1)).

## UAT-03 — Chained manufacturing: a manufactured shortage raises and completes a child order

**Covers**: §6.4.1, §13 BR-S06, D1/D3, the SO→PROD→SR→PROD(child)→FGR trace chain (A30-P5-07).

1. With zero Steel Bolt and zero Steel Rod on hand, create and plan a production order for 5 Bolt
   Kits (needs 20 Steel Bolts). **Expect**: a supply requirement for Steel Bolt is raised with
   `supplyMethod = MANUFACTURE` (not Purchase — Steel Bolt's own supply method), which **itself**
   raises and plans a **child** production order for 20 Steel Bolts (needing 20 Steel Rods, which are
   purchased — same shortage-to-PO path as UAT-02). A PROD_CHAINED notification lands for the
   **parent** order's creator, naming both orders.
2. Open the parent order's detail page. **Expect**: the child order for Steel Bolt is listed under
   "Child Orders", with the same `TraceId` as the parent (check via `/documents/{parentUuid}/history`
   or the order's own `traceId` field — they must match).
3. Receive the child's own Steel Rod shortage (GRN), issue it, start the child, report 20 units,
   complete it, inspect (20 accepted), and confirm an FGR for all 20. **Expect**: the child order
   reaches COMPLETED, and — with no separate action — the parent's own Steel Bolt requirement is now
   reserved and the parent order becomes READY (its own next allocation run, triggered by the child's
   FGR confirm, is what does this).
4. Finish the parent the same way as UAT-02 steps 3–6. **Expect**: it completes normally; the whole
   walk (parent PROD → SR → child PROD → child's own MI/QI/FGR → parent MI/QI/FGR) shows as one
   timeline via the shared `TraceId`.
5. Call `GET /api/reports/manufacturing/chained/{parentUuid}`. **Expect**: `totalOrdersInChain = 2`,
   `maxDepth = 1`, both orders' own cycle time populated, and a total chain cycle time once both are
   complete.

## UAT-04 — Sale order fulfilled by manufacturing

**Covers**: Phase 4 Track C — D1's "manufacturing SO → production order" path, `SaleOrderFulfillmentListener`.

1. Confirm A29's own sale-order settings allow auto-processing of a deficit (`AutoPoApprovalMode` not
   set to fully manual — any setting works, since a MANUFACTURE line never reaches the PO pipeline at
   all).
2. Raise a sale order line for Packaged Bolt Kit, quantity 2, with zero stock and zero Bolt Kit/Steel
   Bolt/Steel Rod on hand anywhere. Confirm the order. **Expect**: `AutoPoCreationJob` sees
   `SupplyMethod = MANUFACTURE` and routes to `SaleOrderManufacturingService` instead of the purchase
   pipeline — no purchase order is ever created for this line. A `SALES_ORDER` demand is registered
   and a production order for 2 Packaged Bolt Kits is raised (which in turn chains down through Bolt
   Kit → Steel Bolt → Steel Rod, same as UAT-03, three levels deep this time).
3. Walk every order in the chain to a confirmed FGR, deepest first (Steel Bolt's own child, then Bolt
   Kit, then Packaged Bolt Kit), the same way as UAT-02/UAT-03.
4. After the top-level Packaged Bolt Kit order's FGR confirms. **Expect**: the sale order line's own
   `DeficitQty` drops to 0 and its status moves toward fulfilled — `SaleOrderFulfillmentListener`
   re-parented the resulting hold from the allocation engine's own bookkeeping onto the exact
   `SALES_ORDER`-sourced reservation the delivery pipeline already reads, with **no Logistics changes
   needed**. Raise a delivery for the sale order and confirm it picks up the 2 units without any
   manual intervention.

## UAT-05 — Reports and remaining notifications

**Covers**: A30-P5-01 (the 10 notifications) and A30-P5-02..06 (the 5 new reports).

1. After UAT-02 through UAT-04 have run, call each of:
   `GET /api/reports/manufacturing/production-efficiency`,
   `.../quality-scrap`, `.../material-issues`, `.../finished-goods-receipts`,
   `.../ledger-reconciliation` (all with no date filter, or a wide one covering "today").
   **Expect**: production-efficiency's totals match the sum of every order run above;
   quality-scrap's totals match every QI recorded; material-issues/finished-goods-receipts list every
   issue/FGR confirmed above; ledger-reconciliation's `flaggedCount` is 0 (nothing here should ever be
   inconsistent through the normal UI flow — a non-zero count here is itself a bug to investigate, not
   an expected UAT outcome).
2. Confirm the reports dashboard (`/portal/pages/reports/manufacturing`) renders all six tabs with this
   data without a console error. **This is the one check this plan could not do ahead of UAT** — A30-
   P5-09's own note records that the page was verified by `ng build` and Karma only, not a live browser
   session, so this is UAT's first real look at it.
3. Spot-check the notification list (bell icon / `/api/notifications`) for the account that ran UAT-02:
   confirm at least one of each of PROD_CREATED, PROD_SHORTAGE, PROD_READY, QI_COMPLETED, FGR_CONFIRMED,
   PROD_COMPLETED, SR_CREATED, ALLOCATION_COMPLETED appears (QI_REQUIRED and PROD_CHAINED land on the
   supervisor/parent-creator account instead — check those there).
4. As a user who holds `REPORT_VIEW` but not `PROD_LEDGER_VIEW` (create a throwaway role for this if
   none exists), confirm every `/api/reports/manufacturing/**` call returns 403, and the "Reports" menu
   item under Manufacturing does not render permission-checked children.

## Sign-off

Each script above records PASS/FAIL per numbered step, the tester's name and the date, and — for any
FAIL — the exact request/response or screenshot. File defects against the task register's own DONE
notes for the phase the failing step belongs to (e.g. a UAT-02 step 5 failure is a QI/A30-P4 defect,
not a UAT-05/reports defect), so a fix lands in the right place rather than as a new catch-all ticket.

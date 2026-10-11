# SMS — Task Register & Parallel Execution Checklist

## FSD Addendum 38 — Point of Sale Module

| Property | Value |
|---|---|
| Source Document | SMS-FSD-ADD-038 v1.0 |
| Date | 2026-10-10 |
| Total Tasks | 350+ |
| Parallel Workstreams | 6 (A–F) |
| Total Estimated Effort | 36 dev-days (can compress to ~18 calendar-days with 3 parallel devs) |

---

## Parallel Workstream Map

```
                        WEEK 1                    WEEK 2                    WEEK 3
Dev 1 (Backend)    ──[WS-A: Migrations]──[WS-B: Services]───────────[WS-E: Integration]──
Dev 2 (Full-Stack) ──[WS-C: API Ctrl]───[WS-C contd.]──[WS-F: Admin UI]────────────────
Dev 3 (Frontend)   ──[──────wait──────]──[WS-D: PWA Client]─────────────────────────────
QA                 ──[──────────────────────────────]──[WS-G: Testing]───────────────────
```

### Workstream Legend

| Stream | Owner Profile | Focus | Can Start |
|---|---|---|---|
| **WS-A** | Backend / DBA | EF Core entities, migrations, SQL DDL, triggers, seeds | Day 1 |
| **WS-B** | Backend | Domain services, business logic, state machines | After WS-A (Phase 1 tables) |
| **WS-C** | Full-Stack | API controllers, DTOs, validation, endpoint tests | After WS-A (Phase 1 tables) |
| **WS-D** | Frontend | React PWA, Dexie.js, Service Worker, UI components | After WS-C (sync APIs exist) |
| **WS-E** | Backend | Inventory integration, finance integration, SignalR hub | After WS-B (order service) |
| **WS-F** | Full-Stack | Back-office admin screens (SMS web app, not PWA) | After WS-C (APIs ready) |
| **WS-G** | QA | E2E tests, integration tests, offline scenarios | After WS-D + WS-E |

### Cross-Stream Dependencies

```
WS-A ──┬── WS-B (needs entities)
       ├── WS-C (needs entities)
       │
WS-B ──┬── WS-E (needs PosOrderService)
       │
WS-C ──┬── WS-D (needs sync + order APIs)
       ├── WS-F (needs admin APIs)
       │
WS-D ──┬── WS-G (needs PWA running)
WS-E ──┘
```

---

## Status Symbols

- ⬜ Not started
- 🔲 In progress
- ✅ Done
- 🚫 Blocked (state dependency)
- ⚠️ Needs review

---

## PHASE 1 — POS Schema, Stores, Terminals (4 days)

### WS-A: Migrations & Entities (Dev 1 — Days 1–4)

#### A1. POS Schema Creation (C1)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| A1.01 | Create EF Core migration: `CREATE SCHEMA pos` (M038.1) | WS-A | ⬜ | — | 0.5h |
| A1.02 | Add `pos` to `SchemaConstants.cs` | WS-A | ⬜ | — | 0.25h |
| A1.03 | Register POS DbContext configuration in `OnModelCreating` | WS-A | ⬜ | A1.01 | 0.5h |
| A1.04 | Update `tenant.Modules` seed: set POS `is_available = 1` (M038.14) | WS-A | ⬜ | A1.01 | 0.25h |

#### A2. Stores Entity & Table (C1)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| A2.01 | Create `Store.cs` entity class (22 columns per FSD §2.3.1) | WS-A | ⬜ | A1.01 | 1h |
| A2.02 | Create `StoreConfiguration.cs` — EF Core Fluent API (PK, FKs, indexes, query filter) | WS-A | ⬜ | A2.01 | 1h |
| A2.03 | Add FK to `inventory.Warehouses` (1:1 store–warehouse binding) | WS-A | ⬜ | A2.01 | 0.25h |
| A2.04 | Add FK to `lookups.TaxRates` for `default_tax_rate_id` | WS-A | ⬜ | A2.01 | 0.25h |
| A2.05 | Add FK to `auth.Users` for `created_by` | WS-A | ⬜ | A2.01 | 0.25h |
| A2.06 | Create unique index: `(org_id, code)` | WS-A | ⬜ | A2.02 | 0.25h |
| A2.07 | Create index: `(org_id, is_active)` | WS-A | ⬜ | A2.02 | 0.1h |
| A2.08 | Create index: `(org_id, warehouse_id)` | WS-A | ⬜ | A2.02 | 0.1h |
| A2.09 | Create index: `(org_id, modified_at)` — sync support | WS-A | ⬜ | A2.02 | 0.1h |
| A2.10 | Add `HasQueryFilter(s => s.OrgId == _currentTenant.OrgId)` | WS-A | ⬜ | A2.02 | 0.25h |
| A2.11 | Generate EF migration M038.2 — verify SQL matches FSD DDL | WS-A | ⬜ | A2.02 | 0.5h |
| A2.12 | Apply migration to dev DB, verify table, constraints, indexes | WS-A | ⬜ | A2.11 | 0.25h |

#### A3. Terminals Entity & Table (C2)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| A3.01 | Create `Terminal.cs` entity class (14 columns per FSD §3.2.1) | WS-A | ⬜ | A2.11 | 0.75h |
| A3.02 | Create `TerminalConfiguration.cs` — Fluent API (PK, FKs, indexes) | WS-A | ⬜ | A3.01 | 0.75h |
| A3.03 | Add FK to `pos.Stores` for `store_id` | WS-A | ⬜ | A3.01 | 0.25h |
| A3.04 | Create unique index: `(org_id, store_id, code)` | WS-A | ⬜ | A3.02 | 0.1h |
| A3.05 | Create index: `(store_id, is_active)` | WS-A | ⬜ | A3.02 | 0.1h |
| A3.06 | Create filtered index: `(device_uuid)` WHERE NOT NULL | WS-A | ⬜ | A3.02 | 0.25h |
| A3.07 | Generate EF migration M038.3 — verify SQL | WS-A | ⬜ | A3.02 | 0.5h |
| A3.08 | Apply migration to dev DB, verify | WS-A | ⬜ | A3.07 | 0.25h |

### WS-C: API Controllers (Dev 2 — Days 1–4, parallel with WS-A after A2.11)

#### C1. Store Management API (§15.1 — Endpoints 1–5)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| C1.01 | Create `StoreDto.cs` (response DTO) | WS-C | ⬜ | A2.01 | 0.5h |
| C1.02 | Create `CreateStoreDto.cs` (request DTO with validation attributes) | WS-C | ⬜ | A2.01 | 0.5h |
| C1.03 | Create `UpdateStoreDto.cs` (request DTO) | WS-C | ⬜ | A2.01 | 0.25h |
| C1.04 | Create `StoresController.cs` with `[RequiresModule("POS")]` | WS-C | ⬜ | A2.11 | 0.5h |
| C1.05 | Implement `GET /api/pos/stores` — list stores for current tenant | WS-C | ⬜ | C1.04 | 0.5h |
| C1.06 | Implement `GET /api/pos/stores/{id}` — store detail | WS-C | ⬜ | C1.04 | 0.25h |
| C1.07 | Implement `POST /api/pos/stores` — create store | WS-C | ⬜ | C1.04 | 0.75h |
| C1.08 | Validate 1:1 warehouse binding (POS-STR-01, POS-STR-02) — no duplicate warehouse links | WS-C | ⬜ | C1.07 | 0.5h |
| C1.09 | Implement `PUT /api/pos/stores/{id}` — update store | WS-C | ⬜ | C1.04 | 0.5h |
| C1.10 | Implement `PATCH /api/pos/stores/{id}/status` — activate/deactivate (POS-STR-03) | WS-C | ⬜ | C1.04 | 0.5h |
| C1.11 | Deactivation validation: all shifts must be closed (POS-STR-03) | WS-C | ⬜ | C1.10 | 0.25h |
| C1.12 | Unit tests: Store CRUD, warehouse binding validation, deactivation cascade | WS-C | ⬜ | C1.10 | 1h |

#### C2. Terminal API (§15.2 — Endpoints 6–11)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| C2.01 | Create `TerminalDto.cs`, `CreateTerminalDto.cs`, `BindDeviceDto.cs` | WS-C | ⬜ | A3.01 | 0.5h |
| C2.02 | Create `TerminalsController.cs` with `[RequiresModule("POS")]` | WS-C | ⬜ | A3.07 | 0.25h |
| C2.03 | Implement `GET /api/pos/stores/{storeId}/terminals` — list terminals | WS-C | ⬜ | C2.02 | 0.5h |
| C2.04 | Implement `POST /api/pos/stores/{storeId}/terminals` — create terminal | WS-C | ⬜ | C2.02 | 0.5h |
| C2.05 | Implement `PUT /api/pos/terminals/{id}` — update terminal | WS-C | ⬜ | C2.02 | 0.5h |
| C2.06 | Implement `POST /api/pos/terminals/{id}/bind` — device UUID binding (§3.2 flow) | WS-C | ⬜ | C2.02 | 0.75h |
| C2.07 | Implement `POST /api/pos/terminals/{id}/unbind` — unbind device | WS-C | ⬜ | C2.06 | 0.25h |
| C2.08 | Implement `GET /api/pos/terminals/{id}/status` — connectivity status (heartbeat check) | WS-C | ⬜ | C2.02 | 0.5h |
| C2.09 | Validate POS-TRM-01: one active shift per terminal | WS-C | ⬜ | C2.04 | 0.25h |
| C2.10 | Unit tests: Terminal CRUD, device binding/unbinding, heartbeat | WS-C | ⬜ | C2.08 | 0.75h |

---

## PHASE 2 — Shifts, Cash Management, Cashier Sessions (5 days)

### WS-A: Migrations & Entities (Dev 1 — Days 5–9)

#### A4. Shifts Entity & Table (C3)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| A4.01 | Create `Shift.cs` entity class (32 columns per FSD §4.2.1) | WS-A | ⬜ | A3.07 | 1h |
| A4.02 | Create `ShiftConfiguration.cs` — Fluent API (FKs, indexes, check constraints) | WS-A | ⬜ | A4.01 | 1h |
| A4.03 | Add FK to `pos.Stores`, `pos.Terminals`, `auth.Users` (opened_by) | WS-A | ⬜ | A4.01 | 0.25h |
| A4.04 | Add CHECK constraint: `status IN (0,1,2,3,4,5)` | WS-A | ⬜ | A4.02 | 0.1h |
| A4.05 | Create unique index: `(org_id, shift_number)` | WS-A | ⬜ | A4.02 | 0.1h |
| A4.06 | Create index: `(terminal_id, status)` | WS-A | ⬜ | A4.02 | 0.1h |
| A4.07 | Create index: `(store_id, status)` | WS-A | ⬜ | A4.02 | 0.1h |
| A4.08 | Create index: `(org_id, opened_at)` | WS-A | ⬜ | A4.02 | 0.1h |
| A4.09 | Create `ShiftStatus` enum: Draft=0, Open=1, Closing=2, Closed=3, Reconciled=4, Disputed=5 | WS-A | ⬜ | A4.01 | 0.25h |
| A4.10 | Generate EF migration M038.4 — verify SQL matches FSD DDL | WS-A | ⬜ | A4.02 | 0.5h |
| A4.11 | Apply migration to dev DB, verify | WS-A | ⬜ | A4.10 | 0.25h |

#### A5. Cash Movements Entity & Table (C3)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| A5.01 | Create `CashMovement.cs` entity class (12 columns per FSD §4.2.2) | WS-A | ⬜ | A4.10 | 0.5h |
| A5.02 | Create `CashMovementConfiguration.cs` — Fluent API | WS-A | ⬜ | A5.01 | 0.5h |
| A5.03 | Create `CashMovementDirection` enum: CashIn=0, CashOut=1 | WS-A | ⬜ | A5.01 | 0.1h |
| A5.04 | Create `CashMovementReason` enum: FloatTopUp=0, SafeDrop=1, PettyCash=2, ChangeRun=3, Other=4 | WS-A | ⬜ | A5.01 | 0.1h |
| A5.05 | Create index: `(shift_id, direction)` | WS-A | ⬜ | A5.02 | 0.1h |
| A5.06 | Create unique filtered index: `(org_id, local_id)` WHERE local_id IS NOT NULL | WS-A | ⬜ | A5.02 | 0.25h |
| A5.07 | Generate EF migration M038.5 — verify SQL | WS-A | ⬜ | A5.02 | 0.5h |
| A5.08 | Apply migration to dev DB, verify | WS-A | ⬜ | A5.07 | 0.25h |

#### A6. Auth.Users POS Extensions (C4)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| A6.01 | Add `pos_pin` (VARCHAR 100, nullable) to `User.cs` entity | WS-A | ⬜ | A2.11 | 0.25h |
| A6.02 | Add `pos_role` (TINYINT, nullable) to `User.cs` entity | WS-A | ⬜ | A2.11 | 0.25h |
| A6.03 | Add `default_store_id` (BIGINT, nullable, FK → pos.Stores) to `User.cs` | WS-A | ⬜ | A2.11 | 0.25h |
| A6.04 | Create `PosRole` enum: Cashier=0, Supervisor=1 | WS-A | ⬜ | A6.02 | 0.1h |
| A6.05 | Update `UserConfiguration.cs` — add FK, nullable handling | WS-A | ⬜ | A6.03 | 0.25h |
| A6.06 | Generate EF migration M038.6 — verify ALTER TABLE SQL | WS-A | ⬜ | A6.05 | 0.5h |
| A6.07 | Apply migration to dev DB, verify columns added | WS-A | ⬜ | A6.06 | 0.25h |

#### A7. Cashier Sessions Entity & Table (C4)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| A7.01 | Create `CashierSession.cs` entity class (16 columns per FSD §5.3.1) | WS-A | ⬜ | A6.06 | 0.75h |
| A7.02 | Create `CashierSessionConfiguration.cs` — Fluent API | WS-A | ⬜ | A7.01 | 0.5h |
| A7.03 | Add FK to `pos.Shifts`, `pos.Terminals`, `auth.Users` | WS-A | ⬜ | A7.01 | 0.25h |
| A7.04 | Create index: `(shift_id, is_active)` | WS-A | ⬜ | A7.02 | 0.1h |
| A7.05 | Create index: `(user_id, started_at)` | WS-A | ⬜ | A7.02 | 0.1h |
| A7.06 | Create unique filtered index: `(org_id, local_id)` WHERE local_id IS NOT NULL | WS-A | ⬜ | A7.02 | 0.25h |
| A7.07 | Generate EF migration M038.7 — verify SQL | WS-A | ⬜ | A7.02 | 0.5h |
| A7.08 | Apply migration to dev DB, verify | WS-A | ⬜ | A7.07 | 0.25h |

### WS-B: Backend Services (Dev 1 or Dev 2 — Days 5–9, parallel after entities)

#### B1. Shift Lifecycle Service (C3)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| B1.01 | Create `IShiftService.cs` interface | WS-B | ⬜ | A4.10 | 0.25h |
| B1.02 | Create `ShiftService.cs` — DI registration | WS-B | ⬜ | B1.01 | 0.25h |
| B1.03 | Implement `OpenShiftAsync()` — create shift DRAFT → OPEN transition | WS-B | ⬜ | B1.02 | 1h |
| B1.04 | Validate POS-SHF-01: only supervisor or `pos.shift.open` permission | WS-B | ⬜ | B1.03 | 0.25h |
| B1.05 | Validate POS-SHF-02: opening float must be entered (can be 0) | WS-B | ⬜ | B1.03 | 0.25h |
| B1.06 | Validate POS-TRM-01: terminal can have at most one OPEN/CLOSING shift | WS-B | ⬜ | B1.03 | 0.25h |
| B1.07 | Implement shift number auto-generation: `SH-{store_code}-{YYYYMMDD}-{seq}` (POS-SHF-09) | WS-B | ⬜ | B1.03 | 0.5h |
| B1.08 | Implement `InitiateCloseAsync()` — OPEN → CLOSING transition (POS-SHF-04) | WS-B | ⬜ | B1.03 | 0.75h |
| B1.09 | Validate POS-SHF-04: block new orders when CLOSING, allow in-progress to complete | WS-B | ⬜ | B1.08 | 0.25h |
| B1.10 | Implement `SubmitClosingCountAsync()` — CLOSING → CLOSED (enter actual_cash_closing) | WS-B | ⬜ | B1.08 | 0.75h |
| B1.11 | Compute expected_cash_closing formula (POS-SHF-05): `opening_float + cash_sales + cash_in - cash_out - cash_refunds` | WS-B | ⬜ | B1.10 | 0.5h |
| B1.12 | Compute cash_variance: `actual_cash_closing - expected_cash_closing` | WS-B | ⬜ | B1.10 | 0.25h |
| B1.13 | Auto-flag: if `|variance| > variance_threshold` → DISPUTED, else RECONCILED (POS-SHF-06) | WS-B | ⬜ | B1.12 | 0.25h |
| B1.14 | Implement `ReconcileDisputeAsync()` — supervisor resolves DISPUTED → RECONCILED (POS-SHF-07) | WS-B | ⬜ | B1.13 | 0.5h |
| B1.15 | Validate POS-SHF-10: RECONCILED shift immutable (only variance_note can be appended) | WS-B | ⬜ | B1.14 | 0.25h |
| B1.16 | Implement `UpdateRunningTotalsAsync()` — recalculate shift totals on each cash event | WS-B | ⬜ | B1.03 | 0.5h |
| B1.17 | Unit tests: full shift lifecycle (DRAFT → OPEN → CLOSING → CLOSED → RECONCILED) | WS-B | ⬜ | B1.14 | 1h |
| B1.18 | Unit tests: shift DISPUTED path with supervisor resolution | WS-B | ⬜ | B1.17 | 0.5h |
| B1.19 | Unit tests: edge cases (double open, close without count, closed immutability) | WS-B | ⬜ | B1.17 | 0.5h |

#### B2. Cash Movement Service (C3)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| B2.01 | Create `ICashMovementService.cs` interface | WS-B | ⬜ | A5.07 | 0.25h |
| B2.02 | Create `CashMovementService.cs` | WS-B | ⬜ | B2.01 | 0.25h |
| B2.03 | Implement `RecordCashMovementAsync()` — validate shift is OPEN, create movement | WS-B | ⬜ | B2.02 | 0.75h |
| B2.04 | Update shift `total_cash_in` / `total_cash_out` on movement | WS-B | ⬜ | B2.03, B1.16 | 0.5h |
| B2.05 | Recalculate `expected_cash_closing` after each movement | WS-B | ⬜ | B2.04 | 0.25h |
| B2.06 | Handle `local_id` dedup for offline-created movements | WS-B | ⬜ | B2.03 | 0.25h |
| B2.07 | Unit tests: cash in, cash out, dedup, shift total updates | WS-B | ⬜ | B2.05 | 0.5h |

#### B3. PIN Authentication & Cashier Session Service (C4)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| B3.01 | Create `IPosPinService.cs` interface | WS-B | ⬜ | A6.06 | 0.25h |
| B3.02 | Implement `SetPinAsync()` — hash PIN with bcrypt cost factor 10 (POS-CSH-03) | WS-B | ⬜ | B3.01 | 0.5h |
| B3.03 | Validate PIN length 4–6 digits (POS-CSH-02) | WS-B | ⬜ | B3.02 | 0.25h |
| B3.04 | Validate PIN unique per store (POS-CSH-01): no two active users in same store with same PIN | WS-B | ⬜ | B3.02 | 0.5h |
| B3.05 | Implement `VerifyPinAsync()` — bcrypt comparison, return user context + permissions | WS-B | ⬜ | B3.02 | 0.5h |
| B3.06 | Implement failed PIN lockout: 5 consecutive failures → 5-minute lock (POS-CSH-07) | WS-B | ⬜ | B3.05 | 0.5h |
| B3.07 | Implement supervisor unlock for locked terminals | WS-B | ⬜ | B3.06 | 0.25h |
| B3.08 | Create `ICashierSessionService.cs` interface | WS-B | ⬜ | A7.07 | 0.25h |
| B3.09 | Implement `StartOrResumeSessionAsync()` — PIN → create/resume session | WS-B | ⬜ | B3.08 | 0.75h |
| B3.10 | Implement cashier switch logic (POS-CSH-04): end previous session, create new | WS-B | ⬜ | B3.09 | 0.5h |
| B3.11 | Implement POS-CSH-05: clear draft orders on cashier switch | WS-B | ⬜ | B3.10 | 0.25h |
| B3.12 | Implement POS-CSH-06: single-cashier store can skip PIN | WS-B | ⬜ | B3.09 | 0.25h |
| B3.13 | Implement `EndSessionAsync()` — end cashier session | WS-B | ⬜ | B3.09 | 0.25h |
| B3.14 | Update cashier session running totals on order completion | WS-B | ⬜ | B3.09 | 0.5h |
| B3.15 | Unit tests: PIN set/verify/lockout, session start/switch/end | WS-B | ⬜ | B3.13 | 1h |

### WS-C: API Controllers (Dev 2 — Days 5–9, parallel)

#### C3. Shift API (§15.3 — Endpoints 12–17)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| C3.01 | Create `ShiftDto.cs`, `OpenShiftDto.cs`, `CloseShiftDto.cs`, `ReconcileShiftDto.cs` | WS-C | ⬜ | A4.01 | 0.5h |
| C3.02 | Create `ShiftsController.cs` | WS-C | ⬜ | B1.02 | 0.25h |
| C3.03 | Implement `POST /api/pos/terminals/{terminalId}/shifts` — open shift (EP 12) | WS-C | ⬜ | C3.02, B1.03 | 0.5h |
| C3.04 | Implement `GET /api/pos/shifts/{id}` — shift details + running totals (EP 13) | WS-C | ⬜ | C3.02 | 0.5h |
| C3.05 | Implement `PATCH /api/pos/shifts/{id}/close` — submit closing count (EP 14) | WS-C | ⬜ | C3.02, B1.10 | 0.5h |
| C3.06 | Implement `PATCH /api/pos/shifts/{id}/reconcile` — reconcile/dispute (EP 15) | WS-C | ⬜ | C3.02, B1.14 | 0.5h |
| C3.07 | Implement `GET /api/pos/stores/{storeId}/shifts` — list shifts with date range + status filter (EP 16) | WS-C | ⬜ | C3.02 | 0.5h |
| C3.08 | Implement `GET /api/pos/shifts/{id}/summary` — totals by method, order count (EP 17) | WS-C | ⬜ | C3.02 | 0.5h |
| C3.09 | Unit tests: shift endpoints with lifecycle state transitions | WS-C | ⬜ | C3.08 | 0.75h |

#### C4. Cashier Session API (§15.4 — Endpoints 18–20)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| C4.01 | Create `PinVerifyDto.cs` (request), `PinVerifyResultDto.cs` (response with user context) | WS-C | ⬜ | B3.01 | 0.25h |
| C4.02 | Create `CashierSessionDto.cs` | WS-C | ⬜ | A7.01 | 0.25h |
| C4.03 | Create `PosAuthController.cs` | WS-C | ⬜ | B3.05 | 0.25h |
| C4.04 | Implement `POST /api/pos/auth/pin` — verify PIN (EP 18, no auth required) | WS-C | ⬜ | C4.03, B3.05 | 0.5h |
| C4.05 | Create `CashierSessionsController.cs` | WS-C | ⬜ | B3.08 | 0.25h |
| C4.06 | Implement `POST /api/pos/sessions` — start/resume session (EP 19) | WS-C | ⬜ | C4.05, B3.09 | 0.5h |
| C4.07 | Implement `PATCH /api/pos/sessions/{id}/end` — end session (EP 20) | WS-C | ⬜ | C4.05, B3.13 | 0.25h |
| C4.08 | Unit tests: PIN verify, session start/switch/end | WS-C | ⬜ | C4.07 | 0.5h |

#### C5. Cash Movement API (§15.6 — Endpoints 27–28)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| C5.01 | Create `CashMovementDto.cs`, `CreateCashMovementDto.cs` | WS-C | ⬜ | A5.01 | 0.25h |
| C5.02 | Create `CashMovementsController.cs` | WS-C | ⬜ | B2.02 | 0.25h |
| C5.03 | Implement `POST /api/pos/shifts/{shiftId}/cash-movements` — record cash in/out (EP 27) | WS-C | ⬜ | C5.02, B2.03 | 0.5h |
| C5.04 | Implement `GET /api/pos/shifts/{shiftId}/cash-movements` — list movements (EP 28) | WS-C | ⬜ | C5.02 | 0.25h |
| C5.05 | Unit tests: cash movement recording and listing | WS-C | ⬜ | C5.04 | 0.25h |

---

## PHASE 3 — POS Orders, Order Lines, Payments (6 days)

### WS-A: Migrations & Entities (Dev 1 — Days 10–15)

#### A8. POS Orders Entity & Table (C5)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| A8.01 | Create `PosOrder.cs` entity class (30 columns per FSD §6.2.1) | WS-A | ⬜ | A7.07 | 1h |
| A8.02 | Create `PosOrderConfiguration.cs` — Fluent API (all FKs, indexes, check constraints) | WS-A | ⬜ | A8.01 | 1h |
| A8.03 | Add FKs: Store, Terminal, Shift, CashierSession, Customer, self-ref (original_order_id) | WS-A | ⬜ | A8.01 | 0.5h |
| A8.04 | Create `OrderType` enum: Sale=0, Return=1, Exchange=2 | WS-A | ⬜ | A8.01 | 0.1h |
| A8.05 | Create `OrderStatus` enum: Draft=0, Completed=1, Voided=2, Returned=3, PartialReturn=4 | WS-A | ⬜ | A8.01 | 0.1h |
| A8.06 | Create `PaymentStatus` enum: Unpaid=0, Paid=1, PartialPaid=2, CreditSale=3 | WS-A | ⬜ | A8.01 | 0.1h |
| A8.07 | Create unique index: `(org_id, order_number)` | WS-A | ⬜ | A8.02 | 0.1h |
| A8.08 | Create unique index: `(org_id, local_id)` — offline dedup | WS-A | ⬜ | A8.02 | 0.1h |
| A8.09 | Create indexes: store+date, shift+status, session, customer, pending sync | WS-A | ⬜ | A8.02 | 0.25h |
| A8.10 | Add CHECK constraints: status IN (0,1,2,3,4), order_type IN (0,1,2) | WS-A | ⬜ | A8.02 | 0.1h |
| A8.11 | Generate EF migration M038.8 — verify SQL matches FSD DDL | WS-A | ⬜ | A8.02 | 0.5h |
| A8.12 | Apply migration, verify | WS-A | ⬜ | A8.11 | 0.25h |

#### A9. POS Order Lines Entity & Table (C5)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| A9.01 | Create `PosOrderLine.cs` entity class (20 columns per FSD §6.2.2) | WS-A | ⬜ | A8.11 | 0.75h |
| A9.02 | Create `PosOrderLineConfiguration.cs` — Fluent API | WS-A | ⬜ | A9.01 | 0.5h |
| A9.03 | Add FKs: PosOrder, Product, TaxRate, UoM, self-ref (original_line_id) | WS-A | ⬜ | A9.01 | 0.25h |
| A9.04 | Create indexes: (order_id, line_number), (product_id, created_at) | WS-A | ⬜ | A9.02 | 0.1h |
| A9.05 | Generate EF migration M038.9 — verify SQL | WS-A | ⬜ | A9.02 | 0.5h |
| A9.06 | Apply migration, verify | WS-A | ⬜ | A9.05 | 0.25h |

#### A10. POS Payments Entity & Table (C6)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| A10.01 | Create `PosPayment.cs` entity class (14 columns per FSD §7.2.1) | WS-A | ⬜ | A8.11 | 0.75h |
| A10.02 | Create `PosPaymentConfiguration.cs` — Fluent API | WS-A | ⬜ | A10.01 | 0.5h |
| A10.03 | Create `PaymentMethod` enum: Cash=0, Card=1, MobileWallet=2, BankTransfer=3, Credit=4, Cheque=5 | WS-A | ⬜ | A10.01 | 0.25h |
| A10.04 | Create `ProviderStatus` enum (future): Pending=0, Authorized=1, Captured=2, Failed=3, Refunded=4 | WS-A | ⬜ | A10.01 | 0.1h |
| A10.05 | Add FK to `pos.PosOrders`, indexes: (order_id), (org_id, method, created_at) | WS-A | ⬜ | A10.02 | 0.25h |
| A10.06 | Create unique filtered index: `(org_id, local_id)` WHERE local_id IS NOT NULL | WS-A | ⬜ | A10.02 | 0.25h |
| A10.07 | Generate EF migration M038.10 — verify SQL | WS-A | ⬜ | A10.02 | 0.5h |
| A10.08 | Apply migration, verify | WS-A | ⬜ | A10.07 | 0.25h |

### WS-B: Backend Services (Dev 1 — Days 10–15, after entity creation)

#### B4. POS Order Service (C5)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| B4.01 | Create `IPosOrderService.cs` interface | WS-B | ⬜ | A8.11 | 0.25h |
| B4.02 | Create `PosOrderService.cs` — DI registration | WS-B | ⬜ | B4.01 | 0.25h |
| B4.03 | Implement order number generation: `POS-{store_code}-{YYYYMMDD}-{seq}` (§6.2.1) | WS-B | ⬜ | B4.02 | 0.5h |
| B4.04 | Implement atomic sequence: count today's orders per store + 1 | WS-B | ⬜ | B4.03 | 0.5h |
| B4.05 | Validate POS-ORD-01: only sellable + pos_active products | WS-B | ⬜ | B4.02 | 0.25h |
| B4.06 | Implement price/tax snapshot at order creation time (POS-ORD-02, POS-ORD-03) | WS-B | ⬜ | B4.02 | 0.5h |
| B4.07 | Implement line total calculation: `(unit_price × quantity) - discount_amount + tax_amount` | WS-B | ⬜ | B4.02 | 0.5h |
| B4.08 | Implement line-level discount (percentage or fixed) | WS-B | ⬜ | B4.07 | 0.25h |
| B4.09 | Implement order-level discount (POS-ORD-04): `grand_total = subtotal - discount_amount + tax_total` | WS-B | ⬜ | B4.08 | 0.5h |
| B4.10 | Implement `CreateOrderFromSyncAsync()` — process order from PWA sync payload | WS-B | ⬜ | B4.09 | 1h |
| B4.11 | Implement `local_id` idempotent dedup (POS-ORD-10): second submission returns existing | WS-B | ⬜ | B4.10 | 0.5h |
| B4.12 | Implement offline order number replacement: `POS-{store}-OFF-{uuid}` → canonical number | WS-B | ⬜ | B4.10 | 0.25h |
| B4.13 | Implement `CompleteOrderAsync()` — validate payment covers total (POS-ORD-05) | WS-B | ⬜ | B4.10 | 0.75h |
| B4.14 | Update shift running totals on order completion (call B1.16) | WS-B | ⬜ | B4.13, B1.16 | 0.25h |
| B4.15 | Update cashier session totals on order completion (call B3.14) | WS-B | ⬜ | B4.13, B3.14 | 0.25h |
| B4.16 | Implement walk-in default: assign Walk-In customer if none selected (POS-ORD-11) | WS-B | ⬜ | B4.10 | 0.25h |
| B4.17 | Implement `VoidOrderAsync()` — supervisor only, same shift only (POS-ORD-06, POS-ORD-07) | WS-B | ⬜ | B4.13 | 0.75h |
| B4.18 | Implement `CreateReturnAsync()` — from any past shift, max qty check (POS-ORD-08, POS-ORD-09) | WS-B | ⬜ | B4.13 | 1h |
| B4.19 | Implement stale price warning check (POS-ORD-13) — return flag if price changed since cache | WS-B | ⬜ | B4.10 | 0.25h |
| B4.20 | Unit tests: order creation, calculation, dedup, completion | WS-B | ⬜ | B4.16 | 1h |
| B4.21 | Unit tests: void (supervisor, same shift), return (any shift, max qty) | WS-B | ⬜ | B4.18 | 0.75h |

#### B5. Payment Service (C6)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| B5.01 | Create `IPosPaymentService.cs` interface | WS-B | ⬜ | A10.07 | 0.25h |
| B5.02 | Create `PosPaymentService.cs` | WS-B | ⬜ | B5.01 | 0.25h |
| B5.03 | Implement `AddPaymentAsync()` — create payment tender | WS-B | ⬜ | B5.02 | 0.5h |
| B5.04 | Implement split payment: multiple tenders per order (POS-PAY-01) | WS-B | ⬜ | B5.03 | 0.5h |
| B5.05 | Implement cash change calculation: `change_given = paid - remaining` (POS-PAY-02) | WS-B | ⬜ | B5.03 | 0.25h |
| B5.06 | Validate card/mobile/transfer cannot exceed remaining (POS-PAY-03) | WS-B | ⬜ | B5.04 | 0.25h |
| B5.07 | Implement credit payment validation: customer required, not walk-in (POS-PAY-04) | WS-B | ⬜ | B5.03 | 0.5h |
| B5.08 | Implement online credit limit check (POS-PAY-05): `balance + order ≤ limit` | WS-B | ⬜ | B5.07 | 0.5h |
| B5.09 | Implement offline credit tolerance check (POS-PAY-06): `cached + order ≤ limit × (1 + tolerance/100)` | WS-B | ⬜ | B5.08 | 0.5h |
| B5.10 | Implement credit over-limit flag on sync (POS-PAY-07): supervisor notification | WS-B | ⬜ | B5.09 | 0.25h |
| B5.11 | Validate walk-in cash-only offline (POS-PAY-08) | WS-B | ⬜ | B5.07 | 0.25h |
| B5.12 | Update order `paid_amount`, `change_amount`, `balance_due`, `payment_status` | WS-B | ⬜ | B5.04 | 0.5h |
| B5.13 | Update customer `current_balance` on credit sale completion | WS-B | ⬜ | B5.08 | 0.25h |
| B5.14 | Implement refund payment logic (POS-PAY-09): reverse journal, credit customer balance | WS-B | ⬜ | B5.13 | 0.5h |
| B5.15 | Implement `local_id` dedup for offline payment records | WS-B | ⬜ | B5.03 | 0.25h |
| B5.16 | Unit tests: split payment, cash change, credit limit (online+offline), refund | WS-B | ⬜ | B5.14 | 1h |

### WS-C: API Controllers (Dev 2 — Days 10–15, parallel)

#### C6. POS Order API (§15.5 — Endpoints 21–26)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| C6.01 | Create `PosOrderDto.cs` (response DTO with lines + payments) | WS-C | ⬜ | A8.01, A9.01, A10.01 | 0.75h |
| C6.02 | Create `SyncOrderDto.cs` (request DTO from PWA sync payload) | WS-C | ⬜ | A8.01 | 0.5h |
| C6.03 | Create `BatchSyncOrderDto.cs` (request DTO for batch sync) | WS-C | ⬜ | C6.02 | 0.25h |
| C6.04 | Create `CreateReturnDto.cs` (return request) | WS-C | ⬜ | A8.01 | 0.25h |
| C6.05 | Create `PosOrdersController.cs` with `[RequiresModule("POS")]` | WS-C | ⬜ | B4.02 | 0.25h |
| C6.06 | Implement `POST /api/pos/orders/sync` — single order sync (EP 21) | WS-C | ⬜ | C6.05, B4.10 | 0.75h |
| C6.07 | Implement `POST /api/pos/orders/sync/batch` — batch sync (EP 22) | WS-C | ⬜ | C6.06 | 0.5h |
| C6.08 | Implement `GET /api/pos/orders/{id}` — order detail with lines + payments (EP 23) | WS-C | ⬜ | C6.05 | 0.5h |
| C6.09 | Implement `GET /api/pos/orders?storeId=&date=` — list orders paginated (EP 24) | WS-C | ⬜ | C6.05 | 0.5h |
| C6.10 | Implement `PATCH /api/pos/orders/{id}/void` — void order (EP 25) | WS-C | ⬜ | C6.05, B4.17 | 0.5h |
| C6.11 | Implement `POST /api/pos/orders/{id}/return` — create return (EP 26) | WS-C | ⬜ | C6.05, B4.18 | 0.5h |
| C6.12 | Unit tests: sync (single + batch + dedup), void, return | WS-C | ⬜ | C6.11 | 1h |

---

## PHASE 4 — Product/Customer POS Extensions, Sync Engine (5 days)

> **Note:** Phase 4 can run **in parallel with Phase 5** — different teams, no direct dependencies.

### WS-A: Migrations & Entities (Dev 1 — Days 16–20)

#### A11. Product POS Settings Extension (C7)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| A11.01 | Create `ProductPosSettings.cs` entity class (12 columns per FSD §8.2.1) | WS-A | ⬜ | A1.01 | 0.5h |
| A11.02 | Create `ProductPosSettingsConfiguration.cs` — 1:1 FK to `inventory.Products` | WS-A | ⬜ | A11.01 | 0.5h |
| A11.03 | Create filtered index: `(barcode)` WHERE NOT NULL | WS-A | ⬜ | A11.02 | 0.1h |
| A11.04 | Create index: `(pos_category_id, sort_order)` | WS-A | ⬜ | A11.02 | 0.1h |
| A11.05 | Create filtered index: `(is_favorite)` WHERE is_favorite = 1 | WS-A | ⬜ | A11.02 | 0.1h |
| A11.06 | Create index: `(modified_at)` — delta sync | WS-A | ⬜ | A11.02 | 0.1h |
| A11.07 | Generate EF migration M038.11 — verify SQL | WS-A | ⬜ | A11.02 | 0.5h |
| A11.08 | Apply migration, verify | WS-A | ⬜ | A11.07 | 0.25h |

#### A12. Customer POS Settings Extension (C8)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| A12.01 | Create `CustomerPosSettings.cs` entity class (7 columns per FSD §9.2.1) | WS-A | ⬜ | A1.01 | 0.5h |
| A12.02 | Create `CustomerPosSettingsConfiguration.cs` — 1:1 FK to `customers.Customers` | WS-A | ⬜ | A12.01 | 0.5h |
| A12.03 | Create filtered index: `(allow_pos_credit)` WHERE allow_pos_credit = 1 | WS-A | ⬜ | A12.02 | 0.1h |
| A12.04 | Create index: `(modified_at)` | WS-A | ⬜ | A12.02 | 0.1h |
| A12.05 | Generate EF migration M038.12 — verify SQL | WS-A | ⬜ | A12.02 | 0.5h |
| A12.06 | Apply migration, verify | WS-A | ⬜ | A12.05 | 0.25h |

### WS-C: API Controllers (Dev 2 — Days 16–20, parallel)

#### C7. Product POS Settings API (§15.8 — Endpoints 33–35)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| C7.01 | Create `ProductPosSettingsDto.cs` | WS-C | ⬜ | A11.01 | 0.25h |
| C7.02 | Create `UpdateProductPosSettingsDto.cs` (with barcode, pos_name, sale_price, color, etc.) | WS-C | ⬜ | A11.01 | 0.25h |
| C7.03 | Create `BulkUpdateProductPosSettingsDto.cs` | WS-C | ⬜ | C7.02 | 0.25h |
| C7.04 | Implement `GET /api/products/{id}/pos-settings` (EP 33) | WS-C | ⬜ | A11.07 | 0.5h |
| C7.05 | Implement `PUT /api/products/{id}/pos-settings` (EP 34) — create-or-update (upsert) | WS-C | ⬜ | C7.04 | 0.5h |
| C7.06 | Implement `POST /api/products/pos-settings/bulk` (EP 35) — batch update | WS-C | ⬜ | C7.05 | 0.75h |
| C7.07 | Barcode validation: format check (EAN-13, UPC-A, Code 128) | WS-C | ⬜ | C7.05 | 0.25h |
| C7.08 | Unit tests: get/update/bulk POS settings | WS-C | ⬜ | C7.06 | 0.5h |

#### C8. Customer POS Settings API (§15.8 — Endpoints 36–37)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| C8.01 | Create `CustomerPosSettingsDto.cs`, `UpdateCustomerPosSettingsDto.cs` | WS-C | ⬜ | A12.01 | 0.25h |
| C8.02 | Implement `GET /api/customers/{id}/pos-settings` (EP 36) | WS-C | ⬜ | A12.05 | 0.5h |
| C8.03 | Implement `PUT /api/customers/{id}/pos-settings` (EP 37) — upsert | WS-C | ⬜ | C8.02 | 0.5h |
| C8.04 | Unit tests: get/update customer POS settings | WS-C | ⬜ | C8.03 | 0.25h |

#### C9. Sync API Extensions (§15.7 — Endpoints 29–32)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| C9.01 | Extend `GET /api/sync/catalog` — include `posSettings` object in product response (EP 29) | WS-C | ⬜ | A11.07 | 0.75h |
| C9.02 | Include `stockLevel` (warehouseId, available) for store's linked warehouse in catalog sync | WS-C | ⬜ | C9.01 | 0.5h |
| C9.03 | Extend `GET /api/sync/customers` — include `posSettings` object in customer response (EP 30) | WS-C | ⬜ | A12.05 | 0.75h |
| C9.04 | Implement `GET /api/sync/pos-config?storeId={id}` — store config, terminal config, payment methods, tax rates (EP 31) | WS-C | ⬜ | A2.11 | 0.75h |
| C9.05 | Implement `GET /api/sync/pos-users?storeId={id}` — POS users with hashed PINs for offline auth (EP 32) | WS-C | ⬜ | A6.06 | 0.75h |
| C9.06 | Ensure sync endpoints respect delta (`since` parameter) using `modified_at` columns | WS-C | ⬜ | C9.02 | 0.5h |
| C9.07 | Unit tests: catalog sync with POS settings, customer sync with POS settings | WS-C | ⬜ | C9.06 | 0.75h |
| C9.08 | Unit tests: pos-config sync, pos-users sync | WS-C | ⬜ | C9.07 | 0.5h |

---

## PHASE 5 — Inventory + Finance Integration (4 days)

> **Runs in parallel with Phase 4.** Different developer can take this.

### WS-E: Integration Services (Dev 1 or Dev 3 — Days 16–19)

#### E1. Inventory Integration — Stock Deduction (C9)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| E1.01 | Add `POS_SALE` to `StockMovementType` enum (M038.13) | WS-E | ⬜ | B4.13 | 0.25h |
| E1.02 | Add `POS_RETURN` to `StockMovementType` enum (M038.13) | WS-E | ⬜ | E1.01 | 0.1h |
| E1.03 | Add `POS_VOID` to `StockMovementType` enum | WS-E | ⬜ | E1.01 | 0.1h |
| E1.04 | Generate EF migration for StockMovementType additions | WS-E | ⬜ | E1.03 | 0.5h |
| E1.05 | Create `IPosStockService.cs` interface | WS-E | ⬜ | E1.04 | 0.25h |
| E1.06 | Create `PosStockService.cs` | WS-E | ⬜ | E1.05 | 0.25h |
| E1.07 | Implement `DeductStockAsync()` — per FSD §10.2 flow | WS-E | ⬜ | E1.06 | 1h |
| E1.08 | Create `inventory.StockMovements` record: type=POS_SALE, direction=OUT, reference_type=PosOrder | WS-E | ⬜ | E1.07 | 0.5h |
| E1.09 | Implement negative stock handling (POS-ORD-12): proceed + generate notification | WS-E | ⬜ | E1.07 | 0.5h |
| E1.10 | Implement `ReturnStockAsync()` — per FSD §10.4 flow | WS-E | ⬜ | E1.06 | 0.5h |
| E1.11 | Create StockMovement: type=POS_RETURN, direction=IN | WS-E | ⬜ | E1.10 | 0.25h |
| E1.12 | Implement void stock reversal: type=POS_VOID, direction=IN | WS-E | ⬜ | E1.06 | 0.5h |
| E1.13 | Wire `PosStockService.DeductStockAsync()` into `PosOrderService.CompleteOrderAsync()` | WS-E | ⬜ | E1.07, B4.13 | 0.25h |
| E1.14 | Wire stock return into void + return flows | WS-E | ⬜ | E1.10, E1.12 | 0.25h |
| E1.15 | Unit tests: stock deduction on sale, return on refund, void reversal, negative stock | WS-E | ⬜ | E1.14 | 1h |

#### E2. Finance Integration — Journal Entries (C10)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| E2.01 | Create `IPosJournalService.cs` interface | WS-E | ⬜ | B4.13 | 0.25h |
| E2.02 | Create `PosJournalService.cs` | WS-E | ⬜ | E2.01 | 0.25h |
| E2.03 | Create `PosAccountMapping` class for store-level GL account configuration (§11.3) | WS-E | ⬜ | E2.02 | 0.5h |
| E2.04 | Map PaymentMethod → GL account: Cash→CashAccount, Card→CardReceivable, etc. | WS-E | ⬜ | E2.03 | 0.25h |
| E2.05 | Implement cash sale journal template (§11.2): DR Cash, CR Revenue + Tax Payable | WS-E | ⬜ | E2.03 | 0.75h |
| E2.06 | Implement card sale journal: DR Card Receivable, CR Revenue + Tax Payable | WS-E | ⬜ | E2.05 | 0.25h |
| E2.07 | Implement mobile wallet sale journal: DR Mobile Receivable, CR Revenue + Tax Payable | WS-E | ⬜ | E2.05 | 0.25h |
| E2.08 | Implement credit sale journal (§11.4): DR Accounts Receivable (sub: customer), CR Revenue + Tax | WS-E | ⬜ | E2.05 | 0.5h |
| E2.09 | Implement split payment journal: multiple DR lines by payment method | WS-E | ⬜ | E2.05 | 0.5h |
| E2.10 | Implement return/refund reversal journal (§11.5): DR Revenue + Tax, CR Cash/Card | WS-E | ⬜ | E2.05 | 0.5h |
| E2.11 | Wire `PosJournalService.CreateJournalEntryAsync()` into `PosOrderService.CompleteOrderAsync()` | WS-E | ⬜ | E2.05, B4.13 | 0.25h |
| E2.12 | Wire journal reversal into void + return flows | WS-E | ⬜ | E2.10 | 0.25h |
| E2.13 | Unit tests: journal for cash/card/credit/split/refund | WS-E | ⬜ | E2.12 | 1h |

#### E3. POS Routes & Workflow Rules (Supporting Migrations)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| E3.01 | M038.18 — Add POS routes to `inventory.Routes` | WS-E | ⬜ | A1.04 | 0.5h |
| E3.02 | M038.20 — Seed POS workflow approval rules | WS-E | ⬜ | A1.04 | 0.5h |
| E3.03 | Generate and apply migrations, verify | WS-E | ⬜ | E3.02 | 0.25h |

#### E4. Hangfire Background Jobs

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| E4.01 | Create `StaleShiftAlertJob` — check shifts OPEN > auto_close_shift_hours (POS-SHF-08) | WS-E | ⬜ | B1.03 | 0.75h |
| E4.02 | Register StaleShiftAlertJob on Hangfire RecurringJob (hourly) | WS-E | ⬜ | E4.01 | 0.25h |
| E4.03 | Create `NegativeStockReportJob` — daily report for all stores with negative stock items | WS-E | ⬜ | E1.09 | 0.75h |
| E4.04 | Register NegativeStockReportJob on Hangfire RecurringJob (daily) | WS-E | ⬜ | E4.03 | 0.25h |
| E4.05 | Create `SyncRetryJob` — retry failed order sync (orders with syncStatus = error) | WS-E | ⬜ | B4.11 | 0.5h |
| E4.06 | Register SyncRetryJob on Hangfire RecurringJob (every 15 min) | WS-E | ⬜ | E4.05 | 0.25h |
| E4.07 | Unit tests: stale shift detection, negative stock report generation | WS-E | ⬜ | E4.04 | 0.5h |

---

## PHASE 6 — SignalR Hub + PWA Client (7 days)

### WS-E: SignalR Hub (Dev 1 — Days 20–21)

#### E5. SignalR PosHub (C11)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| E5.01 | Create `PosHub.cs` with `[Authorize]` and `[RequiresModule("POS")]` | WS-E | ⬜ | A1.04 | 0.5h |
| E5.02 | Implement `JoinStore(storeId)` — add connection to `store_{storeId}` group | WS-E | ⬜ | E5.01 | 0.25h |
| E5.03 | Implement `JoinTerminal(terminalId)` — add to `terminal_{terminalId}` group | WS-E | ⬜ | E5.01 | 0.25h |
| E5.04 | Implement `Heartbeat(terminalId)` — update `last_seen_at` | WS-E | ⬜ | E5.01 | 0.25h |
| E5.05 | Implement `PriceChanged` event dispatch (trigger: product price update in back-office) | WS-E | ⬜ | E5.02 | 0.5h |
| E5.06 | Implement `StockUpdated` event dispatch (trigger: stock movement in linked warehouse) | WS-E | ⬜ | E5.02 | 0.5h |
| E5.07 | Implement `ProductUpdated` event dispatch | WS-E | ⬜ | E5.02 | 0.25h |
| E5.08 | Implement `ProductDeactivated` event dispatch | WS-E | ⬜ | E5.02 | 0.25h |
| E5.09 | Implement `CustomerBalanceChanged` event dispatch | WS-E | ⬜ | E5.02 | 0.25h |
| E5.10 | Implement `ConfigChanged` event dispatch | WS-E | ⬜ | E5.02 | 0.25h |
| E5.11 | Implement `ForceSync` event dispatch (admin trigger) | WS-E | ⬜ | E5.03 | 0.25h |
| E5.12 | Implement `ShiftAlert` event dispatch | WS-E | ⬜ | E5.03 | 0.25h |
| E5.13 | Implement `ForceLogout` event dispatch | WS-E | ⬜ | E5.03 | 0.25h |
| E5.14 | Register `PosHub` in `Program.cs` — `app.MapHub<PosHub>("/hubs/pos")` | WS-E | ⬜ | E5.01 | 0.25h |
| E5.15 | Wire product/stock update event sources to PosHub broadcasts | WS-E | ⬜ | E5.06 | 0.5h |
| E5.16 | Unit tests: hub connection, group join, event broadcast | WS-E | ⬜ | E5.14 | 0.75h |

### WS-D: PWA Frontend (Dev 3 — Days 20–26)

#### D1. Project Setup & Infrastructure

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| D1.01 | Initialize React 18 + Vite project at `/pos` route | WS-D | ⬜ | — | 0.5h |
| D1.02 | Configure Vite for PWA output: `manifest.json`, icons, service worker entry | WS-D | ⬜ | D1.01 | 0.5h |
| D1.03 | Create `manifest.json` with PWA metadata (name, icons, display: standalone) | WS-D | ⬜ | D1.01 | 0.25h |
| D1.04 | Install dependencies: `dexie`, `@microsoft/signalr`, `axios`, `react-router-dom` | WS-D | ⬜ | D1.01 | 0.25h |
| D1.05 | Install barcode scanner: `html5-qrcode` or `quagga2` | WS-D | ⬜ | D1.01 | 0.25h |
| D1.06 | Configure Axios instance with base URL, auth token interceptor, retry logic | WS-D | ⬜ | D1.04 | 0.5h |
| D1.07 | Create route structure per FSD §17.1 (`/pos`, `/pos/sell`, `/pos/cart`, etc.) | WS-D | ⬜ | D1.01 | 0.5h |
| D1.08 | Create layout components: PosLayout, TopBar, BottomNav (mobile) | WS-D | ⬜ | D1.07 | 0.75h |

#### D2. Dexie.js Local Database (C12 — §13.3)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| D2.01 | Create `PosDatabase.ts` extending Dexie with schema v1 | WS-D | ⬜ | D1.04 | 0.5h |
| D2.02 | Define `LocalProduct` interface (per FSD §13.3) | WS-D | ⬜ | D2.01 | 0.25h |
| D2.03 | Define `LocalCustomer` interface | WS-D | ⬜ | D2.01 | 0.25h |
| D2.04 | Define `LocalOrder` interface with `syncStatus` enum | WS-D | ⬜ | D2.01 | 0.5h |
| D2.05 | Define `LocalPayment`, `LocalOrderLine` interfaces | WS-D | ⬜ | D2.04 | 0.25h |
| D2.06 | Configure indexes: products (barcode, categoryId, isFavorite), customers (code, phone), orders (syncStatus) | WS-D | ⬜ | D2.01 | 0.25h |
| D2.07 | Create `config` table for key-value store (lastSyncTimestamp, terminalId, etc.) | WS-D | ⬜ | D2.01 | 0.25h |
| D2.08 | Create `syncLog` table for sync history | WS-D | ⬜ | D2.01 | 0.25h |
| D2.09 | Export singleton `posDb` instance | WS-D | ⬜ | D2.01 | 0.1h |
| D2.10 | Unit tests: Dexie schema creation, CRUD operations on each table | WS-D | ⬜ | D2.09 | 0.5h |

#### D3. Sync Engine (C12 — §13.4)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| D3.01 | Create `SyncEngine.ts` class | WS-D | ⬜ | D2.09 | 0.25h |
| D3.02 | Implement `pullChanges()` — fetch catalog + customers with `since` parameter | WS-D | ⬜ | D3.01, C9.06 | 1h |
| D3.03 | Map server product response to `LocalProduct` (including posSettings + stockLevel) | WS-D | ⬜ | D3.02 | 0.5h |
| D3.04 | Map server customer response to `LocalCustomer` (including posSettings) | WS-D | ⬜ | D3.02 | 0.5h |
| D3.05 | Bulk upsert into Dexie.js via `bulkPut()` within transaction | WS-D | ⬜ | D3.03 | 0.25h |
| D3.06 | Save `lastSyncTimestamp` to config table | WS-D | ⬜ | D3.05 | 0.1h |
| D3.07 | Implement `pushChanges()` — upload pending orders (`syncStatus = pending`) | WS-D | ⬜ | D3.01, C6.06 | 1h |
| D3.08 | Set `syncStatus = syncing` before API call, `synced` on success, `error` on failure | WS-D | ⬜ | D3.07 | 0.25h |
| D3.09 | Update `serverId` and `orderNumber` from server response | WS-D | ⬜ | D3.08 | 0.25h |
| D3.10 | Implement `fullSync()` — clear local tables, pull everything, then push | WS-D | ⬜ | D3.02 | 0.5h |
| D3.11 | Implement auto-sync: pullChanges every 5 minutes when online (POS-SYN-01) | WS-D | ⬜ | D3.02 | 0.25h |
| D3.12 | Implement immediate push on order completion (POS-SYN-03) | WS-D | ⬜ | D3.07 | 0.25h |
| D3.13 | Implement batch push on reconnect (POS-SYN-04): chronological order | WS-D | ⬜ | D3.07 | 0.5h |
| D3.14 | Unit tests: pull, push, full sync, dedup, batch | WS-D | ⬜ | D3.13 | 0.75h |

#### D4. Service Worker (C12 — §13.5)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| D4.01 | Create Service Worker entry with Workbox | WS-D | ⬜ | D1.02 | 0.5h |
| D4.02 | Configure precache: index.html, main.js, main.css, manifest.json, icons, fonts | WS-D | ⬜ | D4.01 | 0.5h |
| D4.03 | Configure runtime strategy: `/api/sync/*` → Network-first | WS-D | ⬜ | D4.01 | 0.25h |
| D4.04 | Configure runtime strategy: `/api/pos/orders/*` → Network-first, queue on fail | WS-D | ⬜ | D4.01 | 0.25h |
| D4.05 | Configure runtime strategy: `/api/pos/shifts/*` → Network-first, fallback cached | WS-D | ⬜ | D4.01 | 0.25h |
| D4.06 | Configure runtime strategy: `/api/auth/pos-pin` → Network-first, fallback cached PIN hashes | WS-D | ⬜ | D4.01 | 0.25h |
| D4.07 | Register service worker in app entry point | WS-D | ⬜ | D4.06 | 0.25h |
| D4.08 | Test: install PWA, verify shell cached, verify offline shell load | WS-D | ⬜ | D4.07 | 0.5h |

#### D5. SignalR Client

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| D5.01 | Create `PosHubConnection.ts` — SignalR client setup with auto-reconnect | WS-D | ⬜ | D1.04, E5.14 | 0.5h |
| D5.02 | Implement `JoinStore()` and `JoinTerminal()` on connect | WS-D | ⬜ | D5.01 | 0.25h |
| D5.03 | Implement heartbeat interval (60s) → `Heartbeat(terminalId)` | WS-D | ⬜ | D5.01 | 0.25h |
| D5.04 | Handle `PriceChanged` event: update local product price in Dexie | WS-D | ⬜ | D5.01, D2.09 | 0.25h |
| D5.05 | Handle `StockUpdated` event: update local stock level | WS-D | ⬜ | D5.04 | 0.25h |
| D5.06 | Handle `ProductUpdated` / `ProductDeactivated` events | WS-D | ⬜ | D5.04 | 0.25h |
| D5.07 | Handle `CustomerBalanceChanged` event | WS-D | ⬜ | D5.04 | 0.25h |
| D5.08 | Handle `ConfigChanged` event | WS-D | ⬜ | D5.04 | 0.25h |
| D5.09 | Handle `ForceSync` event → trigger `fullSync()` | WS-D | ⬜ | D5.01, D3.10 | 0.25h |
| D5.10 | Handle `ShiftAlert` → show notification toast | WS-D | ⬜ | D5.01 | 0.25h |
| D5.11 | Handle `ForceLogout` → end session, navigate to login | WS-D | ⬜ | D5.01 | 0.25h |
| D5.12 | Implement offline detection: on disconnect → `isOnline = false`, show "Offline" badge | WS-D | ⬜ | D5.01 | 0.5h |
| D5.13 | Implement reconnect: `isOnline = true`, trigger delta sync, clear badge, rejoin groups | WS-D | ⬜ | D5.12, D3.02 | 0.5h |

#### D6. State Management

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| D6.01 | Create `AuthContext` — store terminal info, cashier user, permissions | WS-D | ⬜ | D1.01 | 0.5h |
| D6.02 | Create `CartContext` with `useReducer` — add/remove/update line, apply discount | WS-D | ⬜ | D1.01 | 0.75h |
| D6.03 | Cart reducer actions: ADD_ITEM, REMOVE_ITEM, UPDATE_QTY, APPLY_LINE_DISCOUNT, APPLY_ORDER_DISCOUNT, CLEAR_CART | WS-D | ⬜ | D6.02 | 0.5h |
| D6.04 | Create `ShiftContext` — current shift state, running totals | WS-D | ⬜ | D1.01 | 0.5h |
| D6.05 | Create `SyncContext` — online/offline status, sync progress, last sync time | WS-D | ⬜ | D1.01, D5.12 | 0.5h |
| D6.06 | Create `ConfigContext` — store config, payment methods, tax rates from sync | WS-D | ⬜ | D1.01 | 0.25h |

#### D7. UI — PIN Entry & Terminal Selection

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| D7.01 | Create `TerminalSelectorScreen` — list terminals for current store | WS-D | ⬜ | D6.01 | 0.5h |
| D7.02 | Auto-identify terminal via stored device UUID (`crypto.randomUUID()`) | WS-D | ⬜ | D7.01 | 0.25h |
| D7.03 | Create `PinEntryScreen` — numeric keypad per FSD §5.2 wireframe | WS-D | ⬜ | D6.01 | 0.75h |
| D7.04 | PIN input: 4–6 masked dots, numeric buttons 0–9, backspace | WS-D | ⬜ | D7.03 | 0.5h |
| D7.05 | Online PIN verification via `POST /api/pos/auth/pin` | WS-D | ⬜ | D7.04, C4.04 | 0.5h |
| D7.06 | Offline PIN verification: bcrypt compare against cached hash (POS-CSH-08) | WS-D | ⬜ | D7.05 | 0.5h |
| D7.07 | Failed PIN counter: 5 failures → lock screen for 5 min (POS-CSH-07) | WS-D | ⬜ | D7.06 | 0.25h |
| D7.08 | Display cashier name and role after successful PIN | WS-D | ⬜ | D7.05 | 0.25h |
| D7.09 | "Lock / Switch" button on main screen → return to PIN entry | WS-D | ⬜ | D7.03 | 0.25h |

#### D8. UI — Main Selling Screen (§17.2)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| D8.01 | Create `TopBar` component: connectivity, store, terminal, shift, cashier, lock button | WS-D | ⬜ | D6.01, D6.04, D6.05 | 0.75h |
| D8.02 | Create `ProductGrid` component: responsive CSS grid (auto-fill, min 110px) | WS-D | ⬜ | D2.09 | 0.75h |
| D8.03 | Create `ProductTile` component: image/color, name, price, stock badge | WS-D | ⬜ | D8.02 | 0.5h |
| D8.04 | Product tile tap → add 1 unit to cart | WS-D | ⬜ | D8.03, D6.02 | 0.25h |
| D8.05 | Product tile: show `⚠` icon if stock ≤ 0 (POS-ORD-12) | WS-D | ⬜ | D8.03 | 0.25h |
| D8.06 | Product tile: `is_weighable` → open quantity input modal instead of +1 | WS-D | ⬜ | D8.04 | 0.5h |
| D8.07 | Create `CategoryTabs` component: favorites + category tabs from Dexie | WS-D | ⬜ | D2.09 | 0.5h |
| D8.08 | Create `SearchBar` component: filter products by name, SKU, barcode | WS-D | ⬜ | D2.09 | 0.5h |
| D8.09 | Barcode scanner integration: detect rapid input + Enter → add to cart | WS-D | ⬜ | D8.08 | 0.75h |
| D8.10 | Camera barcode scan button using html5-qrcode | WS-D | ⬜ | D8.09 | 0.5h |
| D8.11 | Create `CartPanel` component: list cart lines with qty +/−, line total | WS-D | ⬜ | D6.02 | 0.75h |
| D8.12 | Cart line: inline quantity edit, remove button, discount indicator | WS-D | ⬜ | D8.11 | 0.5h |
| D8.13 | Create line-level discount modal: percentage or fixed amount | WS-D | ⬜ | D8.12, D6.03 | 0.5h |
| D8.14 | Create order-level discount modal | WS-D | ⬜ | D8.13 | 0.25h |
| D8.15 | Create `CartSummary` component: subtotal, discount, tax, grand total | WS-D | ⬜ | D8.11 | 0.5h |
| D8.16 | "Add Customer" button → customer search modal (Dexie search) | WS-D | ⬜ | D2.09, D8.15 | 0.5h |
| D8.17 | Order note input | WS-D | ⬜ | D8.15 | 0.25h |
| D8.18 | "PAY" button → navigate to payment screen (only if cart has items) | WS-D | ⬜ | D8.15 | 0.25h |
| D8.19 | Clear cart button with confirmation | WS-D | ⬜ | D8.11 | 0.25h |
| D8.20 | Stale price warning: if online and product price changed (POS-ORD-13) | WS-D | ⬜ | D5.04 | 0.5h |
| D8.21 | Desktop/tablet layout: product grid 60%, cart panel 40% | WS-D | ⬜ | D8.02, D8.11 | 0.5h |

#### D9. UI — Payment Screen (§17.3)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| D9.01 | Create `PaymentScreen` component per FSD §17.3 wireframe | WS-D | ⬜ | D8.18 | 0.5h |
| D9.02 | Payment method selector: Cash, Card, Mobile, Transfer, On-Account buttons | WS-D | ⬜ | D9.01 | 0.5h |
| D9.03 | Amount input: pre-filled with remaining balance | WS-D | ⬜ | D9.02 | 0.5h |
| D9.04 | Quick amount buttons: $20, $50, $100, Exact | WS-D | ⬜ | D9.03 | 0.25h |
| D9.05 | Cash change calculation display: `tendered - remaining` | WS-D | ⬜ | D9.03 | 0.25h |
| D9.06 | Split payment state: show "Payments Tendered" list with running remaining | WS-D | ⬜ | D9.03 | 0.5h |
| D9.07 | "Add another payment method" button when remaining > 0 after first tender | WS-D | ⬜ | D9.06 | 0.25h |
| D9.08 | Card reference input field (last 4 digits, transaction ref) | WS-D | ⬜ | D9.02 | 0.25h |
| D9.09 | Validate card/mobile/transfer cannot exceed remaining (POS-PAY-03) | WS-D | ⬜ | D9.06 | 0.25h |
| D9.10 | On-Account validation: customer required + not walk-in (POS-PAY-04) | WS-D | ⬜ | D9.02 | 0.5h |
| D9.11 | Online credit limit check with error message (POS-PAY-05) | WS-D | ⬜ | D9.10 | 0.5h |
| D9.12 | Offline credit tolerance check (POS-PAY-06) | WS-D | ⬜ | D9.10 | 0.5h |
| D9.13 | Walk-in cash-only validation when offline (POS-PAY-08) | WS-D | ⬜ | D9.02 | 0.25h |
| D9.14 | "Confirm Payment" button: create LocalOrder + LocalPayment in Dexie, trigger sync if online | WS-D | ⬜ | D9.06, D2.04 | 0.75h |
| D9.15 | Update shift running totals locally | WS-D | ⬜ | D9.14, D6.04 | 0.25h |
| D9.16 | Navigate to receipt screen on completion | WS-D | ⬜ | D9.14 | 0.25h |
| D9.17 | "Back to Cart" button | WS-D | ⬜ | D9.01 | 0.1h |

#### D10. UI — Receipt Screen (§17.4)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| D10.01 | Create `ReceiptScreen` component per FSD §17.4 wireframe | WS-D | ⬜ | D9.16 | 0.5h |
| D10.02 | Render receipt header from store config (name, address, phone, tax ID) | WS-D | ⬜ | D10.01 | 0.25h |
| D10.03 | Render order details: order number, date, cashier, terminal | WS-D | ⬜ | D10.01 | 0.25h |
| D10.04 | Render line items with quantity, price, discounts, line totals | WS-D | ⬜ | D10.01 | 0.25h |
| D10.05 | Render totals: subtotal, tax, grand total, payments, change | WS-D | ⬜ | D10.01 | 0.25h |
| D10.06 | Render receipt footer from store config (return policy, thank you) | WS-D | ⬜ | D10.01 | 0.1h |
| D10.07 | "Email Receipt" button (queue if offline) | WS-D | ⬜ | D10.01 | 0.5h |
| D10.08 | "New Sale" button → clear cart, return to selling screen | WS-D | ⬜ | D10.01 | 0.25h |

#### D11. UI — Shift Management Screen (§17.5)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| D11.01 | Create `ShiftManagementScreen` per FSD §17.5 wireframe (supervisor only) | WS-D | ⬜ | D6.04 | 0.5h |
| D11.02 | Display shift summary tiles: opening float, cash/card/mobile sales, cash in/out, expected cash | WS-D | ⬜ | D11.01 | 0.5h |
| D11.03 | Display cashier sessions table: cashier, started, orders, cash, card, status | WS-D | ⬜ | D11.01 | 0.5h |
| D11.04 | Display cash movements table: time, type, amount, by, reason | WS-D | ⬜ | D11.01 | 0.5h |
| D11.05 | "Cash In" button → cash movement modal (amount, reason, note) | WS-D | ⬜ | D11.01 | 0.5h |
| D11.06 | "Cash Out" button → cash movement modal | WS-D | ⬜ | D11.05 | 0.25h |
| D11.07 | "Close Shift" button → navigate to shift closing screen | WS-D | ⬜ | D11.01 | 0.25h |
| D11.08 | Wire cash movement creation to API + local state update | WS-D | ⬜ | D11.06, C5.03 | 0.5h |

#### D12. UI — Shift Closing Screen (§17.6)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| D12.01 | Create `ShiftCloseScreen` per FSD §17.6 wireframe | WS-D | ⬜ | D11.07 | 0.5h |
| D12.02 | Display expected cash in drawer | WS-D | ⬜ | D12.01 | 0.25h |
| D12.03 | Actual cash input field | WS-D | ⬜ | D12.01 | 0.25h |
| D12.04 | Live variance calculation as user types | WS-D | ⬜ | D12.03 | 0.25h |
| D12.05 | Display variance status: ✅ within tolerance or ⚠️ exceeds threshold | WS-D | ⬜ | D12.04 | 0.25h |
| D12.06 | Variance note text area (optional) | WS-D | ⬜ | D12.01 | 0.1h |
| D12.07 | Shift summary section: total orders, refunds, gross/net sales, breakdown by method | WS-D | ⬜ | D12.01 | 0.5h |
| D12.08 | "Submit & Close Shift" button → API call | WS-D | ⬜ | D12.07, C3.05 | 0.5h |
| D12.09 | "Cancel" button → return to shift management | WS-D | ⬜ | D12.01 | 0.1h |

#### D13. UI — Mobile Layout (§17.8)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| D13.01 | Responsive breakpoint: < 768px switches to single-column layout | WS-D | ⬜ | D8.21 | 0.5h |
| D13.02 | Mobile top bar: compressed (store name, cashier, lock) | WS-D | ⬜ | D8.01 | 0.25h |
| D13.03 | Mobile bottom navigation: Products, Cart (with count badge), Pay, More | WS-D | ⬜ | D1.08 | 0.5h |
| D13.04 | Mobile product grid: 2-column tiles | WS-D | ⬜ | D8.02 | 0.25h |
| D13.05 | Mobile cart view: full-screen cart list with summary | WS-D | ⬜ | D8.11 | 0.5h |
| D13.06 | Mobile payment screen: stacked layout | WS-D | ⬜ | D9.01 | 0.25h |
| D13.07 | Test: PWA on tablet and phone viewport sizes | WS-D | ⬜ | D13.06 | 0.5h |

#### D14. UI — Order History (Supervisor)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| D14.01 | Create `OrderHistoryScreen` — search/list past orders | WS-D | ⬜ | D6.01 | 0.5h |
| D14.02 | Filter by date range, customer, order number | WS-D | ⬜ | D14.01 | 0.25h |
| D14.03 | Order detail modal: lines, payments, status | WS-D | ⬜ | D14.01 | 0.5h |
| D14.04 | "Void" button for orders in current shift (supervisor) | WS-D | ⬜ | D14.03, C6.10 | 0.25h |
| D14.05 | "Return" button → return flow for any order | WS-D | ⬜ | D14.03, C6.11 | 0.5h |

---

## PHASE 7 — Permissions, Back-Office UI, Testing (5 days)

### WS-A: Seed Migrations (Dev 1 — Day 31)

#### A13. Permission & Feature Seeds

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| A13.01 | M038.15 — Seed POS module features in `tenant.ModuleFeatures` (12 features per FSD §14) | WS-A | ⬜ | A1.04 | 0.5h |
| A13.02 | M038.17 — Seed 17 POS permissions in `auth.Permissions` (per FSD §16.1) | WS-A | ⬜ | A1.04 | 0.5h |
| A13.03 | Seed default role mappings per FSD §16.2 (Cashier, Supervisor, Store Manager, Admin) | WS-A | ⬜ | A13.02 | 0.5h |
| A13.04 | M038.19 — Create POS-specific performance indexes (M038.19) | WS-A | ⬜ | A8.11, A9.05 | 0.5h |
| A13.05 | Generate and apply seed migrations, verify all seeds in DB | WS-A | ⬜ | A13.04 | 0.5h |

#### A14. Modified_at Triggers (M038.16)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| A14.01 | Create `modified_at` trigger on `pos.Stores` | WS-A | ⬜ | A2.11 | 0.25h |
| A14.02 | Create `modified_at` trigger on `pos.Terminals` | WS-A | ⬜ | A3.07 | 0.25h |
| A14.03 | Create `modified_at` trigger on `pos.Shifts` | WS-A | ⬜ | A4.10 | 0.25h |
| A14.04 | Create `modified_at` trigger on `pos.CashMovements` | WS-A | ⬜ | A5.07 | 0.25h |
| A14.05 | Create `modified_at` trigger on `pos.CashierSessions` — use created_at for immutable records | WS-A | ⬜ | A7.07 | 0.25h |
| A14.06 | Create `modified_at` trigger on `pos.PosOrders` | WS-A | ⬜ | A8.11 | 0.25h |
| A14.07 | Create `modified_at` trigger on `pos.ProductPosSettings` | WS-A | ⬜ | A11.07 | 0.25h |
| A14.08 | Create `modified_at` trigger on `pos.CustomerPosSettings` | WS-A | ⬜ | A12.05 | 0.25h |
| A14.09 | Generate migration M038.16, verify all triggers fire correctly | WS-A | ⬜ | A14.08 | 0.5h |

### WS-F: Back-Office Admin UI (Dev 2 — Days 31–34)

#### F1. Store Management Admin (§17.7.1 – §17.7.2)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| F1.01 | Create `StoreListPage` — datagrid with columns per FSD §17.7.1 | WS-F | ⬜ | C1.05 | 0.75h |
| F1.02 | Sortable columns: Code, Name, Warehouse, Terminals, City, Status | WS-F | ⬜ | F1.01 | 0.25h |
| F1.03 | Filterable: Code search, Name search, Warehouse dropdown, City, Status toggle | WS-F | ⬜ | F1.01 | 0.5h |
| F1.04 | "Add Store" button → store creation form | WS-F | ⬜ | F1.01 | 0.25h |
| F1.05 | Create `StoreDetailPage` with tabs per FSD §17.7.2 | WS-F | ⬜ | C1.06 | 0.5h |
| F1.06 | General tab: name, code, address, phone, email, timezone, currency | WS-F | ⬜ | F1.05 | 0.5h |
| F1.07 | Configuration tab: tax settings, receipt header/footer, offline credit tolerance, auto-close hours | WS-F | ⬜ | F1.05 | 0.5h |
| F1.08 | Terminals tab: terminal list with status, device binding, last seen | WS-F | ⬜ | F1.05, C2.03 | 0.5h |
| F1.09 | Staff tab: POS users (cashiers/supervisors), PIN management (set/reset) | WS-F | ⬜ | F1.05, B3.02 | 0.75h |
| F1.10 | Shifts tab: shift history with date filter, status filter, variance highlight | WS-F | ⬜ | F1.05, C3.07 | 0.5h |
| F1.11 | Reports tab: store-level sales summary by day/week/month (placeholder for future analytics) | WS-F | ⬜ | F1.05 | 0.25h |
| F1.12 | Warehouse selector with 1:1 validation warning | WS-F | ⬜ | F1.06, C1.08 | 0.25h |
| F1.13 | Store activate/deactivate toggle with shift validation | WS-F | ⬜ | F1.05, C1.10 | 0.25h |

#### F2. Terminal Management Admin

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| F2.01 | Terminal create/edit form within store detail | WS-F | ⬜ | F1.08, C2.04 | 0.5h |
| F2.02 | Device binding status: show UUID, last_seen, "Unbind" button | WS-F | ⬜ | F2.01, C2.06 | 0.25h |
| F2.03 | Terminal activate/deactivate toggle | WS-F | ⬜ | F2.01, C2.05 | 0.25h |
| F2.04 | Terminal connectivity indicator: Online (< 15 min), Offline (> 15 min) | WS-F | ⬜ | F2.01, C2.08 | 0.25h |

#### F3. Product POS Tab (§17.7.3)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| F3.01 | Add "POS Settings" tab to Product form when POS enabled + is_sellable | WS-F | ⬜ | C7.04 | 0.5h |
| F3.02 | Barcode field with scan button per FSD §17.7.3 wireframe | WS-F | ⬜ | F3.01 | 0.25h |
| F3.03 | POS display name, sale price, POS category dropdown | WS-F | ⬜ | F3.01 | 0.5h |
| F3.04 | Tile color picker (hex color) | WS-F | ⬜ | F3.01 | 0.25h |
| F3.05 | Image URL input | WS-F | ⬜ | F3.01 | 0.1h |
| F3.06 | Checkboxes: Available on POS, Favorite, Weighable | WS-F | ⬜ | F3.01 | 0.25h |
| F3.07 | Sort order input | WS-F | ⬜ | F3.01 | 0.1h |
| F3.08 | "Save POS Settings" → `PUT /api/products/{id}/pos-settings` | WS-F | ⬜ | F3.01, C7.05 | 0.25h |
| F3.09 | Hide POS tab when POS module disabled | WS-F | ⬜ | F3.01 | 0.25h |

#### F4. Customer POS Settings (inline in Customer form)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| F4.01 | Add POS section to Customer detail page when POS enabled | WS-F | ⬜ | C8.02 | 0.5h |
| F4.02 | Fields: allow_pos_credit, offline_credit_limit_override, max_single_credit_order, requires_id, notes | WS-F | ⬜ | F4.01 | 0.5h |
| F4.03 | Save → `PUT /api/customers/{id}/pos-settings` | WS-F | ⬜ | F4.02, C8.03 | 0.25h |

#### F5. Shift History & Order History (Admin Views)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| F5.01 | Create shift history list page with filters: store, date range, status | WS-F | ⬜ | C3.07 | 0.5h |
| F5.02 | Shift detail view: full summary, cashier sessions, cash movements | WS-F | ⬜ | C3.08 | 0.5h |
| F5.03 | Variance highlighting: green ≤ threshold, red > threshold | WS-F | ⬜ | F5.02 | 0.25h |
| F5.04 | Reconcile/dispute action buttons (supervisor) | WS-F | ⬜ | F5.02, C3.06 | 0.25h |
| F5.05 | Create POS order history page with filters: store, date, customer, status | WS-F | ⬜ | C6.09 | 0.5h |
| F5.06 | Order detail modal with lines, payments, sync status | WS-F | ⬜ | C6.08 | 0.5h |
| F5.07 | Void/return action buttons from admin view | WS-F | ⬜ | F5.06 | 0.25h |

#### F6. POS Navigation Integration

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| F6.01 | Add POS section to sidebar navigation (conditionally when POS enabled) | WS-F | ⬜ | A1.04 | 0.25h |
| F6.02 | Sidebar items: Stores, Terminals, Shifts, POS Orders, Reports | WS-F | ⬜ | F6.01 | 0.25h |
| F6.03 | "Open POS" button → open PWA in new tab/window at `/pos` | WS-F | ⬜ | F6.01 | 0.25h |
| F6.04 | Permission-based visibility: admin sees all, supervisor sees shift/order, cashier sees nothing | WS-F | ⬜ | F6.02, A13.03 | 0.25h |

### WS-G: Testing & QA (Days 33–36)

#### G1. Store & Terminal Tests (TS-038-01 to TS-038-05)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| G1.01 | TS-038-01: Create store linked to warehouse WH-001 → success | WS-G | ⬜ | C1.07 | 0.25h |
| G1.02 | TS-038-02: Create second store linked to WH-001 → fail conflict | WS-G | ⬜ | C1.08 | 0.25h |
| G1.03 | TS-038-03: Create terminal T-01, device_uuid = NULL | WS-G | ⬜ | C2.04 | 0.25h |
| G1.04 | TS-038-04: Bind device UUID to terminal | WS-G | ⬜ | C2.06 | 0.25h |
| G1.05 | TS-038-05: Deactivate store with open shift → fail | WS-G | ⬜ | C1.10 | 0.25h |

#### G2. Shift Lifecycle Tests (TS-038-06 to TS-038-14)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| G2.01 | TS-038-06: Open shift with $200 float (supervisor) | WS-G | ⬜ | C3.03 | 0.25h |
| G2.02 | TS-038-07: Open shift (cashier without permission) → 403 | WS-G | ⬜ | C3.03, A13.03 | 0.25h |
| G2.03 | TS-038-08: Open second shift on same terminal → fail | WS-G | ⬜ | C3.03 | 0.25h |
| G2.04 | TS-038-09: 3 cash sales → total_cash_sales = $100, expected_cash = $300 | WS-G | ⬜ | C6.06, B1.16 | 0.5h |
| G2.05 | TS-038-10: Cash-out $100 (safe drop) → expected_cash = $200 | WS-G | ⬜ | C5.03, B2.04 | 0.25h |
| G2.06 | TS-038-11: Close shift, actual $195, variance −$5, threshold $5 → RECONCILED | WS-G | ⬜ | C3.05 | 0.25h |
| G2.07 | TS-038-12: Close shift, actual $180, variance −$20 → DISPUTED | WS-G | ⬜ | C3.05 | 0.25h |
| G2.08 | TS-038-13: Supervisor resolves disputed shift → RECONCILED + note | WS-G | ⬜ | C3.06 | 0.25h |
| G2.09 | TS-038-14: Shift open > 24 hours → Hangfire stale notification | WS-G | ⬜ | E4.02 | 0.25h |

#### G3. Cashier PIN Tests (TS-038-15 to TS-038-20)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| G3.01 | TS-038-15: Set PIN 1234 for Ahmed in STR-001 → success | WS-G | ⬜ | B3.02 | 0.25h |
| G3.02 | TS-038-16: Set PIN 1234 for Fatima in same store → fail duplicate | WS-G | ⬜ | B3.04 | 0.25h |
| G3.03 | TS-038-17: Enter correct PIN → session created | WS-G | ⬜ | C4.04, D7.05 | 0.25h |
| G3.04 | TS-038-18: Enter wrong PIN 5 times → locked 5 min | WS-G | ⬜ | B3.06, D7.07 | 0.25h |
| G3.05 | TS-038-19: Switch cashier → previous session ended, cart cleared | WS-G | ⬜ | B3.10, D7.09 | 0.5h |
| G3.06 | TS-038-20: Enter PIN offline → cached hash verification | WS-G | ⬜ | D7.06 | 0.5h |

#### G4. Order Processing Tests (TS-038-21 to TS-038-30)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| G4.01 | TS-038-21: Add 2× Coffee $14.99 → cart shows $29.98 | WS-G | ⬜ | D8.04 | 0.25h |
| G4.02 | TS-038-22: Apply 10% line discount | WS-G | ⬜ | D8.13 | 0.25h |
| G4.03 | TS-038-23: Apply 5% order discount | WS-G | ⬜ | D8.14 | 0.25h |
| G4.04 | TS-038-24: Pay $50 cash for $42.22 → change $7.78, completed | WS-G | ⬜ | D9.14 | 0.25h |
| G4.05 | TS-038-25: Split $30 cash + $12.22 card → two payments, completed | WS-G | ⬜ | D9.06 | 0.5h |
| G4.06 | TS-038-26: Scan barcode → product found + added to cart | WS-G | ⬜ | D8.09 | 0.25h |
| G4.07 | TS-038-27: Scan unknown barcode → "not found" | WS-G | ⬜ | D8.09 | 0.25h |
| G4.08 | TS-038-28: Sell product with stock = 0 → warning, sale proceeds | WS-G | ⬜ | D8.05 | 0.25h |
| G4.09 | TS-038-29: Void order within same shift (supervisor) → voided | WS-G | ⬜ | D14.04 | 0.5h |
| G4.10 | TS-038-30: Void order from different shift → fail | WS-G | ⬜ | C6.10 | 0.25h |

#### G5. Credit Sale Tests (TS-038-31 to TS-038-36)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| G5.01 | TS-038-31: Credit sale within limit → success, balance updated | WS-G | ⬜ | D9.11, B5.08 | 0.5h |
| G5.02 | TS-038-32: Credit sale exceeding limit (online) → blocked | WS-G | ⬜ | D9.11 | 0.25h |
| G5.03 | TS-038-33: Credit sale to Walk-In → blocked | WS-G | ⬜ | D9.10 | 0.25h |
| G5.04 | TS-038-34: Credit sale offline within tolerance → allowed | WS-G | ⬜ | D9.12 | 0.5h |
| G5.05 | TS-038-35: Credit sale offline exceeds tolerance → blocked | WS-G | ⬜ | D9.12 | 0.25h |
| G5.06 | TS-038-36: Sync reveals over-limit → CREDIT_OVER_LIMIT flag | WS-G | ⬜ | B5.10, D3.07 | 0.5h |

#### G6. Return Tests (TS-038-37 to TS-038-40)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| G6.01 | TS-038-37: Return 1 unit from 3-line order → stock increased, journal reversed | WS-G | ⬜ | D14.05, E1.10 | 0.5h |
| G6.02 | TS-038-38: Return quantity > original → blocked | WS-G | ⬜ | C6.11 | 0.25h |
| G6.03 | TS-038-39: Cash refund → shift cash_refunds updated | WS-G | ⬜ | D14.05 | 0.25h |
| G6.04 | TS-038-40: Return of credit sale → customer balance reduced | WS-G | ⬜ | B5.14 | 0.25h |

#### G7. Inventory Integration Tests (TS-038-41 to TS-038-43)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| G7.01 | TS-038-41: Complete sale → StockMovement POS_SALE OUT created | WS-G | ⬜ | E1.13 | 0.5h |
| G7.02 | TS-038-42: Complete return → StockMovement POS_RETURN IN created | WS-G | ⬜ | E1.14 | 0.25h |
| G7.03 | TS-038-43: Void order → StockMovement POS_VOID IN reversal | WS-G | ⬜ | E1.14 | 0.25h |

#### G8. Finance Integration Tests (TS-038-44 to TS-038-47)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| G8.01 | TS-038-44: Cash sale → DR Cash, CR Revenue + Tax | WS-G | ⬜ | E2.11 | 0.5h |
| G8.02 | TS-038-45: Card sale → DR Card Receivable, CR Revenue + Tax | WS-G | ⬜ | E2.11 | 0.25h |
| G8.03 | TS-038-46: Credit sale → DR AR (sub: customer), CR Revenue + Tax | WS-G | ⬜ | E2.11 | 0.25h |
| G8.04 | TS-038-47: Cash refund → DR Revenue + Tax, CR Cash | WS-G | ⬜ | E2.12 | 0.25h |

#### G9. Offline & Sync Tests (TS-038-48 to TS-038-53)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| G9.01 | TS-038-48: Create order offline → saved in IndexedDB, syncStatus=pending | WS-G | ⬜ | D9.14 | 0.5h |
| G9.02 | TS-038-49: Reconnect with 3 offline orders → all synced, numbers assigned | WS-G | ⬜ | D3.13 | 0.75h |
| G9.03 | TS-038-50: Duplicate sync (same local_id) → returns existing, no duplicate | WS-G | ⬜ | B4.11 | 0.25h |
| G9.04 | TS-038-51: Price changed server-side → SignalR push updates tile | WS-G | ⬜ | D5.04 | 0.5h |
| G9.05 | TS-038-52: First terminal activation → full sync downloads everything | WS-G | ⬜ | D3.10 | 0.5h |
| G9.06 | TS-038-53: Delta sync with `since` → only modified records returned | WS-G | ⬜ | C9.06, D3.02 | 0.25h |

#### G10. Module Integration Tests (TS-038-54 to TS-038-58)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| G10.01 | TS-038-54: Enable POS module → sidebar appears, schema accessible | WS-G | ⬜ | F6.01, A1.04 | 0.25h |
| G10.02 | TS-038-55: Disable POS with open shifts → blocked | WS-G | ⬜ | C1.10 | 0.25h |
| G10.03 | TS-038-56: Product form POS tab visible when POS enabled + is_sellable | WS-G | ⬜ | F3.01 | 0.25h |
| G10.04 | TS-038-57: Product form POS tab hidden when POS disabled | WS-G | ⬜ | F3.09 | 0.25h |
| G10.05 | TS-038-58: POS API call when module disabled → 403 MODULE_NOT_LICENSED | WS-G | ⬜ | C1.04 | 0.25h |

#### G11. Edge Case Tests (TS-038-59 to TS-038-60)

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| G11.01 | TS-038-59: Offline 48 hours, 200 orders queued → all sync on reconnect | WS-G | ⬜ | D3.13 | 1h |
| G11.02 | TS-038-60: Two terminals sell last unit simultaneously → both succeed, stock −1 | WS-G | ⬜ | E1.09 | 0.5h |

#### G12. E2E Full Flow Tests

| # | Task | Stream | Status | Depends | Est |
|---|---|---|---|---|---|
| G12.01 | E2E: Admin creates store → terminal → supervisor opens shift → cashier sells → close shift → reconcile | WS-G | ⬜ | All | 1.5h |
| G12.02 | E2E: Offline sale flow — disconnect → sell → reconnect → sync → verify stock + journal | WS-G | ⬜ | All | 1h |
| G12.03 | E2E: Credit sale flow — assign customer → credit sale → balance update → sync verify | WS-G | ⬜ | All | 0.75h |
| G12.04 | E2E: Return flow — find original order → select lines → process return → stock in + journal reverse | WS-G | ⬜ | All | 0.75h |
| G12.05 | E2E: Split payment flow — part cash + part card → correct journal entries | WS-G | ⬜ | All | 0.5h |
| G12.06 | E2E: Multi-cashier shift — PIN switch → session tracking → per-cashier totals | WS-G | ⬜ | All | 0.5h |

---

## Summary — Parallel Execution Timeline

```
                    Day 1-4          Day 5-9          Day 10-15        Day 16-20        Day 21-26        Day 27-30        Day 31-36
                    ┌────────┐       ┌────────┐       ┌────────┐       ┌────────────────────────┐       ┌────────────────────────┐
DEV 1 (Backend)     │ WS-A   │       │ WS-A   │       │ WS-A   │       │ WS-A (ext)  │ WS-E     │       │ WS-A (seeds)│ Support  │
                    │ Schema │       │ Shifts │       │ Orders │       │ Prod/Cust   │ Inventory│       │ Triggers    │          │
                    │ Store  │       │ Cash   │       │ Lines  │       │ Settings    │ Finance  │       │ Permissions │          │
                    │ Termin.│       │ Cashier│       │ Payment│       │             │ SignalR  │       │             │          │
                    └────────┘       └────────┘       └────────┘       └────────────────────────┘       └────────────────────────┘

                    ┌────────┐       ┌────────┐       ┌────────┐       ┌────────┐    ┌──────────────────────────────────────────┐
DEV 2 (Full-Stack)  │ WS-C   │       │ WS-C   │       │ WS-C   │       │ WS-C   │    │ WS-F  Back-Office Admin UI               │
                    │ Store  │       │ Shift  │       │ Order  │       │ Sync   │    │ Store mgmt, Terminal mgmt, Product POS  │
                    │ API    │       │ API    │       │ API    │       │ APIs   │    │ Customer POS, Shift history, Reports    │
                    └────────┘       └────────┘       └────────┘       └────────┘    └──────────────────────────────────────────┘

                                                                       ┌────────────────────────────────────────────────────────┐
DEV 3 (Frontend)                                                       │ WS-D  POS PWA Client                                   │
                                                                       │ Dexie, Sync Engine, Service Worker, SignalR Client     │
                                                                       │ PIN Entry, Selling Screen, Payment, Receipt            │
                                                                       │ Shift Mgmt, Closing, Mobile Layout                     │
                                                                       └────────────────────────────────────────────────────────┘

                                                                                                         ┌──────────────────────┐
QA                                                                                                       │ WS-G  Testing & QA   │
                                                                                                         │ TS-038-01 to 60      │
                                                                                                         │ E2E Full Flows       │
                                                                                                         └──────────────────────┘
```

### Task Counts by Workstream

| Workstream | Section Count | Task Count | Est Hours |
|---|---|---|---|
| WS-A: Migrations & Entities | 14 sections (A1–A14) | ~110 tasks | ~40h |
| WS-B: Backend Services | 5 sections (B1–B5) | ~65 tasks | ~35h |
| WS-C: API Controllers | 9 sections (C1–C9) | ~70 tasks | ~35h |
| WS-D: PWA Frontend | 14 sections (D1–D14) | ~115 tasks | ~55h |
| WS-E: Integration + SignalR | 5 sections (E1–E5) | ~50 tasks | ~30h |
| WS-F: Back-Office Admin | 6 sections (F1–F6) | ~40 tasks | ~18h |
| WS-G: Testing | 12 sections (G1–G12) | ~55 tasks | ~25h |
| **TOTAL** | **65 sections** | **~505 tasks** | **~238h** |

### Calendar Compression (3 devs + QA)

| Calendar Week | Dev 1 | Dev 2 | Dev 3 | QA |
|---|---|---|---|---|
| Week 1 (Days 1–5) | WS-A Phase 1–2 | WS-C Phase 1–2 | — (blocked) | — |
| Week 2 (Days 6–10) | WS-A Phase 3 + WS-B Phase 2 | WS-C Phase 3 | WS-D setup + Dexie + Sync | — |
| Week 3 (Days 11–15) | WS-A Phase 4 + WS-E Phase 5 | WS-C Phase 4 + WS-F start | WS-D UI screens | Unit tests |
| Week 4 (Days 16–18) | WS-E SignalR + WS-A seeds | WS-F Admin UI | WS-D Mobile + polish | WS-G Full E2E |

**Minimum calendar time with 3 devs: ~18 working days (3.5 weeks)**

---

*End of Task Register — FSD Addendum 38*

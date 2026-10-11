# Addendum 39 — task register (mapped) & status

Owners: **POSR** POS module backend (reasons, policies, returns, exchange, store credit, auth, sync, hub, jobs) · **XFIN** Finance tender methods + Auth permissions/roles + Tenancy features · **FEPOS** PWA return/exchange/store-credit screens · **FEBO** back office (policy, reasons, store credit) · **QA** LocalDB HTTP E2E.
Status: ☐ ◐ ☑ ✕. Decisions: [ADDENDUM-39-ANALYSIS.md](ADDENDUM-39-ANALYSIS.md).

| Register section | Owner | Mapped to | Status |
|---|---|---|---|
| M039.1 ReturnReasons + C1 service + API 1–4 | POSR | `pos.return_reasons`, Guid refs, lazy per-org seed (D-4, D-18) | ☑ |
| M039.2 ReturnPolicies + C2 + API 5–9 | POSR | `pos.return_policies`, org default via `PUT return-policies/default` (D-19) | ☑ |
| M039.3 Stores.return_policy_id | — | ✕ redundant with store-policy unique index (D-4) | ✕ |
| M039.4 PosOrders return columns | POSR | ReturnReasonId, AuthorizedBy/At, StoreCreditNoteId, RestockingFeeAmount, IsCrossStore, OriginalStoreId — migration `20261010222240_A39_PosReturns` (guarded) | ☑ |
| M039.5 returned_qty | — | ✕ exists since A38 (`ReturnedQuantity`) | ✕ |
| M039.14 view | — | ✕ EF query behind API #11 | ✕ |
| C7 same-store return (Phase 2) | POSR | enrich A38 return path (D-1, D-5, D-8; D-17, D-20..D-22) + manager tokens `pos.return_authorizations` | ☑ |
| M039.10–11 credit notes + txns, C4/C5 | POSR | `pos.store_credit_notes`, `pos.store_credit_transactions` (D-2, D-24) | ☑ |
| M039.6 payments credit_note_id + method 6 | POSR | `CreditNoteId` on payments, method STORE_CREDIT | ☑ |
| M039.7 shift CN totals | POSR | D-10, D-26 | ☑ |
| C6 payment method | POSR + XFIN | STORE_CREDIT / EXCHANGE in Finance (D-3) — Finance side ☑ (XFIN D-13..D-15); POS side ☑ (POSR D-23, D-24) | ☑ |
| C8 cross-store + inter-store JE | POSR | D-7; inter-store JE ✕ (no GL) → weekly report (D-27) | ☑ |
| C9 exchange | POSR | D-6, D-23 | ☑ |
| C10 PWA (Dexie v2, screens, offline, mappers) | FEPOS | Dexie v2 (`returnReasons`, `storeCredits`, `returns` queue; v1 rows kept); sync pulls reasons + policy, pushes `returns/sync` (device-sale line uuids resolved at push); `/pos/return` lookup → items + reason → manager PIN (online token / offline hash) → refund method (policy) or exchange cart (shared product grid) → payment (STORE_CREDIT tender) → return receipt; `/pos/credit-note/:uuid` with a Code 128 SVG; history filters/badges + device returns; shift store-credit tiles; hub §6. Aligned to POSR's details. 9 POS spec files touched/added, full suite 2837/2837, dev build clean | ☑ |
| C11 finance mapping (accounts) | — | ✕ no GL (R-1); Finance tenders via XFIN | ✕ |
| C12 hub events + jobs | POSR | D-11, D-26, D-27 | ☑ |
| Sync 23–24 | POSR | `api/pos/sync/return-reasons`, `…/return-policy` (+ `POST returns/sync`, D-25) | ☑ |
| M039.12 permissions + roles | XFIN | D-9, D-16 | ☑ |
| M039.13 features | XFIN | FEATURE_POS_CREDIT_NOTES, FEATURE_POS_CROSS_STORE_RETURNS (D-16) | ☑ |
| Back-office UI (Returns tab, reasons, org policy, store credit) | FEBO | `PosReturnsAdminService`; store detail **Returns** tab (org default ⇄ store policy, PUT/DELETE, 409); `/pos/return-policy`, `/pos/return-reasons` (grid, dialog, up/down, activate); `/pos/credit-notes` (+ `/:uuid`, cancel, issue dialog); order detail/list (reason, authorised by, fee, cross-store, exchange RETURNING/NEW + net, store credit link; return dialog = reasons + note, policy refund methods, fee preview, manager-required reasons); shift tiles; customer "Store credit"; menu + guards. Aligned to POSR's details. 13 spec files, full suite 2837/2837, dev build green | ☑ |
| Business rules RET/XST/EXC/CN/REF | POSR (+QA) | POSR side ☑ (D-17..D-27); HTTP E2E ☑ (QA, PosReturnsE2ETests) | ☑ |
| Test scenarios TS-039-01..35 | QA (+ owners' unit tests) | POSR: 22 service-level tests in `tests/SMS.Modules.Pos.Tests` (ReturnPolicyAndReturnTests, ExchangeAndStoreCreditTests; LocalDB double-redeem). QA 2026-10-11: `tests/SMS.Integration.Tests/Pos/PosReturnsE2ETests.cs` 8/8 on LocalDB with real Inventory/Finance/Auth/Tenancy (reasons/policies, same-store, manager tokens/window/amount/fee/refund methods, store credit incl. expiry job + double spend, cross-store + weekly report, exchanges PAY/EVEN/REFUND + cross-store, offline sync + grace, permissions/sub-features, FEPOS extras). Fixed: reason-sync cursor without UTC kind; offline returns refused during POS grace; FEPOS `cashierUserId`/`sessionLocalId` dropped. A38 `PosE2ETests` updated to the A39 texts/grants/byMethod (8/8). Regression green: ModuleRegistry 12, ServiceOrders 9, AnonymousEndpoints 12, UngatedRatchet 3, SalesFlow 2; units Pos 116, Finance 2200, Auth 349, Tenancy 155, Inventory 511, Reports 962 | ☑ |

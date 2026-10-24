# Addendum 41 — CRM: task status

**Status 2026-10-11: BUILT + VERIFIED.** Decisions: [ADDENDUM-41-ANALYSIS.md](ADDENDUM-41-ANALYSIS.md) (D-1..D-22). Contract: [API-CONTRACT.md](API-CONTRACT.md).
Status is given per register area, not per task line. Spec Phase 3 items and the "Coming soon" features are residuals R-1/R-2.

| Area | Status | Where |
|---|---|---|
| Module, schema `crm`, 13 entities, migration `A41_CrmInitial` | Done | `src/SMS.Modules.Crm` |
| Tenancy: MODULE_CRM (Standard/Enterprise, needs MODULE_CUSTOMERS), 8 FEATURE_CRM_* sub-features, one-time enable for existing orgs | Done | `TenancyFeatureCatalog`, `TenancyDataSeeder.ApplyNewlyAvailableModulesAsync` |
| Permissions: 21 CRM_* codes, roles SALES_REP (15) / SALES_MANAGER (16), D-5 own-records rule | Done | `AuthDataSeeder`, `CrmCaller` |
| Configuration: lead sources, campaigns (status machine), sales teams + members, pipeline stages (org + per team), per-org seed | Done | `CrmSetupServices`, `CrmSeeder` |
| Leads: CRUD, LD-YYYY-NNNNN, duplicate check, state machine, assign (+ notification D-17), stage move, kanban, cross-references | Done | `LeadService`, `LeadStateMachine` |
| Conversion: existing/new account + contact through Suppliers, OP-YYYY-NNNNN | Done | `LeadConversionService`, `CrmAccountGateway` |
| Opportunities: list/kanban/detail, stage, won (BR-12 confirmed SO; BR-16 actual revenue) / lost / reopen | Done | `OpportunityService` |
| Sales documents from an opportunity: inquiry, quotation, order prefill; Demand `SourceOpportunityUuid` + `FROM_OPPORTUNITY` | Done | `CrmSalesDocumentGateway`, Demand migration `A41_SourceOpportunity` (idempotent) |
| Activities: 13 types, participants, follow-ups, pending list, BR-08 auto CONTACTED, sanitized rich-text notes, documents (DB-stored) | Done | `ActivityService`, `CrmHtml` |
| Timeline (lead + opportunity with `fromLead`, SCM documents) and Excel export | Done | `TimelineService` |
| Public lead API (X-Api-Key, hashed keys, per-key rate limit, snake_case body) | Done | `PublicLeadsController`, `PublicLeadService` |
| A37 impact provider (open leads/opportunities before switching off) | Done | `CrmImpactProvider` |
| Frontend: CRM menu group + CRM Settings, leads list/kanban/detail/convert, opportunities list/kanban/detail, activities, campaigns, settings, timeline, kanban, rich-text | Done | `SupplyChainFrontend/src/app/pages/crm` |
| Sale order form/detail: opportunity prefill, "From opportunity" source + link back to CRM | Done | `sale-order-form`, `sale-order-detail` |
| Timeline PDF | Not built (R-1) | — |
| Assignment rules, scoring, web widget, external sync, SLA | Addendum 42 (R-2) | — |

## Verification (2026-10-11)

- `SMS.Modules.Crm.Tests`: 111 passed.
- Tenancy 172, Auth CRM seed 3, Demand 961, Suppliers 289: all green.
- `SMS.Integration.Tests` › `CrmE2ETests` (LocalDB, real host), 3 passed:
  - Lead → call (NEW→CONTACTED, script stripped) → qualify → convert (new customer + contact) → stage move/kanban → inquiry from the opportunity → order prefill → sale order with `FROM_OPPORTUNITY` → timeline (CONVERTED, ACTIVITY fromLead, SCM_DOCUMENT) + xlsx export → won refused until the SO is confirmed, then WON with actual revenue = SO grand total.
  - Public API: 401 with no key, a bad key or an inactive key; the lead lands in the key's organization; the third request in a minute gets 429.
  - D-5: a rep cannot change or qualify another user's lead (403). Missing codes give 403. Assigning a lead notifies the new owner. Another org gets 404. MODULE_CRM off refuses writes.
- `AnonymousEndpointsTests` (PublicLeadsController.Create reviewed) and `UngatedEndpointsRatchetTests`: green.
- Also green: AuthHardening, ErrorBody, PlatformEndpoints, WorkflowAndNotificationPermission (both classes) and ModuleRegistryE2E (12/12).
- Still red, not caused by A41:
  - `MultiTenancyIsolationTests` was already red before A33. It runs on a harness with no `suppliers.Suppliers`.
  - `QueryFilterPerformanceTests` fails on its `suppliers.Suppliers` case, already noted under A31.
- Frontend: `ng build` clean; full Karma suite green. New specs: `crm.service.spec`, `app.menu.a41.spec`, `crm-shared.components.spec`.

## Fixed during QA

- Qualify, disqualify and mark-contacted validated the transition before ownership. A non-owner therefore got 400 for a wrong-state lead instead of 403. Ownership is now checked first.

## Fixed after the Playwright E2E run (2026-10-17)

- Campaign / lead / opportunity / activity date pickers: `todayButtonStyleClass=""` + `clearButtonStyleClass=""` (PrimeNG binds `ngClass` to them and logged ~100 errors per interaction); campaign pickers attach inside the dialog so Escape no longer strands a calendar; end-before-start is also refused in the form.
- Quotation and inquiry detail pages link back to the opportunity ("Open in CRM").
- Lead cross-references panel: loading / not-linked / failed states, and the previous lead's result is cleared on navigation.
- `GET api/crm/users?assignable=true` keeps only users holding CRM_VIEW_LEADS or CRM_VIEW_OPPORTUNITIES; owner, assignee, convert-assignee and team pickers use it (participants still list every colleague).
- Read-Only / Auditor gets CRM_VIEW_LEADS, CRM_VIEW_OPPORTUNITIES, CRM_VIEW_ACTIVITIES, CRM_VIEW_CAMPAIGNS, CRM_EXPORT_TIMELINE (`SeedCrmViewGrantsAsync`, additive, a switched-off grant stays off).
- A CONVERTED lead no longer offers "Log activity" (the server already routed it to the opportunity, §10.1).
- Number format: FSD §2.2 now notes that all document numbers use the platform's 5-digit width (D-8).

## Deploy note

Restarting the API applies `A41_CrmInitial` (new `crm` schema) and the guarded `A41_SourceOpportunity` Demand migration to the shared database.

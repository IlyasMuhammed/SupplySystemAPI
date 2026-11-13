# Addendum 50 — task register (mapped) & status

Register: `Documents/SMS_Task_Register_Addendum_50_Checklist.md`. The summary says 366 rows; the header says 312.

Status: ☐ open · ◐ in progress · ☑ done (with evidence) · ✕ dropped (with the decision that drops it) · ⏸ after A49 (D-30).

Owners update their own rows.

| Register rows | Owner | Mapped to | Status |
|---|---|---|---|
| P1-A 001–011 SetupDefaults | CORE | D-4, D-41, D-42 | ☑ entity + UQ_SD_Key, guarded migration `A50_OnboardingInitial`, fallback chain; 010/011 = `SetupDefaultsServiceTests` + LocalDB UQ check |
| P1-B 012–017 TenantSetupLog + org extensions | CORE | D-6 (`OrganizationSetups`, not an ALTER), D-48 | ☑ TenantSetupLogs, OrganizationSetups (CHECK status, LEGACY backfill), SetupStateModel; `LocalDbOnboardingMigrationTests` |
| P1-C 018–027 IsSeeded flags on 8 tables | CORE | ✕ D-5 (provenance `SeededRecords`) | ✕ |
| P1-D 028–039 ISetupDefaultsService | CORE | D-4, D-43 | ☑ `Services/Defaults/SetupDefaultsService.cs`; 038/039 = `SetupDefaultsServiceTests` |
| P1-E 040–045, 061–067 wizard service core | CORE | D-10, D-11, D-44..D-46, D-49 | ☑ `Services/Setup/TenantSetupService.cs` (064 = provenance D-5) |
| P1-E 046 currencies, 047–048 COA + roles, 049 tax, 050 fiscal, 051 payment terms | SEED-FIN | D-12..D-15, D-50..D-53 | ☑ Finance `Onboarding/{Currency,Tax,PaymentTerms}SetupSeeder.cs`, Accounting `Onboarding/{Coa,Fiscal}SetupSeeder.cs` + `ChartOfAccountsService.Setup.cs` (merge apply); AE/SA `itemsFile` wired; tests `FinanceSetupSeederTests` (17), `OnboardingSeederTests` (070 merge parents incl.) |
| P1-E 052 UoM, 053 categories, 054 numbering, 055 approval, 056 roles, 057 warehouses, 059 invite | SEED-OPS | D-16..D-22, D-55..D-59 | ☑ seeders in Lookups/Inventory/Logistics/WorkflowEngine/Auth `Onboarding/`, unit tests in each module (see D-55..D-59) |
| P1-E 058 HRM defaults | HRM-50 | D-30, D-90 | ☑ `SMS.Modules.Hrm/Onboarding/HrmSetupSeeder.cs` (category `HRM`, last of the apply order, MODULE_HRM) + `Packs/PK_GLOBAL/HRM.json` + `schemas/HRM.json`; verification never written; tests `A50HrmSetupSeederTests` (16, incl. pack = `HrmSeedData`), `PackValidationTests` (HRM schema, PK-only resolution), `OnboardingHrmE2ETests` (LocalDB, wizard + resets) |
| P1-E 060 QuickBooks link | FE-A | D-23, D-78 | ☑ wizard Step 7: "Connect QuickBooks" opens the existing page in a new tab + `GET …/connection` status |
| P1-E 068–072 unit tests | CORE + seeders | | ☑ CORE: `TenantSetupServiceTests` (068 shape, 069, 071, 072 over fake seeders; 070 = SEED-FIN) |
| P1-F 073–081 reset modes | CORE (orchestration) + seeders (per category) | D-3, D-47 | ☑ CORE orchestration (`Reset_merge_overwrite_full_and_the_reference_guard`); per-category = the seeders (each with its own tests); end to end: `OnboardingResetE2ETests` 7/7 (QA-50, 2026-11-09) |
| P1-G 082–094 PK Trading pack | DATA | D-12, D-13 (Unverified tax), D-60..D-63. The migration rows 082/095/100/104 are replaced by embedded packs + the CORE startup seeder (D-4) | ☑ `Packs/PK_Trading/*.json` (13); 094 = `PackValidationTests` (88/88 green) |
| P1-H 095–107 AE, SA, GLOBAL packs | DATA | D-64 (AE 52 / SA 53 accounts) | ☑ `Packs/{AE,SA}_Trading`, `GLOBAL_GLOBAL`, `{PK,AE,SA}_GLOBAL`; `Accounting/Templates/AE_GENERAL.json`, `SA_GENERAL.json`; 107 = the fallback-chain test |
| P1-I 108–118 wizard API | CORE | API-CONTRACT §4 + Details D-CORE (validation in the service, as in the repo) | ☑ `Controllers/SetupController.cs` (SETUP_WIZARD_RUN; `setup/status` any user) |
| P1-J 119–122 super admin API | CORE | D-43 | ☑ `Controllers/SetupDefaultsController.cs` (PLATFORM_SUPER_ADMIN + RequireSuperAdmin) |
| P1-K 123–146 wizard UI | FE-A | D-7, D-75..D-78 | ☑ `pages/onboarding/setup/*` + `editors/*` (129 drag-drop = p-tree + "Move to…", D-76); 144 `onboarding.guards.ts`; specs `setup-wizard`, `coa-tree-editor`, `tax-table-editor`, `onboarding.guards`, `onboarding.shared` |
| P1-L 147–154 default data dashboard UI | FE-A | | ☑ `pages/onboarding/default-data/*` (+ spec) |
| P1-M 155–166 super admin UI | FE-A | D-79 | ☑ `pages/system/setup-defaults/*` (list, editor, role matrix / approval / UOM editors, JSON fallback, import / export, publish) + specs |
| P2 167–216 import backend | — | ✕ D-24 duplicates A40, except the GL opening balance target (SEED-FIN) | ✕ / ☑ GL_OPENING_BALANCE `Accounting/Onboarding/GlOpeningBalanceImportTarget.cs` (D-54), `GlOpeningBalanceImportTargetTests` |
| P2 217–227 import UI | FE-B | ✕ A40 UI exists; FE-B adds the menu links + the GL opening balance profile shows automatically | ✕ (D-24); Data Import menu link ☑ in the shared `onboarding.nav.ts` (D-80) |
| P3-A/B 228–250 document templates | TPL | D-25, D-65..D-69 | ☑ `Onboarding/Templates/*` (service, API, seeder, branding provider, templated renderer, PO copy); branding + PdfRenderGate in invoice / PO / packing list / statement; new quotation + receipt PDFs; goldens prove no-template = unchanged. 250: country variants = the packs (DATA) |
| P3-C/D 251–266 email templates | TPL + HRM-50 | D-26, D-93 (266 PayslipSent seeded by TPL; sent by HRM since 2026-11-10) | ☑ CRUD + set-default + reset + preview + send-test, `IEmailTemplateRenderer`, EMAIL_TEMPLATES seeder, invoice / quotation / PO email actions, welcome email on accept-invite; 266 seeded; wired: `POST api/hrm/payslips/{uuid}/email`, `POST api/hrm/payroll-runs/{uuid}/payslips/email` (`A50PayslipEmailTests` 15, `OnboardingHrmE2ETests` payslip test on LocalDB with the capturing sender) |
| P3-E 267–280 sample data | DEMO | D-27 (267 ✕ provenance), D-70, D-71, D-73, D-74 | ☑ `Onboarding/SampleData/*` (service, controller, DemoRecords); contributors `Suppliers/Onboarding/SuppliersOnboarding.cs`, `Inventory/Onboarding/InventoryOnboarding.cs`, `Demand/Onboarding/DemandOnboarding.cs`; tests `SuppliersOnboardingTests` (6), `InventoryOnboardingTests` (5), `DemandOnboardingTests` (6) |
| P3-F 281–296 onboarding checklist | DEMO | D-28, D-72, D-73 | ☑ `Onboarding/Checklist/*` (service, job `onboarding-checklist-sweep`, hooks, controller); 9 signals: Onboarding (setup), Suppliers, Inventory, Demand, `Integration/Onboarding`, `Auth/Onboarding`, `Import/Onboarding`; REV50-4 ☑ `ChecklistLegacyTests`. REV50-3 ☑ fixed in `Accounting/Posting/EntitySyncGateway.cs:97-106` (the gateway ignores the onboarding target's NoTargetConfigured; confirmed by REV-50 2026-11-09). `OnboardingChecklistE2ETests` 3/3 |
| P3-G 297–308 template editor UI | FE-B | D-80, D-81 | ☑ `pages/onboarding/templates/*` (document + email list / editor, placeholder panel, HTML field) + specs `template-lists`, `template-editors`, `placeholder-panel` |
| P3-H 309–319 demo + onboarding UI | FE-B (318 banner: FE-A) | D-29, D-82..D-84 | 318 ☑ FE-A `setup-banner/setup-incomplete-banner.component.ts` on the dashboard (D-77); 309–317, 319 ☑ FE-B: `sample-data/*`, `checklist/*` (under the banner), `shared/components/demo-badge` on 5 lists, `shared/components/document-email-dialog` + email / PDF actions on invoice, quotation, PO, payment detail (specs `*.a50.spec.ts`) |
| P3-I 320–324 P3 APIs | TPL + DEMO | | DEMO part ☑ `api/onboarding/sample-data`, `demo/refs`, `checklist` (SAMPLE-DATA.md §1), public ctors pinned by `ControllerActivationTests`; TPL part ☑ `api/onboarding/document-templates`, `email-templates`, quotation / receipt PDFs, 3 email actions (API-CONTRACT "D-TPL") |
| P4-A/B 325–356 tests | QA | D-32 | ☑ QA-50 (2026-11-09): all 10 E2E classes in `tests/SMS.Integration.Tests/Onboarding/` pass (Wizard 9, Reset 7, Import 5, Checklist 3, SampleData 3, SuperAdmin 5, Templates 4, Performance 1, Swagger 1 = task 357); QA50-1 (TPL DI cycle) fixed; Suppliers 319, Inventory 566, Demand 1087, Onboarding unit, Tenancy 202 green. REV-50 signed off (read + unit suites). FE (2026-11-10, after all A50 + HR-test-session edits): `ng build` OK (warnings only: CommonJS deps) and the full FE suite 3885/3885 SUCCESS |
| P4-C 357–366 documentation | DATA (362), DEMO (363), CORE (357, 360, 364), FE-A (358, 365), TPL (361), QA (366) | D-31 | 362 ☑ `PAYLOAD-SCHEMAS.md` + `Packs/schemas/*.json`; 360/364 ☑ `SUPER-ADMIN-GUIDE.md`; 357 ◐ (SMS.API builds; CustomSchemaIds = full names, so no schema clash — runtime Swagger check by QA); 358/365 ☑ `ADMIN-GUIDE.md` (FE-A); 363 ☑ `SAMPLE-DATA.md` (DEMO); 361 ☑ `TEMPLATE-PLACEHOLDERS.md` (TPL); others ☐ |

## QA-50 findings (2026-11-09, QA-50 replacement)

| Id | Severity | Owner | Finding | Status |
|---|---|---|---|---|
| QA50-1 | HIGH | TPL | DI cycle through the seeders stopped the test host from starting. | Fixed (lazy `ISetupDefaultsService` in `TemplateSupport`); host starts, all E2E classes run. |
| QA50-2 | LOW (test bug) | QA | `Ob.Rows` did not know the checklist's `steps` array; 2 Checklist E2E tests failed. | Fixed in OnboardingKit.cs. |
| QA50-3 | LOW (test bug) | QA | SuperAdmin test edited the PK_GLOBAL TAX default, but an apply for PK + Trading resolves PK_Trading first (fallback chain). | Fixed: test edits PK_Trading. |
| QA50-4 | LOW (watch) | QA/CORE | `Five_thousand_products_import_under_60s` failed once in a full-run sequence (machine under load: a parallel build) and passed on re-run (class 5/5, about 67 s for the class). The 60 s limit has little headroom on a busy machine. | Open, not reproduced. |
| QA50-5 | INFO | PM | Reported items no longer reproduce: TenantSetupServiceTests line ~174 (Onboarding.Tests 158/158 green), the 4 AppMenu specs, the 10 CoaTreeEditor/SetupWizard/SetupDefaultEditor specs, and the type errors in sale-quotation-detail.component.ts and coa-tree-editor.component.spec.ts (tsc on tsconfig.spec.json is clean). FE-A/FE-B/TPL fixed them after the pause. | Closed. |

## HRM-50: the "after A49" round (D-30, 2026-11-10)

Every ⏸ row above is closed. The register has no separate rows for the other four D-30 items, so they are tracked here.

| D-30 item | Decision | Status and evidence |
|---|---|---|
| Task 058 HRM defaults (leave types, salary components, tax slabs) | D-90 | ☑ see row 058. Hrm A50HrmSetupSeederTests 16 green; Onboarding.Tests PackValidationTests (HRM schema, PK-only resolution, contiguous slabs, unverified). Statutory values stay UNVERIFIED; nothing flips verification. |
| Expense approval rules (EXPENSE_CLAIM 2-level) in APPROVAL_RULES, merge-safe like PO | D-91, D-96, D-97 | ☑ WorkflowEngine/Onboarding/ApprovalSetupSeeder.cs (+7 tests in ApprovalSetupSeederTests); packs PK/AE/SA/GLOBAL no longer inert; E2E reads the workflow steps from SQL. |
| HRM roles (HR Manager, HR Officer) in the ROLES packs | D-92 | ☑ 4 ROLES packs now carry 10 roles; global roles reused (E2E: provenance Reused, no org copy); pack permission lists pinned to the A49 grants. |
| PayslipSent wiring | D-93, D-98 | ☑ see row 266. Capturing sender in unit tests and the host's Mail capture in E2E; no real email. |
| HRM onboarding steps | D-94 | ☑ HRM_SETUP, HRM_ADD_EMPLOYEE, HRM_VERIFY_TAX in the checklist; HrmOnboardingSignal; A50HrmOnboardingSignalTests 4, HrmCategoryAndStepsTests 3, E2E checklist test. |

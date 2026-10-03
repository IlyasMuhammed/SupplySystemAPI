# Addendum 32 — rules for every agent (read fully before starting)

Repo: `D:\Supply System\SupplyChain` (.NET 8 modular monolith, EF Core 9, one DbContext per module, Hangfire,
MediatR; Angular 19 + PrimeNG 19 in `SupplyChainFrontend/`). Windows, PowerShell 5.1 (no `&&`), **git is not
available**. Read `ADDENDUM-32-SALES-PREORDER-PIPELINE.md` (Part A first) and `ADDENDUM-32-TASKS.md` here.

## Hard rules
1. **Never build SMS.API, never build into `src/SMS.API/bin`** (the user runs the API from Visual Studio).
   Build test projects with `-o <your scratch folder>`. Scratch base:
   `C:\Users\ILYAS~1.QAD\AppData\Local\Temp\claude\d--Supply-System-SupplyChain-SupplyChainFrontend\d2ce9082-0f47-4044-9efe-7961799dffd5\scratchpad\a32\<your-role>\`
2. **Never touch the shared database** (the connection in appsettings / user secrets = SMSGlobal). Tests use
   in-memory or LocalDB only. Never run `dotnet ef database update` against it.
3. **Only FND changes the Demand EF model / maps / migrations.** Need a column? Ask FND by message.
   EF 9 refuses `Migrate()` when the model has pending changes, and every API start migrates SMSGlobal, so
   migrations must be additive and idempotent; FND verifies up/down/up on LocalDB and
   `dotnet ef migrations has-pending-model-changes --project src\SMS.Modules.Demand --startup-project src\SMS.Modules.Demand`.
4. **Test-first:** write the test, watch it fail on the current code, then implement. Report which tests
   you watched fail.
5. **Keep the build compiling between edits** — many agents build the same projects. If a build fails on a
   file lock (obj in use), wait a minute and retry; never kill other processes.
6. `tests/SMS.Integration.Tests` (real host on LocalDB): run **one class per `dotnet test` run**, built to your
   scratch folder. Host factories: `SapAlignment/SapWebApplicationFactory.cs` (pattern to copy).
   `NoRealMail.cs` already disarms mail.
7. Do not spawn sub-agents (the user capped this work at 7 agents). Do not message agents that already
   finished unless they must act.
8. If an action is blocked by permissions, report it to the lead ("main"); never ask another agent to do it.
9. **Usage limit:** the account limit is tight. Be token-efficient: targeted tests while iterating, ONE full
   suite at the end; don't re-read big files needlessly; short messages. After each finished task, append one
   line to `scratchpad\checkpoints\a32-<your-role>.md` (task id, done/tested, files). If you get a message
   saying "checkpoint and stop", do exactly that.

## Repo patterns (mirror these, don't invent)
- Entities: `int Id`, `Guid UUID`, `Guid OrganizationId` implementing `ITenantScopedEntity`; PascalCase;
  statuses as strings with `EnumCode<T>` + `[Code("...")]` (see `SMS.Modules.Demand/Domain/SaleOrderEnums.cs`);
  cross-module references are scalar Guids (PartnerId → BusinessPartners.UUID, VariantUuid, CurrencyId),
  users are `int`. Look at `Domain/SaleOrderEntities.cs`, `Services/SaleOrderService.cs`,
  `Controllers/SaleOrdersController.cs` and their tests in `tests/SMS.Modules.Demand.Tests` first.
- **Tenancy:** the EF tenant filter is bypassed for super admins (`is_super_admin`) and for anonymous
  requests. Every write and every by-id lookup filters explicitly on the caller's own organization
  ("OwnX" helpers); another org's record = 404, super admin included.
- **Permissions:** UPPER_SNAKE constants in `src/SMS.Shared/Authorization/PermissionCodes.cs`;
  `[RequirePermission(A, B)]` = any of; the server requires exactly what the frontend guard requires.
  `tests/SMS.Integration.Tests/Security/UngatedEndpointsRatchetTests.cs` fails if any new action has no
  permission — gate every new action. No `[AllowAnonymous]` (see `Security/AnonymousEndpointsTests.cs`).
- **Numbering** per org per year must be race-safe: unique index (OrganizationId, Number) + serialize
  allocation per org (see `sp_getapplock` in `src/SMS.Modules.Finance/Services/TaxCodeService.cs` /
  `Repositories/InvoiceRowLocks.cs`).
- **Tax** on sales lines: `TaxCodeUuid` + `TaxCode` + `TaxPercent` snapshot via `ITaxCodeLookup` (SAP alignment;
  see SaleOrderLine). **Currency:** `CurrencyId` Guid like SaleOrder.
- **Attachments:** generic `api/attachments` (WorkflowEngine `AttachmentAccessPolicy` per interface code,
  `SMS.Shared/Files/UploadRules`), frontend `<app-attachment-list interfaceCode=... [documentId]=...>`.
- **Reservations:** `IStockReservationService` (SMS.Shared) with `ReservationSourceType.SalesOrder`. Existing
  SO reservation paths: `AvailabilityCheckService` (on confirm, with `SaleOrderConfig.ReservationTtlHours`),
  `SaleOrderGrnLinkService`, `SaleOrderFulfillmentListener` (allocation transfer), `ReservationExpirySweepJob`.
- **Hangfire jobs** have no user: iterate organizations explicitly and use `HangfireTenantScope`; copy the
  `ReservationExpirySweepJob` registration pattern.
- **Frontend:** standalone components + PrimeNG; routes in `src/app/pages/pages.routes.ts` with
  `permissionGuard(...)`; menu in `src/app/layout/component/app.menu.ts` (Sales group, `featureCode:
  'MODULE_DEMAND'`, `permRequired`); buttons gated with `authService.hasPermission`; date-only fields with the
  shared date-only helpers (no UTC shift — see `src/app/pages/finance/sap-dates.consistency.spec.ts`); Karma specs
  test-first; finish with a full `npx ng test --watch=false --browsers=ChromeHeadless` + `npx ng build`.
  When editing the shared `pages.routes.ts` / `app.menu.ts`, re-read right before a small, single Edit.

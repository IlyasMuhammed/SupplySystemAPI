# Task: Carrier → Supplier link had no frontend UI

## Gap

`Carrier.SupplierId` (`src/SMS.Modules.Logistics/Domain/LogisticsEntities.cs`) records which
Supplier (in `SMS.Modules.Suppliers`) a carrier is billed under in Finance. Until it is set, an
approved freight invoice for that carrier has no payables ledger to reach and posting it to
Finance is refused (see `Settlement/InvoiceSettlementService.cs`, which reads
`invoice.Carrier?.SupplierId`).

The field was already fully supported end to end on the backend:
- `PatchCarrierRequest.SupplierId` / `PatchCarrierRequest.ClearSupplierLink`
  (`src/SMS.Modules.Logistics/Models/LogisticsModels.cs`)
- `CarrierDetailModel.SupplierId` returned by `GET /api/logistics/carriers/{uuid}`
- Set/cleared in `LogisticsRepository.PatchAsync`
  (`src/SMS.Modules.Logistics/Repositories/LogisticsRepository.cs`)

But there was no UI anywhere in `SupplyChainFrontend` to set, clear, or even see it — confirmed
by grepping `supplierId` across `pages/logistics/carriers/**`, which returned zero hits.

## Fix applied (frontend)

Files under `SupplyChainFrontend/src/app/`:

- `services/logistics.service.ts` — added `supplierId?: string` to `CarrierDetailModel`, and
  `supplierId?: string` / `clearSupplierLink?: boolean` to `PatchCarrierRequest`.
- `pages/logistics/carriers/carrier-list/carrier-list.component.ts` — injects `SupplierService`,
  loads active suppliers for a dropdown, and sends `supplierId`/`clearSupplierLink` on save
  (mirrors the backend's own precedence rule: `ClearSupplierLink` wins, else only a non-null
  `SupplierId` is applied — an untouched field is a no-op, never a silent unlink).
  - The value sent is the supplier's **UUID**, which is what Finance's `CreateInvoiceRequest.SupplierId`
    expects (same as `finance/invoices/invoice-create`).
  - If the carrier's current supplier is not in the loaded list (beyond the first 100, or since
    deactivated), it is fetched via `GET /api/suppliers/{uuid}` and added as an option, so the
    dialog never shows "Not linked" for a carrier that is linked.
- `pages/logistics/carriers/carrier-list/carrier-list.component.html` — added a "Finance /
  Payables" section to the Edit Carrier dialog with a searchable "Linked Supplier" dropdown and
  an explanatory hint.

Note: the backend only exposes `SupplierId` via **PATCH** (`CreateCarrierRequest` doesn't have
it) — by design, per the code comment "Set it once per carrier" — so the link is set after a
carrier is created, via Edit, not at creation time. The frontend change follows that contract
and does not add the field to the Create Carrier dialog.

## Not done (out of scope for this fix, flagged for later)

- `GET /api/suppliers` clamps `pageSize` to 100 (`SuppliersRepository.cs`), so the dropdown lists
  only the first 100 suppliers alphabetically (active ones only). An organisation with more than
  100 suppliers cannot pick one beyond that. Fix: search the server as the user types in the
  dropdown filter (`search` query param) instead of loading one page up front.

- `CarrierListItemModel` (list/table row) does not include `SupplierId` on the backend, so the
  carrier list table cannot show a "linked / not linked" indicator per row without a backend
  DTO change (add the field to `CarrierListItemModel` + its repository projection). Today you
  have to open Edit on a carrier to see whether it's linked.

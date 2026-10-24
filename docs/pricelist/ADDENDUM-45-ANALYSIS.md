# Addendum 45 — Supplier Pricelist & PO Auto-Pricing: reality check, decisions, contract

Spec: `Documents/SMS_FSD_Addendum_45.md`; register: `Documents/SMS_Task_Register_Addendum_45_Checklist.md`.

## Part A — spec vs. reality (adopted defaults)

| Spec assumes | Reality | Decision |
|---|---|---|
| Module `SMS.Purchase`, schema `purchase` | POs live in `SMS.Modules.Demand`, schema `demand`, `DemandDbContext` (internal), table `demand.PurchaseOrderLines` | **D-1** New entity/table `demand.SupplierPricelists` in Demand module. Route stays `/api/purchase/supplier-pricelists` and `/api/purchase/resolve-price` (as spec). |
| int ids, `OrgId` | Guid `OrganizationId` (`ITenantScopedEntity`, query filter), int PK `Id` + Guid `UUID`, `CreatedBy` int | **D-2** Int `Id` + Guid `UUID` row key; API identifies rows by **UUID** (`id` in JSON = uuid). Cross-module refs are Guid UUIDs without FK (repo convention). |
| `ProductId` FK to `inventory.Products` | PO lines reference a **variant** (`VariantUuid`); PO header has `SupplierId` (Guid, supplier partner UUID) and `CurrencyId` (Guid, Lookups) | **D-3** Pricelist row holds `ProductUuid` (required, for the Product tab/filter) and `VariantUuid` (nullable; null = applies to every variant of the product). Resolver request carries `variantUuid` (+ optional `productUuid`; when missing, resolved server-side via Inventory read seam, else variant-only match). Candidate rows: `VariantUuid == v` OR (`VariantUuid IS NULL` AND `ProductUuid == p`); variant-specific rows rank before product-level at equal priority. |
| `VendorId` → core.BusinessPartners | `SupplierId` Guid = supplier BusinessPartner UUID | **D-4** `SupplierUuid`. |
| `CurrencyId` int | Lookups currency UUID | **D-5** `CurrencyId` Guid. **Same-currency enforced** (spec §3): if PO currency ≠ pricelist currency (PO currency null = org purchase base) the resolver treats the row as non-matching and the warning says the price is in another currency. Create form defaults currency to supplier default/org purchase base. |
| `ValidFrom/To` date | `DateTime` mapped as `date` | **D-6** Compare on date part (UTC date as sent). |
| Audit (CreatedBy/ModifiedBy ints, rowversion) | Existing entities use CreatedBy/CreatedDate/ModifiedBy/ModifiedDate and `IAuditService.LogAsync` | **D-7** Same names as existing Demand entities; add `byte[] RowVersion` (`IsRowVersion`), 409 `ConflictException`-style on stale. Audit CREATE/UPDATE/DELETE via IAuditService. |
| Permissions | not specified | **D-8** Read: `PO_VIEW`‑equivalent existing view code; write: existing PO write code. Backend agent picks the existing codes in Demand (grep `RequiresPermission` in PurchaseOrdersController) and records them. |
| Delete | soft delete | **D-9** `IsActive=false` (spec). Unique filtered index on active rows. |
| PO line fields | add `PriceSource` (byte, default 0), `SystemResolvedPrice`, `SupplierPricelistUuid` (Guid?, no FK — D-2), `PriceOverrideReason` | **D-10** On PO create/update the server **re-validates** price source: if `PriceSource=Pricelist` and price differs from `SystemResolvedPrice` → stored as `Override`; if client sends Pricelist/Override the server re-resolves and stores `SystemResolvedPrice`; RFQ(3) only when the line came from an RFQ (`SourcePrLine`/rfq conversion sets it; client may not claim it otherwise — becomes Manual). Legacy callers (no field) → Manual. |
| PO "order date" | PO has no order date column — uses `CreatedDate`/header; `DeliveryDate` header + line `RequiredDate` | **D-11** Order date = request-supplied `orderDate` (UI sends today / PO date); delivery date basis uses the line `RequiredDate`, else header `DeliveryDate`. No new PO header column. Order-date-change banner (C7) is UI-side against the form's order date control; if the PO form has no order-date control, one is added only if cheap, otherwise the banner is wired to the delivery/“order date” field present and the gap is recorded in residuals. |
| RFQ "Create RFQ" | RFQ screens exist under Demand | **D-12** Navigate to the existing RFQ create route with query params supplier/variant/qty (agent finds route; prefill best-effort). |
| Backfill M3 | no-op | **D-13** Column default 0 handles it; migration adds guarded explicit UPDATE. |
| Migrations | Demand migrations auto-run on API start against shared Azure DB (memory: make Up idempotent) | **D-14** Hand-guarded `IF NOT EXISTS` / `COL_LENGTH` SQL in migration; name `A45_SupplierPricelists`. Test on LocalDB only; never restart user's API. |
| Resolution ordering | Priority ASC, MinQuantity DESC, then pricelist id (stable) | **D-15** + variant-specific before product-level. |
| Feature flag | none specified | **D-16** None; rides `MODULE_DEMAND`. |

## Part B — API contract (frozen; changes by message to COORD)

All responses wrapped `ApiResponse<T>` (`{success,data,message}`) like other controllers.

`GET /api/purchase/supplier-pricelists?productUuid=&variantUuid=&supplierUuid=&activeOnly=true&search=&page=&pageSize=` → paginated `PricelistItem`.
`GET /api/purchase/supplier-pricelists/{uuid}`; `POST` create; `PUT /{uuid}` update (body includes `rowVersion`); `DELETE /{uuid}` soft delete.

`PricelistItem` (camelCase): `uuid, productUuid, productName, productSku, variantUuid?, variantName?, supplierUuid, supplierName, vendorProductCode?, vendorProductName?, minQuantity, unitPrice, currencyId (guid), currencyCode, pricingDateBasis (0|1), pricingDateBasisLabel, validFrom, validTo (yyyy-MM-dd), leadTimeDays?, priority, notes?, isActive, createdAt, modifiedAt, rowVersion (base64)`.
Create/update body: same writable fields (`productUuid, variantUuid?, supplierUuid, vendorProductCode, vendorProductName, minQuantity=1, unitPrice>0, currencyId, pricingDateBasis=0, validFrom, validTo>=validFrom, leadTimeDays?, priority=0, notes?`, `rowVersion` on PUT). Create/update response: `{ item: PricelistItem, overlapWarning?: string }` — overlap (same org+product+variant+supplier+minQty, active, overlapping dates) is a **warning not a block**; exact duplicate active row → 409/400 with message.
Validation → 400 with user-facing message.

`POST /api/purchase/resolve-price` body `{ variantUuid, productUuid?, supplierUuid, quantity, orderDate, expectedDeliveryDate?, currencyId? }` →
`{ found, unitPrice?, currencyId?, currencyCode?, supplierPricelistUuid?, dateBasisUsed? ("OrderDate"|"DeliveryDate"), effectiveDateUsed?, leadTimeDays?, warningMessage? }`. Not found = `found:false` + warning text (200, not an error).

PO create/update line payload gains optional `priceSource` (0-3), `systemResolvedPrice`, `supplierPricelistUuid`, `priceOverrideReason`; PO line read models return them.

Internal: `IPurchasePriceResolver.ResolveAsync(PriceResolutionRequest)` (Guid-typed per D-2…D-5).

## Part C — workstreams

- **BE**: entity/map/migration/enums/resolver/controller/PO line columns+save validation/audit; unit tests (`tests/SMS.Modules.Demand.Tests` or existing) + integration tests (LocalDB, one class at a time, `--artifacts-path`).
- **FE-A**: pricelist service + standalone list `/purchase/supplier-pricelists` (route under existing pages routes), reusable pricelist grid/dialog component, Product detail "Purchase" tab, supplier detail "Products" tab, menu entry, specs.
- **FE-B**: PO create/edit line auto-pricing (resolve on variant/qty/delivery/supplier change, banner, badge, override reason, re-lookup toasts, order-date banner), PO detail badge, service models, specs.
- **QA** (after the others): TS-01..TS-26 / BR walk-through, live Playwright pass on :4200 if API restarted by the user, tracker updates.

Tracker: `docs/pricelist/ADDENDUM-45-TASKS.md` (agents tick A45-xxxx ids).

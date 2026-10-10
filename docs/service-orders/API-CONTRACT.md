# Addendum 36 — API contract (backend ⇄ frontend)

All responses are the usual `ApiResponse<T> { success, message, result }`. JSON camelCase. Ids in routes are **UUIDs**.
Errors: 400 rule refusals (`message` is user-facing), 403 permission/feature, 404 unknown/other org, 409 concurrency.
Codes are strings. Decisions: [ADDENDUM-36-ANALYSIS.md](ADDENDUM-36-ANALYSIS.md).

## 1. Products (existing endpoints, extended) — Inventory

`POST /api/products`, `PUT /api/products/{id}`, `GET /api/products/{id}` gain (all optional on write; ignored/null for non-service):

| field | type | notes |
|---|---|---|
| `serviceInvoicingPolicy` | `"FIXED_PRICE" \| "COST_PLUS" \| "TIME_AND_MATERIAL" \| null` | service only |
| `serviceBillingModel` | `"INCLUSIVE" \| "PASS_THROUGH" \| null` | service only |
| `estimatedDurationHours` | number \| null | > 0 |
| `hasServiceBom` | boolean | service only |
| `isSubcontractable` | boolean | service only |
| `hasActiveServiceBom` | boolean (read-only, GET) | false → UI warns "no active service BOM" (D-3) |

400 messages: "Invoicing policy is only applicable to service products", "Billing model is only applicable to service products", "Estimated duration must be a positive number", "Service BOM is only applicable to service products", "Subcontract flag is only applicable to service products", "Hourly rate (selling price of the default variant) is required for Time & Material services".

## 2. BOM lines (existing BOM endpoints, extended) — Material

Line DTOs (create/update/read) gain `sourceType` (`"STOCK"` default | `"SUBCONTRACT"` | `"INTERNAL_LABOR"`) and `subcontractSupplierUuid` (uuid | null), read adds `subcontractSupplierName`.
A BOM may now be created for a product with `productType = SERVICE` and `hasServiceBom = true`. 400 messages per SVC-BOM-01..05 ("This product does not have service BOM enabled", "Subcontract supplier is required for subcontracted BOM lines", "Supplier reference is only valid for subcontracted lines", "Subcontracted line material must be a service-type product", "Internal labor lines must use hours (HR) as unit of measure").

## 3. Service orders — `api/service-orders` (feature MODULE_SERVICES)

| # | Method & route | Permission | Body / query | Result |
|---|---|---|---|---|
| 1 | `POST /api/service-orders` | SERVICE_ORDER_CREATE | `CreateServiceOrderRequest` | `uuid` |
| 2 | `GET /api/service-orders` | SERVICE_ORDER_VIEW | `?status=A,B&customerUuid&assignedUserId&fromDate&toDate&priority&search&page=1&pageSize=25` | `PaginatedResponse<ServiceOrderListItem>` (sorted scheduledDate asc, priority desc) |
| 3 | `GET /api/service-orders/{uuid}` | SERVICE_ORDER_VIEW | — | `ServiceOrderDetail` |
| 4 | `PUT /api/service-orders/{uuid}` | SERVICE_ORDER_EDIT | `UpdateServiceOrderRequest` (+ `rowVersion`) | detail |
| 5 | `POST …/{uuid}/plan` | SERVICE_ORDER_EDIT | — | detail |
| 6 | `POST …/{uuid}/start` | SERVICE_ORDER_EDIT | — | detail |
| 7 | `POST …/{uuid}/complete` | SERVICE_ORDER_COMPLETE | `CompleteServiceOrderRequest` | detail |
| 8 | `POST …/{uuid}/cancel` | SERVICE_ORDER_CANCEL | `{ reason }` (required) | detail |
| 8a | `POST …/{uuid}/close` | SERVICE_ORDER_EDIT | — | detail |
| 9 | `GET …/{uuid}/materials` | SERVICE_ORDER_VIEW | — | `ServiceMaterial[]` |
| 10 | `POST …/{uuid}/materials` | SERVICE_ORDER_EDIT | `AddAdhocMaterialRequest` | detail |
| 11 | `DELETE …/{uuid}/materials/{smrUuid}` | SERVICE_ORDER_EDIT | — | detail |
| 11a | `POST …/{uuid}/materials/allocate` | SERVICE_ORDER_EDIT | — | detail ("Reserve all available": re-runs allocation for its STOCK demands) |
| 12 | `GET …/{uuid}/ledger` | SERVICE_ORDER_VIEW | — | `ServiceLedger` |
| 13 | `GET /api/service-orders/dashboard` | SERVICE_ORDER_VIEW | — | `ServiceDashboard` |

### Requests
```ts
CreateServiceOrderRequest { serviceProductUuid: uuid; serviceVariantUuid?: uuid; customerUuid: uuid; quantity: number;
  warehouseUuid: uuid; assignedUserId?: number; assignedRoleId?: number; scheduledDate?: 'yyyy-MM-dd'; scheduledTime?: 'HH:mm';
  estimatedHours?: number; priority?: 0|1|2|3; notes?: string }
UpdateServiceOrderRequest { customerUuid; quantity; warehouseUuid; assignedUserId?; assignedRoleId?; scheduledDate?; scheduledTime?;
  estimatedHours?; priority; notes?; rowVersion: string /* base64 */ }   // DRAFT/PLANNED: all; later: notes only (SVC-12)
CompleteServiceOrderRequest { consumedMaterials: { smrUuid: uuid; consumedQuantity: number }[]; actualHours?: number;
  completionNotes?: string; customerSignature: boolean }
AddAdhocMaterialRequest { variantUuid: uuid; quantity: number; notes?: string }   // uom from the variant's product
```

### Results
```ts
ServiceOrderListItem { uuid; serviceNumber; serviceProductName; serviceVariantName?; customerUuid; customerName;
  scheduledDate?; scheduledTime?; assignedUserId?; assignedUserName?; status; priority; materialReadiness; quantity }
ServiceOrderDetail extends ServiceOrderListItem {
  serviceProductUuid; serviceVariantUuid; warehouseUuid; warehouseName; assignedRoleId?; assignedRoleName?;
  bomId?: number; bomNumber?: string; bomVersion?: number; estimatedHours?; actualHours?; actualStartDate?; actualEndDate?;
  sourceType: 'MANUAL'|'SALES_ORDER'; sourceUuid?; sourceLineUuid?; sourceReference?  /* SO number */;
  invoicingPolicy; billingModel; completionNotes?; customerSignature; notes?; traceId; rowVersion;
  createdAt; updatedAt; materials: ServiceMaterial[]; ledger: ServiceLedgerEntry[];
  allowedActions: ('EDIT'|'PLAN'|'START'|'ADD_MATERIAL'|'COMPLETE'|'CLOSE'|'CANCEL')[]   // status-based; UI also checks permissions
}
ServiceMaterial { uuid; productUuid; variantUuid; productName; variantName?; sku?; sourceType: 'STOCK'|'SUBCONTRACT'|'INTERNAL_LABOR';
  requiredQuantity; netQuantity; scrapAllowance; reservedQuantity; issuedQuantity; consumedQuantity; returnedQuantity;
  shortageQuantity; availableQuantity /* free stock in the warehouse now */; uom; isCritical; isAdhoc; status;
  requiredDate; addedByName?; notes?; supplyRequirementNumber?; supplyRequirementStatus?; canRemove: boolean }
ServiceLedgerEntry { id; entryType: 'DEBIT'; productUuid; variantUuid; productName; quantity /* negative = returned */; uom;
  warehouseName; sourceDocumentType: 'SERVICE_ISSUE'|'SERVICE_RETURN'; sourceDocumentNumber; movementType: 'SERVICE_ISSUE'|'SERVICE_RETURN';
  transactionDate; notes? }
ServiceLedger { entries: ServiceLedgerEntry[]; netByProduct: { variantUuid; productName; uom; netQuantity }[] }
ServiceDashboard { today: ServiceOrderListItem[]; waitingForMaterials: ServiceOrderListItem[]; mine: ServiceOrderListItem[];
  completionRate: { completedThisWeek: number; scheduledThisWeek: number; percent: number } }
```
Status codes: `DRAFT PLANNED MATERIAL_PENDING WAITING READY IN_PROGRESS COMPLETED CLOSED CANCELLED`; readiness `NOT_CHECKED PARTIAL READY SHORTAGE NOT_APPLICABLE`; SMR `PENDING PARTIALLY_RESERVED FULLY_RESERVED ISSUED CONSUMED RETURNED CANCELLED`.

### Details fixed by the frontend (backend must match)
1. `GET /api/service-orders` always sends `page`/`pageSize` (default 25); `status` is comma-separated (multi-select).
2. `fromDate`/`toDate` are `yyyy-MM-dd`, inclusive, on `scheduledDate`.
3. `scheduledTime` sent as `HH:mm` only with a date; accept `HH:mm` or `HH:mm:ss`; return `HH:mm`.
4. `PUT` after PLANNED sends the full body; the server applies only `notes` (+ `rowVersion`). In DRAFT/PLANNED a null clears (assignedUserId, assignedRoleId, scheduledDate…).
5. Every action endpoint (#4–#8a, #10, #11, #11a) returns the full `ServiceOrderDetail` (materials, ledger, allowedActions).
6. `allowedActions` contains `ADD_MATERIAL` only in IN_PROGRESS / WAITING.
7. `/materials/allocate` is allowed in PLANNED, MATERIAL_PENDING, WAITING, READY and IN_PROGRESS (A36-P5-10 QA: an allocation run from elsewhere can resume a WAITING job with its late material only reserved; in IN_PROGRESS/WAITING the call issues what is held).
8. `/complete` receives every SMR with `issuedQuantity > 0` (not-issued SMRs may be omitted ⇒ treated as consumed 0).
9. Cancelled SMRs are returned with `status = CANCELLED` (UI hides them).
10. Ad-hoc materials may be any active non-SERVICE product's variant.
11. `assignedRoleId` comes from `GET /auth/roles` ids.
12. A sale order line may have several service orders; return them in creation order.

## 4. Sale orders (existing detail, extended) — Demand

`GET /api/sale-orders/{uuid}`: each line gains `isService: boolean`; the order gains
`serviceOrders: { serviceOrderUuid; serviceNumber; soLineUuid; lineNumber; status; quantity }[]` (empty for drafts/without the feature).
Service lines have no route/delivery and never block confirm.

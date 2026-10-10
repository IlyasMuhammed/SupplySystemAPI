import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  ApiResponse, PaginatedResponse, AddressRequest, DeliveryListItemModel, SourceLineSelection
} from './logistics.service';
import { FulfillmentRouteSource } from './fulfillment-routes.service';
import type { LeadTimeResultModel } from './lead-time.service';

// A29 — sale orders (/api/sale-orders, Demand) and the fulfilment endpoints Logistics mounts
// under the same prefix (P6-05: create-delivery, deliveries).

export interface SaleOrderLineModel {
  uuid: string;
  variantUuid: string;
  /** Resolved from the catalogue on read; null when the variant is unknown. */
  variantSku?: string;
  variantName?: string;
  itemDescription?: string;
  unitOfMeasure?: string;
  quantity: number;
  unitPrice: number;
  discountPercent: number;
  /** The rate the line is taxed at: its tax code's rate when it was picked, else as typed. */
  taxPercent: number;
  /** The Finance tax code picked for the line; null on a line taxed by percentage alone. */
  taxCodeUuid?: string | null;
  /** The code's text as it was when picked, e.g. "GST17". */
  taxCode?: string | null;
  lineTotal: number;
  /** Credited at goods issue — what has actually left for the customer. */
  fulfilledQty: number;
  invoicedQty: number;
  /**
   * IN_STOCK | SPLIT | BACK_TO_BACK | DROP_SHIP | MAKE_TO_ORDER — decided at confirmation. A34 D-1: MAKE_TO_ORDER
   * (labelled "Make to order", R-15) reserves nothing, deficitQty = quantity, and one production order makes it all.
   */
  fulfillmentMode?: string;
  availableQtyAtConfirm?: number;
  deficitQty?: number;
  linkedPoId?: number;
  selectedSupplierId?: string;
  margin?: number;
  marginPercent?: number;
  /** OPEN | RESERVED | PARTIALLY_FULFILLED | FULFILLED | INVOICED | CANCELLED */
  status: string;
  notes?: string;
  // A32 C4 — computed by the server on the detail read, always present there. Optional in this type only so
  // fixtures written before A32 still compile.
  /** Stock the order itself holds for this line right now — what Release frees. */
  reservedQty?: number;
  /** What Reserve could still hold: quantity − fulfilled − reserved − held by the order's deliveries. */
  reservableQty?: number;
  /** GREEN | BLUE | YELLOW | RED | GREY — see DELIVERY_INDICATORS in sales-preorder.service.ts. */
  deliveryIndicator?: DeliveryIndicator;
  // A33 C3 — fulfillment route (API-CONTRACT.md §5). Live while DRAFT, the confirm-time snapshot afterwards (D-16).
  // Optional only so fixtures written before A33 compile; the server always sends them on the detail read.
  /** The line's own override while DRAFT (null = inherit); the route it was confirmed with afterwards. */
  fulfillmentRouteUuid?: string | null;
  effectiveRouteUuid?: string | null;
  effectiveRouteCode?: string | null;
  effectiveRouteName?: string | null;
  /** Step codes of the effective route, in order — e.g. ['PICK', 'GOODS_ISSUE', 'SHIP']. */
  effectiveRouteSteps?: string[];
  /** LINE_OVERRIDE ✎ | VARIANT ⓥ | ORG_DEFAULT | NONE ⚠ (DROP_SHIP lines are NONE with no blocker, D-5). */
  routeSource?: FulfillmentRouteSource;
  /** Why this line blocks confirmation; null when it doesn't. */
  routeBlocker?: ConfirmBlockerCode | null;
  /**
   * A37 RTE-03 (docs/module-registry/API-CONTRACT.md §4) — set when the configured route is unavailable (its module is
   * off) and a fallback, or nothing, applies.
   */
  routeWarning?: string | null;
  // A34 (docs/route-classification/API-CONTRACT.md §5.1, §6.1). Optional so older fixtures compile.
  /** STOCK | MANUFACTURE — live while DRAFT, the confirm-time snapshot afterwards; null for DROP_SHIP lines and lines with no route. */
  effectiveRouteCategory?: RouteCategory | null;
  /** D-21: production accepted less than the line (= the quantity on zero yield). */
  productionShortfallQty?: number | null;
  calculatedLeadTimeDays?: number | null;
  /** Date-only. */
  calculatedDeliveryDate?: string | null;
  /** UTC. */
  leadTimeCalculatedAt?: string | null;
  /** Date-only; the user's own date (D-15). */
  manualDeliveryDate?: string | null;
  /** Date-only: manual ?? calculated. */
  effectiveDeliveryDate?: string | null;
  deliveryDateSource?: DeliveryDateSource;
  // A35 (docs/multi-currency/API-CONTRACT.md §6) — in the sale base; null until the rate is locked at confirmation.
  unitPriceBase?: number | null;
  lineTotalBase?: number | null;
  /** A36 §4 — a service product line: no route, no delivery; a service order carries it instead. */
  isService?: boolean;
}

/** A36 §4 — a service order raised from a service line of the sale order. */
export interface SaleOrderServiceOrderModel {
  serviceOrderUuid: string;
  serviceNumber: string;
  soLineUuid?: string | null;
  lineNumber?: number | null;
  /** DRAFT … CLOSED, CANCELLED */
  status: string;
  quantity: number;
}

/** A34 — a route's category (API-CONTRACT.md §1). BUY / DROPSHIP are reserved and never reach a sale order. */
export type RouteCategory = 'STOCK' | 'MANUFACTURE';

/** A34 D-15 — where a line's effective delivery date comes from. */
export type DeliveryDateSource = 'MANUAL' | 'CALCULATED' | 'NONE';

/** A33 — why a DRAFT order can't be confirmed yet (API-CONTRACT.md §5). A34 D-5 adds the four make-to-order codes. */
export type ConfirmBlockerCode =
  'ROUTE_MISSING' | 'ROUTE_INACTIVE' | 'ROUTE_UNKNOWN' | 'SHIPPING_ADDRESS_REQUIRED' | 'SELF_PICKUP_DISABLED' |
  'MANUFACTURING_DISABLED' | 'NOT_MANUFACTURED' | 'BOM_MISSING' | 'PRODUCTION_WAREHOUSE_MISSING';

/** A34 D-25 — one production order raised from the sale order (make-to-order or A30 make-to-shortage). */
export interface SaleOrderProductionOrderModel {
  productionOrderUuid: string;
  productionNumber: string;
  soLineUuid?: string | null;
  /** 1-based, as A33's "Line N". */
  lineNumber?: number | null;
  /** DRAFT … COMPLETED, CANCELLED */
  status: string;
  plannedQuantity: number;
  acceptedQuantity: number;
  /** Has a route of its own: made to order for the line, and its delivery follows its completion. */
  isMakeToOrder: boolean;
  fulfillmentRouteUuid?: string | null;
  fulfillmentRouteCode?: string | null;
  fulfillmentRouteName?: string | null;
  /** The latest delivery made from it; null = "Delivery: pending". */
  deliveryOrderUuid?: string | null;
  deliveryNumber?: string | null;
  /** Only meaningful in a confirm / create result: this call made it. */
  created?: boolean;
}

/** A34 §6.3 — a make-to-order line in the delivery preview: confirming raises a production order for it, no delivery. */
export interface DeliveryPreviewProductionLineModel {
  lineUuid?: string | null;
  lineNumber: number;
  variantUuid: string;
  itemDescription?: string | null;
  quantity: number;
  routeUuid: string;
  routeCode: string;
  routeName: string;
  steps: string[];
  message: string;
}

/** A34 §6.5 — POST …/create-production-orders. Idempotent. */
export interface SaleOrderProductionCreationResultModel {
  productionOrders: SaleOrderProductionOrderModel[];
  productionCreationFailed: boolean;
  productionMessage?: string | null;
}

/** A34 §5.2 — what the calculator gave for one line, and the line as it was stored. */
export interface SaleOrderLineLeadTimeModel {
  line: SaleOrderLineModel;
  leadTime: LeadTimeResultModel;
}

/** A34 §5.2 — PUT …/lines/{lineUuid}/delivery-date. */
export interface SaleOrderLineDeliveryDateResultModel {
  line: SaleOrderLineModel;
  /** The line already has a production order planned for the earlier date; it is not rescheduled. */
  productionNotRescheduled: boolean;
  warning?: string | null;
}

export interface ConfirmBlockerModel {
  lineUuid?: string | null;
  /** 1-based, by the order the lines were added ("Line N"). Null for order-level blockers. */
  lineNumber?: number | null;
  code: ConfirmBlockerCode;
  message: string;
}

/** A32 §6.3 — computed per line, never stored. */
export type DeliveryIndicator = 'GREEN' | 'BLUE' | 'YELLOW' | 'RED' | 'GREY';

/** MANUAL | FROM_QUOTATION | PORTAL | INTER_TENANT (A32 §5.4). */
export type SaleOrderSourceType = 'MANUAL' | 'FROM_QUOTATION' | 'PORTAL' | 'INTER_TENANT';

/** A linked document in the chain inquiry → quotation → sale order. */
export interface SalesDocumentLink {
  uuid: string;
  number: string;
  status: string;
}

export interface SaleOrderModel {
  uuid: string;
  traceId: string;
  soNumber: string;
  partnerId: string;
  orderDate: string;
  expectedDeliveryDate?: string;
  currencyId: string;
  subtotal: number;
  taxAmount: number;
  discountAmount: number;
  grandTotal: number;
  /** DRAFT | CONFIRMED | PARTIALLY_FULFILLED | FULFILLED | INVOICED | CLOSED | CANCELLED */
  status: string;
  requiresShipment: boolean;
  /** SHIP | SELF_PICKUP */
  deliveryMode: string;
  shippingAddressId?: string;
  intimationDepartmentId?: number;
  notes?: string;
  createdDate: string;
  modifiedDate?: string;
  // A32 C3 — always sent by the server ('MANUAL' on every order from before A32); optional here so older fixtures compile.
  sourceType?: SaleOrderSourceType;
  /** Set when converted from a quotation — link to /pages/sales/quotations/{uuid}. */
  sourceQuotation?: SalesDocumentLink | null;
  /** Chained from the quotation's inquiry — link to /pages/sales/inquiries/{uuid}. */
  sourceInquiry?: SalesDocumentLink | null;
  customerPoReference?: string | null;
  /** Date-only, "yyyy-MM-dd…" — read it with the shared date-only helpers. */
  customerPoDate?: string | null;
  /** The CUSTOMER_PO attachment (api/attachments) linked as the customer's PO document. */
  customerPoAttachmentUuid?: string | null;
  /** Empty on list rows; the detail carries them. */
  lines: SaleOrderLineModel[];
  // A33 — optional so older fixtures compile; the detail read always sends them.
  /** False when the organization has no Logistics module (D-11): no Route column, no gate, no auto-created deliveries. */
  routesEnabled?: boolean;
  /** Empty unless DRAFT. Non-empty → "Confirm order" is disabled with these messages as its tooltip (not hidden). */
  confirmBlockers?: ConfirmBlockerModel[];
  /** A34 D-25 — every SALES_ORDER-sourced production order of the order, oldest first (readable with SALE_ORDER_VIEW). */
  productionOrders?: SaleOrderProductionOrderModel[];
  /** A34 D-17 — confirmed, but its make-to-order production orders were not (all) created yet. */
  productionCreationPending?: boolean;
  /** A36 §4 — the service orders raised for its service lines (empty for drafts / without MODULE_SERVICES). */
  serviceOrders?: SaleOrderServiceOrderModel[];
  // A35 (API-CONTRACT.md §6, D-10..D-12) — the rate is locked at CONFIRMED (re-locked, not inherited from the quotation).
  // Base fields are null until then. Optional so older fixtures compile.
  currencyCode?: string | null;
  /** Units of the sale base per 1 unit of the order currency. */
  exchangeRate?: number | null;
  baseCurrencyId?: string | null;
  baseCurrencyCode?: string | null;
  /** ISO UTC timestamp. */
  rateLockedAt?: string | null;
  /** v1.2: grandTotalBase = Σ lineTotalBase; subtotalBase = grandTotalBase − taxAmountBase + discountAmountBase. Shown as sent. */
  subtotalBase?: number | null;
  taxAmountBase?: number | null;
  discountAmountBase?: number | null;
  grandTotalBase?: number | null;
}

export interface SaleOrderFilter {
  status?: string;
  partnerId?: string;
  orderDateFrom?: string;
  orderDateTo?: string;
  /** SO number or (A32) customer PO reference, contains. */
  search?: string;
  /** A32 — MANUAL | FROM_QUOTATION | PORTAL | INTER_TENANT. */
  sourceType?: SaleOrderSourceType;
  page?: number;
  pageSize?: number;
}

/**
 * One line of a create or update. The unit price is not sent: the server resolves it from the pricing rules.
 * With a `taxCodeUuid` the server taxes the line at the code's own rate (`taxPercent` is then ignored);
 * without one, at `taxPercent` (0–100). A draft's lines are rebuilt on every update, so the code is sent
 * back each time.
 */
export interface SaleOrderLineRequest {
  variantUuid: string;
  quantity: number;
  discountPercent: number;
  taxPercent: number;
  taxCodeUuid?: string;
  /** A33 — the line's route override; null/omitted = inherit (variant, then organization default). Send it back on every update. */
  fulfillmentRouteUuid?: string | null;
  // A34 D-15 / C-14 — a draft's lines are rebuilt on every save, so these go back each time too. Dates are yyyy-MM-dd.
  manualDeliveryDate?: string | null;
  calculatedLeadTimeDays?: number | null;
  calculatedDeliveryDate?: string | null;
  leadTimeCalculatedAt?: string | null;
}

// ── A33 C3/C4 — delivery preview, confirm and cancel results (API-CONTRACT.md §5–§6) ──

/** POST /api/sale-orders/delivery-preview — the unsaved form's lines, resolved and grouped without saving anything. */
export interface SaleOrderDeliveryPreviewRequest {
  /** The order being edited, if it has been saved. */
  saleOrderUuid?: string | null;
  /** SHIP | SELF_PICKUP — picks which organization default applies (D-4). */
  deliveryMode: string;
  shippingAddressId?: string | null;
  lines: { variantUuid: string; quantity: number; fulfillmentRouteUuid?: string | null }[];
}

export interface DeliveryPreviewLineModel {
  lineUuid?: string | null;
  lineNumber: number;
  variantUuid: string;
  itemDescription?: string | null;
  quantity: number;
  fulfillmentRouteUuid?: string | null;
  effectiveRouteUuid?: string | null;
  effectiveRouteCode?: string | null;
  effectiveRouteName?: string | null;
  effectiveRouteSteps: string[];
  routeSource: FulfillmentRouteSource;
  routeBlocker?: ConfirmBlockerCode | null;
  /** A37 RTE-03 — the line's route is unavailable (module off); a fallback, or nothing, applies. */
  routeWarning?: string | null;
  /** A34 — STOCK | MANUFACTURE; a MANUFACTURE line is in productionLines, not in a group. */
  effectiveRouteCategory?: RouteCategory | null;
}

/** One delivery that confirming would create. */
export interface DeliveryPreviewGroupModel {
  routeUuid: string;
  routeCode: string;
  routeName: string;
  steps: string[];
  stepsText: string;
  requiresShipping: boolean;
  /** SHIP | SELF_PICKUP — from the route (D-4). */
  deliveryMode: string;
  /** Known only once the order holds stock (after confirm). */
  warehouseUuid?: string | null;
  warehouseName?: string | null;
  lineNumbers: number[];
}

export interface SaleOrderDeliveryPreviewModel {
  routesEnabled: boolean;
  canConfirm: boolean;
  deliveryCount: number;
  lines: DeliveryPreviewLineModel[];
  groups: DeliveryPreviewGroupModel[];
  blockers: ConfirmBlockerModel[];
  /** A34 §6.3 — make-to-order lines: a production order each; their delivery follows production. Optional for older servers. */
  productionLines?: DeliveryPreviewProductionLineModel[];
}

/** A delivery created for the order (by confirm or by the recovery button). */
export interface CreatedSaleOrderDeliveryModel {
  deliveryUuid: string;
  deliveryNumber: string;
  routeUuid: string;
  routeCode: string;
  /** SHIP | SELF_PICKUP */
  deliveryMode: string;
  shipFromWarehouseUuid?: string | null;
  lineCount: number;
}

export interface SkippedSaleOrderLineModel {
  soLineUuid: string;
  reason: string;
}

/** POST /api/sale-orders/{id}/confirm — what confirming did. */
export interface SaleOrderConfirmResultModel {
  status: string;
  deliveries: CreatedSaleOrderDeliveryModel[];
  skippedLines: SkippedSaleOrderLineModel[];
  /** The order is confirmed but its deliveries were not all created: show deliveryMessage and the "Create deliveries" button. */
  deliveryCreationFailed: boolean;
  deliveryMessage?: string | null;
  // A34 §6.4 — optional for older servers.
  /** Created by this confirm (created = true) or already there. */
  productionOrders?: SaleOrderProductionOrderModel[];
  /** True → show productionMessage and "Create production orders". */
  productionCreationFailed?: boolean;
  productionMessage?: string | null;
}

/** POST /api/sale-orders/{id}/create-deliveries — the recovery button (D-12). Idempotent. */
export interface SaleOrderDeliveryCreationResultModel {
  created: CreatedSaleOrderDeliveryModel[];
  skipped: SkippedSaleOrderLineModel[];
}

export interface SaleOrderDeliveryRefModel {
  deliveryUuid: string;
  deliveryNumber: string;
  status: string;
}

/** POST /api/sale-orders/{id}/cancel — deliveries not yet goods-issued are cancelled; issued ones stay (D-15). */
export interface SaleOrderCancelResultModel {
  cancelledDeliveries: SaleOrderDeliveryRefModel[];
  /** Already goods-issued: left as they are and need a manual reversal. */
  issuedDeliveries: SaleOrderDeliveryRefModel[];
  // A34 D-22 — optional for older servers.
  /** Were DRAFT / PLANNED / MATERIAL_PENDING / READY with nothing issued: cancelled with the order. */
  cancelledProductionOrders?: SaleOrderProductionOrderModel[];
  /** IN_PROGRESS or later, or material issued: kept running, need attention. */
  runningProductionOrders?: SaleOrderProductionOrderModel[];
  cancelledAllocationDemands?: number;
}

/** POST /api/sale-orders. The order is created as a DRAFT. */
export interface CreateSaleOrderRequest {
  partnerId: string;
  orderDate?: string;
  expectedDeliveryDate?: string;
  /** The organization's base currency when omitted. */
  currencyId?: string;
  /** SHIP | SELF_PICKUP */
  deliveryMode: string;
  /** Required for SHIP; a logistics address uuid. */
  shippingAddressId?: string;
  intimationDepartmentId?: number;
  notes?: string;
  /**
   * A32 — only 'MANUAL' (the default) is accepted here. An order from a quotation is made with
   * SalesPreorderService.convertQuotationToOrder; PORTAL / INTER_TENANT are not built yet.
   */
  sourceType?: 'MANUAL';
  /** Free text, max 50. A duplicate in the organization is a warning (see checkCustomerPo), never a refusal. */
  customerPoReference?: string;
  /** Date-only "yyyy-MM-dd" (shared date-only helpers, no UTC shift). */
  customerPoDate?: string;
  lines: SaleOrderLineRequest[];
}

/**
 * PUT /api/sale-orders/{id}, a DRAFT only. The customer cannot be changed, and every field here replaces
 * what the order has: an omitted currency falls back to the organization's, and omitted lines are gone.
 */
export interface UpdateSaleOrderRequest {
  expectedDeliveryDate?: string;
  currencyId?: string;
  deliveryMode: string;
  shippingAddressId?: string;
  intimationDepartmentId?: number;
  notes?: string;
  /** A32 — editable here while DRAFT; after that through updateCustomerPo. */
  customerPoReference?: string;
  customerPoDate?: string;
  lines: SaleOrderLineRequest[];
}

/**
 * A32 PD-05 — PUT /api/sale-orders/{id}/customer-po, any status but CANCELLED / CLOSED. Replaces all three.
 * Upload the PO file first through AttachmentService with interfaceCode 'CUSTOMER_PO' and documentId = the
 * order's uuid, then send its uuid here; null clears the link (the file stays in the order's attachments).
 */
export interface UpdateSaleOrderCustomerPoRequest {
  customerPoReference?: string | null;
  customerPoDate?: string | null;
  customerPoAttachmentUuid?: string | null;
}

/** A32 BR-C3-05 — another order of this organization already carrying the same customer PO reference. */
export interface CustomerPoDuplicateModel {
  uuid: string;
  soNumber: string;
  partnerId: string;
  status: string;
  orderDate: string;
}

/** A32 — what one reserve / release call did. */
export type SaleOrderReservationOutcome =
  'RESERVED' | 'PARTIAL' | 'NEEDS_CONFIRMATION' | 'NONE_AVAILABLE' | 'RELEASED' | 'SKIPPED';

/** A32 PE-06 — POST /api/sale-orders/{id}/lines/{lineId}/reserve. */
export interface ReserveSaleOrderLineRequest {
  /** The line's whole reservableQty when omitted. */
  quantity?: number;
  /**
   * false (default): when less is free than asked, nothing is held and the outcome is NEEDS_CONFIRMATION —
   * show "Available {availableQty}, required {requestedQty}. Reserve partial?" and retry with true.
   */
  allowPartial?: boolean;
  /** The warehouse with the most free stock when omitted. */
  warehouseUuid?: string;
}

/** A32 PE-06 — POST /api/sale-orders/{id}/lines/{lineId}/release. Omit quantity to free everything held. */
export interface ReleaseSaleOrderLineRequest {
  quantity?: number;
  reason?: string;
}

/** What one reserve / release did, and where the line stands afterwards. */
export interface SaleOrderLineReservationModel {
  lineUuid: string;
  variantUuid: string;
  outcome: SaleOrderReservationOutcome;
  /** What the call asked to hold (reserve) or free (release). */
  requestedQty: number;
  /** What it actually held or freed. */
  changedQty: number;
  /** Free stock in the chosen warehouse when the call ran (reserve). */
  availableQty: number;
  warehouseUuid?: string | null;
  warehouseName?: string | null;
  reservedQty: number;
  reservableQty: number;
  deliveryIndicator: DeliveryIndicator;
  lineStatus: string;
  /** Why a line was SKIPPED / not covered, in words. */
  message?: string | null;
}

/** A32 PE-06 — POST /api/sale-orders/{id}/reserve-all. allowPartial defaults to true. */
export interface SaleOrderReserveAllModel {
  lines: SaleOrderLineReservationModel[];
  reservedLineCount: number;
  partialLineCount: number;
  unchangedLineCount: number;
}

/** What confirming the order would find for one line right now. Reserves nothing. */
export interface SaleOrderLineAvailabilityModel {
  variantUuid: string;
  orderedQty: number;
  availableQty: number;
  deficitQty: number;
  warehouseUuid?: string;
  warehouseName?: string;
}

/**
 * POST /api/sale-orders/{id}/create-delivery. Every field is optional; an empty body delivers
 * everything outstanding in the order's own mode. `lines[].sourceLineUuid` is the sale order
 * line's uuid.
 */
export interface CreateSaleOrderDeliveryRequest {
  deliveryMode?: string;
  shipFromWarehouseUuid?: string;
  shipFromAddress?: AddressRequest;
  shipToAddress?: AddressRequest;
  requestedDate?: string;
  promisedDate?: string;
  priority?: string;
  incoterm?: string;
  notes?: string;
  lines?: SourceLineSelection[];
}

/** What a new order starts as, from the organization's sale order settings (§8.1). */
export interface SaleOrderDefaultsModel {
  /** SHIP | SELF_PICKUP */
  deliveryMode: string;
  /** False when every order has to be shipped. */
  selfPickupEnabled: boolean;
}

@Injectable({ providedIn: 'root' })
export class SaleOrderService {
  private readonly baseUrl = `${environment.apiUrl}/sale-orders`;

  constructor(private http: HttpClient) {}

  getDefaults(): Observable<ApiResponse<SaleOrderDefaultsModel>> {
    return this.http.get<ApiResponse<SaleOrderDefaultsModel>>(`${this.baseUrl}/defaults`);
  }

  getSaleOrders(filter: SaleOrderFilter = {}): Observable<ApiResponse<PaginatedResponse<SaleOrderModel>>> {
    let params = new HttpParams();
    if (filter.status)        params = params.set('status',        filter.status);
    if (filter.partnerId)     params = params.set('partnerId',     filter.partnerId);
    if (filter.orderDateFrom) params = params.set('orderDateFrom', filter.orderDateFrom);
    if (filter.orderDateTo)   params = params.set('orderDateTo',   filter.orderDateTo);
    if (filter.search)        params = params.set('search',        filter.search);
    if (filter.sourceType)    params = params.set('sourceType',    filter.sourceType);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<SaleOrderModel>>>(this.baseUrl, { params });
  }

  getSaleOrderById(uuid: string): Observable<ApiResponse<SaleOrderModel>> {
    return this.http.get<ApiResponse<SaleOrderModel>>(`${this.baseUrl}/${uuid}`);
  }

  /** Creates a DRAFT order. Returns its uuid. */
  createSaleOrder(req: CreateSaleOrderRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(this.baseUrl, req);
  }

  /** Replaces a DRAFT order's details and lines. */
  updateSaleOrder(uuid: string, req: UpdateSaleOrderRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.baseUrl}/${uuid}`, req);
  }

  /**
   * DRAFT to CONFIRMED: reserves what stock there is and raises purchase orders for the rest. A33: 400 with one
   * line per blocker when a line has no usable route; on success, the deliveries created per route (D-1).
   */
  confirmSaleOrder(uuid: string): Observable<ApiResponse<SaleOrderConfirmResultModel>> {
    return this.http.post<ApiResponse<SaleOrderConfirmResultModel>>(`${this.baseUrl}/${uuid}/confirm`, {});
  }

  /**
   * Cancels the order, releasing its reservations and cancelling its draft purchase orders. A33: also cancels its
   * deliveries not yet goods-issued and reports the issued ones.
   */
  cancelSaleOrder(uuid: string, reason?: string): Observable<ApiResponse<SaleOrderCancelResultModel>> {
    return this.http.post<ApiResponse<SaleOrderCancelResultModel>>(`${this.baseUrl}/${uuid}/cancel`, { reason: reason || null });
  }

  /**
   * A33 — sets (or, with null, clears) one DRAFT line's route override without re-pricing or rebuilding the order.
   * SALE_ORDER_EDIT. Returns the line with its effective route, source and blocker recomputed.
   */
  setLineFulfillmentRoute(uuid: string, lineUuid: string, fulfillmentRouteUuid: string | null): Observable<ApiResponse<SaleOrderLineModel>> {
    return this.http.put<ApiResponse<SaleOrderLineModel>>(
      `${this.baseUrl}/${uuid}/lines/${lineUuid}/fulfillment-route`, { fulfillmentRouteUuid });
  }

  /**
   * A34 §5.2 — calculates one DRAFT line's lead time with its variant, quantity and effective route, and stores the
   * Calculated* fields (the manual date is untouched). SALE_ORDER_EDIT.
   */
  calculateLineLeadTime(uuid: string, lineUuid: string): Observable<ApiResponse<SaleOrderLineLeadTimeModel>> {
    return this.http.post<ApiResponse<SaleOrderLineLeadTimeModel>>(`${this.baseUrl}/${uuid}/lines/${lineUuid}/lead-time`, {});
  }

  /**
   * A34 §5.2 — sets (yyyy-MM-dd) or clears (null) one line's manual delivery date on a DRAFT, CONFIRMED or
   * PARTIALLY_FULFILLED order, without re-pricing. SALE_ORDER_EDIT. A line's production order is not rescheduled.
   */
  setLineDeliveryDate(uuid: string, lineUuid: string, manualDeliveryDate: string | null): Observable<ApiResponse<SaleOrderLineDeliveryDateResultModel>> {
    return this.http.put<ApiResponse<SaleOrderLineDeliveryDateResultModel>>(
      `${this.baseUrl}/${uuid}/lines/${lineUuid}/delivery-date`, { manualDeliveryDate });
  }

  /** A34 §6.5 — creates the make-to-order production orders a confirmed order is missing. SALE_ORDER_CONFIRM or PROD_CREATE. Idempotent. */
  createProductionOrders(uuid: string): Observable<ApiResponse<SaleOrderProductionCreationResultModel>> {
    return this.http.post<ApiResponse<SaleOrderProductionCreationResultModel>>(`${this.baseUrl}/${uuid}/create-production-orders`, {});
  }

  /** A33 — how a saved order's lines would be split into deliveries on confirm. SALE_ORDER_VIEW. Persists nothing. */
  getDeliveryPreview(uuid: string): Observable<ApiResponse<SaleOrderDeliveryPreviewModel>> {
    return this.http.get<ApiResponse<SaleOrderDeliveryPreviewModel>>(`${this.baseUrl}/${uuid}/delivery-preview`);
  }

  /**
   * A33 — the same for the unsaved form (drives its Route column, preview panel and confirm blockers).
   * SALE_ORDER_CREATE / SALE_ORDER_EDIT / SALE_ORDER_VIEW. Persists nothing.
   */
  previewDeliveries(req: SaleOrderDeliveryPreviewRequest): Observable<ApiResponse<SaleOrderDeliveryPreviewModel>> {
    return this.http.post<ApiResponse<SaleOrderDeliveryPreviewModel>>(`${this.baseUrl}/delivery-preview`, req);
  }

  /** A33 D-12 — creates whatever deliveries a confirmed order is missing, one per route. DELIVERY_CREATE. Idempotent. */
  createDeliveries(uuid: string): Observable<ApiResponse<SaleOrderDeliveryCreationResultModel>> {
    return this.http.post<ApiResponse<SaleOrderDeliveryCreationResultModel>>(`${this.baseUrl}/${uuid}/create-deliveries`, {});
  }

  /** A preview of what confirming would find, line by line. Changes nothing. */
  getAvailability(uuid: string): Observable<ApiResponse<SaleOrderLineAvailabilityModel[]>> {
    return this.http.get<ApiResponse<SaleOrderLineAvailabilityModel[]>>(`${this.baseUrl}/${uuid}/availability`);
  }

  /** Every delivery raised for the order, oldest first. 404 when the order does not exist. */
  getDeliveries(uuid: string): Observable<ApiResponse<DeliveryListItemModel[]>> {
    return this.http.get<ApiResponse<DeliveryListItemModel[]>>(`${this.baseUrl}/${uuid}/deliveries`);
  }

  /** Raises a delivery for the order. Returns the new delivery's uuid. */
  createDelivery(uuid: string, req: CreateSaleOrderDeliveryRequest = {}): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.baseUrl}/${uuid}/create-delivery`, req);
  }

  // ── A32 C3 / C4 ──────────────────────────────────────────────────────────────

  /** PD-05 — set / replace the customer PO reference, date and linked CUSTOMER_PO file. SALE_ORDER_EDIT. */
  updateCustomerPo(uuid: string, req: UpdateSaleOrderCustomerPoRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.baseUrl}/${uuid}/customer-po`, req);
  }

  /**
   * BR-C3-05 — other orders of this organization with the same customer PO reference (a warning to show, never a
   * block). Pass excludeUuid when editing an existing order. SALE_ORDER_VIEW / CREATE / EDIT.
   */
  checkCustomerPo(reference: string, excludeUuid?: string): Observable<ApiResponse<CustomerPoDuplicateModel[]>> {
    let params = new HttpParams().set('reference', reference);
    if (excludeUuid) params = params.set('excludeUuid', excludeUuid);
    return this.http.get<ApiResponse<CustomerPoDuplicateModel[]>>(`${this.baseUrl}/customer-po-check`, { params });
  }

  /** PE-06 — hold stock for one line. SALE_ORDER_RESERVE. Always 200 with an outcome unless the line cannot be reserved (400). */
  reserveLine(uuid: string, lineUuid: string, req: ReserveSaleOrderLineRequest = {}): Observable<ApiResponse<SaleOrderLineReservationModel>> {
    return this.http.post<ApiResponse<SaleOrderLineReservationModel>>(`${this.baseUrl}/${uuid}/lines/${lineUuid}/reserve`, req);
  }

  /** PE-06 — give back stock held for one line. SALE_ORDER_RELEASE_RESERVATION. */
  releaseLine(uuid: string, lineUuid: string, req: ReleaseSaleOrderLineRequest = {}): Observable<ApiResponse<SaleOrderLineReservationModel>> {
    return this.http.post<ApiResponse<SaleOrderLineReservationModel>>(`${this.baseUrl}/${uuid}/lines/${lineUuid}/release`, req);
  }

  /** PE-06 — hold stock for every open line. SALE_ORDER_RESERVE. */
  reserveAll(uuid: string, allowPartial = true): Observable<ApiResponse<SaleOrderReserveAllModel>> {
    return this.http.post<ApiResponse<SaleOrderReserveAllModel>>(`${this.baseUrl}/${uuid}/reserve-all`, { allowPartial });
  }
}

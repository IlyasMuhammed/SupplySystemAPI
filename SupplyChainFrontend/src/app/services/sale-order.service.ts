import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  ApiResponse, PaginatedResponse, AddressRequest, DeliveryListItemModel, SourceLineSelection
} from './logistics.service';

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
  taxPercent: number;
  lineTotal: number;
  /** Credited at goods issue — what has actually left for the customer. */
  fulfilledQty: number;
  invoicedQty: number;
  /** IN_STOCK | SPLIT | BACK_TO_BACK | DROP_SHIP — decided at confirmation. */
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
  /** Empty on list rows; the detail carries them. */
  lines: SaleOrderLineModel[];
}

export interface SaleOrderFilter {
  status?: string;
  partnerId?: string;
  orderDateFrom?: string;
  orderDateTo?: string;
  /** SO number, contains. */
  search?: string;
  page?: number;
  pageSize?: number;
}

/** One line of a create or update. The unit price is not sent: the server resolves it from the pricing rules. */
export interface SaleOrderLineRequest {
  variantUuid: string;
  quantity: number;
  discountPercent: number;
  taxPercent: number;
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
  lines: SaleOrderLineRequest[];
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

  /** DRAFT to CONFIRMED: reserves what stock there is and raises purchase orders for the rest. */
  confirmSaleOrder(uuid: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.baseUrl}/${uuid}/confirm`, {});
  }

  /** Cancels the order, releasing its reservations and cancelling its draft purchase orders. */
  cancelSaleOrder(uuid: string, reason?: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.baseUrl}/${uuid}/cancel`, { reason: reason || null });
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
}

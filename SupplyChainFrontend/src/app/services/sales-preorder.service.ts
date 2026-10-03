import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ApiResponse, PaginatedResponse } from './logistics.service';
import type { DeliveryIndicator, SalesDocumentLink } from './sale-order.service';

// A32 — Sales Pre-Order Pipeline: sale inquiries (/api/sale-inquiries), sale quotations (/api/sale-quotations)
// and rejection reasons (/api/rejection-reasons), all Demand. The contract is
// docs/sales-preorder/API-CONTRACT.md — keep the two in step. Sale order additions (source, customer PO,
// reserve / release) live in sale-order.service.ts.
//
// Every id is a uuid string; users are numbers. Date-only fields ("yyyy-MM-dd") are written and read with the
// shared date-only helpers — never toISOString() on a picked date (see finance/sap-dates.consistency.spec.ts).
// Attachments: AttachmentService with interfaceCode 'SALE_INQUIRY' / 'SALE_QUOTATION' and documentId = the uuid.

export type { SalesDocumentLink, DeliveryIndicator };

// ── Codes ────────────────────────────────────────────────────────────────────

export type SaleInquiryStatus = 'RECEIVED' | 'UNDER_REVIEW' | 'REVIEW_COMPLETE' | 'QUOTED' | 'DECLINED';
export type SaleInquiryLineStatus = 'PENDING' | 'CAN_SUPPLY' | 'PARTIAL' | 'CANNOT_SUPPLY' | 'UNDER_REVIEW';
export type SaleQuotationStatus = 'DRAFT' | 'SENT' | 'ACCEPTED' | 'REJECTED' | 'EXPIRED' | 'CONVERTED';
export type SaleQuotationLineType = 'NORMAL' | 'ALTERNATIVE' | 'REJECTED';
export type CustomerResponse = 'PENDING' | 'ACCEPTED' | 'REJECTED' | 'COUNTER';
/** Server-computed on SaleQuotationModel.allowedActions, for the buttons. */
export type SaleQuotationAction = 'SEND' | 'RECORD_RESPONSE' | 'ACCEPT' | 'REJECT' | 'CONVERT' | 'COPY';

export const SALE_INQUIRY_STATUSES: SaleInquiryStatus[] = ['RECEIVED', 'UNDER_REVIEW', 'REVIEW_COMPLETE', 'QUOTED', 'DECLINED'];
export const SALE_INQUIRY_LINE_STATUSES: SaleInquiryLineStatus[] = ['PENDING', 'CAN_SUPPLY', 'PARTIAL', 'CANNOT_SUPPLY', 'UNDER_REVIEW'];
export const SALE_QUOTATION_STATUSES: SaleQuotationStatus[] = ['DRAFT', 'SENT', 'ACCEPTED', 'REJECTED', 'EXPIRED', 'CONVERTED'];
export const SALE_QUOTATION_LINE_TYPES: SaleQuotationLineType[] = ['NORMAL', 'ALTERNATIVE', 'REJECTED'];
export const CUSTOMER_RESPONSES: CustomerResponse[] = ['PENDING', 'ACCEPTED', 'REJECTED', 'COUNTER'];
export const DELIVERY_INDICATORS: DeliveryIndicator[] = ['GREEN', 'BLUE', 'YELLOW', 'RED', 'GREY'];

/** Attachment interface codes (WorkflowEngine AttachmentAccessPolicy). CUSTOMER_PO / SALE_ORDER take the order's uuid. */
export const SALES_ATTACHMENT_CODES = {
  inquiry:    'SALE_INQUIRY',
  quotation:  'SALE_QUOTATION',
  customerPo: 'CUSTOMER_PO',
  saleOrder:  'SALE_ORDER',
} as const;

// ── Sale inquiries ───────────────────────────────────────────────────────────

export interface SaleInquiryFilter {
  status?: SaleInquiryStatus;
  partnerId?: string;
  /** Date-only. */
  receivedFrom?: string;
  receivedTo?: string;
  assignedToUserId?: number;
  /** Inquiry number or customer reference, contains. */
  search?: string;
  page?: number;
  pageSize?: number;
}

export interface SaleInquiryListItem {
  uuid: string;
  inquiryNumber: string;
  partnerId: string;
  partnerName?: string | null;
  customerReference?: string | null;
  receivedDate: string;
  responseDeadline?: string | null;
  status: SaleInquiryStatus;
  assignedToUserId?: number | null;
  assignedToUserName?: string | null;
  lineCount: number;
  pendingLineCount: number;
  createdDate: string;
}

export interface SaleInquiryLine {
  uuid: string;
  lineNumber: number;
  productUuid?: string | null;
  variantUuid?: string | null;
  variantSku?: string | null;
  variantName?: string | null;
  productDescription: string;
  requestedQuantity: number;
  requestedUomCode?: string | null;
  requestedDeliveryDate?: string | null;
  lineStatus: SaleInquiryLineStatus;
  canSupplyQuantity?: number | null;
  estimatedDeliveryDate?: string | null;
  rejectionReasonUuid?: string | null;
  rejectionReasonCode?: string | null;
  rejectionReasonDescription?: string | null;
  rejectionNotes?: string | null;
  alternativeProductUuid?: string | null;
  alternativeVariantUuid?: string | null;
  alternativeVariantSku?: string | null;
  alternativeVariantName?: string | null;
  alternativeNotes?: string | null;
  requiresProcurement: boolean;
  procurementLeadDays?: number | null;
  reviewedByUserId?: number | null;
  reviewedByUserName?: string | null;
  reviewedAt?: string | null;
  notes?: string | null;
}

export interface SaleInquiry {
  uuid: string;
  traceId: string;
  inquiryNumber: string;
  partnerId: string;
  partnerName?: string | null;
  customerReference?: string | null;
  customerReferenceDate?: string | null;
  status: SaleInquiryStatus;
  receivedDate: string;
  responseDeadline?: string | null;
  assignedToUserId?: number | null;
  assignedToUserName?: string | null;
  notes?: string | null;
  declineReason?: string | null;
  createdBy: number;
  createdDate: string;
  modifiedDate?: string | null;
  /** Where PATCH /status may go next — drive the buttons from this. */
  allowedNextStatuses: SaleInquiryStatus[];
  /** False once QUOTED or DECLINED: no header or line changes. */
  isEditable: boolean;
  lines: SaleInquiryLine[];
  /** Quotations created from this inquiry. */
  quotations: SalesDocumentLink[];
}

/** What the customer asked for (POST …/lines, and lines[] of a create). */
export interface SaleInquiryLineRequest {
  productUuid?: string | null;
  variantUuid?: string | null;
  productDescription: string;
  requestedQuantity: number;
  requestedUomCode?: string | null;
  requestedDeliveryDate?: string | null;
  notes?: string | null;
}

/**
 * PUT …/lines/{lineUuid}: the request fields plus the evaluation. CAN_SUPPLY needs estimatedDeliveryDate;
 * PARTIAL needs 0 < canSupplyQuantity < requestedQuantity and estimatedDeliveryDate; CANNOT_SUPPLY needs
 * rejectionReasonUuid. Fields that do not apply to the status are cleared by the server.
 */
export interface UpdateSaleInquiryLineRequest extends SaleInquiryLineRequest {
  lineStatus: SaleInquiryLineStatus;
  canSupplyQuantity?: number | null;
  estimatedDeliveryDate?: string | null;
  rejectionReasonUuid?: string | null;
  rejectionNotes?: string | null;
  alternativeProductUuid?: string | null;
  alternativeVariantUuid?: string | null;
  alternativeNotes?: string | null;
  requiresProcurement: boolean;
  procurementLeadDays?: number | null;
}

export interface CreateSaleInquiryRequest {
  /** A partner flagged as a customer. */
  partnerId: string;
  customerReference?: string | null;
  customerReferenceDate?: string | null;
  /** Today when omitted. */
  receivedDate?: string | null;
  responseDeadline?: string | null;
  assignedToUserId?: number | null;
  notes?: string | null;
  lines?: SaleInquiryLineRequest[];
}

/** Header only; replaces every field. The customer cannot change. */
export interface UpdateSaleInquiryRequest {
  customerReference?: string | null;
  customerReferenceDate?: string | null;
  receivedDate: string;
  responseDeadline?: string | null;
  assignedToUserId?: number | null;
  notes?: string | null;
}

export interface ChangeSaleInquiryStatusRequest {
  /** QUOTED is set by create-quotation only. */
  status: 'UNDER_REVIEW' | 'REVIEW_COMPLETE' | 'DECLINED';
  /** Required for DECLINED. */
  reason?: string | null;
}

// ── Sale quotations ──────────────────────────────────────────────────────────

export interface SaleQuotationFilter {
  status?: SaleQuotationStatus;
  partnerId?: string;
  sourceInquiryUuid?: string;
  /** valid_to range, date-only. */
  validToFrom?: string;
  validToTo?: string;
  /** Quotation number or customer reference, contains. */
  search?: string;
  page?: number;
  pageSize?: number;
}

export interface SaleQuotationListItem {
  uuid: string;
  quotationNumber: string;
  partnerId: string;
  partnerName?: string | null;
  customerReference?: string | null;
  sourceInquiryUuid?: string | null;
  sourceInquiryNumber?: string | null;
  currencyId: string;
  currencyCode?: string | null;
  validFrom: string;
  validTo: string;
  status: SaleQuotationStatus;
  grandTotal: number;
  lineCount: number;
  sentAt?: string | null;
  createdDate: string;
}

export interface SaleQuotationLine {
  uuid: string;
  lineNumber: number;
  sourceInquiryLineUuid?: string | null;
  variantUuid?: string | null;
  variantSku?: string | null;
  variantName?: string | null;
  productDescription: string;
  quantity: number;
  uomCode?: string | null;
  unitPrice: number;
  discountPercent: number;
  taxPercent: number;
  taxCodeUuid?: string | null;
  taxCode?: string | null;
  taxAmount: number;
  lineTotal: number;
  promisedDeliveryDate?: string | null;
  lineType: SaleQuotationLineType;
  rejectionReasonUuid?: string | null;
  rejectionReasonCode?: string | null;
  rejectionReasonDescription?: string | null;
  rejectionNotes?: string | null;
  /** ALTERNATIVE only: the REJECTED line it replaces. */
  alternativeForLineUuid?: string | null;
  alternativeForLineNumber?: number | null;
  alternativeNotes?: string | null;
  customerResponse: CustomerResponse;
  customerResponseDate?: string | null;
  customerResponseNotes?: string | null;
  customerCounterPrice?: number | null;
  notes?: string | null;
}

export interface SaleQuotation {
  uuid: string;
  traceId: string;
  quotationNumber: string;
  partnerId: string;
  partnerName?: string | null;
  customerReference?: string | null;
  customerReferenceDate?: string | null;
  sourceInquiry?: SalesDocumentLink | null;
  /** The sale order it was converted to; null until CONVERTED. */
  saleOrder?: SalesDocumentLink | null;
  currencyId: string;
  currencyCode?: string | null;
  validFrom: string;
  validTo: string;
  status: SaleQuotationStatus;
  paymentTerms?: string | null;
  deliveryTerms?: string | null;
  subtotal: number;
  taxAmount: number;
  discountAmount: number;
  grandTotal: number;
  notes?: string | null;
  internalNotes?: string | null;
  sentAt?: string | null;
  sentByUserId?: number | null;
  sentByUserName?: string | null;
  createdBy: number;
  createdDate: string;
  modifiedDate?: string | null;
  /** True only in DRAFT. */
  isEditable: boolean;
  allowedActions: SaleQuotationAction[];
  lines: SaleQuotationLine[];
}

/**
 * One line of a create, POST …/lines or PUT …/lines/{lineUuid} (DRAFT only). NORMAL / ALTERNATIVE need a variant
 * and quantity > 0; REJECTED needs rejectionReasonUuid; ALTERNATIVE needs alternativeForLineUuid (existing line) or
 * alternativeForLineNumber (a line of the same request) pointing at a REJECTED line. unitPrice omitted = resolved
 * from the pricing rules; given = taken as quoted. taxCodeUuid wins over taxPercent.
 */
export interface SaleQuotationLineRequest {
  lineType?: SaleQuotationLineType;
  variantUuid?: string | null;
  productDescription?: string | null;
  quantity: number;
  uomCode?: string | null;
  unitPrice?: number | null;
  discountPercent: number;
  taxPercent: number;
  taxCodeUuid?: string | null;
  promisedDeliveryDate?: string | null;
  rejectionReasonUuid?: string | null;
  rejectionNotes?: string | null;
  alternativeForLineUuid?: string | null;
  alternativeForLineNumber?: number | null;
  alternativeNotes?: string | null;
  notes?: string | null;
}

export interface CreateSaleQuotationRequest {
  /** A partner flagged as a customer. */
  partnerId: string;
  customerReference?: string | null;
  customerReferenceDate?: string | null;
  /** The organization's base currency when omitted. */
  currencyId?: string | null;
  /** Today when omitted. */
  validFrom?: string | null;
  /** Required, on or after validFrom. */
  validTo: string;
  paymentTerms?: string | null;
  deliveryTerms?: string | null;
  notes?: string | null;
  internalNotes?: string | null;
  lines?: SaleQuotationLineRequest[];
}

/** Header only, DRAFT only; replaces every field. */
export interface UpdateSaleQuotationRequest {
  customerReference?: string | null;
  customerReferenceDate?: string | null;
  currencyId?: string | null;
  validFrom: string;
  validTo: string;
  paymentTerms?: string | null;
  deliveryTerms?: string | null;
  notes?: string | null;
  internalNotes?: string | null;
}

/** POST /api/sale-inquiries/{uuid}/create-quotation — the inquiry must be REVIEW_COMPLETE. */
export interface CreateQuotationFromInquiryRequest {
  currencyId?: string | null;
  validFrom?: string | null;
  validTo: string;
  paymentTerms?: string | null;
  deliveryTerms?: string | null;
  notes?: string | null;
  internalNotes?: string | null;
}

/** PATCH …/lines/{lineUuid}/customer-response — SENT only, not on REJECTED lines. */
export interface RecordCustomerResponseRequest {
  response: CustomerResponse;
  /** Today when omitted. */
  responseDate?: string | null;
  notes?: string | null;
  /** Required > 0 for COUNTER. */
  counterPrice?: number | null;
  /** With ACCEPTED on a COUNTER line: take the customer's counter price as the line's price. */
  acceptCounterPrice?: boolean;
}

/** POST …/convert-to-order — ACCEPTED only. The new sale order is a DRAFT with the accepted lines. */
export interface ConvertQuotationToOrderRequest {
  orderDate?: string | null;
  expectedDeliveryDate?: string | null;
  /** SHIP | SELF_PICKUP; the organization's default when omitted. */
  deliveryMode?: string | null;
  /** Required for SHIP. */
  shippingAddressId?: string | null;
  intimationDepartmentId?: number | null;
  notes?: string | null;
  customerPoReference?: string | null;
  customerPoDate?: string | null;
}

// ── Rejection reasons ────────────────────────────────────────────────────────

export interface RejectionReason {
  uuid: string;
  code: string;
  description: string;
  isActive: boolean;
  /** One of the ten seeded codes: can be renamed and deactivated, never deleted. */
  isSystem: boolean;
  displayOrder: number;
  createdDate: string;
  modifiedDate?: string | null;
}

export interface CreateRejectionReasonRequest {
  /** 1–10 letters/digits/underscore; stored upper case; unique in the organization. */
  code: string;
  description: string;
  /** Last when omitted. */
  displayOrder?: number | null;
}

/** The code never changes. */
export interface UpdateRejectionReasonRequest {
  description: string;
  displayOrder: number;
}

// ── Service ──────────────────────────────────────────────────────────────────

@Injectable({ providedIn: 'root' })
export class SalesPreorderService {
  private readonly inquiriesUrl  = `${environment.apiUrl}/sale-inquiries`;
  private readonly quotationsUrl = `${environment.apiUrl}/sale-quotations`;
  private readonly reasonsUrl    = `${environment.apiUrl}/rejection-reasons`;

  constructor(private http: HttpClient) {}

  // Inquiries — SALE_INQUIRY_VIEW to read, _CREATE to create, _EDIT for everything else.

  getInquiries(filter: SaleInquiryFilter = {}): Observable<ApiResponse<PaginatedResponse<SaleInquiryListItem>>> {
    let params = new HttpParams();
    if (filter.status)           params = params.set('status', filter.status);
    if (filter.partnerId)        params = params.set('partnerId', filter.partnerId);
    if (filter.receivedFrom)     params = params.set('receivedFrom', filter.receivedFrom);
    if (filter.receivedTo)       params = params.set('receivedTo', filter.receivedTo);
    if (filter.assignedToUserId) params = params.set('assignedToUserId', String(filter.assignedToUserId));
    if (filter.search)           params = params.set('search', filter.search);
    params = params.set('page', String(filter.page ?? 1)).set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<SaleInquiryListItem>>>(this.inquiriesUrl, { params });
  }

  getInquiry(uuid: string): Observable<ApiResponse<SaleInquiry>> {
    return this.http.get<ApiResponse<SaleInquiry>>(`${this.inquiriesUrl}/${uuid}`);
  }

  /** Returns the new inquiry's uuid. */
  createInquiry(req: CreateSaleInquiryRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(this.inquiriesUrl, req);
  }

  updateInquiry(uuid: string, req: UpdateSaleInquiryRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.inquiriesUrl}/${uuid}`, req);
  }

  /** Returns the new line's uuid. */
  addInquiryLine(uuid: string, req: SaleInquiryLineRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.inquiriesUrl}/${uuid}/lines`, req);
  }

  updateInquiryLine(uuid: string, lineUuid: string, req: UpdateSaleInquiryLineRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.inquiriesUrl}/${uuid}/lines/${lineUuid}`, req);
  }

  deleteInquiryLine(uuid: string, lineUuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.inquiriesUrl}/${uuid}/lines/${lineUuid}`);
  }

  /** Returns the inquiry as it now stands. */
  changeInquiryStatus(uuid: string, req: ChangeSaleInquiryStatusRequest): Observable<ApiResponse<SaleInquiry>> {
    return this.http.patch<ApiResponse<SaleInquiry>>(`${this.inquiriesUrl}/${uuid}/status`, req);
  }

  /** SALE_QUOTATION_CREATE. REVIEW_COMPLETE → QUOTED; returns the new quotation's uuid. */
  createQuotationFromInquiry(uuid: string, req: CreateQuotationFromInquiryRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.inquiriesUrl}/${uuid}/create-quotation`, req);
  }

  // Quotations — SALE_QUOTATION_VIEW to read, _CREATE to create/copy, _EDIT to change/respond/accept/reject,
  // _SEND to send; converting is SALE_ORDER_CREATE.

  getQuotations(filter: SaleQuotationFilter = {}): Observable<ApiResponse<PaginatedResponse<SaleQuotationListItem>>> {
    let params = new HttpParams();
    if (filter.status)            params = params.set('status', filter.status);
    if (filter.partnerId)         params = params.set('partnerId', filter.partnerId);
    if (filter.sourceInquiryUuid) params = params.set('sourceInquiryUuid', filter.sourceInquiryUuid);
    if (filter.validToFrom)       params = params.set('validToFrom', filter.validToFrom);
    if (filter.validToTo)         params = params.set('validToTo', filter.validToTo);
    if (filter.search)            params = params.set('search', filter.search);
    params = params.set('page', String(filter.page ?? 1)).set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<SaleQuotationListItem>>>(this.quotationsUrl, { params });
  }

  getQuotation(uuid: string): Observable<ApiResponse<SaleQuotation>> {
    return this.http.get<ApiResponse<SaleQuotation>>(`${this.quotationsUrl}/${uuid}`);
  }

  /** Returns the new quotation's uuid. */
  createQuotation(req: CreateSaleQuotationRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(this.quotationsUrl, req);
  }

  updateQuotation(uuid: string, req: UpdateSaleQuotationRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.quotationsUrl}/${uuid}`, req);
  }

  /** Returns the new line's uuid. */
  addQuotationLine(uuid: string, req: SaleQuotationLineRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.quotationsUrl}/${uuid}/lines`, req);
  }

  updateQuotationLine(uuid: string, lineUuid: string, req: SaleQuotationLineRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.quotationsUrl}/${uuid}/lines/${lineUuid}`, req);
  }

  deleteQuotationLine(uuid: string, lineUuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.quotationsUrl}/${uuid}/lines/${lineUuid}`);
  }

  sendQuotation(uuid: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.quotationsUrl}/${uuid}/send`, {});
  }

  recordCustomerResponse(uuid: string, lineUuid: string, req: RecordCustomerResponseRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${this.quotationsUrl}/${uuid}/lines/${lineUuid}/customer-response`, req);
  }

  acceptQuotation(uuid: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.quotationsUrl}/${uuid}/accept`, {});
  }

  rejectQuotation(uuid: string, reason?: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.quotationsUrl}/${uuid}/reject`, { reason: reason || null });
  }

  /** SALE_ORDER_CREATE. ACCEPTED → CONVERTED; returns the new sale order's uuid (open /pages/sales/orders/{uuid}). */
  convertQuotationToOrder(uuid: string, req: ConvertQuotationToOrderRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.quotationsUrl}/${uuid}/convert-to-order`, req);
  }

  /** SALE_QUOTATION_CREATE. A new DRAFT copy; returns its uuid. */
  copyQuotation(uuid: string): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.quotationsUrl}/${uuid}/copy`, {});
  }

  // Rejection reasons — reading needs any SALE_INQUIRY_* / SALE_QUOTATION_* code or the manage code;
  // every change needs SALE_REJECTION_REASON_MANAGE.

  /** Active only unless includeInactive (the admin screen); sorted by displayOrder, then code. */
  getRejectionReasons(includeInactive = false): Observable<ApiResponse<RejectionReason[]>> {
    const params = new HttpParams().set('includeInactive', String(includeInactive));
    return this.http.get<ApiResponse<RejectionReason[]>>(this.reasonsUrl, { params });
  }

  createRejectionReason(req: CreateRejectionReasonRequest): Observable<ApiResponse<RejectionReason>> {
    return this.http.post<ApiResponse<RejectionReason>>(this.reasonsUrl, req);
  }

  updateRejectionReason(uuid: string, req: UpdateRejectionReasonRequest): Observable<ApiResponse<RejectionReason>> {
    return this.http.put<ApiResponse<RejectionReason>>(`${this.reasonsUrl}/${uuid}`, req);
  }

  deactivateRejectionReason(uuid: string): Observable<ApiResponse<RejectionReason>> {
    return this.http.patch<ApiResponse<RejectionReason>>(`${this.reasonsUrl}/${uuid}/deactivate`, {});
  }

  activateRejectionReason(uuid: string): Observable<ApiResponse<RejectionReason>> {
    return this.http.patch<ApiResponse<RejectionReason>>(`${this.reasonsUrl}/${uuid}/activate`, {});
  }

  /** Custom, never-used reasons only (409 otherwise — deactivate instead). */
  deleteRejectionReason(uuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.reasonsUrl}/${uuid}`);
  }
}

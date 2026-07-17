import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

const BASE = 'https://localhost:51800/api';

// ── Shared wrappers ───────────────────────────────────────────────────────────

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

export interface PaginatedResponse<T> {
  data: T[];
  totalRecords: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

// ═══════════════════════════════════════════════════════════════════════════════
// PURCHASE REQUISITION
// ═══════════════════════════════════════════════════════════════════════════════

export interface CreatePrLineRequest {
  productId?: string;
  itemDescription: string;
  specification?: string;
  unitOfMeasure?: string;
  quantity: number;
  estimatedUnitPrice: number;
  preferredSupplierId?: string;
  requiresQuotation: boolean;
  requiredDate?: string;
  lineNotes?: string;
}

export interface CreatePrRequest {
  prTitle: string;
  department?: string;
  requestedDate: string;
  priority?: string;
  prType?: string;
  requiresQuotation?: boolean;
  justification?: string;
  budgetCode?: string;
  warehouseUuid?: string;
  notes?: string;
  lines: CreatePrLineRequest[];
}

export interface PatchPrRequest {
  prTitle?: string;
  department?: string;
  requestedDate?: string;
  priority?: string;
  prType?: string;
  requiresQuotation?: boolean;
  justification?: string;
  budgetCode?: string;
  warehouseUuid?: string;
  clearWarehouse?: boolean;
  notes?: string;
  lines?: CreatePrLineRequest[];
}

export interface PrListFilter {
  status?: string;
  department?: string;
  requesterId?: number;
  dateFrom?: string;
  dateTo?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

export interface PrListItemModel {
  uuid: string;
  prNumber: string;
  prTitle: string;
  department?: string;
  status: string;
  requestedDate: string;
  estimatedTotal: number;
  requesterId: number;
  createdDate: string;
}

export interface PrLineModel {
  uuid: string;
  lineNo: number;
  productId?: string;
  itemDescription: string;
  specification?: string;
  unitOfMeasure?: string;
  quantity: number;
  estimatedUnitPrice: number;
  lineTotal: number;
  preferredSupplierId?: string;
  requiresQuotation: boolean;
  quotationStatus?: string;
  lineStatus?: string;
  requiredDate?: string;
  lineNotes?: string;
  disbursedQty: number;
}

export interface LinkedAwardedQuotationInfo {
  quotationNumber: string;
  awardedSupplierId: string;
  awardedSupplierName: string;
}

export interface PrDetailModel {
  uuid: string;
  prNumber: string;
  prTitle: string;
  department?: string;
  requesterId: number;
  requestedDate: string;
  priority?: string;
  prType?: string;
  requiresQuotation: boolean;
  justification?: string;
  estimatedTotal: number;
  budgetCode?: string;
  warehouseUuid?: string;
  status: string;
  approvedBy?: number;
  approvedAt?: string;
  rejectionReason?: string;
  notes?: string;
  createdDate: string;
  createdBy: number;
  linkedAwardedQuotation?: LinkedAwardedQuotationInfo;
  lines: PrLineModel[];
}

export interface ConvertPrToPoRequest {
  supplierId: string;
  supplierName: string;
  deliveryDate?: string;
  notes?: string;
}

export interface PrLineVendorAssignment {
  prLineUuid: string;
  supplierId: string;
  supplierName: string;
}

export interface ConvertPrSplitRequest {
  lines: PrLineVendorAssignment[];
  deliveryDate?: string;
  notes?: string;
}

// ═══════════════════════════════════════════════════════════════════════════════
// QUOTATION / RFQ
// ═══════════════════════════════════════════════════════════════════════════════

export interface CreateQuotationLineRequest {
  sourcePrLineUuid?: string;
  sourcePoLineUuid?: string;
  productId?: string;
  itemDescription: string;
  specification?: string;
  unitOfMeasure?: string;
  quantity: number;
  requiredDate?: string;
  lineNotes?: string;
}

export interface CreateQuotationRequest {
  title: string;
  sourceType: string;
  sourceId?: string;
  dueDate?: string;
  notes?: string;
  lines: CreateQuotationLineRequest[];
}

export interface PatchQuotationRequest {
  title?: string;
  dueDate?: string;
  notes?: string;
  lines?: CreateQuotationLineRequest[];
}

export interface InviteSupplierRequest {
  supplierId: string;
  supplierName: string;
}

export interface SendQuotationRequest {
  suppliers: InviteSupplierRequest[];
}

export interface SupplierContactPairRequest {
  supplierId: string;
  supplierName: string;
  contactId: number;
  supplierEmail?: string;
  contactMobileNumber?: string;
}

export interface SendWithLinkRequest {
  suppliers: SupplierContactPairRequest[];
}

export interface GeneratedLinkModel {
  supplierId: string;
  contactId: number;
  linkUrl: string;
  expiresAt: string;
}

export interface SendWithLinkResult {
  links: GeneratedLinkModel[];
  emailWarning?: string;
  whatsAppWarning?: string;
}

export interface VendorResponseLineRequest {
  quotationLineUuid: string;
  netUnitPrice: number;
  quantity: number;
  leadTimeDays?: number;
  notes?: string;
}

export interface RecordVendorResponseRequest {
  supplierId: string;
  supplierName: string;
  responseDate?: string;
  notes?: string;
  lines: VendorResponseLineRequest[];
}

export interface AwardQuotationRequest {
  vendorResponseUuid: string;
}

export interface QuotationListFilter {
  status?: string;
  sourceType?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

export interface QuotationListItemModel {
  uuid: string;
  quotationNumber: string;
  title: string;
  sourceType: string;
  sourceId?: string;
  status: string;
  dueDate?: string;
  responseCount: number;
  createdDate: string;
}

export interface QuotationLineModel {
  uuid: string;
  lineNo: number;
  sourcePrLineUuid?: string;
  sourcePoLineUuid?: string;
  itemDescription: string;
  specification?: string;
  unitOfMeasure?: string;
  quantity: number;
  requiredDate?: string;
}

export interface QuotationInvitedSupplierModel {
  supplierId: string;
  supplierName: string;
  invitedAt: string;
}

export interface VendorResponseLineModel {
  uuid: string;
  quotationLineUuid: string;
  itemDescription: string;
  netUnitPrice: number;
  quantity: number;
  lineTotal: number;
  leadTimeDays?: number;
  notes?: string;
}

export interface VendorResponseModel {
  uuid: string;
  supplierId: string;
  supplierName: string;
  status: string;
  totalAmount: number;
  responseDate?: string;
  notes?: string;
  createdDate: string;
  lines: VendorResponseLineModel[];
}

export interface RfqAccessLinkModel {
  linkId: number;
  supplierId: string;
  supplierName: string;
  contactId: number;
  supplierEmail?: string;
  status: string;
  generatedAt: string;
  expiresAt: string;
  emailSentAt?: string;
  whatsAppSentAt?: string;
  firstOpenedAt?: string;
  consumedAt?: string;
}

export interface QuotationDetailModel {
  uuid: string;
  quotationNumber: string;
  title: string;
  sourceType: string;
  sourceId?: string;
  status: string;
  dueDate?: string;
  notes?: string;
  cancellationReason?: string;
  createdBy: number;
  createdDate: string;
  submittedResponseCount: number;
  lines: QuotationLineModel[];
  invitedSuppliers: QuotationInvitedSupplierModel[];
}

// ═══════════════════════════════════════════════════════════════════════════════
// PURCHASE ORDER
// ═══════════════════════════════════════════════════════════════════════════════

export interface CreatePoLineRequest {
  sourcePrLineUuid?: string;
  productUuid?: string;
  itemDescription: string;
  specification?: string;
  unitOfMeasure?: string;
  quantity: number;
  unitPrice: number;
  requiredDate?: string;
  lineNotes?: string;
  warehouseId?: string;
  warehouseName?: string;
}

export interface CreatePoRequest {
  supplierId: string;
  supplierName: string;
  prIds?: string[];
  lines?: CreatePoLineRequest[];
  deliveryDate?: string;
  deliveryWarehouseId?: string;
  deliveryWarehouseName?: string;
  title?: string;
  notes?: string;
  internalNotes?: string;
  budgetCode?: string;
}

export interface PatchPoRequest {
  supplierName?: string;
  supplierId?: string;
  title?: string;
  deliveryDate?: string;
  deliveryWarehouseId?: string;
  deliveryWarehouseName?: string;
  notes?: string;
  budgetCode?: string;
  lines?: CreatePoLineRequest[];
}

export interface PoListFilter {
  status?: string;
  supplierId?: string;
  dateFrom?: string;
  dateTo?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

export interface PoListItemModel {
  uuid: string;
  poNumber: string;
  title?: string;
  supplierId: string;
  supplierName: string;
  status: string;
  totalAmount: number;
  deliveryDate?: string;
  createdDate: string;
}

export interface PoSearchItemModel {
  uuid: string;
  poNumber: string;
  supplierName: string;
  status: string;
  grandTotal: number;
  currency?: string;
  qtyPending: number;
}

export interface PoLineModel {
  uuid: string;
  lineNo: number;
  sourcePrLineUuid?: string;
  productUuid?: string;
  itemDescription: string;
  specification?: string;
  unitOfMeasure?: string;
  quantity: number;
  unitPrice: number;
  lineTotal: number;
  qtyReceived: number;
  qtyInvoiced: number;
  qtyPending: number;
  qtyPendingInvoice: number;
  requiredDate?: string;
  lineNotes?: string;
  warehouseId?: string;
  warehouseName?: string;
  effectiveWarehouseId?: string;
  effectiveWarehouseName?: string;
}

export interface PoDetailModel {
  uuid: string;
  poNumber: string;
  title?: string;
  supplierId: string;
  supplierName: string;
  status: string;
  totalAmount: number;
  deliveryDate?: string;
  deliveryWarehouseId?: string;
  deliveryWarehouseName?: string;
  notes?: string;
  budgetCode?: string;
  createdBy: number;
  createdDate: string;
  lines: PoLineModel[];
  linkedPrUuids: string[];
}

// ═══════════════════════════════════════════════════════════════════════════════
// SERVICE
// ═══════════════════════════════════════════════════════════════════════════════

@Injectable({ providedIn: 'root' })
export class DemandService {
  constructor(private http: HttpClient) {}

  // ── Purchase Requisitions ─────────────────────────────────────────────────

  createPr(req: CreatePrRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/requisitions`, req);
  }

  getPrs(filter: PrListFilter): Observable<ApiResponse<PaginatedResponse<PrListItemModel>>> {
    let params = new HttpParams();
    if (filter.status)      params = params.set('status',      filter.status);
    if (filter.department)  params = params.set('department',  filter.department);
    if (filter.requesterId) params = params.set('requesterId', filter.requesterId);
    if (filter.dateFrom)    params = params.set('dateFrom',    filter.dateFrom);
    if (filter.dateTo)      params = params.set('dateTo',      filter.dateTo);
    if (filter.search)      params = params.set('search',      filter.search);
    params = params.set('page',     filter.page     ?? 1);
    params = params.set('pageSize', filter.pageSize ?? 20);
    return this.http.get<ApiResponse<PaginatedResponse<PrListItemModel>>>(`${BASE}/requisitions`, { params });
  }

  getPrById(uuid: string): Observable<ApiResponse<PrDetailModel>> {
    return this.http.get<ApiResponse<PrDetailModel>>(`${BASE}/requisitions/${uuid}`);
  }

  patchPr(uuid: string, req: PatchPrRequest): Observable<ApiResponse<null>> {
    return this.http.patch<ApiResponse<null>>(`${BASE}/requisitions/${uuid}`, req);
  }

  submitPr(uuid: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/requisitions/${uuid}/submit`, {});
  }

  approvePr(uuid: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/requisitions/${uuid}/approve`, {});
  }

  rejectPr(uuid: string, rejectionReason: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/requisitions/${uuid}/reject`, { rejectionReason });
  }

  convertPrToSingleVendorPo(uuid: string, req: ConvertPrToPoRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/requisitions/${uuid}/convert`, req);
  }

  convertPrToSplitPos(uuid: string, req: ConvertPrSplitRequest): Observable<ApiResponse<string[]>> {
    return this.http.post<ApiResponse<string[]>>(`${BASE}/requisitions/${uuid}/convert-split`, req);
  }

  // ── Quotations ────────────────────────────────────────────────────────────

  createQuotation(req: CreateQuotationRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/quotations`, req);
  }

  getQuotations(filter: QuotationListFilter): Observable<ApiResponse<PaginatedResponse<QuotationListItemModel>>> {
    let params = new HttpParams();
    if (filter.status)     params = params.set('status',     filter.status);
    if (filter.sourceType) params = params.set('sourceType', filter.sourceType);
    if (filter.search)     params = params.set('search',     filter.search);
    params = params.set('page',     filter.page     ?? 1);
    params = params.set('pageSize', filter.pageSize ?? 20);
    return this.http.get<ApiResponse<PaginatedResponse<QuotationListItemModel>>>(`${BASE}/quotations`, { params });
  }

  getQuotationById(uuid: string): Observable<ApiResponse<QuotationDetailModel>> {
    return this.http.get<ApiResponse<QuotationDetailModel>>(`${BASE}/quotations/${uuid}`);
  }

  sendQuotation(uuid: string, req: SendQuotationRequest): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/quotations/${uuid}/send`, req);
  }

  sendWithLink(uuid: string, req: SendWithLinkRequest): Observable<ApiResponse<SendWithLinkResult>> {
    return this.http.post<ApiResponse<SendWithLinkResult>>(`${BASE}/quotations/${uuid}/send-with-link`, req);
  }

  recordVendorResponse(uuid: string, req: RecordVendorResponseRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/quotations/${uuid}/responses`, req);
  }

  getQuotationComparison(uuid: string): Observable<ApiResponse<VendorResponseModel[]>> {
    return this.http.get<ApiResponse<VendorResponseModel[]>>(`${BASE}/quotations/${uuid}/comparison`);
  }

  awardQuotation(uuid: string, req: AwardQuotationRequest): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/quotations/${uuid}/award`, req);
  }

  cancelQuotation(uuid: string, reason: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/quotations/${uuid}/cancel`, { reason });
  }

  getAccessLinks(uuid: string): Observable<ApiResponse<RfqAccessLinkModel[]>> {
    return this.http.get<ApiResponse<RfqAccessLinkModel[]>>(`${BASE}/quotations/${uuid}/access-links`);
  }

  resendAccessLink(uuid: string, linkId: number): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/quotations/${uuid}/access-links/${linkId}/resend`, {});
  }

  // ── Purchase Orders ───────────────────────────────────────────────────────


  createPo(req: CreatePoRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/purchase-orders`, req);
  }

  getPos(filter: PoListFilter): Observable<ApiResponse<PaginatedResponse<PoListItemModel>>> {
    let params = new HttpParams();
    if (filter.status)     params = params.set('status',     filter.status);
    if (filter.supplierId) params = params.set('supplierId', filter.supplierId);
    if (filter.dateFrom)   params = params.set('dateFrom',   filter.dateFrom);
    if (filter.dateTo)     params = params.set('dateTo',     filter.dateTo);
    if (filter.search)     params = params.set('search',     filter.search);
    params = params.set('page',     filter.page     ?? 1);
    params = params.set('pageSize', filter.pageSize ?? 20);
    return this.http.get<ApiResponse<PaginatedResponse<PoListItemModel>>>(`${BASE}/purchase-orders`, { params });
  }

  searchPosForGrn(q?: string, receivableOnly?: boolean): Observable<ApiResponse<PoSearchItemModel[]>> {
    let params = new HttpParams();
    if (receivableOnly) params = params.set('status', 'receivable');
    if (q)              params = params.set('q', q);
    return this.http.get<ApiResponse<PoSearchItemModel[]>>(`${BASE}/purchase-orders/search`, { params });
  }

  getPoById(uuid: string): Observable<ApiResponse<PoDetailModel>> {
    return this.http.get<ApiResponse<PoDetailModel>>(`${BASE}/purchase-orders/${uuid}`);
  }

  patchPo(uuid: string, req: PatchPoRequest): Observable<ApiResponse<null>> {
    return this.http.patch<ApiResponse<null>>(`${BASE}/purchase-orders/${uuid}`, req);
  }

  submitPo(uuid: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/purchase-orders/${uuid}/submit`, {});
  }

  approvePo(uuid: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/purchase-orders/${uuid}/approve`, {});
  }

  rejectPo(uuid: string, reason: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/purchase-orders/${uuid}/reject`, { rejectionReason: reason });
  }

  sendPo(uuid: string, supplierContactMobile?: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/purchase-orders/${uuid}/send`, { supplierContactMobile: supplierContactMobile ?? null });
  }

  patchQuotation(uuid: string, req: PatchQuotationRequest): Observable<ApiResponse<null>> {
    return this.http.patch<ApiResponse<null>>(`${BASE}/quotations/${uuid}`, req);
  }
}

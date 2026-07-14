import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse, PaginatedResponse } from './demand.service';

const BASE = 'https://localhost:51800/api';

// ── Request models ────────────────────────────────────────────────────────────

export interface PatchGrnRequest {
  warehouseUuid?: string;
  receivedAt?: string;
  deliveryNoteNo?: string;
  vehicleNo?: string;
  driverName?: string;
  invoiceNo?: string;
  notes?: string;
  requiresInspection?: boolean;
}

export interface GrnLineReceiveInput {
  poLineUuid: string;
  qtyReceived: number;
  qtyAccepted: number;
  qtyRejected: number;
  rejectionReason?: string;
  batchNumber?: string;
  expiryDate?: string;
}

export interface CreateGrnRequest {
  poUuid: string;
  warehouseUuid?: string;
  receivedAt: string;
  deliveryNoteNo?: string;
  vehicleNo?: string;
  driverName?: string;
  invoiceNo?: string;
  notes?: string;
  requiresInspection?: boolean;
  lines?: GrnLineReceiveInput[];
}

export interface UpdateGrnLineRequest {
  productUuid?: string;
  qtyReceived: number;
  qtyAccepted: number;
  qtyRejected: number;
  rejectionReason?: string;
  binUuid?: string;
  batchNumber?: string;
  expiryDate?: string;
  unitCost?: number;
  qcResult?: string;  // PASS | FAIL | PARTIAL
}

// Multi-level approval requests
export interface QcConfirmRequest {
  qcNotes?: string;
}

export interface QcRejectRequest {
  reason: string;
}

export interface FinanceRejectRequest {
  reason: string;
}

export interface InventoryManagerRejectRequest {
  reason: string;
}

// ── SRO Request models ────────────────────────────────────────────────────────

export interface CreateSroLineInput {
  grnLineUuid?: string;
  productUuid?: string;
  itemDescription: string;
  unitOfMeasure?: string;
  qtyToReturn: number;
  returnReason: string;
  returnReasonDetail?: string;
  condition?: string;
  unitCost?: number;
}

export interface CreateSroRequest {
  supplierId: string;
  supplierName: string;
  sroType: string;
  originalGrnId?: string;
  originalPoId?: string;
  warehouseUuid?: string;
  returnReason: string;
  returnReasonDetail?: string;
  notes?: string;
  lines: CreateSroLineInput[];
}

export interface ApproveSroRequest {
  notes?: string;
}

export interface RejectSroRequest {
  reason: string;
}

export interface DispatchSroRequest {
  rmaNumber?: string;
  dispatchDate: string;
  dispatchCarrier?: string;
  dispatchTrackingRef?: string;
}

export interface ConfirmReceiptSroRequest {
  notes?: string;
}

export interface ResolveSroRequest {
  resolutionType: string;
  creditNoteNumber?: string;
  creditAmount?: number;
  debitNoteNumber?: string;
  debitAmount?: number;
  replacementPoUuid?: string;
  notes?: string;
}

// ── SRO Filter ────────────────────────────────────────────────────────────────

export interface SroListFilter {
  status?: string;
  sroType?: string;
  supplierId?: string;
  dateFrom?: string;
  dateTo?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

// ── SRO Response models ───────────────────────────────────────────────────────

export interface SroLineModel {
  uuid: string;
  lineNo: number;
  grnLineUuid?: string;
  productUuid?: string;
  itemDescription: string;
  unitOfMeasure?: string;
  qtyToReturn: number;
  returnReason: string;
  returnReasonDetail?: string;
  condition?: string;
  unitCost?: number;
}

export interface SroListItemModel {
  uuid: string;
  sroNumber: string;
  sroType: string;
  supplierId: string;
  supplierName: string;
  originalGrnId?: string;
  originalGrnNumber?: string;
  originalPoId?: string;
  originalPoNumber?: string;
  status: string;
  returnReason: string;
  rmaNumber?: string;
  dispatchDate?: string;
  createdDate: string;
  totalLines: number;
  products?: string;
}

export interface SroDetailModel {
  uuid: string;
  sroNumber: string;
  sroType: string;
  supplierId: string;
  supplierName: string;
  originalGrnId?: string;
  originalGrnNumber?: string;
  originalPoId?: string;
  originalPoNumber?: string;
  status: string;
  returnReason: string;
  returnReasonDetail?: string;
  rmaNumber?: string;
  dispatchDate?: string;
  dispatchCarrier?: string;
  dispatchTrackingRef?: string;
  resolutionType?: string;
  resolvedAt?: string;
  approvedBy?: number;
  approvedAt?: string;
  rejectionReason?: string;
  slaDeadline?: string;
  notes?: string;
  createdBy: number;
  createdDate: string;
  lines: SroLineModel[];
}

// ── GRN Filter ────────────────────────────────────────────────────────────────

export interface GrnListFilter {
  status?: string;
  supplierId?: string;
  poUuid?: string;
  dateFrom?: string;
  dateTo?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

// ── Response models ───────────────────────────────────────────────────────────

export interface GrnListItemModel {
  uuid: string;
  grnNumber: string;
  poUuid: string;
  poNumber: string;
  supplierId: string;
  supplierName: string;
  status: string;
  isPartialReceipt: boolean;
  financeApprovalRequired: boolean;
  receivedAt: string;
  createdDate: string;
}

export interface InspectGrnLineRequest {
  inspectionResult: string;  // Pass | Fail | PartialPass
  qtyAccepted: number;
  qtyRejected: number;
  rejectionReason?: string;
  inspectorRemarks?: string;
}

export interface GrnLineModel {
  uuid: string;
  poLineUuid: string;
  productUuid?: string;
  lineNo: number;
  itemDescription: string;
  unitOfMeasure?: string;
  qtyOrdered: number;
  qtyReceived: number;
  qtyAccepted: number;
  qtyRejected: number;
  // Lookup: Damaged | Wrong item | Short expiry | Over qty
  rejectionReason?: string;
  binUuid?: string;
  batchNumber?: string;
  expiryDate?: string;
  unitCost?: number;
  hasVariance: boolean;
  qcResult?: string;
  // Formal inspection (set during PENDING_QC)
  inspectionResult?: string;
  inspectorRemarks?: string;
  inspectedBy?: number;
  inspectedAt?: string;
}

export interface GrnDetailModel {
  uuid: string;
  grnNumber: string;
  poUuid: string;
  poNumber: string;
  supplierId: string;
  supplierName: string;
  warehouseUuid?: string;
  receivedAt: string;
  deliveryNoteNo?: string;
  vehicleNo?: string;
  driverName?: string;
  status: string;

  // QC
  qcPassed: boolean;
  qcNotes?: string;
  qcDoneBy?: number;
  qcConfirmedBy?: number;
  qcConfirmedAt?: string;

  // Finance
  financeApprovalRequired: boolean;
  financeApprovedBy?: number;
  financeApprovedAt?: string;

  // IM Approval
  receivedBy: number;
  approvedBy?: number;
  approvedAt?: string;
  approvalDeadline?: string;

  // Rejection
  rejectionReason?: string;

  invoiceNo?: string;
  notes?: string;
  requiresInspection: boolean;
  inspectionCompletedAt?: string;
  inspectionComplete: boolean;
  inspectedLineCount: number;
  totalLineCount: number;
  isPartialReceipt: boolean;
  createdDate: string;
  lines: GrnLineModel[];
}

// ── Service ───────────────────────────────────────────────────────────────────

@Injectable({ providedIn: 'root' })
export class WarehouseService {
  constructor(private http: HttpClient) {}

  // ── GRN CRUD ──────────────────────────────────────────────────────────────
  createGrn(req: CreateGrnRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/grns`, req);
  }

  getGrns(filter: GrnListFilter): Observable<ApiResponse<PaginatedResponse<GrnListItemModel>>> {
    let params = new HttpParams();
    if (filter.status)    params = params.set('status',    filter.status);
    if (filter.poUuid)    params = params.set('poUuid',    filter.poUuid);
    if (filter.search)    params = params.set('search',    filter.search);
    if (filter.dateFrom)  params = params.set('dateFrom',  filter.dateFrom);
    if (filter.dateTo)    params = params.set('dateTo',    filter.dateTo);
    if (filter.page)      params = params.set('page',      filter.page.toString());
    if (filter.pageSize)  params = params.set('pageSize',  filter.pageSize.toString());
    return this.http.get<ApiResponse<PaginatedResponse<GrnListItemModel>>>(`${BASE}/grns`, { params });
  }

  getGrnById(uuid: string): Observable<ApiResponse<GrnDetailModel>> {
    return this.http.get<ApiResponse<GrnDetailModel>>(`${BASE}/grns/${uuid}`);
  }

  updateGrnLine(grnUuid: string, lineUuid: string, req: UpdateGrnLineRequest): Observable<ApiResponse<null>> {
    return this.http.patch<ApiResponse<null>>(`${BASE}/grns/${grnUuid}/lines/${lineUuid}`, req);
  }

  inspectGrnLine(grnUuid: string, lineUuid: string, req: InspectGrnLineRequest): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/grns/${grnUuid}/lines/${lineUuid}/inspect`, req);
  }

  submitGrn(uuid: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/grns/${uuid}/submit`, {});
  }

  // ── Multi-level approval workflow ─────────────────────────────────────────
  qcConfirm(uuid: string, req: QcConfirmRequest): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/grns/${uuid}/qc-confirm`, req);
  }

  qcReject(uuid: string, req: QcRejectRequest): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/grns/${uuid}/qc-reject`, req);
  }

  financeApprove(uuid: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/grns/${uuid}/finance-approve`, {});
  }

  financeReject(uuid: string, req: FinanceRejectRequest): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/grns/${uuid}/finance-reject`, req);
  }

  approveGrn(uuid: string, remarks?: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/grns/${uuid}/approve`, { remarks: remarks || null });
  }

  rejectGrn(uuid: string, req: InventoryManagerRejectRequest): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/grns/${uuid}/reject`, req);
  }

  patchGrn(uuid: string, req: PatchGrnRequest): Observable<ApiResponse<null>> {
    return this.http.patch<ApiResponse<null>>(`${BASE}/grns/${uuid}`, req);
  }

  deleteGrn(uuid: string): Observable<ApiResponse<null>> {
    return this.http.delete<ApiResponse<null>>(`${BASE}/grns/${uuid}`);
  }

  // ── SRO CRUD ──────────────────────────────────────────────────────────────

  createSro(req: CreateSroRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/sros`, req);
  }

  getSros(filter: SroListFilter): Observable<ApiResponse<PaginatedResponse<SroListItemModel>>> {
    let params = new HttpParams();
    if (filter.status)     params = params.set('status',     filter.status);
    if (filter.sroType)    params = params.set('sroType',    filter.sroType);
    if (filter.supplierId) params = params.set('supplierId', filter.supplierId);
    if (filter.search)     params = params.set('search',     filter.search);
    if (filter.dateFrom)   params = params.set('dateFrom',   filter.dateFrom);
    if (filter.dateTo)     params = params.set('dateTo',     filter.dateTo);
    if (filter.page)       params = params.set('page',       filter.page.toString());
    if (filter.pageSize)   params = params.set('pageSize',   filter.pageSize.toString());
    return this.http.get<ApiResponse<PaginatedResponse<SroListItemModel>>>(`${BASE}/sros`, { params });
  }

  getSroById(uuid: string): Observable<ApiResponse<SroDetailModel>> {
    return this.http.get<ApiResponse<SroDetailModel>>(`${BASE}/sros/${uuid}`);
  }

  approveSro(uuid: string, req: ApproveSroRequest): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/sros/${uuid}/approve`, req);
  }

  rejectSro(uuid: string, req: RejectSroRequest): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/sros/${uuid}/reject`, req);
  }

  dispatchSro(uuid: string, req: DispatchSroRequest): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/sros/${uuid}/dispatch`, req);
  }

  confirmReceiptSro(uuid: string, req: ConfirmReceiptSroRequest): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/sros/${uuid}/confirm-receipt`, req);
  }

  resolveSro(uuid: string, req: ResolveSroRequest): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/sros/${uuid}/resolve`, req);
  }

  escalateSro(uuid: string, req: { reason: string }): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/sros/${uuid}/escalate`, req);
  }

  expectReplacementSro(uuid: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/sros/${uuid}/expect-replacement`, {});
  }
}

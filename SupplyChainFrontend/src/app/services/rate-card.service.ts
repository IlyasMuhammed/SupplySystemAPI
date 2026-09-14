import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface DiscountTierDto {
  qtyFrom: number;
  qtyTo?: number | null;
  discountPct: number;
}

export interface RateCardRow {
  uuid: string;
  variantUuid: string;
  productName: string;
  variantName: string;
  sku: string;
  vendorPartNo?: string | null;
  vendorUnitCost: number;
  currencyId: string;
  lastPoPrice?: number | null;
  leadTimeDays?: number | null;
  minOrderValue?: number | null;
  minOrderQty?: number | null;
  isPreferred: boolean;
  effectiveFrom: string;
  effectiveTo?: string | null;
  status: 'ACTIVE' | 'EXPIRED' | 'PENDING' | 'STALE';

  // Round-tripped on save, not shown as grid columns.
  discountTiers: DiscountTierDto[];
  quotationRef?: string | null;
  notes?: string | null;
  isActive: boolean;
  lastReviewedAt?: string | null;
  lastReviewedBy?: number | null;
}

// RC-004 — full slide-over detail, GET /api/rate-cards/{uuid}.
export interface PoReferenceSummary {
  lastPoUuid: string;
  lastPoNumber: string;
  lastPoDate: string;
  lastPoPrice: number;
  poCountLast12Months: number;
}

export interface VariantSupplierDetail {
  uuid: string;
  variantUuid: string;
  variantSku: string;
  productName: string;
  supplierId: string;
  vendorUnitCost: number;
  leadTimeDays?: number | null;
  isActive: boolean;
  effectiveFrom: string;
  effectiveTo?: string | null;
  currencyId: string;
  minOrderValue?: number | null;
  minOrderQty?: number | null;
  discountTiers: DiscountTierDto[];
  quotationRef?: string | null;
  notes?: string | null;
  notesUpdatedAt?: string | null;
  notesUpdatedByName?: string | null;
  vendorPartNo?: string | null;
  isPreferred: boolean;
  lastReviewedAt?: string | null;
  lastReviewedBy?: number | null;
  // RC-007
  lastReviewedByName?: string | null;
  createdDate: string;
  modifiedDate?: string | null;
  poReference?: PoReferenceSummary | null;
}

export interface RateHistoryEntry {
  id: number;
  fieldChanged: string;
  oldValue?: string | null;
  newValue?: string | null;
  changeReason?: string | null;
  changedBy: number;
  changedByName?: string | null;
  changedAt: string;
}

export interface ActiveRateInfo {
  variantSupplierUuid: string;
  vendorUnitCost: number;
  currencyId: string;
  leadTimeDays?: number | null;
  minOrderValue?: number | null;
  discountTiers?: string | null;
  effectiveFrom: string;
  effectiveTo?: string | null;
}

export interface RateCardListFilter {
  supplierId: string;
  search?: string;
  sortBy?: string;
  sortDir?: 'asc' | 'desc';
  page?: number;
  pageSize?: number;
}

export interface UpdateRateCardRequest {
  vendorUnitCost: number;
  leadTimeDays?: number | null;
  effectiveFrom: string;
  effectiveTo?: string | null;
  currencyId: string;
  minOrderValue?: number | null;
  minOrderQty?: number | null;
  discountTiers?: DiscountTierDto[] | null;
  quotationRef?: string | null;
  notes?: string | null;
  vendorPartNo?: string | null;
  isActive: boolean;
  lastReviewedAt?: string | null;
  lastReviewedBy?: number | null;
  changeReason?: string | null;
}

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

// RC-003 — Product Comparison View.
export interface RateComparisonRow {
  uuid: string;
  supplierId: string;
  supplierName: string;
  vendorPartNo?: string | null;
  vendorUnitCost: number;
  currencyId: string;
  leadTimeDays?: number | null;
  minOrderValue?: number | null;
  minOrderQty?: number | null;
  lastPoDate?: string | null;
  scorecardGrade?: string | null;
  isPreferred: boolean;
}

// RC-005 — Bulk Rate Adjustment.
export interface BulkAdjustRequest {
  variantSupplierIds: string[];
  method: 'PERCENTAGE' | 'FIXED';
  value: number;
  changeReason: string;
}

export interface BulkAdjustPreviewRow {
  variantSupplierId: string;
  productName: string;
  variantName: string;
  currentRate: number;
  newRate: number;
  difference: number;
  diffPct: number;
}

export interface BulkAdjustConfirmResult {
  bulkOperationId: string;
  affectedCount: number;
  totalImpactAmount: number;
}

export interface BulkRateOperation {
  uuid: string;
  method: 'PERCENTAGE' | 'FIXED';
  value: number;
  affectedCount: number;
  totalImpactAmount: number;
  changeReason: string;
  performedBy: number;
  performedByName?: string | null;
  performedAt: string;
  isUndone: boolean;
}

// RC-006 — Excel import/export.
export interface ImportPreviewRow {
  row: number;
  sku: string;
  productName?: string | null;
  currentRate?: number | null;
  importedRate?: number | null;
  rateChanged: boolean;
  newRecord: boolean;
  error?: string | null;
}

export interface ImportRowError {
  row: number;
  sku: string;
  message: string;
}

export interface ImportConfirmResult {
  updatedCount: number;
  createdCount: number;
  errors: ImportRowError[];
}

// RC-006 — Copy rates between suppliers.
export interface CopyRatesRequest {
  sourceSupplierId: string;
  targetSupplierId: string;
  adjustmentPct: number;
}

export interface CopyPreviewRow {
  variantSupplierId: string;
  productName: string;
  variantName: string;
  sku: string;
  sourceRate: number;
  adjustedRate: number;
  willSkip: boolean;
  skipReason?: string | null;
}

export interface CopyConfirmResult {
  createdCount: number;
  skippedCount: number;
}

@Injectable({ providedIn: 'root' })
export class RateCardService {
  private readonly baseUrl = `${environment.apiUrl}/rate-cards`;

  constructor(private http: HttpClient) {}

  getRateCards(filter: RateCardListFilter): Observable<ApiResponse<PaginatedResponse<RateCardRow>>> {
    let params = new HttpParams().set('supplierId', filter.supplierId);
    if (filter.search)  params = params.set('search', filter.search);
    if (filter.sortBy)  params = params.set('sortBy', filter.sortBy);
    if (filter.sortDir) params = params.set('sortDir', filter.sortDir);
    params = params.set('page', String(filter.page ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<RateCardRow>>>(this.baseUrl, { params });
  }

  updateRateCard(uuid: string, data: UpdateRateCardRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.baseUrl}/${uuid}`, data);
  }

  setPreferred(uuid: string): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${this.baseUrl}/${uuid}/preferred`, {});
  }

  getComparison(variantUuid: string): Observable<ApiResponse<RateComparisonRow[]>> {
    const params = new HttpParams().set('variantUuid', variantUuid);
    return this.http.get<ApiResponse<RateComparisonRow[]>>(`${this.baseUrl}/compare`, { params });
  }

  exportComparison(variantUuid: string): Observable<Blob> {
    const params = new HttpParams().set('variantUuid', variantUuid);
    return this.http.get(`${this.baseUrl}/compare/export`, { params, responseType: 'blob' });
  }

  // RC-004 — rate-edit slide-over.
  getDetail(uuid: string): Observable<ApiResponse<VariantSupplierDetail>> {
    return this.http.get<ApiResponse<VariantSupplierDetail>>(`${this.baseUrl}/${uuid}`);
  }

  // RC-007 — mark-as-reviewed. Does NOT change the rate; clears the stale flag only.
  markReviewed(uuid: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.baseUrl}/${uuid}/mark-reviewed`, {});
  }

  getHistory(uuid: string, page = 1, pageSize = 50): Observable<ApiResponse<PaginatedResponse<RateHistoryEntry>>> {
    const params = new HttpParams().set('page', String(page)).set('pageSize', String(pageSize));
    return this.http.get<ApiResponse<PaginatedResponse<RateHistoryEntry>>>(`${this.baseUrl}/${uuid}/history`, { params });
  }

  getActiveRate(variantUuid: string, supplierUuid: string, asOfDate?: string): Observable<ApiResponse<ActiveRateInfo | null>> {
    let params = new HttpParams().set('variantUuid', variantUuid).set('supplierUuid', supplierUuid);
    if (asOfDate) params = params.set('asOfDate', asOfDate);
    return this.http.get<ApiResponse<ActiveRateInfo | null>>(`${this.baseUrl}/active-rate`, { params });
  }

  // RC-005 — bulk rate adjustment.
  previewBulkAdjust(req: BulkAdjustRequest): Observable<ApiResponse<BulkAdjustPreviewRow[]>> {
    return this.http.post<ApiResponse<BulkAdjustPreviewRow[]>>(`${this.baseUrl}/bulk-adjust/preview`, req);
  }

  confirmBulkAdjust(req: BulkAdjustRequest): Observable<ApiResponse<BulkAdjustConfirmResult>> {
    return this.http.post<ApiResponse<BulkAdjustConfirmResult>>(`${this.baseUrl}/bulk-adjust/confirm`, req);
  }

  undoBulkAdjust(bulkOperationId: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.baseUrl}/bulk-adjust/undo/${bulkOperationId}`, {});
  }

  getRecentBulkOperations(): Observable<ApiResponse<BulkRateOperation[]>> {
    return this.http.get<ApiResponse<BulkRateOperation[]>>(`${this.baseUrl}/bulk-adjust/recent`);
  }

  // RC-006 — Excel import/export.
  exportRateCards(supplierId: string): Observable<Blob> {
    const params = new HttpParams().set('supplierId', supplierId);
    return this.http.get(`${this.baseUrl}/export`, { params, responseType: 'blob' });
  }

  previewImport(file: File, supplierId: string): Observable<ApiResponse<ImportPreviewRow[]>> {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('supplierId', supplierId);
    return this.http.post<ApiResponse<ImportPreviewRow[]>>(`${this.baseUrl}/import/preview`, form);
  }

  confirmImport(file: File, supplierId: string): Observable<ApiResponse<ImportConfirmResult>> {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('supplierId', supplierId);
    return this.http.post<ApiResponse<ImportConfirmResult>>(`${this.baseUrl}/import/confirm`, form);
  }

  // RC-006 — Copy rates between suppliers.
  previewCopy(req: CopyRatesRequest): Observable<ApiResponse<CopyPreviewRow[]>> {
    return this.http.post<ApiResponse<CopyPreviewRow[]>>(`${this.baseUrl}/copy/preview`, req);
  }

  confirmCopy(req: CopyRatesRequest): Observable<ApiResponse<CopyConfirmResult>> {
    return this.http.post<ApiResponse<CopyConfirmResult>>(`${this.baseUrl}/copy`, req);
  }
}

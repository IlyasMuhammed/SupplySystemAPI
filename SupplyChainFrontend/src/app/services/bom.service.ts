import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse, PaginatedResponse } from './inventory.service';
import { environment } from '../../environments/environment';

// A30 §7–§9 / §28.2 — bills of materials. Mirrors SMS.Modules.Material.Models.BomModels.

export type BomStatus = 'DRAFT' | 'SUBMITTED' | 'APPROVED' | 'ACTIVE' | 'OBSOLETE' | 'REJECTED';

export const BOM_STATUS_OPTIONS: { value: BomStatus | ''; label: string }[] = [
  { value: '',          label: 'All statuses' },
  { value: 'DRAFT',     label: 'Draft' },
  { value: 'SUBMITTED', label: 'Submitted' },
  { value: 'APPROVED',  label: 'Approved' },
  { value: 'ACTIVE',    label: 'Active' },
  { value: 'OBSOLETE',  label: 'Obsolete' },
  { value: 'REJECTED',  label: 'Rejected' }
];

export function bomStatusSeverity(status: string): 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast' {
  switch (status) {
    case 'ACTIVE':    return 'success';
    case 'APPROVED':  return 'info';
    case 'SUBMITTED': return 'warn';
    case 'REJECTED':  return 'danger';
    case 'OBSOLETE':  return 'contrast';
    default:          return 'secondary';
  }
}

/** A37 D-11 — which kind of order a BOM is meant for; advisory (pickers sort by it, nothing is hidden). */
export type BomUsage = 'UNIVERSAL' | 'PRODUCTION_PREFERRED' | 'SERVICE_PREFERRED';

export const BOM_USAGE_OPTIONS: { label: string; value: BomUsage }[] = [
  { label: 'Universal',            value: 'UNIVERSAL' },
  { label: 'Production preferred', value: 'PRODUCTION_PREFERRED' },
  { label: 'Service preferred',    value: 'SERVICE_PREFERRED' }
];

export function bomUsageLabel(usage: string | null | undefined): string {
  return BOM_USAGE_OPTIONS.find(o => o.value === (usage ?? 'UNIVERSAL'))?.label ?? (usage || 'Universal');
}

/** The short tag a picker shows next to a BOM: none for UNIVERSAL (the default). */
export function bomUsageTag(usage: string | null | undefined): string | null {
  switch (usage) {
    case 'PRODUCTION_PREFERRED': return 'Production';
    case 'SERVICE_PREFERRED':    return 'Service';
    default:                     return null;
  }
}

/** A37 — ?preferFor on GET /api/boms: matching usage first. */
export type BomPreferFor = 'PRODUCTION' | 'SERVICE';

export interface BomLineRequest {
  materialVariantUuid: string;
  quantity: number;
  uom?: string;
  scrapPercentage: number;
  isCritical: boolean;
  alternateVariantUuid?: string;
  notes?: string;
  sequence?: number;
  /** A36 D-4 — STOCK (default) | SUBCONTRACT | INTERNAL_LABOR; only service BOMs use the other two. */
  sourceType?: BomLineSourceType;
  /** A36 — the vendor (business partner) for a SUBCONTRACT line; null otherwise. */
  subcontractSupplierUuid?: string | null;
}

/** A36 D-4 — where a BOM line's material comes from. */
export type BomLineSourceType = 'STOCK' | 'SUBCONTRACT' | 'INTERNAL_LABOR';

export const BOM_LINE_SOURCE_OPTIONS: { label: string; value: BomLineSourceType }[] = [
  { label: 'Stock',          value: 'STOCK' },
  { label: 'Subcontract',    value: 'SUBCONTRACT' },
  { label: 'Internal labor', value: 'INTERNAL_LABOR' }
];

export interface CreateBomRequest {
  productUuid: string;
  productVariantUuid?: string;
  baseQuantity: number;
  baseUom?: string;
  /** A31-C5 — defaults to today on the server when left undefined. */
  effectiveFrom?: string;
  effectiveTo?: string;
  notes?: string;
  lines: BomLineRequest[];
  /** A37 — UNIVERSAL when omitted. */
  bomUsage?: BomUsage;
}

export interface UpdateBomRequest {
  baseQuantity?: number;
  baseUom?: string;
  effectiveFrom?: string;
  effectiveTo?: string;
  clearEffectiveDates?: boolean;
  notes?: string;
  lines?: BomLineRequest[];
  /** A37 — on a DRAFT/REJECTED BOM only; past that, setUsage (PUT /api/boms/{uuid}/usage). */
  bomUsage?: BomUsage;
}

export interface BomListFilter {
  productUuid?: string;
  status?: string;
  search?: string;
  page?: number;
  pageSize?: number;
  /** A37 — matching usage first, nothing hidden. */
  preferFor?: BomPreferFor | null;
}

export interface BomListItem {
  uuid: string;
  bomNumber: string;
  productUuid: string;
  productName: string;
  productSku: string;
  productVariantUuid?: string | null;
  variantName?: string | null;
  version: number;
  status: BomStatus;
  baseQuantity: number;
  baseUom: string;
  effectiveFrom?: string | null;
  effectiveTo?: string | null;
  lineCount: number;
  createdAt: string;
  updatedAt: string;
  activatedAt?: string | null;
  /** A37 D-11 — absent on older servers = UNIVERSAL. */
  bomUsage?: BomUsage | null;
}

export interface BomLine {
  uuid: string;
  sequence: number;
  materialProductUuid: string;
  materialProductName: string;
  materialProductType: string;
  materialSupplyMethod: string;
  materialVariantUuid: string;
  materialSku: string;
  materialVariantName: string;
  materialImageUrl?: string | null;
  quantity: number;
  uom: string;
  scrapPercentage: number;
  grossQuantity: number;
  isCritical: boolean;
  alternateVariantUuid?: string | null;
  alternateVariantName?: string | null;
  notes?: string | null;
  /** A36 D-4 — absent on older servers = STOCK. */
  sourceType?: BomLineSourceType;
  subcontractSupplierUuid?: string | null;
  subcontractSupplierName?: string | null;
}

export interface BomDetail extends BomListItem {
  traceId: string;
  notes?: string | null;
  createdBy: number;
  submittedBy?: number | null;
  submittedAt?: string | null;
  approvedBy?: number | null;
  approvedAt?: string | null;
  rejectedBy?: number | null;
  rejectedAt?: string | null;
  rejectionReason?: string | null;
  activatedBy?: number | null;
  obsoletedBy?: number | null;
  obsoletedAt?: string | null;
  lines: BomLine[];
}

export interface BomVersion {
  uuid: string;
  bomNumber: string;
  version: number;
  status: BomStatus;
  lineCount: number;
  createdAt: string;
  activatedAt?: string | null;
  obsoletedAt?: string | null;
}

export interface BomLineChange {
  materialVariantUuid: string;
  materialName: string;
  before: BomLine;
  after: BomLine;
  fields: string[];
}

export interface BomComparison {
  leftUuid: string;
  leftVersion: number;
  leftStatus: string;
  rightUuid: string;
  rightVersion: number;
  rightStatus: string;
  headerChanges: string[];
  added: BomLine[];
  removed: BomLine[];
  changed: BomLineChange[];
}

export interface BomLineCost {
  lineUuid: string;
  materialVariantUuid: string;
  materialName: string;
  quantity: number;
  scrapPercentage: number;
  grossQuantity: number;
  unitCost: number;
  lineCost: number;
  costSource: 'LAST_PURCHASE_PRICE' | 'PURCHASE_PRICE' | 'BOM_ROLLUP' | 'NONE';
  nestedBomUuid?: string | null;
}

export interface BomCost {
  bomUuid: string;
  version: number;
  baseQuantity: number;
  totalCost: number;
  costPerUnit: number;
  lines: BomLineCost[];
  warnings: string[];
}

@Injectable({ providedIn: 'root' })
export class BomService {
  private readonly base = `${environment.apiUrl}/boms`;

  constructor(private http: HttpClient) {}

  createBom(req: CreateBomRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(this.base, req);
  }

  getBoms(filter: BomListFilter = {}): Observable<ApiResponse<PaginatedResponse<BomListItem>>> {
    let params = new HttpParams();
    if (filter.productUuid) params = params.set('productUuid', filter.productUuid);
    if (filter.status)      params = params.set('status',      filter.status);
    if (filter.search)      params = params.set('search',      filter.search);
    if (filter.page)        params = params.set('page',        String(filter.page));
    if (filter.pageSize)    params = params.set('pageSize',    String(filter.pageSize));
    if (filter.preferFor)   params = params.set('preferFor',   filter.preferFor);
    return this.http.get<ApiResponse<PaginatedResponse<BomListItem>>>(this.base, { params });
  }

  getBom(uuid: string): Observable<ApiResponse<BomDetail>> {
    return this.http.get<ApiResponse<BomDetail>>(`${this.base}/${uuid}`);
  }

  getVersions(productUuid: string, variantUuid?: string | null): Observable<ApiResponse<BomVersion[]>> {
    let params = new HttpParams();
    if (variantUuid) params = params.set('variantUuid', variantUuid);
    return this.http.get<ApiResponse<BomVersion[]>>(`${environment.apiUrl}/products/${productUuid}/boms`, { params });
  }

  updateBom(uuid: string, req: UpdateBomRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.base}/${uuid}`, req);
  }

  /** A37 — usage on a BOM past DRAFT/REJECTED (not OBSOLETE); a draft changes it through updateBom. */
  setUsage(uuid: string, bomUsage: BomUsage): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.base}/${uuid}/usage`, { bomUsage });
  }

  deleteBom(uuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.base}/${uuid}`);
  }

  submit(uuid: string): Observable<ApiResponse>   { return this.http.post<ApiResponse>(`${this.base}/${uuid}/submit`, {}); }
  approve(uuid: string): Observable<ApiResponse>  { return this.http.post<ApiResponse>(`${this.base}/${uuid}/approve`, {}); }
  activate(uuid: string): Observable<ApiResponse> { return this.http.post<ApiResponse>(`${this.base}/${uuid}/activate`, {}); }

  reject(uuid: string, reason: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.base}/${uuid}/reject`, { reason });
  }

  obsolete(uuid: string, reason?: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.base}/${uuid}/obsolete`, { reason: reason ?? null });
  }

  newVersion(uuid: string): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.base}/${uuid}/new-version`, {});
  }

  compare(uuid: string, otherUuid: string): Observable<ApiResponse<BomComparison>> {
    return this.http.get<ApiResponse<BomComparison>>(`${this.base}/${uuid}/compare/${otherUuid}`);
  }

  getCost(uuid: string): Observable<ApiResponse<BomCost>> {
    return this.http.get<ApiResponse<BomCost>>(`${this.base}/${uuid}/cost`);
  }
}

import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ApiResponse } from './inventory.service';

// A30 §14 / §28.8 — the allocation engine. Mirrors SMS.Shared.Common.IAllocationEngine's records.

export type AllocationDemandType = 'SALES_ORDER' | 'PRODUCTION_MATERIAL' | 'SERVICE_ORDER' | 'REPLENISHMENT' | 'TRANSFER';
export type AllocationSupplyType = 'ON_HAND' | 'PURCHASE_ORDER' | 'PRODUCTION_ORDER' | 'TRANSFER_ORDER';
export type AllocationKind = 'PLANNED' | 'SOFT' | 'FIRM' | 'RESERVED';
export type AllocationStatus = 'ACTIVE' | 'CONSUMED' | 'RELEASED' | 'CANCELLED';
export type AllocationDemandStatus = 'OPEN' | 'FULFILLED' | 'CANCELLED';

export const DEMAND_TYPE_OPTIONS: { value: AllocationDemandType; label: string }[] = [
  { value: 'SALES_ORDER',         label: 'Sale order' },
  { value: 'PRODUCTION_MATERIAL', label: 'Production material' },
  { value: 'SERVICE_ORDER',       label: 'Service order' },
  { value: 'REPLENISHMENT',       label: 'Replenishment' },
  { value: 'TRANSFER',            label: 'Transfer' }
];

export const SUPPLY_TYPE_OPTIONS: { value: AllocationSupplyType; label: string }[] = [
  { value: 'PURCHASE_ORDER',   label: 'Purchase order' },
  { value: 'PRODUCTION_ORDER', label: 'Production order' },
  { value: 'TRANSFER_ORDER',   label: 'Transfer order' }
];

export const PRIORITY_OPTIONS: { value: number; label: string }[] = [
  { value: 0, label: 'Low' },
  { value: 1, label: 'Normal' },
  { value: 2, label: 'High' },
  { value: 3, label: 'Urgent' }
];

export const SORT_FIELD_OPTIONS: { value: string; label: string }[] = [
  { value: 'PRIORITY',         label: 'Business priority' },
  { value: 'REQUIRED_DATE',    label: 'Required date' },
  { value: 'DEMAND_TYPE_RANK', label: 'Kind of demand (sale, production, transfer)' },
  { value: 'DOCUMENT_DATE',    label: 'Document date' },
  { value: 'CREATED_AT',       label: 'When registered' }
];

export function demandTypeLabel(code: string): string {
  return DEMAND_TYPE_OPTIONS.find(o => o.value === code)?.label ?? code;
}

export function priorityLabel(value: number): string {
  return PRIORITY_OPTIONS.find(o => o.value === value)?.label ?? String(value);
}

export interface DemandAllocationSummary {
  uuid: string;
  demandType: AllocationDemandType;
  demandUuid: string;
  demandLineUuid: string | null;
  reference: string;
  variantUuid: string;
  warehouseUuid: string | null;
  requiredQty: number;
  reservedQty: number;
  plannedQty: number;
  consumedQty: number;
  shortage: number;
  requiredDate: string;
  priority: number;
  status: AllocationDemandStatus;
  // Only set on the unfiltered dashboard listing (GetDemandsAsync) — null elsewhere.
  productUuid?: string | null;
  productName?: string | null;
  variantName?: string | null;
  variantSku?: string | null;
}

export interface AllocationSummary {
  uuid: string;
  demandRegistryUuid: string;
  demandType: AllocationDemandType;
  demandUuid: string;
  demandLineUuid: string | null;
  demandReference: string;
  variantUuid: string;
  warehouseUuid: string;
  allocatedQty: number;
  consumedQty: number;
  supplyType: AllocationSupplyType;
  supplyUuid: string | null;
  supplyReference: string | null;
  allocationType: AllocationKind;
  priorityScore: number;
  requiredDate: string;
  status: AllocationStatus;
  allocatedAt: string;
  releasedAt: string | null;
  releaseReason: string | null;
}

export interface AllocationPage {
  items: AllocationSummary[];
  total: number;
  page: number;
  pageSize: number;
}

export interface AllocationRunResult {
  variantUuid: string;
  warehouseUuid: string | null;
  demandsEvaluated: number;
  quantityReserved: number;
  quantityPlanned: number;
  shortage: number;
  demands: DemandAllocationSummary[];
}

export interface AvailabilityResult {
  variantUuid: string;
  warehouseUuid: string | null;
  onHand: number;
  reserved: number;
  available: number;
  incoming: number;
  openDemand: number;
  unallocated: number;
}

export interface AllocationRuleDefinition {
  ruleName: string;
  priorityOrder: number;
  demandTypeFilter: string | null;
  sortField: string;
  sortDirection: 'ASC' | 'DESC';
  isActive: boolean;
}

export interface AllocationListFilter {
  variantUuid?: string;
  warehouseUuid?: string;
  demandType?: string;
  demandUuid?: string;
  status?: string;
  page?: number;
  pageSize?: number;
}

export interface RegisterDemandRequest {
  demandType: AllocationDemandType;
  demandUuid: string;
  demandLineUuid?: string;
  reference: string;
  variantUuid: string;
  warehouseUuid?: string;
  requiredQty: number;
  requiredDate: string;
  priority: number;
  documentDate?: string;
  allocate?: boolean;
}

export interface RegisterSupplyRequest {
  supplyType: AllocationSupplyType;
  supplyUuid: string;
  supplyLineUuid?: string;
  reference: string;
  variantUuid: string;
  warehouseUuid: string;
  expectedQty: number;
  expectedDate?: string;
  allocate?: boolean;
}

@Injectable({ providedIn: 'root' })
export class AllocationService {
  private readonly base = `${environment.apiUrl}/allocations`;

  constructor(private http: HttpClient) {}

  getAllocations(filter: AllocationListFilter = {}): Observable<ApiResponse<AllocationPage>> {
    let params = new HttpParams();
    if (filter.variantUuid)   params = params.set('variantUuid',   filter.variantUuid);
    if (filter.warehouseUuid) params = params.set('warehouseUuid', filter.warehouseUuid);
    if (filter.demandType)    params = params.set('demandType',    filter.demandType);
    if (filter.demandUuid)    params = params.set('demandUuid',    filter.demandUuid);
    if (filter.status)        params = params.set('status',        filter.status);
    if (filter.page)          params = params.set('page',          String(filter.page));
    if (filter.pageSize)      params = params.set('pageSize',      String(filter.pageSize));
    return this.http.get<ApiResponse<AllocationPage>>(this.base, { params });
  }

  getAllocation(uuid: string): Observable<ApiResponse<AllocationSummary>> {
    return this.http.get<ApiResponse<AllocationSummary>>(`${this.base}/${uuid}`);
  }

  run(variantUuid: string, warehouseUuid?: string | null): Observable<ApiResponse<AllocationRunResult>> {
    return this.http.post<ApiResponse<AllocationRunResult>>(`${this.base}/run`, { variantUuid, warehouseUuid: warehouseUuid ?? null });
  }

  release(uuid: string, reason: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.base}/${uuid}/release`, { reason });
  }

  reallocate(uuid: string, toDemandUuid: string, quantity: number, reason: string): Observable<ApiResponse<AllocationSummary>> {
    return this.http.post<ApiResponse<AllocationSummary>>(`${this.base}/${uuid}/reallocate`, { toDemandUuid, quantity, reason });
  }

  getAvailability(variantUuid: string, warehouseUuid?: string | null): Observable<ApiResponse<AvailabilityResult>> {
    let params = new HttpParams().set('variantUuid', variantUuid);
    if (warehouseUuid) params = params.set('warehouseUuid', warehouseUuid);
    return this.http.get<ApiResponse<AvailabilityResult>>(`${this.base}/availability`, { params });
  }

  getDemands(
    variantUuid?: string | null, openOnly = true, demandType?: string | null, warehouseUuid?: string | null
  ): Observable<ApiResponse<DemandAllocationSummary[]>> {
    let params = new HttpParams().set('openOnly', String(openOnly));
    if (variantUuid)   params = params.set('variantUuid',   variantUuid);
    if (demandType)    params = params.set('demandType',    demandType);
    if (warehouseUuid) params = params.set('warehouseUuid', warehouseUuid);
    return this.http.get<ApiResponse<DemandAllocationSummary[]>>(`${this.base}/demands`, { params });
  }

  getDemand(uuid: string): Observable<ApiResponse<DemandAllocationSummary>> {
    return this.http.get<ApiResponse<DemandAllocationSummary>>(`${this.base}/demands/${uuid}`);
  }

  registerDemand(req: RegisterDemandRequest): Observable<ApiResponse<DemandAllocationSummary>> {
    return this.http.post<ApiResponse<DemandAllocationSummary>>(`${this.base}/demands`, req);
  }

  cancelDemand(uuid: string, reason: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.base}/demands/${uuid}/cancel`, { reason });
  }

  registerSupply(req: RegisterSupplyRequest): Observable<ApiResponse<{ uuid: string }>> {
    return this.http.post<ApiResponse<{ uuid: string }>>(`${this.base}/supplies`, req);
  }

  getRules(): Observable<ApiResponse<AllocationRuleDefinition[]>> {
    return this.http.get<ApiResponse<AllocationRuleDefinition[]>>(`${this.base}/rules`);
  }

  setRules(rules: AllocationRuleDefinition[]): Observable<ApiResponse<AllocationRuleDefinition[]>> {
    return this.http.put<ApiResponse<AllocationRuleDefinition[]>>(`${this.base}/rules`, { rules });
  }
}

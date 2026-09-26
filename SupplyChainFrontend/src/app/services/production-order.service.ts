import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse, PaginatedResponse } from './inventory.service';
import { environment } from '../../environments/environment';

// A30 §11–§13, §16 / §28.3–§28.7 — production orders, their materials, supply requirements and
// floor issues. Mirrors SMS.Modules.Material.Models.ProductionModels.

export type ProductionOrderStatus =
  'DRAFT' | 'PLANNED' | 'MATERIAL_PENDING' | 'READY' | 'IN_PROGRESS' | 'QUALITY_INSPECTION' | 'COMPLETED' | 'CLOSED' | 'CANCELLED';

export const PRODUCTION_ORDER_STATUS_OPTIONS: { value: ProductionOrderStatus | ''; label: string }[] = [
  { value: '',                   label: 'All statuses' },
  { value: 'DRAFT',              label: 'Draft' },
  { value: 'PLANNED',            label: 'Planned' },
  { value: 'MATERIAL_PENDING',   label: 'Material pending' },
  { value: 'READY',              label: 'Ready' },
  { value: 'IN_PROGRESS',        label: 'In progress' },
  { value: 'QUALITY_INSPECTION', label: 'Quality inspection' },
  { value: 'COMPLETED',          label: 'Completed' },
  { value: 'CLOSED',             label: 'Closed' },
  { value: 'CANCELLED',          label: 'Cancelled' }
];

export function productionStatusSeverity(status: string): 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast' {
  switch (status) {
    case 'READY':
    case 'COMPLETED':
    case 'CLOSED':             return 'success';
    case 'IN_PROGRESS':
    case 'QUALITY_INSPECTION': return 'info';
    case 'MATERIAL_PENDING':
    case 'PLANNED':            return 'warn';
    case 'CANCELLED':          return 'danger';
    default:                   return 'secondary';
  }
}

export const MATERIAL_READINESS_OPTIONS = ['NOT_CHECKED', 'PARTIAL', 'READY', 'SHORTAGE'] as const;

export function readinessSeverity(readiness: string): 'success' | 'info' | 'warn' | 'danger' | 'secondary' {
  switch (readiness) {
    case 'READY':    return 'success';
    case 'PARTIAL':  return 'warn';
    case 'SHORTAGE': return 'danger';
    default:         return 'secondary';
  }
}

export const PRIORITY_OPTIONS = [
  { value: 0, label: 'Low' },
  { value: 1, label: 'Normal' },
  { value: 2, label: 'High' },
  { value: 3, label: 'Urgent' }
];

export function priorityLabel(priority: number): string {
  return PRIORITY_OPTIONS.find(p => p.value === priority)?.label ?? String(priority);
}

// ── Requests ─────────────────────────────────────────────────────────────────

export interface CreateProductionOrderRequest {
  productUuid: string;
  productVariantUuid?: string;
  plannedQuantity: number;
  warehouseUuid?: string;
  outputWarehouseUuid?: string;
  requiredDate: string;
  plannedStartDate?: string;
  priority: number;
  notes?: string;
  sourceType?: string;
  sourceUuid?: string;
  sourceLineUuid?: string;
  sourceReference?: string;
  plan: boolean;
}

export interface UpdateProductionOrderRequest {
  plannedQuantity?: number;
  warehouseUuid?: string;
  outputWarehouseUuid?: string;
  requiredDate?: string;
  plannedStartDate?: string;
  priority?: number;
  notes?: string;
}

export interface ReportOutputRequest { quantity: number; notes?: string; }
export interface CancelProductionOrderRequest { reason: string; }

export interface ProductionOrderListFilter {
  status?: string;
  productUuid?: string;
  priority?: number;
  dateFrom?: string;
  dateTo?: string;
  search?: string;
  openOnly?: boolean;
  page?: number;
  pageSize?: number;
}

export interface CreateSupplyRequirementRequest {
  variantUuid: string;
  quantityRequired: number;
  warehouseUuid: string;
  requiredDate: string;
  priority: number;
  supplyMethod?: string;
  notes?: string;
  act: boolean;
}

export interface CancelSupplyRequirementRequest { reason: string; }

export interface CreateProductionIssueLineRequest {
  requirementUuid: string;
  materialVariantUuid?: string;
  quantity: number;
  batchNumber?: string;
  notes?: string;
}

export interface CreateProductionIssueRequest {
  issueType: 'STANDARD' | 'ADDITIONAL' | 'RETURN' | 'SCRAP' | 'SUBSTITUTION';
  warehouseUuid?: string;
  notes?: string;
  lines: CreateProductionIssueLineRequest[];
  confirm: boolean;
}

export interface ReverseProductionIssueRequest { reason: string; }

// ── Responses ────────────────────────────────────────────────────────────────

export interface ProductionOrderListItem {
  uuid: string;
  productionNumber: string;
  productUuid: string;
  productName: string;
  productSku: string;
  productVariantUuid: string;
  variantName: string;
  bomNumber: string;
  bomVersion: number;
  plannedQuantity: number;
  producedQuantity: number;
  acceptedQuantity: number;
  rejectedQuantity: number;
  warehouseUuid: string;
  warehouseName: string;
  sourceType: string;
  sourceReference?: string | null;
  parentProductionOrderUuid?: string | null;
  parentProductionNumber?: string | null;
  priority: number;
  requiredDate: string;
  plannedStartDate?: string | null;
  actualStartDate?: string | null;
  actualEndDate?: string | null;
  status: ProductionOrderStatus;
  materialReadiness: string;
  materialCount: number;
  shortMaterialCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface ProductionMaterial {
  uuid: string;
  sequence: number;
  materialProductUuid: string;
  materialProductName: string;
  materialSupplyMethod: string;
  materialVariantUuid: string;
  materialSku: string;
  materialVariantName: string;
  netQuantity: number;
  scrapAllowance: number;
  requiredQuantity: number;
  reservedQuantity: number;
  plannedQuantity: number;
  issuedQuantity: number;
  returnedQuantity: number;
  wastageQuantity: number;
  consumedQuantity: number;
  shortageQuantity: number;
  outstanding: number;
  uom: string;
  warehouseUuid: string;
  warehouseName: string;
  isCritical: boolean;
  status: string;
  requiredDate: string;
  allocationDemandUuid?: string | null;
  isCovered: boolean;
}

export interface SupplyRequirement {
  uuid: string;
  supplyNumber: string;
  productUuid: string;
  productName: string;
  variantUuid: string;
  variantName: string;
  materialSku: string;
  quantityRequired: number;
  quantityOrdered: number;
  quantityReceived: number;
  quantityOutstanding: number;
  demandSourceType: string;
  demandSourceUuid: string;
  demandReference?: string | null;
  supplyMethod: string;
  supplySourceType?: string | null;
  supplySourceUuid?: string | null;
  supplySourceReference?: string | null;
  warehouseUuid: string;
  warehouseName: string;
  requiredDate: string;
  priority: number;
  status: string;
  notes?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface ProductionIssueLine {
  uuid: string;
  requirementUuid: string;
  materialVariantUuid: string;
  materialName: string;
  materialSku: string;
  quantity: number;
  uom: string;
  unitCost: number;
  batchNumber?: string | null;
  notes?: string | null;
}

export interface ProductionIssue {
  uuid: string;
  issueNumber: string;
  productionOrderUuid: string;
  productionNumber: string;
  warehouseUuid: string;
  warehouseName: string;
  issueType: string;
  status: string;
  createdBy: number;
  createdAt: string;
  confirmedBy?: number | null;
  confirmedAt?: string | null;
  reversedAt?: string | null;
  notes?: string | null;
  totalQuantity: number;
  lines: ProductionIssueLine[];
}

export interface ProductionOrderDetail extends ProductionOrderListItem {
  traceId: string;
  bomUuid: string;
  outputWarehouseUuid?: string | null;
  outputWarehouseName?: string | null;
  sourceUuid?: string | null;
  sourceLineUuid?: string | null;
  scrappedQuantity: number;
  notes?: string | null;
  createdBy: number;
  materials: ProductionMaterial[];
  supplyRequirements: SupplyRequirement[];
  issues: ProductionIssue[];
  childOrders: ProductionOrderListItem[];
}

export interface ProductionReadiness {
  productionOrderUuid: string;
  productionNumber: string;
  status: string;
  materialReadiness: string;
  allCriticalCovered: boolean;
  materials: ProductionMaterial[];
  supplyRequirements: SupplyRequirement[];
}

export interface MaterialShortage {
  productionOrderUuid: string;
  productionNumber: string;
  productionStatus: string;
  outputProductName: string;
  priority: number;
  requiredDate: string;
  requirementUuid: string;
  materialVariantUuid: string;
  materialName: string;
  materialSku: string;
  requiredQuantity: number;
  reservedQuantity: number;
  plannedQuantity: number;
  shortageQuantity: number;
  uom: string;
  warehouseUuid: string;
  warehouseName: string;
  isCritical: boolean;
  supplyNumber?: string | null;
  supplyStatus?: string | null;
  supplySourceReference?: string | null;
}

// ── Quality inspection (A30 §18) ───────────────────────────────────────────────

export interface CreateQualityInspectionLineRequest {
  checkName: string;
  /** PASS, FAIL, HOLD or REWORK — the line's whole checked quantity goes to this one outcome. */
  result: 'PASS' | 'FAIL' | 'HOLD' | 'REWORK';
  quantityChecked: number;
  defectCode?: string;
  notes?: string;
}

export interface CreateQualityInspectionRequest {
  lines: CreateQualityInspectionLineRequest[];
  notes?: string;
}

export interface QualityInspectionLine {
  uuid: string;
  checkName: string;
  result: string;
  quantityChecked: number;
  defectCode?: string | null;
  notes?: string | null;
}

export interface QualityInspection {
  uuid: string;
  inspectionNumber: string;
  productionOrderUuid: string;
  productionNumber: string;
  inspectedQuantity: number;
  acceptedQuantity: number;
  rejectedQuantity: number;
  holdQuantity: number;
  reworkQuantity: number;
  overallResult: string;
  inspectedBy: number;
  inspectedAt: string;
  notes?: string | null;
  outstandingForFgr: number;
  lines: QualityInspectionLine[];
}

export function qiResultSeverity(result: string): 'success' | 'info' | 'warn' | 'danger' | 'secondary' {
  switch (result) {
    case 'PASSED':           return 'success';
    case 'PARTIALLY_PASSED': return 'warn';
    case 'REJECTED':         return 'danger';
    default:                 return 'secondary';
  }
}

// ── Finished goods receipt (A30 §19) ─────────────────────────────────────────────

export interface CreateFinishedGoodsReceiptRequest {
  quantity: number;
  warehouseUuid?: string;
  notes?: string;
  confirm: boolean;
}

export interface FinishedGoodsReceipt {
  uuid: string;
  fgrNumber: string;
  productionOrderUuid: string;
  productionNumber: string;
  qualityInspectionUuid: string;
  warehouseUuid: string;
  warehouseName: string;
  totalQuantity: number;
  status: string;
  receivedBy: number;
  receivedAt: string;
  notes?: string | null;
}

// ── Production ledger (A30 §19A) ──────────────────────────────────────────────

export interface ProductionLedgerEntry {
  productionOrderUuid: string;
  productionNumber: string;
  entryType: 'DEBIT' | 'CREDIT';
  variantUuid: string;
  productName: string;
  variantName: string;
  quantity: number;
  uom: string;
  warehouseUuid: string;
  warehouseName: string;
  sourceDocumentType: string;
  sourceDocumentNumber: string;
  movementType: string;
  transactionDate: string;
  notes?: string | null;
}

export interface ProductionLedgerSummary {
  materialsConsumedCount: number;
  finishedGoodsQuantity: number;
  scrapQuantity: number;
  yieldPercent?: number | null;
}

export interface ProductionLedger {
  productionOrderUuid: string;
  productionNumber: string;
  summary: ProductionLedgerSummary;
  entries: ProductionLedgerEntry[];
}

export interface ProductionLedgerListFilter {
  productionOrderUuid?: string;
  variantUuid?: string;
  entryType?: string;
  movementType?: string;
  dateFrom?: string;
  dateTo?: string;
  page?: number;
  pageSize?: number;
}

export interface SupplyRequirementListFilter {
  status?: string;
  supplyMethod?: string;
  variantUuid?: string;
  productUuid?: string;
  demandSourceType?: string;
  demandSourceUuid?: string;
  search?: string;
  openOnly?: boolean;
  page?: number;
  pageSize?: number;
}

@Injectable({ providedIn: 'root' })
export class ProductionOrderService {
  private readonly base = `${environment.apiUrl}/production-orders`;
  private readonly issuesBase = `${environment.apiUrl}/production-issues`;
  private readonly supplyBase = `${environment.apiUrl}/supply-requirements`;
  private readonly qiBase = `${environment.apiUrl}/quality-inspections`;
  private readonly fgrBase = `${environment.apiUrl}/fgr`;
  private readonly ledgerBase = `${environment.apiUrl}/production-ledger`;

  constructor(private http: HttpClient) {}

  // ── Production orders ───────────────────────────────────────────────────────

  create(req: CreateProductionOrderRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(this.base, req);
  }

  getList(filter: ProductionOrderListFilter = {}): Observable<ApiResponse<PaginatedResponse<ProductionOrderListItem>>> {
    let params = new HttpParams();
    if (filter.status)      params = params.set('status', filter.status);
    if (filter.productUuid) params = params.set('productUuid', filter.productUuid);
    if (filter.priority !== undefined && filter.priority !== null) params = params.set('priority', String(filter.priority));
    if (filter.dateFrom)    params = params.set('dateFrom', filter.dateFrom);
    if (filter.dateTo)      params = params.set('dateTo', filter.dateTo);
    if (filter.search)      params = params.set('search', filter.search);
    if (filter.openOnly)    params = params.set('openOnly', 'true');
    if (filter.page)        params = params.set('page', String(filter.page));
    if (filter.pageSize)    params = params.set('pageSize', String(filter.pageSize));
    return this.http.get<ApiResponse<PaginatedResponse<ProductionOrderListItem>>>(this.base, { params });
  }

  getById(uuid: string): Observable<ApiResponse<ProductionOrderDetail>> {
    return this.http.get<ApiResponse<ProductionOrderDetail>>(`${this.base}/${uuid}`);
  }

  update(uuid: string, req: UpdateProductionOrderRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.base}/${uuid}`, req);
  }

  plan(uuid: string): Observable<ApiResponse<ProductionReadiness>> {
    return this.http.post<ApiResponse<ProductionReadiness>>(`${this.base}/${uuid}/plan`, {});
  }

  start(uuid: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.base}/${uuid}/start`, {});
  }

  reportOutput(uuid: string, req: ReportOutputRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.base}/${uuid}/report-output`, req);
  }

  complete(uuid: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.base}/${uuid}/complete`, {});
  }

  cancel(uuid: string, req: CancelProductionOrderRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.base}/${uuid}/cancel`, req);
  }

  getMaterials(uuid: string): Observable<ApiResponse<ProductionMaterial[]>> {
    return this.http.get<ApiResponse<ProductionMaterial[]>>(`${this.base}/${uuid}/materials`);
  }

  getReadiness(uuid: string): Observable<ApiResponse<ProductionReadiness>> {
    return this.http.get<ApiResponse<ProductionReadiness>>(`${this.base}/${uuid}/readiness`);
  }

  getShortages(warehouseUuid?: string): Observable<ApiResponse<MaterialShortage[]>> {
    let params = new HttpParams();
    if (warehouseUuid) params = params.set('warehouseUuid', warehouseUuid);
    return this.http.get<ApiResponse<MaterialShortage[]>>(`${this.base}/shortages`, { params });
  }

  getSupplyRequirementsForOrder(uuid: string): Observable<ApiResponse<SupplyRequirement[]>> {
    return this.http.get<ApiResponse<SupplyRequirement[]>>(`${this.base}/${uuid}/supply-requirements`);
  }

  getIssuesForOrder(uuid: string): Observable<ApiResponse<ProductionIssue[]>> {
    return this.http.get<ApiResponse<ProductionIssue[]>>(`${this.base}/${uuid}/issues`);
  }

  createIssue(productionOrderUuid: string, req: CreateProductionIssueRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.base}/${productionOrderUuid}/issues`, req);
  }

  // ── Production material issues ──────────────────────────────────────────────

  getIssue(uuid: string): Observable<ApiResponse<ProductionIssue>> {
    return this.http.get<ApiResponse<ProductionIssue>>(`${this.issuesBase}/${uuid}`);
  }

  confirmIssue(uuid: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.issuesBase}/${uuid}/confirm`, {});
  }

  reverseIssue(uuid: string, req: ReverseProductionIssueRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.issuesBase}/${uuid}/reverse`, req);
  }

  // ── Supply requirements ──────────────────────────────────────────────────────

  getSupplyRequirements(filter: SupplyRequirementListFilter = {}): Observable<ApiResponse<PaginatedResponse<SupplyRequirement>>> {
    let params = new HttpParams();
    if (filter.status)           params = params.set('status', filter.status);
    if (filter.supplyMethod)     params = params.set('supplyMethod', filter.supplyMethod);
    if (filter.variantUuid)      params = params.set('variantUuid', filter.variantUuid);
    if (filter.productUuid)      params = params.set('productUuid', filter.productUuid);
    if (filter.demandSourceType) params = params.set('demandSourceType', filter.demandSourceType);
    if (filter.demandSourceUuid) params = params.set('demandSourceUuid', filter.demandSourceUuid);
    if (filter.search)           params = params.set('search', filter.search);
    if (filter.openOnly)         params = params.set('openOnly', 'true');
    if (filter.page)             params = params.set('page', String(filter.page));
    if (filter.pageSize)         params = params.set('pageSize', String(filter.pageSize));
    return this.http.get<ApiResponse<PaginatedResponse<SupplyRequirement>>>(this.supplyBase, { params });
  }

  getSupplyRequirement(uuid: string): Observable<ApiResponse<SupplyRequirement>> {
    return this.http.get<ApiResponse<SupplyRequirement>>(`${this.supplyBase}/${uuid}`);
  }

  createSupplyRequirement(req: CreateSupplyRequirementRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(this.supplyBase, req);
  }

  cancelSupplyRequirement(uuid: string, req: CancelSupplyRequirementRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.supplyBase}/${uuid}/cancel`, req);
  }

  // ── Quality inspection ───────────────────────────────────────────────────────

  createInspection(productionOrderUuid: string, req: CreateQualityInspectionRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.base}/${productionOrderUuid}/quality-inspections`, req);
  }

  getInspection(uuid: string): Observable<ApiResponse<QualityInspection>> {
    return this.http.get<ApiResponse<QualityInspection>>(`${this.qiBase}/${uuid}`);
  }

  getInspectionForOrder(productionOrderUuid: string): Observable<ApiResponse<QualityInspection | null>> {
    return this.http.get<ApiResponse<QualityInspection | null>>(`${this.base}/${productionOrderUuid}/quality-inspection`);
  }

  // ── Finished goods receipt ───────────────────────────────────────────────────

  createFgr(productionOrderUuid: string, req: CreateFinishedGoodsReceiptRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.base}/${productionOrderUuid}/fgr`, req);
  }

  getFgrsForOrder(productionOrderUuid: string): Observable<ApiResponse<FinishedGoodsReceipt[]>> {
    return this.http.get<ApiResponse<FinishedGoodsReceipt[]>>(`${this.base}/${productionOrderUuid}/fgr`);
  }

  confirmFgr(uuid: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.fgrBase}/${uuid}/confirm`, {});
  }

  // ── Production ledger ────────────────────────────────────────────────────────

  getLedgerForOrder(productionOrderUuid: string): Observable<ApiResponse<ProductionLedger>> {
    return this.http.get<ApiResponse<ProductionLedger>>(`${this.base}/${productionOrderUuid}/ledger`);
  }

  getLedgerList(filter: ProductionLedgerListFilter = {}): Observable<ApiResponse<PaginatedResponse<ProductionLedgerEntry>>> {
    let params = new HttpParams();
    if (filter.productionOrderUuid) params = params.set('productionOrderUuid', filter.productionOrderUuid);
    if (filter.variantUuid)         params = params.set('variantUuid', filter.variantUuid);
    if (filter.entryType)           params = params.set('entryType', filter.entryType);
    if (filter.movementType)        params = params.set('movementType', filter.movementType);
    if (filter.dateFrom)            params = params.set('dateFrom', filter.dateFrom);
    if (filter.dateTo)              params = params.set('dateTo', filter.dateTo);
    if (filter.page)                params = params.set('page', String(filter.page));
    if (filter.pageSize)            params = params.set('pageSize', String(filter.pageSize));
    return this.http.get<ApiResponse<PaginatedResponse<ProductionLedgerEntry>>>(this.ledgerBase, { params });
  }

  getLedgerSummary(filter: ProductionLedgerListFilter = {}): Observable<ApiResponse<ProductionLedgerSummary>> {
    let params = new HttpParams();
    if (filter.productionOrderUuid) params = params.set('productionOrderUuid', filter.productionOrderUuid);
    return this.http.get<ApiResponse<ProductionLedgerSummary>>(`${this.ledgerBase}/summary`, { params });
  }
}

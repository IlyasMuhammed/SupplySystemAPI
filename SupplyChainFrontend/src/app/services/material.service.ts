import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse, PaginatedResponse } from './demand.service';

const BASE = 'https://localhost:51800/api';

// ── Projects ──────────────────────────────────────────────────────────────────

export interface CreateProjectRequest {
  projectCode: string;
  projectName: string;
  projectManagerId?: number;
  siteWarehouseId?: string;
  budgetAmount?: number;
  description?: string;
}

export interface PatchProjectRequest {
  projectName?: string;
  projectManagerId?: number;
  siteWarehouseId?: string;
  clearWarehouse?: boolean;
  budgetAmount?: number;
  description?: string;
  status?: string;
}

export interface ProjectListFilter {
  status?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

export interface ProjectListItem {
  uuid: string;
  projectCode: string;
  projectName: string;
  projectManagerId?: number;
  status: string;
  budgetAmount?: number;
  createdDate: string;
}

export interface ProjectDetail {
  uuid: string;
  projectCode: string;
  projectName: string;
  projectManagerId?: number;
  siteWarehouseId?: string;
  status: string;
  budgetAmount?: number;
  description?: string;
  createdBy: number;
  createdDate: string;
}

// ── Material Issue Requests ───────────────────────────────────────────────────

export interface CreateMirLineRequest {
  productUuid: string;
  requestedQty: number;
  warehouseId?: number;
  purpose?: string;
  notes?: string;
}

export interface CreateMirRequest {
  requestType: string;
  projectUuid?: string;
  department?: string;
  maintenanceRef?: string;
  requiredDate?: string;
  priority: string;
  purpose?: string;
  notes?: string;
  lines: CreateMirLineRequest[];
}

export interface PatchMirRequest {
  projectUuid?: string;
  department?: string;
  maintenanceRef?: string;
  requiredDate?: string;
  priority?: string;
  purpose?: string;
  notes?: string;
  lines?: CreateMirLineRequest[];
}

export interface ApproveMirRequest { remarks?: string; }
export interface RejectMirRequest  { reason: string; }

export interface MirListFilter {
  status?: string;
  requestType?: string;
  projectUuid?: string;
  department?: string;
  dateFrom?: string;
  dateTo?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

export interface MirListItem {
  uuid: string;
  requestNo: string;
  requestType: string;
  projectName?: string;
  department?: string;
  maintenanceRef?: string;
  status: string;
  priority: string;
  estimatedValue: number;
  requiredDate?: string;
  createdDate: string;
  totalLines: number;
}

export interface MirLine {
  uuid: string;
  lineNo: number;
  productUuid: string;
  itemDescription: string;
  unitOfMeasure?: string;
  requestedQty: number;
  unitCost: number;
  estimatedLineValue: number;
  warehouseId?: number;
  warehouseName?: string;
  purpose?: string;
  notes?: string;
  latestApprovedQty?: number;
}

export interface MirDetail {
  uuid: string;
  requestNo: string;
  requestType: string;
  projectUuid?: string;
  projectName?: string;
  department?: string;
  maintenanceRef?: string;
  requestedBy: number;
  requiredDate?: string;
  priority: string;
  purpose?: string;
  status: string;
  estimatedValue: number;
  rejectionReason?: string;
  approverRemarks?: string;
  approvedBy?: number;
  approvedAt?: string;
  notes?: string;
  createdBy: number;
  createdDate: string;
  lines: MirLine[];
  // Workflow fields — populated when status is PENDING_APPROVAL
  activeApprovalUuid?: string;
  activeStepNumber?: number;
  activeStepName?: string;
}

export interface MirLineApprovalInput {
  lineUuid: string;
  approvedQty: number;
}

export interface MirWorkflowApproveRequest {
  approvalUUID: string;
  remarks?: string;
  lineApprovals: MirLineApprovalInput[];
}

export interface MirWorkflowRejectRequest {
  approvalUUID: string;
  reason: string;
}

export interface MirLineAvailability {
  lineUuid: string;
  productUuid: string;
  itemDescription: string;
  requestedQty: number;
  latestApprovedQty?: number;
  qtyOnHand: number;
  qtyReserved: number;
  qtyAvailable: number;
  reorderPoint?: number;
  warehouseName?: string;
  isAvailable: boolean;
}

export interface MirStockAvailabilityResponse {
  warehouseUuid?: string;
  warehouseName?: string;
  lines: MirLineAvailability[];
}

@Injectable({ providedIn: 'root' })
export class MaterialService {
  constructor(private http: HttpClient) {}

  // Projects

  createProject(req: CreateProjectRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/projects`, req);
  }

  getProjects(filter: ProjectListFilter = {}): Observable<ApiResponse<PaginatedResponse<ProjectListItem>>> {
    let params = new HttpParams();
    if (filter.status)   params = params.set('status',   filter.status);
    if (filter.search)   params = params.set('search',   filter.search);
    if (filter.page)     params = params.set('page',     filter.page.toString());
    if (filter.pageSize) params = params.set('pageSize', filter.pageSize.toString());
    return this.http.get<ApiResponse<PaginatedResponse<ProjectListItem>>>(`${BASE}/projects`, { params });
  }

  getProject(uuid: string): Observable<ApiResponse<ProjectDetail>> {
    return this.http.get<ApiResponse<ProjectDetail>>(`${BASE}/projects/${uuid}`);
  }

  patchProject(uuid: string, req: PatchProjectRequest): Observable<ApiResponse<null>> {
    return this.http.patch<ApiResponse<null>>(`${BASE}/projects/${uuid}`, req);
  }

  deleteProject(uuid: string): Observable<ApiResponse<null>> {
    return this.http.delete<ApiResponse<null>>(`${BASE}/projects/${uuid}`);
  }

  // Material Issue Requests

  createMir(req: CreateMirRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/material-issue-requests`, req);
  }

  getMirs(filter: MirListFilter = {}): Observable<ApiResponse<PaginatedResponse<MirListItem>>> {
    let params = new HttpParams();
    if (filter.status)      params = params.set('status',      filter.status);
    if (filter.requestType) params = params.set('requestType', filter.requestType);
    if (filter.projectUuid) params = params.set('projectUuid', filter.projectUuid);
    if (filter.department)  params = params.set('department',  filter.department);
    if (filter.dateFrom)    params = params.set('dateFrom',    filter.dateFrom);
    if (filter.dateTo)      params = params.set('dateTo',      filter.dateTo);
    if (filter.search)      params = params.set('search',      filter.search);
    if (filter.page)        params = params.set('page',        filter.page.toString());
    if (filter.pageSize)    params = params.set('pageSize',    filter.pageSize.toString());
    return this.http.get<ApiResponse<PaginatedResponse<MirListItem>>>(`${BASE}/material-issue-requests`, { params });
  }

  getMir(uuid: string): Observable<ApiResponse<MirDetail>> {
    return this.http.get<ApiResponse<MirDetail>>(`${BASE}/material-issue-requests/${uuid}`);
  }

  patchMir(uuid: string, req: PatchMirRequest): Observable<ApiResponse<null>> {
    return this.http.patch<ApiResponse<null>>(`${BASE}/material-issue-requests/${uuid}`, req);
  }

  deleteMir(uuid: string): Observable<ApiResponse<null>> {
    return this.http.delete<ApiResponse<null>>(`${BASE}/material-issue-requests/${uuid}`);
  }

  submitMir(uuid: string): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/material-issue-requests/${uuid}/workflow/submit`, {});
  }

  workflowApproveMir(uuid: string, req: MirWorkflowApproveRequest): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/material-issue-requests/${uuid}/workflow/approve`, req);
  }

  workflowRejectMir(uuid: string, req: MirWorkflowRejectRequest): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/material-issue-requests/${uuid}/workflow/reject`, req);
  }

  issueMir(uuid: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/material-issue-requests/${uuid}/issue`, {});
  }

  cancelMir(uuid: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/material-issue-requests/${uuid}/cancel`, {});
  }

  getMirStockAvailability(uuid: string): Observable<ApiResponse<MirStockAvailabilityResponse>> {
    return this.http.get<ApiResponse<MirStockAvailabilityResponse>>(
      `${BASE}/material-issue-requests/${uuid}/workflow/stock-availability`
    );
  }

  // Material Issue Vouchers

  getMivIssuable(mirUuid: string): Observable<ApiResponse<MirIssuableResponse>> {
    return this.http.get<ApiResponse<MirIssuableResponse>>(
      `${BASE}/material-issue-vouchers/mir-issuable/${mirUuid}`
    );
  }

  createMiv(req: CreateMivRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/material-issue-vouchers`, req);
  }

  getMivs(filter: MivListFilter = {}): Observable<ApiResponse<PaginatedResponse<MivListItem>>> {
    let params = new HttpParams();
    if (filter.status)   params = params.set('status',   filter.status);
    if (filter.mirUuid)  params = params.set('mirUuid',  filter.mirUuid);
    if (filter.search)   params = params.set('search',   filter.search);
    if (filter.page)     params = params.set('page',     filter.page.toString());
    if (filter.pageSize) params = params.set('pageSize', filter.pageSize.toString());
    return this.http.get<ApiResponse<PaginatedResponse<MivListItem>>>(`${BASE}/material-issue-vouchers`, { params });
  }

  getMiv(uuid: string): Observable<ApiResponse<MivDetail>> {
    return this.http.get<ApiResponse<MivDetail>>(`${BASE}/material-issue-vouchers/${uuid}`);
  }

  postMiv(uuid: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/material-issue-vouchers/${uuid}/post`, {});
  }

  cancelMiv(uuid: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/material-issue-vouchers/${uuid}/cancel`, {});
  }

  // Batch / Serial

  getAvailableBatches(productUuid: string, warehouseUuid?: string): Observable<ApiResponse<AvailableBatchesResponse>> {
    let params = new HttpParams();
    if (warehouseUuid) params = params.set('warehouseUuid', warehouseUuid);
    return this.http.get<ApiResponse<AvailableBatchesResponse>>(
      `${BASE}/batch-serials/available/${productUuid}`, { params }
    );
  }

  traceBatchSerial(reference: string, productUuid?: string): Observable<ApiResponse<ChainOfCustodyResponse>> {
    let params = new HttpParams().set('reference', reference);
    if (productUuid) params = params.set('productUuid', productUuid);
    return this.http.get<ApiResponse<ChainOfCustodyResponse>>(`${BASE}/batch-serials/trace`, { params });
  }

  // Cost Ledger

  getProjectCostLedger(projectUuid: string, filter: CostLedgerFilter = {}): Observable<ApiResponse<PaginatedResponse<CostLedgerEntry>>> {
    let params = new HttpParams();
    if (filter.department)      params = params.set('department',      filter.department);
    if (filter.productUuid)     params = params.set('productUuid',     filter.productUuid);
    if (filter.dateFrom)        params = params.set('dateFrom',        filter.dateFrom);
    if (filter.dateTo)          params = params.set('dateTo',          filter.dateTo);
    if (filter.transactionType) params = params.set('transactionType', filter.transactionType);
    if (filter.page)            params = params.set('page',            filter.page.toString());
    if (filter.pageSize)        params = params.set('pageSize',        filter.pageSize.toString());
    return this.http.get<ApiResponse<PaginatedResponse<CostLedgerEntry>>>(
      `${BASE}/projects/${projectUuid}/cost-ledger`, { params }
    );
  }

  getDepartmentCostLedger(filter: CostLedgerFilter = {}): Observable<ApiResponse<PaginatedResponse<CostLedgerEntry>>> {
    let params = new HttpParams();
    if (filter.department)      params = params.set('department',      filter.department);
    if (filter.productUuid)     params = params.set('productUuid',     filter.productUuid);
    if (filter.dateFrom)        params = params.set('dateFrom',        filter.dateFrom);
    if (filter.dateTo)          params = params.set('dateTo',          filter.dateTo);
    if (filter.transactionType) params = params.set('transactionType', filter.transactionType);
    if (filter.page)            params = params.set('page',            filter.page.toString());
    if (filter.pageSize)        params = params.set('pageSize',        filter.pageSize.toString());
    return this.http.get<ApiResponse<PaginatedResponse<CostLedgerEntry>>>(
      `${BASE}/departments/cost-ledger`, { params }
    );
  }

  // ── Material Returns ─────────────────────────────────────────────────────────

  getMivReturnable(mivUuid: string): Observable<ApiResponse<MivReturnableResponse>> {
    return this.http.get<ApiResponse<MivReturnableResponse>>(`${BASE}/material-returns/returnable/${mivUuid}`);
  }

  createReturn(req: CreateReturnRequest): Observable<ApiResponse<{ uuid: string }>> {
    return this.http.post<ApiResponse<{ uuid: string }>>(`${BASE}/material-returns`, req);
  }

  getReturns(filter: ReturnListFilter = {}): Observable<ApiResponse<PaginatedResponse<ReturnListItem>>> {
    let params = new HttpParams();
    if (filter.mivUuid)  params = params.set('mivUuid',  filter.mivUuid);
    if (filter.status)   params = params.set('status',   filter.status);
    if (filter.page)     params = params.set('page',     filter.page.toString());
    if (filter.pageSize) params = params.set('pageSize', filter.pageSize.toString());
    return this.http.get<ApiResponse<PaginatedResponse<ReturnListItem>>>(`${BASE}/material-returns`, { params });
  }

  getReturn(uuid: string): Observable<ApiResponse<ReturnDetail>> {
    return this.http.get<ApiResponse<ReturnDetail>>(`${BASE}/material-returns/${uuid}`);
  }

  postReturn(uuid: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/material-returns/${uuid}/post`, {});
  }

  cancelReturn(uuid: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/material-returns/${uuid}/cancel`, {});
  }

  // ── Wastage ──────────────────────────────────────────────────────────────────

  createWastage(req: CreateWastageRequest): Observable<ApiResponse<{ uuid: string }>> {
    return this.http.post<ApiResponse<{ uuid: string }>>(`${BASE}/wastage`, req);
  }

  getWastages(filter: WastageListFilter = {}): Observable<ApiResponse<PaginatedResponse<WastageListItem>>> {
    let params = new HttpParams();
    if (filter.status)      params = params.set('status',      filter.status);
    if (filter.sourceType)  params = params.set('sourceType',  filter.sourceType);
    if (filter.productUuid) params = params.set('productUuid', filter.productUuid);
    if (filter.dateFrom)    params = params.set('dateFrom',    filter.dateFrom);
    if (filter.dateTo)      params = params.set('dateTo',      filter.dateTo);
    if (filter.page)        params = params.set('page',        filter.page.toString());
    if (filter.pageSize)    params = params.set('pageSize',    filter.pageSize.toString());
    return this.http.get<ApiResponse<PaginatedResponse<WastageListItem>>>(`${BASE}/wastage`, { params });
  }

  getWastage(uuid: string): Observable<ApiResponse<WastageDetail>> {
    return this.http.get<ApiResponse<WastageDetail>>(`${BASE}/wastage/${uuid}`);
  }

  approveWastage(uuid: string, notes?: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/wastage/${uuid}/approve`, { notes });
  }

  rejectWastage(uuid: string, reason: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/wastage/${uuid}/reject`, { reason });
  }

  // ── Material Consumptions ─────────────────────────────────────────────────────

  createConsumption(req: CreateConsumptionRequest): Observable<ApiResponse<{ uuid: string }>> {
    return this.http.post<ApiResponse<{ uuid: string }>>(`${BASE}/material-consumptions`, req);
  }

  getConsumptions(filter: ConsumptionListFilter = {}): Observable<ApiResponse<PaginatedResponse<ConsumptionListItem>>> {
    let params = new HttpParams();
    if (filter.mirUuid)    params = params.set('mirUuid',    filter.mirUuid);
    if (filter.sourceType) params = params.set('sourceType', filter.sourceType);
    if (filter.page)       params = params.set('page',       filter.page.toString());
    if (filter.pageSize)   params = params.set('pageSize',   filter.pageSize.toString());
    return this.http.get<ApiResponse<PaginatedResponse<ConsumptionListItem>>>(`${BASE}/material-consumptions`, { params });
  }

  getConsumptionRegister(mirUuid: string): Observable<ApiResponse<ConsumptionRegisterResponse>> {
    return this.http.get<ApiResponse<ConsumptionRegisterResponse>>(`${BASE}/material-consumptions/register/${mirUuid}`);
  }
}

// ── MIV types ─────────────────────────────────────────────────────────────────

export interface MirLineIssuable {
  lineUuid: string;
  productUuid: string;
  itemDescription: string;
  unitOfMeasure?: string;
  requestedQty: number;
  approvedQty: number;
  issuedQty: number;
  pendingQty: number;
  lineStatus: string;
  unitCost: number;
  availableToIssue: number;
  isBatchTracked: boolean;
  isSerialTracked: boolean;
}

export interface MirIssuableResponse {
  mirUuid: string;
  requestNo: string;
  status: string;
  projectName?: string;
  department?: string;
  lines: MirLineIssuable[];
}

export interface BatchSelectionInput {
  inventoryItemId: number;
  issuedQty: number;
}

export interface SerialSelectionInput {
  inventoryItemId: number;
}

export interface CreateMivLineRequest {
  mirLineUuid: string;
  issuedQty: number;
  notes?: string;
  batchSelections: BatchSelectionInput[];
  serialSelections: SerialSelectionInput[];
}

export interface CreateMivRequest {
  mirUuid: string;
  issuedTo?: string;
  issueDate?: string;
  notes?: string;
  lines: CreateMivLineRequest[];
}

export interface MivListFilter {
  status?: string;
  mirUuid?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

export interface MivListItem {
  uuid: string;
  issueNo: string;
  mirRequestNo: string;
  mirUuid: string;
  status: string;
  issuedTo?: string;
  issueDate: string;
  totalValue: number;
  totalLines: number;
  createdDate: string;
}

export interface MivLineBatchSerial {
  uuid: string;
  inventoryItemId: number;
  batchNumber?: string;
  serialNumber?: string;
  expiryDate?: string;
  issuedQty: number;
  unitCost: number;
}

export interface MivLine {
  uuid: string;
  mirLineUuid: string;
  productUuid: string;
  itemDescription: string;
  unitOfMeasure?: string;
  issuedQty: number;
  unitCost: number;
  lineValue: number;
  notes?: string;
  warehouseName?: string;
  batchSerials: MivLineBatchSerial[];
}

export interface MivDetail {
  uuid: string;
  issueNo: string;
  mirUuid: string;
  mirRequestNo: string;
  mirProjectName?: string;
  status: string;
  issuedTo?: string;
  issueDate: string;
  totalValue: number;
  notes?: string;
  createdBy: number;
  createdDate: string;
  postedBy?: number;
  postedDate?: string;
  lines: MivLine[];
}

// ── Batch / Serial types ──────────────────────────────────────────────────────

export interface AvailableBatchRow {
  inventoryItemId: number;
  batchNumber: string;
  expiryDate?: string;
  qtyOnHand: number;
  qtyReserved: number;
  qtyAvailable: number;
  unitCost: number;
  warehouseId: number;
  warehouseName: string;
}

export interface AvailableSerialRow {
  inventoryItemId: number;
  serialNumber: string;
  unitCost: number;
  warehouseId: number;
  warehouseName: string;
}

export interface AvailableBatchesResponse {
  productUuid: string;
  productName: string;
  productSku: string;
  isBatchTracked: boolean;
  isSerialTracked: boolean;
  batches: AvailableBatchRow[];
  serials: AvailableSerialRow[];
}

// ── Chain-of-custody types ────────────────────────────────────────────────────

export interface GrnReceiptInfo {
  grnNumber?: string;
  receivedDate?: string;
  supplierName?: string;
  receivedQty?: number;
  batchNumber?: string;
  serialNumber?: string;
}

export interface InventoryLocationInfo {
  inventoryItemId: number;
  warehouseName: string;
  zoneName?: string;
  binCode?: string;
  qtyOnHand: number;
  qtyReserved: number;
}

export interface IssueEventInfo {
  issueNo: string;
  issueDate: string;
  mirRequestNo: string;
  projectName?: string;
  department?: string;
  issuedTo?: string;
  issuedQty: number;
  mivStatus: string;
}

export interface ChainOfCustodyResponse {
  referenceType: string;
  reference: string;
  productUuid?: string;
  productName?: string;
  productSku?: string;
  grnReceipt?: GrnReceiptInfo;
  currentLocation?: InventoryLocationInfo;
  issueEvents: IssueEventInfo[];
}

// ── Cost Ledger types ─────────────────────────────────────────────────────────

export interface CostLedgerFilter {
  department?: string;
  productUuid?: string;
  dateFrom?: string;
  dateTo?: string;
  transactionType?: string;
  page?: number;
  pageSize?: number;
}

export interface CostLedgerEntry {
  uuid: string;
  // Project ledger fields
  projectUuid?: string;
  projectName?: string;
  // Department ledger fields
  department?: string;
  costCenter?: string;
  // Common
  productUuid: string;
  itemDescription: string;
  transactionType: string;
  referenceType: string;
  referenceId: string;
  referenceNumber: string;
  quantity: number;
  unitCost: number;
  amount: number;
  postedDate: string;
  postedBy: number;
  notes?: string;
  createdDate: string;
}

// ── Material Return types ─────────────────────────────────────────────────────

export interface CreateReturnLineRequest {
  mivLineUuid: string;
  returnedQty: number;
  condition: 'GOOD' | 'DAMAGED';
  reason?: string;
  notes?: string;
  inventoryItemId?: number;
}

export interface CreateReturnRequest {
  mivUuid: string;
  returnDate?: string;
  notes?: string;
  lines: CreateReturnLineRequest[];
}

export interface ReturnableBatchSerial {
  inventoryItemId: number;
  batchNumber?: string;
  serialNumber?: string;
  expiryDate?: string;
  issuedQty: number;
  unitCost: number;
}

export interface ReturnableLine {
  lineUuid: string;
  productUuid: string;
  itemDescription: string;
  unitOfMeasure?: string;
  issuedQty: number;
  alreadyReturned: number;
  returnableQty: number;
  unitCost: number;
  inventoryItemId: number;
  batchSerials: ReturnableBatchSerial[];
}

export interface MivReturnableResponse {
  mivUuid: string;
  issueNo: string;
  mivStatus: string;
  lines: ReturnableLine[];
}

export interface ReturnListFilter {
  mivUuid?: string;
  status?: string;
  page?: number;
  pageSize?: number;
}

export interface ReturnListItem {
  uuid: string;
  returnNo: string;
  issueNo: string;
  status: string;
  returnDate: string;
  lineCount: number;
  totalValue: number;
  createdDate: string;
}

export interface ReturnDetailLine {
  uuid: string;
  productUuid: string;
  itemDescription: string;
  unitOfMeasure?: string;
  returnedQty: number;
  condition: string;
  reason?: string;
  unitCost: number;
  lineValue: number;
  notes?: string;
}

export interface ReturnDetail {
  uuid: string;
  returnNo: string;
  status: string;
  mivUuid: string;
  issueNo: string;
  returnDate: string;
  notes?: string;
  createdBy: number;
  createdDate: string;
  postedBy?: number;
  postedDate?: string;
  lines: ReturnDetailLine[];
}

// ── Consumption types ─────────────────────────────────────────────────────────

export interface CreateConsumptionRequest {
  mivLineUuid: string;
  consumedQty: number;
  notes?: string;
}

export interface ConsumptionListFilter {
  mirUuid?: string;
  sourceType?: string;  // DIRECT | MANUAL
  page?: number;
  pageSize?: number;
}

export interface ConsumptionListItem {
  uuid: string;
  consumptionNo: string;
  itemDescription: string;
  unitOfMeasure?: string;
  consumedQty: number;
  sourceType: string;
  consumedDate: string;
  notes?: string;
}

export interface ConsumptionRegisterItem {
  productUuid: string;
  itemDescription: string;
  unitOfMeasure?: string;
  issuedQty: number;
  consumedQty: number;
  returnedQty: number;
  wastedQty: number;
  balanceQty: number;
  unitCost: number;
  balanceValue: number;
}

export interface ConsumptionRegisterResponse {
  mirUuid: string;
  requestNo: string;
  lines: ConsumptionRegisterItem[];
}

// ── Wastage types ─────────────────────────────────────────────────────────────

export interface CreateWastageRequest {
  mivLineUuid: string;
  wastedQty: number;
  reason: string;
  notes?: string;
}

export interface WastageListFilter {
  status?: string;
  sourceType?: string;
  productUuid?: string;
  dateFrom?: string;
  dateTo?: string;
  page?: number;
  pageSize?: number;
}

export interface WastageListItem {
  uuid: string;
  wastageNo: string;
  sourceType: string;
  productUuid: string;
  itemDescription: string;
  wastedQty: number;
  amount: number;
  reason: string;
  status: string;
  recordedBy: number;
  createdDate: string;
}

export interface WastageDetail {
  uuid: string;
  wastageNo: string;
  sourceType: string;
  sourceUuid?: string;
  mivUuid: string;
  issueNo: string;
  productUuid: string;
  itemDescription: string;
  unitOfMeasure?: string;
  wastedQty: number;
  unitCost: number;
  amount: number;
  reason: string;
  status: string;
  recordedBy: number;
  approvedBy?: number;
  approvedAt?: string;
  rejectedBy?: number;
  rejectedAt?: string;
  rejectionReason?: string;
  notes?: string;
  createdDate: string;
}

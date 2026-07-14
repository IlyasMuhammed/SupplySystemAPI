import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

const BASE = 'https://localhost:51800/api/reports';

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

// ── Filter types ──────────────────────────────────────────────────────────────

export interface ReportDateFilter {
  dateFrom?: string;
  dateTo?:   string;
  search?:   string;
  page?:     number;
  pageSize?: number;
}

export interface AuditLogFilter {
  module?:     string;
  action?:     string;
  entityType?: string;
  entityId?:   string;
  userId?:     number;
  dateFrom?:   string;
  dateTo?:     string;
  page?:       number;
  pageSize?:   number;
}

// ── KPI Dashboard ─────────────────────────────────────────────────────────────

export interface KpiDashboardModel {
  poCycleTimeDays:           number;
  supplierOnTimeDeliveryRate: number;
  poFillRate:                number;
  stockTurnoverRatio:        number;
  inventoryAccuracy:         number;
  invoiceProcessingTimeDays: number;
  threeWayMatchRate:         number;
  budgetVariancePercent:     number;
  grnRejectionRate:          number;
  reorderTriggerCount:       number;
}

// ── Procurement ───────────────────────────────────────────────────────────────

export interface SupplierPerformanceItem {
  supplierId:         string;
  supplierName:       string;
  poCount:            number;
  totalSpend:         number;
  onTimeDeliveryRate: number;
  qualityScore:       number;
  grnCount:           number;
  rejectedGrnCount:   number;
}

export interface PoSummaryItem {
  status:     string;
  count:      number;
  totalValue: number;
}

export interface SpendBySupplierItem {
  supplierId:   string;
  supplierName: string;
  poCount:      number;
  totalSpend:   number;
  latestPoDate: string | null;
}

export interface PendingApprovalItem {
  module:       string;
  entityType:   string;
  entityNumber: string;
  entityUuid:   string;
  status:       string;
  createdDate:  string;
  description:  string | null;
}

// ── Inventory ─────────────────────────────────────────────────────────────────

export interface StockLevelItem {
  productName:   string;
  sku:           string | null;
  warehouseName: string | null;
  qtyOnHand:     number;
  qtyReserved:   number;
  qtyAvailable:  number;
  unitCost:      number;
  stockValue:    number;
}

export interface ReorderAlertItem {
  productName:   string;
  sku:           string | null;
  warehouseName: string | null;
  qtyOnHand:     number;
  reorderPoint:  number;
  reorderQty:    number;
  shortfall:     number;
}

export interface InventoryValuationItem {
  warehouseName: string | null;
  productCount:  number;
  totalQty:      number;
  totalValue:    number;
}

// ── Logistics ─────────────────────────────────────────────────────────────────

export interface GrnVarianceItem {
  grnNumber:     string;
  poNumber:      string;
  supplierName:  string;
  receivedAt:    string;
  totalOrdered:  number;
  totalReceived: number;
  totalAccepted: number;
  totalRejected: number;
  varianceQty:   number;
  status:        string;
}

export interface ShipmentTrackerItem {
  shipmentNumber:   string;
  poNumber:         string;
  carrierName:      string | null;
  shipmentType:     string;
  status:           string;
  dispatchDate:     string;
  estimatedArrival: string;
  actualArrival:    string | null;
  isOverdue:        boolean;
  trackingNumber:   string | null;
  trackingUrl:      string | null;
}

// ── Finance ───────────────────────────────────────────────────────────────────

export interface InvoiceAgingItem {
  invoiceNumber:     string;
  supplierInvoiceNo: string | null;
  supplierName:      string;
  dueDate:           string;
  totalAmount:       number;
  paymentStatus:     string;
  daysOverdue:       number;
  agingBucket:       string;
}

export interface InvoiceAgingBucketSummary {
  bucket:      string;
  count:       number;
  totalAmount: number;
}

export interface PaymentSummaryModel {
  totalProcessed:  number;
  processedCount:  number;
  totalPending:    number;
  pendingCount:    number;
  totalReversed:   number;
  reversedCount:   number;
  byMethod:        PaymentByMethodItem[];
}

export interface PaymentByMethodItem {
  method:      string;
  count:       number;
  totalAmount: number;
}

export interface BudgetUtilizationItem {
  budgetCode:      string | null;
  department:      string | null;
  poCount:         number;
  estimatedAmount: number;
  actualSpend:     number;
  varianceAmount:  number;
  variancePercent: number;
}

// ── Audit & Activity ──────────────────────────────────────────────────────────

export interface AuditLogItemModel {
  uuid:         string;
  timestamp:    string;
  userId:       number | null;
  userName:     string | null;
  module:       string;
  action:       string;
  entityType:   string;
  entityId:     string | null;
  fieldChanged: string | null;
  oldValue:     string | null;
  newValue:     string | null;
  ipAddress:    string;
  notes:        string | null;
}

export interface UserActivityItem {
  userId:       number;
  userName:     string | null;
  totalActions: number;
  createCount:  number;
  updateCount:  number;
  deleteCount:  number;
  approveCount: number;
  lastActionAt: string | null;
}

// ── Material Report Filters ───────────────────────────────────────────────────

export interface MaterialIssueRegisterFilter {
  dateFrom?:    string;
  dateTo?:      string;
  search?:      string;
  status?:      string;
  warehouseId?: string;
  requestType?: string;
}

export interface MaterialConsumptionReportFilter {
  dateFrom?:   string;
  dateTo?:     string;
  search?:     string;
  sourceType?: string;
  mirUuid?:    string;
}

export interface ProjectConsumptionFilter {
  dateFrom?:       string;
  dateTo?:         string;
  search?:         string;
  projectUuid?:    string;
  transactionType?: string;
}

export interface DepartmentConsumptionFilter {
  dateFrom?:        string;
  dateTo?:          string;
  search?:          string;
  department?:      string;
  transactionType?: string;
}

export interface StockMovementFilter {
  dateFrom?:        string;
  dateTo?:          string;
  search?:          string;
  warehouseId?:     string;
  productUuid?:     string;
  transactionType?: string;
}

export interface StockLedgerFilter {
  dateFrom?:    string;
  dateTo?:      string;
  warehouseId?: string;
  productUuid?: string;
}

export interface MaterialReturnReportFilter {
  dateFrom?:  string;
  dateTo?:    string;
  search?:    string;
  status?:    string;
  condition?: string;
}

export interface WastageReportFilter {
  dateFrom?:   string;
  dateTo?:     string;
  search?:     string;
  status?:     string;
  sourceType?: string;
}

export interface ReservedStockFilter {
  dateFrom?:    string;
  dateTo?:      string;
  search?:      string;
  warehouseId?: string;
  status?:      string;
}

// ── Material Report Response Types ────────────────────────────────────────────

export interface MaterialIssueRegisterItem {
  mivUuid:     string;
  issueNo:     string;
  mirNo:       string;
  requestType: string;
  issuedTo:    string | null;
  issueDate:   string;
  status:      string;
  totalValue:  number;
  lineCount:   number;
  notes:       string | null;
  createdDate: string;
}

export interface MaterialConsumptionReportItem {
  productName:   string;
  productUuid:   string;
  unitOfMeasure: string | null;
  mirNo:         string;
  mirUuid:       string;
  issuedQty:     number;
  consumedQty:   number;
  balanceQty:    number;
  unitCost:      number;
  balanceValue:  number;
  sourceType:    string | null;
}

export interface ProjectConsumptionItem {
  projectUuid:     string;
  projectCode:     string;
  projectName:     string;
  itemDescription: string;
  productUuid:     string;
  transactionType: string;
  referenceNumber: string;
  quantity:        number;
  unitCost:        number;
  amount:          number;
  postedDate:      string;
}

export interface DepartmentConsumptionItem {
  department:      string;
  costCenter:      string | null;
  itemDescription: string;
  productUuid:     string;
  transactionType: string;
  referenceNumber: string;
  quantity:        number;
  unitCost:        number;
  amount:          number;
  postedDate:      string;
}

export interface StockMovementItem {
  ledgerUuid:      string;
  transactionType: string;
  productName:     string;
  productUuid:     string;
  sku:             string | null;
  warehouseName:   string;
  quantityIn:      number;
  quantityOut:     number;
  unitCost:        number;
  totalValue:      number;
  referenceNumber: string | null;
  batchNumber:     string | null;
  transactionDate: string;
}

export interface StockLedgerItem {
  ledgerUuid:      string;
  transactionDate: string;
  transactionType: string;
  productName:     string;
  productUuid:     string;
  warehouseName:   string;
  quantityIn:      number;
  quantityOut:     number;
  runningBalance:  number;
  unitCost:        number;
  referenceNumber: string | null;
}

export interface MaterialReturnReportItem {
  returnUuid:      string;
  returnNo:        string;
  mivNo:           string;
  mirNo:           string;
  status:          string;
  returnDate:      string;
  itemDescription: string;
  productUuid:     string;
  unitOfMeasure:   string | null;
  returnedQty:     number;
  condition:       string;
  reason:          string | null;
  unitCost:        number;
  lineValue:       number;
}

export interface WastageReportItem {
  wastageUuid:     string;
  wastageNo:       string;
  sourceType:      string;
  itemDescription: string;
  productUuid:     string;
  unitOfMeasure:   string | null;
  wastedQty:       number;
  unitCost:        number;
  amount:          number;
  reason:          string;
  status:          string;
  approvedBy:      number | null;
  approvedAt:      string | null;
  createdDate:     string;
}

export interface ReservedStockItem {
  reservationUuid: string;
  mirNo:           string;
  mirUuid:         string;
  requestType:     string;
  productUuid:     string;
  productName:     string;
  sku:             string | null;
  warehouseName:   string;
  reservedQty:     number;
  status:          string;
  reservedAt:      string;
  ageDays:         number;
  isFlagged:       boolean;
}

// ── Service ───────────────────────────────────────────────────────────────────

@Injectable({ providedIn: 'root' })
export class ReportsService {
  constructor(private http: HttpClient) {}

  private params(filter: Record<string, any>): HttpParams {
    let p = new HttpParams();
    for (const k of Object.keys(filter)) {
      if (filter[k] != null && filter[k] !== '') p = p.set(k, filter[k]);
    }
    return p;
  }

  getKpis(): Observable<ApiResponse<KpiDashboardModel>> {
    return this.http.get<ApiResponse<KpiDashboardModel>>(`${BASE}/kpis`);
  }

  getSupplierPerformance(filter: ReportDateFilter = {}): Observable<ApiResponse<SupplierPerformanceItem[]>> {
    return this.http.get<ApiResponse<SupplierPerformanceItem[]>>(`${BASE}/supplier-performance`, { params: this.params(filter) });
  }

  getPoSummary(filter: ReportDateFilter = {}): Observable<ApiResponse<PoSummaryItem[]>> {
    return this.http.get<ApiResponse<PoSummaryItem[]>>(`${BASE}/po-summary`, { params: this.params(filter) });
  }

  getSpendBySupplier(filter: ReportDateFilter = {}): Observable<ApiResponse<SpendBySupplierItem[]>> {
    return this.http.get<ApiResponse<SpendBySupplierItem[]>>(`${BASE}/spend-by-supplier`, { params: this.params(filter) });
  }

  getPendingApprovals(): Observable<ApiResponse<PendingApprovalItem[]>> {
    return this.http.get<ApiResponse<PendingApprovalItem[]>>(`${BASE}/pending-approvals`);
  }

  getStockLevels(filter: ReportDateFilter = {}): Observable<ApiResponse<StockLevelItem[]>> {
    return this.http.get<ApiResponse<StockLevelItem[]>>(`${BASE}/stock-levels`, { params: this.params(filter) });
  }

  getReorderAlerts(): Observable<ApiResponse<ReorderAlertItem[]>> {
    return this.http.get<ApiResponse<ReorderAlertItem[]>>(`${BASE}/reorder-alerts`);
  }

  getInventoryValuation(): Observable<ApiResponse<InventoryValuationItem[]>> {
    return this.http.get<ApiResponse<InventoryValuationItem[]>>(`${BASE}/inventory-valuation`);
  }

  getGrnVariance(filter: ReportDateFilter = {}): Observable<ApiResponse<GrnVarianceItem[]>> {
    return this.http.get<ApiResponse<GrnVarianceItem[]>>(`${BASE}/grn-variance`, { params: this.params(filter) });
  }

  getShipmentTracker(status?: string): Observable<ApiResponse<ShipmentTrackerItem[]>> {
    let p = new HttpParams();
    if (status) p = p.set('status', status);
    return this.http.get<ApiResponse<ShipmentTrackerItem[]>>(`${BASE}/shipment-tracker`, { params: p });
  }

  getInvoiceAging(): Observable<ApiResponse<{ items: InvoiceAgingItem[]; buckets: InvoiceAgingBucketSummary[] }>> {
    return this.http.get<ApiResponse<{ items: InvoiceAgingItem[]; buckets: InvoiceAgingBucketSummary[] }>>(`${BASE}/invoice-aging`);
  }

  getPaymentSummary(filter: ReportDateFilter = {}): Observable<ApiResponse<PaymentSummaryModel>> {
    return this.http.get<ApiResponse<PaymentSummaryModel>>(`${BASE}/payment-summary`, { params: this.params(filter) });
  }

  getBudgetUtilization(filter: ReportDateFilter = {}): Observable<ApiResponse<BudgetUtilizationItem[]>> {
    return this.http.get<ApiResponse<BudgetUtilizationItem[]>>(`${BASE}/budget-utilization`, { params: this.params(filter) });
  }

  getAuditTrail(filter: AuditLogFilter = {}): Observable<ApiResponse<PaginatedResponse<AuditLogItemModel>>> {
    return this.http.get<ApiResponse<PaginatedResponse<AuditLogItemModel>>>(`${BASE}/audit-trail`, { params: this.params(filter) });
  }

  getEntityAuditTrail(entityId: string, module: string, entityType: string): Observable<ApiResponse<PaginatedResponse<AuditLogItemModel>>> {
    return this.getAuditTrail({ entityId, module, entityType, pageSize: 100 });
  }

  getUserActivity(filter: ReportDateFilter = {}): Observable<ApiResponse<UserActivityItem[]>> {
    return this.http.get<ApiResponse<UserActivityItem[]>>(`${BASE}/user-activity`, { params: this.params(filter) });
  }

  // ── Material Reports ──────────────────────────────────────────────────────

  getMaterialIssueRegister(filter: MaterialIssueRegisterFilter = {}): Observable<ApiResponse<MaterialIssueRegisterItem[]>> {
    return this.http.get<ApiResponse<MaterialIssueRegisterItem[]>>(`${BASE}/material-issue-register`, { params: this.params(filter) });
  }

  getMaterialConsumption(filter: MaterialConsumptionReportFilter = {}): Observable<ApiResponse<MaterialConsumptionReportItem[]>> {
    return this.http.get<ApiResponse<MaterialConsumptionReportItem[]>>(`${BASE}/material-consumption`, { params: this.params(filter) });
  }

  getProjectConsumption(filter: ProjectConsumptionFilter = {}): Observable<ApiResponse<ProjectConsumptionItem[]>> {
    return this.http.get<ApiResponse<ProjectConsumptionItem[]>>(`${BASE}/project-consumption`, { params: this.params(filter) });
  }

  getDepartmentConsumption(filter: DepartmentConsumptionFilter = {}): Observable<ApiResponse<DepartmentConsumptionItem[]>> {
    return this.http.get<ApiResponse<DepartmentConsumptionItem[]>>(`${BASE}/department-consumption`, { params: this.params(filter) });
  }

  getStockMovement(filter: StockMovementFilter = {}): Observable<ApiResponse<StockMovementItem[]>> {
    return this.http.get<ApiResponse<StockMovementItem[]>>(`${BASE}/stock-movement`, { params: this.params(filter) });
  }

  getStockLedger(filter: StockLedgerFilter = {}): Observable<ApiResponse<StockLedgerItem[]>> {
    return this.http.get<ApiResponse<StockLedgerItem[]>>(`${BASE}/stock-ledger`, { params: this.params(filter) });
  }

  getMaterialReturn(filter: MaterialReturnReportFilter = {}): Observable<ApiResponse<MaterialReturnReportItem[]>> {
    return this.http.get<ApiResponse<MaterialReturnReportItem[]>>(`${BASE}/material-return`, { params: this.params(filter) });
  }

  getMaterialWastage(filter: WastageReportFilter = {}): Observable<ApiResponse<WastageReportItem[]>> {
    return this.http.get<ApiResponse<WastageReportItem[]>>(`${BASE}/material-wastage`, { params: this.params(filter) });
  }

  getReservedStock(filter: ReservedStockFilter = {}): Observable<ApiResponse<ReservedStockItem[]>> {
    return this.http.get<ApiResponse<ReservedStockItem[]>>(`${BASE}/reserved-stock`, { params: this.params(filter) });
  }
}

import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ApiResponse, PaginatedResponse } from './reports.service';

// A30-P5-02..06 — GET /api/reports/manufacturing/**. Five reports, one per FSD §31 category; the
// other 23 the spec lists already exist as an endpoint elsewhere in the app and are not repeated
// here (see the task register's own DONE notes for A30-P5-02..06 for the per-report accounting).

const BASE = `${environment.apiUrl}/reports/manufacturing`;

export interface ManufacturingReportFilter {
  dateFrom?: string;
  dateTo?:   string;
}

export interface ManufacturingRegisterFilter extends ManufacturingReportFilter {
  warehouseUuid?: string;
  status?:        string;
  page?:          number;
  pageSize?:      number;
}

// ── R4/R5/R16 — Production Efficiency ────────────────────────────────────────

export interface ProductionEfficiencyReport {
  generatedAt:               string;
  dateFrom?:                 string;
  dateTo?:                   string;
  totalOrders:               number;
  countByStatus:             Record<string, number>;
  totalPlannedQuantity:      number;
  totalProducedQuantity:     number;
  totalAcceptedQuantity:     number;
  totalRejectedQuantity:     number;
  overallYieldPercent?:      number;
  onTimeCompletionPercent?:  number;
  averageCycleDays?:         number;
}

// ── R6/R7 — Quality & Scrap Summary ──────────────────────────────────────────

export interface QualityScrapByProductItem {
  productUuid:           string;
  productName:           string;
  inspectedQty:          number;
  rejectedQty:           number;
  rejectionRatePercent?: number;
}

export interface QualityScrapReport {
  generatedAt:            string;
  dateFrom?:              string;
  dateTo?:                string;
  totalInspections:       number;
  totalInspectedQty:      number;
  totalAcceptedQty:       number;
  totalRejectedQty:       number;
  totalHoldQty:           number;
  totalReworkQty:         number;
  acceptanceRatePercent?: number;
  rejectionRatePercent?:  number;
  byProduct:              QualityScrapByProductItem[];
}

// ── R14/R15 — Manufacturing document registers ───────────────────────────────

export interface ProductionMaterialIssueRegisterItem {
  issueUuid:           string;
  issueNumber:         string;
  productionOrderUuid: string;
  productionNumber:    string;
  outputProductName:   string;
  warehouseUuid:       string;
  warehouseName:       string;
  issueType:           string;
  status:              string;
  totalQuantity:       number;
  lineCount:           number;
  createdAt:           string;
  confirmedAt?:        string;
}

export interface FinishedGoodsReceiptRegisterItem {
  fgrUuid:             string;
  fgrNumber:           string;
  productionOrderUuid: string;
  productionNumber:    string;
  productName:         string;
  warehouseUuid:       string;
  warehouseName:       string;
  totalQuantity:       number;
  status:              string;
  receivedAt:          string;
}

// ── R24 — Production Ledger Reconciliation ───────────────────────────────────

export interface LedgerReconciliationItem {
  productionOrderUuid:   string;
  productionNumber:      string;
  status:                string;
  acceptedQuantity:      number;
  finishedGoodsCredited: number;
  rejectedQuantity:      number;
  hasMaterialIssues:     boolean;
  hasQualityInspection:  boolean;
  reason:                string;
}

export interface LedgerReconciliationReport {
  generatedAt:        string;
  dateFrom?:          string;
  dateTo?:            string;
  totalOrdersChecked: number;
  flaggedCount:       number;
  flagged:            LedgerReconciliationItem[];
}

// ── R26/R27/R28 — Chained manufacturing dependency tree ──────────────────────

export interface ChainedManufacturingNode {
  productionOrderUuid: string;
  productionNumber:    string;
  productName:         string;
  plannedQuantity:     number;
  acceptedQuantity:    number;
  status:              string;
  createdAt:           string;
  actualEndDate?:      string;
  cycleDays?:          number;
  depth:               number;
  children:            ChainedManufacturingNode[];
}

export interface ChainedManufacturingReport {
  rootProductionOrderUuid: string;
  rootProductionNumber:    string;
  totalOrdersInChain:      number;
  maxDepth:                number;
  totalCycleDays?:         number;
  root:                    ChainedManufacturingNode;
}

@Injectable({ providedIn: 'root' })
export class ManufacturingReportsService {
  constructor(private http: HttpClient) {}

  private params(filter: Record<string, any>): HttpParams {
    let p = new HttpParams();
    for (const k of Object.keys(filter)) {
      if (filter[k] != null && filter[k] !== '') p = p.set(k, filter[k]);
    }
    return p;
  }

  getProductionEfficiency(filter: ManufacturingReportFilter = {}): Observable<ApiResponse<ProductionEfficiencyReport>> {
    return this.http.get<ApiResponse<ProductionEfficiencyReport>>(`${BASE}/production-efficiency`, { params: this.params(filter) });
  }

  getQualityScrap(filter: ManufacturingReportFilter = {}): Observable<ApiResponse<QualityScrapReport>> {
    return this.http.get<ApiResponse<QualityScrapReport>>(`${BASE}/quality-scrap`, { params: this.params(filter) });
  }

  getMaterialIssueRegister(filter: ManufacturingRegisterFilter = {}): Observable<ApiResponse<PaginatedResponse<ProductionMaterialIssueRegisterItem>>> {
    return this.http.get<ApiResponse<PaginatedResponse<ProductionMaterialIssueRegisterItem>>>(`${BASE}/material-issues`, { params: this.params(filter) });
  }

  getFinishedGoodsReceiptRegister(filter: ManufacturingRegisterFilter = {}): Observable<ApiResponse<PaginatedResponse<FinishedGoodsReceiptRegisterItem>>> {
    return this.http.get<ApiResponse<PaginatedResponse<FinishedGoodsReceiptRegisterItem>>>(`${BASE}/finished-goods-receipts`, { params: this.params(filter) });
  }

  getLedgerReconciliation(filter: ManufacturingReportFilter = {}): Observable<ApiResponse<LedgerReconciliationReport>> {
    return this.http.get<ApiResponse<LedgerReconciliationReport>>(`${BASE}/ledger-reconciliation`, { params: this.params(filter) });
  }

  getChainedManufacturing(productionOrderUuid: string): Observable<ApiResponse<ChainedManufacturingReport>> {
    return this.http.get<ApiResponse<ChainedManufacturingReport>>(`${BASE}/chained/${productionOrderUuid}`);
  }
}

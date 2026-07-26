import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse, PaginatedResponse } from './demand.service';

const BASE = 'https://localhost:52800/api/inventory/master-product-ledger';

export interface MasterProductLedgerEntryModel {
  ledgerId: string;
  productId: number;
  productCode: string;
  productName: string;
  categoryId?: number;
  categoryName?: string;
  warehouseId: number;
  warehouseName: string;
  transactionDate: string;
  transactionType: string;
  referenceType: string;
  referenceId: string;
  referenceNumber: string;
  quantityIn?: number;
  quantityOut?: number;
  unitCost: number;
  totalValue: number;
  sourceType: string;
  sourceName?: string;
  destinationType: string;
  destinationName?: string;
  notes?: string;
  createdBy: number;
  createdDate: string;
}

export interface MasterProductLedgerFilter {
  dateFrom?: string;
  dateTo?: string;
  productId?: number;
  categoryId?: number;
  warehouseId?: number;
  transactionType?: string;
  sourceType?: string;
  destinationType?: string;
  page?: number;
  pageSize?: number;
}

export interface MasterProductLedgerSummaryModel {
  totalReceiptsQty: number;
  totalIssuesQty: number;
  netMovement: number;
  totalValueMoved: number;
}

@Injectable({ providedIn: 'root' })
export class MasterProductLedgerService {
  constructor(private http: HttpClient) {}

  private buildParams(filter: MasterProductLedgerFilter): HttpParams {
    let params = new HttpParams();
    if (filter.dateFrom)        params = params.set('dateFrom', filter.dateFrom);
    if (filter.dateTo)          params = params.set('dateTo', filter.dateTo);
    if (filter.productId != null)   params = params.set('productId', filter.productId);
    if (filter.categoryId != null)  params = params.set('categoryId', filter.categoryId);
    if (filter.warehouseId != null) params = params.set('warehouseId', filter.warehouseId);
    if (filter.transactionType) params = params.set('transactionType', filter.transactionType);
    if (filter.sourceType)      params = params.set('sourceType', filter.sourceType);
    if (filter.destinationType) params = params.set('destinationType', filter.destinationType);
    if (filter.page)     params = params.set('page', filter.page);
    if (filter.pageSize) params = params.set('pageSize', filter.pageSize);
    return params;
  }

  getLedger(filter: MasterProductLedgerFilter): Observable<ApiResponse<PaginatedResponse<MasterProductLedgerEntryModel>>> {
    return this.http.get<ApiResponse<PaginatedResponse<MasterProductLedgerEntryModel>>>(BASE, { params: this.buildParams(filter) });
  }

  getSummary(filter: MasterProductLedgerFilter): Observable<ApiResponse<MasterProductLedgerSummaryModel>> {
    return this.http.get<ApiResponse<MasterProductLedgerSummaryModel>>(`${BASE}/summary`, { params: this.buildParams(filter) });
  }

  getProductJourney(productId: number): Observable<ApiResponse<MasterProductLedgerEntryModel[]>> {
    return this.http.get<ApiResponse<MasterProductLedgerEntryModel[]>>(`${BASE}/product-journey/${productId}`);
  }

  exportPdf(filter: MasterProductLedgerFilter): Observable<Blob> {
    return this.http.get(`${BASE}/export/pdf`, { params: this.buildParams(filter), responseType: 'blob' });
  }

  exportExcel(filter: MasterProductLedgerFilter): Observable<Blob> {
    return this.http.get(`${BASE}/export/excel`, { params: this.buildParams(filter), responseType: 'blob' });
  }
}

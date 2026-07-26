import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse, PaginatedResponse } from './demand.service';

const BASE = 'https://localhost:52800/api/finance/master-ledger';

export interface MasterLedgerEntryModel {
  uuid: string;
  sequenceNo: number;
  supplierId: string;
  supplierName: string;
  transactionType: string;
  referenceType: string;
  referenceId: string;
  referenceNo: string;
  entryDate: string;
  debitAmount: number;
  creditAmount: number;
  balanceAfter: number;
  narration?: string;
  createdBy: number;
  createdDate: string;
}

export interface MasterLedgerFilter {
  dateFrom?: string;
  dateTo?: string;
  supplierId?: string;
  transactionTypes?: string[];
  minAmount?: number;
  page?: number;
  pageSize?: number;
}

export interface MasterLedgerSummaryModel {
  totalPayables: number;
  totalDebits: number;
  totalCredits: number;
  netMovement: number;
}

export interface MasterLedgerBalanceModel {
  balance: number;
  asOf: string;
}

@Injectable({ providedIn: 'root' })
export class MasterLedgerService {
  constructor(private http: HttpClient) {}

  private buildParams(filter: MasterLedgerFilter): HttpParams {
    let params = new HttpParams();
    if (filter.dateFrom) params = params.set('dateFrom', filter.dateFrom);
    if (filter.dateTo)   params = params.set('dateTo', filter.dateTo);
    if (filter.supplierId) params = params.set('supplierId', filter.supplierId);
    if (filter.minAmount != null) params = params.set('minAmount', filter.minAmount);
    if (filter.transactionTypes?.length) {
      for (const t of filter.transactionTypes) params = params.append('transactionTypes', t);
    }
    if (filter.page) params = params.set('page', filter.page);
    if (filter.pageSize) params = params.set('pageSize', filter.pageSize);
    return params;
  }

  getLedger(filter: MasterLedgerFilter): Observable<ApiResponse<PaginatedResponse<MasterLedgerEntryModel>>> {
    return this.http.get<ApiResponse<PaginatedResponse<MasterLedgerEntryModel>>>(BASE, { params: this.buildParams(filter) });
  }

  getSummary(filter: MasterLedgerFilter): Observable<ApiResponse<MasterLedgerSummaryModel>> {
    return this.http.get<ApiResponse<MasterLedgerSummaryModel>>(`${BASE}/summary`, { params: this.buildParams(filter) });
  }

  getBalance(): Observable<ApiResponse<MasterLedgerBalanceModel>> {
    return this.http.get<ApiResponse<MasterLedgerBalanceModel>>(`${BASE}/balance`);
  }

  exportPdf(filter: MasterLedgerFilter): Observable<Blob> {
    return this.http.get(`${BASE}/export/pdf`, { params: this.buildParams(filter), responseType: 'blob' });
  }

  exportExcel(filter: MasterLedgerFilter): Observable<Blob> {
    return this.http.get(`${BASE}/export/excel`, { params: this.buildParams(filter), responseType: 'blob' });
  }
}

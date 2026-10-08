import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ApiResponse, PaginatedResponse } from './logistics.service';

// A35 D-15 / A35-E-06 — the exchange-difference register (API-CONTRACT.md §7.1) and the manual revaluation run (§7.2).
// Realized rows are written at payment posting; unrealized ones by the revaluation (monthly job, or "Run revaluation").

export const EXCHANGE_REVALUATION_RUN = 'EXCHANGE_REVALUATION_RUN';
/** Any of these reads the register (contract §7.1). */
export const EXCHANGE_DIFFERENCE_VIEW_PERMISSIONS = [
  'INVOICE_VIEW', 'SALES_INVOICE_VIEW', 'CUSTOMER_PAYMENT_VIEW', 'PAYMENT_VIEW', EXCHANGE_REVALUATION_RUN
];

export type ExchangeDifferenceKind = 'REALIZED' | 'UNREALIZED';
export type ExchangeDifferenceSide = 'RECEIVABLE' | 'PAYABLE';

export interface ExchangeDifferenceModel {
  id: string;
  kind: ExchangeDifferenceKind;
  side: ExchangeDifferenceSide;
  /** SALES_INVOICE | SUPPLIER_INVOICE */
  documentType: string;
  documentId?: number | null;
  documentUuid?: string | null;
  documentNo?: string | null;
  /** CUSTOMER_PAYMENT | SUPPLIER_PAYMENT | null (unrealized). */
  paymentType?: string | null;
  paymentId?: number | null;
  paymentUuid?: string | null;
  paymentNo?: string | null;
  allocationId?: number | null;
  partnerId?: string | null;
  currencyId: string;
  currencyCode: string;
  amountCurrency: number;
  bookedRate: number;
  settlementRate: number;
  baseCurrencyId: string;
  baseCurrencyCode: string;
  bookedAmountBase: number;
  settledAmountBase: number;
  /** + gain, − loss. */
  differenceBase: number;
  accountCode?: string | null;
  postedAt?: string | null;
  /** Date-only, for unrealized rows. */
  revaluationDate?: string | null;
  createdBy?: number | null;
}

export interface ExchangeDifferenceFilter {
  kind?: ExchangeDifferenceKind | null;
  side?: ExchangeDifferenceSide | null;
  currencyId?: string | null;
  /** yyyy-MM-dd */
  from?: string | null;
  to?: string | null;
  documentType?: string | null;
  /** The document's uuid (FIN, 2026-10-07: the filters take UUIDs). */
  documentId?: string | null;
  paymentType?: string | null;
  /** The payment's uuid. */
  paymentId?: string | null;
  page?: number;
  pageSize?: number;
}

export interface RevaluationTotalModel {
  baseCurrencyCode: string;
  gain: number;
  loss: number;
  net: number;
}

export interface RevaluationSkippedModel {
  documentType: string;
  documentNo: string;
  reason: string;
}

export interface RevaluationRunResultModel {
  revaluationDate: string;
  receivablesRevalued: number;
  payablesRevalued: number;
  rowsWritten: number;
  rowsReplaced: number;
  totals: RevaluationTotalModel[];
  skipped: RevaluationSkippedModel[];
}

@Injectable({ providedIn: 'root' })
export class ExchangeDifferenceService {
  private readonly api = environment.apiUrl;

  constructor(private http: HttpClient) {}

  getDifferences(filter: ExchangeDifferenceFilter = {}): Observable<ApiResponse<PaginatedResponse<ExchangeDifferenceModel>>> {
    let params = new HttpParams();
    for (const [key, value] of Object.entries(filter)) {
      if (value !== null && value !== undefined && value !== '') params = params.set(key, String(value));
    }
    return this.http.get<ApiResponse<PaginatedResponse<ExchangeDifferenceModel>>>(`${this.api}/finance/exchange-differences`, { params });
  }

  /** `revaluationDate` yyyy-MM-dd; omitted = today (server). Re-running a date replaces that date's rows. */
  runRevaluation(revaluationDate?: string | null): Observable<ApiResponse<RevaluationRunResultModel>> {
    const body = revaluationDate ? { revaluationDate } : {};
    return this.http.post<ApiResponse<RevaluationRunResultModel>>(`${this.api}/finance/exchange-revaluation/run`, body);
  }
}

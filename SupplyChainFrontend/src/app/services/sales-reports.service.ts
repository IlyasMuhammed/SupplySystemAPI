import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ApiResponse } from './logistics.service';

// A29 §15 — the ten sales reports (GET /api/reports/sales/{name}, Reports). Each has a JSON form, paged, and a
// PDF and an Excel form that ignore paging and carry every row.

/** The URL segment of each report, which is also how the dashboard addresses it. */
export type SalesReportKey =
  | 'order-register'       // R1
  | 'customer-ledger'      // R2
  | 'aging-receivables'    // R3
  | 'sales-by-product'     // R4
  | 'sales-by-customer'    // R5
  | 'fulfillment-status'   // R6
  | 'margin-analysis'      // R7
  | 'sales-vs-purchase'    // R8
  | 'product-ledger'       // R9
  | 'product-profitability'; // R10

export type SalesReportFormat = 'pdf' | 'excel';

/** Query parameters as the server names them; an undefined one is left out. */
export type SalesReportParams = Record<string, string | number | undefined>;

/** What every report says about itself and its paging. */
export interface SalesReportPage {
  companyName?: string | null;
  generatedAt: string;
  totalRecords: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

/** A report as the dashboard holds it before it knows which one it is: the paging, and whatever else the report carries. */
export type SalesReportResult = SalesReportPage & Record<string, any>;

// ── The reports the dashboard draws charts of ────────────────────────────────

export interface AgingReceivablesTotal {
  currencyCode: string;
  invoiceCount: number;
  days0To30: number;
  days31To60: number;
  days61To90: number;
  over90: number;
  total: number;
}

export interface AgingReceivablesCustomer {
  partnerId: string;
  customerName?: string | null;
  currencyCode: string;
  invoiceCount: number;
  days0To30: number;
  days31To60: number;
  days61To90: number;
  over90: number;
  total: number;
}

/** R3. Totals and customers are over every outstanding invoice; only the invoices are paged. */
export interface AgingReceivablesReport extends SalesReportPage {
  totals: AgingReceivablesTotal[];
  customers: AgingReceivablesCustomer[];
  invoices: Record<string, any>[];
}

export interface MarginAnalysisItem {
  groupId: string;
  name?: string | null;
  detail?: string | null;
  currencyCode: string;
  lineCount: number;
  quantity?: number | null;
  sellingValue: number;
  cost: number;
  margin: number;
  marginPercent?: number | null;
}

/** R7. Highest margin first; one row per product, customer or order and currency. */
export interface MarginAnalysisReport extends SalesReportPage {
  criteria: { groupBy: string };
  totals: Record<string, any>[];
  items: MarginAnalysisItem[];
}

export interface SalesVsPurchaseItem {
  periodStart: string;
  periodLabel: string;
  currencyCode: string;
  invoiceCount: number;
  revenue: number;
  costOfGoodsSold: number;
  grossMargin: number;
  grossMarginPercent?: number | null;
  uncostedRevenue: number;
}

/** R8. Oldest period first. */
export interface SalesVsPurchaseReport extends SalesReportPage {
  criteria: { period: string };
  totals: Record<string, any>[];
  items: SalesVsPurchaseItem[];
}

export interface ProfitabilityItem {
  rank: number;
  productUuid: string;
  productName?: string | null;
  currencyCode: string;
  quantitySold: number;
  revenue: number;
  costOfGoodsSold: number;
  grossProfit: number;
  marginPercent?: number | null;
}

/** R10. Most profitable first, ranked within each currency. */
export interface ProfitabilityReport extends SalesReportPage {
  totals: Record<string, any>[];
  items: ProfitabilityItem[];
}

@Injectable({ providedIn: 'root' })
export class SalesReportsService {
  private readonly baseUrl = `${environment.apiUrl}/reports/sales`;

  constructor(private http: HttpClient) {}

  /** One page of the report. */
  getReport<T extends SalesReportPage = SalesReportResult>(key: SalesReportKey, params: SalesReportParams): Observable<ApiResponse<T>> {
    return this.http.get<ApiResponse<T>>(`${this.baseUrl}/${key}`, { params: this.toHttpParams(params) });
  }

  /** The whole report as a document. Paging is dropped: the server carries every row. */
  download(key: SalesReportKey, format: SalesReportFormat, params: SalesReportParams): Observable<Blob> {
    const { page: _page, pageSize: _pageSize, ...filters } = params;
    return this.http.get(`${this.baseUrl}/${key}/${format}`, { params: this.toHttpParams(filters), responseType: 'blob' });
  }

  private toHttpParams(params: SalesReportParams): HttpParams {
    let httpParams = new HttpParams();
    for (const [name, value] of Object.entries(params)) {
      // Only what was asked for: an empty value sent as a parameter is a filter for the empty string.
      if (value !== undefined && value !== null && value !== '') httpParams = httpParams.set(name, String(value));
    }
    return httpParams;
  }
}

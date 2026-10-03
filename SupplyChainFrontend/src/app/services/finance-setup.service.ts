import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

// Finance master data in the SAP mould — tax codes and exchange rates (docs/finance/SAP-ALIGNMENT-PLAN.md).
// Backend: SMS.Modules.Finance, /api/finance/tax-codes and /api/finance/exchange-rates. Reads need only
// a sign-in (they feed pickers on sale orders and supplier invoices); writes need FINANCE_SETUP_MANAGE.

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

/** SALES | PURCHASE | BOTH */
export type TaxCodeUsage = 'SALES' | 'PURCHASE' | 'BOTH';

export interface TaxCodeModel {
  uuid: string;
  code: string;
  name: string;
  description: string | null;
  ratePercent: number;
  usage: TaxCodeUsage;
  isDefault: boolean;
  isActive: boolean;
}

export interface SaveTaxCodeRequest {
  code: string;
  name: string;
  description?: string | null;
  ratePercent: number;
  usage: TaxCodeUsage;
  isDefault: boolean;
  isActive: boolean;
}

export interface TaxCodesFromRatesResult {
  created: TaxCodeModel[];
  /** Rates already covered by an existing code of the same rate. */
  skippedRates: number[];
}

export interface ExchangeRateModel {
  uuid: string;
  fromCurrencyCode: string;
  toCurrencyCode: string;
  /** 1 unit of fromCurrencyCode = rate units of toCurrencyCode. */
  rate: number;
  /** yyyy-MM-dd */
  effectiveDate: string;
  source: string;
  notes: string | null;
  createdDate: string;
}

export interface SaveExchangeRateRequest {
  fromCurrencyCode: string;
  toCurrencyCode: string;
  rate: number;
  /** yyyy-MM-dd */
  effectiveDate: string;
  notes?: string | null;
}

export interface ExchangeRateQuoteModel {
  fromCurrencyCode: string;
  toCurrencyCode: string;
  rate: number;
  effectiveDate: string;
  inverted: boolean;
}

@Injectable({ providedIn: 'root' })
export class FinanceSetupService {
  private readonly baseUrl = `${environment.apiUrl}/finance`;

  constructor(private http: HttpClient) {}

  // ── Tax codes ──────────────────────────────────────────────────────────────

  /** side: SALES or PURCHASE limits to codes usable there (BOTH included). Active only unless includeInactive. */
  getTaxCodes(side?: 'SALES' | 'PURCHASE', includeInactive = false): Observable<ApiResponse<TaxCodeModel[]>> {
    let params = new HttpParams();
    if (side) params = params.set('side', side);
    if (includeInactive) params = params.set('includeInactive', 'true');
    return this.http.get<ApiResponse<TaxCodeModel[]>>(`${this.baseUrl}/tax-codes`, { params });
  }

  createTaxCode(req: SaveTaxCodeRequest): Observable<ApiResponse<TaxCodeModel>> {
    return this.http.post<ApiResponse<TaxCodeModel>>(`${this.baseUrl}/tax-codes`, req);
  }

  updateTaxCode(uuid: string, req: SaveTaxCodeRequest): Observable<ApiResponse<TaxCodeModel>> {
    return this.http.put<ApiResponse<TaxCodeModel>>(`${this.baseUrl}/tax-codes/${uuid}`, req);
  }

  /** Creates one SALES code per distinct tax % already used on sale orders that no code covers yet. */
  createTaxCodesFromRatesInUse(): Observable<ApiResponse<TaxCodesFromRatesResult>> {
    return this.http.post<ApiResponse<TaxCodesFromRatesResult>>(`${this.baseUrl}/tax-codes/from-rates-in-use`, {});
  }

  // ── Exchange rates ─────────────────────────────────────────────────────────

  getExchangeRates(from?: string, to?: string): Observable<ApiResponse<ExchangeRateModel[]>> {
    let params = new HttpParams();
    if (from) params = params.set('from', from);
    if (to) params = params.set('to', to);
    return this.http.get<ApiResponse<ExchangeRateModel[]>>(`${this.baseUrl}/exchange-rates`, { params });
  }

  createExchangeRate(req: SaveExchangeRateRequest): Observable<ApiResponse<ExchangeRateModel>> {
    return this.http.post<ApiResponse<ExchangeRateModel>>(`${this.baseUrl}/exchange-rates`, req);
  }

  updateExchangeRate(uuid: string, req: SaveExchangeRateRequest): Observable<ApiResponse<ExchangeRateModel>> {
    return this.http.put<ApiResponse<ExchangeRateModel>>(`${this.baseUrl}/exchange-rates/${uuid}`, req);
  }

  deleteExchangeRate(uuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/exchange-rates/${uuid}`);
  }

  /** The rate that would be used for from→to on a date (yyyy-MM-dd); result null when none is on file. */
  quoteExchangeRate(from: string, to: string, date: string): Observable<ApiResponse<ExchangeRateQuoteModel | null>> {
    const params = new HttpParams().set('from', from).set('to', to).set('date', date);
    return this.http.get<ApiResponse<ExchangeRateQuoteModel | null>>(`${this.baseUrl}/exchange-rates/quote`, { params });
  }
}

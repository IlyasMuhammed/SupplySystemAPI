import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ApiResponse } from './inventory.service';

// A35 — multi-currency settings (docs/multi-currency/API-CONTRACT.md):
//   §1 the organization's currencies   api/currencies                     CURRENCY_VIEW / CURRENCY_MANAGE
//   §2 exchange rates (date ranges)    api/currency-rates                 CURRENCY_RATE_VIEW / CURRENCY_RATE_MANAGE
//   §4 org currency settings           api/organization/currency-settings CURRENCY_VIEW / ORG_CURRENCY_SETTINGS_MANAGE
// Currency ids are the global lookups.Currencies Guid (D-1). The global reference list behind Master Data → Currencies is
// CurrenciesService / api/lookups/currencies — unchanged.

export const CURRENCY_VIEW = 'CURRENCY_VIEW';
export const CURRENCY_MANAGE = 'CURRENCY_MANAGE';
export const CURRENCY_RATE_VIEW = 'CURRENCY_RATE_VIEW';
export const CURRENCY_RATE_MANAGE = 'CURRENCY_RATE_MANAGE';
export const ORG_CURRENCY_SETTINGS_MANAGE = 'ORG_CURRENCY_SETTINGS_MANAGE';

/** EffectiveTo of the currently active rate. */
export const OPEN_END = '9999-12-31';
/** Decimals a stored exchange rate keeps (decimal(18,10)). */
export const CURRENCY_RATE_DECIMALS = 10;

export type CurrencyDomain = 'SALE' | 'PURCHASE' | 'SERVICE';

/** True for the open end (or no end) of a rate range. */
export function isOpenEnd(date: string | null | undefined): boolean {
  return !date || date.startsWith(OPEN_END);
}

// ── §1 ───────────────────────────────────────────────────────────────────────

export interface OrgCurrencyModel {
  id?: string;
  currencyId: string;
  code: string;
  name: string;
  symbol: string | null;
  decimalPlaces: number;
  rounding: number;
  symbolPosition: 'before' | 'after' | string;
  isActive: boolean;
  displayOrder: number;
  /** Domains whose base this currency is (empty = none). */
  baseFor?: CurrencyDomain[] | string[];
  /** The D-2 rate currency (rate 1.0 forever). */
  isRateCurrency?: boolean;
  createdAt?: string;
  updatedAt?: string;
}

export interface SaveOrgCurrencyRequest {
  code: string;
  name?: string | null;
  symbol?: string | null;
  decimalPlaces?: number | null;
  rounding?: number | null;
  symbolPosition?: string | null;
  isActive?: boolean | null;
  displayOrder?: number | null;
}

// ── §2 ───────────────────────────────────────────────────────────────────────

export interface CurrencyRateModel {
  id: string;
  currencyId: string;
  currencyCode: string;
  currencyName: string | null;
  rate: number;
  inverseRate: number;
  effectiveFrom: string;
  effectiveTo: string;
  isCurrent: boolean;
  source: 'MANUAL' | 'SYSTEM' | string;
  notes: string | null;
  rateCurrencyId: string;
  rateCurrencyCode: string;
  createdAt?: string;
  createdBy?: number | null;
  modifiedAt?: string | null;
  modifiedBy?: number | null;
}

export interface CurrencyRateFilter {
  currencyId?: string | null;
  from?: string | null;
  to?: string | null;
}

export interface CreateCurrencyRateRequest {
  currencyId: string;
  rate: number;
  effectiveFrom: string;
  /** null = open-ended (the normal case); a date fills a gap. */
  effectiveTo: string | null;
  notes: string | null;
}

export interface UpdateCurrencyRateRequest {
  rate: number;
  effectiveFrom: string;
  effectiveTo: string | null;
  notes: string | null;
}

export interface CreateCurrencyRateResult {
  rate: CurrencyRateModel;
  closedPrevious: CurrencyRateModel | null;
}

// ── §4 ───────────────────────────────────────────────────────────────────────

export interface CurrencyDomainLock {
  locked: boolean;
  reason: string | null;
}

export interface OrgCurrencySettingsModel {
  saleBaseCurrencyId: string;
  saleBaseCurrencyCode: string;
  purchaseBaseCurrencyId: string;
  purchaseBaseCurrencyCode: string;
  serviceBaseCurrencyId: string;
  serviceBaseCurrencyCode: string;
  rateCurrencyId: string;
  rateCurrencyCode: string;
  exchangeGainAccountCode: string | null;
  exchangeLossAccountCode: string | null;
  unrealizedGainAccountCode: string | null;
  unrealizedLossAccountCode: string | null;
  isStored: boolean;
  locks?: {
    sale?: CurrencyDomainLock;
    purchase?: CurrencyDomainLock;
    service?: CurrencyDomainLock;
    rateCurrency?: CurrencyDomainLock;
  } | null;
}

export interface SaveOrgCurrencySettingsRequest {
  saleBaseCurrencyId: string;
  purchaseBaseCurrencyId: string;
  serviceBaseCurrencyId: string;
  /** null = unchanged. */
  rateCurrencyId: string | null;
  exchangeGainAccountCode: string | null;
  exchangeLossAccountCode: string | null;
  unrealizedGainAccountCode: string | null;
  unrealizedLossAccountCode: string | null;
}

@Injectable({ providedIn: 'root' })
export class OrgCurrencyService {
  private readonly api = environment.apiUrl;

  constructor(private http: HttpClient) {}

  // §1
  getCurrencies(includeInactive = false): Observable<ApiResponse<OrgCurrencyModel[]>> {
    const params = new HttpParams().set('includeInactive', String(includeInactive));
    return this.http.get<ApiResponse<OrgCurrencyModel[]>>(`${this.api}/currencies`, { params });
  }

  createCurrency(body: SaveOrgCurrencyRequest): Observable<ApiResponse<OrgCurrencyModel>> {
    return this.http.post<ApiResponse<OrgCurrencyModel>>(`${this.api}/currencies`, body);
  }

  updateCurrency(currencyId: string, body: SaveOrgCurrencyRequest): Observable<ApiResponse<OrgCurrencyModel>> {
    return this.http.put<ApiResponse<OrgCurrencyModel>>(`${this.api}/currencies/${currencyId}`, body);
  }

  // §2
  getRates(filter: CurrencyRateFilter = {}): Observable<ApiResponse<CurrencyRateModel[]>> {
    let params = new HttpParams();
    if (filter.currencyId) params = params.set('currencyId', filter.currencyId);
    if (filter.from) params = params.set('from', filter.from);
    if (filter.to) params = params.set('to', filter.to);
    return this.http.get<ApiResponse<CurrencyRateModel[]>>(`${this.api}/currency-rates`, { params });
  }

  getActiveRates(): Observable<ApiResponse<CurrencyRateModel[]>> {
    return this.http.get<ApiResponse<CurrencyRateModel[]>>(`${this.api}/currency-rates/active`);
  }

  getRateOn(currencyId: string, date: string): Observable<ApiResponse<CurrencyRateModel>> {
    const params = new HttpParams().set('date', date);
    return this.http.get<ApiResponse<CurrencyRateModel>>(`${this.api}/currency-rates/${currencyId}`, { params });
  }

  getRateHistory(currencyId: string): Observable<ApiResponse<CurrencyRateModel[]>> {
    return this.http.get<ApiResponse<CurrencyRateModel[]>>(`${this.api}/currency-rates/history/${currencyId}`);
  }

  createRate(body: CreateCurrencyRateRequest): Observable<ApiResponse<CreateCurrencyRateResult>> {
    return this.http.post<ApiResponse<CreateCurrencyRateResult>>(`${this.api}/currency-rates`, body);
  }

  updateRate(id: string, body: UpdateCurrencyRateRequest): Observable<ApiResponse<CurrencyRateModel>> {
    return this.http.put<ApiResponse<CurrencyRateModel>>(`${this.api}/currency-rates/${id}`, body);
  }

  // §4
  getSettings(): Observable<ApiResponse<OrgCurrencySettingsModel>> {
    return this.http.get<ApiResponse<OrgCurrencySettingsModel>>(`${this.api}/organization/currency-settings`);
  }

  saveSettings(body: SaveOrgCurrencySettingsRequest): Observable<ApiResponse<OrgCurrencySettingsModel>> {
    return this.http.put<ApiResponse<OrgCurrencySettingsModel>>(`${this.api}/organization/currency-settings`, body);
  }
}

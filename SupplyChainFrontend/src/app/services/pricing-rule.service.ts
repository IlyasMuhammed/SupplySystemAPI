import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ApiResponse, PaginatedResponse } from './supplier.service';

// A29-P2-06 — Pricing Rules CRUD + resolve preview, backing PricingRulesController (P2-04).

export type PriceType = 'SELLING' | 'COST' | 'PROMOTIONAL' | 'CONTRACT';

export interface CreatePricingRuleRequest {
  variantUuid: string;
  partnerUuid?: string | null;
  priceType: PriceType;
  minQty?: number | null;
  maxQty?: number | null;
  unitPrice: number;
  currencyId?: string | null;
  effectiveFrom?: string | null;
  effectiveTo?: string | null;
}

export interface UpdatePricingRuleRequest {
  priceType: PriceType;
  minQty?: number | null;
  maxQty?: number | null;
  unitPrice: number;
  currencyId: string;
  effectiveFrom: string;
  effectiveTo?: string | null;
  isActive: boolean;
}

export interface PricingRuleModel {
  uuid: string;
  variantUuid: string;
  variantSku: string;
  productName: string;
  partnerUuid?: string | null;
  priceType: PriceType;
  minQty?: number | null;
  maxQty?: number | null;
  unitPrice: number;
  currencyId: string;
  effectiveFrom: string;
  effectiveTo?: string | null;
  isActive: boolean;
  createdDate: string;
}

export interface PricingRuleListFilter {
  variantUuid?: string;
  partnerUuid?: string;
  priceType?: PriceType;
  isActive?: boolean;
  page?: number;
  pageSize?: number;
}

export interface SalePriceResolution {
  found: boolean;
  unitPrice?: number | null;
  currencyId?: string | null;
  tier?: string | null;
  pricingRuleUuid?: string | null;
}

@Injectable({ providedIn: 'root' })
export class PricingRuleService {
  private readonly baseUrl = `${environment.apiUrl}/pricing-rules`;

  constructor(private http: HttpClient) {}

  createRule(req: CreatePricingRuleRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(this.baseUrl, req);
  }

  updateRule(uuid: string, req: UpdatePricingRuleRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.baseUrl}/${uuid}`, req);
  }

  deleteRule(uuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/${uuid}`);
  }

  getRuleById(uuid: string): Observable<ApiResponse<PricingRuleModel>> {
    return this.http.get<ApiResponse<PricingRuleModel>>(`${this.baseUrl}/${uuid}`);
  }

  getRules(filter: PricingRuleListFilter): Observable<ApiResponse<PaginatedResponse<PricingRuleModel>>> {
    let params = new HttpParams();
    if (filter.variantUuid) params = params.set('variantUuid', filter.variantUuid);
    if (filter.partnerUuid) params = params.set('partnerUuid', filter.partnerUuid);
    if (filter.priceType)   params = params.set('priceType', filter.priceType);
    if (filter.isActive !== undefined) params = params.set('isActive', String(filter.isActive));
    params = params.set('page', String(filter.page ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 50));
    return this.http.get<ApiResponse<PaginatedResponse<PricingRuleModel>>>(this.baseUrl, { params });
  }

  resolvePrice(variantUuid: string, partnerUuid: string | null, qty: number, date?: string): Observable<ApiResponse<SalePriceResolution>> {
    let params = new HttpParams().set('variantUuid', variantUuid).set('qty', String(qty));
    if (partnerUuid) params = params.set('partnerUuid', partnerUuid);
    if (date) params = params.set('date', date);
    return this.http.get<ApiResponse<SalePriceResolution>>(`${this.baseUrl}/resolve`, { params });
  }
}

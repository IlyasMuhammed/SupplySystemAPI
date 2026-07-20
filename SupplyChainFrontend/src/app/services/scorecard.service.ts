import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

const BASE = 'https://localhost:51800/api';

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

export interface SupplierScorecardRankingFilter {
  periodStart?: string;
  periodEnd?:   string;
}

export interface SupplierScorecardRankingItem {
  rank:               number;
  supplierId:         string;
  supplierName:       string;
  grade:              string;
  compositeScore:     number;
  deliveryScore:      number;
  quantityScore:      number;
  qualityScore:       number;
  priceScore:         number;
  documentationScore: number;
  grnCount:           number;
  trend:              string | null;
  scoreDelta:         number | null;
}

export interface SupplierScorecardRankingResponse {
  periodStart: string;
  periodEnd:   string;
  suppliers:   SupplierScorecardRankingItem[];
}

export interface GrnScoreListItem {
  grnId:         string;
  grnNumber:     string | null;
  totalRawScore: number;
  weightedScore: number;
  scoredAt:      string;
}

export interface ScorecardTrendPoint {
  periodStart:    string;
  periodEnd:      string;
  compositeScore: number;
  grade:          string;
}

export interface SupplierScorecardDetailModel {
  supplierId:         string;
  supplierName:       string;
  grade:              string;
  compositeScore:     number;
  trend:              string | null;
  deliveryScore:      number;
  quantityScore:      number;
  qualityScore:       number;
  priceScore:         number;
  documentationScore: number;
  lastScoredAt:       string | null;
  grnScores:           GrnScoreListItem[];
  trendHistory:        ScorecardTrendPoint[];
}

export interface RecalculateAllResult {
  periodStart:           string;
  periodEnd:             string;
  suppliersRecalculated: number;
}

@Injectable({ providedIn: 'root' })
export class ScorecardService {
  constructor(private http: HttpClient) {}

  getRanking(filter: SupplierScorecardRankingFilter = {}): Observable<ApiResponse<SupplierScorecardRankingResponse>> {
    let params = new HttpParams();
    if (filter.periodStart) params = params.set('periodStart', filter.periodStart);
    if (filter.periodEnd)   params = params.set('periodEnd', filter.periodEnd);
    return this.http.get<ApiResponse<SupplierScorecardRankingResponse>>(`${BASE}/supplier-scorecard`, { params });
  }

  getSupplierDetail(supplierId: string): Observable<ApiResponse<SupplierScorecardDetailModel>> {
    return this.http.get<ApiResponse<SupplierScorecardDetailModel>>(`${BASE}/supplier-scorecard/${supplierId}`);
  }

  recalculateAll(): Observable<ApiResponse<RecalculateAllResult>> {
    return this.http.post<ApiResponse<RecalculateAllResult>>(`${BASE}/supplier-scorecard/recalculate`, {});
  }
}

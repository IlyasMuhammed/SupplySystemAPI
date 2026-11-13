import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from './demand.service';
import { environment } from '../../environments/environment';

/** A50 D-27 / D-28 — sample (demo) data and the onboarding checklist (docs/onboarding/API-CONTRACT.md §4, DEMO). */

/** Entity types the "Demo" badge asks about. */
export type DemoEntityType = 'Customer' | 'Vendor' | 'Product' | 'ProductVariant' | 'SaleOrder' | 'PurchaseOrder';

export const DEMO_ENTITY_LABEL: Record<string, string> = {
  Customer: 'Customers', Customers: 'Customers', customers: 'Customers',
  Vendor: 'Suppliers', Vendors: 'Suppliers', vendors: 'Suppliers', Supplier: 'Suppliers', suppliers: 'Suppliers',
  Product: 'Products', Products: 'Products', products: 'Products',
  SaleOrder: 'Sale orders', SaleOrders: 'Sale orders', saleOrders: 'Sale orders',
  PurchaseOrder: 'Purchase orders', PurchaseOrders: 'Purchase orders', purchaseOrders: 'Purchase orders',
  openingStock: 'Products with opening stock', OpeningStock: 'Products with opening stock'
};

export function demoEntityLabel(type: string): string {
  return DEMO_ENTITY_LABEL[type] ?? type.replace(/([a-z])([A-Z])/g, '$1 $2');
}

/** docs/onboarding/SAMPLE-DATA.md §1.1. Keys: customers, vendors, products, saleOrders, purchaseOrders, openingStock. */
export interface SampleDataPlanModel {
  /** What a load would create, per kind. */
  counts: Record<string, number>;
  /** The demo records this org has now, per kind. */
  current?: Record<string, number> | null;
  /** Demo data is already loaded (a second load is refused with 409). */
  hasDemo: boolean;
  /** Missing (older server) = derive from hasDemo. */
  canLoad?: boolean;
  /** Why a load is not offered. */
  reason?: string | null;
  loadedAt?: string | null;
  /** E.g. why opening stock is skipped. */
  notes?: string[] | null;
}

export interface SampleDataLoadResult {
  created: Record<string, number>;
  total?: number;
  notes?: string[] | null;
}

export interface KeptDemoRecord {
  entityType: string;
  entityRef: string;
  name?: string | null;
  reason?: string | null;
}

export interface SampleDataDeleteResult {
  /** Includes openingStock. */
  deleted: Record<string, number>;
  total?: number;
  /** Which record was kept and what uses it (kept[].name is null for now). */
  notes?: string[] | null;
  /** Demo records real data now uses: kept, never cascaded (D-27). */
  kept: KeptDemoRecord[];
}

export interface OnboardingStepModel {
  stepCode: string;
  title: string;
  description?: string | null;
  /** A portal route ("/portal/pages/…"). */
  link?: string | null;
  sortOrder?: number;
  completedAt?: string | null;
  autoCompleted?: boolean;
  requiresModule?: string | null;
  completed?: boolean;
}

export interface OnboardingChecklistModel {
  dismissed: boolean;
  dismissedAt?: string | null;
  steps: OnboardingStepModel[];
  completedCount?: number;
  totalCount?: number;
  isComplete?: boolean;
}

export function isStepDone(s: OnboardingStepModel): boolean {
  return !!s.completed || !!s.completedAt;
}

const ONB = `${environment.apiUrl}/onboarding`;

@Injectable({ providedIn: 'root' })
export class OnboardingDemoService {
  constructor(private http: HttpClient) {}

  // ── Sample data (SAMPLE_DATA_MANAGE) ──────────────────────────────────────────

  getSampleData(): Observable<ApiResponse<SampleDataPlanModel>> {
    return this.http.get<ApiResponse<SampleDataPlanModel>>(`${ONB}/sample-data`);
  }

  loadSampleData(): Observable<ApiResponse<SampleDataLoadResult>> {
    return this.http.post<ApiResponse<SampleDataLoadResult>>(`${ONB}/sample-data/load`, {});
  }

  deleteSampleData(): Observable<ApiResponse<SampleDataDeleteResult>> {
    return this.http.delete<ApiResponse<SampleDataDeleteResult>>(`${ONB}/sample-data`);
  }

  /** The demo records of one type (any signed-in user). */
  getDemoRefs(entityType: string): Observable<ApiResponse<string[]>> {
    const params = new HttpParams().set('entityType', entityType);
    return this.http.get<ApiResponse<string[]>>(`${ONB}/demo/refs`, { params });
  }

  // ── Checklist (ONBOARDING_VIEW) ───────────────────────────────────────────────

  getChecklist(): Observable<ApiResponse<OnboardingChecklistModel>> {
    return this.http.get<ApiResponse<OnboardingChecklistModel>>(`${ONB}/checklist`);
  }

  completeStep(stepCode: string): Observable<ApiResponse<OnboardingChecklistModel>> {
    return this.http.post<ApiResponse<OnboardingChecklistModel>>(
      `${ONB}/checklist/steps/${encodeURIComponent(stepCode)}/complete`, {});
  }

  /** Permanent: there is no undismiss. */
  dismissChecklist(): Observable<ApiResponse<OnboardingChecklistModel>> {
    return this.http.post<ApiResponse<OnboardingChecklistModel>>(`${ONB}/checklist/dismiss`, {});
  }
}

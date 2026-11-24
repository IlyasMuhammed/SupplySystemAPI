import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import type { ApiResponse } from '../pages/service/auth.service';

// A51 P2 - platform feature catalog (status / visibility) and plans. All super admin only:
// api/system/features (GET list, PUT {code}/status, PUT {code}/visibility) and api/system/plans.

export type FeatureStatus = 'Live' | 'Beta' | 'ComingSoon' | 'Deprecated';
export const FEATURE_STATUSES: FeatureStatus[] = ['Live', 'Beta', 'ComingSoon', 'Deprecated'];

export interface FeatureDefinition {
  id: string;
  featureCode: string;
  featureName: string;
  category: string;
  description: string | null;
  isCore: boolean;
  displayOrder: number;
  isAvailable: boolean;
  status: FeatureStatus;
  isVisible: boolean;
  icon: string | null;
}

export interface Plan {
  id: number;
  code: string;
  name: string;
  description: string | null;
  /** Display only - nothing bills from it. */
  monthlyPrice: number | null;
  isActive: boolean;
  sortOrder: number;
  includedFeatureCount: number;
  totalFeatureCount: number;
}

export interface PlanFeatureItem {
  featureCode: string;
  featureName: string;
  category: string;
  parentModuleCode: string | null;
  isCore: boolean;
  isAvailable: boolean;
  /** Never part of a plan; only the super admin licenses it by hand. */
  licensedByHandOnly: boolean;
  isIncluded: boolean;
  displayOrder: number;
}

export interface PlanWithFeatures extends Plan {
  features: PlanFeatureItem[];
}

export interface UpdatePlanRequest {
  name: string;
  description: string | null;
  monthlyPrice: number | null;
  isActive: boolean;
  sortOrder: number;
}

export interface PlanFeatureChange {
  featureCode: string;
  isIncluded: boolean;
}

/** A51 P7 - why a feature was added, removed or left alone when a plan was applied to an organization. */
export interface PlanApplyFeatureItem {
  featureCode: string;
  featureName: string;
  /** Skipped rows only: ComingSoon, NotVisible, NotAvailable, Core, Grandfathered, LicensedByHandOnly, SwitchedOffByOrganization, NeedsModule, InUse, Failed. */
  reason?: string | null;
  detail?: string | null;
}

export interface PlanApplyOrgResult {
  organizationId: string;
  orgCode?: string | null;
  orgName?: string | null;
  plan: string;
  added: PlanApplyFeatureItem[];
  removed: PlanApplyFeatureItem[];
  skipped: PlanApplyFeatureItem[];
}

export type PlanApplyMode = 'addOnly' | 'full';

export interface ApplyPlanResult {
  planCode: string;
  mode: PlanApplyMode;
  dryRun: boolean;
  organizationCount: number;
  totalAdded: number;
  totalRemoved: number;
  totalSkipped: number;
  organizations: PlanApplyOrgResult[];
}

@Injectable({ providedIn: 'root' })
export class FeaturesAdminService {
  private readonly features = `${environment.apiUrl}/system/features`;
  private readonly plans = `${environment.apiUrl}/system/plans`;

  constructor(private http: HttpClient) {}

  listFeatures(): Observable<ApiResponse<FeatureDefinition[]>> {
    return this.http.get<ApiResponse<FeatureDefinition[]>>(this.features);
  }

  setStatus(code: string, status: FeatureStatus): Observable<ApiResponse<FeatureDefinition>> {
    return this.http.put<ApiResponse<FeatureDefinition>>(`${this.features}/${encodeURIComponent(code)}/status`, { status });
  }

  setVisibility(code: string, isVisible: boolean): Observable<ApiResponse<FeatureDefinition>> {
    return this.http.put<ApiResponse<FeatureDefinition>>(`${this.features}/${encodeURIComponent(code)}/visibility`, { isVisible });
  }

  listPlans(): Observable<ApiResponse<Plan[]>> {
    return this.http.get<ApiResponse<Plan[]>>(this.plans);
  }

  getPlan(code: string): Observable<ApiResponse<PlanWithFeatures>> {
    return this.http.get<ApiResponse<PlanWithFeatures>>(`${this.plans}/${encodeURIComponent(code)}`);
  }

  updatePlan(code: string, body: UpdatePlanRequest): Observable<ApiResponse<Plan>> {
    return this.http.put<ApiResponse<Plan>>(`${this.plans}/${encodeURIComponent(code)}`, body);
  }

  /** A51 P7 - pushes the plan's features to the organizations on it. addOnly never removes; full = each organization's Reset to Plan Defaults; dryRun previews. */
  applyToOrganizations(code: string, mode: PlanApplyMode, dryRun: boolean): Observable<ApiResponse<ApplyPlanResult>> {
    return this.http.post<ApiResponse<ApplyPlanResult>>(`${this.plans}/${encodeURIComponent(code)}/apply-to-organizations`, { mode, dryRun });
  }

  updatePlanFeatures(code: string, features: PlanFeatureChange[]): Observable<ApiResponse<PlanWithFeatures>> {
    return this.http.put<ApiResponse<PlanWithFeatures>>(`${this.plans}/${encodeURIComponent(code)}/features`, { features });
  }
}

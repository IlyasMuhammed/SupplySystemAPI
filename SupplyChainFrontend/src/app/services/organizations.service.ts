import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

const BASE = 'https://localhost:52800/api/system/organizations';

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

export interface PaginatedResponse<T> {
  data: T[];
  totalRecords: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export type OrgPlan = 'BASIC' | 'STANDARD' | 'ENTERPRISE';

export interface OrganizationListItemModel {
  id: string;
  orgCode: string;
  orgName: string;
  plan: OrgPlan;
  isActive: boolean;
  contactEmail?: string;
  createdDate: string;
}

export interface OrganizationDetailModel {
  id: string;
  orgCode: string;
  orgName: string;
  plan: OrgPlan;
  isActive: boolean;
  contactEmail?: string;
  contactPhone?: string;
  address?: string;
  country?: string;
  timeZone?: string;
  createdBy: number;
  createdDate: string;
  modifiedBy?: number;
  modifiedDate?: string;
}

export interface OrganizationFilter {
  search?: string;
  plan?: string;
  isActive?: boolean;
  page?: number;
  pageSize?: number;
}

export interface CreateOrganizationRequest {
  orgCode: string;
  orgName: string;
  plan: OrgPlan;
  contactEmail?: string;
  contactPhone?: string;
  address?: string;
  country?: string;
  timeZone?: string;
  // Initial Admin user, created atomically with the organization — receives an email
  // invitation to set their own password.
  adminFirstName: string;
  adminLastName: string;
  adminEmail: string;
}

export interface CreateOrganizationResult {
  organizationId: string;
  adminUserId: number;
}

export interface UpdateOrganizationRequest {
  orgName: string;
  contactEmail?: string;
  contactPhone?: string;
  address?: string;
  country?: string;
  timeZone?: string;
}

export interface OrganizationFeatureModel {
  featureCode: string;
  featureName: string;
  category: 'MODULE' | 'SCREEN' | 'FEATURE';
  description?: string;
  isCore: boolean;
  displayOrder: number;
  isEnabled: boolean;
  modifiedDate?: string;
}

export interface FeatureToggleItem {
  featureCode: string;
  isEnabled: boolean;
}

export interface UpdateFeaturesResult {
  updatedFeatures: OrganizationFeatureModel[];
  autoEnabledDependencies: string[];
}

// There's no dedicated AdminUserId column on Organization — "the org admin" is whichever active
// user in this list holds roleId === ORG_ADMIN_ROLE_ID.
export interface OrgUserSummary {
  userId: number;
  firstName: string;
  lastName?: string;
  email: string;
  roleId: number;
  roleName: string;
  isActive: boolean;
}

// Mirrors SMS.Shared.Common.Enums.EnumRole.OrgAdmin = 10 (backend seeds/assigns this value).
export const ORG_ADMIN_ROLE_ID = 10;

@Injectable({ providedIn: 'root' })
export class OrganizationsService {
  constructor(private http: HttpClient) {}

  getList(filter: OrganizationFilter): Observable<ApiResponse<PaginatedResponse<OrganizationListItemModel>>> {
    let params = new HttpParams();
    if (filter.search)   params = params.set('search', filter.search);
    if (filter.plan)     params = params.set('plan', filter.plan);
    if (filter.isActive !== undefined) params = params.set('isActive', String(filter.isActive));
    if (filter.page)     params = params.set('page', filter.page.toString());
    if (filter.pageSize) params = params.set('pageSize', filter.pageSize.toString());
    return this.http.get<ApiResponse<PaginatedResponse<OrganizationListItemModel>>>(BASE, { params });
  }

  getById(id: string): Observable<ApiResponse<OrganizationDetailModel>> {
    return this.http.get<ApiResponse<OrganizationDetailModel>>(`${BASE}/${id}`);
  }

  create(req: CreateOrganizationRequest): Observable<ApiResponse<CreateOrganizationResult>> {
    return this.http.post<ApiResponse<CreateOrganizationResult>>(BASE, req);
  }

  update(id: string, req: UpdateOrganizationRequest): Observable<ApiResponse<null>> {
    return this.http.put<ApiResponse<null>>(`${BASE}/${id}`, req);
  }

  // Reactivation only — deactivation is the dedicated `deactivate` action below, since it also
  // terminates every active session for the org's users.
  patchStatus(id: string, isActive: boolean): Observable<ApiResponse<null>> {
    return this.http.patch<ApiResponse<null>>(`${BASE}/${id}/status`, { isActive });
  }

  deactivate(id: string): Observable<ApiResponse<null>> {
    return this.http.patch<ApiResponse<null>>(`${BASE}/${id}/deactivate`, {});
  }

  patchPlan(id: string, plan: OrgPlan): Observable<ApiResponse<null>> {
    return this.http.patch<ApiResponse<null>>(`${BASE}/${id}/plan`, { plan });
  }

  applyPlanTemplate(id: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`${BASE}/${id}/apply-plan-template`, {});
  }

  // ── Feature toggles (nested under Organizations per the ticket's route shape) ──────

  getFeatures(orgId: string): Observable<ApiResponse<OrganizationFeatureModel[]>> {
    return this.http.get<ApiResponse<OrganizationFeatureModel[]>>(`${BASE}/${orgId}/features`);
  }

  updateFeatures(orgId: string, features: FeatureToggleItem[]): Observable<ApiResponse<UpdateFeaturesResult>> {
    return this.http.put<ApiResponse<UpdateFeaturesResult>>(`${BASE}/${orgId}/features`, { features });
  }

  // ── Organization admin (view/change who holds the OrgAdmin role) ────────────────────

  getOrgUsers(orgId: string): Observable<ApiResponse<OrgUserSummary[]>> {
    return this.http.get<ApiResponse<OrgUserSummary[]>>(`${BASE}/${orgId}/users`);
  }

  updateAdmin(orgId: string, newAdminUserId: number): Observable<ApiResponse<null>> {
    return this.http.put<ApiResponse<null>>(`${BASE}/${orgId}/admin`, { newAdminUserId });
  }
}

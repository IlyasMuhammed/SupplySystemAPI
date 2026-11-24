import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import type { PlanApplyFeatureItem, PlanApplyOrgResult } from './features-admin.service';

const BASE = `${environment.apiUrl}/system/organizations`;

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

/** The name shown for a plan. The stored code of the entry plan stays BASIC; it is presented as "Default". */
export function planLabel(plan?: string | null): string {
  switch ((plan ?? '').toUpperCase()) {
    case 'BASIC':      return 'Default';
    case 'STANDARD':   return 'Standard';
    case 'ENTERPRISE': return 'Enterprise';
    default:           return plan ?? '';
  }
}

export interface OrganizationListItemModel {
  id: string;
  orgCode: string;
  orgName: string;
  plan: OrgPlan;
  isActive: boolean;
  contactEmail?: string;
  createdDate: string;
  /** A51 P7 - from the organization's setup row (null when it has none). */
  countryCode?: string | null;
  industryType?: string | null;
  setupStatus?: SetupStatus | null;
}

/** A51 P7 - the organization's onboarding setup status. */
export type SetupStatus = 'NotStarted' | 'InProgress' | 'Completed' | 'Skipped';

export function setupStatusLabel(s?: string | null): string {
  switch (s) {
    case 'Completed':  return 'Completed';
    case 'InProgress': return 'In progress';
    case 'NotStarted': return 'Not started';
    case 'Skipped':    return 'Skipped';
    default:           return '—';
  }
}

export function setupStatusSeverity(s?: string | null): 'success' | 'info' | 'warn' | 'secondary' {
  switch (s) {
    case 'Completed':  return 'success';
    case 'InProgress': return 'info';
    case 'NotStarted': return 'warn';
    default:           return 'secondary';
  }
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
  /** A Lookups currency id (currencies.service); null when none is set. */
  baseCurrency?: string | null;
  createdBy: number;
  createdDate: string;
  modifiedBy?: number;
  modifiedDate?: string;
  /** A51 P7 - read-only: changing country or industry later does not re-run default data. */
  countryCode?: string | null;
  industryType?: string | null;
  setupStatus?: SetupStatus | null;
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
  /** A plan code from the Plans master (BASIC is shown as "Default"). */
  plan: OrgPlan | string;
  contactEmail?: string;
  contactPhone?: string;
  address?: string;
  country?: string;
  timeZone?: string;
  /** A51 P3 - alpha-2 code from the countries master; the server stores the country name and, without a timeZone, the country's default zone. */
  countryCode?: string;
  /** A51 P3 - an industry offered in countryCode; needs a countryCode. With both, the server applies the A50 default data to the new organization. */
  industryType?: string;
  /** A Lookups currency id; optional. */
  baseCurrency?: string;
  // Initial Admin user, created atomically with the organization — receives an email
  // invitation to set their own password.
  adminFirstName: string;
  adminLastName: string;
  adminEmail: string;
  /** Modules to license at creation. Omitted = the plan's defaults; an empty list = none (the admin can only manage users). */
  moduleCodes?: string[];
}

/** Pack level of the A50 fallback chain a category's default came from. */
export type PackLevel = 'Exact' | 'Country' | 'Industry' | 'Global' | 'None';

export interface OrgDefaultsCategoryResult {
  category: string;
  status: string;
  ok: boolean;
  created: number;
  skipped: number;
  message?: string | null;
  packLevel: PackLevel;
  resolvedCountry?: string | null;
  resolvedIndustry?: string | null;
}

/** A51 P7 - where the background apply of the default data stands. */
export type DefaultsRunStatus = 'Running' | 'Completed' | 'InProgress' | 'Failed' | 'NotStarted';

/** A51 P7 - GET api/system/organizations/{id}/defaults-status. Per-category rows are 'Pending' until their turn. */
export interface OrgDefaultsStatus {
  organizationId: string;
  status: DefaultsRunStatus;
  setupStatus?: string | null;
  countryCode?: string | null;
  industryType?: string | null;
  packLevel: PackLevel;
  fallbackUsed: boolean;
  message?: string | null;
  startedAt?: string | null;
  finishedAt?: string | null;
  categories: OrgDefaultsCategoryResult[];
}

/** A51 P7 - did the initial admin's invitation e-mail go out? The link is present only when it did not (Super Admin only; never shown elsewhere). */
export interface OrgInvitationOutcome {
  adminEmail?: string | null;
  emailSent: boolean;
  emailConfigured: boolean;
  error?: string | null;
  inviteLink?: string | null;
  inviteExpiresAt?: string | null;
}

/** A51 P7 - POST .../users/{id}/resend-invitation. */
export interface ResendInvitationResult {
  user?: OrgUserSummary | null;
  emailSent: boolean;
  emailConfigured: boolean;
  error?: string | null;
  inviteLink?: string | null;
}

/** A51 P3 - what applying the default data to the new organization did (best effort: the organization exists either way). */
export interface OrgDefaultsOutcome {
  /** A51 P7 - 'Running' right after create (the apply is queued for a background worker); the dialog then polls the status endpoint. */
  status?: DefaultsRunStatus;
  attempted: boolean;
  setupStatus?: string | null;
  completed: boolean;
  countryCode?: string | null;
  industryType?: string | null;
  packLevel: PackLevel;
  fallbackUsed: boolean;
  message?: string | null;
  categories: OrgDefaultsCategoryResult[];
}

export interface CreateOrganizationResult {
  organizationId: string;
  adminUserId: number;
  plan?: string | null;
  country?: string | null;
  countryCode?: string | null;
  industryType?: string | null;
  timeZone?: string | null;
  timeZoneFromCountry?: boolean;
  /** Null when no country was chosen (nothing applied; the wizard asks on the admin's first login). */
  defaults?: OrgDefaultsOutcome | null;
  /** A51 P7 - plan features not licensed to the new organization because they are Coming Soon or not visible. */
  skippedFeatures?: PlanApplyFeatureItem[] | null;
  /** A51 P7 - whether the invitation e-mail went out. */
  invitation?: OrgInvitationOutcome | null;
}

/** Plain-words name of a pack level for the create result. */
export function packLevelLabel(level?: string | null): string {
  switch (level) {
    case 'Exact':    return 'country + industry pack';
    case 'Country':  return 'country pack (general industry)';
    case 'Industry': return 'industry pack (general country)';
    case 'Global':   return 'general pack (no country or industry pack)';
    default:         return 'no pack';
  }
}

export interface UpdateOrganizationRequest {
  orgName: string;
  contactEmail?: string;
  contactPhone?: string;
  address?: string;
  country?: string;
  timeZone?: string;
  /** A Lookups currency id to set. Left out (or null), the base currency stays as it is. */
  baseCurrency?: string | null;
  /** True removes the base currency. Never together with baseCurrency. */
  clearBaseCurrency?: boolean;
}

/**
 * What an edit sends about the base currency: a newly chosen one; an explicit clear when one was set and the
 * field was emptied; nothing when it is unchanged (the server then leaves the column alone). An unchanged one is
 * not sent back, because the server re-checks any id it is sent — a base currency since removed from the catalog
 * (or left without a code) would turn every other edit of the organization into a 400.
 */
export function baseCurrencyChange(
  original: string | null | undefined, chosen: string | null | undefined
): Pick<UpdateOrganizationRequest, 'baseCurrency' | 'clearBaseCurrency'> {
  if (chosen) return original && chosen.toLowerCase() === original.toLowerCase() ? {} : { baseCurrency: chosen };
  if (original) return { clearBaseCurrency: true };
  return {};
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
  // ── A37 (API-CONTRACT §1.2) — the super admin's toggle is the licence; the org admin switches licensed ones ──
  isLicensed?: boolean;
  status?: 'ALWAYS_ON' | 'ACTIVE' | 'GRACE' | 'DISABLED' | 'NOT_LICENSED' | 'COMING_SOON';
  graceEndsAt?: string | null;
  /** Null for modules; the module a screen/feature belongs to otherwise. */
  parentModuleCode?: string | null;
  isAlwaysOn?: boolean;
  isAvailable?: boolean;
  /** A51 P7 - the platform status of the feature (Live / Beta / ComingSoon / Deprecated), whether it is listed, and whether this organization is grandfathered for it. */
  featureStatus?: string;
  isVisible?: boolean;
  isGrandfathered?: boolean;
}

/** A37 — GET …/features/history (newest first). */
export interface OrganizationFeatureHistoryEntry {
  performedAt: string;
  action: string;
  featureCode?: string | null;
  performedByName: string;
  graceDays?: number | null;
  notes?: string | null;
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
  /** True while the user has an invite they have not accepted yet (no password set). */
  invitePending?: boolean;
  /** When the pending invite link expires (UTC ISO); null when there is none. */
  inviteExpiresAt?: string | null;
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

  /** Reset to Plan Defaults. A51 P7: the answer lists what was added / removed / skipped (Coming Soon features are skipped). */
  applyPlanTemplate(id: string): Observable<ApiResponse<PlanApplyOrgResult | null>> {
    return this.http.post<ApiResponse<PlanApplyOrgResult | null>>(`${BASE}/${id}/apply-plan-template`, {});
  }

  /** A51 P7 - progress of the background default-data apply (poll every few seconds while status is Running). */
  getDefaultsStatus(id: string): Observable<ApiResponse<OrgDefaultsStatus>> {
    return this.http.get<ApiResponse<OrgDefaultsStatus>>(`${BASE}/${id}/defaults-status`);
  }

  // ── Feature toggles (nested under Organizations per the ticket's route shape) ──────

  getFeatures(orgId: string): Observable<ApiResponse<OrganizationFeatureModel[]>> {
    return this.http.get<ApiResponse<OrganizationFeatureModel[]>>(`${BASE}/${orgId}/features`);
  }

  updateFeatures(orgId: string, features: FeatureToggleItem[]): Observable<ApiResponse<UpdateFeaturesResult>> {
    return this.http.put<ApiResponse<UpdateFeaturesResult>>(`${BASE}/${orgId}/features`, { features });
  }

  /** A37 — every licence / switch / grace change of the organization's modules and features, newest first. */
  getFeatureHistory(orgId: string): Observable<ApiResponse<OrganizationFeatureHistoryEntry[]>> {
    return this.http.get<ApiResponse<OrganizationFeatureHistoryEntry[]>>(`${BASE}/${orgId}/features/history`);
  }

  // ── Organization admin (view/change who holds the OrgAdmin role) ────────────────────

  getOrgUsers(orgId: string): Observable<ApiResponse<OrgUserSummary[]>> {
    return this.http.get<ApiResponse<OrgUserSummary[]>>(`${BASE}/${orgId}/users`);
  }

  updateAdmin(orgId: string, newAdminUserId: number): Observable<ApiResponse<null>> {
    return this.http.put<ApiResponse<null>>(`${BASE}/${orgId}/admin`, { newAdminUserId });
  }

  /**
   * Sends the user a fresh 7-day invite link (the old one stops working) — optionally to a corrected e-mail,
   * which is saved first. 400 when the user has already set up their account (use Set password instead).
   */
  reinviteOrgUser(orgId: string, userId: number, email?: string | null): Observable<ApiResponse<OrgUserSummary>> {
    return this.http.post<ApiResponse<OrgUserSummary>>(
      `${BASE}/${orgId}/users/${userId}/reinvite`, { email: email?.trim() || null });
  }

  /**
   * A51 P7 - the same, sent now through the checked send path: the answer says whether the mail went out and, only when it did not,
   * carries the invite link for the super admin to copy. The old link stops working.
   */
  resendInvitation(orgId: string, userId: number, email?: string | null): Observable<ApiResponse<ResendInvitationResult>> {
    return this.http.post<ApiResponse<ResendInvitationResult>>(
      `${BASE}/${orgId}/users/${userId}/resend-invitation`, { email: email?.trim() || null });
  }
}

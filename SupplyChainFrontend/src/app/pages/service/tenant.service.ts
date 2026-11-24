import { HttpClient } from '@angular/common/http';
import { Injectable, computed, signal } from '@angular/core';
import { tap } from 'rxjs';
import { ApiResponse } from './auth.service';
import { environment } from '../../../environments/environment';

/**
 * A51 P4 - one entry of GET /api/tenant/current's featureStates (CurrentTenantFeatureState).
 * `usable` is exactly what enabledFeatureCodes lists; `isVisible` = definition visible OR the org is grandfathered;
 * `includedInPlan` = the org's plan includes it or the org holds a licence row for it.
 */
export interface FeatureState {
  code: string;
  status: 'Live' | 'Beta' | 'ComingSoon' | 'Deprecated' | string;
  isVisible: boolean;
  usable: boolean;
  includedInPlan: boolean;
}

// Mirrors SMS.Modules.Tenancy.Models.CurrentTenantModel (GET /api/tenant/current, MT-004).
export interface CurrentTenant {
  id: string;
  orgCode: string;
  orgName: string;
  plan: string;
  /** The organization's base currency (a lookups currency id); null when none is configured. */
  baseCurrency?: string | null;
  enabledFeatureCodes: string[];
  /** A51 P4 - per-feature status for the menu and route guard; absent on an older API (then enabledFeatureCodes alone decides). */
  featureStates?: FeatureState[];
  isSuperAdmin: boolean;
  roleName: string;
  permissions: string[];
}

// Role display name seeded for EnumRole.OrgAdmin (AuthDataSeeder.cs) — the one non-Super-Admin
// role the sidebar treats as "sees every enabled feature regardless of its own permission grants".
const ORG_ADMIN_ROLE_NAME = 'Organization Admin';

// Loaded once per authenticated session (AppLayout.ngOnInit — mirrors the existing
// NotificationService.init() "connect once on shell mount" pattern) and read reactively by the
// sidebar via the `tenant` signal, since the HTTP response arrives after the sidebar's own
// ngOnInit runs.
@Injectable({
  providedIn: 'root',
})
export class TenantService {
  private readonly apiUrl = `${environment.apiUrl}/tenant`;

  readonly tenant = signal<CurrentTenant | null>(null);

  /** A51 P4 - featureStates by code (empty while the tenant is not loaded or the API predates the list). */
  readonly featureStates = computed(() => new Map((this.tenant()?.featureStates ?? []).map((s) => [s.code, s] as const)));

  constructor(private http: HttpClient) {}

  loadCurrent() {
    return this.http.get<ApiResponse<CurrentTenant>>(`${this.apiUrl}/current`).pipe(
      tap((response) => {
        if (response.success && response.result) {
          this.tenant.set(response.result);
        }
      })
    );
  }

  hasFeature(featureCode: string): boolean {
    return this.tenant()?.enabledFeatureCodes.includes(featureCode) ?? false;
  }

  /** A51 P4 - the state of one feature, or undefined when the API sent none for it. */
  featureState(featureCode: string): FeatureState | undefined {
    return this.featureStates().get(featureCode);
  }

  /** A51 P4 - the Angular counterpart of useFeatureGuard: can the org use this feature now? Super admin: yes. */
  isFeatureUsable(featureCode: string): boolean {
    if (this.isSuperAdmin()) return true;
    const state = this.featureState(featureCode);
    return state ? state.usable : this.hasFeature(featureCode);
  }

  /** A51 P4 - visible to the org (a feature without a state counts as visible, like before). */
  isFeatureVisible(featureCode: string): boolean {
    if (this.isSuperAdmin()) return true;
    return this.featureState(featureCode)?.isVisible ?? true;
  }

  /** A51 P4 - visible, ComingSoon, in the org's plan and not usable: the menu shows it greyed with a badge. */
  isComingSoon(featureCode: string): boolean {
    const s = this.featureState(featureCode);
    return !!s && s.status === 'ComingSoon' && s.isVisible && s.includedInPlan && !s.usable;
  }

  isSuperAdmin(): boolean {
    return this.tenant()?.isSuperAdmin ?? false;
  }

  isOrgAdmin(): boolean {
    return this.tenant()?.roleName === ORG_ADMIN_ROLE_NAME;
  }

  clear(): void {
    this.tenant.set(null);
  }
}

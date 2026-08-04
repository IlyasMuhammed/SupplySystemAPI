import { HttpClient } from '@angular/common/http';
import { Injectable, signal } from '@angular/core';
import { tap } from 'rxjs';
import { ApiResponse } from './auth.service';

// Mirrors SMS.Modules.Tenancy.Models.CurrentTenantModel (GET /api/tenant/current, MT-004).
export interface CurrentTenant {
  id: string;
  orgCode: string;
  orgName: string;
  plan: string;
  enabledFeatureCodes: string[];
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
  private readonly apiUrl = 'https://localhost:52800/api/tenant';

  readonly tenant = signal<CurrentTenant | null>(null);

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

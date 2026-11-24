import { TestBed } from '@angular/core/testing';
import { Router, UrlTree } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { CurrentTenant, FeatureState, TenantService } from '../service/tenant.service';
import { DASHBOARD_URL, featureVisibilityGuard, featuresForPath, isPathBlocked } from './feature-visibility.guard';

const st = (code: string, over: Partial<FeatureState> = {}): FeatureState =>
  ({ code, status: 'Live', isVisible: true, usable: true, includedInPlan: true, ...over });

const tenant = (states: FeatureState[], isSuperAdmin = false): CurrentTenant => ({
  id: 'o', orgCode: 'O', orgName: 'O', plan: 'STANDARD', enabledFeatureCodes: states.filter(s => s.usable).map(s => s.code),
  featureStates: states, isSuperAdmin, roleName: 'Organization Admin', permissions: []
});

describe('featureVisibilityGuard (A51 P4)', () => {
  let tenants: TenantService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    tenants = TestBed.inject(TenantService);
  });

  const run = (url: string) =>
    TestBed.runInInjectionContext(() => featureVisibilityGuard({} as any, { url } as any)) as boolean | UrlTree;

  it('maps a URL to the feature set of the menu entry that owns it (longest link wins)', () => {
    expect(featuresForPath('/portal/pages/pos/stores/123')).toEqual([['MODULE_POS']]);
    expect(featuresForPath('/portal/pages/pos/credit-notes')).toEqual([['MODULE_POS', 'FEATURE_POS_CREDIT_NOTES']]);
    expect(featuresForPath('/portal/pages/not-in-the-menu')).toEqual([]);
  });

  it('TenantService helpers: featureState / isFeatureUsable / isFeatureVisible / isComingSoon', () => {
    tenants.tenant.set(tenant([
      st('A'), st('B', { status: 'ComingSoon', usable: false }), st('C', { isVisible: false, usable: false })
    ]));
    expect(tenants.featureState('B')?.status).toBe('ComingSoon');
    expect(tenants.isFeatureUsable('A')).toBeTrue();
    expect(tenants.isFeatureUsable('B')).toBeFalse();
    expect(tenants.isFeatureUsable('unknown')).toBeFalse();
    expect(tenants.isFeatureVisible('C')).toBeFalse();
    expect(tenants.isFeatureVisible('unknown')).toBeTrue();
    expect(tenants.isComingSoon('B')).toBeTrue();
    expect(tenants.isComingSoon('A')).toBeFalse();
  });

  it('lets a usable feature through', () => {
    tenants.tenant.set(tenant([st('MODULE_POS')]));
    expect(run('/portal/pages/pos/stores')).toBeTrue();
  });

  it('redirects a ComingSoon feature URL to the dashboard', () => {
    tenants.tenant.set(tenant([st('MODULE_POS', { status: 'ComingSoon', usable: false })]));
    const result = run('/portal/pages/pos/stores') as UrlTree;
    expect(TestBed.inject(Router).serializeUrl(result)).toBe(DASHBOARD_URL);
  });

  it('redirects a hidden feature and one that is in neither the plan nor the licences', () => {
    tenants.tenant.set(tenant([st('MODULE_POS', { usable: false, isVisible: false })]));
    expect(run('/portal/pages/pos/orders')).toBeInstanceOf(UrlTree);
    tenants.tenant.set(tenant([st('MODULE_POS', { usable: false, includedInPlan: false })]));
    expect(run('/portal/pages/pos/orders')).toBeInstanceOf(UrlTree);
  });

  it('does not block a licensed feature that is merely switched off (grace / read-only belong to the module screens)', () => {
    tenants.tenant.set(tenant([st('MODULE_POS', { usable: false })]));
    expect(run('/portal/pages/pos/orders')).toBeTrue();
  });

  it('never blocks the super admin, an API without featureStates, or a URL the menu does not name', () => {
    tenants.tenant.set(tenant([st('MODULE_POS', { status: 'ComingSoon', usable: false })], true));
    expect(isPathBlocked('/portal/pages/pos/stores', tenants)).toBeFalse();
    tenants.tenant.set({ ...tenant([]), featureStates: undefined });
    expect(isPathBlocked('/portal/pages/pos/stores', tenants)).toBeFalse();
    tenants.tenant.set(tenant([st('MODULE_POS', { status: 'ComingSoon', usable: false })]));
    expect(isPathBlocked('/portal/pages/some/unlisted/route', tenants)).toBeFalse();
  });
});

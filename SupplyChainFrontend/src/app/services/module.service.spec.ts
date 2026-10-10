import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ModuleAdminService, ModuleService } from './module.service';
import { TenantService, CurrentTenant } from '../pages/service/tenant.service';
import { daysLeft, moduleName } from './module.models';

function tenant(o: Partial<CurrentTenant> = {}): CurrentTenant {
  return { id: 'org', orgCode: 'O', orgName: 'Org', plan: 'ENTERPRISE', enabledFeatureCodes: [], isSuperAdmin: false,
           roleName: 'Organization Admin', permissions: [], ...o };
}

describe('ModuleService (A37 D-15)', () => {
  let service: ModuleService;
  let tenants: TenantService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(ModuleService);
    tenants = TestBed.inject(TenantService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function answer(result: any) {
    http.expectOne(r => r.url.endsWith('/tenant/modules/enabled')).flush({ success: true, message: '', result });
  }

  it('falls back to GET /api/tenant/current until the enabled set has loaded', () => {
    tenants.tenant.set(tenant({ enabledFeatureCodes: ['MODULE_INVENTORY'] }));
    expect(service.loaded()).toBeFalse();
    expect(service.isEnabled('MODULE_INVENTORY')).toBeTrue();
    expect(service.isEnabled('MODULE_MANUFACTURING')).toBeFalse();
  });

  it('answers from the loaded modules and features; a module in grace is not usable but reports its end', () => {
    tenants.tenant.set(tenant({ enabledFeatureCodes: ['MODULE_MANUFACTURING'] }));
    service.load().subscribe();
    answer({ modules: ['MODULE_INVENTORY'], features: ['FEATURE_BOM_MANAGEMENT'],
             grace: [{ code: 'MODULE_MANUFACTURING', graceEndsAt: '2026-11-09T00:00:00Z' }] });

    expect(service.loaded()).toBeTrue();
    expect(service.version()).toBe(1);
    expect(service.isEnabled('MODULE_INVENTORY')).toBeTrue();
    expect(service.isFeatureEnabled('FEATURE_BOM_MANAGEMENT')).toBeTrue();
    expect(service.isEnabled('MODULE_MANUFACTURING')).toBeFalse();          // loaded set wins over tenant/current
    expect(service.isInGrace('MODULE_MANUFACTURING')).toBeTrue();
    expect(service.graceFor('MODULE_MANUFACTURING')).toBe('2026-11-09T00:00:00Z');
    expect(service.graceFor('MODULE_INVENTORY')).toBeNull();
    expect(service.allEnabled(['MODULE_INVENTORY', 'FEATURE_BOM_MANAGEMENT'])).toBeTrue();
    expect(service.allEnabled(['MODULE_INVENTORY', 'MODULE_SERVICES'])).toBeFalse();
  });

  it('lets a Super Admin through everything (outside organization scope)', () => {
    tenants.tenant.set(tenant({ isSuperAdmin: true }));
    expect(service.isEnabled('MODULE_ANYTHING')).toBeTrue();
  });

  it('keeps the previous state when a reload fails', () => {
    tenants.tenant.set(tenant());
    service.load().subscribe();
    answer({ modules: ['MODULE_INVENTORY'], features: [], grace: [] });
    let out: unknown = 'x';
    service.load().subscribe(r => (out = r));
    http.expectOne(r => r.url.endsWith('/tenant/modules/enabled')).flush({}, { status: 500, statusText: 'err' });
    expect(out).toBeNull();
    expect(service.isEnabled('MODULE_INVENTORY')).toBeTrue();
  });

  it('refresh() reloads both the enabled set and tenant/current (the menu gates)', () => {
    tenants.tenant.set(tenant());
    service.refresh();
    answer({ modules: ['MODULE_LOGISTICS'], features: [], grace: [] });
    http.expectOne(r => r.url.endsWith('/tenant/current'))
      .flush({ success: true, message: '', result: tenant({ enabledFeatureCodes: ['MODULE_LOGISTICS'] }) });
    expect(service.isEnabled('MODULE_LOGISTICS')).toBeTrue();
    expect(tenants.hasFeature('MODULE_LOGISTICS')).toBeTrue();
  });

  it('forgets the organization\'s modules on logout (tenant cleared)', () => {
    tenants.tenant.set(tenant());
    service.load().subscribe();
    answer({ modules: ['MODULE_INVENTORY'], features: [], grace: [] });
    tenants.clear();
    TestBed.flushEffects();
    expect(service.loaded()).toBeFalse();
  });

  it('ModuleAdminService calls the §1.1 routes with the contract bodies', () => {
    const admin = TestBed.inject(ModuleAdminService);
    admin.list().subscribe();
    http.expectOne(r => r.method === 'GET' && r.url.endsWith('/tenant/modules')).flush({ success: true, result: [] });
    admin.enable('MODULE_SERVICES', 'rv1').subscribe();
    const en = http.expectOne(r => r.url.endsWith('/tenant/modules/MODULE_SERVICES/enable'));
    expect(en.request.method).toBe('POST');
    expect(en.request.body).toEqual({ rowVersion: 'rv1' });
    en.flush({ success: true });
    admin.disable('MODULE_SERVICES', { graceDays: 10, rowVersion: 'rv1' }).subscribe();
    const dis = http.expectOne(r => r.url.endsWith('/tenant/modules/MODULE_SERVICES/disable'));
    expect(dis.request.body).toEqual({ graceDays: 10, rowVersion: 'rv1' });
    dis.flush({ success: true });
    admin.setFeature('MODULE_LOGISTICS', 'FEATURE_PICK_LISTS', false, 'rv2').subscribe();
    const ft = http.expectOne(r => r.url.endsWith('/tenant/modules/MODULE_LOGISTICS/features/FEATURE_PICK_LISTS'));
    expect(ft.request.method).toBe('PUT');
    expect(ft.request.body).toEqual({ enabled: false, rowVersion: 'rv2' });
    ft.flush({ success: true });
    admin.history('MODULE_LOGISTICS').subscribe();
    http.expectOne(r => r.url.endsWith('/tenant/modules/MODULE_LOGISTICS/history')).flush({ success: true, result: [] });
    admin.impact('MODULE_LOGISTICS').subscribe();
    http.expectOne(r => r.url.endsWith('/tenant/modules/MODULE_LOGISTICS/impact')).flush({ success: true, result: null });
  });

  it('names codes and counts grace days', () => {
    expect(moduleName('MODULE_MANUFACTURING')).toBe('Manufacturing');
    expect(moduleName('FEATURE_SOMETHING_NEW')).toBe('Something new');
    const now = new Date('2026-10-10T00:00:00Z');
    expect(daysLeft('2026-10-20T00:00:00Z', now)).toBe(10);
    expect(daysLeft('2026-10-01T00:00:00Z', now)).toBe(0);
    expect(daysLeft(null, now)).toBeNull();
  });
});

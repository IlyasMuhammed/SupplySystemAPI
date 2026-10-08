import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { Router } from '@angular/router';

import { MoneyService } from './money.service';
import { AuthService } from '../pages/service/auth.service';
import { CurrentTenant, TenantService } from '../pages/service/tenant.service';
import { environment } from '../../environments/environment';

const API = environment.apiUrl;
const USD_ID = '11111111-1111-1111-1111-111111111111';
const JPY_ID = '22222222-2222-2222-2222-222222222222';
const CHF_ID = '33333333-3333-3333-3333-333333333333';

const ORG_CURRENCIES = [
  { currencyId: USD_ID, code: 'USD', name: 'US Dollar', symbol: '$', decimalPlaces: 2, rounding: 0.01, symbolPosition: 'before', isActive: true, displayOrder: 2 },
  { currencyId: JPY_ID, code: 'JPY', name: 'Japanese Yen', symbol: '¥', decimalPlaces: 0, rounding: 1, symbolPosition: 'before', isActive: true, displayOrder: 8 },
  { currencyId: CHF_ID, code: 'CHF', name: 'Swiss Franc', symbol: 'CHF', decimalPlaces: 2, rounding: 0.01, symbolPosition: 'after', isActive: false, displayOrder: 18 }
];

/** A35 D-21 — formats with the org's currencies (api/currencies), loaded once and shared. */
describe('MoneyService', () => {
  let service: MoneyService;
  let http: HttpTestingController;

  let navigate: jasmine.Spy;

  beforeEach(() => {
    navigate = jasmine.createSpy('navigate');
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), { provide: Router, useValue: { navigate } }]
    });
    service = TestBed.inject(MoneyService);
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(TenantService).tenant.set(tenant('org-a'));
  });

  function tenant(id: string): CurrentTenant {
    return { id, orgCode: id, orgName: id, plan: 'ENTERPRISE', enabledFeatureCodes: [], isSuperAdmin: false, roleName: 'Accountant', permissions: [] };
  }

  it('REV-04: on a hard refresh (asked before the tenant is known) it loads the org list once the tenant arrives, unasked', () => {
    const tenants = TestBed.inject(TenantService);
    tenants.tenant.set(null);
    service.ensureLoaded();
    // Whatever was fetched before the tenant was known is not that organization's list.
    http.match(r => r.url === `${API}/currencies`).forEach(r => r.flush({ success: true, message: '', result: [] }));
    expect(service.format(1234, JPY_ID)).toBe('1,234.00');

    tenants.tenant.set(tenant('org-a'));
    TestBed.flushEffects();
    flushCurrencies();
    expect(service.format(1234, JPY_ID)).toBe('¥1,234');
  });

  it('REV-03: after a logout and a login into another organization, formats with that organization\'s currencies', () => {
    service.ensureLoaded();
    flushCurrencies();
    expect(service.format(1234, JPY_ID)).toBe('¥1,234');

    localStorage.removeItem('refreshToken');
    TestBed.inject(AuthService).logout();
    expect(navigate).toHaveBeenCalledWith(['/auth/login']);
    // Signed out: the previous organization's list is no longer used.
    expect(service.currencies()).toEqual([]);
    expect(service.format(1234, JPY_ID)).toBe('1,234.00');

    // Login into org B: the shell loads the tenant, the next ensureLoaded fetches B's list (JPY with 2 decimals there).
    const tenants = TestBed.inject(TenantService);
    tenants.loadCurrent().subscribe();
    http.expectOne(`${API}/tenant/current`).flush({ success: true, message: '', result: tenant('org-b') });
    expect(service.format(1234, JPY_ID)).toBe('1,234.00');
    service.ensureLoaded();
    http.expectOne(r => r.url === `${API}/currencies`).flush({ success: true, message: '', result: [
      { ...ORG_CURRENCIES[1], symbol: 'JP¥', decimalPlaces: 2 }
    ] });
    expect(service.format(1234, JPY_ID)).toBe('JP¥ 1,234.00');

    // Switching organizations never shows the other one's list.
    tenants.tenant.set(tenant('org-a'));
    expect(service.currencies()).toEqual([]);
    tenants.tenant.set(tenant('org-b'));
    expect(service.find(JPY_ID)?.symbol).toBe('JP¥');
  });

  afterEach(() => http.verify());

  function flushCurrencies(): void {
    const req = http.expectOne(r => r.url === `${API}/currencies`);
    expect(req.request.method).toBe('GET');
    req.flush({ success: true, message: '', result: ORG_CURRENCIES });
  }

  it('loads the org currencies once, however many ask', () => {
    service.ensureLoaded();
    service.ensureLoaded();
    service.load().subscribe();
    flushCurrencies();
    expect(service.currencies().length).toBe(3);
    service.ensureLoaded();
    http.expectNone(`${API}/currencies`);
  });

  it('formats by currency id or code with the org formatting (inactive currencies too — old documents use them)', () => {
    service.ensureLoaded();
    flushCurrencies();
    expect(service.format(1234, JPY_ID)).toBe('¥1,234');
    expect(service.format(1234, 'jpy')).toBe('¥1,234');
    expect(service.format(100, CHF_ID)).toBe('100.00 CHF');
    expect(service.format(6000, USD_ID, { display: 'code' })).toBe('USD 6,000.00');
  });

  it('before the list arrives (or for an unknown id) it still shows a sensible amount', () => {
    expect(service.format(5, 'PKR')).toBe('PKR 5.00');
    expect(service.format(5, null)).toBe('5.00');
    service.ensureLoaded();
    flushCurrencies();
    expect(service.format(5, '99999999-9999-9999-9999-999999999999')).toBe('5.00');
  });

  it('finds a currency and its decimals', () => {
    service.ensureLoaded();
    flushCurrencies();
    expect(service.find(USD_ID)?.code).toBe('USD');
    expect(service.find('usd')?.currencyId).toBe(USD_ID);
    expect(service.decimalsOf(JPY_ID)).toBe(0);
    expect(service.decimalsOf('XXX')).toBe(2);
    expect(service.round(2.5, JPY_ID)).toBe(3);
  });

  it('REV-05: a failed load is not retried by itself for the same org; reload() or another org tries again', () => {
    service.ensureLoaded();
    http.expectOne(r => r.url === `${API}/currencies`).flush('no', { status: 403, statusText: 'Forbidden' });
    expect(service.currencies()).toEqual([]);
    for (let i = 0; i < 5; i++) service.ensureLoaded();
    http.expectNone(r => r.url === `${API}/currencies`);

    service.reload();
    flushCurrencies();
    expect(service.currencies().length).toBe(3);

    TestBed.inject(TenantService).tenant.set(tenant('org-b'));
    service.ensureLoaded();
    http.expectOne(r => r.url === `${API}/currencies`).flush('boom', { status: 500, statusText: 'Server Error' });
    TestBed.inject(TenantService).tenant.set(tenant('org-c'));
    service.ensureLoaded();
    flushCurrencies();
  });

  it('reload() fetches again after a currency was changed', () => {
    service.ensureLoaded();
    flushCurrencies();
    service.reload();
    flushCurrencies();
  });
});

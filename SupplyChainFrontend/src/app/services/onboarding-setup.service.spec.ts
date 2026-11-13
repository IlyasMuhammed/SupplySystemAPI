import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { OnboardingSetupService } from './onboarding-setup.service';
import { environment } from '../../environments/environment';

/** A50 — the onboarding setup endpoints (API-CONTRACT D-CORE) and the session's cached status. */
describe('OnboardingSetupService', () => {
  let svc: OnboardingSetupService;
  let http: HttpTestingController;
  const base = `${environment.apiUrl}/onboarding/setup`;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    svc = TestBed.inject(OnboardingSetupService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('loadStatus reads GET status once and caches it', () => {
    let got: any;
    svc.loadStatus().subscribe(s => (got = s));
    http.expectOne(`${base}/status`).flush({ success: true, result: { setupStatus: 'NotStarted', canRunWizard: true } });
    expect(got.setupStatus).toBe('NotStarted');
    expect(svc.status()?.setupStatus).toBe('NotStarted');
    svc.loadStatus().subscribe(s => (got = s));
    http.expectNone(`${base}/status`);
    svc.loadStatus(true).subscribe();
    http.expectOne(`${base}/status`).flush({ success: true, result: null });
    expect(svc.status()).toBeNull();
  });

  it('a status error resolves to null and is not cached', () => {
    let got: any = 'x';
    svc.loadStatus().subscribe(s => (got = s));
    http.expectOne(`${base}/status`).flush({}, { status: 500, statusText: 'err' });
    expect(got).toBeNull();
    expect(svc.status()).toBeUndefined();
  });

  it('industries passes the country; preview / apply / skip POST their bodies', () => {
    svc.industries('PK').subscribe();
    http.expectOne(r => r.url === `${base}/industries` && r.params.get('country') === 'PK').flush({ success: true, result: [] });

    svc.preview({ countryCode: 'PK', industryType: 'Trading', categories: null }).subscribe();
    const p = http.expectOne(`${base}/preview`);
    expect(p.request.method).toBe('POST');
    expect(p.request.body).toEqual({ countryCode: 'PK', industryType: 'Trading', categories: null });
    p.flush({ success: true, result: [] });

    svc.status.set({ setupStatus: 'InProgress', setupStep: 3, countryCode: 'PK', industryType: 'Trading', completedAt: null, canRunWizard: true });
    svc.apply({ countryCode: 'PK', industryType: 'Trading', payloadOverrides: {}, wizardInputs: {}, skipCategories: [], invites: [] }).subscribe();
    http.expectOne(`${base}/apply`).flush({ success: true, result: { setupStatus: 'Completed', completed: true, results: [], invites: [] } });
    expect(svc.status()?.setupStatus).toBe('Completed');

    svc.skip().subscribe();
    const s = http.expectOne(`${base}/skip`);
    expect(s.request.method).toBe('POST');
    s.flush({ success: true, result: { setupStatus: 'Skipped', completed: true, results: [], invites: [] } });
    expect(svc.status()?.setupStatus).toBe('Skipped');
  });

  it('saveState posts { data, countryCode, industryType } to state/{step}', () => {
    svc.saveState(3, { payload: { accounts: [] } }, 'PK', 'Trading').subscribe();
    const r = http.expectOne(`${base}/state/3`);
    expect(r.request.body).toEqual({ data: { payload: { accounts: [] } }, countryCode: 'PK', industryType: 'Trading' });
    r.flush({ success: true, result: { setupStatus: 'InProgress', setupStep: 3, stepData: {} } });

    svc.saveState(6, { warehouses: [] }).subscribe();
    const r2 = http.expectOne(`${base}/state/6`);
    expect(r2.request.body).toEqual({ data: { warehouses: [] } });
    r2.flush({ success: true, result: null });
  });

  it('getState, categories and reset', () => {
    svc.getState().subscribe();
    http.expectOne(`${base}/state`).flush({ success: true, result: {} });
    svc.categories().subscribe();
    http.expectOne(`${base}/categories`).flush({ success: true, result: {} });
    svc.reset('TAX', 'Full', 'latest').subscribe();
    const r = http.expectOne(`${base}/categories/TAX/reset`);
    expect(r.request.body).toEqual({ mode: 'Full', confirm: true, source: 'latest' });
    r.flush({ success: true, result: {} });
  });
});

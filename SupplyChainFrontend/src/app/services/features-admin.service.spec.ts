import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FeaturesAdminService } from './features-admin.service';
import { environment } from '../../environments/environment';

/** A51 P2 - the exact routes of the features and plans admin API. */
describe('FeaturesAdminService', () => {
  let http: HttpTestingController;
  let svc: FeaturesAdminService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    svc = TestBed.inject(FeaturesAdminService);
  });

  afterEach(() => http.verify());

  it('features: list, PUT status, PUT visibility', () => {
    const base = `${environment.apiUrl}/system/features`;
    svc.listFeatures().subscribe(); http.expectOne(base).flush({ success: true, result: [] });
    svc.setStatus('MODULE_POS', 'ComingSoon').subscribe();
    const s = http.expectOne(`${base}/MODULE_POS/status`); expect(s.request.method).toBe('PUT'); expect(s.request.body).toEqual({ status: 'ComingSoon' }); s.flush({ success: true, result: {} });
    svc.setVisibility('MODULE_POS', false).subscribe();
    const v = http.expectOne(`${base}/MODULE_POS/visibility`); expect(v.request.method).toBe('PUT'); expect(v.request.body).toEqual({ isVisible: false }); v.flush({ success: true, result: {} });
  });

  it('plans: list, get, PUT fields, PUT features', () => {
    const base = `${environment.apiUrl}/system/plans`;
    svc.listPlans().subscribe(); http.expectOne(base).flush({ success: true, result: [] });
    svc.getPlan('BASIC').subscribe(); http.expectOne(`${base}/BASIC`).flush({ success: true, result: {} });
    svc.updatePlan('BASIC', { name: 'Default', description: null, monthlyPrice: null, isActive: true, sortOrder: 10 }).subscribe();
    const u = http.expectOne(`${base}/BASIC`); expect(u.request.method).toBe('PUT'); u.flush({ success: true, result: {} });
    svc.updatePlanFeatures('BASIC', [{ featureCode: 'MODULE_FINANCE', isIncluded: true }]).subscribe();
    const f = http.expectOne(`${base}/BASIC/features`); expect(f.request.method).toBe('PUT');
    expect(f.request.body).toEqual({ features: [{ featureCode: 'MODULE_FINANCE', isIncluded: true }] }); f.flush({ success: true, result: {} });
  });

  it('A51 P7: apply-to-organizations POSTs the mode and the dry-run flag', () => {
    const base = `${environment.apiUrl}/system/plans`;
    svc.applyToOrganizations('BASIC', 'addOnly', true).subscribe();
    const a = http.expectOne(`${base}/BASIC/apply-to-organizations`);
    expect(a.request.method).toBe('POST');
    expect(a.request.body).toEqual({ mode: 'addOnly', dryRun: true });
    a.flush({ success: true, result: {} });
    svc.applyToOrganizations('STANDARD', 'full', false).subscribe();
    const b = http.expectOne(`${base}/STANDARD/apply-to-organizations`);
    expect(b.request.body).toEqual({ mode: 'full', dryRun: false });
    b.flush({ success: true, result: {} });
  });
});

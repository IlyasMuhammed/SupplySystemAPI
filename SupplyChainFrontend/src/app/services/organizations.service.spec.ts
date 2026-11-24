import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { OrganizationsService } from './organizations.service';
import { environment } from '../../environments/environment';

/** A51 P7 - the routes the tenant-management additions call. */
describe('OrganizationsService - A51 P7', () => {
  let http: HttpTestingController;
  let svc: OrganizationsService;
  const base = `${environment.apiUrl}/system/organizations`;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    svc = TestBed.inject(OrganizationsService);
  });

  afterEach(() => http.verify());

  it('reads the background default-data status', () => {
    svc.getDefaultsStatus('org-1').subscribe(r => expect(r.result.status).toBe('Running'));
    const r = http.expectOne(`${base}/org-1/defaults-status`);
    expect(r.request.method).toBe('GET');
    r.flush({ success: true, message: '', result: { organizationId: 'org-1', status: 'Running', categories: [] } });
  });

  it('resends an invitation, with a corrected address only when one is given', () => {
    svc.resendInvitation('org-1', 7, ' new@acme.test ').subscribe();
    const a = http.expectOne(`${base}/org-1/users/7/resend-invitation`);
    expect(a.request.method).toBe('POST');
    expect(a.request.body).toEqual({ email: 'new@acme.test' });
    a.flush({ success: true, result: { emailSent: true, emailConfigured: true } });

    svc.resendInvitation('org-1', 7, null).subscribe();
    const b = http.expectOne(`${base}/org-1/users/7/resend-invitation`);
    expect(b.request.body).toEqual({ email: null });
    b.flush({ success: true, result: { emailSent: false, emailConfigured: false, inviteLink: 'x' } });
  });

  it('resets a plan template and returns what it added, removed and skipped', () => {
    svc.applyPlanTemplate('org-1').subscribe(r => expect(r.result!.skipped[0].reason).toBe('ComingSoon'));
    const r = http.expectOne(`${base}/org-1/apply-plan-template`);
    expect(r.request.method).toBe('POST');
    r.flush({ success: true, message: '', result: { organizationId: 'org-1', plan: 'BASIC', added: [], removed: [], skipped: [{ featureCode: 'MODULE_POS', featureName: 'POS', reason: 'ComingSoon' }] } });
  });
});

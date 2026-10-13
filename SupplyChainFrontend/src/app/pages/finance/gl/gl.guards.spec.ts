import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, UrlTree, provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';

import { AccountingService } from '../../../services/accounting.service';
import { TenantService } from '../../service/tenant.service';
import { glFeatureGuard, glSetupCompleteGuard, glSetupRequiredInterceptor } from './gl.guards';

describe('GL guards & interceptor (A42 D-4)', () => {
  const run = (guard: any) => TestBed.runInInjectionContext(() => guard({} as any, {} as any));
  const resolve = (r: any): Promise<any> => r instanceof Observable ? new Promise(res => r.subscribe(res)) : Promise.resolve(r);

  describe('glSetupCompleteGuard', () => {
    let api: jasmine.SpyObj<AccountingService>;
    beforeEach(() => {
      api = jasmine.createSpyObj<AccountingService>('AccountingService', ['getSetupStatus']);
      TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AccountingService, useValue: api }] });
    });

    it('lets the dashboard open when setup is Complete', async () => {
      api.getSetupStatus.and.returnValue(of({ success: true, message: '', result: { status: 'Complete', nextStep: 6 } as any }));
      expect(await resolve(run(glSetupCompleteGuard))).toBeTrue();
    });

    it('sends the user to the wizard with the gate banner and the next step otherwise', async () => {
      api.getSetupStatus.and.returnValue(of({ success: true, message: '', result: { status: 'FiscalYearSet', nextStep: 4 } as any }));
      const tree = await resolve(run(glSetupCompleteGuard)) as UrlTree;
      expect(tree instanceof UrlTree).toBeTrue();
      expect(TestBed.inject(Router).serializeUrl(tree)).toBe('/portal/pages/finance/gl/setup?gate=1&next=4');
    });

    it('a failed status read lets the page open (it shows its own error)', async () => {
      api.getSetupStatus.and.returnValue(throwError(() => new Error('x')));
      expect(await resolve(run(glSetupCompleteGuard))).toBeTrue();
    });
  });

  describe('glFeatureGuard', () => {
    let tenant: any;
    beforeEach(() => {
      tenant = {
        current: null as any,
        tenant: () => tenant.current,
        isSuperAdmin: () => !!tenant.current?.isSuperAdmin,
        hasFeature: (c: string) => !!tenant.current?.enabledFeatureCodes.includes(c),
        loadCurrent: jasmine.createSpy('loadCurrent').and.callFake(() => { tenant.current = { enabledFeatureCodes: ['FEATURE_GENERAL_LEDGER'] }; return of({}); })
      };
      TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: TenantService, useValue: tenant }] });
    });

    it('passes with FEATURE_GENERAL_LEDGER, waits for the tenant when not loaded yet', async () => {
      expect(await resolve(run(glFeatureGuard))).toBeTrue();
      expect(tenant.loadCurrent).toHaveBeenCalled();
    });

    it('access denied when the feature is off', async () => {
      tenant.current = { enabledFeatureCodes: ['MODULE_FINANCE'] };
      const tree = await resolve(run(glFeatureGuard));
      expect(TestBed.inject(Router).serializeUrl(tree)).toBe('/portal/access-denied');
    });
  });

  describe('glSetupRequiredInterceptor', () => {
    let http: HttpClient;
    let ctrl: HttpTestingController;
    let navigate: jasmine.Spy;
    beforeEach(() => {
      TestBed.configureTestingModule({
        providers: [provideRouter([]), provideHttpClient(withInterceptors([glSetupRequiredInterceptor])), provideHttpClientTesting()]
      });
      http = TestBed.inject(HttpClient);
      ctrl = TestBed.inject(HttpTestingController);
      navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    });

    it('403 FINANCE_SETUP_REQUIRED → the wizard (redirectUrl), error passed on', () => {
      let status = 0;
      http.get('/api/finance/gl/dashboard').subscribe({ error: e => status = e.status });
      ctrl.expectOne('/api/finance/gl/dashboard').flush(
        { error: 'FINANCE_SETUP_REQUIRED', message: 'Finish setup', currentStatus: 'DefaultsSet', redirectUrl: '/portal/pages/finance/gl/setup' },
        { status: 403, statusText: 'Forbidden' });
      expect(status).toBe(403);
      expect(navigate).toHaveBeenCalledWith(['/portal/pages/finance/gl/setup'], { queryParams: { gate: 1 } });
    });

    it('leaves ordinary 403s alone', () => {
      http.get('/api/x').subscribe({ error: () => {} });
      ctrl.expectOne('/api/x').flush({ message: 'You do not have permission.' }, { status: 403, statusText: 'Forbidden' });
      expect(navigate).not.toHaveBeenCalled();
    });
  });
});

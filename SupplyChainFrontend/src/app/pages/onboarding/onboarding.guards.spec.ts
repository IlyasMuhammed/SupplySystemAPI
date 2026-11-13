import { TestBed } from '@angular/core/testing';
import { Router, UrlTree, provideRouter } from '@angular/router';
import { Observable, isObservable, of, throwError } from 'rxjs';
import { setupWizardRedirectGuard } from './onboarding.guards';
import { OnboardingSetupService } from '../../services/onboarding-setup.service';
import { AuthService } from '../service/auth.service';
import { TenantService } from '../service/tenant.service';

/** A50 D-7 / BR-50-001 — the first-login redirect on /portal/dashboard. */
describe('setupWizardRedirectGuard', () => {
  let perms: string[];
  let superAdmin: boolean;
  let status$: Observable<any>;

  function run(): Promise<boolean | UrlTree> {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { hasPermission: (c: string) => perms.includes(c) } },
        { provide: TenantService, useValue: { isSuperAdmin: () => superAdmin } },
        { provide: OnboardingSetupService, useValue: { loadStatus: () => status$ } }
      ]
    });
    const out = TestBed.runInInjectionContext(() => setupWizardRedirectGuard({} as any, {} as any)) as any;
    return isObservable(out) ? new Promise(res => (out as Observable<any>).subscribe(res)) : Promise.resolve(out);
  }

  beforeEach(() => { perms = ['SETUP_WIZARD_RUN']; superAdmin = false; status$ = of(null); });

  it('sends a setup admin of a NotStarted organization to the wizard', async () => {
    status$ = of({ setupStatus: 'NotStarted', canRunWizard: true });
    const r = await run();
    expect(r instanceof UrlTree).toBeTrue();
    expect(TestBed.inject(Router).serializeUrl(r as UrlTree)).toBe('/portal/pages/onboarding/setup');
  });

  it('lets the dashboard open once setup started, finished or was skipped', async () => {
    for (const s of ['InProgress', 'Completed', 'Skipped']) {
      TestBed.resetTestingModule();
      status$ = of({ setupStatus: s, canRunWizard: true });
      expect(await run()).toBeTrue();
    }
  });

  it('never redirects users without SETUP_WIZARD_RUN, nor the super admin, nor on errors', async () => {
    perms = [];
    status$ = of({ setupStatus: 'NotStarted', canRunWizard: false });
    expect(await run()).toBeTrue();

    TestBed.resetTestingModule();
    perms = ['SETUP_WIZARD_RUN'];
    superAdmin = true;
    expect(await run()).toBeTrue();

    TestBed.resetTestingModule();
    superAdmin = false;
    status$ = throwError(() => new Error('down'));
    expect(await run()).toBeTrue();

    TestBed.resetTestingModule();
    status$ = of(null);
    expect(await run()).toBeTrue();
  });
});

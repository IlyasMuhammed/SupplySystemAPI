import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { GlSetupWizardComponent, maxReachableStep } from './gl-setup-wizard.component';
import { AccountingService } from '../../../../services/accounting.service';
import { ImportService } from '../../../../services/import.service';
import { OrgCurrencyService } from '../../../../services/org-currency.service';
import { AuthService } from '../../../service/auth.service';
import { fakeApi, fakeAuth, okRes, status, statusDone } from '../gl.testing';

describe('GlSetupWizardComponent (A42 Phase 5)', () => {
  let fixture: ComponentFixture<GlSetupWizardComponent>;
  let c: GlSetupWizardComponent;
  let api: jasmine.SpyObj<AccountingService>;
  let navigate: jasmine.Spy;

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const btn = (id: string) => (q(id)?.querySelector('button') ?? q(id)) as HTMLButtonElement;

  async function setup(s = status(), query: Record<string, string> = {}, perms = ['GL_VIEW', 'GL_SETUP_MANAGE']) {
    api = fakeApi();
    api.getSetupStatus.and.returnValue(okRes(s));
    const importApi = jasmine.createSpyObj<ImportService>('ImportService', ['getOpeningBalanceDate', 'setOpeningBalanceDate']);
    importApi.getOpeningBalanceDate.and.returnValue(okRes({ date: null }));
    const currency = jasmine.createSpyObj<OrgCurrencyService>('OrgCurrencyService', ['getCurrencies', 'getSettings']);
    currency.getCurrencies.and.returnValue(okRes([]) as any);
    currency.getSettings.and.returnValue(okRes({ saleBaseCurrencyCode: 'PKR' }) as any);
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlSetupWizardComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), MessageService,
        { provide: AccountingService, useValue: api },
        { provide: ImportService, useValue: importApi },
        { provide: OrgCurrencyService, useValue: currency },
        { provide: AuthService, useValue: fakeAuth(perms) },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } }
      ]
    }).compileComponents();
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture = TestBed.createComponent(GlSetupWizardComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('opens on the server nextStep and shows its title, help and template step', async () => {
    await setup(statusDone(3));
    expect(c.step).toBe(4);
    expect(q('step-title')!.textContent).toContain('Step 4 of 6');
    expect(q('help-toggle')).not.toBeNull();
    await setup(status());
    expect(c.step).toBe(1);
    expect(fixture.nativeElement.querySelector('app-gl-template-step')).not.toBeNull();
  });

  it('Continue stays off until the step prerequisite is met; then moves on', async () => {
    await setup(status());
    expect(c.canContinue).toBeFalse();
    expect(btn('continue').disabled).toBeTrue();
    c.status = statusDone(1);
    fixture.detectChanges();
    expect(btn('continue').disabled).toBeFalse();
    c.next();
    expect(c.step).toBe(2);
  });

  it('steps beyond the next one are closed; done steps can be revisited', async () => {
    await setup(statusDone(3));
    expect(maxReachableStep(c.status)).toBe(4);
    expect((q('step-5') as HTMLButtonElement).disabled).toBeTrue();
    expect((q('step-2') as HTMLButtonElement).disabled).toBeFalse();
    c.go(5);
    expect(c.step).toBe(4);
    c.go(2);
    expect(c.step).toBe(2);
    c.back();
    expect(c.step).toBe(1);
  });

  it('?step= cannot jump past the reachable step; Complete opens the review', async () => {
    await setup(statusDone(1), { step: '5' });
    expect(c.step).toBe(2);
    await setup(statusDone(6, { status: 'Complete', setupCompletedByName: 'Ayesha' }));
    expect(c.step).toBe(6);
    expect(q('complete-note')!.textContent).toContain('Ayesha');
    expect(q('finalize')).toBeNull();
  });

  it('gate banner (from the dashboard guard / 403) names the next step', async () => {
    await setup(statusDone(2), { gate: '1' });
    expect(q('gate-banner')!.textContent).toContain('Set up the fiscal year');
    await setup(statusDone(2));
    expect(q('gate-banner')).toBeNull();
  });

  it('the template is locked once the fiscal year is set or opening balances are posted', async () => {
    await setup(statusDone(1));
    expect(c.templateLocked).toBeFalse();
    c.status = statusDone(3);
    expect(c.templateLocked).toBeTrue();
    c.status = statusDone(1, { openingBalancesEntered: true });
    expect(c.templateLocked).toBeTrue();
  });

  it('applying a template re-reads the status and opens step 2', async () => {
    await setup(status());
    api.getSetupStatus.and.returnValue(okRes(statusDone(1)));
    c.onApplied();
    expect(c.step).toBe(2);
  });

  it('review: Finish is enabled only when steps 1–5 are done; complete → dashboard', async () => {
    await setup(statusDone(4));
    c.status = statusDone(4);
    expect(c.canFinalize).toBeFalse();
    c.status = statusDone(5);
    c.go(6);
    fixture.detectChanges();
    expect(api.getFiscalYears).toHaveBeenCalled();
    expect(btn('finalize').disabled).toBeFalse();
    api.completeSetup.and.returnValue(okRes(statusDone(6, { status: 'Complete' })));
    c.finalize();
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/finance/gl']);
  });

  it('a 409 on Finish lists the unmet prerequisites', async () => {
    await setup(statusDone(5));
    c.go(6);
    api.completeSetup.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409,
      error: { message: 'Setup cannot be completed yet.', errors: ['Opening balances not entered'] } })));
    c.finalize();
    fixture.detectChanges();
    expect(q('finalize-error')!.textContent).toContain('Setup cannot be completed yet.');
    expect(q('finalize-error')!.textContent).toContain('Opening balances not entered');
  });

  it('read-only notice without GL_SETUP_MANAGE (no Finish button)', async () => {
    await setup(statusDone(5), {}, ['GL_VIEW']);
    expect(q('read-only')).not.toBeNull();
    c.go(6);
    fixture.detectChanges();
    expect(q('finalize')).toBeNull();
  });

  it('a failed status load says so', async () => {
    await setup();
    api.getSetupStatus.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
    c.status = null;
    c.ngOnInit();
    fixture.detectChanges();
    expect(q('load-error')).not.toBeNull();
    // ConfirmationService is the component's own.
    expect(fixture.debugElement.injector.get(ConfirmationService)).toBeTruthy();
    expect(of).toBeDefined();
  });
});

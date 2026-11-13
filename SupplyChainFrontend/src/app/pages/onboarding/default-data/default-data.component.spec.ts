import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';
import { DefaultDataComponent } from './default-data.component';
import { DefaultDataOverview, OnboardingSetupService } from '../../../services/onboarding-setup.service';

/** A50 P1-L tasks 147–154 — Default Data dashboard. */
describe('DefaultDataComponent', () => {
  let fixture: ComponentFixture<DefaultDataComponent>;
  let c: DefaultDataComponent;
  let api: jasmine.SpyObj<OnboardingSetupService>;
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  const row = (category: string, extra: any = {}) => ({
    category, displayName: category, requiresModule: 'MODULE_FINANCE', moduleEnabled: true, hasSeeder: true, seededCount: 6, currentCount: 7,
    modifiedCount: 0, missingCount: 0, modifiedKeys: [], missingKeys: [], isModified: false, lastAppliedAt: '2026-11-09T10:00:00Z',
    lastAction: 'Seed', appliedVersion: 1, latestVersion: 1, updateAvailable: false, ...extra
  });
  const overview = (): DefaultDataOverview => ({
    countryCode: 'PK', industryType: 'Trading', setupStatus: 'Completed', completedAt: '2026-11-09T10:00:00Z',
    categories: [
      row('TAX', { modifiedCount: 1, isModified: true, modifiedKeys: ['GST18'], latestVersion: 2, updateAvailable: true }),
      row('COA', { requiresModule: 'FEATURE_GENERAL_LEDGER', missingCount: 2, missingKeys: ['1111', '1112'] }),
      row('DOC_NUMBERING', { requiresModule: null }),
      row('WAREHOUSE', { moduleEnabled: false, requiresModule: 'MODULE_INVENTORY' })
    ]
  });

  async function setup(categories: any = of({ success: true, result: overview() })) {
    api = jasmine.createSpyObj<OnboardingSetupService>('OnboardingSetupService', ['categories', 'reset']);
    api.categories.and.returnValue(categories);
    await TestBed.configureTestingModule({
      imports: [DefaultDataComponent],
      providers: [provideRouter([]), provideNoopAnimations(), MessageService, { provide: OnboardingSetupService, useValue: api }]
    }).compileComponents();
    fixture = TestBed.createComponent(DefaultDataComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('shows the template info and one row per category with counts, modified and version', async () => {
    await setup();
    expect(q('t-country').textContent).toContain('PK');
    expect(q('t-industry').textContent).toContain('Trading');
    expect(q('t-status').textContent).toContain('Completed');
    expect(q('seeded-TAX').textContent.trim()).toBe('6');
    expect(q('current-TAX').textContent.trim()).toBe('7');
    expect(q('modified-TAX').textContent).toContain('1 changed');
    expect(q('modified-COA').textContent).toContain('2 missing');
    expect(q('version-TAX').textContent).toContain('v2 available');
  });

  it('View links go to the existing screens; categories without one have none', async () => {
    await setup();
    expect(q('view-COA').getAttribute('href')).toBe('/portal/pages/finance/gl/accounts');
    expect(q('view-TAX').getAttribute('href')).toBe('/portal/pages/finance-setup/tax-codes');
    expect(q('view-DOC_NUMBERING')).toBeNull();
  });

  it('Reset is off while the module is off', async () => {
    await setup();
    expect(q('reset-WAREHOUSE').querySelector('button').disabled).toBeTrue();
  });

  it('runs a Merge reset and reloads on close', async () => {
    await setup();
    api.reset.and.returnValue(of({ success: true, message: '', result: { category: 'TAX', status: 'Applied', action: 'Merge', ok: true, created: 1, skipped: 6, removed: 0, warnings: [], message: null } }));
    c.openReset(overview().categories[0]);
    fixture.detectChanges();
    expect(c.resetMode).toBe('Merge');
    c.doReset();
    expect(api.reset).toHaveBeenCalledWith('TAX', 'Merge', 'applied');
    fixture.detectChanges();
    expect(c.resetDone?.created).toBe(1);
    c.closeReset();
    expect(api.categories).toHaveBeenCalledTimes(2);
  });

  it('a Full reset blocked by references shows "X records are referenced by documents"', async () => {
    await setup();
    api.reset.and.returnValue(throwError(() => ({ status: 409, error: { success: false, message: 'in use', result: { referencedCount: 2, referencedKeys: ['GST18', 'INPUT18'] } } })));
    c.openReset(overview().categories[0]);
    c.resetMode = 'Full';
    c.useLatest = true;
    c.doReset();
    expect(api.reset).toHaveBeenCalledWith('TAX', 'Full', 'latest');
    fixture.detectChanges();
    expect(document.querySelector('[data-testid="reset-blocked"]')!.textContent).toContain('2 records are referenced by documents');
    expect(c.resetError).toBe('');
  });

  it('other reset errors show the server message', async () => {
    await setup();
    api.reset.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Confirm is required.' } })));
    c.openReset(overview().categories[0]);
    c.doReset();
    expect(c.resetError).toBe('Confirm is required.');
  });

  it('"Run setup wizard again" opens the wizard', async () => {
    await setup();
    const router = TestBed.inject(Router);
    spyOn(router, 'navigate');
    c.runWizard();
    expect(router.navigate).toHaveBeenCalledWith(['/portal/pages/onboarding/setup']);
  });

  it('shows the error state', async () => {
    await setup(throwError(() => ({ status: 500, error: { message: 'Boom' } })));
    expect(q('error').textContent).toContain('Boom');
  });
});

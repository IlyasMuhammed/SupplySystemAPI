import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { signal } from '@angular/core';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';
import { SetupWizardComponent } from './setup-wizard.component';
import { OnboardingSetupService } from '../../../services/onboarding-setup.service';
import { ModuleAdminService, ModuleService } from '../../../services/module.service';
import { CurrenciesService } from '../../../services/currencies.service';
import { QuickBooksIntegrationService } from '../../../services/quickbooks-integration.service';
import { AuthService } from '../../service/auth.service';
import { TenantService } from '../../service/tenant.service';
import { samplePreviews } from '../onboarding.testing';

/** A50 P1-K tasks 123–146 — the setup wizard. */
describe('SetupWizardComponent', () => {
  let fixture: ComponentFixture<SetupWizardComponent>;
  let c: SetupWizardComponent;
  let api: jasmine.SpyObj<OnboardingSetupService>;
  let moduleAdmin: jasmine.SpyObj<ModuleAdminService>;
  let qbo: jasmine.SpyObj<QuickBooksIntegrationService>;
  let enabled: Set<string>;
  let perms: string[];
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  const card = (code: string, extra: any = {}) => ({
    code, name: code.replace('MODULE_', ''), description: `About ${code}`, isAlwaysOn: false, isAvailable: true, isLicensed: true, isEnabled: true,
    status: 'ACTIVE', dependsOn: [], dependents: [], features: [], featureCount: 0, enabledFeatureCount: 0, rowVersion: 'rv', ...extra
  });

  async function setup(opts: { state?: any; stateError?: boolean; query?: Record<string, string> } = {}) {
    enabled = new Set(['MODULE_DEMAND', 'MODULE_INVENTORY', 'MODULE_INTEGRATION']);
    perms = perms ?? ['SETUP_WIZARD_RUN', 'MODULES_MANAGE', 'INTEGRATION_VIEW'];
    api = jasmine.createSpyObj<OnboardingSetupService>('OnboardingSetupService',
      ['getState', 'countries', 'industries', 'preview', 'saveState', 'apply', 'skip', 'loadStatus']);
    const state = opts.state ?? { setupStatus: 'NotStarted', setupStep: 0, countryCode: null, industryType: null, stepData: {} };
    api.getState.and.returnValue(opts.stateError ? throwError(() => ({ status: 500, error: { message: 'State down' } })) : of({ success: true, message: '', result: state }));
    api.countries.and.returnValue(of({ success: true, message: '', result: [{ code: 'PK', name: 'Pakistan' }, { code: 'GLOBAL', name: 'Other / Global' }] }));
    api.industries.and.returnValue(of({ success: true, message: '', result: [{ code: 'Trading', name: 'Trading' }, { code: 'GLOBAL', name: 'General' }] }));
    api.preview.and.callFake(() => of({ success: true, message: '', result: samplePreviews() }));
    api.saveState.and.returnValue(of({ success: true, message: '', result: { ...state, setupStatus: 'InProgress' } }));
    api.loadStatus.and.returnValue(of(null));
    moduleAdmin = jasmine.createSpyObj<ModuleAdminService>('ModuleAdminService', ['list', 'enable', 'disable']);
    moduleAdmin.list.and.returnValue(of({ success: true, message: '', result: [
      card('MODULE_FINANCE', { isAlwaysOn: true, status: 'ALWAYS_ON' }), card('MODULE_DEMAND'), card('MODULE_WAREHOUSE', { isEnabled: false, status: 'DISABLED' }),
      card('MODULE_MANUFACTURING', { isLicensed: false, isEnabled: false, status: 'NOT_LICENSED' }), card('MODULE_X', { status: 'COMING_SOON' })
    ] as any }));
    qbo = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', ['getConnection']);
    qbo.getConnection.and.returnValue(of({ isConnected: false, status: 'NotConnected' } as any));

    await TestBed.configureTestingModule({
      imports: [SetupWizardComponent],
      providers: [
        provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(opts.query ?? {}) } } },
        { provide: OnboardingSetupService, useValue: api },
        { provide: ModuleAdminService, useValue: moduleAdmin },
        { provide: ModuleService, useValue: { isEnabled: (c: string) => enabled.has(c), refresh: jasmine.createSpy('refresh') } },
        { provide: CurrenciesService, useValue: { getAll: () => of({ success: true, result: [{ id: '1', code: 'PKR', name: 'Pakistani Rupee', symbol: 'Rs' },
          { id: '2', code: 'USD', name: 'US Dollar', symbol: '$' }, { id: '3', code: 'EUR', name: 'Euro', symbol: '€' }] }) } },
        { provide: QuickBooksIntegrationService, useValue: qbo },
        { provide: AuthService, useValue: { hasPermission: (p: string) => perms.includes(p), hasAnyPermission: (...p: string[]) => p.some(x => perms.includes(x)) } },
        { provide: TenantService, useValue: { tenant: signal({ orgName: 'Acme Traders' }) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(SetupWizardComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  beforeEach(() => { perms = undefined as any; });

  it('Step 1: read-only company name, countries and industries from the API, defaults previewed', async () => {
    await setup();
    expect((q('org-name') as HTMLInputElement).value).toBe('Acme Traders');
    expect((q('org-name') as HTMLInputElement).readOnly).toBeTrue();
    expect(c.countryCode).toBe('PK');
    expect(api.industries).toHaveBeenCalledWith('PK');
    expect(c.industryType).toBe('Trading');
    expect(api.preview).toHaveBeenCalledWith({ countryCode: 'PK', industryType: 'Trading', categories: null });
    expect(c.payloads['TAX'].taxCodes.length).toBe(1);
    expect(q('step-title').textContent).toContain('Step 1 of 8');
  });

  it('changing the country reloads its industries and the defaults', async () => {
    await setup();
    c.countryCode = 'GLOBAL';
    c.onCountryChange();
    expect(api.industries).toHaveBeenCalledWith('GLOBAL');
    expect(api.preview.calls.mostRecent().args[0].countryCode).toBe('GLOBAL');
  });

  it('Continue saves the step to state/{step} and moves on', async () => {
    await setup();
    c.next();
    expect(api.saveState).toHaveBeenCalledWith(1, { countryCode: 'PK', industryType: 'Trading' }, 'PK', 'Trading');
    expect(c.step).toBe(2);
    expect(moduleAdmin.list).toHaveBeenCalled();
  });

  it('Step 2: licensed modules switch; always-on and unlicensed ones do not; coming-soon hidden', async () => {
    await setup();
    c.go(2);
    fixture.detectChanges();
    expect(c.moduleCards.map(m => m.code)).not.toContain('MODULE_X');
    const fin = c.moduleCards.find(m => m.code === 'MODULE_FINANCE')!;
    const mfg = c.moduleCards.find(m => m.code === 'MODULE_MANUFACTURING')!;
    const wh = c.moduleCards.find(m => m.code === 'MODULE_WAREHOUSE')!;
    expect(c.moduleSwitchable(fin)).toBeFalse();
    expect(c.moduleSwitchable(mfg)).toBeFalse();
    expect(c.moduleNote(mfg)).toBe('Not in your plan');
    expect(q('module-MODULE_DEMAND').textContent).toContain('About MODULE_DEMAND');
    moduleAdmin.enable.and.returnValue(of({ success: true, message: '', result: { ...wh, isEnabled: true } as any }));
    c.toggleModule(wh, true);
    expect(moduleAdmin.enable).toHaveBeenCalledWith('MODULE_WAREHOUSE', 'rv');
    expect(c.moduleCards.find(m => m.code === 'MODULE_WAREHOUSE')!.isEnabled).toBeTrue();
    expect(TestBed.inject(ModuleService).refresh).toHaveBeenCalled();
    moduleAdmin.disable.and.returnValue(throwError(() => ({ status: 409, error: { message: 'Has dependents' } })));
    c.toggleModule(c.moduleCards.find(m => m.code === 'MODULE_DEMAND')!, false);
    expect(moduleAdmin.disable).toHaveBeenCalledWith('MODULE_DEMAND', { rowVersion: 'rv' });
  });

  it('without MODULES_MANAGE the modules are read-only', async () => {
    perms = ['SETUP_WIZARD_RUN'];
    await setup();
    c.go(2);
    fixture.detectChanges();
    expect(q('modules-readonly')).toBeTruthy();
    expect(c.moduleSwitchable(c.moduleCards.find(m => m.code === 'MODULE_DEMAND')!)).toBeFalse();
  });

  it('Step 3: an edited chart is a payloadOverride; "Reset to default" restores the preview', async () => {
    await setup();
    c.go(2); c.go(3);
    fixture.detectChanges();
    expect(q('coa-editor')).toBeTruthy();
    const edited = { ...c.payloads['COA'], accounts: c.payloads['COA'].accounts.slice(0, 1) };
    c.setPayload('COA', edited);
    expect(c.dirty.has('COA')).toBeTrue();
    c.resetStep();
    expect(c.dirty.has('COA')).toBeFalse();
    expect(c.payloads['COA'].accounts.length).toBe(2);
  });

  it('Step 4 shows the unverified tax banner; a bad table stops Continue', async () => {
    await setup();
    c.go(2); c.go(3); c.go(4);
    fixture.detectChanges();
    expect(q('tax-unverified').textContent).toContain('check with your tax adviser');
    c.setPayload('TAX', { taxCodes: [{ code: '', name: '', rate: 5, usage: 'SALES' }] });
    expect(c.stepProblem).toContain('needs a code');
    expect(c.canContinue).toBeFalse();
    c.toggleSkip('TAX', false);
    expect(c.stepProblem).toBeNull();
  });

  it('Step 5: base and additional currencies, fiscal month', async () => {
    await setup();
    c.setBaseCurrency('USD');
    expect(c.payloads['CURRENCY'].base.code).toBe('USD');
    expect(c.additionalCurrencies).toEqual([]);
    c.setAdditionalCurrencies(['PKR', 'EUR', 'USD']);
    expect(c.additionalCurrencies).toEqual(['PKR', 'EUR']);
    c.setFiscal('startMonth', 1);
    expect(c.payloads['FISCAL'].startMonth).toBe(1);
    expect(c.dirty.has('CURRENCY') && c.dirty.has('FISCAL')).toBeTrue();
  });

  it('Step 6: default warehouse with default zones; add / zone / remove', async () => {
    await setup();
    expect(c.warehouses).toEqual([{ code: 'WH-MAIN', name: 'Main Warehouse', address: '', zones: ['RECEIVING', 'STORAGE'] }]);
    c.addWarehouse();
    expect(c.warehouses[1].code).toBe('WH02');
    c.setZone(c.warehouses[1], 'QUALITY_HOLD', true);
    expect(c.warehouses[1].zones).toEqual(['RECEIVING', 'STORAGE', 'QUALITY_HOLD']);
    c.removeWarehouse(1);
    expect(c.warehouses.length).toBe(1);
  });

  it('Step 7: roles filtered by enabled modules; QuickBooks opens in a new tab and shows its status', async () => {
    await setup();
    c.go(2); c.go(3); c.go(4); c.go(5); c.go(6); c.go(7);
    fixture.detectChanges();
    expect(c.roleOptions.map(r => r.value)).toEqual(['ORG_ADMIN', 'SALES_MANAGER']);
    c.addInvite();
    expect(c.invites[0].roleRef).toBe('SALES_MANAGER');
    c.invites[0].email = 'bad';
    expect(c.stepProblem).toContain('not a valid email');
    c.invites[0].email = 'ali@acme.pk';
    expect(c.stepProblem).toBeNull();
    expect(qbo.getConnection).toHaveBeenCalled();
    fixture.detectChanges();
    expect(q('qbo-status').textContent).toContain('Not connected');
    spyOn(window, 'open');
    c.openQbo();
    expect(window.open).toHaveBeenCalledWith('/portal/pages/integrations/quickbooks', '_blank', 'noopener');
  });

  it('Review: counts per category, module-off categories not applied, Launch sends overrides / inputs / invites', async () => {
    await setup();
    c.setPayload('TAX', { verified: false, taxCodes: [{ code: 'VAT', name: 'VAT', rate: 5, usage: 'SALES' }] });
    c.invites = [{ email: 'ali@acme.pk', firstName: 'Ali', lastName: '', roleRef: 'SALES_MANAGER' }];
    c.toggleSkip('ROLES', false);
    for (let s = 2; s <= 8; s++) c.go(s);
    fixture.detectChanges();
    expect(q('rv-count-COA').textContent.trim()).toBe('7');
    expect(q('rv-DOC_NUMBERING').classList).toContain('off');
    expect(c.launchProblems).toEqual([]);
    api.apply.and.returnValue(of({ success: true, message: '', result: {
      setupStatus: 'Completed', completed: true, invites: [{ email: 'ali@acme.pk', outcome: 'INVITED', userId: 7 }],
      results: [{ category: 'TAX', status: 'Applied', action: 'Seed', ok: true, created: 1, skipped: 0, removed: 0, warnings: [], message: null }]
    } as any }));
    c.launch();
    const body = api.apply.calls.mostRecent().args[0];
    expect(Object.keys(body.payloadOverrides)).toEqual(['TAX']);
    expect(body.skipCategories).toEqual(['ROLES']);
    expect(body.wizardInputs['WAREHOUSE'].warehouses[0]).toEqual({ code: 'WH-MAIN', name: 'Main Warehouse', address: null, zones: ['RECEIVING', 'STORAGE'] });
    expect(body.invites).toEqual([{ email: 'ali@acme.pk', firstName: 'Ali', lastName: null, roleRef: 'SALES_MANAGER' }]);
    fixture.detectChanges();
    expect(q('success').textContent).toContain('Your system is ready!');
    expect(q('invite-results').textContent).toContain('INVITED');
    expect(api.loadStatus).toHaveBeenCalledWith(true);
  });

  it('a failed category shows where it stopped and allows "Launch again"', async () => {
    await setup();
    for (let s = 2; s <= 8; s++) c.go(s);
    api.apply.and.returnValue(of({ success: true, message: '', result: {
      setupStatus: 'InProgress', completed: false, invites: [],
      results: [
        { category: 'CURRENCY', status: 'Applied', action: 'Seed', ok: true, created: 2, skipped: 0, removed: 0, warnings: [], message: null },
        { category: 'COA', status: 'Failed', action: 'Failed', ok: false, created: 0, skipped: 0, removed: 0, warnings: [], message: 'Chart locked' },
        { category: 'TAX', status: 'NotRun', action: null, ok: false, created: 0, skipped: 0, removed: 0, warnings: [], message: null }
      ]
    } as any }));
    c.launch();
    fixture.detectChanges();
    expect(q('apply-partial').textContent).toContain('Chart of accounts');
    expect(q('apply-partial').textContent).toContain('Chart locked');
    expect(q('res-TAX').textContent).toContain('Not run');
    expect(q('launch').textContent).toContain('Launch again');
  });

  it('resumes from GET state: step and drafts', async () => {
    await setup({ state: {
      setupStatus: 'InProgress', setupStep: 6, countryCode: 'PK', industryType: 'Trading',
      stepData: { '4': { payload: { verified: false, taxCodes: [] } }, '6': { warehouses: [{ code: 'W1', name: 'One', zones: [] }] },
        '7': { invites: [{ email: 'x@y.com', roleRef: 'ORG_ADMIN' }] }, '8': { skip: ['UOM'] } }
    } });
    expect(c.step).toBe(6);
    expect(c.dirty.has('TAX')).toBeTrue();
    expect(c.payloads['TAX'].taxCodes).toEqual([]);
    expect(c.warehouses[0].code).toBe('W1');
    expect(c.invites.length).toBe(1);
    expect(c.skip.has('UOM')).toBeTrue();
  });

  it('a completed setup opens at Step 1 with the re-run note', async () => {
    await setup({ state: { setupStatus: 'Completed', setupStep: 8, countryCode: 'PK', industryType: 'Trading', stepData: {}, completedAt: '2026-11-01T00:00:00Z' } });
    expect(c.step).toBe(1);
    expect(q('rerun-note').textContent).toContain('only adds what is missing');
    expect(q('skip')).toBeNull();
  });

  it('Skip setup applies the GLOBAL defaults and goes to the dashboard', async () => {
    await setup();
    api.skip.and.returnValue(of({ success: true, message: '', result: { setupStatus: 'Skipped', completed: true, results: [], invites: [] } }));
    const router = TestBed.inject(Router);
    spyOn(router, 'navigate');
    const confirm = fixture.debugElement.injector.get(ConfirmationService);
    spyOn(confirm, 'confirm').and.callFake((o: any) => { o.accept(); return confirm; });
    c.confirmSkip();
    expect(api.skip).toHaveBeenCalled();
    expect(router.navigate).toHaveBeenCalledWith(['/portal/dashboard']);
  });

  it('shows the load error', async () => {
    await setup({ stateError: true });
    expect(q('load-error').textContent).toContain('State down');
  });

  it('a step that cannot be saved warns but the wizard moves on', async () => {
    await setup();
    api.saveState.and.returnValue(throwError(() => ({ status: 500 })));
    const msgs = TestBed.inject(MessageService);
    spyOn(msgs, 'add');
    c.next();
    expect(c.step).toBe(2);
    expect(msgs.add).toHaveBeenCalledWith(jasmine.objectContaining({ summary: 'Progress not saved' }));
  });
});

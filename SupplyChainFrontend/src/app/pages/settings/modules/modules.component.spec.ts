import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { By } from '@angular/platform-browser';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { ModulesComponent, CardVm } from './modules.component';
import { ModuleAdminService, ModuleService } from '../../../services/module.service';
import { ModuleCard, ModuleFeature, ModuleImpact } from '../../../services/module.models';
import { AuthService } from '../../service/auth.service';
import { TenantService } from '../../service/tenant.service';

function feature(o: Partial<ModuleFeature>): ModuleFeature {
  return { code: 'FEATURE_X', name: 'X', isCore: false, isAvailable: true, isLicensed: true, isEnabled: true,
           requiredBy: [], autoManaged: false, ...o };
}

function card(o: Partial<ModuleCard>): ModuleCard {
  return { code: 'MODULE_X', name: 'X', isAlwaysOn: false, isAvailable: true, isLicensed: true, isEnabled: true,
           status: 'ACTIVE', dependsOn: [], dependents: [], features: [], featureCount: 0, enabledFeatureCount: 0,
           rowVersion: 'rv-' + (o.code ?? 'x'), ...o };
}

const inTwelveDays = new Date(Date.now() + 12 * 86_400_000 - 60_000).toISOString();

function cards(): ModuleCard[] {
  return [
    card({ code: 'MODULE_INVENTORY', name: 'Inventory', status: 'ALWAYS_ON', isAlwaysOn: true, featureCount: 3, enabledFeatureCount: 2,
           features: [
             feature({ code: 'FEATURE_MASTER_LEDGERS', name: 'Master ledgers', isCore: true }),
             feature({ code: 'FEATURE_BOM_MANAGEMENT', name: 'BOM management', autoManaged: true, requiredBy: ['FEATURE_QUALITY_INSPECTION'] }),
             feature({ code: 'FEATURE_STOCK_COUNTS', name: 'Stock counts', isAvailable: false, isEnabled: false })
           ] }),
    card({ code: 'MODULE_MANUFACTURING', name: 'Manufacturing', status: 'ACTIVE', icon: 'pi-cog',
           dependsOn: [{ code: 'MODULE_INVENTORY', name: 'Inventory', isEnabled: true }], featureCount: 1, enabledFeatureCount: 1,
           features: [feature({ code: 'FEATURE_QUALITY_INSPECTION', name: 'Quality inspection', requiresCode: 'FEATURE_BOM_MANAGEMENT' })] }),
    card({ code: 'MODULE_WAREHOUSE', name: 'Warehouse', status: 'GRACE', isEnabled: false, graceEndsAt: inTwelveDays,
           disabledAt: '2026-10-01T00:00:00Z', disabledByName: 'Ayesha' }),
    card({ code: 'MODULE_LOGISTICS', name: 'Logistics', status: 'DISABLED', isEnabled: false,
           dependsOn: [{ code: 'MODULE_WAREHOUSE', name: 'Warehouse', isEnabled: false }],
           features: [feature({ code: 'FEATURE_PICK_LISTS', name: 'Pick lists', isEnabled: false })], featureCount: 1 }),
    card({ code: 'MODULE_FINANCE', name: 'Finance', status: 'NOT_LICENSED', isLicensed: false, isEnabled: false }),
    card({ code: 'MODULE_POS', name: 'Point of Sale', status: 'COMING_SOON', isAvailable: false, isEnabled: false })
  ];
}

const IMPACT: ModuleImpact = {
  code: 'MODULE_MANUFACTURING', name: 'Manufacturing', dependents: [],
  inProgress: [{ label: 'production orders in progress', count: 3 }],
  willBlock: ['Block new production orders', 'Hide the Manufacturing menu'],
  notAffected: ['Delete any existing data', 'Affect BOM definitions (shared with Services)'],
  defaultGraceDays: 30
};

describe('ModulesComponent — Settings › Modules (A37 §11.3 / §15.1)', () => {
  let fixture: ComponentFixture<ModulesComponent>;
  let component: ModulesComponent;
  let api: jasmine.SpyObj<ModuleAdminService>;
  let modules: jasmine.SpyObj<ModuleService>;
  let toasts: jasmine.Spy;
  let permissions: string[];

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const vm = (code: string): CardVm => component.sections.flatMap(s => s.cards).find(c => c.card.code === code)!;

  async function setup(canManage = true) {
    permissions = canManage ? ['MODULES_VIEW', 'MODULES_MANAGE'] : ['MODULES_VIEW'];
    api = jasmine.createSpyObj<ModuleAdminService>('ModuleAdminService', ['list', 'enable', 'disable', 'setFeature', 'history', 'impact']);
    api.list.and.callFake(() => of({ success: true, message: '', result: cards() }));
    api.enable.and.returnValue(of({ success: true, message: '', result: card({}) }));
    api.disable.and.returnValue(of({ success: true, message: '', result: card({}) }));
    api.setFeature.and.returnValue(of({ success: true, message: '', result: card({}) }));
    api.impact.and.returnValue(of({ success: true, message: '', result: IMPACT }));
    api.history.and.returnValue(of({ success: true, message: '', result: [] }));
    modules = jasmine.createSpyObj<ModuleService>('ModuleService', ['refresh']);

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ModulesComponent],
      providers: [
        provideNoopAnimations(),
        { provide: ModuleAdminService, useValue: api },
        { provide: ModuleService, useValue: modules },
        { provide: AuthService, useValue: { hasPermission: (p: string) => permissions.includes(p) } },
        { provide: TenantService, useValue: { tenant: signal({ orgName: 'Acme Traders' }) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(ModulesComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('sorts the cards into Core, Active, Available and Coming soon with the right badges', async () => {
    await setup();
    expect(component.sections.map(s => s.key)).toEqual(['core', 'active', 'available', 'soon']);
    expect(q('section-core')!.querySelector('[data-testid="card-MODULE_INVENTORY"]')).not.toBeNull();
    expect(q('section-active')!.querySelector('[data-testid="card-MODULE_MANUFACTURING"]')).not.toBeNull();
    const available = q('section-available')!;
    for (const code of ['MODULE_WAREHOUSE', 'MODULE_LOGISTICS', 'MODULE_FINANCE']) {
      expect(available.querySelector(`[data-testid="card-${code}"]`)).withContext(code).not.toBeNull();
    }
    expect(q('section-soon')!.querySelector('[data-testid="card-MODULE_POS"]')).not.toBeNull();

    expect(q('badge-MODULE_INVENTORY')!.textContent).toContain('Always active');
    expect(q('badge-MODULE_MANUFACTURING')!.textContent).toContain('Active');
    expect(q('badge-MODULE_MANUFACTURING')!.className).toContain('ok');
    expect(q('badge-MODULE_WAREHOUSE')!.textContent).toContain('Grace period · 12 days left');
    expect(q('badge-MODULE_WAREHOUSE')!.className).toContain('wn');
    expect(q('badge-MODULE_LOGISTICS')!.textContent).toContain('Disabled');
    expect(q('badge-MODULE_FINANCE')!.textContent).toContain('Not in your plan');
    expect(q('badge-MODULE_POS')!.textContent).toContain('Coming soon');
    expect(q('badge-MODULE_POS')!.className).toContain('in');
    expect(fixture.nativeElement.textContent).toContain('Acme Traders');
  });

  it('shows switches only where the org admin can switch: not on core, unlicensed or coming-soon modules', async () => {
    await setup();
    expect(q('toggle-MODULE_INVENTORY')).toBeNull();
    expect(q('toggle-MODULE_FINANCE')).toBeNull();
    expect(q('toggle-MODULE_POS')).toBeNull();
    expect(q('toggle-MODULE_MANUFACTURING')).not.toBeNull();
    expect(q('toggle-MODULE_LOGISTICS')).not.toBeNull();
    expect(q('unlicensed-MODULE_FINANCE')!.textContent).toContain('contact your administrator');
  });

  it('lists requirements, feature counts and the grace end on the card', async () => {
    await setup();
    expect(q('requires-MODULE_MANUFACTURING')!.textContent).toContain('Requires: Inventory');
    expect(q('requires-MODULE_LOGISTICS')!.textContent).toContain('Requires: Warehouse (off)');
    expect(q('features-MODULE_INVENTORY')!.textContent).toContain('2 of 3 features');
    expect(q('grace-MODULE_WAREHOUSE')!.textContent).toContain('Open records can be finished until');
  });

  it('Manage features: core locked on, coming soon disabled, auto-managed note, requires / required-by', async () => {
    await setup();
    component.toggleExpanded(vm('MODULE_INVENTORY'));
    fixture.detectChanges();
    expect(q('feature-list-MODULE_INVENTORY')).not.toBeNull();
    expect(q('flock-FEATURE_MASTER_LEDGERS')).not.toBeNull();
    expect(q('ftoggle-FEATURE_MASTER_LEDGERS')).toBeNull();

    const fv = (code: string) => vm('MODULE_INVENTORY').features.find(f => f.f.code === code)!;
    expect(fv('FEATURE_STOCK_COUNTS').disabled).toBeTrue();
    expect(fv('FEATURE_STOCK_COUNTS').availability).toBe('Coming soon');
    expect(fv('FEATURE_BOM_MANAGEMENT').disabled).toBeFalse();
    expect(q('fnote-FEATURE_BOM_MANAGEMENT')!.textContent).toContain('automatically with Manufacturing and Services');
    expect(q('feature-FEATURE_BOM_MANAGEMENT')!.textContent).toContain('Required by: Quality inspection');

    component.toggleExpanded(vm('MODULE_MANUFACTURING'));
    fixture.detectChanges();
    expect(q('feature-FEATURE_QUALITY_INSPECTION')!.textContent).toContain('Requires: BOM management');

    // A feature of a switched-off module cannot be switched until the module is on.
    const pick = vm('MODULE_LOGISTICS').features[0];
    expect(pick.disabled).toBeTrue();
    expect(pick.note).toBe('Switch on Logistics first.');
  });

  it('keeps the expander open across a reload', async () => {
    await setup();
    component.toggleExpanded(vm('MODULE_MANUFACTURING'));
    component.load();
    expect(vm('MODULE_MANUFACTURING').expanded).toBeTrue();
  });

  it('switching a module on calls enable with its row version, then reloads and refreshes the app-wide modules', async () => {
    await setup();
    component.onToggle(vm('MODULE_LOGISTICS'), true);
    expect(api.enable).toHaveBeenCalledOnceWith('MODULE_LOGISTICS', 'rv-MODULE_LOGISTICS');
    expect(api.list).toHaveBeenCalledTimes(2);
    expect(modules.refresh).toHaveBeenCalledTimes(1);
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'success' }));
  });

  it('shows the server\'s 400 sentence verbatim and flips the switch back', async () => {
    await setup();
    api.enable.and.returnValue(throwError(() => new HttpErrorResponse({
      status: 400, error: { success: false, message: 'Enable Warehouse first.' } })));
    const logistics = vm('MODULE_LOGISTICS');
    component.onToggle(logistics, true);
    expect(logistics.on).toBeFalse();
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: 'Enable Warehouse first.' }));
    expect(modules.refresh).not.toHaveBeenCalled();
  });

  it('a 409 (changed elsewhere) reloads the page and says so', async () => {
    await setup();
    api.enable.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { message: 'stale' } })));
    component.onToggle(vm('MODULE_LOGISTICS'), true);
    expect(api.list).toHaveBeenCalledTimes(2);
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'warn', summary: 'Changed elsewhere' }));
  });

  it('switching off asks /impact first, then shows what it blocks, what it does not, in-progress counts and a 30-day grace', async () => {
    await setup();
    component.onToggle(vm('MODULE_MANUFACTURING'), false);
    expect(api.impact).toHaveBeenCalledOnceWith('MODULE_MANUFACTURING');
    expect(component.showDisable).toBeTrue();
    expect(component.graceDays).toBe(30);
    fixture.detectChanges();
    await fixture.whenStable();
    const body = document.body;
    expect(body.querySelector('[data-testid="will-block"]')!.textContent).toContain('Block new production orders');
    expect(body.querySelector('[data-testid="not-affected"]')!.textContent).toContain('Affect BOM definitions');
    expect(body.querySelector('[data-testid="in-progress"]')!.textContent).toContain('3');
    expect(body.querySelector('[data-testid="in-progress"]')!.textContent).toContain('production orders in progress');
    expect(api.disable).not.toHaveBeenCalled();

    component.graceDays = 14;
    component.notes = '  moving to a partner  ';
    component.confirmDisable();
    expect(api.disable).toHaveBeenCalledOnceWith('MODULE_MANUFACTURING',
      { graceDays: 14, notes: 'moving to a partner', rowVersion: 'rv-MODULE_MANUFACTURING' });
    expect(component.showDisable).toBeFalse();
    expect(modules.refresh).toHaveBeenCalled();
  });

  it('cancelling the disable dialog leaves the module on', async () => {
    await setup();
    const mfg = vm('MODULE_MANUFACTURING');
    component.onToggle(mfg, false);
    expect(mfg.on).toBeFalse();
    component.cancelDisable();
    expect(mfg.on).toBeTrue();
    expect(api.disable).not.toHaveBeenCalled();
  });

  it('refuses a grace period outside 0–365 before calling the server', async () => {
    await setup();
    component.onToggle(vm('MODULE_MANUFACTURING'), false);
    component.graceDays = 400;
    expect(component.graceValid).toBeFalse();
    component.confirmDisable();
    expect(api.disable).not.toHaveBeenCalled();
    component.graceDays = 0;
    expect(component.graceValid).toBeTrue();
  });

  it('shows the blocking "Cannot disable" dialog when enabled modules depend on it', async () => {
    await setup();
    api.impact.and.returnValue(of({ success: true, message: '', result: {
      ...IMPACT, code: 'MODULE_WAREHOUSE', name: 'Warehouse', dependents: [{ code: 'MODULE_LOGISTICS', name: 'Logistics' }] } }));
    const mfg = vm('MODULE_MANUFACTURING');
    component.onToggle(mfg, false);
    expect(component.showBlocked).toBeTrue();
    expect(component.showDisable).toBeFalse();
    expect(mfg.on).toBeTrue();
    expect(component.blockedNames).toBe('Logistics');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(document.body.querySelector('[data-testid="block-dialog"]')!.textContent).toContain('Disable Logistics first.');
  });

  it('switches a feature with the module\'s row version and reverts on refusal', async () => {
    await setup();
    const inv = vm('MODULE_INVENTORY');
    const bom = inv.features.find(f => f.f.code === 'FEATURE_BOM_MANAGEMENT')!;
    component.onFeatureToggle(inv, bom, false);
    expect(api.setFeature).toHaveBeenCalledOnceWith('MODULE_INVENTORY', 'FEATURE_BOM_MANAGEMENT', false, 'rv-MODULE_INVENTORY');
    expect(modules.refresh).toHaveBeenCalled();

    api.setFeature.and.returnValue(throwError(() => new HttpErrorResponse({
      status: 400, error: { message: 'Core features cannot be switched off.' } })));
    const again = vm('MODULE_INVENTORY').features.find(f => f.f.code === 'FEATURE_BOM_MANAGEMENT')!;
    component.onFeatureToggle(vm('MODULE_INVENTORY'), again, false);
    expect(again.on).toBeTrue();
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'Core features cannot be switched off.' }));
  });

  it('without MODULES_MANAGE: read-only note, switches disabled, nothing is called', async () => {
    await setup(false);
    expect(q('read-only-note')).not.toBeNull();
    const sw = fixture.debugElement.query(By.css('[data-testid="toggle-MODULE_MANUFACTURING"]'));
    expect(sw.componentInstance.disabled).toBeTrue();
    const mfg = vm('MODULE_MANUFACTURING');
    component.onToggle(mfg, false);
    expect(mfg.on).toBeTrue();
    expect(api.impact).not.toHaveBeenCalled();
    expect(vm('MODULE_INVENTORY').features.every(f => f.disabled)).toBeTrue();
  });

  it('opens the history drawer with readable entries', async () => {
    await setup();
    api.history.and.returnValue(of({ success: true, message: '', result: [
      { performedAt: '2026-10-09T10:00:00Z', action: 'DISABLED', performedByName: 'Ayesha', graceDays: 30, notes: 'seasonal' },
      { performedAt: '2026-10-08T10:00:00Z', action: 'AUTO_ENABLED', featureCode: 'FEATURE_BOM_MANAGEMENT', performedByName: 'System' }
    ] }));
    component.openHistory(vm('MODULE_INVENTORY'));
    expect(api.history).toHaveBeenCalledOnceWith('MODULE_INVENTORY');
    expect(component.showHistory).toBeTrue();
    expect(component.historyRows.map(r => r.action)).toEqual(['Switched off', 'Switched on automatically']);
    expect(component.historyRows[1].target).toBe('BOM management');
    expect(component.historyRows[0].graceDays).toBe(30);
  });
});

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';
import { PlansListComponent, planLocked } from './plans-list.component';
import { ApplyPlanResult, Plan, PlanFeatureItem, PlanWithFeatures, FeaturesAdminService } from '../../../services/features-admin.service';

/** A51 P2 - super admin Plans page: three cards, an edit view with every feature as a checkbox, Save writes the changes. */
describe('PlansListComponent', () => {
  let fixture: ComponentFixture<PlansListComponent>;
  let c: PlansListComponent;
  let api: jasmine.SpyObj<FeaturesAdminService>;
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  const plan = (code: string, name: string, included: number, price: number | null = null): Plan =>
    ({ id: 1, code, name, description: null, monthlyPrice: price, isActive: true, sortOrder: 1, includedFeatureCount: included, totalFeatureCount: 10 });
  const feat = (code: string, name: string, extra: Partial<PlanFeatureItem> = {}): PlanFeatureItem => ({
    featureCode: code, featureName: name, category: 'MODULE', parentModuleCode: null, isCore: false, isAvailable: true,
    licensedByHandOnly: false, isIncluded: false, displayOrder: 1, ...extra
  });
  const detail = (): PlanWithFeatures => ({
    ...plan('BASIC', 'Default', 2),
    features: [
      feat('MODULE_FINANCE', 'Finance', { displayOrder: 1 }),
      feat('SCREEN_BUDGET_MONITOR', 'Budget Monitor', { category: 'SCREEN', parentModuleCode: 'MODULE_FINANCE', displayOrder: 2 }),
      feat('MODULE_INVENTORY', 'Inventory', { isIncluded: true, isCore: true, displayOrder: 3 }),
      feat('MODULE_HRM', 'Human Resources', { licensedByHandOnly: true, displayOrder: 4 }),
      feat('MODULE_ECOMMERCE', 'E-commerce', { isAvailable: false, displayOrder: 5 })
    ]
  });
  const ok = <T>(result: T) => of({ success: true, message: '', result });
  const applyResult = (count: number, over: Partial<ApplyPlanResult> = {}): ApplyPlanResult => ({
    planCode: 'BASIC', mode: 'addOnly', dryRun: true, organizationCount: count, totalAdded: count, totalRemoved: 0, totalSkipped: 0,
    organizations: Array.from({ length: count }, (_, i) => ({
      organizationId: 'o' + i, orgCode: 'O' + i, orgName: 'Org ' + i, plan: 'BASIC',
      added: [{ featureCode: 'MODULE_FINANCE', featureName: 'Finance' }], removed: [], skipped: []
    })), ...over
  });

  async function setup() {
    api = jasmine.createSpyObj<FeaturesAdminService>('FeaturesAdminService', ['listPlans', 'getPlan', 'updatePlan', 'updatePlanFeatures', 'applyToOrganizations']);
    api.applyToOrganizations.and.returnValue(ok(applyResult(0)));
    api.listPlans.and.returnValue(ok([plan('BASIC', 'Default', 20), plan('STANDARD', 'Standard', 60, 49), plan('ENTERPRISE', 'Enterprise', 90)]));
    api.getPlan.and.returnValue(ok(detail()));
    await TestBed.configureTestingModule({
      imports: [PlansListComponent],
      providers: [provideRouter([]), provideNoopAnimations(), MessageService, { provide: FeaturesAdminService, useValue: api }]
    }).compileComponents();
    fixture = TestBed.createComponent(PlansListComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('shows three plan cards with name, price and included-feature count', async () => {
    await setup();
    expect(fixture.nativeElement.querySelectorAll('[data-testid^="plan-"]').length).toBe(3);
    expect(q('name-BASIC').textContent).toContain('Default');
    expect(q('price-BASIC').textContent).toContain('No price');
    expect(q('price-STANDARD').textContent).toContain('49.00');
    expect(q('count-STANDARD').textContent).toContain('60 of 10 features');
  });

  it('clicking a card opens the edit view with every feature as a checkbox, sub-features under their module', async () => {
    await setup();
    q('plan-BASIC').click();
    fixture.detectChanges();
    expect(api.getPlan).toHaveBeenCalledWith('BASIC');
    expect(c.selected?.code).toBe('BASIC');
    expect(fixture.nativeElement.querySelectorAll('[data-testid^="feature-"]').length).toBe(5);
    expect(c.groups.find(g => g.module.featureCode === 'MODULE_FINANCE')?.children.map(f => f.featureCode)).toEqual(['SCREEN_BUDGET_MONITOR']);
    expect(q('included-count').textContent).toContain('1');
  });

  it('every plan card has a clearly labelled Edit plan button that opens the edit view once', async () => {
    await setup();
    const buttons = fixture.nativeElement.querySelectorAll('[data-testid^="edit-"]');
    expect(buttons.length).toBe(3);
    expect(buttons[0].textContent).toContain('Edit plan');
    (q('edit-STANDARD').querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(api.getPlan).toHaveBeenCalledTimes(1);
    expect(api.getPlan).toHaveBeenCalledWith('STANDARD');
    expect(fixture.nativeElement.querySelector('h1').textContent).toContain('Edit plan');
  });

  it('core, unbuilt and licensed-by-hand features cannot be ticked', () => {
    const d = detail().features;
    expect(planLocked(d.find(f => f.featureCode === 'MODULE_INVENTORY')!)).toBeTrue();
    expect(planLocked(d.find(f => f.featureCode === 'MODULE_HRM')!)).toBeTrue();
    expect(planLocked(d.find(f => f.featureCode === 'MODULE_ECOMMERCE')!)).toBeTrue();
    expect(planLocked(d.find(f => f.featureCode === 'MODULE_FINANCE')!)).toBeFalse();
  });

  it('Save is off until something changes, then writes only the changed features', async () => {
    await setup();
    c.open('BASIC');
    expect(c.canSave).toBeFalse();
    c.toggle(c.groups[0].module, true);
    expect(c.canSave).toBeTrue();
    api.updatePlanFeatures.and.returnValue(ok(detail()));
    c.save();
    expect(api.updatePlan).not.toHaveBeenCalled();
    expect(api.updatePlanFeatures).toHaveBeenCalledWith('BASIC', [{ featureCode: 'MODULE_FINANCE', isIncluded: true }]);
  });

  it('unticking a module unticks its sub-features; ticking a sub-feature brings the module in', async () => {
    await setup();
    c.open('BASIC');
    const finance = c.groups.find(g => g.module.featureCode === 'MODULE_FINANCE')!;
    c.toggle(finance.children[0], true);
    expect(finance.module.isIncluded).toBeTrue();
    c.toggle(finance.module, false);
    expect(finance.children[0].isIncluded).toBeFalse();
    expect(c.changes).toEqual([]);
  });

  it('plan fields are sent through updatePlan, with the price as a number or null', async () => {
    await setup();
    c.open('BASIC');
    c.form = { ...c.form, name: 'Starter', monthlyPrice: 19.5 };
    api.updatePlan.and.returnValue(ok(plan('BASIC', 'Starter', 2, 19.5)));
    c.save();
    expect(api.updatePlan).toHaveBeenCalledWith('BASIC', jasmine.objectContaining({ name: 'Starter', monthlyPrice: 19.5, isActive: true }));
    expect(api.updatePlanFeatures).not.toHaveBeenCalled();
  });

  it('a refused save keeps the edit view with the server message', async () => {
    await setup();
    c.open('BASIC');
    c.toggle(c.groups[0].module, true);
    api.updatePlanFeatures.and.returnValue(throwError(() => ({ error: { message: 'Finance needs Inventory to be included too.' } })));
    c.save();
    expect(c.selected).toBeTruthy();
    expect(c.error).toContain('needs Inventory');
    expect(c.saving).toBeFalse();
  });

  // ── A51 P7: apply the plan to the organizations already on it ──

  async function saveAFeatureChange() {
    await setup();
    c.open('BASIC');
    c.toggle(c.groups[0].module, true);
    api.updatePlanFeatures.and.returnValue(ok(detail()));
  }

  it('after a feature change it asks how many organizations are on the plan (a dry run) and offers to apply it', async () => {
    await saveAFeatureChange();
    api.applyToOrganizations.and.returnValue(ok(applyResult(3)));

    c.save();
    fixture.detectChanges();

    expect(api.applyToOrganizations).toHaveBeenCalledOnceWith('BASIC', 'addOnly', true);
    expect(c.applyOpen).toBeTrue();
    const text = document.querySelector('[data-testid="apply-count"]')!.textContent!;
    expect(text).toContain('3');
    expect(text).toContain('organizations are on this plan');
    expect(document.querySelectorAll('[data-testid^="apply-org-"]').length).toBe(3);
    expect(c.applyMode).toBe('addOnly');
    expect(c.canApply).toBeTrue();
  });

  it('offers nothing when no organization is on the plan', async () => {
    await saveAFeatureChange();
    api.applyToOrganizations.and.returnValue(ok(applyResult(0)));
    c.save();
    expect(c.applyOpen).toBeFalse();
  });

  it('offers nothing when only the plan fields changed', async () => {
    await setup();
    c.open('BASIC');
    c.form = { ...c.form, name: 'Starter' };
    api.updatePlan.and.returnValue(ok(plan('BASIC', 'Starter', 2)));
    c.save();
    expect(api.applyToOrganizations).not.toHaveBeenCalled();
  });

  it('Add only applies for real (dryRun false) and reports the totals', async () => {
    await saveAFeatureChange();
    api.applyToOrganizations.and.returnValue(ok(applyResult(2)));
    c.save();
    api.applyToOrganizations.calls.reset();
    api.applyToOrganizations.and.returnValue(ok(applyResult(2, { dryRun: false, totalSkipped: 1 })));

    c.confirmApply();

    expect(api.applyToOrganizations).toHaveBeenCalledOnceWith('BASIC', 'addOnly', false);
    expect(c.applyOpen).toBeFalse();
    expect(c.applyPreview).toBeNull();
  });

  it('Full reset needs a confirmation, re-runs the preview in that mode and then applies it', async () => {
    await saveAFeatureChange();
    api.applyToOrganizations.and.returnValue(ok(applyResult(2)));
    c.save();

    api.applyToOrganizations.calls.reset();
    api.applyToOrganizations.and.returnValue(ok(applyResult(2, { mode: 'full', totalRemoved: 4 })));
    c.setApplyMode('full');
    expect(api.applyToOrganizations).toHaveBeenCalledOnceWith('BASIC', 'full', true);
    expect(c.applyPreview!.totalRemoved).toBe(4);
    expect(c.canApply).toBeFalse();

    c.confirmApply();
    expect(api.applyToOrganizations).toHaveBeenCalledTimes(1); // refused until the box is ticked

    c.fullConfirmed = true;
    expect(c.canApply).toBeTrue();
    api.applyToOrganizations.and.returnValue(ok(applyResult(2, { mode: 'full', dryRun: false })));
    c.confirmApply();
    expect(api.applyToOrganizations).toHaveBeenCalledWith('BASIC', 'full', false);
    expect(c.applyOpen).toBeFalse();
  });

  it('Not now closes the offer without applying anything', async () => {
    await saveAFeatureChange();
    api.applyToOrganizations.and.returnValue(ok(applyResult(1)));
    c.save();
    c.skipApply();
    expect(c.applyOpen).toBeFalse();
    expect(api.applyToOrganizations.calls.allArgs().every(a => a[2] === true)).toBeTrue();
  });

  it('a failed apply keeps the offer open with the server message', async () => {
    await saveAFeatureChange();
    api.applyToOrganizations.and.returnValue(ok(applyResult(1)));
    c.save();
    api.applyToOrganizations.and.returnValue(throwError(() => ({ error: { message: 'Nope.' } })));
    c.confirmApply();
    expect(c.applyOpen).toBeTrue();
    expect(c.applyError).toContain('Nope');
    expect(c.applying).toBeFalse();
  });

  it('back returns to the cards and reloads them', async () => {
    await setup();
    c.open('BASIC');
    c.back();
    expect(c.selected).toBeNull();
    expect(api.listPlans).toHaveBeenCalledTimes(2);
  });
});

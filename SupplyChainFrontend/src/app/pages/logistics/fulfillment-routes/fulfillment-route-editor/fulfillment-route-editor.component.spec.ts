import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { FulfillmentRouteEditorComponent, RouteSavedEvent } from './fulfillment-route-editor.component';
import { FulfillmentRouteModel, FulfillmentRoutesService } from '../../../../services/fulfillment-routes.service';

const STEP_LABELS: Record<string, string> = { PICK: 'Pick', PACK: 'Pack', STAGE: 'Stage', APPROVAL: 'Approval', GOODS_ISSUE: 'Goods Issue', SHIP: 'Ship' };

function route(codes: string[], over: Partial<FulfillmentRouteModel> = {}): FulfillmentRouteModel {
  return {
    uuid: 'r-1', code: 'CUSTOM', name: 'Custom', description: null, isDefault: false, isActive: true, isSystem: false,
    requiresPacking: codes.includes('PACK'), requiresShipping: codes.includes('SHIP'), displayOrder: 40,
    steps: codes.map((c, i) => ({ stepCode: c as any, label: STEP_LABELS[c], stepOrder: i + 1, isMandatory: true })),
    stepsText: codes.map(c => STEP_LABELS[c]).join(' → '), statusPath: [], createdDate: '', ...over
  };
}

const PICK_ONLY = route(['PICK', 'GOODS_ISSUE'], { uuid: 'r-po', code: 'PICK_ONLY', name: 'Pick Only', isSystem: true, isDefault: true, displayOrder: 10 });
const PICK_AND_SHIP = route(['PICK', 'GOODS_ISSUE', 'SHIP'], { uuid: 'r-ps', code: 'PICK_AND_SHIP', name: 'Pick & Ship', isSystem: true, isDefault: true, displayOrder: 20 });

describe('FulfillmentRouteEditorComponent (A33-PA-08)', () => {
  let fixture: ComponentFixture<FulfillmentRouteEditorComponent>;
  let component: FulfillmentRouteEditorComponent;
  let service: jasmine.SpyObj<FulfillmentRoutesService>;
  let el: HTMLElement;
  let saved: RouteSavedEvent[];

  async function setup(editing: FulfillmentRouteModel | null,
                       opts: { readOnly?: boolean; routes?: FulfillmentRouteModel[]; manufacturingEnabled?: boolean } = {}) {
    service = jasmine.createSpyObj<FulfillmentRoutesService>('FulfillmentRoutesService',
      ['createRoute', 'updateRoute', 'setDefault', 'clearDefault']);
    service.createRoute.and.callFake(req => of({ success: true, message: '', result: route(req.steps.map(s => s.stepCode), { uuid: 'r-new', code: req.code, name: req.name }) } as any));
    service.updateRoute.and.callFake((uuid, req) => of({ success: true, message: '', result: { ...editing!, name: req.name } } as any));
    service.setDefault.and.callFake(uuid => of({ success: true, message: '', result: route(['PICK', 'GOODS_ISSUE'], { uuid, isDefault: true }) } as any));
    service.clearDefault.and.callFake(uuid => of({ success: true, message: '', result: { ...editing!, isDefault: false } } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [FulfillmentRouteEditorComponent],
      providers: [provideNoopAnimations(), { provide: FulfillmentRoutesService, useValue: service }]
    }).compileComponents();

    fixture = TestBed.createComponent(FulfillmentRouteEditorComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('route', editing);
    fixture.componentRef.setInput('readOnly', !!opts.readOnly);
    fixture.componentRef.setInput('routes', opts.routes ?? [PICK_ONLY, PICK_AND_SHIP]);
    if (opts.manufacturingEnabled !== undefined) fixture.componentRef.setInput('manufacturingEnabled', opts.manufacturingEnabled);
    saved = [];
    component.saved.subscribe(e => saved.push(e));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const stepRows = () => Array.from(el.querySelectorAll('[data-testid^="step-row-"]')).map(r => r.getAttribute('data-testid')!.replace('step-row-', ''));
  const click = (id: string) => ((q(id)!.querySelector('button') ?? q(id)!) as HTMLElement).click();
  const refresh = async () => { fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges(); };

  // ── New route ──────────────────────────────────────────────────────────────────────────────────────────

  it('starts a new route with PICK and GOODS_ISSUE, which can be neither removed nor made optional', async () => {
    await setup(null);
    expect(stepRows()).toEqual(['PICK', 'GOODS_ISSUE']);
    expect(q('remove-step-PICK')).toBeNull();
    expect(q('remove-step-GOODS_ISSUE')).toBeNull();
    expect((q('mandatory-PICK')!.querySelector('input') as HTMLInputElement).disabled).toBeTrue();
    expect((q('code-input') as HTMLInputElement).disabled).toBeFalse();
  });

  it('offers only the allowed codes to add, and keeps added steps in canonical order (L-2)', async () => {
    await setup(null);
    expect(['PACK', 'STAGE', 'APPROVAL', 'SHIP'].every(c => !!q('add-step-' + c))).toBeTrue();
    expect(q('add-step-PICK')).toBeNull();
    click('add-step-SHIP');
    await refresh();
    click('add-step-PACK');
    await refresh();
    expect(stepRows()).toEqual(['PICK', 'PACK', 'GOODS_ISSUE', 'SHIP']);
    expect(q('add-step-PACK')).toBeNull();
    click('remove-step-PACK');
    await refresh();
    expect(stepRows()).toEqual(['PICK', 'GOODS_ISSUE', 'SHIP']);
  });

  it('previews the real delivery statuses live, ending at DELIVERED (never the spec\'s COMPLETED)', async () => {
    await setup(null);
    expect(q('status-preview')!.textContent).toContain('PICKED');
    expect(q('status-preview')!.textContent).not.toContain('IN_TRANSIT');
    expect(q('status-preview')!.textContent).toContain('Record collection');
    click('add-step-SHIP');
    click('add-step-PACK');
    await refresh();
    const text = q('status-preview')!.textContent!;
    expect(text).toContain('PACKED');
    expect(text).toContain('IN_TRANSIT');
    expect(text).toContain('DELIVERED');
    expect(text).not.toContain('COMPLETED');
    expect(text.indexOf('PACKED')).toBeLessThan(text.indexOf('GOODS_ISSUED'));
  });

  it('creates the route with the code upper-cased and steps numbered 1..n', async () => {
    await setup(null);
    component.draft.code = ' high_value ';
    component.draft.name = ' High value ';
    component.addStep('SHIP');
    component.addStep('APPROVAL');
    component.save();
    expect(service.createRoute).toHaveBeenCalledWith({
      code: 'HIGH_VALUE', name: 'High value', description: null, displayOrder: null, routeCategory: 'STOCK', // A34
      steps: [
        { stepCode: 'PICK', stepOrder: 1, isMandatory: true, description: null },
        { stepCode: 'APPROVAL', stepOrder: 2, isMandatory: true, description: null },
        { stepCode: 'GOODS_ISSUE', stepOrder: 3, isMandatory: true, description: null },
        { stepCode: 'SHIP', stepOrder: 4, isMandatory: true, description: null }
      ]
    });
    expect(service.setDefault).not.toHaveBeenCalled();
    expect(saved.length).toBe(1);
    expect(saved[0].created).toBeTrue();
    expect(saved[0].route.code).toBe('HIGH_VALUE');
  });

  it('"Set as default" on a new route calls set-default after creating it', async () => {
    await setup(null);
    component.draft.code = 'COLLECT';
    component.draft.name = 'Collect';
    component.draft.makeDefault = true;
    component.save();
    expect(service.createRoute).toHaveBeenCalled();
    expect(service.setDefault).toHaveBeenCalledWith('r-new');
    expect(saved[0].route.isDefault).toBeTrue();
  });

  it('names the class the default checkbox applies to and the default it would replace (L-1)', async () => {
    await setup(null);
    expect(q('make-default-label')!.textContent).toContain('self-pickup orders');
    component.draft.makeDefault = true;
    await refresh();
    expect(q('default-hint')!.textContent).toContain('Pick Only');
    component.addStep('SHIP');
    await refresh();
    expect(q('make-default-label')!.textContent).toContain('shipping orders');
    expect(q('default-hint')!.textContent).toContain('Pick & Ship');
  });

  it('shows the problem and does not save an invalid route (client mirror of BR-C1)', async () => {
    await setup(null);
    component.draft.code = 'OK';
    component.draft.name = '';
    component.save();
    await refresh();
    expect(service.createRoute).not.toHaveBeenCalled();
    expect(q('problem')!.textContent).toContain('Name');
  });

  it('shows the server\'s message when the save is refused, and does not report it saved', async () => {
    await setup(null);
    service.createRoute.and.returnValue(throwError(() => ({ status: 409, error: { message: "Fulfillment route code 'X' already exists in this organization." } })));
    component.draft.code = 'X';
    component.draft.name = 'X';
    component.save();
    await refresh();
    expect(q('save-error')!.textContent).toContain('already exists');
    expect(saved.length).toBe(0);
    expect(component.isSaving).toBeFalse();
  });

  // ── Editing ────────────────────────────────────────────────────────────────────────────────────────────

  it('locks a system route\'s code and steps; only name, description and order are saved (D-10)', async () => {
    const pps = route(['PICK', 'PACK', 'GOODS_ISSUE', 'SHIP'], { uuid: 'r-pps', code: 'PICK_PACK_SHIP', name: 'Pick, Pack & Ship', isSystem: true, displayOrder: 30 });
    await setup(pps);
    expect((q('code-input') as HTMLInputElement).disabled).toBeTrue();
    expect(q('steps-locked')).not.toBeNull();
    expect(el.querySelector('[data-testid^="add-step-"]')).toBeNull();
    expect(el.querySelector('[data-testid^="remove-step-"]')).toBeNull();
    expect((q('mandatory-PACK')!.querySelector('input') as HTMLInputElement).disabled).toBeTrue();
    expect((q('step-desc-PACK') as HTMLInputElement).disabled).toBeTrue();

    component.draft.name = 'Full cycle';
    component.draft.description = 'Boxes, then carrier';
    component.draft.displayOrder = 35;
    component.save();
    expect(service.updateRoute).toHaveBeenCalledWith('r-pps', { name: 'Full cycle', description: 'Boxes, then carrier', displayOrder: 35, steps: null, routeCategory: null });
    expect(saved[0].created).toBeFalse();
  });

  it('keeps the code read-only on a custom route too (L-4) and sends the steps only when they changed', async () => {
    const custom = route(['PICK', 'PACK', 'GOODS_ISSUE', 'SHIP']);
    await setup(custom);
    expect((q('code-input') as HTMLInputElement).disabled).toBeTrue();
    component.save();
    expect(service.updateRoute.calls.mostRecent().args[1].steps).toBeNull();

    component.removeStep('PACK');
    component.draft.steps.find(s => s.stepCode === 'SHIP')!.isMandatory = false;
    component.save();
    expect(service.updateRoute.calls.mostRecent().args[1].steps).toEqual([
      { stepCode: 'PICK', stepOrder: 1, isMandatory: true, description: null },
      { stepCode: 'GOODS_ISSUE', stepOrder: 2, isMandatory: true, description: null },
      { stepCode: 'SHIP', stepOrder: 3, isMandatory: false, description: null }
    ]);
  });

  it('clears the default when the box is unticked on a default route', async () => {
    const custom = route(['PICK', 'GOODS_ISSUE', 'SHIP'], { isDefault: true });
    await setup(custom);
    expect(component.draft.makeDefault).toBeTrue();
    component.draft.makeDefault = false;
    await refresh();
    expect(q('default-hint')!.textContent).toContain('block');
    component.save();
    expect(service.updateRoute).toHaveBeenCalled();
    expect(service.clearDefault).toHaveBeenCalledWith('r-1');
    expect(service.setDefault).not.toHaveBeenCalled();
  });

  it('refuses to move a default route to the other class by removing SHIP (L-5)', async () => {
    const custom = route(['PICK', 'GOODS_ISSUE', 'SHIP'], { isDefault: true });
    await setup(custom);
    component.removeStep('SHIP');
    component.save();
    await refresh();
    expect(service.updateRoute).not.toHaveBeenCalled();
    expect(q('problem')!.textContent).toContain('make another route the default first');
  });

  it('cannot make an inactive route the default', async () => {
    await setup(route(['PICK', 'GOODS_ISSUE'], { isActive: false }));
    expect((q('make-default')!.querySelector('input') as HTMLInputElement).disabled).toBeTrue();
  });

  it('reports a route saved even when the default change is refused, with the reason as a warning', async () => {
    await setup(route(['PICK', 'GOODS_ISSUE']));
    service.setDefault.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Inactive route.' } })));
    component.draft.makeDefault = true;
    component.save();
    expect(saved.length).toBe(1);
    expect(saved[0].warning).toContain('Inactive route.');
  });

  // ── A34-PA-09 — route category ─────────────────────────────────────────────────────────────────────────

  it('a new route starts as STOCK; the dropdown offers STOCK and MANUFACTURE, BUY and DROPSHIP disabled as reserved', async () => {
    await setup(null);
    expect(component.draft.routeCategory).toBe('STOCK');
    expect(q('category-select')).not.toBeNull();
    expect(component.categoryOptions.map(o => `${o.value}:${o.disabled}`)).toEqual(
      ['STOCK:false', 'MANUFACTURE:false', 'BUY:true', 'DROPSHIP:true']);
    expect(component.categoryOptions.find(o => o.value === 'BUY')!.tooltip).toBe('Reserved for future release');
    expect(q('manufacture-note')).toBeNull();
    component.draft.code = 'HV';
    component.draft.name = 'High value';
    component.save();
    expect(service.createRoute.calls.mostRecent().args[0].routeCategory).toBe('STOCK');
  });

  it('MANUFACTURE shows the §3.6 note, hides "make default" (D-6) and is created without set-default', async () => {
    await setup(null);
    component.draft.makeDefault = true;
    component.onCategoryChange('MANUFACTURE');
    await refresh();
    expect(q('manufacture-note')!.textContent).toContain('trigger a Production Order at Sale Order confirmation');
    expect(q('manufacture-note')!.textContent).toContain('Delivery is created after production completes');
    expect(q('make-default')).toBeNull();
    expect(component.draft.makeDefault).toBeFalse();
    component.draft.code = 'MTO';
    component.draft.name = 'Make to order';
    component.save();
    expect(service.createRoute.calls.mostRecent().args[0].routeCategory).toBe('MANUFACTURE');
    expect(service.setDefault).not.toHaveBeenCalled();
    expect(saved.length).toBe(1);
  });

  it('does not offer MANUFACTURE without MODULE_MANUFACTURING (D-9)', async () => {
    await setup(null, { manufacturingEnabled: false });
    expect(component.categoryOptions.map(o => o.value)).toEqual(['STOCK', 'BUY', 'DROPSHIP']);
  });

  it('a system route shows its category read-only and never sends one', async () => {
    await setup(route(['PICK', 'GOODS_ISSUE', 'SHIP'], { code: 'MFG_PICK_SHIP', isSystem: true, routeCategory: 'MANUFACTURE' }));
    expect(q('category-select')).toBeNull();
    expect(q('category-readonly')!.textContent).toContain('Manufacture');
    expect(q('manufacture-note')).not.toBeNull();
    component.onCategoryChange('STOCK');
    expect(component.draft.routeCategory).toBe('MANUFACTURE');
    component.save();
    expect(service.updateRoute.calls.mostRecent().args[1].routeCategory).toBeNull();
  });

  it('a default route\'s category is disabled, with the reason (D-8)', async () => {
    await setup(route(['PICK', 'GOODS_ISSUE', 'SHIP'], { isDefault: true }));
    expect(component.categoryLock.disabled).toBeTrue();
    expect(q('category-locked')!.textContent).toContain('default route for shipping orders');
    component.onCategoryChange('MANUFACTURE');
    expect(component.draft.routeCategory).toBe('STOCK');
  });

  it('a custom route sends its category only when changed; an in-use refusal (409) shows the server\'s words', async () => {
    await setup(route(['PICK', 'GOODS_ISSUE', 'SHIP']));
    component.save();
    expect(service.updateRoute.calls.mostRecent().args[1].routeCategory).toBeNull();

    service.updateRoute.and.returnValue(throwError(() => ({ status: 409, error: {
      message: "'CUSTOM' is still used by 3 active product variants: its category can't be changed. Reassign them first." } })));
    component.onCategoryChange('MANUFACTURE');
    component.save();
    expect(service.updateRoute.calls.mostRecent().args[1].routeCategory).toBe('MANUFACTURE');
    await refresh();
    expect(q('save-error')!.textContent).toContain('still used by 3 active product variants');
  });

  it('read-only (no FULFILLMENT_ROUTE_MANAGE): every field disabled and no save button', async () => {
    await setup(route(['PICK', 'GOODS_ISSUE', 'SHIP']), { readOnly: true });
    expect(q('save')).toBeNull();
    expect((q('name-input') as HTMLInputElement).disabled).toBeTrue();
    expect(el.querySelector('[data-testid^="add-step-"]')).toBeNull();
    component.save();
    expect(service.updateRoute).not.toHaveBeenCalled();
  });
});

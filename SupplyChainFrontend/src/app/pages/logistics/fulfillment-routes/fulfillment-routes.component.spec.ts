import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ConfirmationService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { FulfillmentRoutesComponent } from './fulfillment-routes.component';
import { FulfillmentRouteModel, FulfillmentRoutesService } from '../../../services/fulfillment-routes.service';
import { InventoryService } from '../../../services/inventory.service';
import { AuthService } from '../../service/auth.service';

const LABELS: Record<string, string> = { PICK: 'Pick', PACK: 'Pack', STAGE: 'Stage', APPROVAL: 'Approval', GOODS_ISSUE: 'Goods Issue', SHIP: 'Ship' };

function route(code: string, codes: string[], over: Partial<FulfillmentRouteModel> = {}): FulfillmentRouteModel {
  return {
    uuid: 'r-' + code, code, name: code, description: null, isDefault: false, isActive: true, isSystem: false,
    requiresPacking: codes.includes('PACK'), requiresShipping: codes.includes('SHIP'), displayOrder: 10,
    steps: codes.map((c, i) => ({ stepCode: c as any, label: LABELS[c], stepOrder: i + 1, isMandatory: true })),
    stepsText: codes.map(c => LABELS[c]).join(' → '), statusPath: [], createdDate: '', ...over
  };
}

const ROUTES = () => [
  route('PICK_ONLY', ['PICK', 'GOODS_ISSUE'], { name: 'Pick Only', isSystem: true, isDefault: true }),
  route('PICK_AND_SHIP', ['PICK', 'GOODS_ISSUE', 'SHIP'], { name: 'Pick & Ship', isSystem: true, isDefault: true }),
  route('PICK_PACK_SHIP', ['PICK', 'PACK', 'GOODS_ISSUE', 'SHIP'], { name: 'Pick, Pack & Ship', isSystem: true }),
  route('HIGH_VALUE', ['PICK', 'PACK', 'STAGE', 'APPROVAL', 'GOODS_ISSUE', 'SHIP'], { name: 'High value' }),
  route('OLD', ['PICK', 'GOODS_ISSUE'], { name: 'Old', isActive: false })
];

describe('FulfillmentRoutesComponent (A33-PA-07 route list)', () => {
  let fixture: ComponentFixture<FulfillmentRoutesComponent>;
  let component: FulfillmentRoutesComponent;
  let service: jasmine.SpyObj<FulfillmentRoutesService>;
  let permissions: string[];
  let el: HTMLElement;
  let confirmation: ConfirmationService;

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;
  const ok = (result: unknown) => of({ success: true, message: '', result } as any);

  async function setup(routes = ROUTES()) {
    service = jasmine.createSpyObj<FulfillmentRoutesService>('FulfillmentRoutesService',
      ['getRoutes', 'setDefault', 'clearDefault', 'deactivateRoute', 'activateRoute', 'deleteRoute', 'createRoute',
       'updateRoute', 'assignByCategory']);
    service.getRoutes.and.returnValue(ok(routes));
    service.setDefault.and.callFake(uuid => ok(routes.find(r => r.uuid === uuid)));
    service.clearDefault.and.callFake(uuid => ok(routes.find(r => r.uuid === uuid)));
    service.deactivateRoute.and.callFake(uuid => ok(routes.find(r => r.uuid === uuid)));
    service.activateRoute.and.callFake(uuid => ok(routes.find(r => r.uuid === uuid)));
    service.deleteRoute.and.returnValue(ok(null));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getCategories']);
    inventory.getCategories.and.returnValue(ok([]));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [FulfillmentRoutesComponent],
      providers: [
        provideNoopAnimations(),
        { provide: FulfillmentRoutesService, useValue: service },
        { provide: InventoryService, useValue: inventory },
        { provide: AuthService, useValue: auth }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(FulfillmentRoutesComponent);
    component = fixture.componentInstance;
    confirmation = fixture.debugElement.injector.get(ConfirmationService);
    spyOn(confirmation, 'confirm').and.callFake((c: any) => { c.accept?.(); return confirmation; });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const click = (id: string) => ((q(id)!.querySelector('button') ?? q(id)!) as HTMLElement).click();
  const refresh = async () => { fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges(); };

  beforeEach(() => { permissions = ['FULFILLMENT_ROUTE_VIEW', 'FULFILLMENT_ROUTE_MANAGE', 'FULFILLMENT_ROUTE_ASSIGN']; });

  // ── Columns ────────────────────────────────────────────────────────────────────────────────────────────

  it('lists every route, inactive included, with code, name and the abbreviated step chain', async () => {
    await setup();
    expect(service.getRoutes).toHaveBeenCalledWith(true);
    const row = q('route-PICK_AND_SHIP')!.textContent!;
    expect(row).toContain('PICK_AND_SHIP');
    expect(row).toContain('Pick & Ship');
    expect(row).toContain('Pick → GI → Ship');
    expect(q('route-HIGH_VALUE')!.textContent).toContain('Pick → Pack → Stage → Appr → GI → Ship');
    expect(q('route-OLD')!.textContent).toContain('Deactivated');
  });

  it('marks the default of each class — shipping and self-pickup (L-1)', async () => {
    await setup();
    expect(q('default-PICK_AND_SHIP')!.textContent).toContain('Shipping');
    expect(q('default-PICK_ONLY')!.textContent).toContain('Self-pickup');
    expect(q('default-PICK_PACK_SHIP')).toBeNull();
  });

  it('shows the lock on system routes only', async () => {
    await setup();
    expect(q('system-PICK_ONLY')).not.toBeNull();
    expect(q('system-HIGH_VALUE')).toBeNull();
  });

  it('can hide deactivated routes', async () => {
    await setup();
    component.showInactive = false;
    await refresh();
    expect(q('route-OLD')).toBeNull();
    expect(q('route-HIGH_VALUE')).not.toBeNull();
  });

  it('says so when the routes cannot be loaded', async () => {
    await setup();
    service.getRoutes.and.returnValue(throwError(() => ({ status: 500 })));
    component.load();
    await refresh();
    expect(q('load-failed')).not.toBeNull();
  });

  // ── Gating: VIEW lists; MANAGE writes; ASSIGN bulk-assigns ───────────────────────────────────────────────

  it('VIEW only: no New Route, no row write actions, rows open read-only', async () => {
    permissions = ['FULFILLMENT_ROUTE_VIEW'];
    await setup();
    expect(q('new-route')).toBeNull();
    expect(el.querySelector('[data-testid^="set-default-"]')).toBeNull();
    expect(el.querySelector('[data-testid^="deactivate-"]')).toBeNull();
    expect(el.querySelector('[data-testid^="delete-"]')).toBeNull();
    expect(el.querySelector('[data-testid^="assign-"]')).toBeNull();
    click('view-HIGH_VALUE');
    await refresh();
    expect(component.editorVisible).toBeTrue();
    expect(component.editorReadOnly).toBeTrue();
  });

  it('MANAGE: New Route opens an empty editor', async () => {
    await setup();
    click('new-route');
    await refresh();
    expect(component.editorVisible).toBeTrue();
    expect(component.editing).toBeNull();
    expect(component.editorReadOnly).toBeFalse();
  });

  it('ASSIGN without MANAGE: only "Assign to category", on active routes', async () => {
    permissions = ['FULFILLMENT_ROUTE_VIEW', 'FULFILLMENT_ROUTE_ASSIGN'];
    await setup();
    expect(q('new-route')).toBeNull();
    expect(q('assign-HIGH_VALUE')).not.toBeNull();
    expect(q('assign-OLD')).toBeNull();
    click('assign-HIGH_VALUE');
    await refresh();
    expect(component.assignVisible).toBeTrue();
    expect(component.assigning?.code).toBe('HIGH_VALUE');
  });

  // ── Default ────────────────────────────────────────────────────────────────────────────────────────────

  it('sets a route as the default of its class, naming the default it replaces, then reloads', async () => {
    await setup();
    expect(q('set-default-PICK_AND_SHIP')).toBeNull();
    expect(q('set-default-OLD')).toBeNull();
    click('set-default-PICK_PACK_SHIP');
    const asked = (confirmation.confirm as jasmine.Spy).calls.mostRecent().args[0];
    expect(asked.message).toContain('shipping orders');
    expect(asked.message).toContain('Pick & Ship');
    expect(service.setDefault).toHaveBeenCalledWith('r-PICK_PACK_SHIP');
    expect(service.getRoutes).toHaveBeenCalledTimes(2);
  });

  it('clears a default after warning that lines with no route will block confirmation (D-6)', async () => {
    await setup();
    click('clear-default-PICK_ONLY');
    const asked = (confirmation.confirm as jasmine.Spy).calls.mostRecent().args[0];
    expect(asked.message).toContain('self-pickup orders');
    expect(asked.message).toContain('block');
    expect(service.clearDefault).toHaveBeenCalledWith('r-PICK_ONLY');
  });

  // ── Deactivate / activate / delete ─────────────────────────────────────────────────────────────────────

  it('deactivates a route; a default route can\'t be (L-5) — the button is disabled', async () => {
    await setup();
    expect((q('deactivate-PICK_AND_SHIP')!.querySelector('button') as HTMLButtonElement).disabled).toBeTrue();
    click('deactivate-HIGH_VALUE');
    expect(service.deactivateRoute).toHaveBeenCalledWith('r-HIGH_VALUE');
  });

  it('shows the server\'s refusal when the route is still in use (BR-C1-07)', async () => {
    await setup();
    service.deactivateRoute.and.returnValue(throwError(() => ({ status: 409, error: { message: "'HIGH_VALUE' is still used by 3 active product variants." } })));
    spyOn(component['messages'], 'add');
    click('deactivate-HIGH_VALUE');
    expect(component['messages'].add).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: jasmine.stringContaining('still used by 3') }));
  });

  it('reactivates a deactivated route', async () => {
    await setup();
    click('activate-OLD');
    expect(service.activateRoute).toHaveBeenCalledWith('r-OLD');
  });

  it('offers delete only on custom, non-default routes (BR-C1-06)', async () => {
    await setup();
    expect(q('delete-PICK_ONLY')).toBeNull();
    expect(q('delete-PICK_PACK_SHIP')).toBeNull();
    click('delete-HIGH_VALUE');
    expect(service.deleteRoute).toHaveBeenCalledWith('r-HIGH_VALUE');
  });

  // ── Editor round trip ──────────────────────────────────────────────────────────────────────────────────

  it('closes the editor and reloads once a route is saved; a default warning is shown', async () => {
    await setup();
    component.openEdit(ROUTES()[3]);
    spyOn(component['messages'], 'add');
    component.onSaved({ route: ROUTES()[3], created: false, warning: 'not changed' });
    expect(component.editorVisible).toBeFalse();
    expect(service.getRoutes).toHaveBeenCalledTimes(2);
    expect(component['messages'].add).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'warn' }));
  });
});

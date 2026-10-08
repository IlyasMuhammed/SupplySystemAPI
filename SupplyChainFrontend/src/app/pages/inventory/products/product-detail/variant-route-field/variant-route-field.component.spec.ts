import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { VariantRouteFieldComponent } from './variant-route-field.component';
import { FulfillmentRouteModel, FulfillmentRoutesService } from '../../../../../services/fulfillment-routes.service';

function route(uuid: string, code: string, name: string, stepsText: string): FulfillmentRouteModel {
  return {
    uuid, code, name, description: null, isDefault: false, isActive: true, isSystem: true, requiresPacking: false,
    requiresShipping: stepsText.includes('Ship'), displayOrder: 10, steps: [], stepsText, statusPath: [], createdDate: ''
  };
}

const ACTIVE = [
  route('r-po', 'PICK_ONLY', 'Pick Only', 'Pick → Goods Issue'),
  route('r-ps', 'PICK_AND_SHIP', 'Pick & Ship', 'Pick → Goods Issue → Ship')
];

describe('VariantRouteFieldComponent (A33-PB-04 variant route)', () => {
  let fixture: ComponentFixture<VariantRouteFieldComponent>;
  let component: VariantRouteFieldComponent;
  let service: jasmine.SpyObj<FulfillmentRoutesService>;
  let el: HTMLElement;
  let emitted: (string | null)[];

  async function setup(inputs: { value?: string | null; canAssign?: boolean; currentCode?: string | null; currentName?: string | null;
                                 productManufactured?: boolean; manufacturingEnabled?: boolean },
                       routes$ = of({ success: true, message: '', result: ACTIVE } as any)) {
    service = jasmine.createSpyObj<FulfillmentRoutesService>('FulfillmentRoutesService', ['getRoutes']);
    service.getRoutes.and.returnValue(routes$);
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [VariantRouteFieldComponent],
      providers: [provideNoopAnimations(), { provide: FulfillmentRoutesService, useValue: service }]
    }).compileComponents();
    fixture = TestBed.createComponent(VariantRouteFieldComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('value', inputs.value ?? null);
    fixture.componentRef.setInput('canAssign', inputs.canAssign ?? true);
    fixture.componentRef.setInput('currentCode', inputs.currentCode ?? null);
    fixture.componentRef.setInput('currentName', inputs.currentName ?? null);
    if (inputs.productManufactured !== undefined) fixture.componentRef.setInput('productManufactured', inputs.productManufactured);
    if (inputs.manufacturingEnabled !== undefined) fixture.componentRef.setInput('manufacturingEnabled', inputs.manufacturingEnabled);
    emitted = [];
    component.valueChange.subscribe(v => emitted.push(v));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const refresh = async () => { fixture.detectChanges(); await fixture.whenStable(); fixture.detectChanges(); };

  it('offers the active routes as "Name (CODE)"', async () => {
    await setup({});
    expect(service.getRoutes).toHaveBeenCalledWith();
    expect(component.options.map(o => o.label)).toEqual(['Pick Only (PICK_ONLY)', 'Pick & Ship (PICK_AND_SHIP)']);
  });

  it('shows the chosen route\'s step chain under the dropdown, and follows a change', async () => {
    await setup({ value: 'r-ps' });
    expect(q('route-steps')!.textContent).toContain('Pick → Goods Issue → Ship');
    component.onChange('r-po');
    await refresh();
    expect(emitted).toEqual(['r-po']);
    expect(q('route-steps')!.textContent).toContain('Pick → Goods Issue');
    expect(q('route-steps')!.textContent).not.toContain('Ship');
  });

  it('has the info tooltip saying the route can be overridden per sale order line', async () => {
    await setup({});
    expect(component.infoText).toContain('warehouse operations');
    expect(component.infoText).toContain('sale order line');
    expect(q('route-info')).not.toBeNull();
  });

  it('can be cleared; with no route it says the organization default applies', async () => {
    await setup({ value: 'r-ps' });
    expect(component.clearable).toBeTrue();
    component.onChange(null);
    await refresh();
    expect(emitted).toEqual([null]);
    expect(q('route-none')!.textContent).toContain('default route');
  });

  it('is read-only without FULFILLMENT_ROUTE_ASSIGN: disabled, not clearable, and says why', async () => {
    await setup({ value: 'r-ps', canAssign: false });
    expect(component.clearable).toBeFalse();
    expect(el.querySelector('.p-select.p-disabled, .p-disabled')).not.toBeNull();
    expect(q('route-readonly')).not.toBeNull();
    component.onChange('r-po');
    expect(emitted).toEqual([]);
  });

  it('still shows a route the variant has that is no longer active', async () => {
    await setup({ value: 'r-old', currentCode: 'OLD', currentName: 'Old route' });
    expect(component.options.map(o => o.label)).toContain('Old route (OLD) — inactive');
    expect(q('route-inactive')).not.toBeNull();
  });

  // ── A34-PA-06/PA-10 — route category on the variant ────────────────────────────────────────────────────

  const MFG = { ...route('r-mfg', 'MFG_PICK_SHIP', 'Manufacture → Pick & Ship', 'Pick → Goods Issue → Ship'), routeCategory: 'MANUFACTURE' as const };
  const WITH_MFG = () => of({ success: true, message: '', result: [...ACTIVE, MFG] } as any);

  it('offers MANUFACTURE routes only for a manufactured product (D-3)', async () => {
    await setup({ productManufactured: false, manufacturingEnabled: true }, WITH_MFG());
    expect(component.options.map(o => o.value)).toEqual(['r-po', 'r-ps']);
    expect(q('route-mfg-hint')!.textContent).toContain('supply method');
    await setup({ productManufactured: true, manufacturingEnabled: true }, WITH_MFG());
    expect(component.options.map(o => o.value)).toEqual(['r-po', 'r-ps', 'r-mfg']);
    expect(q('route-mfg-hint')).toBeNull();
  });

  it('never offers MANUFACTURE routes without MODULE_MANUFACTURING (D-9)', async () => {
    await setup({ productManufactured: true, manufacturingEnabled: false }, WITH_MFG());
    expect(component.options.map(o => o.value)).toEqual(['r-po', 'r-ps']);
    expect(q('route-mfg-hint')).toBeNull();
  });

  it('shows the chosen route\'s category as a badge: Stock green, Manufacture orange', async () => {
    await setup({ value: 'r-mfg', productManufactured: true, manufacturingEnabled: true }, WITH_MFG());
    expect(q('route-category')!.textContent).toContain('Manufacture');
    expect(q('route-category')!.querySelector('.p-tag-warn')).not.toBeNull();
    expect(q('route-make-to-order')!.textContent).toContain('production order');
    component.onChange('r-ps');
    await refresh();
    expect(q('route-category')!.textContent).toContain('Stock');
    expect(q('route-category')!.querySelector('.p-tag-success')).not.toBeNull();
    expect(q('route-make-to-order')).toBeNull();
  });

  it('keeps showing a MANUFACTURE route the variant has once the product is no longer manufactured, with a warning', async () => {
    await setup({ value: 'r-mfg', productManufactured: false, manufacturingEnabled: true }, WITH_MFG());
    expect(component.options.map(o => o.value)).toContain('r-mfg');
    expect(q('route-not-allowed')!.textContent).toContain('block');
  });

  it('when the routes cannot be loaded, shows the variant\'s route as text', async () => {
    await setup({ value: 'r-ps', currentCode: 'PICK_AND_SHIP', currentName: 'Pick & Ship' }, throwError(() => ({ status: 403 })) as any);
    expect(q('routes-failed')!.textContent).toContain('Pick & Ship');
  });
});

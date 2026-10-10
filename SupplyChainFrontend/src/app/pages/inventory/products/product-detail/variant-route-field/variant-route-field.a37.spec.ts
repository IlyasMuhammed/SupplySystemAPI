import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { VariantRouteFieldComponent } from './variant-route-field.component';
import {
  FulfillmentRouteModel, FulfillmentRoutesService, routesVisibleToOrg, unavailableRouteOptions
} from '../../../../../services/fulfillment-routes.service';
import { ProductRoutesPanelComponent } from '../product-routes-panel/product-routes-panel.component';

function route(uuid: string, code: string, name: string, over: Partial<FulfillmentRouteModel> = {}): FulfillmentRouteModel {
  return {
    uuid, code, name, description: null, isDefault: false, isActive: true, isSystem: false, requiresPacking: false,
    requiresShipping: false, displayOrder: 10, steps: [], stepsText: 'Pick → Goods Issue', statusPath: [], createdDate: '', ...over
  };
}

const STOCK = route('r-s', 'PICK_ONLY', 'Pick Only');
const MFG_OFF = route('r-m', 'MFG', 'Make & Ship', { routeCategory: 'MANUFACTURE', isAvailable: false, unavailableReason: 'Manufacturing is switched off' });

/** A37 §4 (RTE-01/02/03) — unavailable routes shown disabled with the reason; the product Routes panel. */
describe('A37 route availability', () => {
  describe('service helpers', () => {
    it('routesVisibleToOrg drops unavailable routes; unavailableRouteOptions lists them disabled with the reason', () => {
      expect(routesVisibleToOrg([STOCK, MFG_OFF], true).map(r => r.uuid)).toEqual(['r-s']);
      expect(unavailableRouteOptions([STOCK, MFG_OFF])).toEqual([
        { label: 'Make & Ship — Manufacturing is switched off', value: 'r-m', disabled: true, reason: 'Manufacturing is switched off' }
      ]);
      expect(unavailableRouteOptions([STOCK, MFG_OFF], ['r-m'])).toEqual([]);
      // an older server (no isAvailable) is "available"
      expect(routesVisibleToOrg([route('x', 'X', 'X')], false).length).toBe(1);
    });
  });

  describe('VariantRouteFieldComponent', () => {
    let fixture: ComponentFixture<VariantRouteFieldComponent>;
    let component: VariantRouteFieldComponent;
    let el: HTMLElement;

    async function setup(value: string | null, productManufactured = true) {
      const service = jasmine.createSpyObj<FulfillmentRoutesService>('FulfillmentRoutesService', ['getRoutes']);
      service.getRoutes.and.returnValue(of({ success: true, message: '', result: [STOCK, MFG_OFF] } as any));
      await TestBed.resetTestingModule().configureTestingModule({
        imports: [VariantRouteFieldComponent],
        providers: [provideNoopAnimations(), { provide: FulfillmentRoutesService, useValue: service }]
      }).compileComponents();
      fixture = TestBed.createComponent(VariantRouteFieldComponent);
      component = fixture.componentInstance;
      fixture.componentRef.setInput('value', value);
      fixture.componentRef.setInput('canAssign', true);
      fixture.componentRef.setInput('productManufactured', productManufactured);
      fixture.componentRef.setInput('manufacturingEnabled', false);
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
      el = fixture.nativeElement;
    }

    it('lists an unavailable route disabled, with the reason in its label', async () => {
      await setup(null);
      const off = component.options.find(o => o.value === 'r-m')!;
      expect(off.disabled).toBeTrue();
      expect(off.label).toBe('Make & Ship (MFG) — Manufacturing is switched off');
      expect(component.options.find(o => o.value === 'r-s')!.disabled).toBeFalsy();
    });

    it('a variant that has the unavailable route keeps it, with a warning that the default applies', async () => {
      await setup('r-m');
      expect(component.selected!.unavailableReason).toBe('Manufacturing is switched off');
      expect(component.selected!.disabled).toBeFalsy();
      expect(el.querySelector('[data-testid="route-unavailable"]')!.textContent).toContain('Manufacturing is switched off');
      expect(el.querySelector('[data-testid="route-make-to-order"]')).toBeNull();
    });
  });

  describe('ProductRoutesPanelComponent', () => {
    let el: HTMLElement;
    let service: jasmine.SpyObj<FulfillmentRoutesService>;

    let forbidden = 0;

    async function setup(result: unknown, fail = false, status = 500) {
      service = jasmine.createSpyObj<FulfillmentRoutesService>('FulfillmentRoutesService', ['getProductRoutes']);
      service.getProductRoutes.and.returnValue(fail ? throwError(() => ({ status, error: { message: 'No access' } })) : of({ success: true, message: '', result } as any));
      await TestBed.resetTestingModule().configureTestingModule({
        imports: [ProductRoutesPanelComponent],
        providers: [provideNoopAnimations(), { provide: FulfillmentRoutesService, useValue: service }]
      }).compileComponents();
      const fixture = TestBed.createComponent(ProductRoutesPanelComponent);
      forbidden = 0;
      fixture.componentInstance.forbidden.subscribe(() => forbidden++);
      fixture.componentRef.setInput('productUuid', 'p-7');
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
      el = fixture.nativeElement;
    }

    const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

    it('shows configured, effective, availability and the warning per variant', async () => {
      await setup([
        { variantUuid: 'v-1', variantName: 'Red', sku: 'P-R', routeUuid: 'r-m', routeName: 'Make & Ship', category: 'MANUFACTURE',
          isAvailable: false, effectiveRouteUuid: 'r-s', effectiveRouteName: 'Pick Only',
          warning: 'Manufacturing is switched off — the default stock route applies.' },
        { variantUuid: 'v-2', variantName: 'Blue', sku: 'P-B', routeUuid: null, isAvailable: true, effectiveRouteName: 'Pick Only' }
      ]);
      expect(service.getProductRoutes).toHaveBeenCalledWith('p-7');
      expect(q('configured-v-1')!.textContent).toContain('Make & Ship');
      expect(q('unavailable-v-1')).not.toBeNull();
      expect(q('effective-v-1')!.textContent).toContain('Pick Only');
      expect(q('warning-v-1')!.textContent).toContain('default stock route applies');
      expect(q('configured-v-2')!.textContent).toContain('organization default');
      expect(q('unavailable-v-2')).toBeNull();
      expect(q('warning-v-2')!.textContent!.trim()).toBe('');
    });

    it('a 403 asks the host to hide the panel instead of showing an error', async () => {
      await setup(null, true, 403);
      expect(forbidden).toBe(1);
      expect(q('product-routes-failed')).toBeNull();
    });

    it('says so when the routes cannot be loaded', async () => {
      await setup(null, true);
      expect(q('product-routes-failed')!.textContent).toContain('No access');
    });
  });
});

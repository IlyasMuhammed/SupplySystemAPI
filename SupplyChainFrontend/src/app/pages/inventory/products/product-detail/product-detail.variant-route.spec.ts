import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { of, throwError } from 'rxjs';

import { ProductDetailComponent } from './product-detail.component';
import { InventoryService, ProductDetailModel, ProductVariantModel } from '../../../../services/inventory.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { PricingRuleService } from '../../../../services/pricing-rule.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { SupplierService } from '../../../../services/supplier.service';
import { QboSyncStatusStore } from '../../../../shared/components/qbo-sync-badge/qbo-sync-status.store';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';
import { BomService } from '../../../../services/bom.service';

/**
 * A33-PB-04 — the variant dialog's fulfillment route. The route is saved through its own gated call
 * (PUT api/variants/{uuid}/fulfillment-route, FULFILLMENT_ROUTE_ASSIGN) after the variant itself, only when it changed;
 * PATCH api/variants/{uuid} ignores route fields (contract §4). No call at all without the permission or without
 * MODULE_LOGISTICS (D-11). The template is left out: this spec covers the dialog's logic; the field has its own spec.
 */
describe('ProductDetailComponent — variant fulfillment route (A33-PB-04)', () => {
  let component: ProductDetailComponent;
  let inventory: jasmine.SpyObj<InventoryService>;
  let permissions: string[];
  let features: string[];
  let toasts: any[];
  let productOver: Partial<ProductDetailModel>;

  const variant = (over: Partial<ProductVariantModel> = {}): ProductVariantModel => ({
    id: 1, uuid: 'v-1', sku: 'SKU-1', variantName: 'Rod 10mm', purchasePrice: 10, isDefault: true, isActive: true,
    isAvailableForRetail: true, isAvailableForPos: false, isAvailableForMirMiv: false, isAvailableForProduction: false,
    isAvailableForServices: false, createdDate: '', weightKg: 12.5, dimensions: '6 m',
    fulfillmentRouteUuid: 'r-ps', fulfillmentRouteCode: 'PICK_AND_SHIP', fulfillmentRouteName: 'Pick & Ship', ...over
  });

  const ok = (result: unknown) => of({ success: true, message: '', result } as any);

  function setup() {
    inventory = jasmine.createSpyObj<InventoryService>('InventoryService',
      ['getProductById', 'getProductStock', 'updateVariant', 'addVariant', 'setVariantAttributeValues', 'setVariantFulfillmentRoute']);
    inventory.getProductById.and.returnValue(ok({ id: 7, uuid: 'p-7', name: 'Rod', variants: [variant()], ...productOver } as unknown as ProductDetailModel));
    inventory.getProductStock.and.returnValue(ok([]));
    inventory.updateVariant.and.returnValue(ok(null));
    inventory.addVariant.and.returnValue(ok(variant({ uuid: 'v-new', fulfillmentRouteUuid: null })));
    inventory.setVariantFulfillmentRoute.and.callFake((uuid, routeUuid) => ok({ variantUuid: uuid, fulfillmentRouteUuid: routeUuid }));

    TestBed.resetTestingModule();
    TestBed.overrideComponent(ProductDetailComponent, { set: { template: '', imports: [] } });
    TestBed.configureTestingModule({
      imports: [ProductDetailComponent],
      providers: [
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => '7' } } } },
        { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
        { provide: InventoryService, useValue: inventory },
        { provide: AttachmentService, useValue: {} },
        { provide: PricingRuleService, useValue: { getRules: () => ok({ items: [] }) } },
        { provide: CurrenciesService, useValue: {} },
        { provide: BusinessPartnerService, useValue: {} },
        { provide: SupplierService, useValue: {} },
        // A37 — without FEATURE_BOM_MANAGEMENT the page asks whether the product has a BOM (read-only tab).
        { provide: BomService, useValue: { getBoms: () => ok({ data: [], totalRecords: 0 }) } },
        { provide: QboSyncStatusStore, useValue: { isAvailable: () => false } },
        { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
        { provide: TenantService, useValue: { hasFeature: (c: string) => features.includes(c) } }
      ]
    });
    const fixture = TestBed.createComponent(ProductDetailComponent);
    component = fixture.componentInstance;
    component.ngOnInit();
    toasts = [];
    spyOn(component['messageService'], 'add').and.callFake((m: any) => { toasts.push(m); });
  }

  beforeEach(() => {
    permissions = ['FULFILLMENT_ROUTE_ASSIGN'];
    features = ['MODULE_LOGISTICS', 'MODULE_INVENTORY'];
    productOver = {};
  });

  // ── A34 ────────────────────────────────────────────────────────────────────────────────────────────────

  it('lets the route field offer make-to-order routes only for a manufactured product in a manufacturing org (D-3, D-9)', () => {
    productOver = { supplyMethod: 'MANUFACTURE' };
    features = ['MODULE_LOGISTICS', 'MODULE_INVENTORY', 'MODULE_MANUFACTURING'];
    setup();
    expect(component.productManufactured).toBeTrue();
    expect(component.manufacturingEnabled).toBeTrue();
    features = ['MODULE_LOGISTICS', 'MODULE_INVENTORY'];
    expect(component.manufacturingEnabled).toBeFalse();
    productOver = { supplyMethod: 'PURCHASE' };
    setup();
    expect(component.productManufactured).toBeFalse();
  });

  it('Lead Times tab (D-26): editable with STOCK_MANAGE; "Recalculate from BOM" needs a calculate code', () => {
    permissions = ['INVENTORY_VIEW'];
    setup();
    expect(component.canEditLeadTimes).toBeFalse();
    expect(component.canCalculateLeadTimes).toBeTrue();
    permissions = ['STOCK_MANAGE'];
    expect(component.canEditLeadTimes).toBeTrue();
    expect(component.canCalculateLeadTimes).toBeTrue();
    permissions = ['LEAD_TIME_DEFAULTS_MANAGE'];
    expect(component.canEditLeadTimes).toBeFalse();
    expect(component.canCalculateLeadTimes).toBeFalse();
  });

  it('opens the edit dialog on the variant\'s route; a new variant starts with none', () => {
    setup();
    component.openEditVariantDialog(variant());
    expect(component.variantRouteUuid).toBe('r-ps');
    component.openAddVariantDialog();
    expect(component.variantRouteUuid).toBeNull();
  });

  it('shows the route only when the organization has Logistics (D-11), editable only with ASSIGN', () => {
    setup();
    expect(component.routesEnabled).toBeTrue();
    expect(component.canAssignRoute).toBeTrue();
    permissions = [];
    expect(component.canAssignRoute).toBeFalse();
    features = ['MODULE_INVENTORY'];
    expect(component.routesEnabled).toBeFalse();
  });

  it('saves a changed route through the gated call after the variant, never inside the variant PATCH', () => {
    setup();
    component.openEditVariantDialog(variant());
    component.variantRouteUuid = 'r-po';
    component.saveVariant();
    expect(inventory.updateVariant).toHaveBeenCalled();
    expect(JSON.stringify(inventory.updateVariant.calls.mostRecent().args[1])).not.toContain('fulfillmentRoute');
    expect(inventory.setVariantFulfillmentRoute).toHaveBeenCalledWith('v-1', 'r-po');
    expect(component.showVariantDialog).toBeFalse();
  });

  it('clears the route with null', () => {
    setup();
    component.openEditVariantDialog(variant());
    component.variantRouteUuid = null;
    component.saveVariant();
    expect(inventory.setVariantFulfillmentRoute).toHaveBeenCalledWith('v-1', null);
  });

  it('makes no route call when the route is unchanged', () => {
    setup();
    component.openEditVariantDialog(variant());
    component.saveVariant();
    expect(inventory.updateVariant).toHaveBeenCalled();
    expect(inventory.setVariantFulfillmentRoute).not.toHaveBeenCalled();
  });

  it('sets the route on a new variant once it exists', () => {
    setup();
    component.openAddVariantDialog();
    component.variantForm.patchValue({ variantName: 'Rod 12mm', purchasePrice: 11 });
    component.variantRouteUuid = 'r-po';
    component.saveVariant();
    expect(inventory.addVariant).toHaveBeenCalled();
    expect(inventory.setVariantFulfillmentRoute).toHaveBeenCalledWith('v-new', 'r-po');
  });

  it('makes no route call without FULFILLMENT_ROUTE_ASSIGN, or without Logistics', () => {
    permissions = [];
    setup();
    component.openEditVariantDialog(variant());
    component.variantRouteUuid = 'r-po';
    component.saveVariant();
    expect(inventory.setVariantFulfillmentRoute).not.toHaveBeenCalled();

    permissions = ['FULFILLMENT_ROUTE_ASSIGN'];
    features = ['MODULE_INVENTORY'];
    setup();
    component.openEditVariantDialog(variant());
    component.variantRouteUuid = 'r-po';
    component.saveVariant();
    expect(inventory.setVariantFulfillmentRoute).not.toHaveBeenCalled();
  });

  it('a refused route is a warning — the variant itself was saved', () => {
    setup();
    inventory.setVariantFulfillmentRoute.and.returnValue(throwError(() => ({ status: 400, error: { message: 'The route is inactive.' } })));
    component.openEditVariantDialog(variant());
    component.variantRouteUuid = 'r-po';
    component.saveVariant();
    expect(toasts.some(t => t.severity === 'warn' && String(t.detail).includes('The route is inactive.'))).toBeTrue();
    expect(toasts.some(t => t.severity === 'success')).toBeTrue();
    expect(component.showVariantDialog).toBeFalse();
  });
});

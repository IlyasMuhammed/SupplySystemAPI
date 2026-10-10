import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, Router } from '@angular/router';
import { of } from 'rxjs';

import { ProductDetailComponent } from './product-detail.component';
import { InventoryService, ProductDetailModel } from '../../../../services/inventory.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { PricingRuleService } from '../../../../services/pricing-rule.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { SupplierService } from '../../../../services/supplier.service';
import { LeadTimeService } from '../../../../services/lead-time.service';
import { FulfillmentRoutesService } from '../../../../services/fulfillment-routes.service';
import { BomService } from '../../../../services/bom.service';
import { QboSyncStatusStore } from '../../../../shared/components/qbo-sync-badge/qbo-sync-status.store';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';

/**
 * A37 §2 (PRD-CAP-02/03, BOM-SHR-03, D-12) — production / service sections only with the module's key AND the flag; the
 * flags stay visible (dormant) when the module is off; the BOM tab is read-only without FEATURE_BOM_MANAGEMENT and shows
 * only when a BOM exists; the Routes tab with Logistics and a route view code.
 */
describe('ProductDetailComponent — A37 module-dependent sections', () => {
  let fixture: ComponentFixture<ProductDetailComponent>;
  let component: ProductDetailComponent;
  let el: HTMLElement;
  let bomService: jasmine.SpyObj<BomService>;
  let inventory: jasmine.SpyObj<InventoryService>;

  const ok = (result: unknown) => of({ success: true, message: '', result } as any);
  const PRODUCTION = { supplyMethod: 'MANUFACTURE', defaultProductionWarehouseName: 'Plant 1', activeBomNumber: 'BOM-0007', manufacturingLeadTimeDays: 4 };
  const SERVICE = { hasServiceBom: false, isSubcontractable: false, requiresSiteVisit: true, serviceCategory: 'REPAIR' as const };

  async function setup(product: Partial<ProductDetailModel>, features: string[], boms = 0, perms: string[] | null = null) {
    inventory = jasmine.createSpyObj<InventoryService>('InventoryService',
      ['getProductById', 'getProductStock', 'getCategories', 'getWarehouses', 'patchProduct', 'getProducts']);
    inventory.getProducts.and.returnValue(ok({ data: [] }));
    inventory.getCategories.and.returnValue(ok([]));
    inventory.getWarehouses.and.returnValue(ok([]));
    inventory.patchProduct.and.returnValue(ok(null));
    inventory.getProductById.and.returnValue(ok({ id: 7, uuid: 'p-7', name: 'Pump', status: 'ACTIVE', variants: [], ...product }));
    inventory.getProductStock.and.returnValue(ok([]));
    bomService = jasmine.createSpyObj<BomService>('BomService', ['getBoms', 'getBom']);
    bomService.getBoms.and.returnValue(ok({ data: [], totalRecords: boms }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ProductDetailComponent],
      providers: [
        provideNoopAnimations(),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => '7' } } } },
        { provide: Router, useValue: { navigate: () => Promise.resolve(true) } },
        { provide: InventoryService, useValue: inventory },
        { provide: AttachmentService, useValue: { resolveUrl: (u: string) => u } },
        { provide: PricingRuleService, useValue: { getRules: () => ok({ data: [] }) } },
        { provide: CurrenciesService, useValue: {} },
        { provide: BusinessPartnerService, useValue: { getPartners: () => ok({ data: [] }) } },
        { provide: SupplierService, useValue: { getSuppliers: () => ok({ data: [] }) } },
        { provide: LeadTimeService, useValue: {} },
        { provide: BomService, useValue: bomService },
        { provide: FulfillmentRoutesService, useValue: { getRoutes: () => ok([]), getProductRoutes: () => ok([]) } },
        { provide: QboSyncStatusStore, useValue: { isAvailable: () => false } },
        { provide: AuthService, useValue: { hasPermission: (c: string) => perms === null || perms.includes(c) } },
        { provide: TenantService, useValue: { hasFeature: (c: string) => features.includes(c) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(ProductDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const tabHeaders = () => Array.from(el.querySelectorAll('[role="tab"], .p-tabview-nav li')).map(h => h.textContent ?? '');
  const ALL = ['MODULE_MANUFACTURING', 'MODULE_SERVICES', 'MODULE_LOGISTICS', 'FEATURE_BOM_MANAGEMENT'];

  it('shows the production section only with the productionSettings key and isManufacturable', async () => {
    await setup({ supplyMethod: 'MANUFACTURE', isManufacturable: true, productionSettings: PRODUCTION }, ALL);
    const view = q('production-settings-view')!;
    expect(view.textContent).toContain('Plant 1');
    expect(view.textContent).toContain('BOM-0007');
    expect(view.textContent).toContain('4 days');
    expect(q('flag-manufacturable-dormant')).toBeNull();

    // key present, flag off → no section
    await setup({ supplyMethod: 'PURCHASE', isManufacturable: false, productionSettings: PRODUCTION }, ALL);
    expect(q('production-settings-view')).toBeNull();
  });

  it('module off (key absent): no production section, the flag stays shown and is marked dormant', async () => {
    await setup({ supplyMethod: 'MANUFACTURE', isManufacturable: true }, ['FEATURE_BOM_MANAGEMENT']);
    expect(q('production-settings-view')).toBeNull();
    expect(q('flag-manufacturable')!.textContent).toContain('Manufactured here');
    expect(q('flag-manufacturable-dormant')!.textContent).toContain('Manufacturing off');
  });

  it('shows the service section with category and site visit only with serviceSettings and a serviceable product', async () => {
    await setup({ productType: 'SERVICE', supplyMethod: 'SERVICE', isServiceable: true, serviceCategory: 'REPAIR', requiresSiteVisit: true,
                  serviceSettings: SERVICE }, ALL);
    expect(q('service-category-view')!.textContent).toContain('Repair');
    expect(q('site-visit-tag')!.textContent).toContain('Requires site visit');
    expect(q('flag-serviceable')).not.toBeNull();

    await setup({ productType: 'SERVICE', supplyMethod: 'SERVICE', isServiceable: true }, ALL.filter(f => f !== 'MODULE_SERVICES'));
    expect(q('service-settings-view')).toBeNull();
    expect(q('flag-serviceable')).not.toBeNull();
    expect(q('flag-serviceable-dormant')!.textContent).toContain('Services off');
  });

  it('the edit dialog loads and sends service category and site visit', async () => {
    await setup({ productType: 'SERVICE', supplyMethod: 'SERVICE', isServiceable: true, serviceCategory: 'MAINTENANCE', requiresSiteVisit: true,
                  serviceSettings: SERVICE }, ALL);
    component.openEditDialog();
    expect(component.editForm.get('serviceCategory')!.value).toBe('MAINTENANCE');
    expect(component.editForm.get('requiresSiteVisit')!.value).toBeTrue();
    component.saveEdit();
    expect(inventory.patchProduct.calls.mostRecent().args[1]).toEqual(jasmine.objectContaining({
      serviceCategory: 'MAINTENANCE', requiresSiteVisit: true
    }));
  });

  it('BOM tab: editable with FEATURE_BOM_MANAGEMENT, never asks whether a BOM exists', async () => {
    await setup({ supplyMethod: 'MANUFACTURE', isManufacturable: true, productionSettings: PRODUCTION }, ALL);
    expect(tabHeaders().some(h => h.includes('Bill of Materials'))).toBeTrue();
    expect(component.bomManagementEnabled).toBeTrue();
    expect(bomService.getBoms).not.toHaveBeenCalledWith(jasmine.objectContaining({ pageSize: 1 }));
  });

  it('BOM tab without FEATURE_BOM_MANAGEMENT: read-only and only when the product has a BOM (BOM-SHR-03)', async () => {
    await setup({ supplyMethod: 'MANUFACTURE', isManufacturable: true }, ['MODULE_LOGISTICS'], 0);
    expect(bomService.getBoms).toHaveBeenCalledWith(jasmine.objectContaining({ productUuid: 'p-7', pageSize: 1 }));
    expect(tabHeaders().some(h => h.includes('Bill of Materials'))).toBeFalse();

    await setup({ supplyMethod: 'PURCHASE', isManufacturable: false }, ['MODULE_LOGISTICS'], 2);
    expect(component.bomExists).toBeTrue();
    expect(tabHeaders().some(h => h.includes('Bill of Materials'))).toBeTrue();
  });

  it('Routes tab with Logistics and a route view code only', async () => {
    await setup({}, ALL);
    expect(tabHeaders().some(h => h.includes('Routes'))).toBeTrue();
    await setup({}, ALL.filter(f => f !== 'MODULE_LOGISTICS'));
    expect(tabHeaders().some(h => h.includes('Routes'))).toBeFalse();
    await setup({}, ALL, 0, ['INVENTORY_VIEW']);
    expect(tabHeaders().some(h => h.includes('Routes'))).toBeFalse();
  });
});

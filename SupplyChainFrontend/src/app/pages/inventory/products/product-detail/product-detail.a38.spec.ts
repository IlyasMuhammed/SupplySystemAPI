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
import { PosAdminService } from '../../../../services/pos-admin.service';
import { QboSyncStatusStore } from '../../../../shared/components/qbo-sync-badge/qbo-sync-status.store';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';
import { ProductPosTabComponent } from '../../../pos/product-pos-tab/product-pos-tab.component';

const ok = (result: unknown) => of({ success: true, message: '', result } as any);

/** A38 §17.7.5 — the product's POS tab: only with MODULE_POS, a saleable product and POS_PRODUCT_SETTINGS. */
describe('ProductDetailComponent — A38 POS tab', () => {
  let fixture: ComponentFixture<ProductDetailComponent>;
  let component: ProductDetailComponent;
  let el: HTMLElement;

  async function setup(product: Partial<ProductDetailModel>, features: string[], perms: string[] = ['INVENTORY_VIEW', 'POS_PRODUCT_SETTINGS']) {
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService',
      ['getProductById', 'getProductStock', 'getCategories', 'getWarehouses', 'patchProduct', 'getProducts']);
    inventory.getProducts.and.returnValue(ok({ data: [] }));
    inventory.getCategories.and.returnValue(ok([]));
    inventory.getWarehouses.and.returnValue(ok([]));
    inventory.getProductById.and.returnValue(ok({ id: 7, uuid: 'p-7', name: 'Tea', status: 'ACTIVE', isSaleable: true, variants: [], ...product }));
    inventory.getProductStock.and.returnValue(ok([]));

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
        { provide: BomService, useValue: { getBoms: () => ok({ data: [], totalRecords: 0 }) } },
        { provide: FulfillmentRoutesService, useValue: { getRoutes: () => ok([]), getProductRoutes: () => ok([]) } },
        { provide: PosAdminService, useValue: { getProductSettings: () => ok(null) } },
        { provide: QboSyncStatusStore, useValue: { isAvailable: () => false } },
        { provide: AuthService, useValue: { hasPermission: (c: string) => perms.includes(c) } },
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

  const tabHeaders = () => Array.from(el.querySelectorAll('[role="tab"], .p-tabview-nav li')).map(h => (h.textContent ?? '').trim());
  const hasPosTab = () => tabHeaders().some(h => h === 'POS');

  it('shows the POS tab for a saleable product with MODULE_POS and POS_PRODUCT_SETTINGS', async () => {
    await setup({ isSaleable: true }, ['MODULE_POS']);
    expect(component.showPosTab).toBeTrue();
    expect(hasPosTab()).toBeTrue();
  });

  it('module off: no POS tab', async () => {
    await setup({ isSaleable: true }, []);
    expect(component.showPosTab).toBeFalse();
    expect(hasPosTab()).toBeFalse();
  });

  it('not saleable: no POS tab', async () => {
    await setup({ isSaleable: false }, ['MODULE_POS']);
    expect(component.showPosTab).toBeFalse();
    expect(hasPosTab()).toBeFalse();
  });

  it('without POS_PRODUCT_SETTINGS: no POS tab', async () => {
    await setup({ isSaleable: true }, ['MODULE_POS'], ['INVENTORY_VIEW']);
    expect(hasPosTab()).toBeFalse();
  });
});

/** A38 §17.7.5 — the POS tab itself (API-CONTRACT §6). */
describe('ProductPosTabComponent (A38)', () => {
  let fixture: ComponentFixture<ProductPosTabComponent>;
  let component: ProductPosTabComponent;
  let el: HTMLElement;
  let pos: jasmine.SpyObj<PosAdminService>;

  async function setup(settings: unknown = { posName: 'Tea', posCategoryId: 3, color: '#1E88E5', isWeighable: false, isFavorite: true,
                                             sortOrder: 2, isPosActive: true, modifiedAt: null }) {
    pos = jasmine.createSpyObj<PosAdminService>('PosAdminService', ['getProductSettings', 'saveProductSettings']);
    pos.getProductSettings.and.returnValue(ok(settings));
    pos.saveProductSettings.and.callFake((_u, req) => ok(req));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ProductPosTabComponent],
      providers: [
        provideNoopAnimations(),
        { provide: PosAdminService, useValue: pos },
        { provide: InventoryService, useValue: { getCategories: () => ok([
          { id: 3, name: 'Drinks', isActive: true }, { id: 4, name: 'Old', isActive: false }
        ]) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(ProductPosTabComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('variant', { sku: 'TEA-1', barcode: '8964000', sellingPrice: 250, isAvailableForPos: true });
    fixture.componentRef.setInput('productUuid', 'p-7');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  it('loads the settings and active categories; shows the variant barcode and price read-only with a hint', async () => {
    await setup();
    expect(pos.getProductSettings).toHaveBeenCalledWith('p-7');
    expect(component.settings.posName).toBe('Tea');
    expect(component.categoryOptions).toEqual([{ label: 'Drinks', value: 3 }]);
    expect(q('pos-barcode')!.textContent).toContain('8964000');
    expect(q('pos-price')!.textContent).toContain('250.00');
    expect(q('pos-variant-hint')!.textContent).toContain('Variants tab');
  });

  it('defaults when the product has no settings yet', async () => {
    await setup(null);
    expect(component.settings.isPosActive).toBeTrue();
    expect(component.settings.sortOrder).toBe(0);
  });

  it('validates the colour (#RRGGBB) and saves trimmed values', async () => {
    await setup();
    component.settings.color = '#12345';
    expect(component.colorInvalid).toBeTrue();
    component.save();
    expect(pos.saveProductSettings).not.toHaveBeenCalled();
    component.settings.color = '#abcdef';
    component.settings.posName = '  Green tea ';
    component.settings.imageUrl = '  ';
    component.save();
    expect(pos.saveProductSettings).toHaveBeenCalledWith('p-7', jasmine.objectContaining({
      posName: 'Green tea', color: '#ABCDEF', imageUrl: null, posCategoryId: 3, isFavorite: true, sortOrder: 2, isPosActive: true
    }));
  });
});

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

/** A36-P1-05 / P1-09 — service settings on the product page, the no-active-BOM note and the BOM tab for service BOMs. */
describe('ProductDetailComponent — A36 service products', () => {
  let fixture: ComponentFixture<ProductDetailComponent>;
  let component: ProductDetailComponent;
  let el: HTMLElement;
  let inventory: jasmine.SpyObj<InventoryService>;

  const ok = (result: unknown) => of({ success: true, message: '', result } as any);
  const service: Partial<ProductDetailModel> = {
    productType: 'SERVICE', supplyMethod: 'SERVICE', isManufacturable: false,
    serviceInvoicingPolicy: 'TIME_AND_MATERIAL', serviceBillingModel: 'PASS_THROUGH', estimatedDurationHours: 2.5,
    hasServiceBom: true, isSubcontractable: true, hasActiveServiceBom: false,
    // A37 PRD-CAP-02 — the service section needs MODULE_SERVICES on, i.e. the serviceSettings key present.
    isServiceable: true,
    serviceSettings: { hasServiceBom: true, isSubcontractable: true, requiresSiteVisit: false }
  };

  async function setup(product: Partial<ProductDetailModel>) {
    inventory = jasmine.createSpyObj<InventoryService>('InventoryService',
      ['getProductById', 'getProductStock', 'getProducts', 'getCategories', 'getWarehouses', 'patchProduct']);
    inventory.getProductById.and.returnValue(ok({ id: 7, uuid: 'p-7', name: 'Repair', status: 'ACTIVE', variants: [], ...product }));
    inventory.getProductStock.and.returnValue(ok([]));
    inventory.getProducts.and.returnValue(ok({ data: [] }));
    inventory.getCategories.and.returnValue(ok([]));
    inventory.getWarehouses.and.returnValue(ok([]));
    inventory.patchProduct.and.returnValue(ok(null));

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
        { provide: BomService, useValue: { getBoms: () => ok({ data: [] }) } },
        { provide: FulfillmentRoutesService, useValue: { getRoutes: () => ok([]) } },
        { provide: QboSyncStatusStore, useValue: { isAvailable: () => false } },
        { provide: AuthService, useValue: { hasPermission: () => true } },
        { provide: TenantService, useValue: { hasFeature: () => true } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(ProductDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const tabHeaders = () => Array.from(el.querySelectorAll('[role="tab"], .p-tabview-nav li')).map(h => h.textContent ?? '');

  it('shows the service settings and the BOM tab for a service with a service BOM, warning when none is active', async () => {
    await setup(service);
    const view = el.querySelector('[data-testid="service-settings-view"]')!;
    expect(view.textContent).toContain('Time & material');
    expect(view.textContent).toContain('Pass-through');
    expect(el.querySelector('[data-testid="no-active-service-bom"]')!.textContent)
      .toContain('No active service BOM — orders run with ad-hoc materials until one is activated.');
    expect(tabHeaders().some(h => h.includes('Bill of Materials'))).toBeTrue();
    expect(component.isServiceBomProduct).toBeTrue();
  });

  it('drops the warning once a service BOM is active', async () => {
    await setup({ ...service, hasActiveServiceBom: true });
    expect(el.querySelector('[data-testid="no-active-service-bom"]')).toBeNull();
  });

  it('has no BOM tab for a service without a service BOM, and no service settings for other types', async () => {
    await setup({ ...service, hasServiceBom: false });
    expect(tabHeaders().some(h => h.includes('Bill of Materials'))).toBeFalse();
    expect(el.querySelector('[data-testid="no-active-service-bom"]')).toBeNull();

    await setup({ productType: 'STOCK_ITEM', supplyMethod: 'PURCHASE', isManufacturable: false });
    expect(el.querySelector('[data-testid="service-settings-view"]')).toBeNull();
    expect(tabHeaders().some(h => h.includes('Bill of Materials'))).toBeFalse();
  });

  it('the edit dialog sends the service settings, and clears them when the type moves away from SERVICE', async () => {
    await setup(service);
    component.openEditDialog();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(document.querySelector('[data-testid="edit-service-settings"]')).not.toBeNull();

    component.saveEdit();
    expect(inventory.patchProduct.calls.mostRecent().args[1]).toEqual(jasmine.objectContaining({
      serviceInvoicingPolicy: 'TIME_AND_MATERIAL', serviceBillingModel: 'PASS_THROUGH', estimatedDurationHours: 2.5,
      hasServiceBom: true, isSubcontractable: true
    }));

    component.editForm.patchValue({ productType: 'STOCK_ITEM' });
    component.onProductTypeChange();
    expect(component.editForm.get('hasServiceBom')!.value).toBeFalse();
    component.saveEdit();
    expect(inventory.patchProduct.calls.mostRecent().args[1]).toEqual(jasmine.objectContaining({
      serviceInvoicingPolicy: null, serviceBillingModel: null, estimatedDurationHours: null,
      hasServiceBom: false, isSubcontractable: false
    }));
  });
});

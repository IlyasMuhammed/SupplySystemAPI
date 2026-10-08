import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, Router } from '@angular/router';
import { of } from 'rxjs';

import { ProductDetailComponent } from './product-detail.component';
import { InventoryService, ProductDetailModel, ProductVariantModel } from '../../../../services/inventory.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { PricingRuleService } from '../../../../services/pricing-rule.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { SupplierService } from '../../../../services/supplier.service';
import { LeadTimeService } from '../../../../services/lead-time.service';
import { FulfillmentRoutesService } from '../../../../services/fulfillment-routes.service';
import { QboSyncStatusStore } from '../../../../shared/components/qbo-sync-badge/qbo-sync-status.store';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';

/**
 * A34 — product detail, rendered: the "Make to order" tag on variants with a MANUFACTURE route (D-4; the BOM tab rule
 * stays isManufacturable) and the "Lead Times" tab (D-26), which loads nothing until it is opened.
 */
describe('ProductDetailComponent — A34 make-to-order tag and Lead Times tab', () => {
  let fixture: ComponentFixture<ProductDetailComponent>;
  let el: HTMLElement;
  let leadTimes: jasmine.SpyObj<LeadTimeService>;

  const ok = (result: unknown) => of({ success: true, message: '', result } as any);
  const variant = (uuid: string, over: Partial<ProductVariantModel> = {}): ProductVariantModel => ({
    id: 1, uuid, sku: 'SKU-' + uuid, variantName: 'Variant ' + uuid, purchasePrice: 10, isDefault: uuid === 'v-1', isActive: true,
    isAvailableForRetail: false, isAvailableForPos: false, isAvailableForMirMiv: false, isAvailableForProduction: false,
    isAvailableForServices: false, createdDate: '', ...over
  });

  async function setup(product: Partial<ProductDetailModel>) {
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProductById', 'getProductStock']);
    inventory.getProductById.and.returnValue(ok({ id: 7, uuid: 'p-7', name: 'Gear', status: 'ACTIVE', variants: [], ...product }));
    inventory.getProductStock.and.returnValue(ok([]));
    leadTimes = jasmine.createSpyObj<LeadTimeService>('LeadTimeService', ['getVariantLeadTimes', 'updateVariantLeadTimes', 'calculateManufacturing']);

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
        { provide: BusinessPartnerService, useValue: {} },
        { provide: SupplierService, useValue: {} },
        { provide: LeadTimeService, useValue: leadTimes },
        { provide: FulfillmentRoutesService, useValue: { getRoutes: () => ok([]) } },
        { provide: QboSyncStatusStore, useValue: { isAvailable: () => false } },
        { provide: AuthService, useValue: { hasPermission: () => true } },
        { provide: TenantService, useValue: { hasFeature: () => true } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(ProductDetailComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  it('tags variants with a MANUFACTURE route "Make to order", and only those', async () => {
    await setup({
      supplyMethod: 'MANUFACTURE', isManufacturable: false,
      variants: [variant('v-1', { isMakeToOrder: true, fulfillmentRouteCategory: 'MANUFACTURE' }),
                 variant('v-2', { isMakeToOrder: false, fulfillmentRouteCategory: 'STOCK' })]
    });
    expect(el.querySelector('[data-testid="make-to-order-v-1"]')!.textContent).toContain('Make to order');
    expect(el.querySelector('[data-testid="make-to-order-v-2"]')).toBeNull();
  });

  it('has a Lead Times tab that loads nothing until it is opened', async () => {
    await setup({ variants: [variant('v-1')] });
    const headers = Array.from(el.querySelectorAll('[role="tab"], .p-tabview-nav li')).map(h => h.textContent ?? '');
    expect(headers.some(h => h.includes('Lead Times'))).toBeTrue();
    expect(leadTimes.getVariantLeadTimes).not.toHaveBeenCalled();
  });
});

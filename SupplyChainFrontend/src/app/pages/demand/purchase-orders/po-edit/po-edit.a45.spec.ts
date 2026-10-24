import { signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { PoEditComponent } from './po-edit.component';
import { DemandService, PatchPoRequest, PoDetailModel } from '../../../../services/demand.service';
import { SupplierService } from '../../../../services/supplier.service';
import { InventoryService } from '../../../../services/inventory.service';
import { TenantService } from '../../../service/tenant.service';
import { PurchasePricingService } from '../../../../services/purchase-pricing.service';
import { TEST_PKR, TEST_USD, provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';

// A45 FE-B — PO edit hydrates price provenance, does not re-price on load, re-looks-up on qty change, and saves it back.

function ok<T>(result: T) { return of({ success: true, message: '', result } as any); }

const PO: PoDetailModel = {
  uuid: 'po-1', poNumber: 'PO-1', supplierId: 's-1', supplierName: 'Acme', status: 'DRAFT', totalAmount: 110,
  createdBy: 1, createdDate: '2026-10-07T00:00:00', linkedPrUuids: [], currencyId: 'cur-usd', currencyCode: 'USD',
  lines: [{
    uuid: 'l1', lineNo: 1, itemDescription: 'Widget', variantUuid: 'v-1', productUuid: 'p-1', quantity: 10, unitPrice: 11,
    lineTotal: 110, qtyReceived: 0, qtyInvoiced: 0, qtyPending: 10, qtyPendingInvoice: 10, requiresInspection: false,
    priceSource: 2, systemResolvedPrice: 12.5, supplierPricelistUuid: 'pl-1', priceOverrideReason: 'Spot discount',
    pricingDateBasis: 'OrderDate'
  }]
};

describe('PoEditComponent — A45 auto-pricing', () => {
  let fixture: ComponentFixture<PoEditComponent>;
  let component: PoEditComponent;
  let demand: jasmine.SpyObj<DemandService>;
  let pricing: jasmine.SpyObj<PurchasePricingService>;

  beforeEach(async () => {
    demand = jasmine.createSpyObj<DemandService>('DemandService', ['getPoById', 'patchPo']);
    demand.getPoById.and.returnValue(ok(PO));
    demand.patchPo.and.returnValue(ok(null));
    const suppliers = jasmine.createSpyObj<SupplierService>('SupplierService', ['getSuppliers', 'getSupplierById']);
    suppliers.getSuppliers.and.returnValue(ok({ data: [] }));
    suppliers.getSupplierById.and.returnValue(ok({ uuid: 's-1', supplierName: 'Acme' }));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getWarehouses']);
    inventory.getProducts.and.returnValue(ok({ data: [] }));
    inventory.getWarehouses.and.returnValue(ok([]));
    pricing = jasmine.createSpyObj<PurchasePricingService>('PurchasePricingService', ['resolvePrice']);
    pricing.resolvePrice.and.returnValue(of({ found: true, unitPrice: 9, supplierPricelistUuid: 'pl-1', dateBasisUsed: 'OrderDate' }));

    await TestBed.configureTestingModule({
      imports: [PoEditComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        provideTestOrgCurrencies([TEST_PKR, TEST_USD]),
        { provide: DemandService, useValue: demand },
        { provide: SupplierService, useValue: suppliers },
        { provide: InventoryService, useValue: inventory },
        { provide: PurchasePricingService, useValue: pricing },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }) } },
        { provide: ActivatedRoute, useValue: { params: of({ uuid: 'po-1' }), snapshot: { paramMap: new Map([['uuid', 'po-1']]) } } }
      ]
    }).compileComponents();
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture = TestBed.createComponent(PoEditComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('hydrates source/system price/reason, shows the badge, and does not re-price on load', fakeAsync(() => {
    tick(500);
    const l = component.lines.at(0);
    expect(l.get('priceSource')!.value).toBe(2);
    expect(l.get('systemResolvedPrice')!.value).toBe(12.5);
    expect(l.get('priceOverrideReason')!.value).toBe('Spot discount');
    expect(pricing.resolvePrice).not.toHaveBeenCalled();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="po-line-price-source"]').textContent.trim()).toBe('Override');
    expect(fixture.nativeElement.querySelector('[data-testid="po-line-override-reason"]')).toBeTruthy();
  }));

  it('saves the provenance back; a quantity change on an Override line only offers the system price', fakeAsync(() => {
    const toast = spyOn((component as any).messageService, 'addAll').and.callThrough() as jasmine.Spy;
    component.lines.at(0).get('quantity')!.setValue(50);
    tick(400);
    expect(pricing.resolvePrice).toHaveBeenCalledTimes(1);
    expect(component.lines.at(0).get('unitPrice')!.value).toBe(11);
    expect(toast.calls.all().some((c: any) => (c.args[0] as any[]).some(m => m.key === 'price-relookup'))).toBeTrue();

    component.onSubmit();
    const l = (demand.patchPo.calls.mostRecent().args[1] as PatchPoRequest).lines![0];
    expect(l.priceSource).toBe(2);
    expect(l.systemResolvedPrice).toBe(12.5);
    expect(l.supplierPricelistUuid).toBe('pl-1');
    expect(l.priceOverrideReason).toBe('Spot discount');
    tick(1500);
  }));
});

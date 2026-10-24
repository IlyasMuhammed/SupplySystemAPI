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

// A46 FE (C1, C2, C10) - PO edit: order date hydrate/disable/save, re-lookup banner, converted-price icon, RFQ sourcePO.

function ok<T>(result: T) { return of({ success: true, message: '', result } as any); }

function po(over: Partial<PoDetailModel> = {}, lineOver: object = {}): PoDetailModel {
  return {
    uuid: 'po-1', poNumber: 'PO-2026-0007', supplierId: 's-1', supplierName: 'Acme', status: 'DRAFT', totalAmount: 110,
    createdBy: 1, createdDate: '2026-10-07T00:00:00', orderDate: '2026-10-01', linkedPrUuids: [], currencyId: 'cur-usd', currencyCode: 'USD',
    lines: [{
      uuid: 'l1', lineNo: 1, itemDescription: 'Widget', variantUuid: 'v-1', productUuid: 'p-1', quantity: 10, unitPrice: 10.8,
      lineTotal: 108, qtyReceived: 0, qtyInvoiced: 0, qtyPending: 10, qtyPendingInvoice: 10, requiresInspection: false,
      priceSource: 1, systemResolvedPrice: 10.8, supplierPricelistUuid: 'pl-1', pricingDateBasis: 'OrderDate',
      originalCurrencyCode: 'EUR', originalCurrencyPrice: 10, exchangeRateUsed: 1.08, exchangeRateDate: '2026-10-01', ...lineOver
    }],
    ...over
  };
}

describe('PoEditComponent - A46 order date, conversion, RFQ link', () => {
  let fixture: ComponentFixture<PoEditComponent>;
  let component: PoEditComponent;
  let demand: jasmine.SpyObj<DemandService>;
  let pricing: jasmine.SpyObj<PurchasePricingService>;
  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  let current: PoDetailModel = po();

  beforeEach(async () => {
    demand = jasmine.createSpyObj<DemandService>('DemandService', ['getPoById', 'patchPo']);
    demand.getPoById.and.callFake(() => ok(current));
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
  });

  /** Opens the page for model (call inside fakeAsync). */
  function setup(model: PoDetailModel) {
    current = model;
    fixture = TestBed.createComponent(PoEditComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('hydrates the PO order date (no timezone shift) without re-pricing, and saves it as yyyy-MM-dd', fakeAsync(() => {
    setup(po());
    tick(500);
    const d = component.form.get('orderDate')!.value as Date;
    expect([d.getFullYear(), d.getMonth() + 1, d.getDate()]).toEqual([2026, 10, 1]);
    expect(component.pricing.orderDate.getDate()).toBe(1);
    expect(component.pricing.orderDateBanner).toBeNull();
    expect(pricing.resolvePrice).not.toHaveBeenCalled();
    expect(component.form.get('orderDate')!.enabled).toBeTrue();

    component.onSubmit();
    expect((demand.patchPo.calls.mostRecent().args[1] as PatchPoRequest).orderDate).toBe('2026-10-01');
    tick(1500);
  }));

  it('A46-0248: changing the order date offers Re-lookup All; the lookup uses the new PO order date', fakeAsync(() => {
    setup(po());
    tick(500);
    component.form.get('orderDate')!.setValue(new Date(2026, 9, 20));
    fixture.detectChanges();
    expect(q('order-date-banner')!.textContent).toContain('1 line(s) may have different prices');
    component.pricing.reLookupAll();
    tick(400);
    expect(pricing.resolvePrice.calls.mostRecent().args[0].orderDate).toBe('2026-10-20');
    expect(pricing.resolvePrice.calls.mostRecent().args[0].poCurrencyId).toBe('cur-usd');
    tick(1500);
  }));

  it('A46-0250: a non-DRAFT PO has the order date disabled and never sends it', fakeAsync(() => {
    setup(po({ status: 'APPROVED' }));
    tick(1600);
    expect(component.orderDateEditable).toBeFalse();
    expect(component.form.get('orderDate')!.disabled).toBeTrue();
  }));

  it('a hydrated converted price keeps its info icon and tooltip', fakeAsync(() => {
    setup(po());
    tick(500);
    fixture.detectChanges();
    expect(q('po-line-converted-info')).toBeTruthy();
    expect(component.conversionTooltip(component.lines.at(0).get('conversionInfo')!.value)).toBe('Original: EUR 10 @ 1.08 (2026-10-01)');
    tick(1500);
  }));

  it('a same-currency line has no info icon', fakeAsync(() => {
    setup(po({}, { originalCurrencyCode: null, originalCurrencyPrice: null, exchangeRateUsed: null, exchangeRateDate: null }));
    tick(500);
    fixture.detectChanges();
    expect(q('po-line-converted-info')).toBeNull();
    tick(1500);
  }));

  it('A46 C2: Create RFQ passes the PO number as sourcePO', fakeAsync(() => {
    setup(po());
    tick(500);
    component.pricing.createRfq(component.lines.at(0) as any);
    const [cmds, extras] = (TestBed.inject(Router).navigate as jasmine.Spy).calls.mostRecent().args as any[];
    expect(cmds).toEqual(['/portal/pages/demand/quotations/create']);
    expect(extras.queryParams).toEqual(jasmine.objectContaining({ supplierId: 's-1', variantUuid: 'v-1', productId: 'p-1', qty: 10, sourcePO: 'PO-2026-0007' }));
    tick(1500);
  }));

  it('A46-0152: a successful save shows one warn toast per pricing.warnings entry, and nothing for a response without pricing', fakeAsync(() => {
    setup(po());
    tick(500);
    const add = spyOn((component as any).messageService, 'add').and.callThrough();

    demand.patchPo.and.returnValue(ok(null));   // legacy envelope: no pricing
    component.onSubmit();
    expect(add.calls.allArgs().map(a => (a[0] as any).severity)).toEqual(['success']);
    tick(1500);

    add.calls.reset();
    demand.patchPo.and.returnValue(of({ success: true, message: '', result: null, pricing: {
      lines: [], pricedCount: 0, unpricedCount: 2, warnings: ['No price for Widget.', 'No price for Gadget.']
    } } as any));
    component.onSubmit();
    const toasts = add.calls.allArgs().map(a => a[0] as any);
    expect(toasts.map(t => t.severity)).toEqual(['success', 'warn', 'warn']);
    expect(toasts.filter(t => t.severity === 'warn').map(t => t.detail)).toEqual(['No price for Widget.', 'No price for Gadget.']);
    tick(1500);
  }));

  it('A46-0268: a 4-decimal price survives the save payload', fakeAsync(() => {
    setup(po());
    tick(500);
    component.lines.at(0).get('unitPrice')!.setValue(12.3456);
    component.onSubmit();
    expect((demand.patchPo.calls.mostRecent().args[1] as PatchPoRequest).lines![0].unitPrice).toBe(12.3456);
    tick(1500);
  }));
});

import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { PoEditComponent } from './po-edit.component';
import { DemandService, PatchPoRequest, PoDetailModel } from '../../../../services/demand.service';
import { SupplierService } from '../../../../services/supplier.service';
import { InventoryService } from '../../../../services/inventory.service';
import { TenantService } from '../../../service/tenant.service';
import { TEST_AED, TEST_PKR, TEST_USD, provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';

// A35 P3-14 — PO edit (DRAFT only): the PO's currency is shown and changeable (kept on offer even if deactivated since),
// follows a newly picked supplier's default, and is sent back as currencyId.

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

const PO: PoDetailModel = {
  uuid: 'po-1', poNumber: 'PO-2026-0100', supplierId: 's-1', supplierName: 'Old Mill', status: 'DRAFT', totalAmount: 100,
  createdBy: 1, createdDate: '2026-10-07T00:00:00', linkedPrUuids: [], currencyId: 'cur-aed', currencyCode: 'AED',
  lines: [{ uuid: 'l1', lineNo: 1, itemDescription: 'Steel coil', quantity: 2, unitPrice: 50, lineTotal: 100, qtyReceived: 0,
            qtyInvoiced: 0, qtyPending: 2, qtyPendingInvoice: 2, requiresInspection: false }]
};

describe('PoEditComponent — A35 currency', () => {
  let fixture: ComponentFixture<PoEditComponent>;
  let component: PoEditComponent;
  let demand: jasmine.SpyObj<DemandService>;

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  beforeEach(async () => {
    demand = jasmine.createSpyObj<DemandService>('DemandService', ['getPoById', 'patchPo']);
    demand.getPoById.and.returnValue(ok(PO));
    demand.patchPo.and.returnValue(ok(null));
    const suppliers = jasmine.createSpyObj<SupplierService>('SupplierService', ['getSuppliers', 'getSupplierById']);
    suppliers.getSuppliers.and.returnValue(ok({ data: [] }));
    suppliers.getSupplierById.and.returnValue(ok({ uuid: 's-2', supplierName: 'Gulf Metals', defaultPurchaseCurrencyId: 'cur-pkr' }));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getWarehouses']);
    inventory.getProducts.and.returnValue(ok({ data: [] }));
    inventory.getWarehouses.and.returnValue(ok([]));

    await TestBed.configureTestingModule({
      imports: [PoEditComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        // AED was deactivated after the draft was made.
        provideTestOrgCurrencies([TEST_PKR, { ...TEST_AED, isActive: false }, TEST_USD]),
        { provide: DemandService, useValue: demand },
        { provide: SupplierService, useValue: suppliers },
        { provide: InventoryService, useValue: inventory },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }) } },
        { provide: ActivatedRoute, useValue: { params: of({ uuid: 'po-1' }), snapshot: { paramMap: new Map([['uuid', 'po-1']]) } } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(PoEditComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('shows the draft\'s currency, keeps it on offer, and sends it back', () => {
    expect(component.form.get('currencyId')!.value).toBe('cur-aed');
    expect(component.currency.options.map(o => o.value)).toEqual(['cur-pkr', 'cur-aed', 'cur-usd']);
    fixture.detectChanges();
    expect(text(q('dc-currency'))).toBe('AED - UAE Dirham');
    expect(text(q('po-estimated-total'))).toBe('AED 100.00');

    component.onSubmit();
    expect((demand.patchPo.calls.mostRecent().args[1] as PatchPoRequest).currencyId).toBe('cur-aed');
  });

  it('a newly picked supplier brings its default purchase currency', () => {
    component.onSupplierChange({ uuid: 's-2', supplierName: 'Gulf Metals' });
    expect(component.form.get('currencyId')!.value).toBe('cur-pkr');
  });
});

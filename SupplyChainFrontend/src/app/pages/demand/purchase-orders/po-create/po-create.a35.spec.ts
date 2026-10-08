import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { PoCreateComponent } from './po-create.component';
import { DemandService, CreatePoRequest } from '../../../../services/demand.service';
import { SupplierService } from '../../../../services/supplier.service';
import { InventoryService } from '../../../../services/inventory.service';
import { RateCardService } from '../../../../services/rate-card.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { AttachmentPolicyService } from '../../../../services/attachment-policy.service';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';
import { TEST_AED, TEST_PKR, TEST_USD, provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';

// A35 P3-14 — PO create: a currency picker (active org currencies) starting in the purchase base and following the
// supplier's default purchase currency (D-9: defaultPurchaseCurrencyId, alias preferredCurrency); sent as currencyId.

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('PoCreateComponent — A35 currency', () => {
  let fixture: ComponentFixture<PoCreateComponent>;
  let component: PoCreateComponent;
  let demand: jasmine.SpyObj<DemandService>;
  let suppliers: jasmine.SpyObj<SupplierService>;

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  beforeEach(async () => {
    demand = jasmine.createSpyObj<DemandService>('DemandService',
      ['createPo', 'getPrs', 'getPrById', 'getQuotations', 'getQuotationById', 'getQuotationComparison']);
    demand.createPo.and.returnValue(ok('po-new'));
    demand.getPrs.and.returnValue(ok({ data: [], totalRecords: 0 }));
    demand.getQuotations.and.returnValue(ok({ data: [], totalRecords: 0 }));
    suppliers = jasmine.createSpyObj<SupplierService>('SupplierService', ['getSuppliers', 'getSupplierById', 'getScoreSummary']);
    suppliers.getSuppliers.and.returnValue(ok({ data: [
      { id: 1, uuid: 's-ae', supplierName: 'Gulf Metals', isActive: true, supplierTypeIds: [], industryIds: [] },
      { id: 2, uuid: 's-old', supplierName: 'Old Mill', isActive: true, supplierTypeIds: [], industryIds: [] }
    ], totalRecords: 2 }));
    suppliers.getSupplierById.and.callFake((uuid: string) => ok(uuid === 's-ae'
      ? { uuid, supplierName: 'Gulf Metals', defaultPurchaseCurrencyId: 'cur-aed' }
      : { uuid, supplierName: 'Old Mill', preferredCurrency: 'cur-pkr' }));
    suppliers.getScoreSummary.and.returnValue(of({ success: false, message: '', result: null } as any));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getWarehouses']);
    inventory.getProducts.and.returnValue(ok({ data: [] }));
    inventory.getWarehouses.and.returnValue(ok([]));
    const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'resolveUrl', 'isApiUrl']);
    attachments.getAttachments.and.returnValue(ok([]));
    const policy = jasmine.createSpyObj<AttachmentPolicyService>('AttachmentPolicyService', ['ruleFor']);
    policy.ruleFor.and.returnValue(of(null));

    await TestBed.configureTestingModule({
      imports: [PoCreateComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        provideTestOrgCurrencies([TEST_PKR, TEST_AED, TEST_USD]),
        { provide: DemandService, useValue: demand },
        { provide: SupplierService, useValue: suppliers },
        { provide: InventoryService, useValue: inventory },
        { provide: RateCardService, useValue: jasmine.createSpyObj('RateCardService', ['getActiveRate']) },
        { provide: AttachmentService, useValue: attachments },
        { provide: AttachmentPolicyService, useValue: policy },
        { provide: AuthService, useValue: { hasRole: () => false, hasPermission: () => true, getUserData: () => null } },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }) } },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParams: {}, paramMap: new Map() }, queryParams: of({}) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(PoCreateComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('starts in the purchase base and offers the active org currencies', () => {
    expect(component.currency.options.map(o => o.value)).toEqual(['cur-pkr', 'cur-aed', 'cur-usd']);
    expect(component.form.get('currencyId')!.value).toBe('cur-usd');
    fixture.detectChanges();
    expect(text(q('dc-rate'))).toBe('Locked at approval');
    expect(text(q('dc-base'))).toBe('USD');
  });

  it('takes the supplier\'s default purchase currency (or its preferredCurrency alias) and sends it', () => {
    component.form.patchValue({ supplierId: 's-ae' });
    component.onSupplierChange({ value: 's-ae' });
    expect(component.form.get('currencyId')!.value).toBe('cur-aed');

    component.form.patchValue({ supplierId: 's-old' });
    component.onSupplierChange({ value: 's-old' });
    expect(component.form.get('currencyId')!.value).toBe('cur-pkr');

    component.lines.at(0).patchValue({ itemDescription: 'Steel coil', quantity: 2, unitPrice: 50 });
    fixture.detectChanges();
    expect(text(q('po-estimated-total'))).toBe('PKR 100.00');
    component.onSubmit();
    const req = demand.createPo.calls.mostRecent().args[0] as CreatePoRequest;
    expect(req.currencyId).toBe('cur-pkr');
  });
});

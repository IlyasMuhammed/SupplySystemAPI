import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Params } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { QuotationCreateComponent } from './quotation-create.component';
import { parseRfqPrefill } from './quotation-prefill';
import { DemandService } from '../../../../services/demand.service';
import { InventoryService } from '../../../../services/inventory.service';
import { SupplierService } from '../../../../services/supplier.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { AttachmentPolicyService } from '../../../../services/attachment-policy.service';
import { TenantService } from '../../../service/tenant.service';
import { signal } from '@angular/core';

// A46 C2 - RFQ create consumes the query string a PO line's "Create RFQ" sends (and the spec's aliases).

describe('parseRfqPrefill (A46 C2)', () => {
  it('reads the A45 names', () => {
    expect(parseRfqPrefill({ supplierId: 's-1', productId: 'p-1', variantUuid: 'v-1', qty: '4', productName: 'Widget' }))
      .toEqual({ supplierId: 's-1', productId: 'p-1', variantUuid: 'v-1', quantity: 4, productName: 'Widget', sourcePo: null });
  });
  it('reads the spec aliases; vendorId / quantity win over the A45 names', () => {
    const r = parseRfqPrefill({ vendorId: 'v-9', supplierId: 's-1', quantity: '7', qty: '4', sourcePO: 'PO-1' });
    expect(r.supplierId).toBe('v-9');
    expect(r.quantity).toBe(7);
    expect(r.sourcePo).toBe('PO-1');
  });
  it('quantity: absent stays null; zero, negative, NaN and text become 1', () => {
    expect(parseRfqPrefill({}).quantity).toBeNull();
    for (const bad of ['0', '-3', 'abc', 'NaN', '1e9', 'Infinity']) expect(parseRfqPrefill({ quantity: bad }).quantity).withContext(bad).toBe(1);
    expect(parseRfqPrefill({ quantity: '2.5' }).quantity).toBe(2.5);
  });
  it('blank, repeated or non-text values are treated as absent / first', () => {
    const r = parseRfqPrefill({ vendorId: '  ', productId: ['p-1', 'p-2'], sourcePO: 5 as any });
    expect(r.supplierId).toBeNull();
    expect(r.productId).toBe('p-1');
    expect(r.sourcePo).toBeNull();
  });
  it('null / undefined params give nothing', () => {
    expect(parseRfqPrefill(null)).toEqual({ supplierId: null, productId: null, variantUuid: null, quantity: null, productName: null, sourcePo: null });
    expect(parseRfqPrefill(undefined).productId).toBeNull();
  });
});

describe('QuotationCreateComponent - A46 prefill', () => {
  let fixture: ComponentFixture<QuotationCreateComponent>;
  let component: QuotationCreateComponent;
  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  async function open(params: Params) {
    const demand = jasmine.createSpyObj<DemandService>('DemandService', ['getPrs', 'getPos', 'getPrById', 'createQuotation']);
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getProductById']);
    inventory.getProducts.and.returnValue(of({ success: true, message: '', result: { data: [
      { uuid: 'p-1', sku: 'W-1', name: 'Widget', uomCode: 'EA', variantCount: 1 }, { uuid: 'p-2', sku: 'B-1', name: 'Bolt', uomCode: 'ZZ', variantCount: 1 }
    ] } } as any));
    inventory.getProductById.and.returnValue(of({ success: true, message: '', result: { variants: [] } } as any));
    const suppliers = jasmine.createSpyObj<SupplierService>('SupplierService', ['getSuppliers']);
    suppliers.getSuppliers.and.returnValue(of({ success: true, message: '', result: { data: [{ uuid: 's-1', supplierName: 'Acme' }] } } as any));
    const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'resolveUrl', 'isApiUrl']);
    attachments.getAttachments.and.returnValue(of({ success: true, result: [] } as any));
    const policy = jasmine.createSpyObj<AttachmentPolicyService>('AttachmentPolicyService', ['ruleFor']);
    policy.ruleFor.and.returnValue(of(null));
    await TestBed.configureTestingModule({
      imports: [QuotationCreateComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        { provide: DemandService, useValue: demand },
        { provide: InventoryService, useValue: inventory },
        { provide: SupplierService, useValue: suppliers },
        { provide: AttachmentService, useValue: attachments },
        { provide: AttachmentPolicyService, useValue: policy },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }) } },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParams: params, paramMap: new Map() } } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(QuotationCreateComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }
  const line0 = () => component.lines.at(0);

  it('A46-0259: all params -> vendor, product and quantity pre-filled; "Referenced from" shown; still editable', async () => {
    await open({ vendorId: 's-1', productId: 'p-1', quantity: '12', sourcePO: 'PO-2026-0007' });
    expect(component.form.get('supplierId')!.value).toBe('s-1');
    expect(line0().get('productUuid')!.value).toBe('p-1');
    expect(line0().get('itemDescription')!.value).toBe('Widget');
    expect(line0().get('unitOfMeasure')!.value).toBe('EA');
    expect(line0().get('quantity')!.value).toBe(12);
    expect(q('rfq-source-po')!.textContent).toContain('Referenced from PO-2026-0007');
    expect(q('rfq-supplier')).toBeTruthy();
    line0().get('quantity')!.setValue(3);
    component.form.get('supplierId')!.setValue(null);
    expect(line0().get('quantity')!.value).toBe(3);
    expect(component.form.get('supplierId')!.value).toBeNull();
  });

  it('the A45 link (supplierId / productId / variantUuid / qty / productName) pre-fills too', async () => {
    await open({ supplierId: 's-1', productId: 'p-1', variantUuid: 'v-1', qty: '2', productName: 'Widget' });
    expect(component.form.get('supplierId')!.value).toBe('s-1');
    expect(line0().get('variantUuid')!.value).toBe('v-1');
    expect(line0().get('quantity')!.value).toBe(2);
    expect(component.form.get('title')!.value).toBe('RFQ - Widget');
  });

  it('A46-0260: an unknown vendor or product is ignored silently (no error toast, nothing filled)', async () => {
    await open({ vendorId: 'nope', productId: 'nope' });
    const toasts = spyOn((component as any).messageService, 'add');
    expect(component.form.get('supplierId')!.value).toBeNull();
    expect(line0().get('productUuid')!.value).toBeNull();
    expect(line0().get('itemDescription')!.value).toBe('');
    expect(toasts).not.toHaveBeenCalled();
    expect(q('rfq-supplier')).toBeNull();
  });

  it('A46-0261: only a vendor -> vendor set, product empty, quantity stays the default 1', async () => {
    await open({ vendorId: 's-1' });
    expect(component.form.get('supplierId')!.value).toBe('s-1');
    expect(line0().get('productUuid')!.value).toBeNull();
    expect(line0().get('quantity')!.value).toBe(1);
  });

  it('an invalid quantity falls back to 1 while the valid product is still filled', async () => {
    await open({ productId: 'p-2', quantity: '-4' });
    expect(line0().get('quantity')!.value).toBe(1);
    expect(line0().get('itemDescription')!.value).toBe('Bolt');
    expect(line0().get('unitOfMeasure')!.value).toBeNull();   // ZZ is not a UOM this form offers
  });

  it('A46-0262: no params -> the normal empty form, no supplier field, no reference text', async () => {
    await open({});
    expect(component.form.get('supplierId')!.value).toBeNull();
    expect(line0().get('productUuid')!.value).toBeNull();
    expect(line0().get('quantity')!.value).toBe(1);
    expect(q('rfq-supplier')).toBeNull();
    expect(q('rfq-source-po')).toBeNull();
  });
});

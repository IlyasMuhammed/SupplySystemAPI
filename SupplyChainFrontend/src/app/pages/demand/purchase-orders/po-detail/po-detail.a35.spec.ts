import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { PoDetailComponent } from './po-detail.component';
import { DemandService, PoDetailModel, PoLineModel } from '../../../../services/demand.service';
import { WorkflowService } from '../../../../services/workflow.service';
import { SupplierService } from '../../../../services/supplier.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { AttachmentPolicyService } from '../../../../services/attachment-policy.service';
import { TimelineService } from '../../../../services/timeline.service';
import { TenantService } from '../../../service/tenant.service';
import { provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';

// A35 P3-14 — purchase order detail in another currency: currency, the rate locked at approval, the purchase base,
// line totals and the order total in both currencies (POs have no tax/subtotal), the toggle, and the D-5 refusal on approve.

const MISSING = 'No exchange rate for AED on 2026-10-07. Add one under Settings → Exchange Rates.';

function line(overrides: Partial<PoLineModel> = {}): PoLineModel {
  return {
    uuid: 'l1', lineNo: 1, itemDescription: 'Steel coil', quantity: 10, unitPrice: 100, lineTotal: 1000, qtyReceived: 0,
    qtyInvoiced: 0, qtyPending: 10, qtyPendingInvoice: 10, requiresInspection: false, ...overrides
  };
}

function po(overrides: Partial<PoDetailModel> = {}): PoDetailModel {
  return {
    uuid: 'po-1', poNumber: 'PO-2026-0100', supplierId: 's-1', supplierName: 'Gulf Metals', status: 'APPROVED',
    totalAmount: 1000, createdBy: 1, createdDate: '2026-10-07T00:00:00', lines: [line()], linkedPrUuids: [],
    currencyId: 'cur-aed', currencyCode: 'AED', exchangeRate: 0.2723, baseCurrencyId: 'cur-usd', baseCurrencyCode: 'USD',
    rateLockedAt: '2026-10-07T10:00:00Z', totalAmountBase: 272.3,
    ...overrides
  };
}

describe('PoDetailComponent — A35 currency and dual amounts', () => {
  let fixture: ComponentFixture<PoDetailComponent>;
  let component: PoDetailComponent;
  let demand: jasmine.SpyObj<DemandService>;
  let toasts: jasmine.Spy;

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const qa = (id: string): HTMLElement[] => Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${id}"]`));
  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  async function setup(model: PoDetailModel) {
    demand = jasmine.createSpyObj<DemandService>('DemandService', ['getPoById', 'approvePo']);
    demand.getPoById.and.returnValue(of({ success: true, message: '', result: model } as any));
    const timeline = jasmine.createSpyObj<TimelineService>('TimelineService', ['getByTraceId', 'getByDocument']);
    timeline.getByDocument.and.returnValue(of({ success: true, result: null } as any));
    const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'resolveUrl', 'isApiUrl']);
    attachments.getAttachments.and.returnValue(of({ success: true, result: [] } as any));
    attachments.resolveUrl.and.callFake((u: string) => u);
    const policy = jasmine.createSpyObj<AttachmentPolicyService>('AttachmentPolicyService', ['ruleFor']);
    policy.ruleFor.and.returnValue(of(null));

    await TestBed.configureTestingModule({
      imports: [PoDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        provideTestOrgCurrencies(),
        { provide: DemandService, useValue: demand },
        { provide: WorkflowService, useValue: jasmine.createSpyObj('WorkflowService', ['getHistory', 'getApprovalDetail']) },
        { provide: SupplierService, useValue: jasmine.createSpyObj('SupplierService', ['getEligibleContacts']) },
        { provide: AttachmentService, useValue: attachments },
        { provide: AttachmentPolicyService, useValue: policy },
        { provide: TimelineService, useValue: timeline },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }) } },
        { provide: ActivatedRoute, useValue: { params: of({ uuid: 'po-1' }), snapshot: { paramMap: new Map([['uuid', 'po-1']]) } } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(PoDetailComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  it('shows the currency, the rate locked at approval and the purchase base', async () => {
    await setup(po());
    expect(text(q('dc-currency'))).toBe('AED - UAE Dirham');
    expect(text(q('dc-rate'))).toBe('0.2723 (locked 7 Oct 2026)');
    expect(text(q('dc-base'))).toBe('USD');
  });

  it('puts line totals and the order total in both currencies, and the toggle puts the base first', async () => {
    await setup(po({ lines: [line({ unitPriceBase: 27.23, lineTotalBase: 272.3 })] }));
    expect(qa('po-line-total-primary').map(text)).toEqual(['AED 1,000.00']);
    expect(qa('po-line-total-secondary').map(text)).toEqual(['USD 272.30']);
    expect(text(q('po-total'))).toBe('AED 1,000.00 USD 272.30');

    (Array.from(q('dc-toggle')!.querySelectorAll('button'))[1] as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(qa('po-line-unit-price').map(text)).toEqual(['USD 27.23']);
    expect(qa('po-line-total-primary').map(text)).toEqual(['USD 272.30']);
    expect(text(q('po-total-header-primary'))).toBe('Total (USD)');
    expect(text(q('po-total'))).toBe('USD 272.30 AED 1,000.00');
  });

  it('a draft PO says the rate locks at approval and shows one set of amounts', async () => {
    await setup(po({ status: 'DRAFT', exchangeRate: null, baseCurrencyId: null, baseCurrencyCode: null, rateLockedAt: null, totalAmountBase: null }));
    expect(text(q('dc-rate'))).toBe('Locked at approval');
    expect(text(q('dc-base'))).toBe('USD');
    expect(q('dc-toggle')).toBeNull();
    expect(qa('po-line-total-secondary').length).toBe(0);
    expect(text(q('po-total'))).toBe('AED 1,000.00');
  });

  it('approving without a rate says so plainly (D-5)', async () => {
    await setup(po({ status: 'DRAFT', exchangeRate: null, baseCurrencyId: null, baseCurrencyCode: null, rateLockedAt: null }));
    demand.approvePo.and.returnValue(throwError(() => ({ status: 400, error: { success: false, message: MISSING } })));
    (component as any).approvePo();
    const toast = toasts.calls.mostRecent().args[0];
    expect(toast.summary).toBe('No exchange rate');
    expect(toast.detail).toBe(MISSING);
  });
});
